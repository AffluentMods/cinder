"""Protocol-layer regressions: encoding, stream isolation, id-less errors, progress."""

from __future__ import annotations

import asyncio
import json
from pathlib import Path

import pytest

from shared.protocol import PROGRESS_METHOD, Sidecar, json_safe

NOISY = str(Path(__file__).parent / "workers" / "noisy_worker.py")
UNICODE_TEXT = "日本語 Привет 😀 café — naïve Ω"


@pytest.mark.parametrize("io_encoding", ["cp1252", "ascii", None])
def test_non_ascii_round_trips_whatever_the_stream_encoding_is(spawn, io_encoding) -> None:
    # A piped stdout on Windows defaults to cp1252. Simulate that everywhere by forcing
    # the interpreter's stream encoding; the protocol must still be UTF-8.
    env = {"PYTHONIOENCODING": io_encoding} if io_encoding else {}
    sc = spawn("-m", "echo.echo_worker", env=env)
    assert sc.call("ping", {"text": UNICODE_TEXT}) == {"pong": {"text": UNICODE_TEXT}}


@pytest.mark.parametrize("io_encoding", ["cp1252", "ascii", None])
def test_text_produced_by_the_worker_is_sent_as_utf8(spawn, io_encoding) -> None:
    # An echo cannot show this bug: stdin mis-decodes the UTF-8 request as cp1252 and stdout
    # re-encodes the very same mojibake. Text that starts inside the worker is what fails
    # ("'charmap' codec can't encode characters").
    env = {"PYTHONIOENCODING": io_encoding} if io_encoding else {}
    sc = spawn(NOISY, env=env)
    assert sc.call("unicode_literal") == "日本語 Привет 😀 café"


@pytest.mark.parametrize("io_encoding", ["cp1252", "ascii", None])
def test_request_text_reaches_the_handler_uncorrupted(spawn, io_encoding) -> None:
    env = {"PYTHONIOENCODING": io_encoding} if io_encoding else {}
    sc = spawn(NOISY, env=env)
    # "é" must arrive as U+00E9, not as the two characters "Ã©"; "я" (UTF-8 D1 8F) holds a
    # byte cp1252 cannot decode at all, which used to end the sidecar.
    assert sc.call("codepoints", "é日я😀") == [0xE9, 0x65E5, 0x44F, 0x1F600]


def test_request_with_utf8_bom_is_accepted(spawn) -> None:
    sc = spawn("-m", "echo.echo_worker")
    request = json.dumps({"jsonrpc": "2.0", "id": 5, "method": "ping", "params": 1}).encode()
    sc.send_raw(b"\xef\xbb\xbf" + request + b"\n")
    assert sc.read_response() == {"jsonrpc": "2.0", "id": 5, "result": {"pong": 1}}


def test_unparseable_line_gets_an_idless_error_and_the_sidecar_keeps_serving(spawn) -> None:
    sc = spawn("-m", "echo.echo_worker")
    # Two requests interleaved on one line, as an unlocked concurrent writer produces.
    sc.send_raw(b'{"jsonrpc":"2.0","id":1,"method":"ping"{"jsonrpc":"2.0","id":2,"method":"ping"}\n')
    error = sc.read_response()
    assert "id" not in error
    assert error["error"]["code"] == -32700
    sc.send_raw(b"\xff\xfe not utf-8 at all\n")
    assert "id" not in sc.read_response()
    assert sc.call("ping", "still alive") == {"pong": "still alive"}


def test_invalid_request_that_is_json_keeps_its_id(spawn) -> None:
    sc = spawn("-m", "echo.echo_worker")
    sc.send_raw(b'{"jsonrpc":"2.0","id":41,"method":17}\n')
    response = sc.read_response()
    assert response["id"] == 41
    assert response["error"]["code"] == -32600


@pytest.mark.parametrize("method", ["print_stdout", "write_fd1", "child_stdout"])
def test_stray_stdout_output_cannot_corrupt_the_protocol_stream(spawn, method) -> None:
    sc = spawn(NOISY)
    # read_message() asserts that every stdout line is JSON; the forged response and the
    # raw bytes must not appear there at all.
    assert sc.call(method) == {"ok": True}
    assert sc.call("echo", "next") == "next"
    sc.close()
    expected = {
        "print_stdout": "forged by print",
        "write_fd1": "raw bytes written to fd 1",
        "child_stdout": "child process output",
    }[method]
    assert expected in sc.stderr


def test_result_with_bytes_and_lone_surrogates_is_still_delivered(spawn) -> None:
    sc = spawn(NOISY)
    result = sc.call("hostile_result")
    assert result["raw"] == b"\xff\xfe\x00binary".hex()
    assert result["lone_surrogate"] == "name-�-end"
    assert result["nan"] is None
    assert result["nested"] == [{"blob": "80"}]


def test_sys_exit_in_a_handler_fails_the_call_not_the_sidecar(spawn) -> None:
    sc = spawn(NOISY)
    response = sc.request("exit")
    assert response["error"]["code"] == -32000
    assert response["error"]["data"] == {"type": "SystemExit"}
    assert sc.call("echo", 1) == 1


def test_long_handler_emits_progress_notifications_for_its_request(spawn) -> None:
    sc = spawn(NOISY)
    result = sc.call("slow_with_progress", {"seconds": 0.6})
    assert result["ticks"] > 0
    assert sc.notifications, "no progress notification was sent"
    for note in sc.notifications:
        assert note["method"] == PROGRESS_METHOD
        assert "id" not in note
        assert note["params"]["id"] == 1


def test_handle_line_in_process() -> None:
    sidecar = Sidecar()

    @sidecar.method("boom")
    def boom(_params):
        raise KeyError("SourceDevice")

    async def run(line):
        return await sidecar.handle_line(line)

    assert asyncio.run(run(b"   \n")) is None
    response = asyncio.run(run('{"id": 3, "method": "boom"}'))
    assert response.id == 3 and response.error.code == -32000
    assert "SourceDevice" in response.error.message
    missing = asyncio.run(run(b'{"id": 4, "method": "nope"}'))
    assert missing.error.code == -32601


def test_json_safe_never_raises_and_never_emits_invalid_text() -> None:
    class Hostile:
        def __str__(self) -> str:
            raise RuntimeError("no")

    value = json_safe({1: b"\x00\xff", "s": "\ud800", "f": float("inf"), "o": Hostile(), "t": (1, 2)})
    assert value == {"1": "00ff", "s": "�", "f": None, "o": "<unprintable Hostile>", "t": [1, 2]}
    json.dumps(value).encode("utf-8")
