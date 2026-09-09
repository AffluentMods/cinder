using System.Globalization;
using Dapper;

namespace Cinder.Core.Cases;

/// <summary>A row an examiner flagged as a finding, with the note they wrote about it.</summary>
public sealed record Bookmark(
    long Id,
    Guid CaseId,
    DateTimeOffset CreatedUtc,
    string Examiner,
    string Tool,
    string? EvidencePath,
    string Title,
    string? Note,
    string RowJson);

/// <summary>
/// Case-wide bookmarks. Any tool can flag a row; the report builder consumes them as exhibits.
/// Rows are stored as the JSON the exporting tool would have written, so a bookmark survives
/// the tool's grid columns changing later.
/// </summary>
public sealed class BookmarkStore
{
    private readonly CaseStore _store;

    public BookmarkStore(CaseStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        // Idempotent; guarantees the bookmarks table exists for a case created before it did.
        _store.Migrate();
    }

    public async Task<Bookmark> AddAsync(
        Guid caseId, string examiner, string tool, string? evidencePath, string title, string? note, string rowJson,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examiner);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(rowJson);

        var created = DateTimeOffset.UtcNow;
        await using var conn = _store.Open();
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO bookmarks (case_id, created_utc, examiner, tool, evidence_path, title, note, row_json)
            VALUES (@CaseId, @CreatedUtc, @Examiner, @Tool, @EvidencePath, @Title, @Note, @RowJson)
            RETURNING id;
            """,
            new
            {
                CaseId = caseId.ToString("D"),
                CreatedUtc = created.ToString("O", CultureInfo.InvariantCulture),
                Examiner = examiner,
                Tool = tool,
                EvidencePath = evidencePath,
                Title = title,
                Note = note,
                RowJson = rowJson,
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return new Bookmark(id, caseId, created, examiner, tool, evidencePath, title, note, rowJson);
    }

    public async Task<IReadOnlyList<Bookmark>> ListAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var conn = _store.Open();
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT id AS Id, case_id AS CaseId, created_utc AS CreatedUtc, examiner AS Examiner,
                   tool AS Tool, evidence_path AS EvidencePath, title AS Title, note AS Note, row_json AS RowJson
            FROM bookmarks WHERE case_id = @CaseId ORDER BY created_utc ASC, id ASC;
            """,
            new { CaseId = caseId.ToString("D") },
            cancellationToken: ct)).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToBookmark())];
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _store.Open();
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM bookmarks WHERE id = @Id;", new { Id = id }, cancellationToken: ct)).ConfigureAwait(false);
        return n > 0;
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public string CaseId { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string Examiner { get; set; } = "";
        public string Tool { get; set; } = "";
        public string? EvidencePath { get; set; }
        public string Title { get; set; } = "";
        public string? Note { get; set; }
        public string RowJson { get; set; } = "";

        public Bookmark ToBookmark() => new(
            Id, Guid.Parse(CaseId),
            DateTimeOffset.Parse(CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Examiner, Tool, EvidencePath, Title, Note, RowJson);
    }
}
