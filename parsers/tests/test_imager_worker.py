"""Imager: alignment under read errors, retry cap, format honesty, verification."""

from __future__ import annotations

import hashlib
import os
import sys
import types

import pytest

from imager import imager_worker as iw

MIB = 1 << 20


class FlakySource:
    """Patches os.read so that chosen offsets of one fd fail, the way a bad sector does.

    A failed read does not move the file position (it is undefined after an error on a
    real device; "did not move" is the case that duplicates data if the caller carries
    on reading blindly).
    """

    def __init__(self, monkeypatch, fd: int, bad: dict[int, int]) -> None:
        # bad: sector-aligned offset -> number of times a read touching it fails
        # (-1 = always).
        self.fd = fd
        self.bad = dict(bad)
        self.failures = 0
        self._real_read = os.read
        monkeypatch.setattr(os, "read", self._read)

    def _read(self, fd: int, n: int) -> bytes:
        if fd != self.fd:
            return self._real_read(fd, n)
        pos = os.lseek(fd, 0, os.SEEK_CUR)
        for offset, remaining in self.bad.items():
            if remaining != 0 and pos <= offset < pos + n:
                if remaining > 0:
                    self.bad[offset] = remaining - 1
                self.failures += 1
                if self.failures > 10_000:
                    raise AssertionError("reader is retrying without bound")
                raise OSError(5, "Input/output error")
        return self._real_read(fd, n)


@pytest.fixture
def source(tmp_path):
    data = os.urandom(3 * MIB + 1536)
    path = tmp_path / "source.bin"
    path.write_bytes(data)
    return path, data


def _image_with_faults(monkeypatch, tmp_path, source, bad, **job):
    path, data = source
    out = tmp_path / "out.dd"
    real_open = iw._open_source
    flaky: list[FlakySource] = []

    def open_and_break(p):
        fd, size = real_open(p)
        flaky.append(FlakySource(monkeypatch, fd, bad))
        return fd, size

    monkeypatch.setattr(iw, "_open_source", open_and_break)
    result = iw.image({"sourceDevice": str(path), "outputPath": str(out), "format": 0, **job})
    return result, out.read_bytes(), data


def test_job_keys_are_accepted_in_camel_pascal_and_snake_case(tmp_path, source) -> None:
    path, data = source
    digest = hashlib.sha256(data).hexdigest()
    jobs = [
        # What the C# caller sends: JsonSerializerDefaults.Web => camelCase, enum as number.
        {"sourceDevice": str(path), "outputPath": str(tmp_path / "a.dd"), "format": 0,
         "readErrorRetry": True, "readErrorRetries": 2, "caseNumber": None},
        {"SourceDevice": str(path), "OutputPath": str(tmp_path / "b.dd"), "Format": "Raw"},
        {"source_device": str(path), "output_path": str(tmp_path / "c.dd")},
    ]
    for job in jobs:
        result = iw.image(job)
        assert result["sha256"] == digest
        assert result["bytes_written"] == len(data)


def test_missing_required_field_is_a_clear_error_not_a_keyerror(tmp_path) -> None:
    with pytest.raises(ValueError, match="SourceDevice"):
        iw.image({"outputPath": str(tmp_path / "x.dd")})


def test_transient_read_error_does_not_lengthen_or_shift_the_image(monkeypatch, tmp_path, source) -> None:
    # The sector at 1 MiB + 4096 fails once and then reads fine.
    result, image, data = _image_with_faults(monkeypatch, tmp_path, source, {MIB + 4096: 1})
    assert len(image) == len(data)
    assert image == data
    assert result["bad_sectors"] == 0
    assert result["bytes_written"] == len(data)
    assert result["sha256"] == hashlib.sha256(data).hexdigest()


def test_persistent_bad_sector_is_zero_filled_in_place_and_counted(monkeypatch, tmp_path, source) -> None:
    bad_offset = 2 * MIB + 512 * 7
    result, image, data = _image_with_faults(monkeypatch, tmp_path, source, {bad_offset: -1})
    assert len(image) == len(data)
    expected = bytearray(data)
    expected[bad_offset:bad_offset + 512] = b"\x00" * 512
    assert image == bytes(expected), "only the unreadable sector may differ from the source"
    assert result["bad_sectors"] == 1
    assert result["bad_ranges"] == [[bad_offset, 512]]
    # The reported hash is the hash of the image as written, and the result says it is
    # not a clean acquisition.
    assert result["sha256"] == hashlib.sha256(bytes(expected)).hexdigest()
    assert result["sha256"] != hashlib.sha256(data).hexdigest()


def test_retries_per_sector_are_capped(monkeypatch, tmp_path, source) -> None:
    path, data = source
    out = tmp_path / "out.dd"
    real_open = iw._open_source
    holder: list[FlakySource] = []

    def open_and_break(p):
        fd, size = real_open(p)
        holder.append(FlakySource(monkeypatch, fd, {0: -1}))
        return fd, size

    monkeypatch.setattr(iw, "_open_source", open_and_break)
    iw.image({"sourceDevice": str(path), "outputPath": str(out), "readErrorRetries": 3})
    # One failed chunk read, then 1 + 3 attempts at the bad sector itself.
    assert holder[0].failures == 1 + 4


def test_read_error_with_retry_disabled_fails_the_job(monkeypatch, tmp_path, source) -> None:
    with pytest.raises(OSError):
        _image_with_faults(monkeypatch, tmp_path, source, {4096: -1}, readErrorRetry=False)


def test_unsized_source_that_never_reads_again_stops_instead_of_zero_filling_forever(monkeypatch, tmp_path) -> None:
    # A device whose size cannot be determined and which fails from 1 MiB onwards: the
    # old EWF loop wrote zeros until the disk filled.
    path = tmp_path / "device.bin"
    path.write_bytes(os.urandom(MIB))
    fd = os.open(path, os.O_RDONLY | getattr(os, "O_BINARY", 0))
    real_read = os.read
    calls = {"n": 0}

    def read(f, n):
        if f == fd and os.lseek(f, 0, os.SEEK_CUR) >= MIB:
            calls["n"] += 1
            if calls["n"] > 1_000_000:
                raise AssertionError("unbounded retry loop")
            raise OSError(5, "Input/output error")
        return real_read(f, n)

    monkeypatch.setattr(os, "read", read)
    monkeypatch.setattr(iw, "MAX_UNSIZED_BAD_RUN", 64 * 1024)
    reader = iw._SourceReader(fd, None, retry=True, retries=1)
    try:
        produced = 0
        with pytest.raises(OSError, match="size is unknown"):
            for chunk in reader:
                produced += len(chunk)
        assert produced == MIB
    finally:
        os.close(fd)


def test_ewf_job_without_pyewf_raises_and_writes_nothing(monkeypatch, tmp_path, source) -> None:
    path, _ = source
    monkeypatch.setitem(sys.modules, "pyewf", None)  # import pyewf -> ImportError
    out = tmp_path / "evidence.E01"
    with pytest.raises(RuntimeError, match="libewf-python"):
        iw.image({"sourceDevice": str(path), "outputPath": str(out), "format": 1})
    assert not out.exists(), "raw data must never be written under an .E01 name"
    assert list(tmp_path.glob("evidence.E01*")) == []


@pytest.mark.parametrize("fmt", [2, 3, 4, "Aff4", "Vhd", "Vhdx"])
def test_formats_without_a_writer_are_refused(tmp_path, source, fmt) -> None:
    path, _ = source
    out = tmp_path / "evidence.img"
    with pytest.raises(NotImplementedError):
        iw.image({"sourceDevice": str(path), "outputPath": str(out), "format": fmt})
    assert not out.exists()


class _FakeEwfHandle:
    """Stand-in for pyewf.handle: records what is written, with a persistent read fault
    on the source it would loop on forever without a seek and a retry cap."""

    written: bytearray

    def __init__(self) -> None:
        self.written = bytearray()
        self.closed = False

    def __getattr__(self, name):
        if name.startswith("set_"):
            return lambda *a, **k: None
        raise AttributeError(name)

    def open(self, names, flags=None):
        self.names = names

    def write(self, buf):
        self.written += buf

    def close(self):
        self.closed = True


def test_ewf_path_with_a_persistent_bad_sector_terminates_and_stays_aligned(monkeypatch, tmp_path, source) -> None:
    path, data = source
    handles: list[_FakeEwfHandle] = []

    def make_handle():
        handles.append(_FakeEwfHandle())
        return handles[-1]

    fake = types.SimpleNamespace(
        handle=make_handle, format_encase6=6, compression_method_deflate=1, access_flag_write=2,
    )
    monkeypatch.setitem(sys.modules, "pyewf", fake)
    bad_offset = MIB + 1024
    real_open = iw._open_source

    def open_and_break(p):
        fd, size = real_open(p)
        FlakySource(monkeypatch, fd, {bad_offset: -1})
        return fd, size

    monkeypatch.setattr(iw, "_open_source", open_and_break)
    out = tmp_path / "evidence.E01"
    result = iw.image({"sourceDevice": str(path), "outputPath": str(out), "format": 1})

    expected = bytearray(data)
    expected[bad_offset:bad_offset + 512] = b"\x00" * 512
    assert bytes(handles[0].written) == bytes(expected)
    assert handles[0].closed
    assert result["bad_sectors"] == 1
    assert result["bytes_written"] == len(data)


def test_ewf_image_then_verify_compare_the_media_stream(monkeypatch, tmp_path, source) -> None:
    # A fake pyewf that stores the media bytes per container name. The container file on
    # disk is deliberately different from the media, as a real (compressed) E01 is: the
    # companion digest and verify must both be about the media stream.
    path, data = source
    store: dict[str, bytearray] = {}

    class Handle:
        def __getattr__(self, name):
            if name.startswith("set_"):
                return lambda *a, **k: None
            raise AttributeError(name)

        def open(self, names, flags=None):
            self.name = names[0]
            self.pos = 0
            if flags is not None:
                store[self.name] = bytearray()
                with open(self.name, "wb") as f:
                    f.write(b"EVF\x09\x0d\x0a\xff\x00 container bytes, not media")

        def write(self, buf):
            store[self.name] += buf

        def read(self, n):
            chunk = bytes(store[self.name][self.pos:self.pos + n])
            self.pos += len(chunk)
            return chunk

        def close(self):
            pass

    fake = types.SimpleNamespace(handle=Handle, glob=lambda p: [p], format_encase6=6,
                                 compression_method_deflate=1, access_flag_write=2)
    monkeypatch.setitem(sys.modules, "pyewf", fake)
    out = tmp_path / "evidence.E01"
    result = iw.image({"sourceDevice": str(path), "outputPath": str(out), "format": "Ewf"})
    assert result["sha256"] == hashlib.sha256(data).hexdigest()
    assert (tmp_path / "evidence.E01.sha256").read_text(encoding="utf-8").split()[0] == result["sha256"]

    verified = iw.verify({"image_path": str(out)})
    assert verified["match"] is True and verified["bytes_verified"] == len(data)

    store[str(out)][100] ^= 0xFF
    assert iw.verify({"image_path": str(out)})["match"] is False

    # Without pyewf an E01 cannot be verified; hashing the container file instead would
    # compare the wrong bytes.
    monkeypatch.setitem(sys.modules, "pyewf", None)
    with pytest.raises(RuntimeError, match="libewf-python"):
        iw.verify({"image_path": str(out)})


def test_convert_refuses_a_target_format_it_cannot_write(tmp_path, source) -> None:
    path, data = source
    dst = tmp_path / "copy.dd"
    result = iw.convert({"source": str(path), "destination": str(dst), "format": "raw"})
    assert dst.read_bytes() == data and result["sha256"] == hashlib.sha256(data).hexdigest()
    with pytest.raises(NotImplementedError):
        iw.convert({"source": str(path), "destination": str(tmp_path / "copy.E01"), "format": "e01"})
    assert not (tmp_path / "copy.E01").exists()


def test_image_writes_companion_digests_and_verify_uses_them(tmp_path, source) -> None:
    path, data = source
    out = tmp_path / "out.dd"
    result = iw.image({"sourceDevice": str(path), "outputPath": str(out)})
    sha_file = tmp_path / "out.dd.sha256"
    md5_file = tmp_path / "out.dd.md5"
    assert result["digest_files"] == [str(sha_file), str(md5_file)]
    assert sha_file.read_text(encoding="utf-8") == f"{hashlib.sha256(data).hexdigest()}  out.dd\n"
    assert md5_file.read_text(encoding="utf-8").split()[0] == hashlib.md5(data).hexdigest()

    verified = iw.verify({"image_path": str(out)})
    assert verified["match"] is True
    assert verified["bytes_verified"] == len(data)

    # Flip one byte of the image: verification must now fail.
    corrupted = bytearray(out.read_bytes())
    corrupted[12345] ^= 0xFF
    out.write_bytes(bytes(corrupted))
    tampered = iw.verify({"image_path": str(out)})
    assert tampered["match"] is False
    assert tampered["reason"]


def test_imaging_reports_progress_the_client_can_show(spawn, tmp_path, source) -> None:
    # The worker is serial: a `progress` request sent during `image` is answered only after
    # the image is done. What the C# SidecarDiskImager shows comes from these notifications,
    # so they have to carry every field of its progress record.
    path, data = source
    sc = spawn("-m", "imager.imager_worker")
    result = sc.call("image", {"sourceDevice": str(path), "outputPath": str(tmp_path / "p.dd"), "format": 0})
    assert result["bytes_written"] == len(data)
    assert sc.notifications, "no progress notification during imaging"
    note = sc.notifications[0]["params"]
    assert note["id"] == 1
    assert 0 < note["bytes_read"] <= len(data)
    assert note["total_bytes"] == len(data)
    assert note["bad_sectors"] == 0
    assert note["phase"] == "imaging"
    assert isinstance(note["throughput"], (int, float)) and note["throughput"] >= 0


def test_verify_without_any_reference_hash_does_not_claim_a_match(tmp_path, source) -> None:
    path, data = source
    result = iw.verify({"image_path": str(path)})
    assert result["match"] is None
    assert "no reference hash" in result["reason"]
    assert result["expected_sha256"] is None and result["expected_md5"] is None
    assert result["actual_sha256"] == hashlib.sha256(data).hexdigest()


def test_verify_with_a_malformed_companion_is_an_error(tmp_path, source) -> None:
    path, _ = source
    (tmp_path / "source.bin.sha256").write_text("not a digest\n", encoding="utf-8")
    with pytest.raises(ValueError, match="valid sha256"):
        iw.verify({"image_path": str(path)})


def test_verify_through_the_protocol_reports_null_match(spawn, tmp_path, source) -> None:
    # End to end, as ImageVerifier would see it: "match" must be JSON null, which the C#
    # side reads as "not verified" (false), never true.
    path, _ = source
    sc = spawn("-m", "imager.imager_worker")
    result = sc.call("verify", {"image_path": str(path)})
    assert "match" in result and result["match"] is None
