using Cinder.Core.Analysis;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

public sealed class IocListTests
{
    [Fact]
    public void Classifies_each_line_by_shape()
    {
        var text = """
            # C2 infrastructure
            185.220.101.5
            2001:db8::dead:beef
            evil.example.com
            https://evil.example.com/gate.php
            ops@evil.example.com

            // hashes
            d41d8cd98f00b204e9800998ecf8427e
            da39a3ee5e6b4b0d3255bfef95601890afd80709
            e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
            mutex_Global\PoisonIvy
            """;

        var iocs = IocList.Parse(text);

        iocs.Select(i => i.Type).Should().Equal(
            IndicatorType.Ipv4, IndicatorType.Ipv6, IndicatorType.Domain, IndicatorType.Url, IndicatorType.Email,
            IndicatorType.Md5, IndicatorType.Sha1, IndicatorType.Sha256, IndicatorType.Text);
        iocs.Last().Value.Should().Be(@"mutex_Global\PoisonIvy");
        iocs.Count(i => i.IsHash).Should().Be(3);
    }

    [Fact]
    public void Ignores_blank_lines_comments_and_duplicates()
    {
        var iocs = IocList.Parse("\n\n# x\nevil.com\nEVIL.COM\n   \nevil.com   # again\n");
        iocs.Should().ContainSingle();
        iocs[0].Line.Should().Be(4);
    }

    [Fact]
    public void Tolerates_csv_style_rows_by_taking_the_classifiable_token()
    {
        var iocs = IocList.Parse("ip,185.220.101.5,tor exit\n\"sha1\",\"da39a3ee5e6b4b0d3255bfef95601890afd80709\"\nplain text,desc\n");
        iocs.Should().HaveCount(3);
        iocs[0].Should().Be(new Indicator("185.220.101.5", IndicatorType.Ipv4, 1));
        iocs[1].Type.Should().Be(IndicatorType.Sha1);
        iocs[2].Value.Should().Be("plain text");
    }

    [Fact]
    public void A_domain_that_looks_like_a_filename_is_text()
        => IocList.Classify("payload.exe").Should().Be(IndicatorType.Text);

    [Fact]
    public void Partial_matches_do_not_promote_text_to_a_typed_indicator()
        => IocList.Classify("contact ops@evil.example.com now").Should().Be(IndicatorType.Text);
}
