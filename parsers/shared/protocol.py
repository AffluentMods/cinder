"""JSON-RPC 2.0 over newline-delimited JSON on stdio.

Sidecars are stateless workers: read one JSON object per line on stdin, write
one per line on stdout. Logs go to stderr (Cinder collects them as DEBUG).
"""

from __future__ import annotations

import json
import sys
from collections.abc import Awaitable, Callable
from typing import Any

from pydantic import BaseModel, Field

JsonValue = dict[str, Any] | list[Any] | str | int | float | bool | None
Handler = Callable[[JsonValue], Awaitable[JsonValue] | JsonValue]


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


class Sidecar:
    """A trivial dispatch loop. Subclass-free; register handlers by method name."""

    def __init__(self) -> None:
        self._handlers: dict[str, Handler] = {}

    def method(self, name: str) -> Callable[[Handler], Handler]:
        def decorator(fn: Handler) -> Handler:
            self._handlers[name] = fn
            return fn

        return decorator

    async def serve(self) -> None:
        import asyncio

        loop = asyncio.get_running_loop()
        while True:
            line = await loop.run_in_executor(None, sys.stdin.readline)
            if not line:
                return
            line = line.strip()
            if not line:
                continue
            try:
                request = JsonRpcRequest.model_validate_json(line)
            except Exception as ex:  # noqa: BLE001 — JSON parse error
                self._write(
                    JsonRpcResponse(
                        id=None,
                        error=JsonRpcError(code=-32700, message=f"Parse error: {ex}"),
                    )
                )
                continue

            handler = self._handlers.get(request.method)
            if handler is None:
                self._write(
                    JsonRpcResponse(
                        id=request.id,
                        error=JsonRpcError(
                            code=-32601, message=f"Method not found: {request.method}"
                        ),
                    )
                )
                continue

            try:
                value = handler(request.params)
                if hasattr(value, "__await__"):
                    value = await value  # type: ignore[assignment]
                self._write(JsonRpcResponse(id=request.id, result=value))
            except Exception as ex:  # noqa: BLE001 — handler errors are returned as JSON-RPC errors
                self._write(
                    JsonRpcResponse(
                        id=request.id,
                        error=JsonRpcError(code=-32000, message=str(ex)),
                    )
                )

    @staticmethod
    def _write(response: JsonRpcResponse) -> None:
        sys.stdout.write(response.model_dump_json(exclude_none=True) + "\n")
        sys.stdout.flush()


__all__ = ["JsonRpcError", "JsonRpcRequest", "JsonRpcResponse", "Sidecar"]
