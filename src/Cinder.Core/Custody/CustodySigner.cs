using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Cinder.Core.Cases;
using Dapper;

namespace Cinder.Core.Custody;

/// <summary>A signed statement that the chain stood at a given entry hash at a given time.</summary>
public sealed record CustodyAttestation(
    long Id,
    Guid CaseId,
    long Sequence,
    string EntryHash,
    DateTimeOffset SignedUtc,
    string Examiner,
    string PublicKeySpkiBase64,
    string SignatureBase64,
    string KeyFingerprint,
    byte[]? TsaToken = null,
    string? TsaUrl = null,
    DateTimeOffset? TsaTime = null)
{
    public bool HasTimestamp => TsaToken is { Length: > 0 };
}

public sealed record AttestationVerification(
    CustodyAttestation Attestation,
    bool SignatureValid,
    bool ChainMatches,
    string? Reason,
    TimestampVerification? Timestamp = null)
{
    /// <summary>Signature, chain and — when a timestamp is present — the timestamp all hold.</summary>
    public bool Ok => SignatureValid && ChainMatches && (Timestamp is null || Timestamp.SignatureValid);
}

/// <summary>
/// The external anchor the custody chain lacked. The chain itself is an unkeyed hash stored
/// beside the entries it protects, so anyone who can write the case file can recompute it. An
/// attestation binds the chain's tip to something that lives outside the case file: an
/// examiner signing key (ECDSA P-256) held in the examiner's own profile.
///
/// <para>After a rewrite, the entry at the attested sequence no longer carries the attested
/// hash. Re-attesting would need the private key. So the guarantee becomes: nobody who lacks
/// the examiner's key can alter the log up to the attested point without it showing. It is
/// still not proof against the examiner themself; publishing the attestation (the JSON that
/// <see cref="ExportAttestation"/> produces) somewhere the examiner does not control is what
/// closes that, and is a process decision rather than code.</para>
///
/// <para>Key storage: PKCS#8 at <c>&lt;profile&gt;/Cinder/examiner-signing-key.p8</c>, mode 0600 on
/// Unix; the Windows copy relies on the profile ACL. The public key travels inside every
/// attestation so verification needs nothing but the case file.</para>
/// </summary>
public sealed class CustodySigner
{
    private readonly CaseStore _store;
    private readonly string _keyPath;

    public CustodySigner(CaseStore store, string? keyPath = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _keyPath = keyPath ?? DefaultKeyPath();
        _store.Migrate();
    }

    public static string DefaultKeyPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cinder", "examiner-signing-key.p8");

    public string KeyPath => _keyPath;

    /// <summary>Loads the examiner key, creating it on first use.</summary>
    public ECDsa LoadOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(File.ReadAllBytes(_keyPath), out _);
            return key;
        }

        var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        File.WriteAllBytes(_keyPath, created.ExportPkcs8PrivateKey());
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(_keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        }
        File.WriteAllBytes(_keyPath + ".pub", created.ExportSubjectPublicKeyInfo());
        return created;
    }

    /// <summary>Fingerprint of the current examiner key (SHA-256 over SPKI), for display.</summary>
    public string KeyFingerprint()
    {
        using var key = LoadOrCreateKey();
        return Fingerprint(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Signs the chain's current tip for <paramref name="caseId"/>. Throws if the chain is empty.</summary>
    public async Task<CustodyAttestation> SignTipAsync(Guid caseId, string examiner, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examiner);

        var log = new CustodyLog(_store);
        var entries = await log.ListAsync(caseId, ct).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("The custody chain is empty; nothing to attest.");
        }
        var tip = entries[^1];
        var signedUtc = DateTimeOffset.UtcNow;

        using var key = LoadOrCreateKey();
        var spki = key.ExportSubjectPublicKeyInfo();
        var signature = key.SignData(Payload(caseId, tip.Sequence, tip.EntryHash, signedUtc), HashAlgorithmName.SHA256);

        await using var conn = _store.Open();
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO custody_attestations (case_id, sequence, entry_hash, signed_utc, examiner, public_key_spki, signature)
            VALUES (@CaseId, @Sequence, @EntryHash, @SignedUtc, @Examiner, @Spki, @Signature)
            RETURNING id;
            """,
            new
            {
                CaseId = caseId.ToString("D"),
                tip.Sequence,
                tip.EntryHash,
                SignedUtc = signedUtc.ToString("O", CultureInfo.InvariantCulture),
                Examiner = examiner,
                Spki = Convert.ToBase64String(spki),
                Signature = Convert.ToBase64String(signature),
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return new CustodyAttestation(id, caseId, tip.Sequence, tip.EntryHash, signedUtc, examiner,
            Convert.ToBase64String(spki), Convert.ToBase64String(signature), Fingerprint(spki));
    }

    /// <summary>
    /// Asks an RFC 3161 Time-Stamp Authority to timestamp the attestation's signature and stores
    /// the token beside it. The examiner key says <em>who</em>; the TSA says <em>no later than
    /// when</em>, from a clock the examiner does not control. Public TSAs: DigiCert, Sectigo,
    /// freetsa.org; an organisation can run its own.
    /// </summary>
    public async Task<CustodyAttestation> TimestampAsync(long attestationId, Uri tsaUrl, HttpClient http, CancellationToken ct = default)
    {
        await using var conn = _store.Open();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            SelectColumns + " WHERE id = @Id;", new { Id = attestationId }, cancellationToken: ct)).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No attestation with id {attestationId}.");
        var a = row.ToAttestation();

        var stamp = await Rfc3161Timestamper.TimestampAsync(Convert.FromBase64String(a.SignatureBase64), tsaUrl, http, ct).ConfigureAwait(false);

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE custody_attestations SET tsa_token = @Token, tsa_url = @Url, tsa_time = @Time WHERE id = @Id;",
            new
            {
                Token = stamp.Token,
                Url = tsaUrl.ToString(),
                Time = stamp.Time.ToString("O", CultureInfo.InvariantCulture),
                Id = attestationId,
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return a with { TsaToken = stamp.Token, TsaUrl = tsaUrl.ToString(), TsaTime = stamp.Time };
    }

    private const string SelectColumns =
        """
        SELECT id AS Id, case_id AS CaseId, sequence AS Sequence, entry_hash AS EntryHash, signed_utc AS SignedUtc,
               examiner AS Examiner, public_key_spki AS Spki, signature AS Signature,
               tsa_token AS TsaToken, tsa_url AS TsaUrl, tsa_time AS TsaTime
        FROM custody_attestations
        """;

    public async Task<IReadOnlyList<CustodyAttestation>> ListAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var conn = _store.Open();
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            SelectColumns + " WHERE case_id = @CaseId ORDER BY sequence ASC, id ASC;",
            new { CaseId = caseId.ToString("D") },
            cancellationToken: ct)).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToAttestation())];
    }

    /// <summary>
    /// Checks every attestation for the case: the signature must verify under the embedded
    /// public key, and the chain entry at the attested sequence must still carry the attested
    /// hash. A rewrite after signing fails the second check; a forged attestation fails the first.
    /// </summary>
    public async Task<IReadOnlyList<AttestationVerification>> VerifyAsync(Guid caseId, CancellationToken ct = default)
    {
        var attestations = await ListAsync(caseId, ct).ConfigureAwait(false);
        var entries = await new CustodyLog(_store).ListAsync(caseId, ct).ConfigureAwait(false);
        var bySequence = entries.ToDictionary(e => e.Sequence, e => e.EntryHash);

        var results = new List<AttestationVerification>(attestations.Count);
        foreach (var a in attestations)
        {
            ct.ThrowIfCancellationRequested();
            var sigOk = VerifySignature(a);
            var chainOk = bySequence.TryGetValue(a.Sequence, out var hash) && string.Equals(hash, a.EntryHash, StringComparison.Ordinal);
            TimestampVerification? ts = null;
            if (a.HasTimestamp)
            {
                ts = Rfc3161Timestamper.Verify(a.TsaToken!, Convert.FromBase64String(a.SignatureBase64));
            }
            var reason = (sigOk, chainOk) switch
            {
                (false, _) => "signature does not verify under the embedded public key",
                (true, false) when !bySequence.ContainsKey(a.Sequence) => $"chain no longer has an entry at sequence {a.Sequence}",
                (true, false) => $"entry {a.Sequence} now hashes differently from what was attested — the log was altered after signing",
                _ when ts is { SignatureValid: false } => ts.Reason,
                _ => null,
            };
            results.Add(new AttestationVerification(a, sigOk, chainOk, reason, ts));
        }
        return results;
    }

    public static bool VerifySignature(CustodyAttestation a)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(a.PublicKeySpkiBase64), out _);
            return key.VerifyData(Payload(a.CaseId, a.Sequence, a.EntryHash, a.SignedUtc),
                Convert.FromBase64String(a.SignatureBase64), HashAlgorithmName.SHA256);
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    /// <summary>
    /// The attestation as a self-contained JSON document. Publish it somewhere outside the
    /// examiner's control — mail it to a supervisor, a ticket, a WORM share — and the chain
    /// gains an anchor that even the examiner cannot move.
    /// </summary>
    public static string ExportAttestation(CustodyAttestation a) => System.Text.Json.JsonSerializer.Serialize(new
    {
        format = "cinder-custody-attestation/1",
        case_id = a.CaseId,
        sequence = a.Sequence,
        entry_hash = a.EntryHash,
        signed_utc = a.SignedUtc.ToString("O", CultureInfo.InvariantCulture),
        examiner = a.Examiner,
        algorithm = "ECDSA-P256-SHA256",
        public_key_spki = a.PublicKeySpkiBase64,
        key_fingerprint = a.KeyFingerprint,
        signature = a.SignatureBase64,
        signed_payload = "case_id|sequence|entry_hash|signed_utc (UTF-8, ISO 8601 round-trip)",
        rfc3161_timestamp = a.HasTimestamp
            ? new
            {
                tsa_url = a.TsaUrl,
                time = a.TsaTime?.ToString("O", CultureInfo.InvariantCulture),
                token_der = Convert.ToBase64String(a.TsaToken!),
                covers = "SHA-256 of the signature bytes",
            }
            : null,
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    internal static byte[] Payload(Guid caseId, long sequence, string entryHash, DateTimeOffset signedUtc) =>
        Encoding.UTF8.GetBytes(string.Join('|',
            caseId.ToString("D"),
            sequence.ToString(CultureInfo.InvariantCulture),
            entryHash,
            signedUtc.ToString("O", CultureInfo.InvariantCulture)));

    private static string Fingerprint(byte[] spki) => "SHA256:" + Convert.ToHexStringLower(SHA256.HashData(spki))[..32];

    private sealed class Row
    {
        public long Id { get; set; }
        public string CaseId { get; set; } = "";
        public long Sequence { get; set; }
        public string EntryHash { get; set; } = "";
        public string SignedUtc { get; set; } = "";
        public string Examiner { get; set; } = "";
        public string Spki { get; set; } = "";
        public string Signature { get; set; } = "";
        public byte[]? TsaToken { get; set; }
        public string? TsaUrl { get; set; }
        public string? TsaTime { get; set; }

        public CustodyAttestation ToAttestation() => new(
            Id, Guid.Parse(CaseId), Sequence, EntryHash,
            DateTimeOffset.Parse(SignedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Examiner, Spki, Signature, Fingerprint(Convert.FromBase64String(Spki)),
            TsaToken, TsaUrl,
            TsaTime is null ? null : DateTimeOffset.Parse(TsaTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }
}
