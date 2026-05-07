"""Cinder Linux-artifact sidecar.

Pure Python — no external native libraries. Operates on a *root path* that's typically the
mount point of an examined Linux filesystem (or the live `/` for triage). All paths inside
the sidecar are relative to that root.

Methods (all return JSON-serializable dicts):
    shell_history(root)            -> {entries:[{user, shell, command, timestamp}]}
    auth_log(path)                 -> {entries:[{timestamp, host, process, message, user, remote_host}]}
    journalctl(journal_dir)        -> {entries:[{timestamp, unit, priority, message, user}]}
    cron(root)                     -> {entries:[{user, schedule, command, source}]}
    ssh_known_hosts(root)          -> {entries:[{user, host, key_type, fingerprint}]}
    ssh_authorized_keys(root)      -> {entries:[{user, key_type, comment, fingerprint}]}
    trash(root)                    -> {entries:[{user, original_path, size, deleted_at}]}
    recently_used(root)            -> {entries:[{user, uri, mime, modified, visited}]}
    systemd_units(root)            -> {entries:[{name, path, enabled, masked, state}]}
    package_logs(root)             -> {entries:[{timestamp, pm, action, package, version}]}
    passwd_shadow(root)            -> {entries:[{name, uid, gid, home, shell, comment, password_hash}]}
"""

from __future__ import annotations

import asyncio
import datetime as _dt
import hashlib
import os
import re
from pathlib import Path
from typing import Any

from shared.protocol import Sidecar

sidecar = Sidecar()


def _utc_iso(ts) -> str | None:
    if ts is None:
        return None
    if isinstance(ts, (int, float)):
        return _dt.datetime.fromtimestamp(ts, tz=_dt.timezone.utc).isoformat()
    if isinstance(ts, _dt.datetime):
        if ts.tzinfo is None:
            ts = ts.replace(tzinfo=_dt.timezone.utc)
        return ts.astimezone(_dt.timezone.utc).isoformat()
    return str(ts)


def _users_in(root: Path) -> list[tuple[str, Path]]:
    """Yield (username, home_dir) for each user with a home directory under <root>/home/* (and root)."""
    out: list[tuple[str, Path]] = []
    home = root / "home"
    if home.exists():
        for d in home.iterdir():
            if d.is_dir():
                out.append((d.name, d))
    rooth = root / "root"
    if rooth.exists() and rooth.is_dir():
        out.append(("root", rooth))
    return out


@sidecar.method("shell_history")
def shell_history(params: Any) -> dict[str, Any]:
    root = Path(params["root"])
    out: list[dict[str, Any]] = []
    for user, home in _users_in(root):
        for fname, shell in (
            (".bash_history", "bash"),
            (".zsh_history", "zsh"),
            (".fish_history", "fish"),
            (".sh_history", "sh"),
        ):
            f = home / fname
            if not f.exists():
                continue
            try:
                lines = f.read_text(encoding="utf-8", errors="replace").splitlines()
            except Exception:
                continue
            ts: float | None = None
            for line in lines:
                # Bash with HISTTIMEFORMAT prefixes "#<unix_ts>" lines
                if shell == "bash" and line.startswith("#") and line[1:].isdigit():
                    try:
                        ts = float(line[1:])
                    except Exception:
                        ts = None
                    continue
                # Zsh extended_history: ": <epoch>:<elapsed>;<cmd>"
                if shell == "zsh" and line.startswith(": "):
                    parts = line[2:].split(";", 1)
                    head = parts[0]
                    cmd = parts[1] if len(parts) > 1 else ""
                    try:
                        ts = float(head.split(":")[0])
                    except Exception:
                        ts = None
                    out.append({"user": user, "shell": shell, "command": cmd, "timestamp": _utc_iso(ts)})
                    continue
                # Fish stores - cmd: <cmd>\n  when: <epoch>
                if shell == "fish":
                    if line.startswith("- cmd: "):
                        out.append({"user": user, "shell": shell, "command": line[7:], "timestamp": None})
                    elif line.startswith("  when: ") and out and out[-1]["user"] == user and out[-1]["timestamp"] is None:
                        try:
                            out[-1]["timestamp"] = _utc_iso(float(line[8:]))
                        except Exception:
                            pass
                    continue
                if not line.strip():
                    continue
                out.append({"user": user, "shell": shell, "command": line, "timestamp": _utc_iso(ts)})
                ts = None
    return {"entries": out}


_AUTH_RE = re.compile(
    r"^(?P<ts>\w+\s+\d+\s+\d+:\d+:\d+|\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[\.\d]*[+\-Z\d:]*)\s+"
    r"(?P<host>\S+)\s+(?P<proc>[^\[\s:]+)(?:\[\d+\])?:\s*(?P<msg>.*)$"
)


@sidecar.method("auth_log")
def auth_log(params: Any) -> dict[str, Any]:
    p = Path(params["path"])
    out: list[dict[str, Any]] = []
    if not p.exists():
        return {"entries": out}
    year = _dt.datetime.now(tz=_dt.timezone.utc).year
    with p.open("r", encoding="utf-8", errors="replace") as f:
        for line in f:
            m = _AUTH_RE.match(line)
            if not m:
                continue
            ts_str = m.group("ts")
            try:
                if "T" in ts_str:
                    ts = _dt.datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                else:
                    ts = _dt.datetime.strptime(f"{year} {ts_str}", "%Y %b %d %H:%M:%S")
                    ts = ts.replace(tzinfo=_dt.timezone.utc)
            except Exception:
                ts = _dt.datetime.now(tz=_dt.timezone.utc)
            msg = m.group("msg")
            user = None
            remote = None
            for tag in (" user=", " for user ", " for "):
                if tag in msg:
                    user = msg.split(tag, 1)[1].split()[0].strip("'\"")
                    break
            for tag in (" from ",):
                if tag in msg:
                    remote = msg.split(tag, 1)[1].split()[0]
                    break
            out.append({
                "timestamp": _utc_iso(ts), "host": m.group("host"),
                "process": m.group("proc"), "message": msg,
                "user": user, "remote_host": remote,
            })
    return {"entries": out}


@sidecar.method("journalctl")
def journalctl(params: Any) -> dict[str, Any]:
    """systemd journals are a binary format. Cinder shells out to `journalctl --directory <dir>
    -o json` rather than parse the on-disk format directly. TODO 5.1: add a native parser so
    Cinder can read offline journals without a `journalctl` binary."""
    import subprocess
    journal_dir = params["journal_dir"]
    try:
        proc = subprocess.run(
            ["journalctl", "--directory", journal_dir, "-o", "json", "--no-pager", "--quiet"],
            capture_output=True, text=True, timeout=120, check=False,
        )
    except FileNotFoundError:
        return {"entries": [], "todo": "install journalctl or wait for native parser (5.1)"}
    out: list[dict[str, Any]] = []
    if proc.returncode != 0:
        return {"entries": out, "stderr": proc.stderr}
    for line in proc.stdout.splitlines():
        try:
            import json as _json
            j = _json.loads(line)
            ts_us = int(j.get("__REALTIME_TIMESTAMP", "0"))
            ts = _dt.datetime.fromtimestamp(ts_us / 1_000_000, tz=_dt.timezone.utc) if ts_us else None
            out.append({
                "timestamp": _utc_iso(ts),
                "unit": j.get("_SYSTEMD_UNIT", j.get("SYSLOG_IDENTIFIER", "")),
                "priority": j.get("PRIORITY", ""),
                "message": j.get("MESSAGE", ""),
                "user": j.get("_UID"),
            })
        except Exception:
            continue
    return {"entries": out}


@sidecar.method("cron")
def cron(params: Any) -> dict[str, Any]:
    root = Path(params["root"])
    out: list[dict[str, Any]] = []
    crontabs = [
        (root / "etc/crontab", "root", "system"),
        (root / "etc/anacrontab", "root", "system"),
    ]
    for crontab_dir in (root / "etc/cron.d", root / "var/spool/cron/crontabs", root / "var/spool/cron"):
        if crontab_dir.exists():
            for f in crontab_dir.iterdir():
                if f.is_file():
                    crontabs.append((f, f.name, str(f.relative_to(root)) if f.is_relative_to(root) else str(f)))
    for path, user, source in crontabs:
        if not path.exists():
            continue
        try:
            for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                parts = line.split(None, 5)
                if len(parts) >= 6:
                    schedule = " ".join(parts[:5])
                    cmd = parts[5]
                    out.append({"user": user, "schedule": schedule, "command": cmd, "source": source})
        except Exception:
            continue
    return {"entries": out}


def _ssh_fp(line: str) -> str:
    parts = line.split()
    if len(parts) < 2:
        return ""
    try:
        import base64 as _b64
        # Last field is the base64 key blob; sha256 fingerprint per RFC 4255
        key_blob = _b64.b64decode(parts[-1] + "==")
        h = hashlib.sha256(key_blob).digest()
        return "SHA256:" + _b64.b64encode(h).decode().rstrip("=")
    except Exception:
        return ""


@sidecar.method("ssh_known_hosts")
def ssh_known_hosts(params: Any) -> dict[str, Any]:
    root = Path(params["root"])
    out: list[dict[str, Any]] = []
    for user, home in _users_in(root):
        f = home / ".ssh/known_hosts"
        if not f.exists():
            continue
        for line in f.read_text(encoding="utf-8", errors="replace").splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            parts = line.split(None, 2)
            if len(parts) < 3:
                continue
            out.append({"user": user, "host": parts[0], "key_type": parts[1], "fingerprint": _ssh_fp(line)})
    return {"entries": out}


@sidecar.method("ssh_authorized_keys")
def ssh_authorized_keys(params: Any) -> dict[str, Any]:
    root = Path(params["root"])
    out: list[dict[str, Any]] = []
    for user, home in _users_in(root):
        f = home / ".ssh/authorized_keys"
        if not f.exists():
            continue
        for line in f.read_text(encoding="utf-8", errors="replace").splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            parts = line.split(None, 2)
            if len(parts) < 2:
                continue
            comment = parts[2] if len(parts) >= 3 else ""
            out.append({"user": user, "key_type": parts[0], "comment": comment, "fingerprint": _ssh_fp(line)})
    return {"entries": out}


@sidecar.method("trash")
def trash(params: Any) -> dict[str, Any]:
    root = Path(params["root"])
    out: list[dict[str, Any]] = []
    for user, home in _users_in(root):
        info_dir = home / ".local/share/Trash/info"
        if not info_dir.exists():
            continue
        for f in info_dir.glob("*.trashinfo"):
            try:
                txt = f.read_text(encoding="utf-8", errors="replace")
                orig = next((l[5:] for l in txt.splitlines() if l.startswith("Path=")), "")
                deleted = next((l[13:] for l in txt.splitlines() if l.startswith("DeletionDate=")), "")
                files_root = home / ".local/share/Trash/files"
                size = (files_root / f.stem).stat().st_size if (files_root / f.stem).exists() else 0
                try:
                    deleted_dt = _dt.datetime.fromisoformat(deleted).replace(tzinfo=_dt.timezone.utc)
                except Exception:
                    deleted_dt = None
                out.append({
                    "user": user, "original_path": orig, "size": size,
                    "deleted_at": _utc_iso(deleted_dt) if deleted_dt else None,
                })
            except Exception:
                continue
    return {"entries": out}


@sidecar.method("recently_used")
def recently_used(params: Any) -> dict[str, Any]:
    import xml.etree.ElementTree as ET
    root_path = Path(params["root"])
    out: list[dict[str, Any]] = []
    for user, home in _users_in(root_path):
        f = home / ".local/share/recently-used.xbel"
        if not f.exists():
            continue
        try:
            tree = ET.parse(f)
            for bm in tree.findall(".//bookmark"):
                out.append({
                    "user": user,
                    "uri": bm.get("href", ""),
                    "mime": bm.get("mime-type", None),
                    "modified": bm.get("modified", None),
                    "visited": bm.get("visited", None),
                })
        except Exception:
            continue
    return {"entries": out}


@sidecar.method("systemd_units")
def systemd_units(params: Any) -> dict[str, Any]:
    root = Path(params["root"])
    out: list[dict[str, Any]] = []
    for d in (root / "etc/systemd/system", root / "usr/lib/systemd/system", root / "lib/systemd/system"):
        if not d.exists():
            continue
        for f in d.rglob("*.service"):
            out.append({
                "name": f.name, "path": str(f),
                "enabled": (root / "etc/systemd/system/multi-user.target.wants" / f.name).exists(),
                "masked": f.is_symlink() and str(f.readlink()) == "/dev/null",
                "state": None,
            })
    return {"entries": out}


@sidecar.method("package_logs")
def package_logs(params: Any) -> dict[str, Any]:
    root = Path(params["root"])
    out: list[dict[str, Any]] = []

    apt_log = root / "var/log/apt/history.log"
    if apt_log.exists():
        for entry in apt_log.read_text(encoding="utf-8", errors="replace").split("\n\n"):
            if "Start-Date:" not in entry:
                continue
            ts_match = re.search(r"Start-Date:\s+(\S+\s+\S+)", entry)
            for action in ("Install", "Upgrade", "Remove"):
                am = re.search(rf"{action}: ([^\n]+)", entry)
                if am:
                    for pkg in am.group(1).split(", "):
                        name = pkg.split(":")[0].strip()
                        out.append({
                            "timestamp": _utc_iso(_dt.datetime.strptime(ts_match.group(1), "%Y-%m-%d %H:%M:%S").replace(tzinfo=_dt.timezone.utc)) if ts_match else None,
                            "pm": "apt", "action": action.lower(), "package": name, "version": None,
                        })

    dnf_log = root / "var/log/dnf.log"
    if dnf_log.exists():
        for line in dnf_log.read_text(encoding="utf-8", errors="replace").splitlines():
            if " INFO " in line and (" Installed:" in line or " Erased:" in line or " Upgrade:" in line):
                out.append({"timestamp": None, "pm": "dnf", "action": "log", "package": line[-200:], "version": None})

    pacman_log = root / "var/log/pacman.log"
    if pacman_log.exists():
        for line in pacman_log.read_text(encoding="utf-8", errors="replace").splitlines():
            m = re.match(r"\[(\S+)\] \[ALPM\] (installed|removed|upgraded|reinstalled) ([^\s]+) \(([^)]+)\)", line)
            if m:
                try:
                    ts = _dt.datetime.fromisoformat(m.group(1).replace("T", " ").replace("Z", "+00:00"))
                except Exception:
                    ts = None
                out.append({"timestamp": _utc_iso(ts), "pm": "pacman", "action": m.group(2), "package": m.group(3), "version": m.group(4)})
    return {"entries": out}


@sidecar.method("passwd_shadow")
def passwd_shadow(params: Any) -> dict[str, Any]:
    root = Path(params["root"])
    passwd = root / "etc/passwd"
    shadow = root / "etc/shadow"
    out: list[dict[str, Any]] = []
    if not passwd.exists():
        return {"entries": out}
    shadow_map: dict[str, str] = {}
    if shadow.exists():
        try:
            for line in shadow.read_text(encoding="utf-8", errors="replace").splitlines():
                parts = line.split(":")
                if len(parts) >= 2:
                    shadow_map[parts[0]] = parts[1]
        except Exception:
            pass
    for line in passwd.read_text(encoding="utf-8", errors="replace").splitlines():
        parts = line.split(":")
        if len(parts) < 7:
            continue
        out.append({
            "name": parts[0], "uid": int(parts[2] or 0), "gid": int(parts[3] or 0),
            "home": parts[5], "shell": parts[6],
            "comment": parts[4] or None,
            "password_hash": shadow_map.get(parts[0]),
        })
    return {"entries": out}


def main() -> None:
    asyncio.run(sidecar.serve())


if __name__ == "__main__":
    main()
