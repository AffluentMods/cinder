using Cinder.App.Services;
using Cinder.App.ViewModels;
using Cinder.Core.Cases;
using Cinder.Core.Custody;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>
/// <see cref="ShellNotice.ErrorSink"/> is one sink for the whole process. Test classes that
/// replace it, or that provoke a notice, share this collection so they run one after another
/// and never read each other's notices.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ShellNoticeCollection
{
    public const string Name = "Shell notice (process-wide sink)";
}

/// <summary>
/// A custody entry that could not be written is a hole in the record. The tool doing the work
/// must not fail because of it, and the examiner must be told — once per run of failures.
/// </summary>
[Collection(ShellNoticeCollection.Name)]
public class CustodyFailureNoticeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-custody-" + Guid.NewGuid().ToString("N"));
    private readonly Action<string>? _previousSink = ShellNotice.ErrorSink;
    private readonly List<string> _notices = [];

    public CustodyFailureNoticeTests()
    {
        Directory.CreateDirectory(_dir);
        ActiveCaseContext.ResetFailureTracking();
        ShellNotice.ErrorSink = message =>
        {
            lock (_notices)
            {
                _notices.Add(message);
            }
        };
    }

    public void Dispose()
    {
        ShellNotice.ErrorSink = _previousSink;
        ActiveCaseContext.ResetFailureTracking();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly object Details = new { Tool = "test", Evidence = "disk.E01" };

    /// <summary>Two pages of text: unmistakably a file, unmistakably not a SQLite database.</summary>
    private static readonly string NotADatabase = new('x', 8192);

    private CaseSession Session(string fileName, string name = "Operation Ember") =>
        new(Guid.NewGuid(), name, "examiner", Path.Combine(_dir, fileName), DateTimeOffset.UtcNow);

    private async Task<CaseSession> RealCaseAsync(string fileName, string name)
    {
        var path = Path.Combine(_dir, fileName);
        var store = new CaseStore(path);
        var created = await new CaseService(store, new CustodyLog(store)).CreateAsync(name, "examiner", null);
        return new CaseSession(created.Id, created.Name, created.Examiner, path, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_run_of_failed_writes_is_announced_once_and_never_thrown()
    {
        var session = Session("not-a-database.cinder");
        File.WriteAllText(session.Path!, NotADatabase);

        for (int i = 0; i < 5; i++)
        {
            var act = () => ActiveCaseContext.LogAsync(session, CustodyAction.ParserRan, Details);
            (await act.Should().NotThrowAsync("custody bookkeeping must not fail the work it describes"))
                .Which.Should().BeFalse("the entry was not written");
        }

        var notice = _notices.Should().ContainSingle("five lost entries are one event for the examiner, not five").Subject;
        notice.Should().Contain("NOT being recorded").And.Contain("Operation Ember");
    }

    [Fact]
    public async Task A_case_file_that_has_gone_away_is_a_failure_not_a_silent_no_op()
    {
        var session = Session("unplugged.cinder");

        (await ActiveCaseContext.LogAsync(session, CustodyAction.ParserRan, Details)).Should().BeFalse();
        (await ActiveCaseContext.LogAsync(session, CustodyAction.DataExported, Details)).Should().BeFalse();

        _notices.Should().ContainSingle().Which.Should().Contain("no longer there").And.Contain("unplugged.cinder");
        File.Exists(session.Path).Should().BeFalse("logging must not create a case file that was not there");
    }

    [Fact]
    public async Task Recovery_is_announced_with_the_number_lost_and_a_later_failure_is_announced_afresh()
    {
        // 1. The case file is not there (say, its drive is unplugged): three entries are lost.
        var path = Path.Combine(_dir, "comes-back.cinder");
        var missing = new CaseSession(Guid.NewGuid(), "Comes Back", "examiner", path, DateTimeOffset.UtcNow);
        for (int i = 0; i < 3; i++)
        {
            (await ActiveCaseContext.LogAsync(missing, CustodyAction.ParserRan, Details)).Should().BeFalse();
        }
        _notices.Should().ContainSingle();

        // 2. It comes back. The next entry is written, and the examiner learns what was missed.
        var real = await RealCaseAsync("comes-back.cinder", "Comes Back");
        (await ActiveCaseContext.LogAsync(real, CustodyAction.ParserRan, Details)).Should().BeTrue();
        _notices.Should().HaveCount(2);
        _notices[1].Should().Contain("recorded again").And.Contain("3 entries were not written");

        // 3. Further successes say nothing.
        (await ActiveCaseContext.LogAsync(real, CustodyAction.DataExported, Details)).Should().BeTrue();
        _notices.Should().HaveCount(2);
        (await new CustodyLog(new CaseStore(path)).ListAsync(real.Id))
            .Select(e => e.Action).Should().Contain(new[] { CustodyAction.ParserRan, CustodyAction.DataExported });

        // 4. It breaks again: that is a new run of failures, announced again, once.
        SqliteConnection.ClearAllPools();
        foreach (var leftover in Directory.GetFiles(_dir, "comes-back.cinder*"))
        {
            File.Delete(leftover);
        }
        File.WriteAllText(path, NotADatabase);
        (await ActiveCaseContext.LogAsync(real, CustodyAction.ParserRan, Details)).Should().BeFalse();
        (await ActiveCaseContext.LogAsync(real, CustodyAction.ParserRan, Details)).Should().BeFalse();
        _notices.Should().HaveCount(3);
        _notices[2].Should().Contain("NOT being recorded");
    }

    [Fact]
    public async Task Each_case_has_its_own_run_of_failures()
    {
        var first = Session("first-missing.cinder", "First");
        var second = Session("second-missing.cinder", "Second");

        await ActiveCaseContext.LogAsync(first, CustodyAction.ParserRan, Details);
        await ActiveCaseContext.LogAsync(second, CustodyAction.ParserRan, Details);
        await ActiveCaseContext.LogAsync(first, CustodyAction.ParserRan, Details);
        await ActiveCaseContext.LogAsync(second, CustodyAction.ParserRan, Details);

        _notices.Should().HaveCount(2);
        _notices.Should().Contain(n => n.Contains("\"First\"")).And.Contain(n => n.Contains("\"Second\""));
    }

    [Fact]
    public async Task Working_outside_a_case_is_not_a_failure()
    {
        (await ActiveCaseContext.LogAsync(session: null, CustodyAction.ParserRan, Details)).Should().BeFalse();
        var noFile = new CaseSession(Guid.NewGuid(), "Scratch", "examiner", Path: null, DateTimeOffset.UtcNow);
        (await ActiveCaseContext.LogAsync(noFile, CustodyAction.ParserRan, Details)).Should().BeFalse();

        _notices.Should().BeEmpty();
    }

    [Fact]
    public async Task A_healthy_case_is_written_without_any_notice()
    {
        var session = await RealCaseAsync("healthy.cinder", "Healthy");

        (await ActiveCaseContext.LogAsync(session, CustodyAction.ParserRan, Details)).Should().BeTrue();

        _notices.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sink_that_throws_still_cannot_reach_the_tool()
    {
        ShellNotice.ErrorSink = _ => throw new InvalidOperationException("the status bar is gone");
        var session = Session("unplugged-too.cinder");

        var act = () => ActiveCaseContext.LogAsync(session, CustodyAction.ParserRan, Details);

        await act.Should().NotThrowAsync();
    }
}
