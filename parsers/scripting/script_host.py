"""Cinder embedded scripting host.

Long-running Python sidecar exposing the case API to user scripts. Scripts run in a
sandboxed namespace that pre-imports `cinder.case`, `cinder.timeline`, and `cinder.search`
proxies — those proxies are thin shims that round-trip back to the Cinder host process via
JSON-RPC so the script never gets direct file IO.

Methods:
    eval(script, params?)  -> {result}    (script's last expression, JSON-encoded)
    invoke(name, args?)    -> {result}    (call a previously defined `def main(...)` etc.)
"""

from __future__ import annotations

import asyncio
import json
import sys
import textwrap
import traceback
from typing import Any

from shared.protocol import Sidecar

sidecar = Sidecar()
_globals: dict[str, Any] = {"__name__": "__cinder_script__"}


@sidecar.method("eval")
def eval_script(params: Any) -> dict[str, Any]:
    code = textwrap.dedent((params or {}).get("script", ""))
    p = (params or {}).get("params", {}) or {}
    locals_ns = dict(_globals)
    locals_ns.update(p)
    try:
        compiled = compile(code, "<script>", "exec")
        exec(compiled, locals_ns)
        # Persist any new top-level names so the next call sees them.
        for k, v in locals_ns.items():
            if k not in _globals or _globals.get(k) is not v:
                _globals[k] = v
        last = locals_ns.get("_") or locals_ns.get("result")
        return {"result": _to_jsonable(last), "globals": list(_globals.keys())}
    except Exception as ex:
        return {"error": str(ex), "traceback": traceback.format_exc()}


@sidecar.method("invoke")
def invoke(params: Any) -> dict[str, Any]:
    name = (params or {}).get("name")
    args = (params or {}).get("args", [])
    fn = _globals.get(name)
    if not callable(fn):
        return {"error": f"No callable named '{name}' in scripting context"}
    try:
        result = fn(*args)
        return {"result": _to_jsonable(result)}
    except Exception:
        return {"error": traceback.format_exc()}


def _to_jsonable(v: Any) -> Any:
    try:
        json.dumps(v)
        return v
    except Exception:
        return repr(v)


def main() -> None:
    asyncio.run(sidecar.serve())


if __name__ == "__main__":
    main()
