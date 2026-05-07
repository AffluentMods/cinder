using Cinder.Core.Cases;
using Cinder.Core.Custody;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Cinder.Core.Tests;

public sealed class CaseServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly CaseStore _store;

    public CaseServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cinder-cases-{Guid.NewGuid():N}.db");
        _store = new CaseStore(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Creating_a_case_persists_the_row_and_logs_genesis_custody_entry()
    {
        var custody = new CustodyLog(_store);
        var sut = new CaseService(_store, custody);

        var c = await sut.CreateAsync("Acme-2026-04", "alice", "Test description");

        var fetched = await sut.GetAsync(c.Id);
        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be("Acme-2026-04");
        fetched.Examiner.Should().Be("alice");
        fetched.SchemaVersion.Should().Be(CaseStore.CurrentSchemaVersion);

        var entries = await custody.ListAsync(c.Id);
        entries.Should().ContainSingle()
            .Which.Action.Should().Be(CustodyAction.CaseCreated);
    }
}
