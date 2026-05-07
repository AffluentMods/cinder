using Cinder.Core.Cases;
using Cinder.Core.Custody;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Cinder.Core.Tests;

public sealed class CustodyLogTests : IDisposable
{
    private readonly string _dbPath;
    private readonly CaseStore _store;
    private readonly Guid _caseId = Guid.NewGuid();

    public CustodyLogTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cinder-custody-{Guid.NewGuid():N}.db");
        _store = new CaseStore(_dbPath);
        _store.Migrate();

        using var conn = _store.Open();
        conn.Execute(
            "INSERT INTO cases (id, name, examiner, description, created_utc, schema_version) " +
            "VALUES (@Id, 'Test case', 'examiner', null, @Created, 1);",
            new { Id = _caseId.ToString("D"), Created = DateTimeOffset.UtcNow.ToString("O") });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Genesis_entry_chains_from_zero_hash()
    {
        var log = new CustodyLog(_store);

        var entry = await log.AppendAsync(_caseId, "examiner", CustodyAction.CaseCreated, """{"k":"v"}""");

        entry.Sequence.Should().Be(1);
        entry.PrevHash.Should().Be(new string('0', 64));
        entry.EntryHash.Should().HaveLength(64);
        entry.EntryHash.Should().NotBe(entry.PrevHash);
    }

    [Fact]
    public async Task Each_entry_chains_to_previous()
    {
        var log = new CustodyLog(_store);

        var first = await log.AppendAsync(_caseId, "examiner", "a", "{}");
        var second = await log.AppendAsync(_caseId, "examiner", "b", "{}");
        var third = await log.AppendAsync(_caseId, "examiner", "c", "{}");

        second.Sequence.Should().Be(2);
        second.PrevHash.Should().Be(first.EntryHash);
        third.PrevHash.Should().Be(second.EntryHash);

        var entries = await log.ListAsync(_caseId);
        entries.Should().HaveCount(3);
    }

    [Fact]
    public async Task Verify_reports_ok_for_intact_chain()
    {
        var log = new CustodyLog(_store);
        await log.AppendAsync(_caseId, "examiner", "a", "{}");
        await log.AppendAsync(_caseId, "examiner", "b", "{}");
        await log.AppendAsync(_caseId, "examiner", "c", "{}");

        var verification = await log.VerifyAsync(_caseId);

        verification.Ok.Should().BeTrue();
        verification.EntriesChecked.Should().Be(3);
        verification.FirstBrokenSequence.Should().BeNull();
    }

    [Fact]
    public async Task Verify_detects_payload_tampering()
    {
        var log = new CustodyLog(_store);
        await log.AppendAsync(_caseId, "examiner", "a", "{}");
        await log.AppendAsync(_caseId, "examiner", "b", "{}");

        // Mutate the second entry's stored details_json after the fact.
        using (var conn = _store.Open())
        {
            conn.Execute(
                "UPDATE custody_entries SET details_json='{\"hacked\":true}' WHERE sequence=2 AND case_id=@CaseId;",
                new { CaseId = _caseId.ToString("D") });
        }

        var verification = await log.VerifyAsync(_caseId);

        verification.Ok.Should().BeFalse();
        verification.FirstBrokenSequence.Should().Be(2);
        verification.Reason.Should().Contain("Entry-hash mismatch");
    }

    [Fact]
    public async Task Empty_log_verifies_ok()
    {
        var log = new CustodyLog(_store);

        var verification = await log.VerifyAsync(_caseId);

        verification.Ok.Should().BeTrue();
        verification.EntriesChecked.Should().Be(0);
    }
}
