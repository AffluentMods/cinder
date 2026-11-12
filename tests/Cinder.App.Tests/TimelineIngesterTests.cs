using Cinder.App.Services;
using Cinder.Search;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Cinder.App.Tests;

public class TimelineIngesterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-timeline-" + Guid.NewGuid().ToString("N"));

    public TimelineIngesterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static List<TimelineEvent> Events(SuperTimeline timeline) =>
        timeline.Range(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToList();

    // ------------------------------------------------------------------ mail dates

    [Theory]
    [InlineData("Tue, 3 Jun 2025 10:00:00 +0000 (UTC)", "2025-06-03T10:00:00Z")]
    [InlineData("Tue, 03 Jun 2025 10:00:00 -0700 (PDT)", "2025-06-03T17:00:00Z")]
    [InlineData("Tue, 03 Jun 2025 10:00:00 -0700", "2025-06-03T17:00:00Z")]
    [InlineData("Tue, 3 Jun 2025 10:00:00 GMT", "2025-06-03T10:00:00Z")]
    [InlineData("3 Jun 2025 10:00:00 +0200 (CEST (summer time))", "2025-06-03T08:00:00Z")]
    [InlineData("Tue, 3 Jun 2025 10:00:00 EST", "2025-06-03T15:00:00Z")]
    [InlineData("Tue, 3 Jun 2025 10:00:00 PDT", "2025-06-03T17:00:00Z")]
    [InlineData("Tue, 3 Jun 2025 10:00:00 UT", "2025-06-03T10:00:00Z")]
    [InlineData("  Tue,  3 Jun 2025   10:00:00   +0000  ", "2025-06-03T10:00:00Z")]
    public void Mail_dates_parse_with_comments_and_named_zones(string header, string expectedUtc)
    {
        TimelineIngester.ParseMailDate(header).Should().Be(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("(no date)")]
    [InlineData("yesterday-ish")]
    public void Unusable_mail_dates_are_null_not_an_exception(string? header)
    {
        TimelineIngester.ParseMailDate(header).Should().BeNull();
    }

    [Fact]
    public void Folded_headers_are_unfolded_and_the_block_ends_at_the_blank_line()
    {
        var headers = TimelineIngester.ReadHeaders(new StringReader(
            "Received: from a\n by b\nSubject: a long\n\tsubject line\nDate: Tue, 3 Jun 2025 10:00:00 +0000\n (UTC)\nReceived: second\n\nDate: in the body"));

        headers["Subject"].Should().Be("a long subject line");
        headers["date"].Should().Be("Tue, 3 Jun 2025 10:00:00 +0000 (UTC)");
        headers["Received"].Should().Be("from a by b");
    }

    // ------------------------------------------------------------------ timestamps

    [Fact]
    public void UserAssist_last_run_is_read_from_a_real_filetime()
    {
        // 2024-01-15 12:30:00 UTC is FILETIME 133,498,098,000,000,000 — well above the
        // 9,999,999,999,999,999 ceiling that used to reject every entry written since 1632.
        var when = new DateTimeOffset(2024, 1, 15, 12, 30, 0, TimeSpan.Zero);
        var fileTime = when.UtcDateTime.ToFileTimeUtc();
        fileTime.Should().BeGreaterThan(9_999_999_999_999_999);

        var data = new byte[72];
        BitConverter.TryWriteBytes(data.AsSpan(60), fileTime).Should().BeTrue();

        TimelineIngester.UserAssistLastRun(data).Should().Be(when);
    }

    [Fact]
    public void UserAssist_entries_never_run_or_too_short_yield_nothing()
    {
        TimelineIngester.UserAssistLastRun(new byte[72]).Should().BeNull("a zero FILETIME means never run");
        TimelineIngester.UserAssistLastRun(new byte[16]).Should().BeNull();
        TimelineIngester.UserAssistLastRun(null).Should().BeNull();

        var garbage = new byte[72];
        BitConverter.TryWriteBytes(garbage.AsSpan(60), long.MaxValue);
        TimelineIngester.UserAssistLastRun(garbage).Should().BeNull();
    }

    [Fact]
    public void Browser_timestamps_convert_and_reject_out_of_range_values()
    {
        // 13,393,512,600,000,000 µs after 1601-01-01 is 2025-06-03 10:10:00 UTC.
        var expected = new DateTimeOffset(2025, 6, 3, 10, 10, 0, TimeSpan.Zero);
        var webkit = (expected - new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero)).Ticks / 10;
        TimelineIngester.FromWebKitMicroseconds(webkit).Should().Be(expected);
        TimelineIngester.FromUnixMicroseconds(expected.ToUnixTimeMilliseconds() * 1000).Should().Be(expected);

        TimelineIngester.FromWebKitMicroseconds(0).Should().BeNull();
        TimelineIngester.FromWebKitMicroseconds(long.MaxValue).Should().BeNull();
        TimelineIngester.FromUnixMicroseconds(long.MaxValue).Should().BeNull();
        TimelineIngester.FromUnixMicroseconds(long.MinValue).Should().BeNull();
    }

    // ------------------------------------------------------------------ mail ingest

    [Fact]
    public async Task Eml_and_mbox_messages_reach_the_timeline_and_undated_ones_are_counted()
    {
        File.WriteAllText(Path.Combine(_dir, "one.eml"),
            "From: alice@example.org\r\nSubject: Quarterly\r\n numbers\r\nDate: Tue, 3 Jun 2025 10:00:00 +0000 (UTC)\r\n\r\nbody\r\n");
        File.WriteAllText(Path.Combine(_dir, "undated.eml"),
            "From: bob@example.org\r\nSubject: no date here\r\n\r\nbody\r\n");
        File.WriteAllText(Path.Combine(_dir, "archive.mbox"),
            "From alice@example.org Tue Jun  3 10:00:00 2025\n" +
            "From: alice@example.org\nSubject: first\nDate: Tue, 03 Jun 2025 11:00:00 -0700 (PDT)\n\n" +
            "A body line.\nFrom here on this body line must not start a message\n\n" +
            "From bob@example.org Wed Jun  4 09:00:00 2025\n" +
            "From: bob@example.org\nSubject: second\nDate: Wed, 4 Jun 2025 09:00:00 GMT\n\nbody\n\n" +
            "From carol@example.org Thu Jun  5 09:00:00 2025\n" +
            "From: carol@example.org\nSubject: third, date unreadable\nDate: sometime in June\n\nbody\n");

        var timeline = new SuperTimeline();
        var stats = await TimelineIngester.IngestAsync(timeline, _dir);

        stats.Errors.Should().Be(0, stats.LastError);
        stats.Email.Should().Be(3);
        stats.Skipped.Should().Be(2);
        stats.LastSkipReason.Should().NotBeNullOrEmpty();
        stats.ToString().Should().Contain("email=3").And.Contain("skipped=2");

        var events = Events(timeline);
        events.Select(e => e.Timestamp).Should().BeEquivalentTo(new[]
        {
            new DateTimeOffset(2025, 6, 3, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 6, 3, 18, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 6, 4, 9, 0, 0, TimeSpan.Zero),
        });
        events.Should().Contain(e => e.Summary.Contains("Quarterly numbers") && e.User == "alice@example.org");
    }

    // ------------------------------------------------------------------ browser history

    [Fact]
    public void Browser_history_includes_rows_still_in_the_wal_and_leaves_no_staged_copy()
    {
        var evidence = Path.Combine(_dir, "profile");
        var staging = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(evidence);
        Directory.CreateDirectory(staging);
        var history = Path.Combine(evidence, "History");

        var visited = new DateTimeOffset(2025, 6, 3, 10, 10, 0, TimeSpan.Zero);
        var webkit = (visited - new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero)).Ticks / 10;

        // A browser that is still running: the database is in WAL mode, the connection is open,
        // and the newest visit has not been checkpointed into the main file.
        var cs = new SqliteConnectionStringBuilder { DataSource = history, Pooling = false }.ToString();
        using (var browser = new SqliteConnection(cs))
        {
            browser.Open();
            Execute(browser, "PRAGMA journal_mode=WAL;");
            Execute(browser, "PRAGMA wal_autocheckpoint=0;");
            Execute(browser, "CREATE TABLE urls(id INTEGER PRIMARY KEY, url TEXT, title TEXT, last_visit_time INTEGER);");
            Execute(browser, "PRAGMA wal_checkpoint(TRUNCATE);");
            Execute(browser, $"INSERT INTO urls(url, title, last_visit_time) VALUES ('https://recent.example/', 'Recent', {webkit});");
            Execute(browser, $"INSERT INTO urls(url, title, last_visit_time) VALUES ('https://broken.example/', NULL, {long.MaxValue});");
            new FileInfo(history + "-wal").Length.Should().BeGreaterThan(0, "the rows under test must be in the WAL, not the main file");

            var timeline = new SuperTimeline();
            var stats = new IngestStats();
            TimelineIngester.IngestBrowserHistory(timeline, history, stats, CancellationToken.None, staging);

            stats.Errors.Should().Be(0, stats.LastError);
            stats.Browser.Should().Be(1);
            stats.Skipped.Should().Be(1, "the row with an impossible timestamp costs one row, not the file");
            var only = Events(timeline).Should().ContainSingle().Subject;
            only.Timestamp.Should().Be(visited);
            only.Summary.Should().Be("Recent — https://recent.example/");
        }

        Directory.EnumerateFileSystemEntries(staging).Should().BeEmpty("a staged copy of evidence must not outlive the ingest");
    }

    [Fact]
    public void Firefox_places_are_read_with_unix_microsecond_times()
    {
        var places = Path.Combine(_dir, "places.sqlite");
        var visited = new DateTimeOffset(2025, 6, 3, 10, 10, 0, TimeSpan.Zero);
        var cs = new SqliteConnectionStringBuilder { DataSource = places, Pooling = false }.ToString();
        using (var db = new SqliteConnection(cs))
        {
            db.Open();
            Execute(db, "CREATE TABLE moz_places(id INTEGER PRIMARY KEY, url TEXT, title TEXT, last_visit_date INTEGER);");
            Execute(db, $"INSERT INTO moz_places(url, title, last_visit_date) VALUES ('https://fx.example/', 'Fx', {visited.ToUnixTimeMilliseconds() * 1000});");
        }

        var timeline = new SuperTimeline();
        var stats = new IngestStats();
        TimelineIngester.IngestBrowserHistory(timeline, places, stats, CancellationToken.None, _dir);

        stats.Errors.Should().Be(0, stats.LastError);
        Events(timeline).Should().ContainSingle().Which.Timestamp.Should().Be(visited);
        Directory.EnumerateDirectories(_dir, "cinder-history-*").Should().BeEmpty();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ cancellation

    [Fact]
    public async Task A_cancelled_ingest_stops_instead_of_being_counted_as_an_error()
    {
        File.WriteAllText(Path.Combine(_dir, "one.eml"), "Date: Tue, 3 Jun 2025 10:00:00 +0000\r\n\r\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => TimelineIngester.IngestAsync(new SuperTimeline(), _dir, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
