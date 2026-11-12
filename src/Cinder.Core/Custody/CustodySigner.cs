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

    /// <summary>
    /// Which signed payload the signature verifies against: <c>2</c> covers the examiner name
    /// as well as case, sequence, entry hash and time; <c>1</c> is the original payload,
    /// which does not cover the examiner name; <c>0</c> means the signature verifies against
    /// neither (or has not been checked).
    /// </summary>
    public int PayloadVersion { get; init; }
}

/// <summary>A public key the verifier accepts as belonging to a known examiner.</summary>
/// <param name="Fingerprint">Display fingerprint, <c>SHA256:</c> + 32 hex digits of SHA-256 over the SPKI.</param>
/// <param name="Source">Where the trust came from, for display: the local examiner key, a file, or the caller.</param>
public sealed record TrustedKey(string Fingerprint, string Source);

public sealed record AttestationVerification(
    CustodyAttestation Attestation,
    bool SignatureValid,
    bool ChainMatches,
    string? Reason,
    TimestampVerification? Timestamp = null)
{
    /// <summary>
    /// True when the key that made the signature is one this verifier trusts: the local
    /// examiner key, a key in the trusted-keys directory, or one the caller supplied. False for
    /// a key nobody vouched for — the signature may be mathematically perfect and still mean
    /// nothing, because anyone who can write the case file can generate a key and sign with it.
    /// </summary>
    public bool KeyTrusted { get; init; }

    /// <summary>Where the trust in the signing key came from; null when <see cref="KeyTrusted"/> is false.</summary>
    public string? KeyTrustSource { get; init; }

    /// <summary>
    /// True when the signature also covers the examiner name (payload version 2). For an
    /// attestation made before that was added, the name beside the signature is a label
    /// anyone with write access to the case file could have changed.
    /// </summary>
    public bool ExaminerBound => SignatureValid && Attestation.PayloadVersion >= 2;

    /// <summary>
    /// The signature is valid but was made by a key this verifier does not trust. Distinct
    /// from a bad signature: nothing is provably wrong, but nothing is proven either.
    /// </summary>
    public bool SignedByUnknownKey => SignatureValid && !KeyTrusted;

    /// <summary>
    /// Signature valid, made by a trusted key, chain unchanged at the attested point and —
    /// when a timestamp is present — the timestamp holds.
    /// </summary>
    public bool Ok => SignatureValid && KeyTrusted && ChainMatches && (Timestamp is null || Timestamp.SignatureValid);
}

/// <summary>
/// The external anchor the custody chain lacked. The chain itself is an unkeyed hash stored
/// beside the entries it protects, so anyone who can write the case file can recompute it. An
/// attestation binds the chain's tip to something that lives outside the case file: an
/// examiner signing key (ECDSA P-256) held in the examiner's own profile.
///
/// <para><b>Trust model.</b> Every attestation carries the public key that verifies it, and
/// that key is stored in the case file next to the signature. A signature that verifies under
/// its own embedded key therefore proves only that <em>someone</em> signed — an attacker who
/// rewrites the log can delete the examiner's attestation and insert one made with a key
/// generated five minutes ago. What makes an attestation mean something is that its key is one
/// the verifier already knows, from outside the case file. <see cref="VerifyAsync(Guid, CancellationToken)"/>
/// accepts a key as trusted when it is:</para>
/// <list type="number">
/// <item>the local examiner key (the private key at <see cref="KeyPath"/>, or its <c>.pub</c>
/// if only that is present);</item>
/// <item>a public key file in <see cref="TrustedKeysDirectory"/> — the place to drop a
/// colleague's <c>.pub</c> after checking its fingerprint with them over another channel;</item>
/// <item>a key the caller passes explicitly, as base64 SPKI or as a fingerprint.</item>
/// </list>
/// <para>An attestation whose signature is valid but whose key is none of those is reported
/// with <see cref="AttestationVerification.SignedByUnknownKey"/> and is not
/// <see cref="AttestationVerification.Ok"/>. Verification never creates a key.</para>
///
/// <para>With a trusted key the guarantee is: nobody who lacks that key can alter the log up
/// to the attested point without it showing. It is still not proof against the examiner
/// themself; publishing the attestation (the JSON that <see cref="ExportAttestation"/>
/// produces) somewhere the examiner does not control is what closes that, and is a process
/// decision rather than code.</para>
///
/// <para><b>What is signed.</b> Payload version 2 —
/// <c>cinder-custody-attestation/2|case_id|sequence|entry_hash|signed_utc|examiner</c> — binds
/// the examiner name. Version 1 (<c>case_id|sequence|entry_hash|signed_utc</c>) did not, so the
/// name on a version-1 attestation is unauthenticated; those still verify and are reported
/// with <see cref="AttestationVerification.ExaminerBound"/> false. New attestations are always
/// version 2.</para>
///
/// <para>Key storage: PKCS#8 at <c>&lt;profile&gt;/Cinder/examiner-signing-key.p8</c>, mode 0600 on
/// Unix; the Windows copy relies on the profile ACL.</para>
/// </summary>
public sealed class CustodySigner
{
    private const string PayloadV2Prefix = "cinder-custody-attestation/2";

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

    /// <summary>
    /// Directory of other examiners' public keys this workstation trusts: <c>trusted-keys</c>
    /// beside the examiner key. Each <c>*.pub</c> file is one SubjectPublicKeyInfo, as raw DER
    /// (what Cinder writes next to a signing key), base64, or PEM.
    /// </summary>
    public string TrustedKeysDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_keyPath))!, "trusted-keys");

    /// <summary>Loads the examiner key, creating it on first use.</summary>
    public ECDsa LoadOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(File.ReadAllBytes(_keyPath), out _);
            }
            catch
            {
                key.Dispose();
                throw;
            }
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

    /// <summary>
    /// The keys <see cref="VerifyAsync(Guid, CancellationToken)"/> trusts on this workstation:
    /// the local examiner key, then every readable key in <see cref="TrustedKeysDirectory"/>,
    /// then <paramref name="additional"/> (each a base64 SPKI or a <c>SHA256:…</c>
    /// fingerprint). Unreadable or malformed entries are skipped — a file that is not a public
    /// key grants no trust. Never creates a key.
    /// </summary>
    public IReadOnlyList<TrustedKey> GetTrustedKeys(IEnumerable<string>? additional = null)
    {
        var keys = new List<TrustedKey>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? fingerprint, string source)
        {
            if (fingerprint is not null && seen.Add(fingerprint))
            {
                keys.Add(new TrustedKey(fingerprint, source));
            }
        }

        Add(LocalKeyFingerprint(), "local examiner key");

        try
        {
            var dir = TrustedKeysDirectory;
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.pub").Order(StringComparer.Ordinal))
                {
                    Add(FingerprintOfKeyFile(file), "trusted-keys/" + Path.GetFileName(file));
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (additional is not null)
        {
            foreach (var item in additional)
            {
                Add(FingerprintOfSuppliedKey(item), "supplied by caller");
            }
        }
        return keys;
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
        var signature = key.SignData(PayloadV2(caseId, tip.Sequence, tip.EntryHash, signedUtc, examiner), HashAlgorithmName.SHA256);

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
            Convert.ToBase64String(spki), Convert.ToBase64String(signature), Fingerprint(spki))
        {
            PayloadVersion = 2,
        };
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
    /// Checks every attestation for the case against the keys this workstation trusts (see
    /// <see cref="GetTrustedKeys"/>): the signature must verify under the embedded public key,
    /// that key must be a trusted one, and the chain entry at the attested sequence must still
    /// carry the attested hash. A rewrite after signing fails the last check; a tampered
    /// attestation fails the first; an attestation re-made with somebody else's key fails the
    /// second.
    /// </summary>
    public Task<IReadOnlyList<AttestationVerification>> VerifyAsync(Guid caseId, CancellationToken ct = default)
        => VerifyAsync(caseId, trustedKeys: null, ct);

    /// <summary>
    /// As <see cref="VerifyAsync(Guid, CancellationToken)"/>, also trusting
    /// <paramref name="trustedKeys"/>: each a base64 SubjectPublicKeyInfo (the
    /// <c>public_key_spki</c> of an exported attestation, or the contents of a <c>.pub</c>
    /// file) or a <c>SHA256:…</c> fingerprint as Cinder displays it. Only pass keys whose
    /// origin has been checked outside the case file being verified — a key copied out of
    /// that file proves nothing about it.
    /// </summary>
    public async Task<IReadOnlyList<AttestationVerification>> VerifyAsync(
        Guid caseId, IEnumerable<string>? trustedKeys, CancellationToken ct = default)
    {
        var attestations = await ListAsync(caseId, ct).ConfigureAwait(false);
        var entries = await new CustodyLog(_store).ListAsync(caseId, ct).ConfigureAwait(false);
        var bySequence = new Dictionary<long, string>();
        foreach (var e in entries)
        {
            bySequence.TryAdd(e.Sequence, e.EntryHash);   // a duplicated sequence is the chain's problem, not a crash here
        }
        var trusted = GetTrustedKeys(trustedKeys);

        var results = new List<AttestationVerification>(attestations.Count);
        foreach (var a in attestations)
        {
            ct.ThrowIfCancellationRequested();
            var sigOk = a.PayloadVersion > 0;
            var trust = sigOk ? trusted.FirstOrDefault(k => string.Equals(k.Fingerprint, a.KeyFingerprint, StringComparison.OrdinalIgnoreCase)) : null;
            var chainOk = bySequence.TryGetValue(a.Sequence, out var hash) && string.Equals(hash, a.EntryHash, StringComparison.Ordinal);

            TimestampVerification? ts = null;
            if (a.HasTimestamp)
            {
                ts = TryDecodeBase64(a.SignatureBase64) is { } signatureBytes
                    ? Rfc3161Timestamper.Verify(a.TsaToken!, signatureBytes)
                    : new TimestampVerification(false, false, null, null, "the attestation's signature is not valid base64, so the timestamp cannot be checked against it");
            }

            var reasons = new List<string>();
            if (!sigOk)
            {
                reasons.Add("signature does not verify under the embedded public key");
            }
            else
            {
                if (trust is null)
                {
                    reasons.Add($"signed by a key this workstation does not trust ({a.KeyFingerprint}) — the signature is valid, but it only means something once that fingerprint is confirmed with the examiner and their public key is added to the trusted keys");
                }
                if (!chainOk)
                {
                    reasons.Add(bySequence.ContainsKey(a.Sequence)
                        ? $"entry {a.Sequence} now hashes differently from what was attested — the log was altered after signing"
                        : $"chain no longer has an entry at sequence {a.Sequence}");
                }
                if (ts is { SignatureValid: false } && ts.Reason is not null)
                {
                    reasons.Add(ts.Reason);
                }
            }

            results.Add(new AttestationVerification(a, sigOk, chainOk, reasons.Count == 0 ? null : string.Join("; ", reasons), ts)
            {
                KeyTrusted = trust is not null,
                KeyTrustSource = trust?.Source,
            });
        }
        return results;
    }

    /// <summary>
    /// True when the signature verifies under the attestation's own embedded public key, for
    /// either payload version. This is the cryptographic check only: it says nothing about
    /// whose key that is — see <see cref="AttestationVerification.KeyTrusted"/>.
    /// </summary>
    public static bool VerifySignature(CustodyAttestation a) => VerifiedPayloadVersion(a) > 0;

    /// <summary>
    /// The payload version the signature verifies against under the embedded key: 2, 1, or 0
    /// when it verifies against neither or any field is malformed.
    /// </summary>
    public static int VerifiedPayloadVersion(CustodyAttestation a)
    {
        ArgumentNullException.ThrowIfNull(a);
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(a.PublicKeySpkiBase64), out _);
            var signature = Convert.FromBase64String(a.SignatureBase64);
            if (key.VerifyData(PayloadV2(a.CaseId, a.Sequence, a.EntryHash, a.SignedUtc, a.Examiner), signature, HashAlgorithmName.SHA256))
            {
                return 2;
            }
            if (key.VerifyData(Payload(a.CaseId, a.Sequence, a.EntryHash, a.SignedUtc), signature, HashAlgorithmName.SHA256))
            {
                return 1;
            }
            return 0;
        }
        catch (CryptographicException) { return 0; }
        catch (FormatException) { return 0; }
    }

    /// <summary>
    /// The attestation as a self-contained JSON document. Publish it somewhere outside the
    /// examiner's control — mail it to a supervisor, a ticket, a WORM share — and the chain
    /// gains an anchor that even the examiner cannot move.
    /// </summary>
    public static string ExportAttestation(CustodyAttestation a)
    {
        var version = a.PayloadVersion > 0 ? a.PayloadVersion : VerifiedPayloadVersion(a);
        return System.Text.Json.JsonSerializer.Serialize(new
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
            payload_version = version,
            signed_payload = version >= 2
                ? PayloadV2Prefix + "|case_id|sequence|entry_hash|signed_utc|examiner (UTF-8, ISO 8601 round-trip)"
                : "case_id|sequence|entry_hash|signed_utc (UTF-8, ISO 8601 round-trip) — the examiner name is not covered by the signature",
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
    }

    /// <summary>Payload version 1: does not cover the examiner name. Verified for old attestations, never signed.</summary>
    internal static byte[] Payload(Guid caseId, long sequence, string entryHash, DateTimeOffset signedUtc) =>
        Encoding.UTF8.GetBytes(string.Join('|',
            caseId.ToString("D"),
            sequence.ToString(CultureInfo.InvariantCulture),
            entryHash,
            signedUtc.ToString("O", CultureInfo.InvariantCulture)));

    /// <summary>
    /// Payload version 2. The leading tag keeps it from ever colliding with a version-1
    /// payload; the examiner name comes last, so a '|' inside it cannot shift a field.
    /// </summary>
    internal static byte[] PayloadV2(Guid caseId, long sequence, string entryHash, DateTimeOffset signedUtc, string examiner) =>
        Encoding.UTF8.GetBytes(string.Join('|',
            PayloadV2Prefix,
            caseId.ToString("D"),
            sequence.ToString(CultureInfo.InvariantCulture),
            entryHash,
            signedUtc.ToString("O", CultureInfo.InvariantCulture),
            examiner));

    internal static string Fingerprint(byte[] spki) => "SHA256:" + Convert.ToHexStringLower(SHA256.HashData(spki))[..32];

    private static byte[]? TryDecodeBase64(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }

    /// <summary>Fingerprint for an SPKI that actually parses as an EC public key; null otherwise.</summary>
    private static string? FingerprintOfSpki(byte[]? spki)
    {
        if (spki is null || spki.Length == 0)
        {
            return null;
        }
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            return read == spki.Length ? Fingerprint(spki) : null;
        }
        catch (CryptographicException) { return null; }
    }

    private string? LocalKeyFingerprint()
    {
        try
        {
            if (File.Exists(_keyPath))
            {
                using var key = ECDsa.Create();
                key.ImportPkcs8PrivateKey(File.ReadAllBytes(_keyPath), out _);
                return Fingerprint(key.ExportSubjectPublicKeyInfo());
            }
            var pub = _keyPath + ".pub";
            return File.Exists(pub) ? FingerprintOfKeyFile(pub) : null;
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>A public-key file: raw SPKI DER, base64 of it, or a PEM "PUBLIC KEY" block.</summary>
    private static string? FingerprintOfKeyFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > 16 * 1024)
            {
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            return FingerprintOfSpki(bytes) ?? FingerprintOfSuppliedKey(Encoding.UTF8.GetString(bytes));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>A caller-supplied key: a <c>SHA256:</c> fingerprint, base64 SPKI, or PEM.</summary>
    private static string? FingerprintOfSuppliedKey(string? item)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            return null;
        }
        var text = item.Trim();
        if (text.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            var hex = text[7..];
            return hex.Length == 32 && hex.All(char.IsAsciiHexDigit) ? "SHA256:" + hex.ToLowerInvariant() : null;
        }
        if (text.StartsWith("-----BEGIN", StringComparison.Ordinal))
        {
            text = string.Concat(text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)));
        }
        return FingerprintOfSpki(TryDecodeBase64(text));
    }

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

        /// <summary>
        /// Never throws on a malformed row. The case file is evidence-adjacent and may have
        /// been edited by hand; a row that does not parse must come out as an attestation
        /// that fails verification, not as an exception that stops every other attestation
        /// from being checked.
        /// </summary>
        public CustodyAttestation ToAttestation()
        {
            var caseId = Guid.TryParse(CaseId, out var g) ? g : Guid.Empty;
            var signedUtc = DateTimeOffset.TryParse(SignedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var s) ? s : default;
            DateTimeOffset? tsaTime = TsaTime is not null
                && DateTimeOffset.TryParse(TsaTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
            var fingerprint = TryDecodeBase64(Spki) is { } spki ? Fingerprint(spki) : "(unreadable public key)";

            var a = new CustodyAttestation(Id, caseId, Sequence, EntryHash, signedUtc, Examiner, Spki, Signature, fingerprint,
                TsaToken, TsaUrl, tsaTime);
            return a with { PayloadVersion = VerifiedPayloadVersion(a) };
        }
    }
}
