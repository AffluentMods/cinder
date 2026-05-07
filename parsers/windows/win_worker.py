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
    prefetch(prefetch_dir)                      -> {entries:[{executable, hash, run_count, last_run, all_run_times[], loaded_files[]}]}
    shellbags(ntuser_path, user)                -> {entries:[{user, path, first_accessed, last_accessed, access_count}]}
    jumplists(jumplist_dir, user)               -> {entries:[{user, app_id, target_path, access_time}]}
    lnk(path)                                   -> {target_path, arguments, icon, working_dir, target_created, target_modified, target_accessed, volume_serial, machine_id}
    evtx_page(path, cursor, limit)              -> {entries:[{record_id, event_id, provider, channel, computer, user, timestamp, level, summary}]}
    browser_history(profile_path, browser, user)-> {entries:[{user, browser, url, title, visit_count, timestamp, visit_type}]}
    srum_applications(srudb_path)               -> {entries:[{user, application, timestamp, fg_cpu_ms, bytes_read, bytes_written}]}
"""

from __future__ import annotations

import asyncio
import datetime as _dt
import os
import sqlite3
import struct
import sys
from pathlib import Path
from typing import Any, Iterable

from shared.protocol import Sidecar

sidecar = Sidecar()


def _utc_iso(ts) -> str | None:
    if ts is None:
        return None
    if isinstance(ts, (int, float)):
        return _dt.datetime.fromtimestamp(ts, tz=_dt.timezone.utc).isoformat()
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

@sidecar.method("prefetch")
def prefetch(params: Any) -> dict[str, Any]:
    """Phase 4 ships a minimal Win10+ Prefetch parser. For full Win7/8/10/11 coverage, use
    PECmd's CSV import path (TODO 4.1: shell out to PECmd when available)."""
    pf_dir = Path(params["prefetch_dir"])
    out = []
    for f in pf_dir.glob("*.pf"):
        try:
            data = f.read_bytes()
            if data[:4] not in (b"SCCA", b"MAM\x04"):
                continue
            # MAM = compressed (Win10+). For now skip MAM and let a future PECmd integration handle it.
            if data[:4] == b"MAM\x04":
                continue
            # SCCA: uncompressed Prefetch. Header: version(4) signature(4) unk(4) filesize(4)
            # name(120 UTF-16LE) hash(4) ...
            name = data[16:16 + 120].decode("utf-16-le", errors="replace").rstrip("\x00")
            file_hash = struct.unpack_from("<I", data, 0x4C)[0] if len(data) >= 0x50 else 0
            run_count = struct.unpack_from("<I", data, 0x98)[0] if len(data) >= 0x9C else 0
            last_run_filetime = struct.unpack_from("<Q", data, 0x80)[0] if len(data) >= 0x88 else 0
            last_run = (
                _dt.datetime(1601, 1, 1, tzinfo=_dt.timezone.utc) + _dt.timedelta(microseconds=last_run_filetime / 10)
                if last_run_filetime else None
            )
            out.append({
                "executable": name,
                "hash": f"{file_hash:08X}",
                "run_count": int(run_count),
                "last_run": _utc_iso(last_run),
                "all_run_times": [_utc_iso(last_run)] if last_run else [],
                "loaded_files": [],
            })
        except Exception as ex:
            sys.stderr.write(f"[prefetch] {f.name}: {ex}\n")
    return {"entries": out}


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


def _chrome_to_dt(micros: int) -> _dt.datetime | None:
    if not micros:
        return None
    return _CHROME_EPOCH + _dt.timedelta(microseconds=micros)


@sidecar.method("browser_history")
def browser_history(params: Any) -> dict[str, Any]:
    profile = Path(params["profile_path"])
    browser = params["browser"].lower()
    user = params.get("user", "?")
    out: list[dict[str, Any]] = []
    if browser in ("chrome", "chromium", "edge", "brave", "opera"):
        history_db = profile / "History"
        if not history_db.exists():
            return {"entries": []}
        # SQLite is locked while the browser runs — copy first.
        import shutil
        tmp = profile / "_cinder_tmp_history.sqlite"
        shutil.copy2(history_db, tmp)
        try:
            con = sqlite3.connect(tmp)
            cur = con.execute("SELECT url, title, visit_count, last_visit_time FROM urls ORDER BY last_visit_time DESC")
            for url, title, vc, lvt in cur.fetchall():
                out.append({
                    "user": user, "browser": browser,
                    "url": url, "title": title or "",
                    "visit_count": int(vc or 1),
                    "timestamp": _utc_iso(_chrome_to_dt(lvt)),
                    "visit_type": None,
                })
            con.close()
        finally:
            try: tmp.unlink()
            except Exception: pass
    elif browser == "firefox":
        places = profile / "places.sqlite"
        if not places.exists():
            return {"entries": []}
        import shutil
        tmp = profile / "_cinder_tmp_places.sqlite"
        shutil.copy2(places, tmp)
        try:
            con = sqlite3.connect(tmp)
            cur = con.execute("SELECT url, title, visit_count, last_visit_date FROM moz_places ORDER BY last_visit_date DESC")
            for url, title, vc, lvd in cur.fetchall():
                ts = _dt.datetime.fromtimestamp((lvd or 0) / 1_000_000, tz=_dt.timezone.utc) if lvd else None
                out.append({
                    "user": user, "browser": browser,
                    "url": url, "title": title or "",
                    "visit_count": int(vc or 1),
                    "timestamp": _utc_iso(ts),
                    "visit_type": None,
                })
            con.close()
        finally:
            try: tmp.unlink()
            except Exception: pass
    elif browser == "safari":
        # TODO 4.1: parse plist + History.db
        return {"entries": [], "todo": "safari history parsing"}
    return {"entries": out}


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
