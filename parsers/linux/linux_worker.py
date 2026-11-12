"""Cinder Linux-artifact sidecar.

Pure Python — no external native libraries. Operates on a *root path* that's typically the
mount point of an examined Linux filesystem (or the live `/` for triage). All paths inside
the sidecar are relative to that root.

Methods (all return JSON-serializable dicts):
    shell_history(root)            -> {entries:[{user, shell, command, timestamp}]}
    auth_log(path, root?)          -> {entries:[{timestamp, timestamp_raw, year_inferred, tz_known,
                                                 host, process, message, user, remote_host}]}
    journalctl(journal_dir, root?, limit?) -> {entries:[{timestamp, unit, priority, message, user}], truncated_by}
    cron(root)                     -> {entries:[{user, schedule, command, source}]}
    ssh_known_hosts(root)          -> {entries:[{user, host, marker, key_type, fingerprint}]}
    ssh_authorized_keys(root)      -> {entries:[{user, key_type, comment, options, fingerprint}]}
    trash(root)                    -> {entries:[{user, original_path, size, deleted_at}]}
    recently_used(root)            -> {entries:[{user, uri, mime, modified, visited}]}
    systemd_units(root)            -> {entries:[{name, path, enabled, masked, state}]}
    package_logs(root)             -> {entries:[{timestamp, pm, action, package, version}]}
    passwd_shadow(root)            -> {entries:[{name, uid, gid, home, shell, comment, password_hash}]}

Every result also carries ``refused`` (paths that were not read, and why) and ``truncated``
(files that were longer than the read limit).

Rules this module holds itself to, because the input is evidence and may be hostile:

* Nothing outside the evidence root is ever read. A mounted image is full of symlinks;
  an absolute one (``.bash_history -> /etc/shadow``) points into the *examiner's* system
  once the image is mounted. Every path is resolved first and refused if it leaves the
  root, and only regular files are opened (no devices, FIFOs or sockets).
* Every read is bounded.
* One malformed line costs at most that line. It never fails the file or the call.
* A timestamp that cannot be determined is null. It is never "now". A value that had to
  be inferred (the year of a classic syslog line) says so in the row, and a time whose
  zone the log does not record is emitted without an offset rather than labelled UTC.
"""

from __future__ import annotations

import asyncio
import base64
import datetime as _dt
import hashlib
import os
import re
import stat
from collections.abc import Iterator
from pathlib import Path
from typing import Any

from shared.protocol import Sidecar, report_progress

sidecar = Sidecar()

MAX_TEXT_BYTES = 64 << 20        # files read whole: histories, crontabs, passwd, key files
MAX_LOG_BYTES = 256 << 20        # logs read as a stream of lines
MAX_LINE_BYTES = 64 << 10        # one line of a streamed log
MAX_JOURNAL_ENTRIES = 200_000
MAX_JOURNAL_BYTES = 256 << 20
JOURNAL_TIMEOUT_SECONDS = 120.0
_JOURNALCTL = ["journalctl"]     # replaced in tests

_UNIX_EPOCH = _dt.datetime(1970, 1, 1, tzinfo=_dt.timezone.utc)


# -------------------- time helpers --------------------

def _epoch_iso(seconds: Any) -> str | None:
    """Unix seconds -> UTC ISO 8601; None if unset or not a representable time."""
    if seconds is None:
        return None
    try:
        return (_UNIX_EPOCH + _dt.timedelta(seconds=float(seconds))).isoformat()
    except (OverflowError, ValueError, TypeError):
        return None


def _iso(ts: _dt.datetime | None) -> str | None:
    """Aware datetime -> UTC ISO 8601. A naive datetime has no known zone and is emitted
    as it is, without an offset: local time is not relabelled as UTC."""
    if ts is None:
        return None
    try:
        if ts.tzinfo is None:
            return ts.isoformat()
        return ts.astimezone(_dt.timezone.utc).isoformat()
    except (OverflowError, ValueError):
        return None


def _utc_iso(ts: Any) -> str | None:
    """Kept for callers that pass either an epoch number or a datetime."""
    if ts is None:
        return None
    if isinstance(ts, (int, float)):
        return _epoch_iso(ts)
    if isinstance(ts, _dt.datetime):
        return _iso(ts)
    return str(ts)


# -------------------- confined, bounded reading --------------------

class _Evidence:
    """Reads files below one evidence root and refuses everything else."""

    def __init__(self, root: str | os.PathLike[str]) -> None:
        self.root = Path(root)
        self._resolved_root = Path(os.path.realpath(self.root))
        self.refused: list[dict[str, str]] = []
        self.truncated: list[str] = []

    def _refuse(self, path: Path, reason: str) -> None:
        record = {"path": str(path), "reason": reason}
        if record not in self.refused:
            self.refused.append(record)

    def locate(self, path: Path, *, directory: bool = False) -> Path | None:
        """The real location of ``path`` if it exists, lies inside the root and is a
        regular file (or a directory when ``directory``). Otherwise None; a refusal that is
        not simply "does not exist" is recorded."""
        try:
            if not os.path.lexists(path):
                return None
            resolved = Path(os.path.realpath(path))
        except (OSError, ValueError, RuntimeError) as ex:
            self._refuse(path, f"cannot be resolved: {ex}")
            return None
        if resolved != self._resolved_root and not resolved.is_relative_to(self._resolved_root):
            self._refuse(path, "symlink resolves outside the evidence root")
            return None
        try:
            mode = resolved.stat().st_mode
        except OSError:
            return None  # dangling link whose target would be inside the root
        if directory:
            if not stat.S_ISDIR(mode):
                return None
        elif not stat.S_ISREG(mode):
            self._refuse(path, "not a regular file")
            return None
        return resolved

    def lexists(self, path: Path) -> bool:
        """Does the directory entry exist? Never follows a symlink."""
        try:
            return os.path.lexists(path)
        except (OSError, ValueError):
            return False

    def listdir(self, path: Path) -> list[Path]:
        """Entries of a directory inside the root (sorted, unresolved), or []."""
        resolved = self.locate(path, directory=True)
        if resolved is None:
            return []
        try:
            return [path / name for name in sorted(os.listdir(resolved))]
        except OSError:
            return []

    def read_bytes(self, path: Path, limit: int = MAX_TEXT_BYTES) -> bytes | None:
        resolved = self.locate(path)
        if resolved is None:
            return None
        try:
            with open(resolved, "rb") as f:
                data = f.read(limit + 1)
        except OSError as ex:
            self._refuse(path, f"unreadable: {ex}")
            return None
        if len(data) > limit:
            self.truncated.append(str(path))
            data = data[:limit]
        return data

    def read_text(self, path: Path, limit: int = MAX_TEXT_BYTES) -> str | None:
        data = self.read_bytes(path, limit)
        return None if data is None else data.decode("utf-8", errors="replace")

    def iter_lines(self, path: Path, limit: int = MAX_LOG_BYTES) -> Iterator[str]:
        """Lines of a log, read as a stream. Stops (and records it) at ``limit`` bytes; a
        line longer than MAX_LINE_BYTES is cut there."""
        resolved = self.locate(path)
        if resolved is None:
            return
        consumed = 0
        try:
            with open(resolved, "rb") as f:
                while True:
                    if consumed >= limit:
                        if f.read(1):
                            self.truncated.append(str(path))
                        return
                    raw = f.readline(min(MAX_LINE_BYTES, limit - consumed))
                    if not raw:
                        return
                    consumed += len(raw)
                    yield raw.decode("utf-8", errors="replace").rstrip("\r\n")
        except OSError as ex:
            self._refuse(path, f"unreadable: {ex}")

    def result(self, entries: list[dict[str, Any]], **extra: Any) -> dict[str, Any]:
        out: dict[str, Any] = {"entries": entries, "refused": self.refused, "truncated": self.truncated}
        out.update(extra)
        return out


def _users_in(ev: _Evidence) -> list[tuple[str, Path]]:
    """(username, home_dir) for each home directory under <root>/home/* (and /root)."""
    out: list[tuple[str, Path]] = []
    for d in ev.listdir(ev.root / "home"):
        if ev.locate(d, directory=True) is not None:
            out.append((d.name, d))
    rooth = ev.root / "root"
    if ev.locate(rooth, directory=True) is not None:
        out.append(("root", rooth))
    return out


# -------------------- shell history --------------------

def _parse_history(text: str, user: str, shell: str) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    ts: float | None = None
    fish_last: dict[str, Any] | None = None
    for line in text.splitlines():
        # Bash with HISTTIMEFORMAT prefixes "#<unix_ts>" lines
        if shell == "bash" and line.startswith("#") and line[1:].isdigit():
            try:
                ts = float(line[1:])
            except (ValueError, OverflowError):
                ts = None
            continue
        # Zsh extended_history: ": <epoch>:<elapsed>;<cmd>"
        if shell == "zsh" and line.startswith(": "):
            head, _, cmd = line[2:].partition(";")
            try:
                ts = float(head.split(":")[0])
            except (ValueError, OverflowError):
                ts = None
            out.append({"user": user, "shell": shell, "command": cmd, "timestamp": _epoch_iso(ts)})
            ts = None
            continue
        # Fish stores - cmd: <cmd>\n  when: <epoch>
        if shell == "fish":
            if line.startswith("- cmd: "):
                fish_last = {"user": user, "shell": shell, "command": line[7:], "timestamp": None}
                out.append(fish_last)
            elif line.startswith("  when: ") and fish_last is not None and fish_last["timestamp"] is None:
                try:
                    fish_last["timestamp"] = _epoch_iso(float(line[8:]))
                except (ValueError, OverflowError):
                    pass
            continue
        if not line.strip():
            continue
        # A "#<digits>" that is not a plausible time (e.g. #99999999999999999) gives a
        # null timestamp for this command; it does not fail the file.
        out.append({"user": user, "shell": shell, "command": line, "timestamp": _epoch_iso(ts)})
        ts = None
    return out


@sidecar.method("shell_history")
def shell_history(params: Any) -> dict[str, Any]:
    ev = _Evidence(params["root"])
    out: list[dict[str, Any]] = []
    for user, home in _users_in(ev):
        for fname, shell in (
            (".bash_history", "bash"),
            (".zsh_history", "zsh"),
            (".fish_history", "fish"),
            (".sh_history", "sh"),
        ):
            text = ev.read_text(home / fname)
            if text is None:
                continue
            try:
                out.extend(_parse_history(text, user, shell))
            except Exception as ex:  # one user's file must not cost every other user's history
                ev.refused.append({"path": str(home / fname), "reason": f"unparseable: {ex}"})
    return ev.result(out)


# -------------------- auth.log --------------------

_AUTH_RE = re.compile(
    r"^(?P<ts>\w+\s+\d+\s+\d+:\d+:\d+|\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[\.\d]*[+\-Z\d:]*)\s+"
    r"(?P<host>\S+)\s+(?P<proc>[^\[\s:]+)(?:\[\d+\])?:\s*(?P<msg>.*)$"
)
_SYSLOG_TS_RE = re.compile(r"^(?P<mon>[A-Za-z]{3})\s+(?P<day>\d{1,2})\s+(?P<h>\d{1,2}):(?P<m>\d{2}):(?P<s>\d{2})$")
_MONTHS = {m: i + 1 for i, m in enumerate(
    ("jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"))}

# The widest possible difference between a log's local wall clock and UTC is 14 hours.
_ZONE_SLACK = _dt.timedelta(days=1)

# sshd's own messages have a fixed shape, so they are matched whole. The user name in them
# is whatever the client sent: it may be empty, contain spaces, or contain " from 1.2.3.4".
# The greedy match makes the *last* "from <addr> port <n>" the address, so a crafted user
# name cannot forge the source of a login attempt.
_SSHD_PATTERNS = (
    # Failed password for invalid user admin from 1.2.3.4 port 22 ssh2
    # Accepted publickey for alice from 1.2.3.4 port 22 ssh2: ...
    re.compile(r"^(?:Failed|Accepted|Postponed) \S+ for (?:(?:invalid|illegal) user )?"
               r"(?P<user>.*) from (?P<addr>\S+) port \d+"),
    # Invalid user admin from 1.2.3.4 port 22
    re.compile(r"^(?:Invalid|Illegal) user (?P<user>.*) from (?P<addr>\S+)(?: port \d+)?\s*$"),
    # Disconnected from invalid user admin 1.2.3.4 port 22 [preauth]
    # Connection closed by authenticating user root 1.2.3.4 port 22 [preauth]
    # Connection closed by 1.2.3.4 port 22 [preauth]     (no user at all)
    re.compile(r"^(?:Disconnected from|Connection (?:closed|reset) by)"
               r"(?: (?:invalid |illegal |authenticating )?user (?P<user>\S+))? (?P<addr>\S+) port \d+"),
)
# Everything else (PAM, su, sudo, ...): looser, most specific first. "for invalid user X"
# names X and must not be read as the user "invalid"; a bare "for user" / "for invalid user"
# with nothing after it names nobody.
_AUTH_USER_PATTERNS = (
    re.compile(r"(?:^|\s)for (?:invalid|illegal) user (\S+)"),
    re.compile(r"(?<![A-Za-z0-9_])user=(\S+)"),
    re.compile(r"(?:^|\s)for user (\S+)"),
    re.compile(r"(?:^|\s)for (?!(?:(?:invalid|illegal) )?user\s*$)(\S+)"),
)
_RHOST_RE = re.compile(r"(?<![A-Za-z0-9_])rhost=(\S+)")


def _auth_fields(msg: str) -> tuple[str | None, str | None]:
    """(user, remote host) named by an auth.log message; None for whatever it does not name."""
    for pattern in _SSHD_PATTERNS:
        m = pattern.match(msg)
        if m:
            return (m.group("user") or None), m.group("addr")

    user: str | None = None
    for pattern in _AUTH_USER_PATTERNS:
        m = pattern.search(msg)
        if m:
            # "session opened for user root(uid=0) by ..." -> root
            user = re.sub(r"\(uid=\d+\)$", "", m.group(1).strip("'\"")) or None
            break

    remote: str | None = None
    _head, sep, tail = msg.rpartition(" from ")
    fields = tail.split() if sep else []
    if fields:
        remote = fields[0]
    else:
        m = _RHOST_RE.search(msg)
        remote = m.group(1) if m else None
    return user, remote


def _auth_user(msg: str) -> str | None:
    return _auth_fields(msg)[0]


def _auth_remote(msg: str) -> str | None:
    return _auth_fields(msg)[1]


def _parse_syslog_stamp(text: str) -> tuple[int, int, int, int, int] | None:
    m = _SYSLOG_TS_RE.match(text)
    if not m:
        return None
    month = _MONTHS.get(m.group("mon").lower())
    if month is None:
        return None
    stamp = (month, int(m.group("day")), int(m.group("h")), int(m.group("m")), int(m.group("s")))
    # Reject what is a date in no year at all (Feb 30, hour 99); Feb 29 passes here and is
    # checked against the inferred year later. 2000 is a leap year.
    return stamp if _stamp_in_year(2000, stamp) is not None else None


def _stamp_in_year(year: int, stamp: tuple[int, int, int, int, int]) -> _dt.datetime | None:
    try:
        return _dt.datetime(year, *stamp)
    except ValueError:
        return None  # e.g. Feb 29 in a year that has none


def _infer_years(stamps: list[tuple[int, int, int, int, int] | None],
                 reference: _dt.datetime | None) -> list[int | None]:
    """Years for classic syslog timestamps, which do not record one.

    ``reference`` is when the log was last written (the file's modification time, naive
    UTC). The last line was written then, so it carries the reference's year — or the year
    before, if in the reference's year it would lie in the future. Walking backwards from
    there, each time the month number goes *up* (January back to December) the log has
    crossed a New Year and the year drops by one.

    Returns None for every line when there is no reference: the year is then unknown and
    is not replaced by the year the analysis happens to run in.
    """
    years: list[int | None] = [None] * len(stamps)
    if reference is None:
        return years
    indexed = [i for i, s in enumerate(stamps) if s is not None]
    if not indexed:
        return years
    last = stamps[indexed[-1]]
    assert last is not None
    year = reference.year
    candidate = _stamp_in_year(year, last)
    if candidate is None or candidate > reference + _ZONE_SLACK:
        year -= 1
    years[indexed[-1]] = year
    newer_month = last[0]
    for i in reversed(indexed[:-1]):
        stamp = stamps[i]
        assert stamp is not None
        if stamp[0] > newer_month:
            year -= 1
        years[i] = year
        newer_month = stamp[0]
    return years


def _reference_time(params: Any, resolved: Path | None) -> _dt.datetime | None:
    """When the log was last written, as naive UTC: ``reference_mtime`` (unix seconds) if
    the caller knows it from the image, else the file's own modification time."""
    seconds: Any = params.get("reference_mtime") if isinstance(params, dict) else None
    if seconds is None and resolved is not None:
        try:
            seconds = resolved.stat().st_mtime
        except OSError:
            seconds = None
    if seconds is None:
        return None
    try:
        return (_UNIX_EPOCH + _dt.timedelta(seconds=float(seconds))).replace(tzinfo=None)
    except (OverflowError, ValueError, TypeError):
        return None


@sidecar.method("auth_log")
def auth_log(params: Any) -> dict[str, Any]:
    """Parse auth.log / secure.

    ``root`` (optional) is the evidence root the path must stay inside. Without it the
    log's own directory is used, so a log that is a symlink to somewhere else is refused.
    """
    p = Path(params["path"])
    ev = _Evidence(params.get("root") or p.parent)
    resolved = ev.locate(p)
    rows: list[dict[str, Any]] = []
    stamps: list[tuple[int, int, int, int, int] | None] = []
    if resolved is None:
        return ev.result(rows)

    for line in ev.iter_lines(p):
        report_progress(lines=len(rows))
        m = _AUTH_RE.match(line)
        if not m:
            continue
        ts_str = m.group("ts")
        timestamp: str | None = None
        tz_known = False
        stamp: tuple[int, int, int, int, int] | None = None
        if "T" in ts_str:
            try:
                parsed = _dt.datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                timestamp = _iso(parsed)
                tz_known = parsed.tzinfo is not None
            except ValueError:
                timestamp = None
        else:
            stamp = _parse_syslog_stamp(ts_str)
        msg = m.group("msg")
        try:
            user, remote = _auth_fields(msg)
        except Exception:  # a crafted message costs its own user/host fields, nothing else
            user, remote = None, None
        rows.append({
            "timestamp": timestamp, "timestamp_raw": ts_str,
            "year_inferred": False, "tz_known": tz_known,
            "host": m.group("host"), "process": m.group("proc"), "message": msg,
            "user": user, "remote_host": remote,
        })
        stamps.append(stamp)

    extra: dict[str, Any] = {}
    if any(s is not None for s in stamps):
        reference = _reference_time(params, resolved)
        years = _infer_years(stamps, reference)
        for row, stamp, year in zip(rows, stamps, years):
            if stamp is None or year is None:
                continue
            moment = _stamp_in_year(year, stamp)
            if moment is None:
                continue
            # Classic syslog records neither year nor zone: the year is inferred, and the
            # time is the log host's local wall clock, emitted without an offset.
            row["timestamp"] = moment.isoformat()
            row["year_inferred"] = True
        extra["year_source"] = "reference_mtime" if params.get("reference_mtime") is not None else "file_mtime"
        extra["year_reference"] = reference.isoformat() + "Z" if reference is not None else None
    return ev.result(rows, **extra)


# -------------------- journal --------------------

@sidecar.method("journalctl")
def journalctl(params: Any) -> dict[str, Any]:
    """systemd journals are a binary format. Cinder shells out to `journalctl --directory <dir>
    -o json` rather than parse the on-disk format directly. TODO 5.1: add a native parser so
    Cinder can read offline journals without a `journalctl` binary.

    The output is read as a stream and capped (``limit`` entries, MAX_JOURNAL_BYTES bytes,
    JOURNAL_TIMEOUT_SECONDS): a journal can hold millions of records, and buffering all of
    them before parsing any is how a sidecar runs out of memory. ``truncated`` says whether
    a cap ended the read.
    """
    import json as _json
    import subprocess
    import tempfile
    import threading

    journal_dir = Path(params["journal_dir"])
    ev = _Evidence(params.get("root") or journal_dir)
    if not params.get("root") and os.path.islink(journal_dir):
        # No root to hold the link's target against: do not follow it at all.
        ev._refuse(journal_dir, "journal directory is a symlink and no evidence root was given")
        return ev.result([], truncated_by=None)
    if ev.locate(journal_dir, directory=True) is None:
        return ev.result([], truncated_by=None)
    try:
        limit = max(1, min(int(params.get("limit") or MAX_JOURNAL_ENTRIES), MAX_JOURNAL_ENTRIES))
    except (TypeError, ValueError):
        limit = MAX_JOURNAL_ENTRIES

    out: list[dict[str, Any]] = []
    truncated_by: str | None = None
    with tempfile.TemporaryFile() as err_file:
        try:
            proc = subprocess.Popen(
                [*_JOURNALCTL, "--directory", str(journal_dir), "-o", "json", "--no-pager", "--quiet"],
                stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=err_file,
            )
        except FileNotFoundError:
            return ev.result([], todo="install journalctl or wait for native parser (5.1)")
        timed_out = threading.Event()

        def _expire() -> None:
            timed_out.set()
            try:
                proc.kill()
            except OSError:
                pass

        watchdog = threading.Timer(JOURNAL_TIMEOUT_SECONDS, _expire)
        watchdog.daemon = True
        watchdog.start()
        consumed = 0
        try:
            assert proc.stdout is not None
            while True:
                raw = proc.stdout.readline(MAX_LINE_BYTES * 16)
                if not raw:
                    break
                consumed += len(raw)
                if consumed > MAX_JOURNAL_BYTES:
                    truncated_by = "bytes"
                    break
                try:
                    j = _json.loads(raw)
                    if not isinstance(j, dict):
                        continue
                    try:
                        ts_us = int(j.get("__REALTIME_TIMESTAMP") or 0)
                    except (TypeError, ValueError):
                        ts_us = 0
                    out.append({
                        "timestamp": _epoch_iso(ts_us / 1_000_000) if ts_us else None,
                        "unit": j.get("_SYSTEMD_UNIT", j.get("SYSLOG_IDENTIFIER", "")),
                        "priority": j.get("PRIORITY", ""),
                        "message": j.get("MESSAGE", ""),
                        "user": j.get("_UID"),
                    })
                except Exception:
                    continue
                report_progress(entries=len(out))
                if len(out) >= limit:
                    truncated_by = "entries"
                    break
        finally:
            watchdog.cancel()
            if proc.poll() is None:
                try:
                    proc.kill()
                except OSError:
                    pass
            try:
                proc.stdout.close()  # type: ignore[union-attr]
            except OSError:
                pass
            proc.wait()
        if timed_out.is_set() and truncated_by is None:
            truncated_by = "timeout"
        extra: dict[str, Any] = {"truncated_by": truncated_by}
        if truncated_by is None and proc.returncode != 0:
            err_file.seek(0)
            extra["stderr"] = err_file.read(64 << 10).decode("utf-8", errors="replace")
    return ev.result(out, **extra)


# -------------------- cron --------------------

_CRON_ENV_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*\s*=")


def _parse_cron_line(line: str, *, has_user_field: bool, owner: str) -> dict[str, str] | None | bool:
    """One crontab line -> {user, schedule, command}; None for blanks, comments and
    environment assignments; False for a line that is none of those and does not parse.

    User crontabs (/var/spool/cron/...) are ``<5 time fields> <command>`` and belong to the
    user the file is named after. System crontabs (/etc/crontab, /etc/cron.d/*) have a
    sixth field, the user to run as. Both also accept ``@reboot`` / ``@daily`` / ... in
    place of the five time fields.
    """
    line = line.strip()
    if not line or line.startswith("#") or _CRON_ENV_RE.match(line):
        return None
    time_fields = 1 if line.startswith("@") else 5
    fields_before_command = time_fields + (1 if has_user_field else 0)
    parts = line.split(None, fields_before_command)
    if len(parts) <= fields_before_command:
        return False
    schedule = " ".join(parts[:time_fields])
    user = parts[time_fields] if has_user_field else owner
    return {"user": user, "schedule": schedule, "command": parts[fields_before_command]}


def _parse_anacron_line(line: str) -> dict[str, str] | None | bool:
    """anacrontab: ``<period> <delay> <job-id> <command>``. Not a cron schedule."""
    line = line.strip()
    if not line or line.startswith("#") or _CRON_ENV_RE.match(line):
        return None
    parts = line.split(None, 3)
    if len(parts) < 4:
        return False
    period, delay, job_id, command = parts
    return {"user": "root", "schedule": f"anacron period={period} delay={delay} job={job_id}", "command": command}


@sidecar.method("cron")
def cron(params: Any) -> dict[str, Any]:
    ev = _Evidence(params["root"])
    root = ev.root
    out: list[dict[str, Any]] = []
    unparsed = 0

    # (path, kind, owner, source); kind: "system" has a user field, "user" does not.
    crontabs: list[tuple[Path, str, str, str]] = [
        (root / "etc/crontab", "system", "root", "system"),
        (root / "etc/anacrontab", "anacron", "root", "system"),
    ]

    def _source(f: Path) -> str:
        try:
            return str(f.relative_to(root))
        except ValueError:
            return str(f)

    for f in ev.listdir(root / "etc/cron.d"):
        crontabs.append((f, "system", "root", _source(f)))
    for spool in (root / "var/spool/cron/crontabs", root / "var/spool/cron"):
        for f in ev.listdir(spool):
            crontabs.append((f, "user", f.name, _source(f)))

    for path, kind, owner, source in crontabs:
        if ev.locate(path, directory=True) is not None:
            continue  # e.g. var/spool/cron/crontabs seen while listing var/spool/cron
        text = ev.read_text(path)
        if text is None:
            continue
        for line in text.splitlines():
            try:
                if kind == "anacron":
                    parsed = _parse_anacron_line(line)
                else:
                    parsed = _parse_cron_line(line, has_user_field=(kind == "system"), owner=owner)
            except Exception:
                parsed = False
            if parsed is None:
                continue
            if parsed is False:
                unparsed += 1
                continue
            assert isinstance(parsed, dict)
            out.append({**parsed, "source": source})
    return ev.result(out, unparsed_lines=unparsed)


# -------------------- ssh --------------------

# "<key type> <base64 key blob> [comment]", anywhere in the line: known_hosts puts a marker
# and host names in front, authorized_keys puts options in front. Every OpenSSH key blob
# starts with a 4-byte length whose first three bytes are zero, hence "AAAA".
_SSH_KEY_RE = re.compile(
    r"(?:^|\s)(?P<type>[A-Za-z0-9@._+-]+)\s+(?P<blob>AAAA[A-Za-z0-9+/]+={0,2})(?:\s+(?P<comment>.*))?$"
)


def _ssh_key_fields(line: str) -> dict[str, str] | None:
    """Split a known_hosts / authorized_keys line into prefix, key type, blob, comment."""
    m = _SSH_KEY_RE.search(line.strip())
    if not m:
        return None
    return {
        "prefix": line.strip()[: m.start()].strip(),
        "type": m.group("type"),
        "blob": m.group("blob"),
        "comment": (m.group("comment") or "").strip(),
    }


def _ssh_fp(line: str) -> str | None:
    """OpenSSH SHA256 fingerprint of the key on ``line``: SHA-256 over the decoded key
    blob (the field after the key type), not over the trailing comment. None if the line
    holds no decodable key."""
    fields = _ssh_key_fields(line)
    if fields is None:
        return None
    blob = fields["blob"]
    try:
        key = base64.b64decode(blob + "=" * (-len(blob) % 4), validate=True)
    except Exception:
        return None
    if not key:
        return None
    return "SHA256:" + base64.b64encode(hashlib.sha256(key).digest()).decode("ascii").rstrip("=")


@sidecar.method("ssh_known_hosts")
def ssh_known_hosts(params: Any) -> dict[str, Any]:
    ev = _Evidence(params["root"])
    out: list[dict[str, Any]] = []
    unparsed = 0
    for user, home in _users_in(ev):
        text = ev.read_text(home / ".ssh/known_hosts")
        if text is None:
            continue
        for line in text.splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            fields = _ssh_key_fields(line)
            prefix = fields["prefix"].split() if fields else []
            if fields is None or not prefix:
                unparsed += 1
                continue
            marker = prefix[0] if prefix[0].startswith("@") and len(prefix) > 1 else None
            out.append({
                "user": user, "host": prefix[-1], "marker": marker,
                "key_type": fields["type"], "fingerprint": _ssh_fp(line),
            })
    return ev.result(out, unparsed_lines=unparsed)


@sidecar.method("ssh_authorized_keys")
def ssh_authorized_keys(params: Any) -> dict[str, Any]:
    ev = _Evidence(params["root"])
    out: list[dict[str, Any]] = []
    unparsed = 0
    for user, home in _users_in(ev):
        text = ev.read_text(home / ".ssh/authorized_keys")
        if text is None:
            continue
        for line in text.splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            fields = _ssh_key_fields(line)
            if fields is None:
                unparsed += 1
                continue
            out.append({
                "user": user, "key_type": fields["type"], "comment": fields["comment"],
                "options": fields["prefix"] or None, "fingerprint": _ssh_fp(line),
            })
    return ev.result(out, unparsed_lines=unparsed)


# -------------------- trash / recent files --------------------

@sidecar.method("trash")
def trash(params: Any) -> dict[str, Any]:
    ev = _Evidence(params["root"])
    out: list[dict[str, Any]] = []
    for user, home in _users_in(ev):
        info_dir = home / ".local/share/Trash/info"
        files_root = home / ".local/share/Trash/files"
        for f in ev.listdir(info_dir):
            if f.suffix != ".trashinfo":
                continue
            try:
                txt = ev.read_text(f, 1 << 20)
                if txt is None:
                    continue
                orig = next((line[5:] for line in txt.splitlines() if line.startswith("Path=")), "")
                deleted = next((line[13:] for line in txt.splitlines() if line.startswith("DeletionDate=")), "")
                try:
                    # lstat: the trashed item may itself be a symlink to anywhere.
                    size: int | None = os.lstat(files_root / f.stem).st_size
                except OSError:
                    size = None
                try:
                    # The spec stores local time without a zone; it is passed on as such.
                    deleted_at = _iso(_dt.datetime.fromisoformat(deleted.strip()))
                except ValueError:
                    deleted_at = None
                out.append({"user": user, "original_path": orig, "size": size, "deleted_at": deleted_at})
            except Exception:
                continue
    return ev.result(out)


@sidecar.method("recently_used")
def recently_used(params: Any) -> dict[str, Any]:
    import xml.etree.ElementTree as ET
    ev = _Evidence(params["root"])
    out: list[dict[str, Any]] = []
    for user, home in _users_in(ev):
        f = home / ".local/share/recently-used.xbel"
        data = ev.read_bytes(f)
        if data is None:
            continue
        if b"<!ENTITY" in data:
            ev.refused.append({"path": str(f), "reason": "declares XML entities (possible entity-expansion bomb)"})
            continue
        try:
            tree = ET.fromstring(data)
            for bm in tree.iter("bookmark"):
                out.append({
                    "user": user,
                    "uri": bm.get("href", ""),
                    "mime": bm.get("mime-type", None),
                    "modified": bm.get("modified", None),
                    "visited": bm.get("visited", None),
                })
        except Exception:
            continue
    return ev.result(out)


# -------------------- systemd --------------------

@sidecar.method("systemd_units")
def systemd_units(params: Any) -> dict[str, Any]:
    ev = _Evidence(params["root"])
    root = ev.root
    out: list[dict[str, Any]] = []
    wants = root / "etc/systemd/system/multi-user.target.wants"
    for d in (root / "etc/systemd/system", root / "usr/lib/systemd/system", root / "lib/systemd/system"):
        if ev.locate(d, directory=True) is None:
            continue
        for dirpath, _dirnames, filenames in os.walk(d, followlinks=False):
            for name in sorted(filenames):
                if not name.endswith(".service"):
                    continue
                f = Path(dirpath) / name
                try:
                    masked = f.is_symlink() and str(f.readlink()) == "/dev/null"
                except OSError:
                    masked = False
                out.append({
                    "name": name, "path": str(f),
                    # An enablement link is an absolute symlink; whether its target exists
                    # on the *examiner's* machine is irrelevant, so the link is not followed.
                    "enabled": ev.lexists(wants / name),
                    "masked": masked,
                    "state": None,
                })
    return ev.result(out)


# -------------------- package manager logs --------------------

_APT_ACTIONS = ("Install", "Upgrade", "Remove", "Purge", "Reinstall", "Downgrade")
# "name:arch (version[, more])" — the list separator ", " also occurs inside the
# parentheses ("(1.0, 1.1)", "(1.0, automatic)"), so entries are matched, not split.
_APT_PKG_RE = re.compile(r"([^\s,()]+)\s*\(([^)]*)\)")


def _parse_apt_packages(action: str, text: str) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    for name, detail in _APT_PKG_RE.findall(text):
        versions = [v.strip() for v in detail.split(",") if v.strip() and v.strip() != "automatic"]
        row: dict[str, Any] = {"package": name.split(":")[0], "version": None}
        if action in ("Upgrade", "Downgrade") and len(versions) >= 2:
            row["previous_version"], row["version"] = versions[0], versions[1]
        elif versions:
            row["version"] = versions[0]
        out.append(row)
    return out


def _parse_apt_history(text: str) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    for entry in re.split(r"\r?\n\s*\r?\n", text):
        if "Start-Date:" not in entry:
            continue
        timestamp: str | None = None
        ts_match = re.search(r"^Start-Date:\s+(\S+)\s+(\S+)", entry, re.MULTILINE)
        if ts_match:
            try:
                # apt writes local time and no zone: emitted without an offset.
                timestamp = _iso(_dt.datetime.strptime(
                    f"{ts_match.group(1)} {ts_match.group(2)}", "%Y-%m-%d %H:%M:%S"))
            except ValueError:
                timestamp = None  # malformed Start-Date: unknown, and the packages still count
        for action in _APT_ACTIONS:
            am = re.search(rf"^{action}: (.+)$", entry, re.MULTILINE)
            if not am:
                continue
            for pkg in _parse_apt_packages(action, am.group(1)):
                out.append({"timestamp": timestamp, "pm": "apt", "action": action.lower(), **pkg})
    return out


@sidecar.method("package_logs")
def package_logs(params: Any) -> dict[str, Any]:
    ev = _Evidence(params["root"])
    root = ev.root
    out: list[dict[str, Any]] = []

    apt_text = ev.read_text(root / "var/log/apt/history.log")
    if apt_text is not None:
        try:
            out.extend(_parse_apt_history(apt_text))
        except Exception as ex:
            ev.refused.append({"path": str(root / "var/log/apt/history.log"), "reason": f"unparseable: {ex}"})

    for line in ev.iter_lines(root / "var/log/dnf.log"):
        if " INFO " in line and (" Installed:" in line or " Erased:" in line or " Upgrade:" in line):
            out.append({"timestamp": None, "pm": "dnf", "action": "log", "package": line[-200:], "version": None})

    pacman_re = re.compile(r"\[(\S+)\] \[ALPM\] (installed|removed|upgraded|reinstalled) ([^\s]+) \(([^)]+)\)")
    for line in ev.iter_lines(root / "var/log/pacman.log"):
        m = pacman_re.match(line)
        if m:
            try:
                ts = _iso(_dt.datetime.fromisoformat(m.group(1).replace("Z", "+00:00")))
            except ValueError:
                ts = None
            out.append({"timestamp": ts, "pm": "pacman", "action": m.group(2), "package": m.group(3), "version": m.group(4)})
    return ev.result(out)


# -------------------- accounts --------------------

def _int_or_none(text: str) -> int | None:
    try:
        return int(text.strip())
    except ValueError:
        return None


@sidecar.method("passwd_shadow")
def passwd_shadow(params: Any) -> dict[str, Any]:
    ev = _Evidence(params["root"])
    root = ev.root
    out: list[dict[str, Any]] = []
    malformed = 0
    passwd_text = ev.read_text(root / "etc/passwd")
    if passwd_text is None:
        return ev.result(out, malformed_lines=0)
    shadow_map: dict[str, str] = {}
    shadow_text = ev.read_text(root / "etc/shadow")
    if shadow_text is not None:
        for line in shadow_text.splitlines():
            parts = line.split(":")
            if len(parts) >= 2:
                shadow_map[parts[0]] = parts[1]
    for line in passwd_text.splitlines():
        parts = line.split(":")
        if len(parts) < 7:
            if line.strip() and not line.startswith("#"):
                malformed += 1
            continue
        uid = _int_or_none(parts[2])
        gid = _int_or_none(parts[3])
        row = {
            # A UID/GID that is not a number is reported as null with the raw text kept:
            # an account line edited by hand is evidence, not a reason to fail the call
            # (and an empty field is not UID 0).
            "name": parts[0], "uid": uid, "gid": gid,
            "home": parts[5], "shell": parts[6],
            "comment": parts[4] or None,
            "password_hash": shadow_map.get(parts[0]),
        }
        if uid is None:
            row["uid_raw"] = parts[2]
        if gid is None:
            row["gid_raw"] = parts[3]
        if uid is None or gid is None:
            malformed += 1
        out.append(row)
    return ev.result(out, malformed_lines=malformed)


def main() -> None:
    asyncio.run(sidecar.serve())


if __name__ == "__main__":
    main()
