using Cinder.Search;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

public sealed class MitreTaggerTests
{
    [Theory]
    [InlineData("[Security] Microsoft-Windows-Security-Auditing EventID=4624 — An account was successfully logged on", "T1078")]
    [InlineData("[Security] Microsoft-Windows-Security-Auditing EventID=4698 — A scheduled task was created", "T1053.005")]
    [InlineData("[System] Service Control Manager EventID=7045 — A service was installed", "T1543.003")]
    [InlineData("[Security] Microsoft-Windows-Security-Auditing EventID=4720", "T1136.001")]
    [InlineData("[Security] Microsoft-Windows-Eventlog EventID=1102 — The audit log was cleared", "T1070.001")]
    [InlineData("[Security] EventID=5145 — A network share object was checked", "T1021.002")]
    [InlineData("[Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational] EventID=1149", "T1021.001")]
    public void Well_known_security_events_map_to_their_technique(string summary, string expected)
        => MitreTagger.Tag("evtx.security", summary).Should().Contain(expected);

    [Fact]
    public void Failed_logon_is_both_valid_accounts_and_brute_force()
    {
        var tags = MitreTagger.Tag("evtx.security", "[Security] EventID=4625 — An account failed to log on");
        tags.Should().Contain("T1078").And.Contain("T1110");
    }

    [Fact]
    public void PowerShell_script_block_logging_maps_to_the_PowerShell_technique()
    {
        MitreTagger.Tag("evtx.powershell", "[Microsoft-Windows-PowerShell/Operational] EventID=4104 — Creating Scriptblock text")
            .Should().Equal("T1059.001");
    }

    [Fact]
    public void Sysmon_event_ids_are_not_confused_with_security_event_ids()
    {
        // Sysmon 8 is CreateRemoteThread; Security 8 does not exist. Same number, different channel.
        MitreTagger.Tag("evtx.sysmon", "[Microsoft-Windows-Sysmon/Operational] EventID=8 — CreateRemoteThread")
            .Should().Equal("T1055");
        MitreTagger.Tag("evtx.sysmon", "[Microsoft-Windows-Sysmon/Operational] EventID=10 — ProcessAccess lsass.exe")
            .Should().Equal("T1003.001");
        MitreTagger.Tag("evtx.sysmon", "[Microsoft-Windows-Sysmon/Operational] EventID=10 — ProcessAccess notepad.exe")
            .Should().BeEmpty();
    }

    [Fact]
    public void Generic_process_creation_is_not_tagged()
    {
        // 4688 on its own proves a process started, not that any technique was used.
        MitreTagger.Tag("evtx.security", "[Security] EventID=4688 — A new process has been created").Should().BeEmpty();
    }

    [Fact]
    public void Execution_evidence_gets_the_execution_tactic()
    {
        MitreTagger.Tag("prefetch", "Executed: MIMIKATZ.EXE").Should().Equal("TA0002");
        MitreTagger.Tag("registry.userassist", "UserAssist: cmd.exe").Should().Equal("TA0002");
    }

    [Fact]
    public void Recycle_bin_deletion_is_file_deletion()
        => MitreTagger.Tag("recyclebin", "Deleted: C:\\Users\\x\\secret.docx").Should().Equal("T1070.004");

    [Fact]
    public void Sources_without_a_defensible_mapping_get_no_tag()
    {
        MitreTagger.Tag("email", "Email: invoice (from a@b.c)").Should().BeEmpty();
        MitreTagger.Tag("browser.history", "https://example.com").Should().BeEmpty();
        MitreTagger.Tag("lnk.created", "x").Should().BeEmpty();
    }

    [Fact]
    public void Malformed_summaries_never_throw()
    {
        MitreTagger.Tag("evtx.x", "").Should().BeEmpty();
        MitreTagger.Tag("evtx.x", "EventID=notanumber").Should().BeEmpty();
        MitreTagger.Tag("", "anything").Should().BeEmpty();
    }

    [Fact]
    public void Describe_expands_known_ids_and_passes_unknown_ones_through()
    {
        MitreTagger.Describe("T1078").Should().Be("T1078 Valid Accounts");
        MitreTagger.Describe("T9999").Should().Be("T9999");
    }
}
