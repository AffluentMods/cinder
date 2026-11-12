"""JSON-RPC 2.0 over newline-delimited JSON on stdio.

Sidecars are stateless workers: read one JSON object per line on stdin, write
one per line on stdout. Logs go to stderr (Cinder collects them as DEBUG).

Wire rules (the C# ``SidecarClient`` relies on every one of them):

* Both directions are UTF-8 without a BOM, whatever the console code page is.
  ``serve()`` does not trust the interpreter's default stream encoding (cp1252
  on a piped Windows stdout): it reads and writes bytes itself.
* Only protocol frames ever reach the real stdout. ``serve()`` keeps a private
  duplicate of file descriptor 1 for the frames and then points both fd 1 and
  ``sys.stdout`` at stderr, so a stray ``print()`` in a parser, in a user
  script, or in a C library cannot corrupt the stream.
* A request that cannot be parsed is answered with an error whose ``id`` is
  absent. The client treats that as "some request was lost" and fails every
  pending call rather than waiting forever.
* A long-running handler calls :func:`report_progress` from its work loop. That
  emits a ``$/progress`` notification (no ``id``), which the client counts as a
  sign of life when it enforces its per-call inactivity timeout.
"""

from __future__ import annotations

import asyncio
import datetime as _dt
import json
import math
import os
import sys
import threading
import time
import traceback
from collections.abc import Awaitable, Callable
from typing import Any, BinaryIO

from pydantic import BaseModel

JsonValue = dict[str, Any] | list[Any] | str | int | float | bool | None
Handler = Callable[[JsonValue], Awaitable[JsonValue] | JsonValue]

PROGRESS_METHOD = "$/progress"
_PROGRESS_MIN_INTERVAL = 1.0  # seconds between two progress notifications

_write_lock = threading.Lock()
_protocol_out: BinaryIO | None = None
_current_request_id: int | None = None
_last_progress = 0.0


class JsonRpcRequest(BaseModel):
    jsonrpc: str = "2.0"
    id: int
    method: str
    params: JsonValue = None


class JsonRpcError(BaseModel):
    code: int
    message: str
    data: JsonValue = None


class JsonRpcResponse(BaseModel):
    jsonrpc: str = "2.0"
    id: int | None = None
    result: JsonValue = None
    error: JsonRpcError | None = None


def json_safe(value: Any, _depth: int = 0) -> Any:
    """Return a copy of ``value`` that always serialises to valid UTF-8 JSON.

    Evidence is hostile: file names carry lone surrogates, database columns carry
    arbitrary bytes, plugins return their own value types. Bytes become a hex
    string, lone surrogates become U+FFFD, NaN/Infinity become null and anything
    unknown becomes its ``str()``. Nothing here raises.
    """
    if value is None or isinstance(value, bool):
        return value
    if isinstance(value, int):
        return int(value)
    if isinstance(value, float):
        return float(value) if math.isfinite(value) else None
    if isinstance(value, str):
        try:
            value.encode("utf-8")
            return str(value)
        except UnicodeEncodeError:
            return value.encode("utf-16", "surrogatepass").decode("utf-16", "replace")
    if isinstance(value, (bytes, bytearray, memoryview)):
        return bytes(value).hex()
    if isinstance(value, (_dt.datetime, _dt.date, _dt.time)):
        return value.isoformat()
    if _depth > 64:
        return "<nesting too deep>"
    if isinstance(value, dict):
        return {str(json_safe(k, _depth + 1)): json_safe(v, _depth + 1) for k, v in value.items()}
    if isinstance(value, (list, tuple, set, frozenset)):
        return [json_safe(v, _depth + 1) for v in value]
    try:
        return json_safe(str(value), _depth + 1)
    except Exception:  # noqa: BLE001 — a hostile __str__ must not take the sidecar down
        return f"<unprintable {type(value).__name__}>"


def _encode(response: JsonRpcResponse) -> bytes:
    try:
        return response.model_dump_json(exclude_none=True).encode("utf-8")
    except Exception:  # noqa: BLE001 — bytes / lone surrogates / foreign types in the result
        payload: dict[str, Any] = {"jsonrpc": "2.0"}
        if response.id is not None:
            payload["id"] = response.id
        if response.error is not None:
            payload["error"] = json_safe(response.error.model_dump(exclude_none=True))
        elif response.result is not None:
            payload["result"] = json_safe(response.result)
        return json.dumps(payload, ensure_ascii=False, allow_nan=False).encode("utf-8")


def _take_over_stdio() -> BinaryIO:
    """Reserve the real stdout for protocol frames and return the stream to write them to."""
    global _protocol_out
    if _protocol_out is not None:
        return _protocol_out

    reconfigure = getattr(sys.stderr, "reconfigure", None)
    if reconfigure is not None:
        try:
            reconfigure(encoding="utf-8", errors="backslashreplace")
        except Exception:  # noqa: BLE001 — a replaced stderr that cannot be reconfigured
            pass

    out: BinaryIO | None = None
    try:
        sys.stdout.flush()
        private_fd = os.dup(1)
        if sys.platform == "win32":
            import msvcrt

            msvcrt.setmode(private_fd, os.O_BINARY)
        # Anything that still writes to fd 1 (C libraries, child processes, print) lands
        # on stderr from here on.
        os.dup2(2, 1)
        out = os.fdopen(private_fd, "wb", buffering=0)
    except Exception:  # noqa: BLE001 — no real fd 1 (embedded / replaced stdout): use what there is
        out = getattr(sys.stdout, "buffer", None)
        if out is None:
            raise
    sys.stdout = sys.stderr
    _protocol_out = out
    return out


def _write_frame(frame: bytes) -> None:
    out = _protocol_out
    if out is None:
        return
    data = frame + b"\n"
    with _write_lock:
        view = memoryview(data)
        while view:
            written = out.write(view)
            if written is None:
                break
            view = view[written:]
        out.flush()


def report_progress(**fields: Any) -> None:
    """Tell the client that the current call is still making progress.

    Call it from the work loop of anything that can run for a long time (imaging,
    hashing, walking a filesystem). It is rate limited, costs one clock read when it
    does nothing, and is a no-op outside ``serve()`` (unit tests, direct calls). It is
    deliberately *not* driven by a background thread: a handler that hangs stops
    reporting, and the client's inactivity timeout then terminates the sidecar.
    """
    global _last_progress
    if _protocol_out is None:
        return
    now = time.monotonic()
    if now - _last_progress < _PROGRESS_MIN_INTERVAL:
        return
    _last_progress = now
    params: dict[str, Any] = {"id": _current_request_id}
    params.update(fields)
    try:
        frame = json.dumps(
            {"jsonrpc": "2.0", "method": PROGRESS_METHOD, "params": json_safe(params)},
            ensure_ascii=False,
            allow_nan=False,
        ).encode("utf-8")
        _write_frame(frame)
    except Exception:  # noqa: BLE001 — progress is advisory; never fail the job over it
        pass


def _salvage_id(raw: bytes) -> int | None:
    """Best effort: the id of a request that is JSON but not a valid request."""
    try:
        obj = json.loads(raw)
    except Exception:  # noqa: BLE001
        return None
    if isinstance(obj, dict):
        candidate = obj.get("id")
        if isinstance(candidate, int) and not isinstance(candidate, bool):
            return candidate
    return None


class Sidecar:
    """A trivial dispatch loop. Subclass-free; register handlers by method name."""

    def __init__(self) -> None:
        self._handlers: dict[str, Handler] = {}

    def method(self, name: str) -> Callable[[Handler], Handler]:
        def decorator(fn: Handler) -> Handler:
            self._handlers[name] = fn
            return fn

        return decorator

    async def handle_line(self, raw: bytes | str) -> JsonRpcResponse | None:
        """Turn one request line into its response (``None`` for a blank line)."""
        global _current_request_id
        if isinstance(raw, str):
            raw = raw.encode("utf-8", "surrogatepass")
        if raw.startswith(b"\xef\xbb\xbf"):
            raw = raw[3:]
        raw = raw.strip()
        if not raw:
            return None
        try:
            request = JsonRpcRequest.model_validate_json(raw)
        except Exception as ex:  # noqa: BLE001 — not JSON, not UTF-8, or not a request
            salvaged = _salvage_id(raw)
            return JsonRpcResponse(
                id=salvaged,
                error=JsonRpcError(
                    code=-32700 if salvaged is None else -32600,
                    message=f"Parse error: {ex}" if salvaged is None else f"Invalid request: {ex}",
                ),
            )

        handler = self._handlers.get(request.method)
        if handler is None:
            return JsonRpcResponse(
                id=request.id,
                error=JsonRpcError(code=-32601, message=f"Method not found: {request.method}"),
            )

        _current_request_id = request.id
        try:
            value = handler(request.params)
            if hasattr(value, "__await__"):
                value = await value  # type: ignore[assignment]
            return JsonRpcResponse(id=request.id, result=value)
        except (KeyboardInterrupt, asyncio.CancelledError):
            raise
        except BaseException as ex:  # noqa: BLE001 — handler errors are returned as JSON-RPC errors
            # BaseException on purpose: a parser or a user script calling sys.exit() must
            # fail its own call, not silently end the sidecar under every other caller.
            try:
                traceback.print_exc(file=sys.stderr)
            except Exception:  # noqa: BLE001
                pass
            return JsonRpcResponse(
                id=request.id,
                error=JsonRpcError(
                    code=-32000,
                    message=str(ex) or type(ex).__name__,
                    data={"type": type(ex).__name__},
                ),
            )
        finally:
            _current_request_id = None

    async def serve(self) -> None:
        _take_over_stdio()
        stdin = getattr(sys.stdin, "buffer", sys.stdin)
        loop = asyncio.get_running_loop()
        while True:
            line = await loop.run_in_executor(None, stdin.readline)
            if not line:
                return
            response = await self.handle_line(line)
            if response is not None:
                self._write(response)

    @staticmethod
    def _write(response: JsonRpcResponse) -> None:
        try:
            frame = _encode(response)
        except Exception as ex:  # noqa: BLE001 — never leave the caller without an answer
            frame = json.dumps(
                {
                    "jsonrpc": "2.0",
                    "id": response.id,
                    "error": {"code": -32603, "message": f"Result could not be serialised: {ex!r}"},
                }
            ).encode("utf-8")
        _write_frame(frame)


__all__ = [
    "PROGRESS_METHOD",
    "JsonRpcError",
    "JsonRpcRequest",
    "JsonRpcResponse",
    "Sidecar",
    "json_safe",
    "report_progress",
]
