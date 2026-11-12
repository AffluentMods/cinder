"""Windows artifacts: Prefetch header parsing and browser-history extraction."""

from __future__ import annotations

import os
import sqlite3
import struct
import tempfile
from pathlib import Path

import pytest

from windows import win_worker as ww

# FILETIME for 2021-03-04 05:06:07 UTC.
FT_2021 = (ww._dt.datetime(2021, 3, 4, 5, 6, 7, tzinfo=ww._dt.timezone.utc) - ww._FILETIME_EPOCH)
FT_2021 = FT_2021.days * 864_000_000_000 + FT_2021.seconds * 10_000_000
FT_2020 = FT_2021 - 365 * 864_000_000_000


def make_prefetch(version: int, name: str = "CALC.EXE", path_hash: int = 0xDEADBEEF, run_count: int = 7,
                  last_runs: list[int] | None = None, variant2: bool = False, trailing_garbage: bool = True) -> bytes:
    """An uncompressed Prefetch header as Windows writes it: version, then "SCCA"."""
    last_runs = last_runs if last_runs is not None else [FT_2021]
    data = bytearray(0x200)
    struct.pack_into("<I", data, 0, version)
    data[4:8] = b"SCCA"
    struct.pack_into("<I", data, 8, 0x11)
    struct.pack_into("<I", data, 12, len(data))
    encoded = name.encode("utf-16-le") + b"\x00\x00"
    assert len(encoded) <= 60
    field = bytearray(encoded.ljust(60, b"\x00"))
    if trailing_garbage and len(encoded) < 58:
        # Windows does not clear the rest of the buffer: stale characters follow the NUL.
        field[len(encoded):len(encoded) + 4] = "ZZ".encode("utf-16-le")
    data[16:76] = field
    struct.pack_into("<I", data, 0x4C, path_hash)
    # The bytes right after the 60-byte name field are the hash and flags, not name.
    last_run_offset, slots, run_count_offset = ww._PF_LAYOUT[version]
    if version >= 30:
        struct.pack_into("<I", data, 0x54, 0x128 if variant2 else 0x130)
        if variant2:
            run_count_offset = 0xC8
    for i, filetime in enumerate(last_runs[:slots]):
        struct.pack_into("<Q", data, last_run_offset + 8 * i, filetime)
    struct.pack_into("<I", data, run_count_offset, run_count)
    return bytes(data)


@pytest.mark.parametrize("version", [17, 23, 26, 30, 31])
def test_uncompressed_prefetch_is_parsed_for_every_supported_version(version) -> None:
    entry = ww.parse_prefetch(make_prefetch(version, run_count=40 + version))
    assert entry["executable"] == "CALC.EXE"
    assert entry["hash"] == "DEADBEEF"
    assert entry["run_count"] == 40 + version
    assert entry["last_run"] == "2021-03-04T05:06:07+00:00"
    assert entry["version"] == version


def test_version_30_variant_2_reads_run_count_from_its_own_offset() -> None:
    data = bytearray(make_prefetch(30, run_count=12, variant2=True))
    struct.pack_into("<I", data, 0xD0, 999)  # where variant 1 keeps it: must be ignored
    assert ww.parse_prefetch(bytes(data))["run_count"] == 12


def test_version_26_and_30_return_all_recorded_run_times_newest_first() -> None:
    entry = ww.parse_prefetch(make_prefetch(26, last_runs=[FT_2021, FT_2020, 0, 0]))
    assert entry["all_run_times"] == ["2021-03-04T05:06:07+00:00", "2020-03-04T05:06:07+00:00"]
    assert entry["last_run"] == "2021-03-04T05:06:07+00:00"


def test_name_is_the_60_byte_field_cut_at_the_first_nul() -> None:
    # 29 characters fill the field exactly up to its terminator.
    long_name = "ABCDEFGHIJKLMNOPQRSTUVWXY.EXE"
    assert ww.parse_prefetch(make_prefetch(23, name=long_name))["executable"] == long_name
    # A short name followed by stale buffer contents and then the hash bytes.
    entry = ww.parse_prefetch(make_prefetch(23, name="A.EXE", path_hash=0x00410042))
    assert entry["executable"] == "A.EXE"


def test_absurd_last_run_time_becomes_null_and_keeps_the_entry() -> None:
    entry = ww.parse_prefetch(make_prefetch(23, last_runs=[2**64 - 1]))
    assert entry["last_run"] is None and entry["all_run_times"] == []
    assert entry["executable"] == "CALC.EXE" and entry["run_count"] == 7


@pytest.mark.parametrize("data, reason", [
    (b"MAM\x04" + b"\x00" * 100, "compressed (MAM), not decoded"),
    (b"MAM\x84" + b"\x00" * 100, "compressed (MAM), not decoded"),
    (b"SCCA" + b"\x00" * 300, "no SCCA signature at offset 4"),  # signature where the version belongs
    (b"", "no SCCA signature"),
    (struct.pack("<I", 99) + b"SCCA" + b"\x00" * 300, "unsupported Prefetch format version 99"),
    (make_prefetch(30)[:0x90], "truncated"),
])
def test_files_that_cannot_be_parsed_say_why(data, reason) -> None:
    with pytest.raises(ww.PrefetchSkipped, match=reason.replace("(", r"\(").replace(")", r"\)")):
        ww.parse_prefetch(data)


def test_prefetch_directory_reports_compressed_files_instead_of_dropping_them(tmp_path) -> None:
    (tmp_path / "CALC.EXE-11111111.pf").write_bytes(make_prefetch(23, run_count=3))
    (tmp_path / "NOTEPAD.EXE-22222222.PF").write_bytes(make_prefetch(17, name="NOTEPAD.EXE", run_count=9))
    (tmp_path / "CHROME.EXE-33333333.pf").write_bytes(b"MAM\x04" + os.urandom(64))
    (tmp_path / "JUNK.pf").write_bytes(b"not a prefetch file")
    (tmp_path / "readme.txt").write_text("ignored")
    result = ww.prefetch({"prefetch_dir": str(tmp_path)})
    assert [(e["executable"], e["run_count"], e["source_file"]) for e in result["entries"]] == [
        ("CALC.EXE", 3, "CALC.EXE-11111111.pf"),
        ("NOTEPAD.EXE", 9, "NOTEPAD.EXE-22222222.PF"),
    ]
    assert {s["file"]: s["reason"] for s in result["skipped"]} == {
        "CHROME.EXE-33333333.pf": "compressed (MAM), not decoded",
        "JUNK.pf": "not a Prefetch file (no SCCA signature at offset 4)",
    }
    assert result["compressed_not_decoded"] == 1


# -------------------- browser history --------------------

CHROME_2021 = int((ww._dt.datetime(2021, 3, 4, 5, 6, 7, tzinfo=ww._dt.timezone.utc) - ww._CHROME_EPOCH)
                  .total_seconds()) * 1_000_000
UNIX_2021_US = 1_614_834_367 * 1_000_000


def _snapshot(directory: Path) -> dict[str, tuple[int, int]]:
    return {p.name: (p.stat().st_size, p.stat().st_mtime_ns) for p in sorted(directory.iterdir())}


@pytest.fixture
def chrome_profile(tmp_path):
    profile = tmp_path / "Default"
    profile.mkdir()
    con = sqlite3.connect(profile / "History")
    con.execute("CREATE TABLE urls (url, title, visit_count, last_visit_time)")
    con.executemany("INSERT INTO urls VALUES (?, ?, ?, ?)", [
        ("https://example.com/good", "Good page", 3, CHROME_2021),
        ("https://example.com/unicode", "日本語 😀", 1, CHROME_2021 - 1_000_000),
        ("https://example.com/never", None, 0, 0),
    ])
    con.commit()
    con.close()
    return profile


def test_chrome_history_is_read_without_touching_the_profile_directory(chrome_profile, monkeypatch) -> None:
    made: list[str] = []
    real_mkdtemp = tempfile.mkdtemp

    def tracking_mkdtemp(*args, **kwargs):
        made.append(real_mkdtemp(*args, **kwargs))
        return made[-1]

    monkeypatch.setattr(tempfile, "mkdtemp", tracking_mkdtemp)
    before = _snapshot(chrome_profile)
    dir_mtime = chrome_profile.stat().st_mtime_ns

    result = ww.browser_history({"profile_path": str(chrome_profile), "browser": "Chrome", "user": "alice"})

    assert _snapshot(chrome_profile) == before, "nothing may be created or modified in the evidence directory"
    assert chrome_profile.stat().st_mtime_ns == dir_mtime
    assert len(made) == 1
    assert Path(made[0]).resolve().parent != chrome_profile.resolve()
    assert not Path(made[0]).exists(), "the temporary copy must be removed"

    assert [e["url"] for e in result["entries"]] == [
        "https://example.com/good", "https://example.com/unicode", "https://example.com/never"]
    good, uni, never = result["entries"]
    assert good == {"user": "alice", "browser": "chrome", "url": "https://example.com/good", "title": "Good page",
                    "visit_count": 3, "timestamp": "2021-03-04T05:06:07+00:00", "visit_type": None}
    assert uni["title"] == "日本語 😀"
    # Never visited: no time, and a visit count of 0 stays 0.
    assert never["timestamp"] is None and never["visit_count"] == 0
    assert result["row_errors"] == 0 and result["timestamp_errors"] == 0


def test_history_from_a_read_only_profile(chrome_profile) -> None:
    history = chrome_profile / "History"
    os.chmod(history, 0o444)
    try:
        result = ww.browser_history({"profile_path": str(chrome_profile), "browser": "edge"})
        assert len(result["entries"]) == 3
    finally:
        os.chmod(history, 0o644)


def test_one_hostile_row_does_not_fail_the_query(chrome_profile) -> None:
    con = sqlite3.connect(chrome_profile / "History")
    con.execute("INSERT INTO urls VALUES ('https://evil/invalid-utf8', CAST(X'41FF42C328' AS TEXT), 2, ?)",
                (CHROME_2021 + 1_000_000,))
    con.execute("INSERT INTO urls VALUES ('https://evil/huge-time', 'huge', 5, ?)", (2**63 - 1,))
    con.execute("INSERT INTO urls VALUES ('https://evil/negative-time', 'neg', 5, ?)", (-(2**63),))
    con.execute("INSERT INTO urls VALUES ('https://evil/text-time', 'txt', 'many', 'yesterday')")
    con.execute("INSERT INTO urls VALUES (X'68747470733A2F2FFF', X'00FF', 1, ?)", (CHROME_2021 + 2_000_000,))
    con.commit()
    con.close()

    result = ww.browser_history({"profile_path": str(chrome_profile), "browser": "chrome"})
    by_url = {e["url"]: e for e in result["entries"]}
    assert len(result["entries"]) == 8, "every row is kept"
    assert by_url["https://evil/invalid-utf8"]["title"] == "A�B�("
    assert by_url["https://evil/invalid-utf8"]["timestamp"] == "2021-03-04T05:06:08+00:00"
    for url in ("https://evil/huge-time", "https://evil/negative-time", "https://evil/text-time"):
        assert by_url[url]["timestamp"] is None, url
    assert by_url["https://evil/text-time"]["visit_count"] is None
    assert by_url["https://�"]["title"] == "\x00�"
    assert result["timestamp_errors"] == 3
    assert result["row_errors"] == 0
    # The good rows are untouched by their neighbours.
    assert by_url["https://example.com/good"]["timestamp"] == "2021-03-04T05:06:07+00:00"


def test_firefox_visits_that_only_exist_in_the_wal_are_returned(tmp_path) -> None:
    profile = tmp_path / "ff.default"
    profile.mkdir()
    db = profile / "places.sqlite"
    writer = sqlite3.connect(db)
    try:
        writer.execute("PRAGMA journal_mode=WAL")
        writer.execute("PRAGMA wal_autocheckpoint=0")
        writer.execute("CREATE TABLE moz_places (url, title, visit_count, last_visit_date)")
        writer.execute("INSERT INTO moz_places VALUES ('https://old.example/', 'Old', 4, ?)", (UNIX_2021_US,))
        writer.commit()
        writer.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        # Everything from here on lives only in places.sqlite-wal while Firefox is running.
        writer.execute("INSERT INTO moz_places VALUES ('https://recent.example/', 'Recent', 1, ?)",
                       (UNIX_2021_US + 60_000_000,))
        writer.execute("INSERT INTO moz_places VALUES ('https://bookmark.example/', 'Bookmark', 0, NULL)")
        writer.commit()
        assert (profile / "places.sqlite-wal").stat().st_size > 0
        before = _snapshot(profile)

        result = ww.browser_history({"profile_path": str(profile), "browser": "firefox", "user": "bob"})

        assert _snapshot(profile) == before
    finally:
        writer.close()
    by_url = {e["url"]: e for e in result["entries"]}
    assert set(by_url) == {"https://old.example/", "https://recent.example/", "https://bookmark.example/"}
    assert by_url["https://recent.example/"]["timestamp"] == "2021-03-04T05:07:07+00:00"
    assert by_url["https://old.example/"]["timestamp"] == "2021-03-04T05:06:07+00:00"
    assert by_url["https://bookmark.example/"]["timestamp"] is None
    assert by_url["https://bookmark.example/"]["visit_count"] == 0


def test_corrupt_database_returns_an_error_field_not_an_exception(tmp_path) -> None:
    profile = tmp_path / "p"
    profile.mkdir()
    (profile / "History").write_bytes(b"SQLite format 3\x00" + os.urandom(4096))
    result = ww.browser_history({"profile_path": str(profile), "browser": "chrome"})
    assert result["entries"] == []
    assert result["read_error"]
    assert sorted(p.name for p in profile.iterdir()) == ["History"]


def test_missing_database_and_unknown_browser() -> None:
    with tempfile.TemporaryDirectory() as d:
        assert ww.browser_history({"profile_path": d, "browser": "chrome"}) == {"entries": []}
        assert ww.browser_history({"profile_path": d, "browser": "netscape"}) == {"entries": []}
