using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cinder.Core.Cases;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cinder.Core.Custody;

/// <summary>
/// SQLite-backed chain-of-custody log. Each appended entry is hashed against its predecessor:
/// <c>entry_hash = SHA-256(prev_hash || US || sequence || US || timestamp || US || examiner ||
///   US || action || US || details)</c> where US = U+001F (ASCII Unit Separator).
/// Genesis prev_hash is 64 zero hex chars.
/// </summary>
public sealed class CustodyLog : ICustodyLog
{
    private const string GenesisPrevHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private const char Separator = '';

    private readonly CaseStore _store;
    private readonly TimeProvider _clock;

    public CustodyLog(CaseStore store, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<CustodyEntry> AppendAsync(
        Guid caseId,
        string examiner,
        string action,
        string detailsJson,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examiner);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        detailsJson = string.IsNullOrEmpty(detailsJson) ? "{}" : detailsJson;

        await using var conn = _store.Open();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var tip = await GetTipAsync(conn, tx, caseId, ct).ConfigureAwait(false);
        var sequence = (tip?.Sequence ?? 0) + 1;
        var prevHash = tip?.EntryHash ?? GenesisPrevHash;
        var timestamp = _clock.GetUtcNow();
        var entryHash = ComputeEntryHash(prevHash, sequence, timestamp, examiner, action, detailsJson);

        const string insert = """
            INSERT INTO custody_entries
                (case_id, sequence, timestamp_utc, examiner, action, details_json, prev_hash, entry_hash)
            VALUES
                (@CaseId, @Sequence, @TimestampUtc, @Examiner, @Action, @DetailsJson, @PrevHash, @EntryHash)
            RETURNING id;
            """;

        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            insert,
            new
            {
                CaseId = caseId.ToString("D"),
                Sequence = sequence,
                TimestampUtc = timestamp.ToString("O", CultureInfo.InvariantCulture),
                Examiner = examiner,
                Action = action,
                DetailsJson = detailsJson,
                PrevHash = prevHash,
                EntryHash = entryHash,
            },
            transaction: tx,
            cancellationToken: ct)).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new CustodyEntry(id, caseId, sequence, timestamp, examiner, action, detailsJson, prevHash, entryHash);
    }

    public async Task<IReadOnlyList<CustodyEntry>> ListAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var conn = _store.Open();
        const string sql = """
            SELECT id            AS Id,
                   case_id       AS CaseId,
                   sequence      AS Sequence,
                   timestamp_utc AS TimestampUtc,
                   examiner      AS Examiner,
                   action        AS Action,
                   details_json  AS DetailsJson,
                   prev_hash     AS PrevHash,
                   entry_hash    AS EntryHash
            FROM custody_entries
            WHERE case_id = @CaseId
            ORDER BY sequence ASC;
            """;

        var rows = await conn.QueryAsync<CustodyRow>(new CommandDefinition(
            sql,
            new { CaseId = caseId.ToString("D") },
            cancellationToken: ct)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToEntry())];
    }

    public async Task<CustodyVerificationResult> VerifyAsync(Guid caseId, CancellationToken ct = default)
    {
        var entries = await ListAsync(caseId, ct).ConfigureAwait(false);
        var prev = GenesisPrevHash;
        long expectedSeq = 1;

        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (e.Sequence != expectedSeq)
            {
                return new CustodyVerificationResult(false, expectedSeq - 1, e.Sequence, "Sequence gap or duplicate.");
            }
            if (!string.Equals(e.PrevHash, prev, StringComparison.Ordinal))
            {
                return new CustodyVerificationResult(false, expectedSeq - 1, e.Sequence, "Prev-hash mismatch.");
            }

            var recomputed = ComputeEntryHash(e.PrevHash, e.Sequence, e.TimestampUtc, e.Examiner, e.Action, e.DetailsJson);
            if (!string.Equals(recomputed, e.EntryHash, StringComparison.Ordinal))
            {
                return new CustodyVerificationResult(false, expectedSeq - 1, e.Sequence, "Entry-hash mismatch (tampering).");
            }

            prev = e.EntryHash;
            expectedSeq++;
        }

        return new CustodyVerificationResult(true, entries.Count, null, null);
    }

    private static async Task<TipRow?> GetTipAsync(
        SqliteConnection conn,
        IDbTransaction tx,
        Guid caseId,
        CancellationToken ct)
    {
        const string sql = """
            SELECT sequence AS Sequence, entry_hash AS EntryHash
            FROM custody_entries
            WHERE case_id = @CaseId
            ORDER BY sequence DESC
            LIMIT 1;
            """;

        return await conn.QueryFirstOrDefaultAsync<TipRow?>(new CommandDefinition(
            sql,
            new { CaseId = caseId.ToString("D") },
            transaction: tx,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static string ComputeEntryHash(
        string prevHash,
        long sequence,
        DateTimeOffset timestamp,
        string examiner,
        string action,
        string detailsJson)
    {
        var payload = new StringBuilder(128 + detailsJson.Length)
            .Append(prevHash).Append(Separator)
            .Append(sequence.ToString(CultureInfo.InvariantCulture)).Append(Separator)
            .Append(timestamp.ToString("O", CultureInfo.InvariantCulture)).Append(Separator)
            .Append(examiner).Append(Separator)
            .Append(action).Append(Separator)
            .Append(detailsJson)
            .ToString();

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(digest);
    }

    private sealed class TipRow
    {
        public long Sequence { get; set; }
        public string EntryHash { get; set; } = "";
    }

    private sealed class CustodyRow
    {
        public long Id { get; set; }
        public string CaseId { get; set; } = "";
        public long Sequence { get; set; }
        public string TimestampUtc { get; set; } = "";
        public string Examiner { get; set; } = "";
        public string Action { get; set; } = "";
        public string DetailsJson { get; set; } = "";
        public string PrevHash { get; set; } = "";
        public string EntryHash { get; set; } = "";

        public CustodyEntry ToEntry() => new(
            Id,
            Guid.Parse(CaseId),
            Sequence,
            DateTimeOffset.Parse(TimestampUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Examiner,
            Action,
            DetailsJson,
            PrevHash,
            EntryHash);
    }
}
