namespace Cinder.Core.Custody;

/// <summary>
/// One row in the chain-of-custody log. The chain is append-only and tamper-evident:
/// <c>EntryHash = SHA-256(prev_hash || sequence || timestamp || examiner || action || details)</c>.
/// </summary>
public sealed record CustodyEntry(
    long Id,
    Guid CaseId,
    long Sequence,
    DateTimeOffset TimestampUtc,
    string Examiner,
    string Action,
    string DetailsJson,
    string PrevHash,
    string EntryHash);
