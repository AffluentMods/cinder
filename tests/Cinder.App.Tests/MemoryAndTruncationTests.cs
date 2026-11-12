using Cinder.App.Services;
using Cinder.App.ViewModels.Tools;
using Cinder.Core.Export;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>
/// The Memory tool's handling of Volatility's nested output, and the truncation flags of the
/// parsers that used to cut their results short without saying so.
/// </summary>
public class MemoryAndTruncationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-tests-" + Guid.NewGuid().ToString("N"));

    public MemoryAndTruncationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that outlives the test run is not a test failure.
        }
    }

    // ---------------------------------------------------------------- Volatility output

    private const string PsTreeJson = """
        [
          {"PID": 4, "PPID": 0, "ImageFileName": "System", "Threads": 150, "CreateTime": "2024-01-01T00:00:00", "ExitTime": null,
           "__children": [
             {"PID": 400, "PPID": 4, "ImageFileName": "smss.exe", "Threads": 2, "__children": [
               {"PID": 500, "PPID": 400, "ImageFileName": "csrss.exe", "Threads": 9, "__children": []}
             ]}
           ]},
          {"PID": 900, "PPID": 700, "ImageFileName": "explorer.exe", "Threads": 60, "__children": [
             {"PID": 1234, "PPID": 900, "ImageFileName": "evil.exe", "Threads": 3, "__children": []}
          ]}
        ]
        """;

    [Fact]
    public void Nested_children_are_flattened_in_tree_order_with_their_depth()
    {
        var rows = Vol3Runner.ParseRows(PsTreeJson);

        // Reading only the top-level array would give 2 processes and lose evil.exe.
        rows.Select(r => r["PID"]).Should().Equal(4L, 400L, 500L, 900L, 1234L);
        rows.Select(r => r[Vol3Runner.DepthKey]).Should().Equal(0L, 1L, 2L, 0L, 1L);
        rows.Should().OnlyContain(r => !r.ContainsKey("__children"));
        rows[0]["ExitTime"].Should().BeNull();
    }

    [Fact]
    public void Output_that_is_not_json_is_an_error_and_an_empty_list_is_empty()
    {
        Vol3Runner.ParseRows("[]").Should().BeEmpty();
        var act = () => Vol3Runner.ParseRows("Volatility 3 Framework 2.7.0\nProgress: 100.00");
        act.Should().Throw<System.Text.Json.JsonException>();
    }

    [Fact]
    public void Memory_grid_rows_keep_the_tree_readable()
    {
        var rows = MemoryTool.ToRows(Vol3Runner.ParseRows(PsTreeJson));

        rows.Should().HaveCount(5);
        var evil = TabularExporter.ToDictionary(rows[4]);
        evil["Pid"].Should().Be("1234");
        evil["ParentPid"].Should().Be("900");
        evil["Depth"].Should().Be("1");
        evil["Name"].Should().Be("  evil.exe", "the name is indented by its depth");
        TabularExporter.ToDictionary(rows[0])["Name"].Should().Be("System");
    }

    [Fact]
    public void Tail_of_an_error_stream_is_what_gets_shown()
    {
        Vol3Runner.LastLines("a\nb\nc\nd\n", 2).Should().Be("c d");
        Vol3Runner.LastLines(null).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Budgets

    [Fact]
    public void A_source_with_more_rows_than_its_budget_is_reported_as_cut_short()
    {
        var into = new List<object>();
        RowBudgets.Take(Enumerable.Range(0, 11), budget: 10, into, i => i).Should().BeTrue();
        into.Should().HaveCount(10);
    }

    [Fact]
    public void A_source_with_exactly_its_budget_is_complete()
    {
        var into = new List<object>();
        RowBudgets.Take(Enumerable.Range(0, 10), budget: 10, into, i => i).Should().BeFalse();
        into.Should().HaveCount(10);
    }

    [Fact]
    public void Each_source_gets_its_own_budget_so_a_large_one_cannot_starve_the_next()
    {
        // The SRUM bug: one shared 25,000 cap meant a full first table left nothing for the others.
        var into = new List<object>();
        var first = RowBudgets.Take(Enumerable.Range(0, 1000), budget: 10, into, i => $"app{i}");
        var second = RowBudgets.Take(Enumerable.Range(0, 3), budget: 10, into, i => $"net{i}");

        first.Should().BeTrue();
        second.Should().BeFalse();
        into.Should().HaveCount(13).And.Contain("net2");
    }

    [Fact]
    public void Items_that_map_to_nothing_do_not_use_up_the_budget()
    {
        var into = new List<object>();
        RowBudgets.Take(Enumerable.Range(0, 20), budget: 5, into, i => i % 2 == 0 ? i : null).Should().BeTrue();
        into.Should().Equal(0, 2, 4, 6, 8);
    }

    [Fact]
    public void Parse_result_flags_truncation_when_the_budget_was_reached()
    {
        ParseResult.WithBudget(new object[5], 5).Truncated.Should().BeTrue();
        ParseResult.WithBudget(new object[4], 5).Truncated.Should().BeFalse();
    }

    [Fact]
    public void Truncation_notice_never_promises_that_export_has_the_rest()
    {
        var notice = SidecarToolViewModel.DescribeTruncation(200_000, null);

        notice.Should().Contain("200,000").And.Contain("not the whole artifact");
        notice.Should().Contain("Export and bookmarks cover only the rows shown");
        notice.Should().NotContain("export to see").And.NotContain("export to get");
        SidecarToolViewModel.DescribeTruncation(25_000, 61_234).Should().Contain("of 61,234");
    }

    // ---------------------------------------------------------------- Browser history

    private string NewChromiumHistory(string relativePath, int urls, bool leaveInWal = false)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = (leaveInWal ? "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; " : "") +
                              "CREATE TABLE urls (id INTEGER PRIMARY KEY, url TEXT, title TEXT, visit_count INTEGER, last_visit_time INTEGER);";
            cmd.ExecuteNonQuery();
        }
        using (var tx = conn.BeginTransaction())
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO urls (url, title, visit_count, last_visit_time) VALUES ($u, $t, 1, $v);";
            var u = cmd.Parameters.Add("$u", SqliteType.Text);
            var t = cmd.Parameters.Add("$t", SqliteType.Text);
            var v = cmd.Parameters.Add("$v", SqliteType.Integer);
            for (var i = 0; i < urls; i++)
            {
                u.Value = $"https://example.com/page{i}";
                t.Value = $"Page {i}";
                v.Value = 13_253_932_800_000_000 + i * 1_000_000L;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        if (leaveInWal)
        {
            // Left open on purpose: the rows above are still only in the -wal file, as they are
            // in a profile copied from a running browser.
            _open.Add(conn);
        }
        else
        {
            conn.Dispose();
        }
        return path;
    }

    private readonly List<SqliteConnection> _open = [];

    private static IReadOnlyDictionary<string, string?> Cells(object row) => TabularExporter.ToDictionary(row);

    [Fact]
    public void One_database_over_the_per_database_limit_is_flagged_even_below_the_total_budget()
    {
        // The bug: 60,000 URLs against a 50,000 per-database limit and a 100,000 budget
        // showed 50,000 rows and no truncation banner.
        NewChromiumHistory(Path.Combine("User Data", "Default", "History"), urls: 12);

        var result = BrowserHistoryTool.Parse(Path.Combine(_dir, "User Data"), CancellationToken.None, perDatabaseLimit: 10, budget: 100);

        result.Rows.Should().HaveCount(10);
        result.Truncated.Should().BeTrue();
        Cells(result.Rows[0])["Url"].Should().Be("https://example.com/page11", "the newest visits are kept");
    }

    [Fact]
    public void A_database_with_exactly_the_limit_is_not_flagged()
    {
        NewChromiumHistory(Path.Combine("User Data", "Default", "History"), urls: 10);

        var result = BrowserHistoryTool.Parse(Path.Combine(_dir, "User Data"), CancellationToken.None, perDatabaseLimit: 10, budget: 100);

        result.Rows.Should().HaveCount(10);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Reaching_the_total_budget_across_profiles_is_flagged()
    {
        NewChromiumHistory(Path.Combine("User Data", "Default", "History"), urls: 6);
        NewChromiumHistory(Path.Combine("User Data", "Profile 1", "History"), urls: 6);

        var result = BrowserHistoryTool.Parse(Path.Combine(_dir, "User Data"), CancellationToken.None, perDatabaseLimit: 50, budget: 8);

        result.Rows.Should().HaveCount(8);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Source_column_names_the_profile_database_not_a_temporary_copy()
    {
        NewChromiumHistory(Path.Combine("User Data", "Default", "History"), urls: 1);
        NewChromiumHistory(Path.Combine("User Data", "Profile 1", "History"), urls: 1);

        var result = BrowserHistoryTool.Parse(Path.Combine(_dir, "User Data"), CancellationToken.None);

        var sources = result.Rows.Select(r => Cells(r)["Source"]).ToList();
        sources.Should().BeEquivalentTo([Path.Combine("Default", "History"), Path.Combine("Profile 1", "History")]);
        sources.Should().NotContain(s => s!.Contains("cinder-", StringComparison.OrdinalIgnoreCase));
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public void A_single_history_file_keeps_its_profile_folder_in_the_source()
    {
        var file = NewChromiumHistory(Path.Combine("Google", "Chrome", "User Data", "Default", "History"), urls: 2);

        var result = BrowserHistoryTool.Parse(file, CancellationToken.None);

        Cells(result.Rows[0])["Source"].Should().Be(Path.Combine("Default", "History"));
        Cells(result.Rows[0])["Browser"].Should().Be("Chrome");
        Cells(result.Rows[0])["LastVisited"].Should().Be("2021-01-01 00:00:01Z");
    }

    [Fact]
    public void Visits_still_in_the_write_ahead_log_are_read()
    {
        var file = NewChromiumHistory(Path.Combine("wal", "Default", "History"), urls: 3, leaveInWal: true);
        try
        {
            File.Exists(file + "-wal").Should().BeTrue("the test needs rows that have not reached the main file");

            var result = BrowserHistoryTool.Parse(file, CancellationToken.None);

            result.Rows.Should().HaveCount(3);
        }
        finally
        {
            _open.ForEach(c => c.Dispose());
        }
    }

    [Fact]
    public void A_folder_with_no_history_database_is_an_error_not_zero_rows()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));
        var act = () => BrowserHistoryTool.Parse(Path.Combine(_dir, "empty"), CancellationToken.None);
        act.Should().Throw<InvalidOperationException>().WithMessage("*No browser history database*");
    }

    [Fact]
    public void A_file_that_is_not_a_history_database_is_an_error_not_zero_rows()
    {
        var file = Path.Combine(_dir, "History");
        File.WriteAllText(file, "this is not a database");
        var act = () => BrowserHistoryTool.Parse(file, CancellationToken.None);
        act.Should().Throw<InvalidOperationException>().WithMessage("Could not read*");
    }
}
