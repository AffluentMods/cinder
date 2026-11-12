"""Cinder filesystem-parsing sidecar.

Wraps pytsk3 (Sleuth Kit) for the bulk of filesystems Cinder cares about:
NTFS, FAT12/16/32/exFAT, ext2/3/4, APFS (read-only), HFS+, ReFS (when libfsrefs is built),
UDF, ISO9660, Btrfs, ZFS (libfszfs), XFS, F2FS, ReiserFS, Squashfs.

When pytsk3 isn't installed, methods raise informative errors so the C# layer can show a
"Install pytsk3 to enable filesystem parsing" prompt.

Methods:
  identify(image_path, offset)          -> {kind, label, volume_size, cluster_size, extras}
  enumerate_page(image_path, offset, cursor, limit)
                                        -> {entries:[FileEntry...], next_cursor, done, errors}
  read_file(image_path, offset, inode, file_offset?, length?)
                                        -> {data_b64, file_offset, length, size, eof}
  parse_mft(image_path, offset, cursor?, limit?)
                                        -> {entries:[MftEntry...], next_cursor, done, last_inode}
  parse_logfile(image_path, offset)     -> {records:[...]}
  parse_usnjrnl(image_path, offset)     -> {records:[...]}
"""

from __future__ import annotations

import asyncio
import base64
import datetime as _dt
import sys
from typing import Any

from shared.protocol import Sidecar, report_progress

sidecar = Sidecar()

_EPOCH = _dt.datetime(1970, 1, 1, tzinfo=_dt.timezone.utc)

DEFAULT_PAGE_LIMIT = 500
MAX_PAGE_LIMIT = 10_000
MAX_READ_BYTES = 8 << 20  # hard cap on the bytes one read_file call returns
DEFAULT_MFT_PAGE = 1_000
MAX_MFT_PAGE = 10_000


def _import_tsk():
    try:
        import pytsk3  # type: ignore[import-not-found]
        return pytsk3
    except Exception as ex:
        raise RuntimeError(
            "pytsk3 is not installed. Install with: pip install pytsk3 libewf-python"
        ) from ex


def _open_image(pytsk3: Any, path: str):
    """Open raw or E01 — heuristic on extension."""
    if path.lower().endswith((".e01", ".ex01", ".s01")):
        try:
            import pyewf  # type: ignore[import-not-found]
        except Exception as ex:
            raise RuntimeError("libewf-python is required to open E01 images") from ex
        names = pyewf.glob(path)
        ewf = pyewf.handle()
        ewf.open(names)

        class _EwfImg(pytsk3.Img_Info):
            def __init__(self, handle):
                self._h = handle
                super().__init__(url="", type=pytsk3.TSK_IMG_TYPE_EXTERNAL)

            def read(self, offset, size):
                self._h.seek(offset)
                return self._h.read(size)

            def get_size(self):
                return self._h.get_media_size()

            def close(self):
                self._h.close()

        return _EwfImg(ewf)
    return pytsk3.Img_Info(path)


def _kind_for(fs_info: Any) -> str:
    pytsk3 = _import_tsk()
    types = {
        pytsk3.TSK_FS_TYPE_NTFS: "Ntfs",
        pytsk3.TSK_FS_TYPE_FAT12: "Fat12",
        pytsk3.TSK_FS_TYPE_FAT16: "Fat16",
        pytsk3.TSK_FS_TYPE_FAT32: "Fat32",
        pytsk3.TSK_FS_TYPE_EXFAT: "ExFat",
        pytsk3.TSK_FS_TYPE_EXT2: "Ext2",
        pytsk3.TSK_FS_TYPE_EXT3: "Ext3",
        pytsk3.TSK_FS_TYPE_EXT4: "Ext4",
        pytsk3.TSK_FS_TYPE_HFS: "HfsPlus",
        pytsk3.TSK_FS_TYPE_APFS: "Apfs",
        pytsk3.TSK_FS_TYPE_ISO9660: "Iso9660",
        pytsk3.TSK_FS_TYPE_UDF: "Udf",
    }
    return types.get(fs_info.info.ftype, "Unknown")


@sidecar.method("identify")
def identify(params: Any) -> dict[str, Any]:
    pytsk3 = _import_tsk()
    img = _open_image(pytsk3, params["image_path"])
    offset = int(params.get("offset", 0))
    fs = pytsk3.FS_Info(img, offset=offset)
    return {
        "kind": _kind_for(fs),
        "label": getattr(fs.info, "label", None),
        "volume_size": fs.info.block_count * fs.info.block_size if fs.info.block_count else None,
        "cluster_size": fs.info.block_size,
        "extras": {
            "block_count": fs.info.block_count,
            "first_block": fs.info.first_block,
            "last_block": fs.info.last_block,
        },
    }


def _ts(epoch: Any) -> str | None:
    """Seconds since 1970 (UTC) as ISO 8601, or None when the value is unset or cannot be
    represented. A timestomped or corrupt entry (year 30000, 2**63, garbage) yields None
    for that one field; it must never cost the caller the row, let alone the directory."""
    if not epoch:
        return None
    try:
        return (_EPOCH + _dt.timedelta(seconds=int(epoch))).isoformat()
    except (ValueError, OverflowError, OSError, TypeError):
        return None


def _raw_epoch(epoch: Any) -> int | None:
    """The stored value itself, so an out-of-range timestamp stays visible as evidence."""
    try:
        return int(epoch) if epoch else None
    except (ValueError, OverflowError, TypeError):
        return None


def _int_param(params: Any, name: str, default: int, lo: int, hi: int) -> int:
    value = params.get(name) if isinstance(params, dict) else None
    try:
        number = default if value is None else int(value)
    except (TypeError, ValueError) as ex:
        raise ValueError(f"'{name}' must be an integer") from ex
    return max(lo, min(hi, number))


def _entry_for(f: Any, prefix: str) -> dict[str, Any] | None:
    """One directory entry as a row; None for the "." / ".." entries."""
    raw_name = f.info.name.name
    if raw_name in (b".", b".."):
        return None
    try:
        name = raw_name.decode("utf-8", errors="replace")
    except Exception:
        name = "<unreadable>"
    meta = f.info.meta
    is_dir = bool(meta and meta.type == 2)  # TSK_FS_META_TYPE_DIR
    is_deleted = bool(f.info.name.flags & 0x02)  # TSK_FS_NAME_FLAG_UNALLOC
    path = f"{prefix}/{name}".replace("//", "/")
    return {
        "inode": meta.addr if meta else 0,
        "path": path,
        "name": name,
        "size": meta.size if meta else 0,
        "is_dir": is_dir,
        "is_deleted": is_deleted,
        "btime": _ts(getattr(meta, "crtime", 0)) if meta else None,
        "mtime": _ts(getattr(meta, "mtime", 0)) if meta else None,
        "atime": _ts(getattr(meta, "atime", 0)) if meta else None,
        "ctime": _ts(getattr(meta, "ctime", 0)) if meta else None,
        "owner": str(getattr(meta, "uid", "")) if meta else None,
        "group": str(getattr(meta, "gid", "")) if meta else None,
        "mode": getattr(meta, "mode", None) if meta else None,
    }


class _Walk:
    """A depth-first walk of one filesystem that can be consumed a page at a time.

    The order is: an entry, then (if it is a directory) everything below it, then its next
    sibling. ``position`` counts every entry produced so far at any depth, which is exactly
    what ``next_cursor`` counts, so a cursor identifies one place in the walk whatever the
    depth it falls at.

    Hostile images are expected: an unreadable entry or directory is counted in ``errors``
    and skipped, and a directory inode is only ever descended into once, so a directory
    loop cannot recurse without end.
    """

    def __init__(self, root_dir: Any) -> None:
        self.position = 0
        self.errors = 0
        self._stack: list[tuple[Any, str]] = [(iter(root_dir), "")]
        self._visited_dirs: set[int] = set()

    def __iter__(self) -> "_Walk":
        return self

    def __next__(self) -> dict[str, Any]:
        while self._stack:
            entries, prefix = self._stack[-1]
            try:
                f = next(entries)
            except StopIteration:
                self._stack.pop()
                continue
            except Exception:
                self.errors += 1  # directory became unreadable part-way: leave it
                self._stack.pop()
                continue
            try:
                entry = _entry_for(f, prefix)
            except Exception:
                self.errors += 1
                continue
            if entry is None:
                continue
            if entry["is_dir"] and entry["inode"] not in self._visited_dirs:
                self._visited_dirs.add(entry["inode"])
                try:
                    self._stack.append((iter(f.as_directory()), entry["path"]))
                except Exception:
                    self.errors += 1
            self.position += 1
            report_progress(entries=self.position)
            return entry
        raise StopIteration

    def skip_to(self, position: int) -> None:
        """Consume entries until ``position`` of them have been produced."""
        while self.position < position:
            try:
                next(self)
            except StopIteration:
                return


# The walk in progress, kept so that page N+1 continues where page N stopped instead of
# re-walking from the root (which would make a full enumeration quadratic). One walk only:
# it pins an open image handle.
_active_walk: dict[str, Any] = {}


def _walk_for(image_path: str, offset: int, cursor: int) -> _Walk:
    key = (image_path, offset)
    walk = _active_walk.get("walk")
    if _active_walk.get("key") == key and walk is not None and walk.position == cursor:
        return walk
    _active_walk.clear()
    pytsk3 = _import_tsk()
    img = _open_image(pytsk3, image_path)
    fs = pytsk3.FS_Info(img, offset=offset)
    walk = _Walk(fs.open_dir(path="/"))
    walk.skip_to(cursor)
    _active_walk.update(key=key, walk=walk, fs=fs, img=img)  # fs/img: keep the handles alive
    return walk


@sidecar.method("enumerate_page")
def enumerate_page(params: Any) -> dict[str, Any]:
    image_path = params["image_path"]
    offset = int(params.get("offset", 0))
    cursor = _int_param(params, "cursor", 0, 0, sys.maxsize)
    limit = _int_param(params, "limit", DEFAULT_PAGE_LIMIT, 1, MAX_PAGE_LIMIT)
    walk = _walk_for(image_path, offset, cursor)
    entries: list[dict[str, Any]] = []
    for entry in walk:
        entries.append(entry)
        if len(entries) >= limit:
            break
    done = len(entries) < limit
    result = {
        "entries": entries,
        "next_cursor": cursor + len(entries),
        "done": done,
        "errors": walk.errors,
    }
    if done:
        _active_walk.clear()
    return result


@sidecar.method("read_file")
def read_file(params: Any) -> dict[str, Any]:
    """Read a byte range of a file. ``offset`` is the volume offset inside the image (as for
    every other method); the range within the file is ``file_offset`` / ``length``.

    One call never returns more than MAX_READ_BYTES: the declared size in a hostile image
    can be anything, and the reply is a single base64 JSON line. A caller that omits
    ``length`` gets the whole file only if it fits under the cap; otherwise the call fails
    (rather than silently returning a truncated file) and the caller must read in ranges.
    """
    pytsk3 = _import_tsk()
    img = _open_image(pytsk3, params["image_path"])
    offset = int(params.get("offset", 0))
    inode = int(params["inode"])
    file_offset = _int_param(params, "file_offset", 0, 0, sys.maxsize)
    fs = pytsk3.FS_Info(img, offset=offset)
    f = fs.open_meta(inode=inode)
    try:
        size = int(f.info.meta.size) if f.info.meta else 0
    except (TypeError, ValueError):
        size = 0
    size = max(size, 0)
    remaining = max(size - file_offset, 0)
    if params.get("length") is None:
        if remaining > MAX_READ_BYTES:
            raise ValueError(
                f"file is {size} bytes, more than the {MAX_READ_BYTES}-byte limit of one read_file "
                "call; read it in ranges with 'file_offset' and 'length'"
            )
        length = remaining
    else:
        length = min(_int_param(params, "length", 0, 0, MAX_READ_BYTES), remaining)

    chunks: list[bytes] = []
    got = 0
    while got < length:
        data = f.read_random(file_offset + got, min(1 << 20, length - got))
        if not data:
            break
        data = data[: length - got]
        chunks.append(data)
        got += len(data)
    return {
        "data_b64": base64.b64encode(b"".join(chunks)).decode("ascii"),
        "file_offset": file_offset,
        "length": got,
        "size": size,
        "eof": file_offset + got >= size or got < length,
    }


@sidecar.method("parse_mft")
def parse_mft(params: Any) -> dict[str, Any]:
    """NTFS-specific: enumerate $MFT entries via inode walk, one page at a time
    (``cursor`` = first inode of the page, ``limit`` = inodes per page). Returns
    $STANDARD_INFORMATION timestamps so the UI can flag timestomp.

    A timestamp that cannot be represented is reported as null next to its raw stored
    value (``*_raw``) and the entry is kept: those are exactly the entries this method
    exists to surface."""
    pytsk3 = _import_tsk()
    img = _open_image(pytsk3, params["image_path"])
    offset = int(params.get("offset", 0))
    fs = pytsk3.FS_Info(img, offset=offset)
    if _kind_for(fs) != "Ntfs":
        raise RuntimeError("parse_mft is NTFS-only")
    last = int(fs.info.last_inum or 1024)
    cursor = _int_param(params, "cursor", 0, 0, sys.maxsize)
    limit = _int_param(params, "limit", DEFAULT_MFT_PAGE, 1, MAX_MFT_PAGE)
    stop = max(min(cursor + limit, last), cursor)
    entries: list[dict[str, Any]] = []
    unreadable = 0
    for inode in range(cursor, stop):
        report_progress(inode=inode, last_inode=last)
        try:
            f = fs.open_meta(inode=inode)
            meta = f.info.meta
        except Exception:
            unreadable += 1  # unallocated / invalid MFT record
            continue
        btime = getattr(meta, "crtime", 0)
        mtime = getattr(meta, "mtime", 0)
        try:
            size = int(meta.size) if meta else 0
        except (TypeError, ValueError):
            size = 0
        entries.append({
            "inode": inode,
            "size": size,
            "btime_si": _ts(btime),
            "mtime_si": _ts(mtime),
            "btime_si_raw": _raw_epoch(btime),
            "mtime_si_raw": _raw_epoch(mtime),
        })
    return {
        "entries": entries,
        "next_cursor": stop,
        "done": stop >= last,
        "last_inode": last,
        "unreadable": unreadable,
    }


@sidecar.method("parse_logfile")
def parse_logfile(params: Any) -> dict[str, Any]:
    """TODO: $LogFile / $UsnJrnl/$J detailed parsing. analyzeMFT or USN-Journal-Parser
    integration in Phase 3.1; for now, expose the raw metadata enumeration so the UI has
    something to show."""
    _ = params
    return {"records": [], "todo": "phase-3.1: $LogFile parser"}


@sidecar.method("parse_usnjrnl")
def parse_usnjrnl(params: Any) -> dict[str, Any]:
    _ = params
    return {"records": [], "todo": "phase-3.1: $UsnJrnl/$J parser"}


def main() -> None:
    asyncio.run(sidecar.serve())


if __name__ == "__main__":
    main()
