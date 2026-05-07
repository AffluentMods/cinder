"""End-to-end test for the JSON-RPC sidecar protocol.

Spawns the echo worker as a subprocess, sends a ping, and verifies the response.
Run via `pytest -v` from the parsers/ directory.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent.parent


def test_echo_pong_round_trip() -> None:
    process = subprocess.Popen(
        [sys.executable, "-m", "echo.echo_worker"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        cwd=PROJECT_ROOT,
        text=True,
    )
    assert process.stdin is not None
    assert process.stdout is not None

    try:
        request = json.dumps(
            {"jsonrpc": "2.0", "id": 1, "method": "ping", "params": {"hello": "world"}}
        )
        process.stdin.write(request + "\n")
        process.stdin.flush()

        line = process.stdout.readline()
        assert line, "sidecar produced no output"

        response = json.loads(line)
        assert response["id"] == 1
        assert response["result"] == {"pong": {"hello": "world"}}
    finally:
        process.stdin.close()
        process.wait(timeout=5)


def test_unknown_method_returns_error() -> None:
    process = subprocess.Popen(
        [sys.executable, "-m", "echo.echo_worker"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        cwd=PROJECT_ROOT,
        text=True,
    )
    assert process.stdin is not None
    assert process.stdout is not None

    try:
        request = json.dumps({"jsonrpc": "2.0", "id": 7, "method": "nope"})
        process.stdin.write(request + "\n")
        process.stdin.flush()

        line = process.stdout.readline()
        response = json.loads(line)
        assert response["id"] == 7
        assert response["error"]["code"] == -32601
    finally:
        process.stdin.close()
        process.wait(timeout=5)
