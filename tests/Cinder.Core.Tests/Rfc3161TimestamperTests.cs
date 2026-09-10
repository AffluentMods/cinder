using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using Cinder.Core.Cases;
using Cinder.Core.Custody;
using FluentAssertions;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// The TSA under test is a BouncyCastle-generated one running on a loopback listener, so the
/// whole RFC 3161 exchange — request encoding, nonce, imprint, response parsing, token
/// verification — is exercised against an implementation that is not .NET's own.
/// </summary>
public sealed class Rfc3161TimestamperTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-tsa").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Round_trips_a_token_and_verifies_it_against_the_data()
    {
        using var tsa = new FakeTsa();
        var data = RandomNumberGenerator.GetBytes(64);
        using var http = new HttpClient();

        var stamp = await Rfc3161Timestamper.TimestampAsync(data, tsa.Url, http);
        stamp.Token.Should().NotBeEmpty();
        stamp.Time.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
        stamp.TsaSubject.Should().Contain("Cinder Test TSA");

        var v = Rfc3161Timestamper.Verify(stamp.Token, data);
        v.SignatureValid.Should().BeTrue(v.Reason);
        v.Time.Should().Be(stamp.Time);
        v.ChainTrusted.Should().BeFalse("a self-signed test TSA is not in any trust store — and the verdict must say so rather than pretend");

        // A token is bound to the data it was issued for.
        var other = (byte[])data.Clone();
        other[0] ^= 1;
        Rfc3161Timestamper.Verify(stamp.Token, other).SignatureValid.Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_a_response_whose_nonce_does_not_match()
    {
        using var tsa = new FakeTsa { BreakNonce = true };
        using var http = new HttpClient();
        var act = async () => await Rfc3161Timestamper.TimestampAsync(new byte[] { 1, 2, 3 }, tsa.Url, http);
        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task Refuses_plain_http_to_anything_but_loopback()
    {
        using var http = new HttpClient();
        var act = async () => await Rfc3161Timestamper.TimestampAsync(new byte[] { 1 }, new Uri("http://timestamp.example.com/tsr"), http);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Attestation_gains_a_timestamp_that_survives_reload_and_fails_when_the_signature_is_swapped()
    {
        var store = new CaseStore(Path.Combine(_dir, "case.cinder"));
        store.Migrate();
        var log = new CustodyLog(store);
        var c = await new CaseService(store, log).CreateAsync("tsa", "alice", null);
        await log.AppendAsync(c.Id, "alice", CustodyAction.ParserRan, "{}");

        var signer = new CustodySigner(store, Path.Combine(_dir, "key.p8"));
        var a = await signer.SignTipAsync(c.Id, "alice");
        a.HasTimestamp.Should().BeFalse();

        using var tsa = new FakeTsa();
        using var http = new HttpClient();
        var stamped = await signer.TimestampAsync(a.Id, tsa.Url, http);
        stamped.HasTimestamp.Should().BeTrue();
        stamped.TsaUrl.Should().Be(tsa.Url.ToString());

        var reloaded = (await signer.ListAsync(c.Id)).Single();
        reloaded.TsaToken.Should().Equal(stamped.TsaToken);
        var v = (await signer.VerifyAsync(c.Id)).Single();
        v.Ok.Should().BeTrue(v.Reason);
        v.Timestamp!.SignatureValid.Should().BeTrue();

        var json = CustodySigner.ExportAttestation(reloaded);
        json.Should().Contain("rfc3161_timestamp").And.Contain("token_der");

        // Re-sign the same tip with a fresh key and graft the old token onto it: the token
        // covers the old signature bytes, so it must not verify against the new ones.
        var other = new CustodySigner(store, Path.Combine(_dir, "other.p8"));
        var b = await other.SignTipAsync(c.Id, "mallory");
        await using (var conn = store.Open())
        {
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE custody_attestations SET tsa_token = @T, tsa_url = 'x', tsa_time = @W WHERE id = @Id",
                new { T = stamped.TsaToken, W = stamped.TsaTime!.Value.ToString("O"), Id = b.Id });
        }
        var results = await signer.VerifyAsync(c.Id);
        results.Should().HaveCount(2);
        results[1].SignatureValid.Should().BeTrue("the ECDSA signature itself is fine");
        results[1].Timestamp!.SignatureValid.Should().BeFalse("the grafted token does not cover this signature");
        results[1].Ok.Should().BeFalse();
    }

    /// <summary>A minimal RFC 3161 responder: self-signed RSA cert with the timeStamping EKU, BouncyCastle TSP.</summary>
    private sealed class FakeTsa : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly AsymmetricCipherKeyPair _key;
        private readonly X509Certificate _cert;
        private readonly CancellationTokenSource _cts = new();
        private long _serial = 1;

        public bool BreakNonce { get; init; }
        public Uri Url { get; }

        public FakeTsa()
        {
            var gen = new RsaKeyPairGenerator();
            gen.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
            _key = gen.GenerateKeyPair();

            var name = new X509Name("CN=Cinder Test TSA, O=Cinder tests");
            var cg = new X509V3CertificateGenerator();
            cg.SetSerialNumber(BigInteger.One);
            cg.SetIssuerDN(name);
            cg.SetSubjectDN(name);
            cg.SetNotBefore(DateTime.UtcNow.AddDays(-1));
            cg.SetNotAfter(DateTime.UtcNow.AddDays(1));
            cg.SetPublicKey(_key.Public);
            cg.AddExtension(X509Extensions.ExtendedKeyUsage, true, new ExtendedKeyUsage(KeyPurposeID.id_kp_timeStamping));
            cg.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
            _cert = cg.Generate(new Asn1SignatureFactory("SHA256WITHRSA", _key.Private));

            var port = FreePort();
            Url = new Uri($"http://127.0.0.1:{port}/tsr");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                try
                {
                    using var ms = new MemoryStream();
                    await ctx.Request.InputStream.CopyToAsync(ms);
                    var request = new TimeStampRequest(ms.ToArray());

                    var tokenGen = new TimeStampTokenGenerator(_key.Private, _cert, TspAlgorithms.Sha256, "1.3.6.1.4.1.99999.1");
                    tokenGen.SetCertificates(CollectionUtilities.CreateStore(new[] { _cert }));
                    var responseGen = new TimeStampResponseGenerator(tokenGen, TspAlgorithms.Allowed);

                    var effective = BreakNonce
                        ? new TimeStampRequestGenerator().Generate(TspAlgorithms.Sha256, request.GetMessageImprintDigest(), BigInteger.ValueOf(424242))
                        : request;
                    var response = responseGen.Generate(effective, BigInteger.ValueOf(Interlocked.Increment(ref _serial)), DateTime.UtcNow);
                    var bytes = response.GetEncoded();

                    ctx.Response.ContentType = "application/timestamp-reply";
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.Close(bytes, willBlock: true);
                }
                catch
                {
                    try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
                }
            }
        }

        private static int FreePort()
        {
            using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
        }
    }
}
