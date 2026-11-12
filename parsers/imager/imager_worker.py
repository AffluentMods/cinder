"""Cinder disk-imager sidecar.

Wraps:
- libewf-python (E01 / EX01) — `pip install libewf-python` (binary wheels for win/linux x64)
- a plain Python writer for raw .dd

Methods (JSON-RPC):
  image(job)     -> {output_path, bytes_written, md5, sha1, sha256, blake3, bad_sectors,
                     bad_ranges, sector_size, source_size, complete, digest_files}
  progress()     -> {bytes_read, total_bytes, throughput, bad_sectors, phase}
  verify(args)   -> {match, reason, expected_*, actual_*, bytes_verified}
  convert(args)  -> {output_path, bytes_written, sha256}

Acquisition rules this module holds itself to:

* The image is the same length as the source and every byte sits at its source offset.
  The read position is tracked explicitly and every read seeks first; a failed read is
  never followed by a blind "next" read from wherever the OS left the file pointer.
* A chunk that fails to read is re-read sector by sector. Only the sectors that still
  fail after the configured number of retries are zero-filled, and each one is counted
  and listed in ``bad_ranges``. The reported hashes are hashes of the image as written
  (zero fill included); ``bad_sectors`` says whether that equals the source.
* A format that cannot be written is an error. Nothing ever writes raw data into a file
  named .E01 / .aff4 / .vhd.
* ``verify`` answers ``match: null`` when it has no reference hash to compare with. It
  never reports a match it did not check. ``image`` writes the reference: a
  ``<output>.sha256`` and ``<output>.md5`` companion in ``sha256sum`` format.

The job dictionary is accepted with PascalCase, camelCase or snake_case keys (the C#
caller serialises ``ImageJob`` with camelCase).
"""

from __future__ import annotations

import asyncio
import hashlib
import os
import re
import stat
import sys
import time
from collections.abc import Iterator
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from shared.protocol import Sidecar, report_progress

CHUNK_SIZE = 1 << 20
DEFAULT_SECTOR_SIZE = 512
# When the source size is unknown there is no end to stop at, so a source that fails
# forever would be zero-filled forever. Give up after this many unreadable bytes in a row.
MAX_UNSIZED_BAD_RUN = 16 << 20
MAX_REPORTED_BAD_RANGES = 10_000

EWF_SUFFIXES = (".e01", ".ex01", ".s01")
_FORMAT_NAMES = {0: "raw", 1: "ewf", 2: "aff4", 3: "vhd", 4: "vhdx"}
_FORMAT_ALIASES = {"dd": "raw", "img": "raw", "e01": "ewf", "ex01": "ewf", "encase": "ewf"}


@dataclass
class _ProgressState:
    bytes_read: int = 0
    total_bytes: int | None = None
    started_at: float = field(default_factory=time.monotonic)
    bad_sectors: int = 0
    phase: str = "idle"

    @property
    def throughput(self) -> float:
        elapsed = max(time.monotonic() - self.started_at, 1e-3)
        return self.bytes_read / elapsed


_progress = _ProgressState()
sidecar = Sidecar()


@sidecar.method("progress")
def progress(_params: Any) -> dict[str, Any]:
    return {
        "bytes_read": _progress.bytes_read,
        "total_bytes": _progress.total_bytes,
        "throughput": _progress.throughput,
        "bad_sectors": _progress.bad_sectors,
        "phase": _progress.phase,
    }


# -------------------- job parameters --------------------

def _canon(key: str) -> str:
    return re.sub(r"[^a-z0-9]", "", str(key).lower())


class _Job:
    """Case-insensitive view of the job: SourceDevice == sourceDevice == source_device."""

    def __init__(self, raw: Any) -> None:
        if raw is None:
            raw = {}
        if not isinstance(raw, dict):
            raise ValueError("image job must be a JSON object")
        self._values = {_canon(k): v for k, v in raw.items()}

    def get(self, name: str, default: Any = None) -> Any:
        value = self._values.get(_canon(name))
        return default if value is None else value

    def require(self, name: str) -> Any:
        value = self._values.get(_canon(name))
        if value is None or value == "":
            raise ValueError(f"image job is missing required field '{name}'")
        return value


def _format_of(job: _Job) -> str:
    raw = job.get("Format", "Raw")
    if isinstance(raw, bool):
        raise ValueError(f"unknown image format: {raw!r}")
    if isinstance(raw, int):
        name = _FORMAT_NAMES.get(raw)
    else:
        name = str(raw).strip().lower()
        name = _FORMAT_ALIASES.get(name, name)
        if name not in _FORMAT_NAMES.values():
            name = None
    if name is None:
        raise ValueError(f"unknown image format: {raw!r}")
    return name


# -------------------- source --------------------

def _windows_device_length(fd: int) -> int | None:
    """Size of a raw Windows device (\\\\.\\PhysicalDriveN), which fstat reports as 0."""
    try:
        import ctypes
        import msvcrt

        handle = msvcrt.get_osfhandle(fd)
        length = ctypes.c_longlong(0)
        returned = ctypes.c_ulong(0)
        ioctl_disk_get_length_info = 0x0007405C
        ok = ctypes.windll.kernel32.DeviceIoControl(  # type: ignore[attr-defined]
            ctypes.c_void_p(handle), ioctl_disk_get_length_info, None, 0,
            ctypes.byref(length), ctypes.sizeof(length), ctypes.byref(returned), None,
        )
        return int(length.value) if ok and length.value > 0 else None
    except Exception:
        return None


def _open_source(path: str) -> tuple[int, int | None]:
    """Open the source read-only and return (fd, size). Size is None when it cannot be
    determined; it is never guessed. On Linux, /dev/* devices; on Windows, \\\\.\\PhysicalDriveN."""
    fd = os.open(path, os.O_RDONLY | (getattr(os, "O_BINARY", 0)))
    size: int | None = None
    try:
        st = os.fstat(fd)
        if stat.S_ISREG(st.st_mode):
            size = st.st_size
        else:
            try:
                end = os.lseek(fd, 0, os.SEEK_END)
                size = end if end > 0 else None
            except OSError:
                size = None
            finally:
                try:
                    os.lseek(fd, 0, os.SEEK_SET)
                except OSError:
                    pass
            if size is None and sys.platform == "win32":
                size = _windows_device_length(fd)
    except Exception:
        size = None
    return fd, size


def _hash_factory() -> dict[str, Any]:
    return {
        "md5": hashlib.md5(),
        "sha1": hashlib.sha1(),
        "sha256": hashlib.sha256(),
    }


class _SourceReader:
    """Reads a source front to back and yields exactly the bytes for each offset.

    ``pos`` is the only notion of position: every read seeks to it first, so an error
    (after which the OS file pointer is undefined) cannot shift later data.
    """

    def __init__(self, fd: int, total_bytes: int | None, *, retry: bool, retries: int,
                 sector_size: int = DEFAULT_SECTOR_SIZE, chunk_size: int = CHUNK_SIZE) -> None:
        if sector_size <= 0 or chunk_size <= 0:
            raise ValueError("sector_size and chunk_size must be positive")
        self.fd = fd
        self.total_bytes = total_bytes
        self.retry = retry
        self.retries = max(0, int(retries))
        self.sector_size = sector_size
        self.chunk_size = max(chunk_size - chunk_size % sector_size, sector_size)
        self.pos = 0
        self.bad_sectors = 0
        self.bad_ranges: list[list[int]] = []  # [offset, length], adjacent ranges merged
        self.bad_ranges_truncated = False
        self.ended_early = False
        self._bad_run = 0

    def _read_at(self, offset: int, length: int) -> bytes:
        os.lseek(self.fd, offset, os.SEEK_SET)
        return os.read(self.fd, length)

    def _want(self, limit: int) -> int:
        if self.total_bytes is None:
            return limit
        return min(limit, self.total_bytes - self.pos)

    def _note_bad(self, offset: int, length: int) -> None:
        self.bad_sectors += 1
        if self.bad_ranges and self.bad_ranges[-1][0] + self.bad_ranges[-1][1] == offset:
            self.bad_ranges[-1][1] += length
        elif len(self.bad_ranges) < MAX_REPORTED_BAD_RANGES:
            self.bad_ranges.append([offset, length])
        else:
            self.bad_ranges_truncated = True

    def _read_sector(self, offset: int, length: int) -> bytes | None:
        """One sector, with retries. None means it stayed unreadable."""
        for _ in range(1 + self.retries):
            try:
                return self._read_at(offset, length)
            except OSError:
                continue
        return None

    def _recover(self, offset: int, length: int) -> bytes:
        """Re-read a failed chunk sector by sector. Returns the bytes for
        [offset, offset+length), zero-filled where a sector stayed unreadable; shorter
        only if the source genuinely ends inside the range."""
        out = bytearray()
        cursor = offset
        end = offset + length
        while cursor < end:
            size = min(self.sector_size, end - cursor)
            data = self._read_sector(cursor, size)
            if data is None:
                self._note_bad(cursor, size)
                self._bad_run += size
                if self.total_bytes is None and self._bad_run > MAX_UNSIZED_BAD_RUN:
                    raise OSError(
                        f"source became unreadable at offset {cursor - self._bad_run + size} and its size is "
                        f"unknown; stopped after {self._bad_run} consecutive unreadable bytes rather than "
                        "zero-filling without end"
                    )
                data = b"\x00" * size
            elif not data:
                break  # end of source
            else:
                self._bad_run = 0
            out += data
            cursor += len(data)  # a short read just carries on from where it stopped
        return bytes(out)

    def __iter__(self) -> Iterator[bytes]:
        while True:
            want = self._want(self.chunk_size)
            if want <= 0:
                return
            try:
                buf = self._read_at(self.pos, want)
                if buf:
                    self._bad_run = 0
            except OSError:
                if not self.retry:
                    raise
                buf = self._recover(self.pos, want)
            if not buf:
                if self.total_bytes is not None and self.pos < self.total_bytes:
                    self.ended_early = True
                return
            self.pos += len(buf)
            yield buf


def _acquire(reader: _SourceReader, write: Any) -> tuple[int, dict[str, Any]]:
    hashers = _hash_factory()
    written = 0
    _progress.phase = "imaging"
    _progress.total_bytes = reader.total_bytes
    for buf in reader:
        write(buf)
        for h in hashers.values():
            h.update(buf)
        written += len(buf)
        _progress.bytes_read = written
        _progress.bad_sectors = reader.bad_sectors
        report_progress(bytes_read=written, total_bytes=reader.total_bytes, bad_sectors=reader.bad_sectors)
    return written, hashers


def _result(output_path: str, written: int, hashers: dict[str, Any], reader: _SourceReader) -> dict[str, Any]:
    return {
        "output_path": output_path,
        "bytes_written": written,
        "md5": hashers["md5"].hexdigest(),
        "sha1": hashers["sha1"].hexdigest(),
        "sha256": hashers["sha256"].hexdigest(),
        "blake3": None,
        "bad_sectors": reader.bad_sectors,
        "bad_ranges": reader.bad_ranges,
        "bad_ranges_truncated": reader.bad_ranges_truncated,
        "sector_size": reader.sector_size,
        "source_size": reader.total_bytes,
        # False when the source ended before its declared size.
        "complete": not reader.ended_early,
    }


def _write_digest_files(output_path: str, result: dict[str, Any]) -> list[str]:
    """Write <output>.sha256 / <output>.md5 next to the image (sha256sum format), so that
    `verify` has a reference to compare against."""
    written: list[str] = []
    name = os.path.basename(output_path)
    for algo in ("sha256", "md5"):
        digest = result.get(algo)
        if not digest:
            continue
        companion = f"{output_path}.{algo}"
        with open(companion, "w", encoding="utf-8", newline="\n") as f:
            f.write(f"{digest}  {name}\n")
        written.append(companion)
    return written


def _write_raw(fd: int, total_bytes: int | None, output_path: str, retry: bool, retries: int,
               sector_size: int = DEFAULT_SECTOR_SIZE) -> dict[str, Any]:
    reader = _SourceReader(fd, total_bytes, retry=retry, retries=retries, sector_size=sector_size)
    with open(output_path, "wb") as out:
        written, hashers = _acquire(reader, out.write)
        out.flush()
        try:
            os.fsync(out.fileno())
        except OSError:
            pass
    return _result(output_path, written, hashers, reader)


def _import_pyewf(purpose: str) -> Any:
    try:
        import pyewf  # type: ignore[import-not-found]
    except Exception as ex:
        raise RuntimeError(
            f"libewf-python (pyewf) is not installed, so {purpose} is not possible. "
            "Install it with: pip install libewf-python"
        ) from ex
    return pyewf


def _write_ewf(fd: int, total_bytes: int | None, job: _Job) -> dict[str, Any]:
    """E01 path. Requires libewf-python. If it is missing this raises: raw data is never
    written under an .E01 name."""
    pyewf = _import_pyewf("writing an E01 image")
    output_path = str(job.require("OutputPath"))

    out = pyewf.handle()
    out.set_format(pyewf.format_encase6)
    out.set_compression_method(pyewf.compression_method_deflate)
    out.set_compression_values(int(job.get("CompressionLevel", 1)), 0)
    out.set_maximum_segment_size(int(job.get("SegmentSizeMiB", 2048)) * 1024 * 1024)
    out.set_header_value_case_number(job.get("CaseNumber", "") or "")
    out.set_header_value_evidence_number(job.get("EvidenceNumber", "") or "")
    out.set_header_value_examiner_name(job.get("ExaminerName", "") or "")
    out.set_header_value_description(job.get("Description", "") or "")
    out.set_header_value_notes(job.get("Notes", "") or "")
    out.set_header_value_acquiry_software_version("Cinder 0.0.1")

    out.open([output_path], pyewf.access_flag_write)
    reader = _SourceReader(
        fd, total_bytes,
        retry=bool(job.get("ReadErrorRetry", True)),
        retries=int(job.get("ReadErrorRetries", 2)),
        sector_size=int(job.get("SectorSize", DEFAULT_SECTOR_SIZE)),
    )
    try:
        written, hashers = _acquire(reader, out.write)
    finally:
        out.close()
    # For EWF the hashes are of the media (the acquired byte stream), as is conventional.
    return _result(output_path, written, hashers, reader)


@sidecar.method("image")
def image(params: Any) -> dict[str, Any]:
    job = _Job(params)
    fmt = _format_of(job)
    src = str(job.require("SourceDevice"))
    output_path = str(job.require("OutputPath"))
    if fmt not in ("raw", "ewf"):
        # There is no AFF4 / VHD / VHDX writer. Writing raw bytes under that name would
        # produce a file that claims a format it does not have.
        raise NotImplementedError(
            f"image format '{fmt}' is not implemented; acquire as Raw or Ewf and convert afterwards"
        )
    if fmt == "ewf":
        _import_pyewf("writing an E01 image")  # fail before touching the output path

    _progress.__dict__.update(_ProgressState().__dict__)
    _progress.phase = "opening"
    fd, total = _open_source(src)
    try:
        if fmt == "ewf":
            result = _write_ewf(fd, total, job)
        else:
            result = _write_raw(
                fd, total, output_path,
                bool(job.get("ReadErrorRetry", True)),
                int(job.get("ReadErrorRetries", 2)),
                int(job.get("SectorSize", DEFAULT_SECTOR_SIZE)),
            )
        result["digest_files"] = _write_digest_files(output_path, result)
        _progress.phase = "done"
        return result
    except BaseException:
        _progress.phase = "failed"
        raise
    finally:
        os.close(fd)


# -------------------- verify --------------------

_HEX_RE = re.compile(r"^[0-9a-fA-F]+$")


def _read_reference(path: str, algo: str, hex_len: int) -> str | None:
    """The digest recorded in <image>.<algo>, or None if there is no such file. A companion
    that exists but does not contain a digest is an error, not "no reference"."""
    companion = Path(f"{path}.{algo}")
    if not companion.is_file():
        return None
    with companion.open("rb") as f:
        text = f.read(4096).decode("utf-8", errors="replace")
    token = text.split()[0] if text.split() else ""
    if len(token) != hex_len or not _HEX_RE.match(token):
        raise ValueError(f"{companion.name} does not contain a valid {algo} digest")
    return token.lower()


def _iter_image_bytes(path: str) -> Iterator[bytes]:
    """The byte stream the digests are defined over: the file itself for raw images, the
    media stream for EWF containers."""
    if path.lower().endswith(EWF_SUFFIXES):
        pyewf = _import_pyewf("verifying an E01 image")
        handle = pyewf.handle()
        handle.open(pyewf.glob(path))
        try:
            while True:
                buf = handle.read(CHUNK_SIZE)
                if not buf:
                    return
                yield buf
        finally:
            handle.close()
    else:
        with open(path, "rb") as f:
            while True:
                buf = f.read(CHUNK_SIZE)
                if not buf:
                    return
                yield buf


@sidecar.method("verify")
def verify(params: Any) -> dict[str, Any]:
    p = str((params or {})["image_path"])
    expected_sha256 = _read_reference(p, "sha256", 64)
    expected_md5 = _read_reference(p, "md5", 32)

    h_sha256 = hashlib.sha256()
    h_md5 = hashlib.md5()
    total = 0
    _progress.phase = "verifying"
    for buf in _iter_image_bytes(p):
        h_sha256.update(buf)
        h_md5.update(buf)
        total += len(buf)
        report_progress(bytes_verified=total)

    actual_sha256 = h_sha256.hexdigest()
    actual_md5 = h_md5.hexdigest()
    comparisons = []
    if expected_sha256 is not None:
        comparisons.append(expected_sha256 == actual_sha256)
    if expected_md5 is not None:
        comparisons.append(expected_md5 == actual_md5)
    if comparisons:
        match: bool | None = all(comparisons)
        reason = None if match else "computed digest differs from the recorded digest"
    else:
        # Nothing to compare against. That is "unknown", never "verified".
        match = None
        reason = "no reference hash: neither a .sha256 nor a .md5 companion file exists for this image"
    return {
        "match": match,
        "reason": reason,
        "expected_sha256": expected_sha256,
        "actual_sha256": actual_sha256,
        "expected_md5": expected_md5,
        "actual_md5": actual_md5,
        "bytes_verified": total,
    }


@sidecar.method("convert")
def convert(params: Any) -> dict[str, Any]:
    """Convert E01 → raw. Hash-preserving when libewf is installed."""
    p = params or {}
    src = p["source"]
    dst = p["destination"]
    target_format = str(p.get("format", "raw") or "raw").lower()
    if target_format not in ("raw", "dd", "img"):
        raise NotImplementedError(f"convert to '{target_format}' is not implemented (raw output only)")
    if str(src).lower().endswith(EWF_SUFFIXES):
        _import_pyewf("converting an E01 image")  # fail before creating the destination
    h = hashlib.sha256()
    total = 0
    with open(dst, "wb") as out:
        for buf in _iter_image_bytes(str(src)):
            out.write(buf)
            h.update(buf)
            total += len(buf)
            report_progress(bytes_written=total)
    return {"output_path": dst, "bytes_written": total, "sha256": h.hexdigest()}


def main() -> None:
    asyncio.run(sidecar.serve())


if __name__ == "__main__":
    main()
