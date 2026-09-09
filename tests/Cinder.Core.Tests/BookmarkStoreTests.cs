using Cinder.Core.Cases;
using Cinder.Core.Custody;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

public sealed class BookmarkStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-bm").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task<(CaseStore Store, Guid CaseId)> NewCaseAsync()
    {
        var store = new CaseStore(Path.Combine(_dir, "case.cinder"));
        store.Migrate();
        var svc = new CaseService(store, new CustodyLog(store));
        var c = await svc.CreateAsync("bm-case", "alice", null);
        return (store, c.Id);
    }

    [Fact]
    public async Task Add_then_list_round_trips_every_field()
    {
        var (store, caseId) = await NewCaseAsync();
        var bm = new BookmarkStore(store);

        var added = await bm.AddAsync(caseId, "alice", "registry", @"C:\ev\NTUSER.DAT",
            "Run key: evil.exe", "persistence", "{\"Key\":\"HKCU\\\\Run\",\"Value\":\"evil.exe\"}");

        added.Id.Should().BeGreaterThan(0);
        var list = await bm.ListAsync(caseId);
        list.Should().ContainSingle();
        list[0].Should().BeEquivalentTo(added, o => o.Excluding(b => b.CreatedUtc));
        list[0].CreatedUtc.Should().BeCloseTo(added.CreatedUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Bookmarks_are_scoped_to_their_case()
    {
        var (store, caseId) = await NewCaseAsync();
        var bm = new BookmarkStore(store);
        await bm.AddAsync(caseId, "alice", "evtx", null, "logon", null, "{}");

        (await bm.ListAsync(Guid.NewGuid())).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_removes_exactly_one()
    {
        var (store, caseId) = await NewCaseAsync();
        var bm = new BookmarkStore(store);
        var a = await bm.AddAsync(caseId, "alice", "evtx", null, "a", null, "{}");
        await bm.AddAsync(caseId, "alice", "evtx", null, "b", null, "{}");

        (await bm.DeleteAsync(a.Id)).Should().BeTrue();
        (await bm.DeleteAsync(a.Id)).Should().BeFalse();
        (await bm.ListAsync(caseId)).Should().ContainSingle(b => b.Title == "b");
    }

    [Fact]
    public async Task Migration_adds_the_table_to_a_version_one_case()
    {
        // A case file created before the bookmarks migration existed must gain the table on
        // first use rather than failing.
        var store = new CaseStore(Path.Combine(_dir, "old.cinder"));
        await using (var conn = store.Open())
        {
            await Dapper.SqlMapper.ExecuteAsync(conn, """
                CREATE TABLE schema_migrations (name TEXT PRIMARY KEY, applied_utc TEXT NOT NULL);
                CREATE TABLE cases (id TEXT PRIMARY KEY, name TEXT NOT NULL, examiner TEXT NOT NULL, description TEXT NULL, created_utc TEXT NOT NULL, schema_version INTEGER NOT NULL);
                CREATE TABLE custody_entries (id INTEGER PRIMARY KEY AUTOINCREMENT, case_id TEXT NOT NULL, sequence INTEGER NOT NULL, timestamp_utc TEXT NOT NULL, examiner TEXT NOT NULL, action TEXT NOT NULL, details_json TEXT NOT NULL, prev_hash TEXT NOT NULL, entry_hash TEXT NOT NULL UNIQUE);
                CREATE TABLE hashes (id INTEGER PRIMARY KEY AUTOINCREMENT, case_id TEXT NOT NULL, target_path TEXT NOT NULL, target_size INTEGER NOT NULL, md5 TEXT NULL, sha1 TEXT NULL, sha256 TEXT NULL, blake3 TEXT NULL, computed_utc TEXT NOT NULL);
                INSERT INTO schema_migrations VALUES ('0001_initial_schema.sql', '2026-01-01T00:00:00Z');
                """);
        }

        var bm = new BookmarkStore(store);   // runs Migrate(): 0001 skipped, 0002 applied
        var caseId = Guid.NewGuid();
        await using (var conn = store.Open())
        {
            await Dapper.SqlMapper.ExecuteAsync(conn,
                "INSERT INTO cases VALUES (@Id, 'old', 'bob', NULL, '2026-01-01T00:00:00Z', 1);",
                new { Id = caseId.ToString("D") });
        }

        await bm.AddAsync(caseId, "bob", "hex", null, "offset 0x40", null, "{}");
        (await bm.ListAsync(caseId)).Should().ContainSingle();
    }
}
