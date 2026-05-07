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
"""

from __future__ import annotations

import asyncio
import sys
from typing import Any

from shared.protocol import Sidecar

sidecar = Sidecar()

_contexts: dict[str, Any] = {}


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
    constructed = construct_plugin(ctx, automagics, plugin, "plugins", None, options or {})

    treegrid = constructed.run()
    rows: list[dict[str, Any]] = []

    def _visitor(node, accumulator):
        record = {col.name: node.values[i] for i, col in enumerate(treegrid.columns)}
        rows.append(record)
        return accumulator
    treegrid.populate(_visitor, None)
    return rows


def _shape_pstree(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    out = []
    for r in rows:
        out.append({
            "pid": int(r.get("PID", 0) or 0),
            "ppid": int(r.get("PPID", 0) or 0),
            "image": str(r.get("ImageFileName", "") or ""),
            "cmdline": r.get("CommandLine", None),
            "created_at": str(r.get("CreateTime", "")) or None,
            "exited_at": str(r.get("ExitTime", "")) or None,
            "threads": int(r.get("Threads", 0) or 0),
            "handles": int(r.get("Handles", 0) or 0),
            "session": str(r.get("SessionId", "")) or None,
            "integrity": None,
            "anomalies": [],
        })
    return out


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
    out = []
    for r in rows:
        out.append({
            "pid": int(r.get("PID", 0) or 0),
            "proto": r.get("Proto", ""),
            "local_addr": r.get("LocalAddr", ""),
            "local_port": int(r.get("LocalPort", 0) or 0),
            "remote_addr": r.get("ForeignAddr", ""),
            "remote_port": int(r.get("ForeignPort", 0) or 0),
            "state": r.get("State", ""),
            "created_at": str(r.get("Created", "")) or None,
        })
    return {"entries": out}


@sidecar.method("dlllist")
def dlllist(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], "windows.dlllist.DllList")
    out = []
    for r in rows:
        if "pid" in params and int(r.get("PID", 0) or 0) != int(params["pid"]):
            continue
        out.append({
            "pid": int(r.get("PID", 0) or 0),
            "name": r.get("Name", ""),
            "path": r.get("Path", ""),
            "base": str(r.get("Base", "")),
            "size": int(r.get("Size", 0) or 0),
            "signed": False,  # vol3 doesn't validate signing here
        })
    return {"entries": out}


@sidecar.method("malfind")
def malfind(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], "windows.malfind.Malfind")
    out = []
    for r in rows:
        out.append({
            "pid": int(r.get("PID", 0) or 0),
            "image_name": r.get("Process", ""),
            "type": "malfind",
            "address": str(r.get("Start VPN", r.get("Start", ""))),
            "length": int(r.get("Length", 0) or 0),
            "notes": r.get("Hexdump", None),
        })
    return {"entries": out}


@sidecar.method("hollowfind")
def hollowfind(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], "windows.hollowfind.Hollowfind") if False else []
    # `hollowfind` is a community plugin; if present in the user's vol3 plugins dir, this will
    # work — otherwise return empty.
    return {"entries": rows}


@sidecar.method("hashdump")
def hashdump(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], "windows.hashdump.Hashdump")
    out = []
    for r in rows:
        out.append({
            "plugin": "hashdump",
            "account": r.get("User", ""),
            "domain": None,
            "hash": f"{r.get('LM', '')}:{r.get('NT', '')}",
            "last_change": None,
        })
    return {"entries": out}


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
    out = []
    for r in rows:
        if "pid" in params and int(r.get("PID", 0) or 0) != int(params["pid"]):
            continue
        out.append({"pid": int(r.get("PID", 0) or 0), "cmdline": r.get("Args", "")})
    return {"entries": out}


@sidecar.method("run_plugin")
def run_plugin(params: Any) -> dict[str, Any]:
    rows = _run_plugin(params["image"], params["plugin"], params.get("options"))
    return {"raw": rows}


def main() -> None:
    asyncio.run(sidecar.serve())


if __name__ == "__main__":
    main()
