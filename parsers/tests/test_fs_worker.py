"""Filesystem worker, exercised against a fake pytsk3 (the real one is an optional,
native dependency). The fake mirrors the parts of the pytsk3 object model the worker
touches: Img_Info, FS_Info.open_dir / open_meta, File.info.name / info.meta, as_directory,
read_random."""

from __future__ import annotations

import base64
import types

import pytest

from filesystem import fs_worker as fw

TSK_DIR = 2
TSK_REG = 1


class FakeFile:
    def __init__(self, name, inode, *, children=None, data=b"", deleted=False, declared_size=None, **times):
        self.children = children
        self.data = data
        meta = types.SimpleNamespace(
            addr=inode, type=TSK_DIR if children is not None else TSK_REG,
            size=len(data) if declared_size is None else declared_size,
            crtime=times.get("crtime", 1_700_000_000), mtime=times.get("mtime", 1_700_000_100),
            atime=times.get("atime", 1_700_000_200), ctime=times.get("ctime", 1_700_000_300),
            uid=0, gid=0, mode=0o644,
        )
        name_info = types.SimpleNamespace(name=name.encode() if isinstance(name, str) else name,
                                          flags=0x02 if deleted else 0x01)
        self.info = types.SimpleNamespace(name=name_info, meta=meta)

    def as_directory(self):
        if self.children is None:
            raise OSError("not a directory")
        return [FakeFile(".", self.info.meta.addr, children=[]), FakeFile("..", 2, children=[]), *self.children]

    def read_random(self, offset, length):
        return self.data[offset:offset + length]


class FakeFs:
    def __init__(self, root_children, ftype="ntfs"):
        self.root = FakeFile("", 5, children=root_children)
        self.by_inode: dict[int, FakeFile] = {}
        self._index(self.root)
        self.info = types.SimpleNamespace(ftype=ftype, last_inum=max(self.by_inode) + 1)
        self.open_dir_calls = 0

    def _index(self, f, seen=None):
        seen = set() if seen is None else seen
        if id(f) in seen:
            return
        seen.add(id(f))
        self.by_inode.setdefault(f.info.meta.addr, f)
        for child in f.children or []:
            self._index(child, seen)

    def open_dir(self, path="/"):
        self.open_dir_calls += 1
        return self.root.as_directory()

    def open_meta(self, inode):
        if inode not in self.by_inode:
            raise OSError(f"invalid inode {inode}")
        return self.by_inode[inode]


@pytest.fixture
def install_fs(monkeypatch):
    """Make fs_worker use a fake pytsk3 serving the given tree."""
    def _install(fs: FakeFs) -> FakeFs:
        fake = types.SimpleNamespace(
            Img_Info=lambda path: object(),
            FS_Info=lambda img, offset=0: fs,
            **{f"TSK_FS_TYPE_{n}": n.lower() for n in (
                "NTFS", "FAT12", "FAT16", "FAT32", "EXFAT", "EXT2", "EXT3", "EXT4",
                "HFS", "APFS", "ISO9660", "UDF")},
        )
        monkeypatch.setattr(fw, "_import_tsk", lambda: fake)
        fw._active_walk.clear()
        return fs

    yield _install
    fw._active_walk.clear()


def _tree(dirs: int, files_per_dir: int) -> tuple[list[FakeFile], list[str]]:
    """dirs directories of files_per_dir files each, plus the expected depth-first order."""
    children, expected, inode = [], [], 100
    for d in range(dirs):
        files = []
        expected.append(f"/dir{d:03d}")
        dir_inode = inode
        inode += 1
        for f in range(files_per_dir):
            files.append(FakeFile(f"file{f:03d}.txt", inode))
            expected.append(f"/dir{d:03d}/file{f:03d}.txt")
            inode += 1
        children.append(FakeFile(f"dir{d:03d}", dir_inode, children=files))
    return children, expected


def _enumerate_all(limit: int, max_pages: int = 1000) -> tuple[list[dict], int]:
    """Page the way PytskFilesystemParser.EnumerateAsync does: stop on a short page."""
    rows, cursor, pages = [], 0, 0
    while pages < max_pages:
        page = fw.enumerate_page({"image_path": "img.dd", "offset": 0, "cursor": cursor, "limit": limit})
        pages += 1
        rows.extend(page["entries"])
        if len(page["entries"]) < limit:
            break
        cursor = page["next_cursor"]
    return rows, pages


def test_paging_returns_every_entry_of_a_nested_tree_exactly_once(install_fs) -> None:
    # 1,210 entries, all but 10 of them below the root: page 2 used to come back empty.
    children, expected = _tree(dirs=10, files_per_dir=120)
    install_fs(FakeFs(children))
    rows, pages = _enumerate_all(limit=500)
    assert [r["path"] for r in rows] == expected
    assert pages == 3


def test_page_boundaries_inside_and_between_directories(install_fs) -> None:
    children, expected = _tree(dirs=4, files_per_dir=5)
    install_fs(FakeFs(children))
    for limit in (1, 2, 5, 6, 7, 23, 24, 25):
        fw._active_walk.clear()
        rows, _ = _enumerate_all(limit=limit)
        assert [r["path"] for r in rows] == expected, f"limit={limit}"


def test_cursor_is_stateless_any_page_can_be_requested_cold(install_fs) -> None:
    children, expected = _tree(dirs=3, files_per_dir=10)
    install_fs(FakeFs(children))
    for cursor in (0, 1, 11, 12, 20, 32, 33, 40):
        fw._active_walk.clear()  # no walk in progress: the cursor alone must locate the page
        page = fw.enumerate_page({"image_path": "img.dd", "cursor": cursor, "limit": 4})
        assert [r["path"] for r in page["entries"]] == expected[cursor:cursor + 4]
        assert page["next_cursor"] == cursor + len(page["entries"])


def test_sequential_pages_continue_one_walk_instead_of_restarting(install_fs) -> None:
    children, expected = _tree(dirs=5, files_per_dir=50)
    fs = install_fs(FakeFs(children))
    rows, pages = _enumerate_all(limit=40)
    assert len(rows) == len(expected) and pages > 5
    assert fs.open_dir_calls == 1


def test_directory_loop_terminates(install_fs) -> None:
    looping = FakeFile("loop", 200, children=[])
    looping.children.append(FakeFile("again", 200, children=looping.children))  # same inode, same entries
    install_fs(FakeFs([looping, FakeFile("after.txt", 300)]))
    rows, _ = _enumerate_all(limit=50, max_pages=20)
    assert [r["path"] for r in rows] == ["/loop", "/loop/again", "/after.txt"]


def test_unreadable_entry_is_counted_and_the_rest_of_the_directory_survives(install_fs) -> None:
    broken = FakeFile("broken", 400)
    broken.info.name = None  # accessing .name raises
    install_fs(FakeFs([FakeFile("a.txt", 401), broken, FakeFile("b.txt", 402)]))
    page = fw.enumerate_page({"image_path": "img.dd", "cursor": 0, "limit": 500})
    assert [r["name"] for r in page["entries"]] == ["a.txt", "b.txt"]
    assert page["errors"] == 1


@pytest.mark.parametrize("epoch", [10**15, 2**63 - 1, -(2**62), 253402300800, "garbage"])
def test_ts_returns_none_for_unrepresentable_timestamps(epoch) -> None:
    assert fw._ts(epoch) is None


def test_ts_handles_ordinary_and_pre_1970_values() -> None:
    assert fw._ts(0) is None
    assert fw._ts(1_700_000_000) == "2023-11-14T22:13:20+00:00"
    assert fw._ts(-86400) == "1969-12-31T00:00:00+00:00"
    assert fw._ts(253402300799) == "9999-12-31T23:59:59+00:00"


def test_one_absurd_timestamp_does_not_hide_the_directory(install_fs) -> None:
    install_fs(FakeFs([
        FakeFile("before.txt", 501),
        FakeFile("stomped.exe", 502, mtime=10**15, crtime=2**63 - 1),
        FakeFile("after.txt", 503),
    ]))
    page = fw.enumerate_page({"image_path": "img.dd", "cursor": 0, "limit": 500})
    by_name = {r["name"]: r for r in page["entries"]}
    assert list(by_name) == ["before.txt", "stomped.exe", "after.txt"]
    assert by_name["stomped.exe"]["mtime"] is None and by_name["stomped.exe"]["btime"] is None
    assert by_name["stomped.exe"]["atime"] == "2023-11-14T22:16:40+00:00"
    assert page["errors"] == 0


def test_parse_mft_keeps_timestomped_entries_and_pages(install_fs) -> None:
    files = [FakeFile(f"f{i}", 10 + i) for i in range(30)]
    files[7] = FakeFile("stomped", 17, crtime=10**15, mtime=2**63 - 1)
    install_fs(FakeFs(files))

    first = fw.parse_mft({"image_path": "img.dd", "cursor": 0, "limit": 20})
    assert first["next_cursor"] == 20 and first["done"] is False
    stomped = next(e for e in first["entries"] if e["inode"] == 17)
    assert stomped["btime_si"] is None and stomped["mtime_si"] is None
    assert stomped["btime_si_raw"] == 10**15 and stomped["mtime_si_raw"] == 2**63 - 1

    seen, cursor, pages = [], 0, 0
    while True:
        page = fw.parse_mft({"image_path": "img.dd", "cursor": cursor, "limit": 20})
        pages += 1
        assert len(page["entries"]) <= 20
        seen.extend(e["inode"] for e in page["entries"])
        cursor = page["next_cursor"]
        if page["done"]:
            break
    assert pages == 2
    assert seen == [5, *range(10, 40)]


def test_parse_mft_page_size_is_capped(install_fs) -> None:
    install_fs(FakeFs([FakeFile("a", 10)]))
    fs = fw._import_tsk().FS_Info(None)
    fs.info.last_inum = 10**9
    page = fw.parse_mft({"image_path": "img.dd", "limit": 10**9})
    assert page["next_cursor"] == fw.MAX_MFT_PAGE
    assert page["done"] is False


def test_read_file_returns_ranges(install_fs) -> None:
    data = bytes(range(256)) * 40
    install_fs(FakeFs([FakeFile("data.bin", 600, data=data)]))
    whole = fw.read_file({"image_path": "img.dd", "inode": 600})
    assert base64.b64decode(whole["data_b64"]) == data
    assert whole["eof"] is True and whole["size"] == len(data)

    part = fw.read_file({"image_path": "img.dd", "inode": 600, "file_offset": 1000, "length": 300})
    assert base64.b64decode(part["data_b64"]) == data[1000:1300]
    assert part["eof"] is False and part["length"] == 300

    tail = fw.read_file({"image_path": "img.dd", "inode": 600, "file_offset": len(data) - 10, "length": 500})
    assert base64.b64decode(tail["data_b64"]) == data[-10:]
    assert tail["eof"] is True


def test_read_file_never_returns_more_than_the_cap(install_fs, monkeypatch) -> None:
    monkeypatch.setattr(fw, "MAX_READ_BYTES", 1024)
    data = b"A" * 5000
    install_fs(FakeFs([FakeFile("big.bin", 700, data=data)]))
    # Whole-file request for a file over the cap: an error, not a silently truncated file.
    with pytest.raises(ValueError, match="file_offset"):
        fw.read_file({"image_path": "img.dd", "inode": 700})
    # An over-long range is clamped and says it is not the end.
    part = fw.read_file({"image_path": "img.dd", "inode": 700, "length": 10**12})
    assert part["length"] == 1024 and part["eof"] is False
    # Reading in ranges reassembles the file.
    got, offset = b"", 0
    while True:
        chunk = fw.read_file({"image_path": "img.dd", "inode": 700, "file_offset": offset, "length": 1024})
        got += base64.b64decode(chunk["data_b64"])
        offset += chunk["length"]
        if chunk["eof"]:
            break
    assert got == data


def test_read_file_does_not_trust_a_hostile_declared_size(install_fs) -> None:
    # 2**62 bytes declared, 3 bytes present: bounded work, and no attempt to buffer it all.
    install_fs(FakeFs([FakeFile("liar.bin", 800, data=b"abc", declared_size=2**62)]))
    with pytest.raises(ValueError, match="limit"):
        fw.read_file({"image_path": "img.dd", "inode": 800})
    part = fw.read_file({"image_path": "img.dd", "inode": 800, "length": 4096})
    assert base64.b64decode(part["data_b64"]) == b"abc"
    assert part["eof"] is True
