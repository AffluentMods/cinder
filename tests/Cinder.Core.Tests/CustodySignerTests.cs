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
    public async Task Verification_needs_only_the_case_file_not_the_key()
    {
        var (store, _, caseId) = await NewCaseAsync();
        await new CustodySigner(store, KeyPath()).SignTipAsync(caseId, "alice");

        // A second signer with no access to the original key (fresh path) can still verify.
        var verifier = new CustodySigner(store, KeyPath("someone-elses-key.p8"));
        (await verifier.VerifyAsync(caseId)).Single().Ok.Should().BeTrue();
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
