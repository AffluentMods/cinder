using System.Globalization;
using System.Text.Json;
using Cinder.Core.Custody;
using Dapper;

namespace Cinder.Core.Cases;

/// <summary>High-level operations on a Cinder case file: create, open, list.</summary>
public sealed class CaseService
{
    private readonly CaseStore _store;
    private readonly ICustodyLog _custody;
    private readonly TimeProvider _clock;

    public CaseService(CaseStore store, ICustodyLog custody, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _custody = custody ?? throw new ArgumentNullException(nameof(custody));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<Case> CreateAsync(string name, string examiner, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(examiner);

        _store.Migrate();

        var c = new Case(
            Id: Guid.NewGuid(),
            Name: name,
            Examiner: examiner,
            Description: description,
            CreatedUtc: _clock.GetUtcNow(),
            SchemaVersion: CaseStore.CurrentSchemaVersion);

        await using var conn = _store.Open();
        const string sql = """
            INSERT INTO cases (id, name, examiner, description, created_utc, schema_version)
            VALUES (@Id, @Name, @Examiner, @Description, @CreatedUtc, @SchemaVersion);
            """;

        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = c.Id.ToString("D"),
                c.Name,
                c.Examiner,
                c.Description,
                CreatedUtc = c.CreatedUtc.ToString("O", CultureInfo.InvariantCulture),
                c.SchemaVersion,
            },
            cancellationToken: ct)).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new { c.Name, c.Examiner, c.Description });
        await _custody.AppendAsync(c.Id, examiner, CustodyAction.CaseCreated, details, ct).ConfigureAwait(false);
        return c;
    }

    public async Task<Case?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = _store.Open();
        const string sql = """
            SELECT id            AS Id,
                   name          AS Name,
                   examiner      AS Examiner,
                   description   AS Description,
                   created_utc   AS CreatedUtc,
                   schema_version AS SchemaVersion
            FROM cases
            WHERE id = @Id;
            """;

        var row = await conn.QueryFirstOrDefaultAsync<CaseRow?>(new CommandDefinition(
            sql,
            new { Id = id.ToString("D") },
            cancellationToken: ct)).ConfigureAwait(false);

        return row?.ToCase();
    }

    /// <summary>
    /// Loads the (single) case row from an already-existing .cinder file. Each .cinder file
    /// holds exactly one case, so this is the canonical "open an existing case by path" path.
    /// Returns null if the file has no cases row.
    /// </summary>
    public async Task<Case?> GetFirstAsync(CancellationToken ct = default)
    {
        _store.Migrate();
        await using var conn = _store.Open();
        const string sql = """
            SELECT id            AS Id,
                   name          AS Name,
                   examiner      AS Examiner,
                   description   AS Description,
                   created_utc   AS CreatedUtc,
                   schema_version AS SchemaVersion
            FROM cases
            ORDER BY created_utc ASC
            LIMIT 1;
            """;

        var row = await conn.QueryFirstOrDefaultAsync<CaseRow?>(new CommandDefinition(
            sql,
            cancellationToken: ct)).ConfigureAwait(false);

        return row?.ToCase();
    }

    private sealed class CaseRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Examiner { get; set; } = "";
        public string? Description { get; set; }
        public string CreatedUtc { get; set; } = "";
        public long SchemaVersion { get; set; }

        public Case ToCase() => new(
            Guid.Parse(Id),
            Name,
            Examiner,
            Description,
            DateTimeOffset.Parse(CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            (int)SchemaVersion);
    }
}
