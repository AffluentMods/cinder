"""Volatility worker value conversion.

volatility3 is an optional heavy dependency and is not installed where these tests run,
so the plugin runs themselves are not exercised. What is tested is the conversion layer,
against stand-ins that reproduce the relevant behaviour of volatility3's types:

* ``BaseAbsentValue`` and its subclasses (renderers.NotApplicableValue etc.): plain
  objects with no ``__int__``; ``int()`` of one raises TypeError and ``str()`` is a repr.
* ``format_hints.Hex``: an ``int`` subclass.  ``format_hints.HexBytes``: a ``bytes`` subclass.
"""

from __future__ import annotations

import datetime as dt
import json

import pytest

from memory import vol_worker as vw
from shared.protocol import JsonRpcResponse


class BaseAbsentValue:  # same class name as volatility3.framework.interfaces.renderers.BaseAbsentValue
    pass


class NotApplicableValue(BaseAbsentValue):
    pass


class NotAvailableValue(BaseAbsentValue):
    pass


class UnreadableValue(BaseAbsentValue):
    pass


class Hex(int):
    pass


class HexBytes(bytes):
    pass


class Column:
    def __init__(self, name: str) -> None:
        self.name = name


ABSENT = [NotApplicableValue(), NotAvailableValue(), UnreadableValue(), None]


@pytest.mark.parametrize("absent", ABSENT)
def test_absent_values_become_none(absent) -> None:
    assert vw._is_absent(absent)
    assert vw._plain(absent) is None
    assert vw._int(absent) is None
    assert vw._str(absent) is None


def test_the_stand_in_really_breaks_int_like_the_real_thing() -> None:
    with pytest.raises(TypeError):
        int(NotAvailableValue() or 0)  # what the old code did


def test_plain_values() -> None:
    assert vw._plain(Hex(0x7FF6_0000)) == 0x7FF6_0000 and type(vw._plain(Hex(5))) is int
    assert vw._plain(HexBytes(b"\x4d\x5a\x90\x00\xff")) == "4d5a9000ff"
    assert vw._plain(b"\xff\xfe") == "fffe"
    assert vw._plain(dt.datetime(2021, 3, 4, 5, 6, 7, tzinfo=dt.timezone.utc)) == "2021-03-04T05:06:07+00:00"
    assert vw._plain("svchost.exe") == "svchost.exe"
    assert vw._plain(True) is True
    assert vw._int("0x10") == 16 and vw._int("12") == 12 and vw._int("n/a") is None and vw._int("") is None
    assert vw._str("") is None and vw._str(0) == "0"


def test_pstree_rows_with_absent_fields_do_not_fail() -> None:
    rows = [
        {"PID": 4, "PPID": 0, "ImageFileName": "System", "Threads": 164, "Handles": NotAvailableValue(),
         "SessionId": NotApplicableValue(), "CreateTime": dt.datetime(2021, 3, 4, 5, 6, 7, tzinfo=dt.timezone.utc),
         "ExitTime": NotApplicableValue()},
        {"PID": Hex(1234), "PPID": 4, "ImageFileName": "evil.exe", "Threads": UnreadableValue(),
         "Handles": 12, "SessionId": 1, "CreateTime": UnreadableValue(), "ExitTime": None,
         "Cmd": "evil.exe -hidden"},
    ]
    shaped = vw._shape_pstree(rows)
    assert shaped[0] == {
        "pid": 4, "ppid": 0, "image": "System", "cmdline": None,
        "created_at": "2021-03-04T05:06:07+00:00", "exited_at": None,
        "threads": 164, "handles": None, "session": None, "integrity": None, "anomalies": [],
    }
    assert shaped[1]["pid"] == 1234 and shaped[1]["threads"] is None and shaped[1]["handles"] == 12
    assert shaped[1]["created_at"] is None and shaped[1]["session"] == "1"
    assert shaped[1]["cmdline"] == "evil.exe -hidden"
    # No object repr leaks out as if it were a time.
    assert "object at 0x" not in json.dumps(shaped)


def test_malfind_hexdump_bytes_serialise() -> None:
    hexdump = HexBytes(bytes(range(240, 256)) + b"MZ\x90\x00")
    rows = [{"PID": 620, "Process": "lsass.exe", "Start VPN": Hex(0x1F0000), "Length": NotAvailableValue(),
             "Hexdump": hexdump, "Disasm": "nop"}]
    shaped = vw._shape_malfind(rows)
    assert shaped == [{
        "pid": 620, "image_name": "lsass.exe", "type": "malfind", "address": str(0x1F0000),
        "length": None, "notes": hexdump.hex(),
    }]
    # The whole reply survives the protocol encoder (the raw bytes did not: invalid UTF-8).
    JsonRpcResponse(id=1, result={"entries": shaped}).model_dump_json()


@pytest.mark.parametrize("lm_col, nt_col", [("lmhash", "nthash"), ("LM", "NT")])
def test_hashdump_accepts_both_column_spellings(lm_col, nt_col) -> None:
    rows = [
        {"User": "Administrator", "rid": 500, lm_col: "aad3b435b51404eeaad3b435b51404ee",
         nt_col: "31d6cfe0d16ae931b73c59d7e0c089c0"},
        {"User": "ghost", "rid": 1001, lm_col: NotAvailableValue(), nt_col: UnreadableValue()},
    ]
    shaped = vw._shape_hashdump(rows)
    assert shaped[0]["account"] == "Administrator" and shaped[0]["rid"] == 500
    assert shaped[0]["hash"] == "aad3b435b51404eeaad3b435b51404ee:31d6cfe0d16ae931b73c59d7e0c089c0"
    assert shaped[0]["lm"] == "aad3b435b51404eeaad3b435b51404ee"
    assert shaped[0]["nt"] == "31d6cfe0d16ae931b73c59d7e0c089c0"
    # No hash recovered: null, not the pair of empty hashes ":".
    assert shaped[1]["hash"] is None and shaped[1]["lm"] is None and shaped[1]["nt"] is None


def test_row_from_node_makes_any_treegrid_row_json_safe() -> None:
    columns = [Column("Key"), Column("Secret"), Column("Hex"), Column("When"), Column("Missing")]
    values = ["DefaultPassword", b"\x00\xffp\x00w\x00", HexBytes(b"\xde\xad"),
              dt.datetime(2021, 3, 4, tzinfo=dt.timezone.utc), NotAvailableValue()]
    row = vw._row_from_node(columns, values)
    assert row == {"Key": "DefaultPassword", "Secret": "00ff70007700", "Hex": "dead",
                   "When": "2021-03-04T00:00:00+00:00", "Missing": None}
    json.dumps(row)


def test_dlllist_netscan_cmdline_shapes_tolerate_absent_values() -> None:
    dlls = vw._shape_dlllist([
        {"PID": 4, "Name": "ntdll.dll", "Path": NotAvailableValue(), "Base": Hex(4096), "Size": UnreadableValue()},
        {"PID": 8, "Name": "x.dll", "Path": "C:\\x.dll", "Base": Hex(8192), "Size": 10},
    ], pid="4")
    assert dlls == [{"pid": 4, "name": "ntdll.dll", "path": "", "base": "4096", "size": None, "signed": False}]
    net = vw._shape_netscan([{"PID": NotAvailableValue(), "Proto": "TCPv4", "LocalAddr": "0.0.0.0", "LocalPort": 445,
                              "ForeignAddr": "*", "ForeignPort": NotApplicableValue(), "State": "LISTENING",
                              "Created": NotApplicableValue()}])
    assert net[0]["pid"] is None and net[0]["remote_port"] is None and net[0]["created_at"] is None
    assert vw._shape_cmdline([{"PID": 4, "Args": NotAvailableValue()}]) == [{"pid": 4, "cmdline": None}]


def test_without_volatility_the_methods_fail_with_a_clear_error() -> None:
    try:
        import volatility3  # noqa: F401
    except Exception:
        with pytest.raises(RuntimeError, match="volatility3 is not installed"):
            vw.pslist({"image": "memory.raw"})
    else:
        pytest.skip("volatility3 is installed")
