using System.Security.Cryptography;
using Cinder.Cli;
using Cinder.Core.Cases;
using Cinder.Core.Custody;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Cinder.Libs.Tests;

/// <summary>
/// <c>cinder custody verify</c>. The verb used to print a placeholder and exit 0 for any path
/// at all, so <c>cinder custody verify case.db &amp;&amp; echo OK</c> passed a tampered case, a
/// text file and a path that did not exist. The exit code is the product here.
/// </summary>
public sealed class CustodyVerifyCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-cli-custody").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string NewCasePath() => Path.Combine(_dir, $"case-{Guid.NewGuid():N}.cinder");

    /// <summary>Creates a case file holding the given cases, each with <paramref name="extraEntries"/> further custody entries.</summary>
    private static async Task<List<Case>> CreateCasesAsync(string path, int extraEntries, params string[] names)
    {
        var store = new CaseStore(path);
        var log = new CustodyLog(store);
        var service = new CaseService(store, log);
        var cases = new List<Case>();
        foreach (var name in names)
        {
            var c = await service.CreateAsync(name, "examiner", null);
            for (int i = 0; i < extraEntries; i++)
            {
                await log.AppendAsync(c.Id, "examiner", CustodyAction.Annotation, $$"""{"n":{{i}}}""");
            }
            cases.Add(c);
        }
        SqliteConnection.ClearAllPools();
        return cases;
    }

    private static void Execute(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }
            cmd.ExecuteNonQuery().Should().BeGreaterThan(0, "the tampering statement must actually change something");
        }
        SqliteConnection.ClearAllPools();
    }

    private static async Task<(int Exit, string Out, string Err)> RunAsync(string path)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CustodyVerifyCommand.RunAsync(path, output, error);
        SqliteConnection.ClearAllPools();
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Every_case_in_the_file_is_verified_and_an_intact_file_exits_zero()
    {
        var path = NewCasePath();
        var cases = await CreateCasesAsync(path, extraEntries: 3, "Alpha", "Bravo");

        var (exit, output, error) = await RunAsync(path);

        exit.Should().Be(CustodyVerifyCommand.ExitOk);
        error.Should().BeEmpty();
        foreach (var c in cases)
        {
            output.Should().MatchRegex($@"OK\s+{c.Id:D}\s+""{c.Name}""\s+4 entries");
        }
        output.Should().Contain("Custody chain intact: 2 cases, 8 entries.");
    }

    [Fact]
    public async Task An_edited_entry_fails_naming_the_case_and_the_sequence()
    {
        var path = NewCasePath();
        var cases = await CreateCasesAsync(path, extraEntries: 4, "Alpha", "Bravo");
        Execute(path,
            "UPDATE custody_entries SET details_json = '{\"n\":999}' WHERE case_id = @id AND sequence = 3;",
            ("@id", cases[1].Id.ToString("D")));

        var (exit, output, error) = await RunAsync(path);

        exit.Should().Be(CustodyVerifyCommand.ExitBroken);
        output.Should().MatchRegex($@"OK\s+{cases[0].Id:D}", "the untouched case is still reported, as intact");
        output.Should().MatchRegex($@"BROKEN\s+{cases[1].Id:D}\s+""Bravo""\s+chain breaks at sequence 3: .*\(2 entries verified before the break\)");
        error.Should().Contain("FAILED for 1 of 2 cases");
        output.Should().NotContain("Custody chain intact");
    }

    [Fact]
    public async Task A_removed_entry_fails_at_the_gap()
    {
        var path = NewCasePath();
        var cases = await CreateCasesAsync(path, extraEntries: 4, "Alpha");
        Execute(path, "DELETE FROM custody_entries WHERE case_id = @id AND sequence = 2;", ("@id", cases[0].Id.ToString("D")));

        var (exit, output, _) = await RunAsync(path);

        exit.Should().Be(CustodyVerifyCommand.ExitBroken);
        output.Should().MatchRegex($@"BROKEN\s+{cases[0].Id:D}.*sequence 3: Sequence gap");
    }

    [Fact]
    public async Task Reordered_entries_fail()
    {
        var path = NewCasePath();
        var cases = await CreateCasesAsync(path, extraEntries: 4, "Alpha");
        var id = cases[0].Id.ToString("D");
        Execute(path, "UPDATE custody_entries SET sequence = 99 WHERE case_id = @id AND sequence = 2;", ("@id", id));
        Execute(path, "UPDATE custody_entries SET sequence = 2 WHERE case_id = @id AND sequence = 3;", ("@id", id));
        Execute(path, "UPDATE custody_entries SET sequence = 3 WHERE case_id = @id AND sequence = 99;", ("@id", id));

        var (exit, output, _) = await RunAsync(path);

        exit.Should().Be(CustodyVerifyCommand.ExitBroken);
        output.Should().MatchRegex($@"BROKEN\s+{id}.*sequence 2");
    }

    [Fact]
    public async Task A_case_whose_custody_log_was_emptied_fails()
    {
        // An empty chain verifies trivially; a case with no entries is still not a clean case.
        var path = NewCasePath();
        var cases = await CreateCasesAsync(path, extraEntries: 2, "Alpha", "Bravo");
        Execute(path, "DELETE FROM custody_entries WHERE case_id = @id;", ("@id", cases[0].Id.ToString("D")));

        var (exit, output, _) = await RunAsync(path);

        exit.Should().Be(CustodyVerifyCommand.ExitBroken);
        output.Should().MatchRegex($@"BROKEN\s+{cases[0].Id:D}.*no custody entries");
        output.Should().MatchRegex($@"OK\s+{cases[1].Id:D}");
    }

    [Fact]
    public async Task Custody_entries_left_behind_by_a_deleted_case_record_fail()
    {
        var path = NewCasePath();
        var cases = await CreateCasesAsync(path, extraEntries: 2, "Alpha", "Bravo");
        Execute(path, "PRAGMA foreign_keys = OFF; DELETE FROM cases WHERE id = @id;", ("@id", cases[1].Id.ToString("D")));

        var (exit, output, _) = await RunAsync(path);

        exit.Should().Be(CustodyVerifyCommand.ExitBroken);
        output.Should().MatchRegex($@"BROKEN\s+{cases[1].Id:D}\s+""\(no case record\)"".*case record is missing");
    }

    [Fact]
    public async Task A_path_that_does_not_exist_is_an_error_and_is_not_created()
    {
        var path = Path.Combine(_dir, "typo.cinder");

        var (exit, output, error) = await RunAsync(path);

        exit.Should().Be(CustodyVerifyCommand.ExitUnreadable);
        error.Should().Contain("not found");
        output.Should().BeEmpty();
        File.Exists(path).Should().BeFalse("verifying must never create the database it was asked to verify");
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_is_an_error()
    {
        var path = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat("this is not a SQLite database\n", 200)));
        var before = SHA256.HashData(await File.ReadAllBytesAsync(path));

        var (exit, _, error) = await RunAsync(path);

        exit.Should().Be(CustodyVerifyCommand.ExitUnreadable);
        error.Should().Contain("not a readable Cinder case file");
        SHA256.HashData(await File.ReadAllBytesAsync(path)).Should().Equal(before);
    }

    [Fact]
    public async Task A_database_that_is_not_a_case_file_is_an_error()
    {
        var path = Path.Combine(_dir, "other.db");
        Execute(path, "CREATE TABLE unrelated (x INTEGER); INSERT INTO unrelated VALUES (1);");

        var (exit, _, error) = await RunAsync(path);

        exit.Should().Be(CustodyVerifyCommand.ExitUnreadable);
        error.Should().Contain("not a readable Cinder case file");
    }

    [Fact]
    public async Task A_case_file_with_no_cases_is_not_reported_as_verified()
    {
        var path = NewCasePath();
        new CaseStore(path).Migrate();
        SqliteConnection.ClearAllPools();

        var (exit, _, error) = await RunAsync(path);

        exit.Should().Be(CustodyVerifyCommand.ExitUnreadable);
        error.Should().Contain("nothing was verified");
    }

    [Fact]
    public async Task Verifying_does_not_change_the_case_file()
    {
        var path = NewCasePath();
        await CreateCasesAsync(path, extraEntries: 3, "Alpha");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var filesBefore = Directory.GetFiles(_dir).Order().ToArray();

        (await RunAsync(path)).Exit.Should().Be(CustodyVerifyCommand.ExitOk);

        SHA256.HashData(await File.ReadAllBytesAsync(path)).Should().Equal(before);
        Directory.GetFiles(_dir).Order().ToArray().Should().Equal(filesBefore, "no journal or sidecar file may be left next to the evidence");
    }

    [Fact]
    public async Task The_cli_entry_point_returns_the_verification_result_as_its_exit_code()
    {
        var intact = NewCasePath();
        await CreateCasesAsync(intact, extraEntries: 1, "Alpha");
        var tampered = NewCasePath();
        var cases = await CreateCasesAsync(tampered, extraEntries: 2, "Bravo");
        Execute(tampered, "UPDATE custody_entries SET examiner = 'someone-else' WHERE case_id = @id AND sequence = 2;",
            ("@id", cases[0].Id.ToString("D")));

        (await Program.Main(["custody", "verify", intact])).Should().Be(0);
        SqliteConnection.ClearAllPools();
        (await Program.Main(["custody", "verify", tampered])).Should().Be(1, "this used to be 0 for every input");
        SqliteConnection.ClearAllPools();
        (await Program.Main(["custody", "verify", Path.Combine(_dir, "missing.cinder")])).Should().Be(2);
        (await Program.Main(["custody", "verify"])).Should().NotBe(0, "a missing argument is a usage error, not a pass");
        (await Program.Main(["custody"])).Should().NotBe(0);
    }
}
