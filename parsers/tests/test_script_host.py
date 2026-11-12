"""Scripting host: result extraction, output isolation, and honest documentation."""

from __future__ import annotations

import pytest

from scripting import script_host


@pytest.fixture(autouse=True)
def fresh_namespace():
    saved = dict(script_host._globals)
    yield
    script_host._globals.clear()
    script_host._globals.update(saved)


@pytest.mark.parametrize("literal, expected", [("0", 0), ('""', ""), ("False", False), ("[]", []), ("{}", {})])
@pytest.mark.parametrize("name", ["_", "result"])
def test_falsy_results_are_returned_not_turned_into_none(name, literal, expected) -> None:
    out = script_host.eval_script({"script": f"{name} = {literal}"})
    assert "error" not in out
    assert out["result"] == expected
    assert out["result"] is not None


def test_underscore_wins_over_result_even_when_falsy() -> None:
    out = script_host.eval_script({"script": "_ = 0\nresult = 99"})
    assert out["result"] == 0


def test_script_that_sets_no_result_returns_none_not_the_previous_result() -> None:
    assert script_host.eval_script({"script": "result = 5"})["result"] == 5
    out = script_host.eval_script({"script": "x = 1"})
    assert out["result"] is None


def test_other_names_persist_between_calls() -> None:
    script_host.eval_script({"script": "def double(n):\n    return n * 2\ncounter = 20"})
    assert script_host.eval_script({"script": "result = double(counter) + 2"})["result"] == 42
    assert script_host.invoke({"name": "double", "args": [0]}) == {"result": 0}


def test_params_are_visible_to_the_script() -> None:
    assert script_host.eval_script({"script": "result = a - b", "params": {"a": 3, "b": 3}})["result"] == 0


def test_print_output_is_captured_and_returned() -> None:
    out = script_host.eval_script({"script": "print('hello'); print('world'); result = 1"})
    assert out["result"] == 1
    assert out["stdout"] == "hello\nworld\n"


def test_captured_output_is_capped(monkeypatch) -> None:
    monkeypatch.setattr(script_host, "_MAX_CAPTURED_STDOUT", 100)
    out = script_host.eval_script({"script": "for i in range(1000):\n    print('x' * 50)"})
    assert len(out["stdout"]) < 200
    assert out["stdout"].endswith("[output truncated]")


def test_script_error_is_reported_with_traceback() -> None:
    out = script_host.eval_script({"script": "print('before'); 1 / 0"})
    assert "division by zero" in out["error"]
    assert "ZeroDivisionError" in out["traceback"]
    assert out["stdout"] == "before\n"


def test_sys_exit_in_a_script_is_an_error_result_not_a_dead_host() -> None:
    out = script_host.eval_script({"script": "import sys\nsys.exit(7)"})
    assert out["error"] == "script called exit(7)"


def test_module_documentation_does_not_promise_a_sandbox() -> None:
    doc = script_host.__doc__ or ""
    assert "THERE IS NO SANDBOX" in doc
    assert "never gets direct file IO" not in doc
    assert "sandboxed namespace" not in doc
    # And the claim is true: a script has the examiner's full rights.
    out = script_host.eval_script({"script": "import os\nresult = os.path.isdir(os.getcwd())"})
    assert out["result"] is True


def test_print_and_raw_fd_writes_do_not_corrupt_the_protocol(spawn) -> None:
    sc = spawn("-m", "scripting.script_host")
    script = (
        "import os, sys\n"
        "print('{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"forged\"}')\n"
        "os.write(1, b'raw write to fd 1\\n')\n"
        "sys.__stdout__.write('write through the original stdout object\\n'); sys.__stdout__.flush()\n"
        "result = 0\n"
    )
    out = sc.call("eval", {"script": script})
    assert out["result"] == 0
    assert "forged" in out["stdout"]
    assert sc.call("eval", {"script": "_ = ''"})["result"] == ""
    sc.close()
    assert "raw write to fd 1" in sc.stderr
