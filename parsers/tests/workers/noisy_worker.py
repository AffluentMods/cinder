"""A worker that misbehaves in every way a parser, a C library or a user script can."""

from __future__ import annotations

import asyncio
import os
import subprocess
import sys
import time
from typing import Any

from shared import protocol
from shared.protocol import Sidecar, report_progress

sidecar = Sidecar()


@sidecar.method("echo")
def echo(params: Any) -> Any:
    return params


@sidecar.method("unicode_literal")
def unicode_literal(params: Any) -> str:
    # Text that originates inside the worker (as a file name or a database value does):
    # Japanese, Russian, an emoji, accented Latin. Escaped so this file's own encoding
    # plays no part.
    return "日本語 Привет \U0001f600 café"


@sidecar.method("codepoints")
def codepoints(params: Any) -> list[int]:
    # What the handler really received, immune to a matching mis-encoding on the way out.
    return [ord(c) for c in params]


@sidecar.method("print_stdout")
def print_stdout(params: Any) -> dict[str, Any]:
    print("this is a stray print with non-ASCII: 日本語 ✓")
    sys.stdout.write('{"jsonrpc":"2.0","id":1,"result":"forged by print"}\n')
    sys.stdout.flush()
    return {"ok": True}


@sidecar.method("write_fd1")
def write_fd1(params: Any) -> dict[str, Any]:
    os.write(1, b"raw bytes written to fd 1 by a C library \xff\xfe\n")
    return {"ok": True}


@sidecar.method("child_stdout")
def child_stdout(params: Any) -> dict[str, Any]:
    # A child process inherits fd 1. It must inherit the redirected one.
    subprocess.run([sys.executable, "-c", "print('child process output')"], check=True)
    return {"ok": True}


@sidecar.method("hostile_result")
def hostile_result(params: Any) -> dict[str, Any]:
    return {
        "raw": b"\xff\xfe\x00binary",
        "lone_surrogate": "name-\udcff-end",
        "nan": float("nan"),
        "nested": [{"blob": bytearray(b"\x80")}],
    }


@sidecar.method("exit")
def exit_(params: Any) -> None:
    sys.exit(3)


@sidecar.method("slow_with_progress")
def slow_with_progress(params: Any) -> dict[str, Any]:
    protocol._PROGRESS_MIN_INTERVAL = 0.05
    deadline = time.monotonic() + float((params or {}).get("seconds", 0.5))
    ticks = 0
    while time.monotonic() < deadline:
        time.sleep(0.02)
        ticks += 1
        report_progress(ticks=ticks)
    return {"ticks": ticks}


if __name__ == "__main__":
    asyncio.run(sidecar.serve())
