"""Shared fixtures for the sidecar tests.

`SidecarProcess` talks to a worker the way the C# client does: UTF-8 bytes over pipes, one
JSON object per line. Reads go through a thread with a deadline so that a protocol bug
shows up as a failed assertion, not as a test run that never ends.
"""

from __future__ import annotations

import json
import os
import queue
import subprocess
import sys
import threading
from pathlib import Path
from typing import Any

import pytest

PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))


class SidecarProcess:
    def __init__(self, args: list[str], env: dict[str, str] | None = None) -> None:
        full_env = dict(os.environ)
        full_env["PYTHONPATH"] = str(PROJECT_ROOT) + os.pathsep + full_env.get("PYTHONPATH", "")
        full_env.pop("PYTHONIOENCODING", None)
        full_env.pop("PYTHONUTF8", None)
        full_env.update(env or {})
        self.process = subprocess.Popen(
            [sys.executable, *args],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            cwd=PROJECT_ROOT, env=full_env,
        )
        self._lines: queue.Queue[bytes | None] = queue.Queue()
        self.stderr_chunks: list[bytes] = []
        self.notifications: list[dict[str, Any]] = []
        self._next_id = 0
        threading.Thread(target=self._pump_stdout, daemon=True).start()
        threading.Thread(target=self._pump_stderr, daemon=True).start()

    def _pump_stdout(self) -> None:
        assert self.process.stdout is not None
        for line in self.process.stdout:
            self._lines.put(line)
        self._lines.put(None)

    def _pump_stderr(self) -> None:
        assert self.process.stderr is not None
        for chunk in iter(lambda: self.process.stderr.read1(65536), b""):
            self.stderr_chunks.append(chunk)

    @property
    def stderr(self) -> str:
        return b"".join(self.stderr_chunks).decode("utf-8", errors="replace")

    def send_raw(self, data: bytes) -> None:
        assert self.process.stdin is not None
        self.process.stdin.write(data)
        self.process.stdin.flush()

    def read_message(self, timeout: float = 20.0) -> dict[str, Any]:
        """Next protocol line as a dict. Every line on stdout must be UTF-8 JSON."""
        try:
            line = self._lines.get(timeout=timeout)
        except queue.Empty:
            raise AssertionError(f"sidecar sent nothing for {timeout}s; stderr: {self.stderr!r}") from None
        if line is None:
            raise AssertionError(f"sidecar closed stdout; stderr: {self.stderr!r}")
        return json.loads(line.decode("utf-8"))

    def read_response(self, timeout: float = 20.0) -> dict[str, Any]:
        """Next response, collecting progress notifications on the way."""
        while True:
            message = self.read_message(timeout)
            if "id" not in message and "method" in message:
                self.notifications.append(message)
                continue
            return message

    def request(self, method: str, params: Any = None, timeout: float = 20.0) -> dict[str, Any]:
        self._next_id += 1
        payload = {"jsonrpc": "2.0", "id": self._next_id, "method": method, "params": params}
        self.send_raw(json.dumps(payload, ensure_ascii=False).encode("utf-8") + b"\n")
        response = self.read_response(timeout)
        assert response.get("id") == self._next_id, response
        return response

    def call(self, method: str, params: Any = None, timeout: float = 20.0) -> Any:
        response = self.request(method, params, timeout)
        assert "error" not in response, response
        return response.get("result")

    def close(self) -> None:
        try:
            if self.process.stdin is not None:
                self.process.stdin.close()
        except OSError:
            pass
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)


@pytest.fixture
def spawn():
    started: list[SidecarProcess] = []

    def _spawn(*args: str, env: dict[str, str] | None = None) -> SidecarProcess:
        proc = SidecarProcess(list(args), env)
        started.append(proc)
        return proc

    yield _spawn
    for proc in started:
        proc.close()
