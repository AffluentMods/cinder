using Cinder.App.Services;
using Cinder.App.ViewModels;
using Cinder.App.ViewModels.Tools;
using Cinder.Core.Cases;
using Cinder.Core.Custody;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>
/// The Chain of custody tool checking the case that is open: it must say "intact" for an
/// untouched log, name the entry when one has been altered, and say so when no case is open.
/// </summary>
[Collection(ActiveCaseCollection.Name)]
public sealed class CustodyToolFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-custody-" + Guid.NewGuid().ToString("N"));

    public CustodyToolFlowTests()
    {
        Directory.CreateDirectory(_dir);
        ActiveCaseContext.Set(null);
    }

    public void Dispose()
    {
        ActiveCaseContext.Set(null);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A case file still held open by a background write is not a test failure.
        }
    }

    private async Task<CaseSession> NewCaseAsync(int extraEntries)
    {
        var path = Path.Combine(_dir, "case.cinder");
        var store = new CaseStore(path);
        var log = new CustodyLog(store);
        var created = await new CaseService(store, log).CreateAsync("Demo", "examiner", null);
        for (var i = 0; i < extraEntries; i++)
        {
            await log.AppendAsync(created.Id, "examiner", CustodyAction.ParserRan, $$"""{"Tool":"registry","Rows":{{i}}}""");
        }
        return new CaseSession(created.Id, "Demo", "examiner", path, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task With_no_case_open_it_says_so_and_checks_nothing()
    {
        var tool = new CustodyTool();
        tool.HasOpenCase.Should().BeFalse();

        await tool.VerifyOpenCaseCommand.ExecuteAsync(null);

        tool.StatusLine.Should().StartWith("No case is open.");
        tool.Verdict.Should().BeNull();
        tool.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task An_untouched_log_is_reported_intact_with_every_entry_listed()
    {
        var session = await NewCaseAsync(extraEntries: 3);
        var tool = new CustodyTool();
        ActiveCaseContext.Set(session);
        tool.HasOpenCase.Should().BeTrue("the tool follows the active case");

        await tool.VerifyOpenCaseCommand.ExecuteAsync(null);

        tool.VerdictIsOk.Should().BeTrue();
        tool.VerdictIsBroken.Should().BeFalse();
        tool.Verdict.Should().StartWith("Log intact:");
        tool.Entries.Count.Should().BeGreaterThanOrEqualTo(4, "case creation plus the three appended entries");
        tool.CasePath.Should().Be(session.Path);
        tool.AttestationVerdict.Should().StartWith("Not signed yet.");
    }

    [Fact]
    public async Task An_altered_entry_is_reported_as_altered_and_named()
    {
        var session = await NewCaseAsync(extraEntries: 3);
        // Change what one entry says, the way someone editing the case file would.
        SqliteConnection.ClearAllPools();
        using (var conn = new SqliteConnection($"Data Source={session.Path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE custody_entries SET details_json = '{\"Tool\":\"registry\",\"Rows\":999}' WHERE sequence = 2;";
            cmd.ExecuteNonQuery().Should().Be(1);
        }
        var tool = new CustodyTool();
        ActiveCaseContext.Set(session);

        await tool.VerifyOpenCaseCommand.ExecuteAsync(null);

        tool.VerdictIsBroken.Should().BeTrue();
        tool.VerdictIsOk.Should().BeFalse();
        tool.Verdict.Should().StartWith("Log altered: the chain breaks at entry 2");
    }

    [Fact]
    public async Task Signing_then_checking_reports_a_verified_signature()
    {
        var session = await NewCaseAsync(extraEntries: 1);
        var tool = new CustodyTool();
        ActiveCaseContext.Set(session);
        await tool.VerifyOpenCaseCommand.ExecuteAsync(null);

        await tool.SignTipCommand.ExecuteAsync(null);

        tool.StatusLine.Should().StartWith("Signed: the log up to entry");
        tool.AttestationVerdict.Should().StartWith("1 signature verify");
        tool.KeyFingerprint.Should().NotBeNullOrEmpty();
    }
}
