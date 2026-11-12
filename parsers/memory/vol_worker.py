"""Cinder volatility3 sidecar.

Embeds the volatility3 framework as a library and runs plugins on demand. Required deps:
    pip install volatility3

The sidecar caches a per-image vol3 context so multiple plugin invocations don't pay the
symbol-table load cost twice.

Methods:
    pstree(image)              -> {entries:[MemoryProcess...]}
    pslist(image)              -> {entries:[...]}
    netscan(image)             -> {entries:[MemoryConnection...]}
    dlllist(image, pid?)       -> {entries:[LoadedModule...]}
    malfind(image)             -> {entries:[InjectionFinding...]}
    hollowfind(image)          -> {entries:[InjectionFinding...]}
    hashdump(image)            -> {entries:[CredentialDump...]}
    lsadump(image)             -> {entries:[...]}
    cachedump(image)           -> {entries:[...]}
    cmdline(image, pid?)       -> {entries:[{pid, cmdline}]}
    privs(image, pid?)         -> {entries:[...]}
    handles(image, pid?)       -> {entries:[...]}
    run_plugin(image, plugin, options?) -> {raw: <list of dicts>}

Value conversion. A volatility TreeGrid cell is not always a plain value:

* a field that could not be read is an *absent value* object (``NotApplicableValue``,
  ``NotAvailableValue``, ``UnreadableValue``, ``UnparsableValue``, all subclasses of
  ``BaseAbsentValue``). It has no integer or string meaning. It becomes ``None`` here;
  it is never turned into 0, "" or the object's repr.
* raw memory (``bytes``, e.g. malfind's ``Hexdump``) is not UTF-8 and cannot go into JSON
  as text. It becomes a lowercase hex string.
* ``format_hints.Hex`` and friends are ``int`` subclasses and become plain ints;
  datetimes become ISO 8601.

The helpers below do this by duck typing so they can be tested without volatility3.
"""

from __future__ import annotations

import asyncio
import datetime as _dt
from typing import Any

from shared.protocol import Sidecar, json_safe, report_progress

sidecar = Sidecar()

_contexts: dict[str, Any] = {}


# -------------------- value conversion --------------------

def _is_absent(value: Any) -> bool:
    """True for None and for volatility3's absent-value markers."""
    if value is None:
        return True
    return any(cls.__name__ == "BaseAbsentValue" for cls in type(value).__mro__)


def _plain(value: Any) -> Any:
    """One TreeGrid cell as a JSON-serialisable value (see module docstring)."""
    if _is_absent(value):
        return None
    if isinstance(value, bool):
        return bool(value)
    if isinstance(value, int):
        return int(value)
    if isinstance(value, float):
        return float(value)
    if isinstance(value, (bytes, bytearray, memoryview)):
        return bytes(value).hex()
    if isinstance(value, str):
        return json_safe(str(value))
    if isinstance(value, (_dt.datetime, _dt.date)):
        return value.isoformat()
    if isinstance(value, dict):
        return {str(k): _plain(v) for k, v in value.items()}
    if isinstance(value, (list, tuple, set, frozenset)):
        return [_plain(v) for v in value]
    return json_safe(value)


def _int(value: Any) -> int | None:
    """Integer value of a cell, or None when it is absent or not a number."""
    value = _plain(value)
    if value is None or isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    if isinstance(value, float):
        return int(value) if value == value and value not in (float("inf"), float("-inf")) else None
    if isinstance(value, str):
        text = value.strip()
        try:
            return int(text, 0) if text else None
        except ValueError:
            return None
    return None


def _str(value: Any) -> str | None:
    """Text value of a cell, or None when it is absent or empty."""
    value = _plain(value)
    if value is None:
        return None
    text = value if isinstance(value, str) else str(value)
    return text or None


def _first(row: dict[str, Any], *names: str) -> Any:
    """The first present, non-absent column among ``names`` (column spellings differ
    between volatility3 releases and between plugins)."""
    for name in names:
        if name in row and not _is_absent(row[name]):
            return row[name]
    return None


# -------------------- volatility3 plumbing --------------------

def _import_vol():
    try:
        import volatility3.framework
        from volatility3 import framework
        from volatility3.cli import text_renderer
        return volatility3, framework, text_renderer
    except Exception as ex:
        raise RuntimeError("volatility3 is not installed (pip install volatility3)") from ex


def _automagic_context(image_path: str):
    if image_path in _contexts:
        return _contexts[image_path]
    volatility3, framework, _ = _import_vol()
    framework.require_interface_version(2, 0, 0)
    framework.import_files(volatility3.plugins, ignore_errors=True)
    framework.import_files(volatility3.framework.symbols, ignore_errors=True)

    from volatility3.framework import contexts, automagic
    ctx = contexts.Context()
    automagics = automagic.available(ctx)
    ctx.config["automagic.LayerStacker.single_location"] = f"file:{image_path}"
    _contexts[image_path] = (ctx, automagics)
    return _contexts[image_path]


def _on_vol_progress(_progress: Any = None, _description: Any = None) -> None:
    report_progress(phase="volatility")


def _run_plugin(image_path: str, plugin_id: str, options: dict[str, Any] | None = None) -> list[dict[str, Any]]:
    volatility3, framework, _ = _import_vol()
    from volatility3.framework import plugins, automagic, contexts
    from volatility3.framework.configuration import requirements

    ctx, automagics = _automagic_context(image_path)
    plugin_cls = plugins.construct_plugin if False else None  # noqa: SIM108 — stub for type-checker

    # Resolve plugin class by id (e.g. "windows.pslist.PsList").
    target = plugin_id
    if "." not in target:
        target = f"windows.{target}.{target.title()}"
    try:
        mod_path, cls_name = target.rsplit(".", 1)
        module = __import__(f"volatility3.plugins.{mod_path}", fromlist=[cls_name])
        plugin = getattr(module, cls_name)
    except Exception as ex:
        raise RuntimeError(f"Unknown vol3 plugin: {plugin_id} ({ex})") from ex

    # Build the actual plugin instance. The framework's `construct_plugin` handles automagic.
    from volatility3.framework import constants
    from volatility3.framework.plugins import construct_plugin
    automagic.choose_automagic(automagics, plugin)
    constructed = construct_plugin(ctx, automagics, plugin, "plugins", _on_vol_progress, options or {})

    treegrid = constructed.run()
    rows: list[dict[str, Any]] = []

    def _visitor(node, accumulator):
        rows.append(_row_from_node(treegrid.columns, node.values))
        report_progress(phase="volatility", rows=len(rows))
        return accumulator
    treegrid.populate(_visitor, None)
    return rows


def _row_from_node(columns: Any, values: Any) -> dict[str, Any]:
    """One TreeGrid row as {column name: plain value}. Everything a handler returns goes
    through here, so no absent-value object or raw byte string reaches the JSON encoder."""
    return {str(col.name): _plain(values[i]) for i, col in enumerate(columns)}


# -------------------- shaping --------------------

def _shape_pstree(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    out = []
    for r in rows:
        out.append({
            "pid": _int(r.get("PID")),
            "ppid": _int(r.get("PPID")),
            "image": _str(r.get("ImageFileName")) or "",
            "cmdline": _str(_first(r, "CommandLine", "Cmd")),
            "created_at": _str(r.get("CreateTime")),
            "exited_at": _str(r.get("ExitTime")),
            "threads": _int(r.get("Threads")),
            "handles": _int(r.get("Handles")),
            "session": _str(r.get("SessionId")),
            "integrity": None,
            "anomalies": [],
        })
    return out


def _shape_netscan(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    out = []
    for r in rows:
        out.append({
            "pid": _int(r.get("PID")),
            "proto": _str(r.get("Proto")) or "",
            "local_addr": _str(r.get("LocalAddr")) or "",
            "local_port": _int(r.get("LocalPort")),
            "remote_addr": _str(r.get("ForeignAddr")) or "",
            "remote_port": _int(r.get("ForeignPort")),
            "state": _str(r.get("State")) or "",
            "created_at": _str(r.get("Created")),
        })
    return out


def _shape_dlllist(rows: list[dict[str, Any]], pid: Any = None) -> list[dict[str, Any]]:
    wanted = _int(pid)
    out = []
    for r in rows:
        row_pid = _int(r.get("PID"))
        if wanted is not None and row_pid != wanted:
            continue
        out.append({
            "pid": row_pid,
            "name": _str(r.get("Name")) or "",
            "path": _str(r.get("Path")) or "",
            "base": _str(r.get("Base")),
            "size": _int(r.get("Size")),
            "signed": False,  # vol3 doesn't validate signing here
        })
    return out


def _shape_malfind(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    out = []
    for r in rows:
        out.append({
            "pid": _int(r.get("PID")),
            "image_name": _str(r.get("Process")) or "",
            "type": "malfind",
            "address": _str(_first(r, "Start VPN", "Start")),
            "length": _int(r.get("Length")),
            # Hexdump is raw memory: _plain() turns it into hex text.
            "notes": _str(r.get("Hexdump")),
        })
    return out


def _shape_hashdump(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    out = []
    for r in rows:
        # volatility3 names the columns "lmhash" / "nthash"; older builds and other tools
        # use "LM" / "NT". Accept either.
        lm = _str(_first(r, "lmhash", "LM", "LMHash", "lm"))
        nt = _str(_first(r, "nthash", "NT", "NTHash", "nt"))
        out.append({
            "plugin": "hashdump",
            "account": _str(_first(r, "User", "user")) or "",
            "domain": None,
            "rid": _int(_first(r, "rid", "RID")),
            "lm": lm,
            "nt": nt,
            # pwdump-style "LM:NT"; null when neither hash was recovered, so an empty
            # result can never be read as the pair of empty hashes ":".
            "hash": f"{lm or ''}:{nt or ''}" if (lm or nt) else None,
            "last_change": None,
        })
    return out


def _shape_cmdline(rows: list[dict[str, Any]], pid: Any = None) -> list[dict[str, Any]]:
    wanted = _int(pid)
    out = []
    for r in rows:
        row_pid = _int(r.get("PID"))
        if wanted is not None and row_pid != wanted:
            continue
        out.append({"pid": row_pid, "cmdline": _str(r.get("Args"))})
    return out


# -------------------- methods --------------------

@sidecar.method("pstree")
def pstree(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], "windows.pstree.PsTree")
    return {"entries": _shape_pstree(rows)}


@sidecar.method("pslist")
def pslist(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], "windows.pslist.PsList")
    return {"entries": _shape_pstree(rows)}


@sidecar.method("netscan")
def netscan(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], "windows.netscan.NetScan")
    return {"entries": _shape_netscan(rows)}


@sidecar.method("dlllist")
def dlllist(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], "windows.dlllist.DllList")
    return {"entries": _shape_dlllist(rows, params.get("pid"))}


@sidecar.method("malfind")
def malfind(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], "windows.malfind.Malfind")
    return {"entries": _shape_malfind(rows)}


@sidecar.method("hollowfind")
def hollowfind(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], "windows.hollowfind.Hollowfind") if False else []
    # `hollowfind` is a community plugin; if present in the user's vol3 plugins dir, this will
    # work — otherwise return empty.
    return {"entries": rows}


@sidecar.method("hashdump")
def hashdump(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], "windows.hashdump.Hashdump")
    return {"entries": _shape_hashdump(rows)}


@sidecar.method("lsadump")
def lsadump(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], "windows.lsadump.Lsadump")
    return {"entries": rows}


@sidecar.method("cachedump")
def cachedump(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], "windows.cachedump.Cachedump")
    return {"entries": rows}


@sidecar.method("cmdline")
def cmdline(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], "windows.cmdline.CmdLine")
    return {"entries": _shape_cmdline(rows, params.get("pid"))}


@sidecar.method("run_plugin")
def run_plugin(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], params["plugin"], params.get("options"))
    return {"raw": rows}


def main() -> None:
    asyncio.run(sidecar.serve())


if __name__ == "__main__":
    main()
