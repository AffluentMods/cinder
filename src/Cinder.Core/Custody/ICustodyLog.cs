namespace Cinder.Core.Custody;

/// <summary>The append-only, hash-chained custody log. Every action on a case ends up here.</summary>
public interface ICustodyLog
{
    /// <summary>Append a new entry. Returns the persisted entry, including hashes and sequence.</summary>
    Task<CustodyEntry> AppendAsync(
        Guid caseId,
        string examiner,
        string action,
        string detailsJson,
        CancellationToken ct = default);

    Task<IReadOnlyList<CustodyEntry>> ListAsync(Guid caseId, CancellationToken ct = default);

    /// <summary>Re-hash every entry and confirm the chain is intact.</summary>
    Task<CustodyVerificationResult> VerifyAsync(Guid caseId, CancellationToken ct = default);
}

public sealed record CustodyVerificationResult(bool Ok, long EntriesChecked, long? FirstBrokenSequence, string? Reason);
