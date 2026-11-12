"""Cinder embedded scripting host.

Long-running Python sidecar that runs examiner-supplied scripts.

SECURITY: THERE IS NO SANDBOX. A script is compiled and run with ``exec`` in this
process with the full set of builtins. It runs with the examiner's own rights: it
can import any module, read and write any file the examiner can (including evidence
and case files), start processes and use the network. Restricting ``exec``'s globals
or builtins is not a security boundary in CPython and this module does not pretend
otherwise. Only run scripts you trust and have read, exactly as you would any other
program. Isolation, if it is wanted, has to come from the operating system (a
separate low-privilege account, a container or a VM) around the whole sidecar.

The only containment is operational:

* ``print()`` output cannot corrupt the JSON-RPC stream: the protocol layer moves
  stdout to stderr, and ``eval`` captures what a script prints and returns it.
* A script that never returns is ended by the client: ``SidecarClient`` enforces a
  per-call inactivity timeout and kills the sidecar's process tree when it expires.
  Interpreter state (names defined by earlier scripts) is lost when that happens.
* ``sys.exit()`` / ``exit()`` inside a script fails that call; it does not end the host.

Methods:
    eval(script, params?)  -> {result, stdout, globals} | {error, traceback, stdout}
    invoke(name, args?)    -> {result} | {error}   (call a previously defined function)

``eval`` result: the value the script left in ``_``, or else in ``result``; ``None`` when
it set neither. Falsy values (``0``, ``""``, ``False``, ``[]``) are returned as they are.
``_`` and ``result`` are outputs of one call and are not carried over to the next one;
every other top-level name persists between calls.
"""

from __future__ import annotations

import asyncio
import contextlib
import io
import json
import textwrap
import traceback
from typing import Any

from shared.protocol import Sidecar

sidecar = Sidecar()
_globals: dict[str, Any] = {"__name__": "__cinder_script__"}

# Names that carry one call's result and must not leak into the next call.
_RESULT_NAMES = ("_", "result")
_MAX_CAPTURED_STDOUT = 1 << 20  # characters of script print() output returned per call


class _CappedStringIO(io.StringIO):
    """Collects what a script prints, up to a cap, without ever raising."""

    def __init__(self, cap: int) -> None:
        super().__init__()
        self._cap = cap
        self.truncated = False

    def write(self, s: str) -> int:
        room = self._cap - self.tell()
        if room <= 0:
            self.truncated = True
            return len(s)
        if len(s) > room:
            self.truncated = True
            super().write(s[:room])
            return len(s)
        return super().write(s)


def _extract_result(namespace: dict[str, Any]) -> Any:
    """The script's result: ``_`` if the script set it, else ``result``, else None.

    Membership is tested explicitly. ``ns.get("_") or ns.get("result")`` would turn
    every falsy result (0, "", False, []) into None.
    """
    for name in _RESULT_NAMES:
        if name in namespace:
            return namespace[name]
    return None


@sidecar.method("eval")
def eval_script(params: Any) -> dict[str, Any]:
    code = textwrap.dedent((params or {}).get("script", ""))
    p = (params or {}).get("params", {}) or {}
    locals_ns = dict(_globals)
    for name in _RESULT_NAMES:
        locals_ns.pop(name, None)
    locals_ns.update(p)
    captured = _CappedStringIO(_MAX_CAPTURED_STDOUT)

    def _stdout() -> str:
        text = captured.getvalue()
        return text + "\n[output truncated]" if captured.truncated else text

    try:
        compiled = compile(code, "<script>", "exec")
        with contextlib.redirect_stdout(captured):
            exec(compiled, locals_ns)  # noqa: S102 — trusted-script host; see module docstring
        last = _extract_result(locals_ns)
        # Persist any new top-level names so the next call sees them.
        for k, v in locals_ns.items():
            if k in _RESULT_NAMES:
                continue
            if k not in _globals or _globals.get(k) is not v:
                _globals[k] = v
        return {"result": _to_jsonable(last), "stdout": _stdout(), "globals": list(_globals.keys())}
    except SystemExit as ex:
        return {"error": f"script called exit({ex.code!r})", "traceback": "", "stdout": _stdout()}
    except Exception as ex:
        return {"error": str(ex), "traceback": traceback.format_exc(), "stdout": _stdout()}


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
    except SystemExit as ex:
        return {"error": f"'{name}' called exit({ex.code!r})"}
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
