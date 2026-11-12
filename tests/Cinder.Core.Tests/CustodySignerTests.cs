using Cinder.Core.Cases;
using Cinder.Core.Custody;
using Dapper;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

public sealed class CustodySignerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-attest").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task<(CaseStore Store, CustodyLog Log, Guid CaseId)> NewCaseAsync(string name = "case")
    {
        var store = new CaseStore(Path.Combine(_dir, name + ".cinder"));
        store.Migrate();
        var log = new CustodyLog(store);
        var c = await new CaseService(store, log).CreateAsync(name, "alice", null);
        await log.AppendAsync(c.Id, "alice", CustodyAction.ParserRan, "{\"tool\":\"registry\"}");
        await log.AppendAsync(c.Id, "alice", CustodyAction.EvidenceHashed, "{\"sha256\":\"abc\"}");
        return (store, log, c.Id);
    }

    private string KeyPath(string name = "key.p8") => Path.Combine(_dir, name);

    [Fact]
    public async Task Signing_the_tip_creates_a_key_on_first_use_and_verifies()
    {
        var (store, _, caseId) = await NewCaseAsync();
        var signer = new CustodySigner(store, KeyPath());

        File.Exists(KeyPath()).Should().BeFalse();
        var a = await signer.SignTipAsync(caseId, "alice");
        File.Exists(KeyPath()).Should().BeTrue();
        File.Exists(KeyPath() + ".pub").Should().BeTrue();

        a.Sequence.Should().Be(3, "genesis + two appends");
        a.KeyFingerprint.Should().StartWith("SHA256:");
        CustodySigner.VerifySignature(a).Should().BeTrue();

        var results = await signer.VerifyAsync(caseId);
        results.Should().ContainSingle().Which.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task A_rewrite_after_signing_is_detected_even_though_the_chain_itself_re_verifies()
    {
        var (store, log, caseId) = await NewCaseAsync();
        var signer = new CustodySigner(store, KeyPath());
        await signer.SignTipAsync(caseId, "alice");

        // The attack the plain chain cannot see: rewrite an entry and recompute every hash
        // forward, exactly as someone with write access to the file would.
        await RewriteEntryAndRechainAsync(store, log, caseId, sequence: 2, newDetails: "{\"tool\":\"nothing-to-see\"}");
        (await log.VerifyAsync(caseId)).Ok.Should().BeTrue("the unkeyed chain is consistent after a full rewrite");

        var results = await signer.VerifyAsync(caseId);
        var r = results.Should().ContainSingle().Subject;
        r.SignatureValid.Should().BeTrue("the attestation itself was not touched");
        r.ChainMatches.Should().BeFalse("entry 3 no longer hashes to what was attested");
        r.Reason.Should().Contain("altered after signing");
    }

    [Fact]
    public async Task A_forged_attestation_fails_signature_verification()
    {
        var (store, _, caseId) = await NewCaseAsync();
        var signer = new CustodySigner(store, KeyPath());
        var a = await signer.SignTipAsync(caseId, "alice");

        // Tamper with the stored attestation so it claims a different hash.
        await using (var conn = store.Open())
        {
            await conn.ExecuteAsync("UPDATE custody_attestations SET entry_hash = @H WHERE id = @Id",
                new { H = new string('0', 64), a.Id });
        }

        var r = (await signer.VerifyAsync(caseId)).Single();
        r.SignatureValid.Should().BeFalse();
        r.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task A_valid_signature_from_a_key_the_verifier_does_not_know_is_not_verified()
    {
        // This test used to be "Verification_needs_only_the_case_file_not_the_key" and asserted
        // Ok for a verifier that had never seen the signing key. That is the defect: the key
        // travels in the case file beside the signature, so "verifies under its own key" is
        // true of anything anyone signs. The signature is still checkable from the case file
        // alone; whether to believe it needs the key to be known from somewhere else.
        var (store, _, caseId) = await NewCaseAsync();
        var a = await new CustodySigner(store, KeyPath()).SignTipAsync(caseId, "alice");

        var verifier = new CustodySigner(store, KeyPath("someone-elses-key.p8"));
        var r = (await verifier.VerifyAsync(caseId)).Single();

        r.SignatureValid.Should().BeTrue("the cryptographic check needs nothing but the case file");
        r.ChainMatches.Should().BeTrue();
        r.KeyTrusted.Should().BeFalse();
        r.SignedByUnknownKey.Should().BeTrue();
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("does not trust").And.Contain(a.KeyFingerprint);
        File.Exists(KeyPath("someone-elses-key.p8")).Should().BeFalse("verifying must never create a key");
    }

    [Fact]
    public async Task An_attestation_verifies_for_another_examiner_once_its_key_is_trusted()
    {
        var (store, _, caseId) = await NewCaseAsync();
        var a = await new CustodySigner(store, KeyPath()).SignTipAsync(caseId, "alice");
        var verifier = new CustodySigner(store, Path.Combine(_dir, "bob", "key.p8"));

        // Supplied explicitly: as the public key, or as the fingerprint Cinder displays.
        var bySpki = (await verifier.VerifyAsync(caseId, [a.PublicKeySpkiBase64])).Single();
        bySpki.Ok.Should().BeTrue();
        bySpki.KeyTrustSource.Should().Be("supplied by caller");
        (await verifier.VerifyAsync(caseId, [a.KeyFingerprint])).Single().Ok.Should().BeTrue();
        (await verifier.VerifyAsync(caseId, ["SHA256:" + new string('0', 32)])).Single().Ok.Should().BeFalse();

        // Or installed: the signer's .pub dropped into the verifier's trusted-keys directory.
        (await verifier.VerifyAsync(caseId)).Single().Ok.Should().BeFalse();
        Directory.CreateDirectory(verifier.TrustedKeysDirectory);
        File.Copy(KeyPath() + ".pub", Path.Combine(verifier.TrustedKeysDirectory, "alice.pub"));
        var installed = (await verifier.VerifyAsync(caseId)).Single();
        installed.Ok.Should().BeTrue();
        installed.KeyTrustSource.Should().Be("trusted-keys/alice.pub");

        // A file that is not a public key grants nothing.
        File.WriteAllText(Path.Combine(verifier.TrustedKeysDirectory, "junk.pub"), "not a key");
        verifier.GetTrustedKeys().Should().ContainSingle();
    }

    [Fact]
    public async Task A_rewritten_log_re_attested_with_a_fresh_key_is_reported_as_an_unknown_signer()
    {
        // The full attack: rewrite an entry, recompute the chain, throw away the real
        // attestation and sign the new tip with a key made for the purpose. Every hash and
        // every signature in the file is then internally consistent.
        var (store, log, caseId) = await NewCaseAsync();
        var signer = new CustodySigner(store, KeyPath());
        await signer.SignTipAsync(caseId, "alice");

        await RewriteEntryAndRechainAsync(store, log, caseId, sequence: 2, newDetails: "{\"tool\":\"nothing-to-see\"}");
        await using (var conn = store.Open())
        {
            await conn.ExecuteAsync("DELETE FROM custody_attestations");
        }
        var forged = await new CustodySigner(store, KeyPath("attacker.p8")).SignTipAsync(caseId, "alice");
        CustodySigner.VerifySignature(forged).Should().BeTrue("the forgery is cryptographically flawless");
        (await log.VerifyAsync(caseId)).Ok.Should().BeTrue();

        var r = (await signer.VerifyAsync(caseId)).Single();

        r.SignatureValid.Should().BeTrue();
        r.ChainMatches.Should().BeTrue();
        r.KeyTrusted.Should().BeFalse("it was not signed by this examiner key or any key trusted here");
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("does not trust");
    }

    [Fact]
    public async Task The_examiner_name_is_covered_by_the_signature()
    {
        var (store, _, caseId) = await NewCaseAsync();
        var signer = new CustodySigner(store, KeyPath());
        var a = await signer.SignTipAsync(caseId, "alice");
        a.PayloadVersion.Should().Be(2);
        (await signer.VerifyAsync(caseId)).Single().ExaminerBound.Should().BeTrue();

        await using (var conn = store.Open())
        {
            await conn.ExecuteAsync("UPDATE custody_attestations SET examiner = @E WHERE id = @Id", new { E = "mallory", a.Id });
        }

        var r = (await signer.VerifyAsync(caseId)).Single();
        r.SignatureValid.Should().BeFalse("attributing the signature to someone else must break it");
        r.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task An_attestation_made_before_the_examiner_was_signed_still_verifies_and_says_so()
    {
        var (store, log, caseId) = await NewCaseAsync();
        var signer = new CustodySigner(store, KeyPath());
        var tip = (await log.ListAsync(caseId))[^1];
        var signedUtc = DateTimeOffset.UtcNow;

        // Exactly what the previous version stored: a signature over case|sequence|hash|time.
        using (var key = signer.LoadOrCreateKey())
        {
            var signature = key.SignData(CustodySigner.Payload(caseId, tip.Sequence, tip.EntryHash, signedUtc),
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            await using var conn = store.Open();
            await conn.ExecuteAsync(
                """
                INSERT INTO custody_attestations (case_id, sequence, entry_hash, signed_utc, examiner, public_key_spki, signature)
                VALUES (@C, @S, @H, @T, @E, @K, @Sig)
                """,
                new
                {
                    C = caseId.ToString("D"),
                    S = tip.Sequence,
                    H = tip.EntryHash,
                    T = signedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    E = "alice",
                    K = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                    Sig = Convert.ToBase64String(signature),
                });
        }

        var r = (await signer.VerifyAsync(caseId)).Single();
        r.Ok.Should().BeTrue("existing attestations must keep verifying");
        r.Attestation.PayloadVersion.Should().Be(1);
        r.ExaminerBound.Should().BeFalse("the old payload never covered the examiner name");

        using var doc = System.Text.Json.JsonDocument.Parse(CustodySigner.ExportAttestation(r.Attestation));
        doc.RootElement.GetProperty("payload_version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_malformed_attestation_row_fails_verification_instead_of_throwing()
    {
        var (store, log, caseId) = await NewCaseAsync();
        var signer = new CustodySigner(store, KeyPath());
        var first = await signer.SignTipAsync(caseId, "alice");
        await log.AppendAsync(caseId, "alice", CustodyAction.Annotation, "{}");
        var second = await signer.SignTipAsync(caseId, "alice");

        await using (var conn = store.Open())
        {
            await conn.ExecuteAsync(
                "UPDATE custody_attestations SET public_key_spki = @K, signed_utc = @T, tsa_token = @Tok, tsa_time = @T WHERE id = @Id",
                new { K = "not base64!", T = "yesterday", Tok = new byte[] { 0 }, first.Id });
        }

        var results = await signer.VerifyAsync(caseId);

        results.Should().HaveCount(2);
        results.Single(r => r.Attestation.Id == first.Id).Ok.Should().BeFalse();
        results.Single(r => r.Attestation.Id == first.Id).SignatureValid.Should().BeFalse();
        results.Single(r => r.Attestation.Id == second.Id).Ok.Should().BeTrue("one bad row must not stop the others being checked");
    }

    [Fact]
    public async Task Export_is_a_self_contained_document()
    {
        var (store, _, caseId) = await NewCaseAsync();
        var a = await new CustodySigner(store, KeyPath()).SignTipAsync(caseId, "alice");

        var json = CustodySigner.ExportAttestation(a);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.GetProperty("format").GetString().Should().Be("cinder-custody-attestation/1");
        doc.RootElement.GetProperty("entry_hash").GetString().Should().Be(a.EntryHash);
        doc.RootElement.GetProperty("public_key_spki").GetString().Should().Be(a.PublicKeySpkiBase64);
        doc.RootElement.GetProperty("signature").GetString().Should().Be(a.SignatureBase64);
        doc.RootElement.GetProperty("payload_version").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("signed_payload").GetString().Should().Contain("examiner");
    }

    [Fact]
    public async Task Empty_chain_cannot_be_attested()
    {
        var store = new CaseStore(Path.Combine(_dir, "empty.cinder"));
        store.Migrate();
        var act = async () => await new CustodySigner(store, KeyPath()).SignTipAsync(Guid.NewGuid(), "alice");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>Alters one entry and recomputes the chain from it forward, as a full-rewrite attacker would.</summary>
    private static async Task RewriteEntryAndRechainAsync(CaseStore store, CustodyLog log, Guid caseId, long sequence, string newDetails)
    {
        var entries = await log.ListAsync(caseId);
        await using var conn = store.Open();
        var prev = entries.First(e => e.Sequence == sequence).PrevHash;
        foreach (var e in entries.Where(e => e.Sequence >= sequence).OrderBy(e => e.Sequence))
        {
            var details = e.Sequence == sequence ? newDetails : e.DetailsJson;
            var hash = Rehash(prev, e.Sequence, e.TimestampUtc, e.Examiner, e.Action, details);
            await conn.ExecuteAsync(
                "UPDATE custody_entries SET details_json=@D, prev_hash=@P, entry_hash=@H WHERE case_id=@C AND sequence=@S",
                new { D = details, P = prev, H = hash, C = caseId.ToString("D"), S = e.Sequence });
            prev = hash;
        }
    }

    private static string Rehash(string prev, long seq, DateTimeOffset ts, string examiner, string action, string details)
    {
        var payload = string.Join('', prev, seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ts.ToString("O", System.Globalization.CultureInfo.InvariantCulture), examiner, action, details);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
    }
}
