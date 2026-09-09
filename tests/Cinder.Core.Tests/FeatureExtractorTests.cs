using Cinder.Core.Analysis;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

public sealed class FeatureExtractorTests
{
    private static IEnumerable<string> Values(string id, string text)
        => FeatureExtractor.Extract(text, FeatureExtractor.Find(id)!).Select(h => h.Value);

    [Fact]
    public void Every_preset_has_a_unique_id()
        => FeatureExtractor.Presets.Select(p => p.Id).Should().OnlyHaveUniqueItems();

    [Fact]
    public void Email_addresses()
        => Values("email", "contact alice.b+tag@example.co.uk or bob@localhost now")
            .Should().Equal("alice.b+tag@example.co.uk");

    [Fact]
    public void Urls_stop_at_quotes_and_whitespace()
        => Values("url", "see \"https://example.com/a?b=1&c=2\" and ftp://x.y/z then done")
            .Should().Equal("https://example.com/a?b=1&c=2", "ftp://x.y/z");

    [Fact]
    public void Ipv4_rejects_out_of_range_octets()
        => Values("ipv4", "10.0.0.1 999.1.1.1 192.168.001.1 256.0.0.1 8.8.8.8")
            .Should().Equal("10.0.0.1", "8.8.8.8");

    [Fact]
    public void Ipv6_is_validated_by_the_runtime_parser()
    {
        Values("ipv6", "fe80::1 and 2001:db8:85a3::8a2e:370:7334 but not 12:34").Should()
            .Contain("fe80::1").And.Contain("2001:db8:85a3::8a2e:370:7334").And.NotContain("12:34");
    }

    [Fact]
    public void Credit_card_numbers_must_pass_luhn()
    {
        // 4111 1111 1111 1111 is the canonical Luhn-valid test PAN; 4111 1111 1111 1112 is not.
        Values("creditcard", "card 4111 1111 1111 1111 and 4111-1111-1111-1112 and 1234567890123")
            .Should().Equal("4111 1111 1111 1111");
    }

    [Theory]
    [InlineData("4111111111111111", true)]
    [InlineData("79927398713", false)]        // valid Luhn but only 11 digits
    [InlineData("0000000000000000", false)]   // repeated digit passes Luhn trivially — rejected
    [InlineData("4111111111111112", false)]
    public void Luhn(string digits, bool expected) => FeatureExtractor.Luhn(digits).Should().Be(expected);

    [Fact]
    public void Bitcoin_and_ethereum_addresses()
    {
        Values("bitcoin", "pay 1BvBMSEYstWetqTFn5Au4m4GFg7xJaNVN2 or bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq")
            .Should().HaveCount(2);
        Values("ethereum", "to 0x52908400098527886E0F7030069857D2E4169EE7")
            .Should().ContainSingle();
    }

    [Fact]
    public void Hashes_by_length()
    {
        var md5 = new string('a', 32);
        var sha1 = new string('b', 40);
        var sha256 = new string('c', 64);
        var text = $"{md5} {sha1} {sha256}";
        Values("md5", text).Should().Equal(md5);
        Values("sha1", text).Should().Equal(sha1);
        Values("sha256", text).Should().Equal(sha256);
    }

    [Fact]
    public void Windows_paths_and_unc()
        => Values("winpath", @"open C:\Users\x\Desktop\notes.txt then \\server\share\file.docx ok")
            .Should().Equal(@"C:\Users\x\Desktop\notes.txt", @"\\server\share\file.docx");

    [Fact]
    public void Domain_preset_excludes_obvious_file_names()
        => Values("domain", "kernel32.dll loaded from evil.example.com log.txt")
            .Should().Equal("evil.example.com");

    [Fact]
    public void Credential_shaped_tokens()
    {
        // AKIAIOSFODNN7EXAMPLE is Amazon's own documented placeholder key id, allow-listed by
        // secret scanners. The JWT is assembled here from throwaway parts rather than written
        // as a literal: a token-shaped string in a public repository trips secret scanning
        // forever, whether or not it was ever issued by anything.
        Values("awskey", "key AKIAIOSFODNN7EXAMPLE here").Should().Equal("AKIAIOSFODNN7EXAMPLE");

        static string B64Url(string s) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var fakeJwt = B64Url("{\"alg\":\"none\"}") + "." + B64Url("{\"sub\":\"fixture\"}") + "." + B64Url("not-a-signature");
        fakeJwt.Should().StartWith("eyJ");
        Values("jwt", $"bearer {fakeJwt} end").Should().Equal(fakeJwt);

        // Assembled at runtime for the same reason as the JWT: a PEM header literal is a
        // secret-scanner hit whether or not any key material follows it.
        var pemHeader = "-----BEGIN " + "OPENSSH PRIVATE KEY" + "-----";
        Values("privatekey", pemHeader + "\nabc").Should().ContainSingle();
    }

    [Fact]
    public void Base64_must_decode()
    {
        var good = Convert.ToBase64String(new byte[45]);     // 60 chars, valid
        Values("base64", $"blob {good} end").Should().Equal(good);
        Values("base64", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA").Should().BeEmpty("length not a multiple of 4");
    }

    [Fact]
    public void IsMatch_reports_presence_without_materialising_hits()
    {
        FeatureExtractor.Find("email")!.IsMatch("x a@b.io y").Should().BeTrue();
        FeatureExtractor.Find("email")!.IsMatch("nothing here").Should().BeFalse();
        FeatureExtractor.Find("email")!.IsMatch("").Should().BeFalse();
    }

    [Fact]
    public void Pathological_input_does_not_hang()
    {
        // A long run designed to make a careless phone/card regex backtrack. Every preset has
        // a match timeout, so the worst case is "no hits", never a stall.
        var nasty = new string('1', 20_000) + new string('-', 20_000) + "x";
        Action act = () => { _ = FeatureExtractor.ExtractAll(nasty).ToList(); };
        act.ExecutionTime().Should().BeLessThan(TimeSpan.FromSeconds(10));
    }
}
