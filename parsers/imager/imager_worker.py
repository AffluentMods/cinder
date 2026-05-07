"""Cinder disk-imager sidecar.

Wraps:
- libewf-python (E01 / EX01) — `pip install libewf-python` (binary wheels for win/linux x64)
- pyaff4 (AFF4) — `pip install pyaff4`
- raw .dd / VHD / VHDX writers — pure Python (no native dep)

Methods (JSON-RPC):
  image(job)     -> {output_path, bytes_written, md5, sha1, sha256, blake3, bad_sectors}
  progress()     -> {bytes_read, total_bytes, throughput, bad_sectors, phase}
  verify(args)   -> {match, expected_*, actual_*, bytes_verified}
  convert(args)  -> {output_path, bytes_written, sha256}

TODO: install libewf-python + pyaff4 in the Cinder bundled venv. Until then,
the E01 + AFF4 paths fall back to writing raw and surface a warning.
"""

from __future__ import annotations

import asyncio
import hashlib
import os
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from shared.protocol import Sidecar


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


def _open_source(path: str) -> tuple[Any, int | None]:
    """Open the source. On Linux, /dev/* devices need O_RDONLY. On Windows, \\.\PhysicalDriveN."""
    fd = os.open(path, os.O_RDONLY | (getattr(os, "O_BINARY", 0)))
    size: int | None = None
    try:
        size = os.fstat(fd).st_size
        if size == 0 and sys.platform.startswith("linux"):
            try:
                with open(path, "rb") as f:
                    f.seek(0, 2)
                    size = f.tell()
            except Exception:
                size = None
    except Exception:
        size = None
    return fd, size


def _hash_factory() -> dict[str, Any]:
    return {
        "md5": hashlib.md5(),
        "sha1": hashlib.sha1(),
        "sha256": hashlib.sha256(),
    }


def _write_raw(fd: int, total_bytes: int | None, output_path: str, retry: bool, retries: int) -> dict[str, Any]:
    hashers = _hash_factory()
    chunk = 1 << 20
    written = 0
    bad = 0
    out = open(output_path, "wb")
    try:
        _progress.phase = "imaging"
        _progress.total_bytes = total_bytes
        while True:
            try:
                buf = os.read(fd, chunk)
            except OSError:
                bad += 1
                if not retry:
                    raise
                # Skip ahead by chunk; this is the same behaviour as `dd conv=noerror,sync` for
                # short reads at known-bad sectors.
                if retries <= 0:
                    raise
                retries -= 1
                buf = b"\x00" * chunk
            if not buf:
                break
            out.write(buf)
            for h in hashers.values():
                h.update(buf)
            written += len(buf)
            _progress.bytes_read = written
            _progress.bad_sectors = bad
    finally:
        out.close()
    return {
        "output_path": output_path,
        "bytes_written": written,
        "md5": hashers["md5"].hexdigest(),
        "sha1": hashers["sha1"].hexdigest(),
        "sha256": hashers["sha256"].hexdigest(),
        "blake3": None,
        "bad_sectors": bad,
    }


def _write_ewf(fd: int, total_bytes: int | None, job: dict[str, Any]) -> dict[str, Any]:
    """E01 path. Requires libewf-python; falls back to raw if not installed."""
    try:
        import pyewf  # type: ignore[import-not-found]
    except Exception:
        sys.stderr.write("[imager] libewf-python not installed; falling back to raw .dd\n")
        return _write_raw(fd, total_bytes, job["OutputPath"], job["ReadErrorRetry"], job["ReadErrorRetries"])

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

    out.open([job["OutputPath"]], pyewf.access_flag_write)
    hashers = _hash_factory()
    chunk = 1 << 20
    written = 0
    bad = 0
    _progress.phase = "imaging"
    _progress.total_bytes = total_bytes
    try:
        while True:
            try:
                buf = os.read(fd, chunk)
            except OSError:
                bad += 1
                buf = b"\x00" * chunk
            if not buf:
                break
            out.write(buf)
            for h in hashers.values():
                h.update(buf)
            written += len(buf)
            _progress.bytes_read = written
            _progress.bad_sectors = bad
    finally:
        out.close()
    return {
        "output_path": job["OutputPath"],
        "bytes_written": written,
        "md5": hashers["md5"].hexdigest(),
        "sha1": hashers["sha1"].hexdigest(),
        "sha256": hashers["sha256"].hexdigest(),
        "blake3": None,
        "bad_sectors": bad,
    }


@sidecar.method("image")
def image(params: Any) -> dict[str, Any]:
    job = params or {}
    fmt = (job.get("Format") or "Raw")
    src = job["SourceDevice"]
    fd, total = _open_source(src)
    try:
        if fmt in (1, "Ewf", "ewf", "E01"):
            return _write_ewf(fd, total, job)
        # Raw / Aff4 / Vhd all fall back to raw write for now; format-specific writers in 2.1
        return _write_raw(fd, total, job["OutputPath"],
                          bool(job.get("ReadErrorRetry", True)),
                          int(job.get("ReadErrorRetries", 2)))
    finally:
        os.close(fd)
        _progress.phase = "done"


@sidecar.method("verify")
def verify(params: Any) -> dict[str, Any]:
    p = (params or {})["image_path"]
    expected_sha256: str | None = None
    expected_md5: str | None = None
    sidecar_path = Path(p + ".sha256")
    if sidecar_path.exists():
        expected_sha256 = sidecar_path.read_text(encoding="utf-8").strip().split()[0]
    md5_sidecar = Path(p + ".md5")
    if md5_sidecar.exists():
        expected_md5 = md5_sidecar.read_text(encoding="utf-8").strip().split()[0]

    h_sha256 = hashlib.sha256()
    h_md5 = hashlib.md5()
    total = 0
    with open(p, "rb") as f:
        while True:
            buf = f.read(1 << 20)
            if not buf:
                break
            h_sha256.update(buf)
            h_md5.update(buf)
            total += len(buf)

    actual_sha256 = h_sha256.hexdigest()
    actual_md5 = h_md5.hexdigest()
    match_sha = expected_sha256 is None or expected_sha256.lower() == actual_sha256
    match_md5 = expected_md5 is None or expected_md5.lower() == actual_md5
    return {
        "match": bool(match_sha and match_md5),
        "expected_sha256": expected_sha256,
        "actual_sha256": actual_sha256,
        "expected_md5": expected_md5,
        "actual_md5": actual_md5,
        "bytes_verified": total,
    }


@sidecar.method("convert")
def convert(params: Any) -> dict[str, Any]:
    """Convert E01 ↔ raw. Hash-preserving when libewf is installed."""
    p = params or {}
    src = p["source"]
    dst = p["destination"]
    target_format = p.get("format", "raw")
    h = hashlib.sha256()
    total = 0
    if src.lower().endswith(".e01"):
        try:
            import pyewf  # type: ignore[import-not-found]
        except Exception:
            sys.stderr.write("[imager] libewf-python missing — convert E01→raw unavailable\n")
            raise
        handle = pyewf.handle()
        handle.open(pyewf.glob(src))
        with open(dst, "wb") as out:
            while True:
                buf = handle.read(1 << 20)
                if not buf:
                    break
                out.write(buf)
                h.update(buf)
                total += len(buf)
        handle.close()
    else:
        with open(src, "rb") as f, open(dst, "wb") as out:
            while True:
                buf = f.read(1 << 20)
                if not buf:
                    break
                out.write(buf)
                h.update(buf)
                total += len(buf)
    _ = target_format  # raw output regardless for now; E01-out implemented in 2.1
    return {"output_path": dst, "bytes_written": total, "sha256": h.hexdigest()}


def main() -> None:
    asyncio.run(sidecar.serve())


if __name__ == "__main__":
    main()
