"""Cinder Windows-artifact sidecar.

One process, many methods. Each method routes to a focused parser (regipy, python-evtx,
parsing of .pf/.lnk/.automaticDestinations/.customDestinations). When a library isn't
installed, the method raises a RuntimeError describing what to install. The sidecar protocol
turns that into a JSON-RPC error that the C# layer can surface as user-friendly UI.

Required Python deps (install in the bundled venv):
    pip install regipy python-evtx pylnk3 libesedb-python construct

Methods (all return JSON-serializable dicts):
    registry_userassist(ntuser_path)            -> {entries:[{user, program, run_count, focus_ms, last_executed}]}
    registry_shimcache(system_hive)             -> {entries:[{path, modified, executed}]}
    registry_amcache(amcache_hive)              -> {entries:[{path, sha1, first_seen, publisher}]}
    registry_usb(system_hive)                   -> {entries:[{device_id, friendly_name, first_connected, last_connected, serial}]}
    registry_wifi(software_hive)                -> {entries:[{ssid, first_seen, last_seen, auth}]}
    prefetch(prefetch_dir)                      -> {entries:[{executable, hash, run_count, last_run, all_run_times[], loaded_files[], version, source_file}],
                                                    skipped:[{file, reason}], compressed_not_decoded}
    shellbags(ntuser_path, user)                -> {entries:[{user, path, first_accessed, last_accessed, access_count}]}
    jumplists(jumplist_dir, user)               -> {entries:[{user, app_id, target_path, access_time}]}
    lnk(path)                                   -> {target_path, arguments, icon, working_dir, target_created, target_modified, target_accessed, volume_serial, machine_id}
    evtx_page(path, cursor, limit)              -> {entries:[{record_id, event_id, provider, channel, computer, user, timestamp, level, summary}]}
    browser_history(profile_path, browser, user)-> {entries:[{user, browser, url, title, visit_count, timestamp, visit_type}],
                                                    row_errors, timestamp_errors, read_error?}
    srum_applications(srudb_path)               -> {entries:[{user, application, timestamp, fg_cpu_ms, bytes_read, bytes_written}]}
"""

from __future__ import annotations

import asyncio
import datetime as _dt
import sqlite3
import struct
import sys
from pathlib import Path
from typing import Any

from shared.protocol import Sidecar, report_progress

sidecar = Sidecar()


def _utc_iso(ts) -> str | None:
    if ts is None:
        return None
    if isinstance(ts, (int, float)):
        try:
            return (_dt.datetime(1970, 1, 1, tzinfo=_dt.timezone.utc) + _dt.timedelta(seconds=ts)).isoformat()
        except (OverflowError, ValueError):
            return None  # not a representable time: unknown, never a made-up value
    if isinstance(ts, _dt.datetime):
        if ts.tzinfo is None:
            ts = ts.replace(tzinfo=_dt.timezone.utc)
        return ts.astimezone(_dt.timezone.utc).isoformat()
    return str(ts)


# -------------------- Registry (regipy) --------------------

def _open_hive(path: str):
    try:
        from regipy.registry import RegistryHive  # type: ignore[import-not-found]
    except Exception as ex:
        raise RuntimeError("regipy is not installed (pip install regipy)") from ex
    return RegistryHive(path)


@sidecar.method("registry_userassist")
def registry_userassist(params: Any) -> dict[str, Any]:
    from regipy.plugins.ntuser.user_assist import UserAssistPlugin  # type: ignore[import-not-found]
    hive = _open_hive(params["ntuser_path"])
    plugin = UserAssistPlugin(hive)
    plugin.run()
    out = []
    for r in plugin.entries or []:
        out.append({
            "user": r.get("user", ""),
            "program": r.get("name", ""),
            "run_count": int(r.get("run_count", 0) or 0),
            "focus_ms": r.get("focus_time", None),
            "last_executed": _utc_iso(r.get("last_execution", None)),
        })
    return {"entries": out}


@sidecar.method("registry_shimcache")
def registry_shimcache(params: Any) -> dict[str, Any]:
    from regipy.plugins.system.shimcache import ShimCachePlugin  # type: ignore[import-not-found]
    hive = _open_hive(params["system_hive"])
    plugin = ShimCachePlugin(hive)
    plugin.run()
    out = []
    for r in plugin.entries or []:
        out.append({
            "path": r.get("path", ""),
            "modified": _utc_iso(r.get("last_mod_date", None)),
            "executed": bool(r.get("exec_flag", False)),
        })
    return {"entries": out}


@sidecar.method("registry_amcache")
def registry_amcache(params: Any) -> dict[str, Any]:
    from regipy.plugins.amcache.amcache import AmCachePlugin  # type: ignore[import-not-found]
    hive = _open_hive(params["amcache_hive"])
    plugin = AmCachePlugin(hive)
    plugin.run()
    out = []
    for r in plugin.entries or []:
        out.append({
            "path": r.get("file_path", ""),
            "sha1": r.get("sha1", None),
            "first_seen": _utc_iso(r.get("first_run", None) or r.get("creation", None)),
            "publisher": r.get("publisher", None),
        })
    return {"entries": out}


@sidecar.method("registry_usb")
def registry_usb(params: Any) -> dict[str, Any]:
    hive = _open_hive(params["system_hive"])
    out = []
    try:
        usbstor = hive.get_key(r"\ControlSet001\Enum\USBSTOR")
        for sub in usbstor.iter_subkeys():
            for serial in sub.iter_subkeys():
                friendly = next((v.value for v in serial.iter_values() if v.name == "FriendlyName"), "")
                out.append({
                    "device_id": f"{sub.name}\\{serial.name}",
                    "friendly_name": friendly or sub.name,
                    "first_connected": None,  # populated by setupapi.dev.log parser in 4.1
                    "last_connected": _utc_iso(serial.last_modified),
                    "serial": serial.name,
                })
    except Exception:
        pass
    return {"entries": out}


@sidecar.method("registry_wifi")
def registry_wifi(params: Any) -> dict[str, Any]:
    hive = _open_hive(params["software_hive"])
    out = []
    try:
        nl = hive.get_key(r"\Microsoft\WlanSvc\Interfaces")
        for iface in nl.iter_subkeys():
            try:
                for net in iface.subkey("Profiles").iter_subkeys() if iface.subkey("Profiles") else []:
                    out.append({
                        "ssid": net.name,
                        "first_seen": None,
                        "last_seen": _utc_iso(net.last_modified),
                        "auth": None,
                    })
            except Exception:
                continue
    except Exception:
        pass
    return {"entries": out}


# -------------------- Prefetch --------------------

_FILETIME_EPOCH = _dt.datetime(1601, 1, 1, tzinfo=_dt.timezone.utc)

# Only the header and the file-information block are parsed, so only that much is read.
_PF_READ_BYTES = 0x200
_PF_NAME_OFFSET = 0x10
_PF_NAME_BYTES = 60        # UTF-16LE, 29 characters + terminator
_PF_HASH_OFFSET = 0x4C
_PF_METRICS_OFFSET_FIELD = 0x54

# Format version -> (offset of the last-run FILETIME array, slots in it, offset of the run count).
# The file-information block differs per version; these offsets are from the start of the file.
#   17 = Windows XP / 2003, 23 = Vista / 7, 26 = 8 / 8.1, 30 = 10 / 11, 31 = 11 (same layout as 30)
_PF_LAYOUT: dict[int, tuple[int, int, int]] = {
    17: (0x78, 1, 0x90),
    23: (0x80, 1, 0x98),
    26: (0x80, 8, 0xD0),
    30: (0x80, 8, 0xD0),
    31: (0x80, 8, 0xD0),
}
# Version 30/31 has a second, 8-byte-shorter file-information variant (later Windows 10
# builds). It is recognised by where the metrics array starts; its run count sits at 0xC8.
_PF_V30_VARIANT2_METRICS_OFFSET = 0x128
_PF_V30_VARIANT2_RUN_COUNT = 0xC8


def _filetime_to_dt(filetime: int) -> _dt.datetime | None:
    """FILETIME (100 ns ticks since 1601) -> datetime, or None if unset or out of range."""
    if not filetime:
        return None
    try:
        return _FILETIME_EPOCH + _dt.timedelta(microseconds=filetime // 10)
    except (OverflowError, ValueError):
        return None


class PrefetchSkipped(Exception):
    """This file is not something the parser can turn into a row; str(ex) says why."""


def parse_prefetch(data: bytes) -> dict[str, Any]:
    """Parse the header of one uncompressed Prefetch file.

    Layout: bytes 0-3 format version, bytes 4-7 the signature ``SCCA``, 8-11 unknown,
    12-15 file size, 16-75 executable name, 76-79 path hash. Raises PrefetchSkipped for
    anything else, including MAM-compressed files (Windows 10+), which are identified but
    not decoded here.
    """
    if data[:3] == b"MAM":
        raise PrefetchSkipped("compressed (MAM), not decoded")
    if len(data) < 8 or data[4:8] != b"SCCA":
        raise PrefetchSkipped("not a Prefetch file (no SCCA signature at offset 4)")
    version = struct.unpack_from("<I", data, 0)[0]
    layout = _PF_LAYOUT.get(version)
    if layout is None:
        raise PrefetchSkipped(f"unsupported Prefetch format version {version}")
    last_run_offset, last_run_slots, run_count_offset = layout
    if version >= 30 and len(data) >= _PF_METRICS_OFFSET_FIELD + 4:
        metrics_offset = struct.unpack_from("<I", data, _PF_METRICS_OFFSET_FIELD)[0]
        if metrics_offset == _PF_V30_VARIANT2_METRICS_OFFSET:
            run_count_offset = _PF_V30_VARIANT2_RUN_COUNT
    needed = max(last_run_offset + 8 * last_run_slots, run_count_offset + 4)
    if len(data) < needed:
        raise PrefetchSkipped(f"truncated: {len(data)} bytes, version {version} header needs {needed}")

    raw_name = data[_PF_NAME_OFFSET:_PF_NAME_OFFSET + _PF_NAME_BYTES]
    name = raw_name.decode("utf-16-le", errors="replace").split("\x00", 1)[0]
    file_hash = struct.unpack_from("<I", data, _PF_HASH_OFFSET)[0]
    run_count = struct.unpack_from("<I", data, run_count_offset)[0]
    run_times = [
        _filetime_to_dt(struct.unpack_from("<Q", data, last_run_offset + 8 * i)[0])
        for i in range(last_run_slots)
    ]
    run_times = [t for t in run_times if t is not None]  # slot 0 is the most recent run
    return {
        "executable": name,
        "hash": f"{file_hash:08X}",
        "run_count": int(run_count),
        "last_run": _utc_iso(run_times[0]) if run_times else None,
        "all_run_times": [_utc_iso(t) for t in run_times],
        "loaded_files": [],
        "version": version,
    }


@sidecar.method("prefetch")
def prefetch(params: Any) -> dict[str, Any]:
    """Minimal Prefetch parser: executable name, path hash, run count and last-run times for
    format versions 17, 23, 26 and 30/31. MAM-compressed files (the norm on Windows 10+)
    are not decoded: each one is listed in ``skipped`` with the reason, never dropped
    silently. (TODO 4.1: decode MAM / shell out to PECmd when available.)"""
    pf_dir = Path(params["prefetch_dir"])
    out: list[dict[str, Any]] = []
    skipped: list[dict[str, str]] = []
    for f in sorted(pf_dir.iterdir(), key=lambda p: p.name.lower()):
        if f.suffix.lower() != ".pf":
            continue
        try:
            if not f.is_file():
                raise PrefetchSkipped("not a regular file")
            with f.open("rb") as fh:
                data = fh.read(_PF_READ_BYTES)
            entry = parse_prefetch(data)
            entry["source_file"] = f.name
            out.append(entry)
        except PrefetchSkipped as ex:
            skipped.append({"file": f.name, "reason": str(ex)})
        except Exception as ex:
            skipped.append({"file": f.name, "reason": f"unreadable: {ex}"})
            sys.stderr.write(f"[prefetch] {f.name}: {ex}\n")
    return {
        "entries": out,
        "skipped": skipped,
        "compressed_not_decoded": sum(1 for s in skipped if s["reason"].startswith("compressed")),
    }


# -------------------- Shellbags / Jumplists --------------------

@sidecar.method("shellbags")
def shellbags(params: Any) -> dict[str, Any]:
    """TODO 4.1: full Shellbag parsing (Eric Zimmerman's SBECmd structure). For now, emit only
    UsrClass.dat key timestamps so timeline has *something* to plot."""
    try:
        hive = _open_hive(params["ntuser_path"])
    except RuntimeError:
        return {"entries": [], "todo": "install regipy"}
    out = []
    user = params.get("user", "?")
    try:
        bags = hive.get_key(r"\Software\Microsoft\Windows\Shell\BagMRU")
        for k in bags.iter_subkeys():
            out.append({
                "user": user, "path": k.name,
                "first_accessed": None, "last_accessed": _utc_iso(k.last_modified),
                "access_count": 1,
            })
    except Exception:
        pass
    return {"entries": out}


@sidecar.method("jumplists")
def jumplists(params: Any) -> dict[str, Any]:
    jl_dir = Path(params["jumplist_dir"])
    user = params.get("user", "?")
    out = []
    for f in jl_dir.glob("*.automaticDestinations-ms"):
        try:
            stat = f.stat()
            app_id = f.stem.split(".")[0]
            out.append({
                "user": user, "app_id": app_id,
                "target_path": str(f),
                "access_time": _utc_iso(stat.st_mtime),
            })
        except Exception:
            continue
    # TODO 4.1: parse the OLE compound stream to yield individual DestList entries instead of
    # one row per .automaticDestinations file.
    return {"entries": out}


# -------------------- LNK --------------------

@sidecar.method("lnk")
def lnk(params: Any) -> dict[str, Any]:
    try:
        import pylnk3  # type: ignore[import-not-found]
    except Exception as ex:
        raise RuntimeError("pylnk3 is not installed (pip install pylnk3)") from ex
    parsed = pylnk3.parse(params["path"])
    return {
        "target_path": str(getattr(parsed, "path", "") or ""),
        "arguments": getattr(parsed, "arguments", None),
        "icon": getattr(parsed, "icon", None),
        "working_dir": getattr(parsed, "working_dir", None),
        "target_created": _utc_iso(getattr(parsed, "creation_time", None)),
        "target_modified": _utc_iso(getattr(parsed, "modification_time", None)),
        "target_accessed": _utc_iso(getattr(parsed, "access_time", None)),
        "volume_serial": getattr(parsed, "volume_serial_number", None),
        "machine_id": getattr(parsed, "machine_identifier", None),
    }


# -------------------- EVTX --------------------

_evtx_cache: dict[str, list[dict[str, Any]]] = {}


def _load_evtx(path: str) -> list[dict[str, Any]]:
    if path in _evtx_cache:
        return _evtx_cache[path]
    try:
        from Evtx.Evtx import Evtx  # type: ignore[import-not-found]
        from Evtx.Views import evtx_record_xml_view  # type: ignore[import-not-found]
        import xml.etree.ElementTree as ET
    except Exception as ex:
        raise RuntimeError("python-evtx is not installed (pip install python-evtx)") from ex

    rows: list[dict[str, Any]] = []
    ns = "{http://schemas.microsoft.com/win/2004/08/events/event}"
    with Evtx(path) as evtx:
        for r in evtx.records():
            report_progress(records=len(rows))
            try:
                xml = evtx_record_xml_view(r)
                root = ET.fromstring(xml)
                sys_el = root.find(f"{ns}System")
                event_id = int((sys_el.findtext(f"{ns}EventID") or 0) if sys_el is not None else 0)
                provider = sys_el.find(f"{ns}Provider").get("Name", "") if sys_el is not None else ""
                channel = sys_el.findtext(f"{ns}Channel", "") if sys_el is not None else ""
                computer = sys_el.findtext(f"{ns}Computer", "") if sys_el is not None else ""
                ts_str = sys_el.find(f"{ns}TimeCreated").get("SystemTime", "") if sys_el is not None else ""
                level = sys_el.findtext(f"{ns}Level", "") if sys_el is not None else ""
                summary_parts = []
                edata = root.find(f"{ns}EventData")
                if edata is not None:
                    for d in edata.findall(f"{ns}Data"):
                        if d.text:
                            summary_parts.append(f"{d.get('Name', '?')}={d.text}")
                rows.append({
                    "record_id": int(sys_el.findtext(f"{ns}EventRecordID", "0")) if sys_el is not None else 0,
                    "event_id": event_id, "provider": provider, "channel": channel,
                    "computer": computer, "user": None, "timestamp": ts_str,
                    "level": level,
                    "summary": " | ".join(summary_parts[:6]) or f"EventID {event_id}",
                })
            except Exception as ex:
                sys.stderr.write(f"[evtx] record skipped: {ex}\n")
    _evtx_cache[path] = rows
    return rows


@sidecar.method("evtx_page")
def evtx_page(params: Any) -> dict[str, Any]:
    rows = _load_evtx(params["path"])
    cursor = int(params.get("cursor", 0))
    limit = int(params.get("limit", 1000))
    return {"entries": rows[cursor:cursor + limit]}


# -------------------- Browser history (Chromium / Firefox) --------------------

_CHROME_EPOCH = _dt.datetime(1601, 1, 1, tzinfo=_dt.timezone.utc)
_UNIX_EPOCH = _dt.datetime(1970, 1, 1, tzinfo=_dt.timezone.utc)

# A live SQLite database is more than one file: recent writes sit in <db>-wal (indexed by
# <db>-shm) until a checkpoint, and an interrupted transaction leaves <db>-journal. Firefox
# runs places.sqlite in WAL mode, so without the -wal file the newest visits are missing.
_SQLITE_COMPANION_SUFFIXES = ("-wal", "-shm", "-journal")


def _micros_to_dt(epoch: _dt.datetime, micros: Any) -> _dt.datetime | None:
    """Microseconds since ``epoch`` -> datetime. None when unset; raises ValueError when the
    stored value is not a representable time (hostile or corrupt row)."""
    if micros is None or micros == 0:
        return None
    if isinstance(micros, bool) or not isinstance(micros, (int, float)):
        raise ValueError(f"timestamp is not a number: {type(micros).__name__}")
    try:
        return epoch + _dt.timedelta(microseconds=micros)
    except (OverflowError, ValueError) as ex:
        raise ValueError(f"timestamp out of range: {micros}") from ex


def _chrome_to_dt(micros: Any) -> _dt.datetime | None:
    return _micros_to_dt(_CHROME_EPOCH, micros)


def _lossy_text(value: bytes) -> str:
    return value.decode("utf-8", errors="replace")


def _as_text(value: Any) -> str | None:
    if value is None:
        return None
    if isinstance(value, (bytes, bytearray)):
        return _lossy_text(bytes(value))
    return str(value)


def _read_history_db(db_path: Path, query: str) -> tuple[list[tuple[Any, ...]], str | None]:
    """Run ``query`` against a private copy of ``db_path`` and return (rows, read_error).

    The evidence directory is never written to: the database and its -wal / -shm /
    -journal companions are copied into a fresh temporary directory (which is removed
    afterwards) and SQLite only ever opens the copy. Text is decoded leniently so one
    title with invalid UTF-8 cannot fail the query. If the database is damaged part-way
    through, the rows read up to that point are returned together with the error text.
    """
    import shutil
    import tempfile

    tmp_dir = Path(tempfile.mkdtemp(prefix="cinder-history-"))
    rows: list[tuple[Any, ...]] = []
    read_error: str | None = None
    try:
        copy = tmp_dir / db_path.name
        shutil.copyfile(db_path, copy)  # contents only: a read-only source must not make a read-only copy
        for suffix in _SQLITE_COMPANION_SUFFIXES:
            companion = db_path.with_name(db_path.name + suffix)
            if companion.is_file():
                shutil.copyfile(companion, tmp_dir / companion.name)
        con = sqlite3.connect(str(copy))
        try:
            con.text_factory = _lossy_text
            try:
                for row in con.execute(query):
                    rows.append(tuple(row))
            except sqlite3.Error as ex:
                read_error = str(ex)
        finally:
            con.close()
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)
    return rows, read_error


def _history_rows(raw_rows: list[tuple[Any, ...]], user: str, browser: str,
                  epoch: _dt.datetime) -> tuple[list[dict[str, Any]], int, int]:
    """Convert raw (url, title, visit_count, last_visit) rows one at a time. A row that
    cannot be converted is counted and skipped; a row whose only problem is its timestamp
    is kept with ``timestamp: null`` and counted separately. Nothing is made up: a NULL
    visit count stays null."""
    out: list[dict[str, Any]] = []
    row_errors = 0
    timestamp_errors = 0
    for raw in raw_rows:
        try:
            url, title, visit_count, last_visit = raw
            try:
                timestamp = _utc_iso(_micros_to_dt(epoch, last_visit))
            except ValueError:
                timestamp = None
                timestamp_errors += 1
            out.append({
                "user": user, "browser": browser,
                "url": _as_text(url) or "", "title": _as_text(title) or "",
                "visit_count": int(visit_count) if isinstance(visit_count, (int, float)) else None,
                "timestamp": timestamp,
                "visit_type": None,
            })
        except Exception:
            row_errors += 1
    return out, row_errors, timestamp_errors


@sidecar.method("browser_history")
def browser_history(params: Any) -> dict[str, Any]:
    profile = Path(params["profile_path"])
    browser = params["browser"].lower()
    user = params.get("user", "?")
    if browser in ("chrome", "chromium", "edge", "brave", "opera"):
        db_path = profile / "History"
        query = "SELECT url, title, visit_count, last_visit_time FROM urls ORDER BY last_visit_time DESC"
        epoch = _CHROME_EPOCH   # microseconds since 1601
    elif browser == "firefox":
        db_path = profile / "places.sqlite"
        query = "SELECT url, title, visit_count, last_visit_date FROM moz_places ORDER BY last_visit_date DESC"
        epoch = _UNIX_EPOCH     # microseconds since 1970
    elif browser == "safari":
        # TODO 4.1: parse plist + History.db
        return {"entries": [], "todo": "safari history parsing"}
    else:
        return {"entries": []}
    if not db_path.is_file():
        return {"entries": []}

    raw_rows, read_error = _read_history_db(db_path, query)
    entries, row_errors, timestamp_errors = _history_rows(raw_rows, user, browser, epoch)
    result: dict[str, Any] = {
        "entries": entries,
        "row_errors": row_errors,
        "timestamp_errors": timestamp_errors,
    }
    if read_error is not None:
        result["read_error"] = read_error
    return result


# -------------------- SRUM (libesedb) --------------------

@sidecar.method("srum_applications")
def srum_applications(params: Any) -> dict[str, Any]:
    """SRUDB.dat is an ESE database. Requires libesedb-python; falls back to empty + TODO."""
    try:
        import pyesedb  # type: ignore[import-not-found]
    except Exception:
        return {"entries": [], "todo": "install libesedb-python for SRUM"}
    db = pyesedb.file()
    db.open(params["srudb_path"])
    out = []
    try:
        for tbl in db.tables:
            if "{D10CA2FE-6FCF-4F6D-848E-B2E99266FA89}" not in tbl.name:  # Application Resource Usage
                continue
            for row in tbl.records:
                # ESE column order is fragile; production code maps by name. For the placeholder
                # we just surface row count so the UI shows progress.
                _ = row
                out.append({"user": "?", "application": "?", "timestamp": None,
                            "fg_cpu_ms": 0, "bytes_read": 0, "bytes_written": 0})
            break
    finally:
        db.close()
    return {"entries": out, "todo": "phase-4.1: full SRUM column mapping"}


def main() -> None:
    asyncio.run(sidecar.serve())


if __name__ == "__main__":
    main()
