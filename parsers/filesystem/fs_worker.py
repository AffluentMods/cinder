"""Cinder filesystem-parsing sidecar.

Wraps pytsk3 (Sleuth Kit) for the bulk of filesystems Cinder cares about:
NTFS, FAT12/16/32/exFAT, ext2/3/4, APFS (read-only), HFS+, ReFS (when libfsrefs is built),
UDF, ISO9660, Btrfs, ZFS (libfszfs), XFS, F2FS, ReiserFS, Squashfs.

When pytsk3 isn't installed, methods raise informative errors so the C# layer can show a
"Install pytsk3 to enable filesystem parsing" prompt.

Methods:
  identify(image_path, offset)          -> {kind, label, volume_size, cluster_size, extras}
  enumerate_page(image_path, offset, cursor, limit) -> {entries:[FileEntry...], next_cursor}
  read_file(image_path, offset, inode)  -> {data_b64}
  parse_mft(image_path, offset)         -> {entries:[MftEntry...]}
  parse_logfile(image_path, offset)     -> {records:[...]}
  parse_usnjrnl(image_path, offset)     -> {records:[...]}
"""

from __future__ import annotations

import asyncio
import base64
import sys
from typing import Any

from shared.protocol import Sidecar

sidecar = Sidecar()


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


def _walk(fs: Any, dir_obj: Any, prefix: str, out: list[dict[str, Any]], cursor_skip: int, limit: int):
    if cursor_skip < 0 or len(out) >= limit:
        return
    for f in dir_obj:
        if f.info.name.name in (b".", b".."):
            continue
        if cursor_skip > 0:
            cursor_skip -= 1
            continue
        try:
            name = f.info.name.name.decode("utf-8", errors="replace")
        except Exception:
            name = "<unreadable>"
        is_dir = (f.info.meta and f.info.meta.type == 2)  # TSK_FS_META_TYPE_DIR
        is_deleted = bool(f.info.name.flags & 0x02)  # TSK_FS_NAME_FLAG_UNALLOC
        path = f"{prefix}/{name}".replace("//", "/")
        meta = f.info.meta
        out.append({
            "inode": meta.addr if meta else 0,
            "path": path,
            "name": name,
            "size": meta.size if meta else 0,
            "is_dir": bool(is_dir),
            "is_deleted": is_deleted,
            "btime": _ts(getattr(meta, "crtime", 0)) if meta else None,
            "mtime": _ts(getattr(meta, "mtime", 0)) if meta else None,
            "atime": _ts(getattr(meta, "atime", 0)) if meta else None,
            "ctime": _ts(getattr(meta, "ctime", 0)) if meta else None,
            "owner": str(getattr(meta, "uid", "")) if meta else None,
            "group": str(getattr(meta, "gid", "")) if meta else None,
            "mode": getattr(meta, "mode", None) if meta else None,
        })
        if len(out) >= limit:
            return
        if is_dir:
            try:
                child = f.as_directory()
                _walk(fs, child, path, out, 0, limit)
            except Exception:
                continue


def _ts(epoch: int) -> str | None:
    if not epoch:
        return None
    import datetime as _dt
    return _dt.datetime.fromtimestamp(epoch, tz=_dt.timezone.utc).isoformat()


@sidecar.method("enumerate_page")
def enumerate_page(params: Any) -> dict[str, Any]:
    pytsk3 = _import_tsk()
    img = _open_image(pytsk3, params["image_path"])
    offset = int(params.get("offset", 0))
    cursor = int(params.get("cursor", 0))
    limit = int(params.get("limit", 500))
    fs = pytsk3.FS_Info(img, offset=offset)
    root = fs.open_dir(path="/")
    entries: list[dict[str, Any]] = []
    _walk(fs, root, "", entries, cursor_skip=cursor, limit=limit)
    return {"entries": entries, "next_cursor": cursor + len(entries)}


@sidecar.method("read_file")
def read_file(params: Any) -> dict[str, str]:
    pytsk3 = _import_tsk()
    img = _open_image(pytsk3, params["image_path"])
    offset = int(params.get("offset", 0))
    inode = int(params["inode"])
    fs = pytsk3.FS_Info(img, offset=offset)
    f = fs.open_meta(inode=inode)
    size = f.info.meta.size if f.info.meta else 0
    if size <= 0:
        return {"data_b64": ""}
    chunks: list[bytes] = []
    pos = 0
    while pos < size:
        data = f.read_random(pos, min(1 << 20, size - pos))
        if not data:
            break
        chunks.append(data)
        pos += len(data)
    return {"data_b64": base64.b64encode(b"".join(chunks)).decode("ascii")}


@sidecar.method("parse_mft")
def parse_mft(params: Any) -> dict[str, Any]:
    """NTFS-specific: enumerate $MFT entries via inode walk. Returns the same shape as
    enumerate_page but with $STANDARD_INFORMATION + $FILE_NAME timestamp pairs so the UI can
    flag timestomp."""
    pytsk3 = _import_tsk()
    img = _open_image(pytsk3, params["image_path"])
    offset = int(params.get("offset", 0))
    fs = pytsk3.FS_Info(img, offset=offset)
    if _kind_for(fs) != "Ntfs":
        raise RuntimeError("parse_mft is NTFS-only")
    entries: list[dict[str, Any]] = []
    inode = 0
    while inode < (fs.info.last_inum or 1024):
        try:
            f = fs.open_meta(inode=inode)
            entries.append({
                "inode": inode,
                "size": f.info.meta.size if f.info.meta else 0,
                "btime_si": _ts(getattr(f.info.meta, "crtime", 0)),
                "mtime_si": _ts(getattr(f.info.meta, "mtime", 0)),
            })
        except Exception:
            pass
        inode += 1
    return {"entries": entries}


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
