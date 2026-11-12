using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cinder.App.Services;
using Cinder.App.ViewModels;
using Cinder.Core.Cases;
using Cinder.Core.Custody;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

[Collection(ShellNoticeCollection.Name)]
public class CrashPolicyTests
{
    public static TheoryData<Exception> Operational => new()
    {
        new IOException("The process cannot access the file because it is being used by another process."),
        new FileNotFoundException(),
        new InvalidDataException(),
        new UnauthorizedAccessException(),
        new OperationCanceledException(),
        new TaskCanceledException(),
        new TimeoutException(),
        new RegexMatchTimeoutException(),
        new ObjectDisposedException("buffer"),
        new FormatException(),
        new JsonException(),
        new Win32Exception(2),
        new SettingsUnreadableException("settings.json", "bad"),
        new AggregateException(new IOException(), new TimeoutException()),
    };

    public static TheoryData<Exception> Defects => new()
    {
        new NullReferenceException(),
        new InvalidOperationException("Call from invalid thread"),
        new InvalidCastException(),
        new ArgumentNullException("x"),
        new IndexOutOfRangeException(),
        new KeyNotFoundException(),
        new OutOfMemoryException(),
        new NotImplementedException(),
        new AggregateException(new IOException(), new NullReferenceException()),
        new AggregateException(),
    };

    [Theory]
    [MemberData(nameof(Operational))]
    public void A_failed_operation_is_survivable(Exception ex) =>
        CrashHandler.IsRecoverable(ex).Should().BeTrue();

    [Theory]
    [MemberData(nameof(Defects))]
    public void A_defect_is_not_swallowed(Exception ex) =>
        CrashHandler.IsRecoverable(ex).Should().BeFalse();

    [Fact]
    public void Descriptions_use_the_innermost_message_as_a_sentence()
    {
        CrashHandler.Describe(new IOException("Disk full")).Should().Be("Disk full.");
        CrashHandler.Describe(new AggregateException(new IOException("Access denied."))).Should().Be("Access denied.");
        CrashHandler.Describe(new InvalidOperationException("")).Should().Be("InvalidOperationException.");
    }

    [Fact]
    public void A_file_that_will_not_open_is_explained_briefly_without_repeating_its_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cinder-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var locked = Path.Combine(dir, "locked.bin");
        File.WriteAllText(locked, "x");
        try
        {
            MainWindowViewModel.DescribeOpenFailure(new FileNotFoundException("gone", locked), locked)
                .Should().Be("it no longer exists.");
            MainWindowViewModel.DescribeOpenFailure(new UnauthorizedAccessException(), dir)
                .Should().Be("it is a folder, not a file.");
            MainWindowViewModel.DescribeOpenFailure(new UnauthorizedAccessException(), locked)
                .Should().Be("access was denied.");
            MainWindowViewModel.DescribeOpenFailure(new InvalidDataException("odd"), locked).Should().Be("odd");

            if (OperatingSystem.IsWindows())
            {
                // The real exception, from a real sharing violation.
                using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var failure = FluentActions.Invoking(() => new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
                    .Should().Throw<IOException>().Which;
                MainWindowViewModel.DescribeOpenFailure(failure, locked)
                    .Should().StartWith("it is in use by another program").And.NotContain(dir);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_notice_with_no_shell_attached_or_a_throwing_sink_never_throws()
    {
        var previous = ShellNotice.ErrorSink;
        try
        {
            ShellNotice.ErrorSink = null;
            FluentActions.Invoking(() => ShellNotice.Error("nobody is listening")).Should().NotThrow();

            ShellNotice.ErrorSink = _ => throw new InvalidOperationException("sink failed");
            FluentActions.Invoking(() => ShellNotice.Error("sink throws")).Should().NotThrow();
        }
        finally
        {
            ShellNotice.ErrorSink = previous;
        }
    }
}

[Collection(ShellNoticeCollection.Name)]
public class CaseCreationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-cases-" + Guid.NewGuid().ToString("N"));

    public CaseCreationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Case stores pool their connections; release the files before deleting them.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_new_path_is_accepted()
    {
        CommandRegistration.ExistingFileRefusal(Path.Combine(_dir, "new.cinder")).Should().BeNull();
    }

    [Fact]
    public void A_zero_length_placeholder_left_by_a_save_dialog_is_accepted()
    {
        var path = Path.Combine(_dir, "placeholder.cinder");
        File.WriteAllBytes(path, []);
        CommandRegistration.ExistingFileRefusal(path).Should().BeNull();
    }

    [Fact]
    public async Task An_existing_case_file_is_refused_with_a_message_that_says_why()
    {
        var path = Path.Combine(_dir, "existing.cinder");
        var store = new CaseStore(path);
        await new CaseService(store, new CustodyLog(store)).CreateAsync("Existing case", "examiner", null);
        var before = new FileInfo(path).Length;

        var refusal = CommandRegistration.ExistingFileRefusal(path);

        refusal.Should().NotBeNull();
        refusal.Should().Contain("existing.cinder").And.Contain("already exists").And.Contain("will not create a case over an existing file");
        new FileInfo(path).Length.Should().Be(before);
    }

    [Fact]
    public void A_folder_or_any_other_existing_file_is_refused_too()
    {
        CommandRegistration.ExistingFileRefusal(_dir).Should().Contain("folder");

        var other = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(other, "not a case");
        CommandRegistration.ExistingFileRefusal(other).Should().Contain("already exists");
    }

    [Fact]
    public async Task Custody_entries_go_to_the_case_the_work_started_under_not_the_one_active_now()
    {
        var pathA = Path.Combine(_dir, "a.cinder");
        var pathB = Path.Combine(_dir, "b.cinder");
        var storeA = new CaseStore(pathA);
        var storeB = new CaseStore(pathB);
        var caseA = await new CaseService(storeA, new CustodyLog(storeA)).CreateAsync("Case A", "examiner", null);
        var caseB = await new CaseService(storeB, new CustodyLog(storeB)).CreateAsync("Case B", "examiner", null);
        var sessionA = new CaseSession(caseA.Id, caseA.Name, caseA.Examiner, pathA, DateTimeOffset.UtcNow);
        var beforeB = (await new CustodyLog(storeB).ListAsync(caseB.Id)).Count;

        // The examiner started a parse under case A; by the time it finishes, something else
        // (here: nothing at all) is the active case. The entry still belongs to A.
        await ActiveCaseContext.LogAsync(sessionA, CustodyAction.ParserRan, new { Tool = "test", Evidence = "disk.E01" });

        var entriesA = await new CustodyLog(storeA).ListAsync(caseA.Id);
        entriesA.Should().Contain(e => e.Action == CustodyAction.ParserRan && e.DetailsJson.Contains("disk.E01"));
        (await new CustodyLog(storeB).ListAsync(caseB.Id)).Should().HaveCount(beforeB);
    }

    [Fact]
    public async Task Work_started_with_no_case_open_logs_nothing_and_does_not_throw()
    {
        var act = () => ActiveCaseContext.LogAsync(session: null, CustodyAction.ParserRan, new { Tool = "test" });
        await act.Should().NotThrowAsync();

        var gone = new CaseSession(Guid.NewGuid(), "Gone", "examiner", Path.Combine(_dir, "deleted.cinder"), DateTimeOffset.UtcNow);
        var missing = () => ActiveCaseContext.LogAsync(gone, CustodyAction.ParserRan, new { Tool = "test" });
        await missing.Should().NotThrowAsync();
        File.Exists(gone.Path).Should().BeFalse("logging must not create a case file that was not there");
    }
}
