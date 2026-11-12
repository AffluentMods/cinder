using Cinder.App.Services;
using Cinder.App.ViewModels;
using Cinder.App.ViewModels.Tools;
using Cinder.Core.Cases;
using Cinder.Core.Custody;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>Tests that change the process-wide active case run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ActiveCaseCollection
{
    public const string Name = "Active case";
}

/// <summary>
/// Bookmarks and report exhibits belong to one case. These tests switch the active case under
/// the Bookmarks tool and the report builder and check that neither can show, delete or export
/// another case's findings.
/// </summary>
[Collection(ActiveCaseCollection.Name)]
public sealed class CaseBoundListsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-tests-" + Guid.NewGuid().ToString("N"));

    public CaseBoundListsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        ActiveCaseContext.Set(null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A case file still held open by a background write is not a test failure.
        }
    }

    /// <summary>A case file on disk with the given bookmark titles. Each file numbers its bookmarks from 1.</summary>
    private async Task<CaseSession> NewCaseAsync(string name, params string[] bookmarkTitles)
    {
        var path = Path.Combine(_dir, name + ".cinder");
        var store = new CaseStore(path);
        var created = await new CaseService(store, new CustodyLog(store)).CreateAsync(name, "examiner", null);
        var bookmarks = new BookmarkStore(store);
        foreach (var title in bookmarkTitles)
        {
            await bookmarks.AddAsync(created.Id, "examiner", "registry", @"E:\evidence\NTUSER.DAT", title, "note", """{"Key":"Run"}""");
        }
        return new CaseSession(created.Id, name, "examiner", path, DateTimeOffset.UtcNow);
    }

    private static Task<IReadOnlyList<Bookmark>> StoredAsync(CaseSession session) =>
        new BookmarkStore(new CaseStore(session.Path!)).ListAsync(session.Id);

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Bookmarks_load_without_a_click_and_follow_the_open_case_Async()
    {
        var a = await NewCaseAsync("alpha", "alpha finding 1", "alpha finding 2");
        var b = await NewCaseAsync("bravo", "bravo finding");
        ActiveCaseContext.Set(a);

        var tool = new BookmarksTool();
        await UntilAsync(() => tool.Bookmarks.Count == 2);
        tool.Bookmarks.Select(x => x.Title).Should().Equal("alpha finding 1", "alpha finding 2");

        ActiveCaseContext.Set(b);
        await UntilAsync(() => tool.Bookmarks.Count == 1);
        tool.Bookmarks.Should().ContainSingle().Which.Title.Should().Be("bravo finding");
        tool.Selected.Should().BeNull("a selection made in the other case does not carry over");

        ActiveCaseContext.Set(null);
        await UntilAsync(() => tool.Bookmarks.Count == 0);
        tool.Bookmarks.Should().BeEmpty();
        tool.StatusLine.Should().Be("Open a case to see its bookmarks.");
    }

    [Fact]
    public async Task A_bookmark_from_another_case_cannot_be_deleted_from_the_open_one_Async()
    {
        var a = await NewCaseAsync("alpha", "alpha finding");
        var b = await NewCaseAsync("bravo", "bravo finding");
        ActiveCaseContext.Set(a);
        var tool = new BookmarksTool();
        await tool.ReloadAsync(CancellationToken.None);
        var fromAlpha = tool.Bookmarks.Single();

        ActiveCaseContext.Set(b);
        await tool.ReloadAsync(CancellationToken.None);
        // Both files number their bookmarks from 1, so alpha's #1 and bravo's #1 share an id.
        // This is the stale row an examiner could still have selected.
        fromAlpha.Id.Should().Be(tool.Bookmarks.Single().Id);
        tool.Selected = fromAlpha;

        await tool.DeleteSelectedCommand.ExecuteAsync(null);

        (await StoredAsync(b)).Should().ContainSingle("bravo's bookmark has the same id but is not the one that was selected");
        (await StoredAsync(a)).Should().ContainSingle();
        tool.StatusLine.Should().Contain("different case");
    }

    [Fact]
    public async Task Deleting_a_bookmark_of_the_open_case_removes_it_from_that_case_only_Async()
    {
        var a = await NewCaseAsync("alpha", "alpha finding");
        var b = await NewCaseAsync("bravo", "bravo finding");
        ActiveCaseContext.Set(b);
        var tool = new BookmarksTool();
        await tool.ReloadAsync(CancellationToken.None);
        tool.Selected = tool.Bookmarks.Single();

        await tool.DeleteSelectedCommand.ExecuteAsync(null);

        tool.Bookmarks.Should().BeEmpty();
        (await StoredAsync(b)).Should().BeEmpty();
        (await StoredAsync(a)).Should().ContainSingle();
        tool.StatusLine.Should().StartWith("Deleted bookmark");
    }

    [Fact]
    public async Task Report_exhibits_come_only_from_the_open_case_Async()
    {
        var a = await NewCaseAsync("alpha", "alpha finding");
        var b = await NewCaseAsync("bravo");
        ActiveCaseContext.Set(a);
        var reports = new ReportsToolViewModel(() => ActiveCaseContext.Current?.Name ?? "Untitled case");
        await reports.SyncBookmarksAsync(CancellationToken.None);
        reports.PreviewMarkdown.Should().Contain("alpha finding").And.Contain("Exhibits");

        ActiveCaseContext.Set(b);

        // Straight away, before any reload has had time to finish: alpha's findings are gone.
        reports.RefreshCommand.Execute(null);
        reports.PreviewMarkdown.Should().NotContain("alpha finding");
        reports.Bookmarks.Should().BeEmpty();

        await reports.SyncBookmarksAsync(CancellationToken.None);
        reports.PreviewMarkdown.Should().NotContain("alpha finding");
        reports.BookmarkSummary.Should().StartWith("No bookmarks in bravo");
    }

    [Fact]
    public async Task Report_exhibits_load_when_the_case_opens_Async()
    {
        var a = await NewCaseAsync("alpha", "alpha finding");
        var reports = new ReportsToolViewModel(() => "alpha");
        reports.Bookmarks.Should().BeEmpty();

        ActiveCaseContext.Set(a);
        await UntilAsync(() => reports.Bookmarks.Count == 1);

        reports.Bookmarks.Should().ContainSingle();
        reports.BookmarkSummary.Should().Be("1 bookmark from alpha");
    }

    [Fact]
    public void Changing_the_template_keeps_what_was_written()
    {
        var reports = new ReportsToolViewModel(() => "case");
        var templates = reports.Templates;
        templates.Count.Should().BeGreaterThan(1);
        var kept = reports.Sections[0];
        kept.Body = "Text the examiner wrote.";
        var keptTitle = kept.Title;

        reports.SelectedTemplate = templates[1];

        reports.Sections.Should().Contain(s => s.Title == keptTitle && s.Body == "Text the examiner wrote.",
            "switching template must not discard a section that has text in it");
        reports.Sections.Select(s => s.Title).Should().Contain(templates[1].DefaultSections);
    }
}
