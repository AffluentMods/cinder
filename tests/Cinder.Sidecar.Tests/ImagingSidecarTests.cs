using System.Text.Json.Nodes;
using Cinder.Imaging;
using FluentAssertions;
using Xunit;

namespace Cinder.Sidecar.Tests;

/// <summary>ImageVerifier against canned <c>verify</c> replies.</summary>
public sealed class ImageVerifierTests
{
    private const string Sha = "ebdc4ebe3dfae2e0f48e1062f013a5cde19a684062ff69af218d0746b84ed4f1";
    private const string OtherSha = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string Md5 = "c71cfca7bd3b4e7128759b28a26baa0f";

    private static async Task<(ImageVerificationReport Report, VerificationResult Result)> Verify(string reply)
    {
        var report = await new ImageVerifier(FakeChannelFactory.Returning(reply).Open).VerifyDetailedAsync("image.dd");
        var result = await new ImageVerifier(FakeChannelFactory.Returning(reply).Open).VerifyAsync("image.dd");
        return (report, result);
    }

    [Fact]
    public async Task Matching_digests_are_verified()
    {
        var (report, result) = await Verify($$"""
            {"match": true, "reason": null, "expected_sha256": "{{Sha}}", "actual_sha256": "{{Sha}}",
             "expected_md5": "{{Md5.ToUpperInvariant()}}", "actual_md5": "{{Md5}}", "bytes_verified": 3146428}
            """);

        report.Status.Should().Be(ImageVerificationStatus.Verified);
        report.Match.Should().BeTrue();
        report.Reason.Should().BeNull();
        report.BytesVerified.Should().Be(3146428);
        result.Match.Should().BeTrue();
        result.ExpectedSha256.Should().Be(Sha);
    }

    [Fact]
    public async Task A_differing_digest_is_a_mismatch_with_its_reason()
    {
        var (report, result) = await Verify($$"""
            {"match": false, "reason": "computed digest differs from the recorded digest",
             "expected_sha256": "{{Sha}}", "actual_sha256": "{{OtherSha}}", "expected_md5": null, "actual_md5": "{{Md5}}",
             "bytes_verified": 10}
            """);

        report.Status.Should().Be(ImageVerificationStatus.Mismatch);
        report.Match.Should().BeFalse();
        report.Reason.Should().Be("computed digest differs from the recorded digest");
        result.Match.Should().BeFalse();
    }

    [Fact]
    public async Task No_reference_hash_is_its_own_outcome_not_a_mismatch_and_not_a_pass()
    {
        var (report, result) = await Verify($$"""
            {"match": null, "reason": "no reference hash: neither a .sha256 nor a .md5 companion file exists for this image",
             "expected_sha256": null, "actual_sha256": "{{Sha}}", "expected_md5": null, "actual_md5": "{{Md5}}",
             "bytes_verified": 3146428}
            """);

        report.Status.Should().Be(ImageVerificationStatus.NoReference);
        report.Match.Should().BeNull("nothing was compared, so nothing is known");
        report.Reason.Should().StartWith("no reference hash");
        report.ActualSha256.Should().Be(Sha, "the computed digest is still worth showing");
        result.Match.Should().BeFalse("the two-state result can only say 'not verified'");
    }

    [Fact]
    public async Task A_claimed_match_with_nothing_to_compare_against_is_not_believed()
    {
        // What the worker answered before it was fixed: match=true with no companion file.
        var (report, result) = await Verify($$"""
            {"match": true, "expected_sha256": null, "actual_sha256": "{{Sha}}", "expected_md5": null, "actual_md5": "{{Md5}}",
             "bytes_verified": 5}
            """);

        report.Status.Should().Be(ImageVerificationStatus.NoReference);
        report.Reason.Should().NotBeNullOrEmpty();
        result.Match.Should().BeFalse();
    }

    [Fact]
    public async Task A_claimed_match_whose_digests_differ_is_a_mismatch()
    {
        var (report, _) = await Verify($$"""
            {"match": true, "expected_sha256": "{{Sha}}", "actual_sha256": "{{OtherSha}}", "bytes_verified": 5}
            """);

        report.Status.Should().Be(ImageVerificationStatus.Mismatch);
        report.Reason.Should().Contain("SHA-256");
    }

    [Fact]
    public async Task Progress_comes_from_the_workers_notifications()
    {
        var factory = new FakeChannelFactory(() => new FakeSidecarChannel(call =>
        {
            call.Params["image_path"]!.GetValue<string>().Should().Be("evidence.dd");
            call.ReportProgress("""{"id": 1, "bytes_verified": 1048576}""");
            call.ReportProgress("""{"id": 1}""");
            call.ReportProgress("""{"id": 1, "bytes_verified": 2097152}""");
            return JsonNode.Parse($$"""{"match": true, "expected_sha256": "{{Sha}}", "actual_sha256": "{{Sha}}", "bytes_verified": 3000000}""");
        }));
        var progress = new RecordingProgress<long>();

        await new ImageVerifier(factory.Open).VerifyDetailedAsync("evidence.dd", progress);

        progress.Reports.Should().Equal(1048576, 2097152, 3000000);
        factory.Calls.Select(c => c.Method).Should().Equal("verify");
    }

    [Fact]
    public async Task No_result_is_an_error()
    {
        var act = () => new ImageVerifier(FakeChannelFactory.Returning("null").Open).VerifyDetailedAsync("x");

        await act.Should().ThrowAsync<SidecarException>();
    }
}

/// <summary>SidecarDiskImager against canned <c>image</c> replies.</summary>
public sealed class SidecarDiskImagerTests
{
    private static readonly ImageJob Job = new(@"\\.\PhysicalDrive3", @"D:\case\evidence.dd", ImageFormat.Raw,
        CaseNumber: "2026-041", ReadErrorRetries: 5);

    private const string DamagedReply = """
        {"output_path": "D:\\case\\evidence.dd", "bytes_written": 3146240,
         "md5": "c71cfca7bd3b4e7128759b28a26baa0f", "sha1": "cfcf7e125f02c589a5608c29bb9b6066438dbf30",
         "sha256": "ebdc4ebe3dfae2e0f48e1062f013a5cde19a684062ff69af218d0746b84ed4f1", "blake3": null,
         "bad_sectors": 3, "bad_ranges": [[1052672, 512], [2097152, 1024]], "bad_ranges_truncated": false,
         "sector_size": 512, "source_size": 3146752, "complete": false,
         "digest_files": ["D:\\case\\evidence.dd.sha256", "D:\\case\\evidence.dd.md5"]}
        """;

    [Fact]
    public async Task The_job_is_sent_as_one_image_request_and_progress_is_not_polled()
    {
        var factory = FakeChannelFactory.Returning(DamagedReply);

        await new SidecarDiskImager(factory.Open).ImageAsync(Job, new RecordingProgress<ImageJobProgress>());

        var call = factory.Calls.Should().ContainSingle().Subject;
        call.Method.Should().Be("image", "a 'progress' request could only be answered after the image had finished");
        call.Params["sourceDevice"]!.GetValue<string>().Should().Be(Job.SourceDevice);
        call.Params["outputPath"]!.GetValue<string>().Should().Be(Job.OutputPath);
        call.Params["format"]!.GetValue<int>().Should().Be((int)ImageFormat.Raw);
        call.Params["caseNumber"]!.GetValue<string>().Should().Be("2026-041");
        call.Params["readErrorRetries"]!.GetValue<int>().Should().Be(5);
        call.Progress.Should().NotBeNull();
    }

    [Fact]
    public async Task Bad_ranges_and_an_incomplete_source_are_surfaced()
    {
        var report = await new SidecarDiskImager(FakeChannelFactory.Returning(DamagedReply).Open).ImageDetailedAsync(Job);

        report.Result.BytesWritten.Should().Be(3146240);
        report.Result.BadSectors.Should().Be(3);
        report.Result.Sha256.Should().Be("ebdc4ebe3dfae2e0f48e1062f013a5cde19a684062ff69af218d0746b84ed4f1");
        report.Result.Blake3.Should().BeNull();
        report.BadRanges.Should().Equal(new UnreadableRange(1052672, 512), new UnreadableRange(2097152, 1024));
        report.BadRangesTruncated.Should().BeFalse();
        report.Complete.Should().BeFalse("the source ended before its declared size");
        report.SourceSize.Should().Be(3146752);
        report.SectorSize.Should().Be(512);
        report.DigestFiles.Should().Equal(@"D:\case\evidence.dd.sha256", @"D:\case\evidence.dd.md5");
        report.IsFaithfulCopy.Should().BeFalse();
    }

    [Fact]
    public async Task A_clean_acquisition_is_a_faithful_copy()
    {
        var report = await new SidecarDiskImager(FakeChannelFactory.Returning("""
            {"output_path": "o.dd", "bytes_written": 4096, "md5": "m", "sha1": "s", "sha256": "h", "blake3": null,
             "bad_sectors": 0, "bad_ranges": [], "bad_ranges_truncated": false, "sector_size": 512,
             "source_size": 4096, "complete": true, "digest_files": ["o.dd.sha256"]}
            """).Open).ImageDetailedAsync(Job);

        report.Complete.Should().BeTrue();
        report.BadRanges.Should().BeEmpty();
        report.IsFaithfulCopy.Should().BeTrue();
    }

    [Fact]
    public async Task Complete_is_not_taken_on_trust_when_the_byte_count_says_otherwise()
    {
        var report = await new SidecarDiskImager(FakeChannelFactory.Returning("""
            {"bytes_written": 1000, "bad_sectors": 0, "source_size": 4096, "complete": true}
            """).Open).ImageDetailedAsync(Job);

        report.Complete.Should().BeFalse();
        report.Result.OutputPath.Should().Be(Job.OutputPath, "the job's own path is used when the reply names none");
    }

    [Theory]
    [InlineData("""{"bad_sectors": 0, "sha256": "h"}""", "bytes_written")]
    [InlineData("""{"bytes_written": 4096, "sha256": "h"}""", "bad_sectors")]
    [InlineData("""{"bytes_written": 4096, "bad_sectors": "none"}""", "bad_sectors")]
    public async Task A_reply_without_a_byte_count_or_a_bad_sector_count_is_rejected_not_defaulted(string reply, string missing)
    {
        var act = () => new SidecarDiskImager(FakeChannelFactory.Returning(reply).Open).ImageAsync(Job);

        (await act.Should().ThrowAsync<SidecarException>()).WithMessage($"*{missing}*");
    }

    [Fact]
    public async Task Progress_is_taken_from_notifications_and_ends_with_the_final_figures()
    {
        var factory = new FakeChannelFactory(() => new FakeSidecarChannel(call =>
        {
            call.ReportProgress("""{"id": 1, "bytes_read": 1048576, "total_bytes": 3146752, "bad_sectors": 0, "throughput": 52428800.5, "phase": "imaging"}""");
            call.ReportProgress("""{"id": 1, "phase": "volatility"}""");
            call.ReportProgress("""{"id": 1, "bytes_read": 2097152, "total_bytes": null, "bad_sectors": 2}""");
            return JsonNode.Parse(DamagedReply);
        }));
        var progress = new RecordingProgress<ImageJobProgress>();

        await new SidecarDiskImager(factory.Open).ImageAsync(Job, progress);

        progress.Reports.Should().HaveCount(3, "a notification without figures is only a keep-alive");
        progress.Reports[0].Should().Be(new ImageJobProgress(1048576, 3146752, 52428800.5, 0, "imaging"));
        progress.Reports[1].BytesRead.Should().Be(2097152);
        progress.Reports[1].TotalBytes.Should().BeNull();
        progress.Reports[1].BadSectors.Should().Be(2);
        progress.Reports[2].Phase.Should().Be("done");
        progress.Reports[2].BytesRead.Should().Be(3146240);
        progress.Reports[2].BadSectors.Should().Be(3);
        progress.Reports[2].TotalBytes.Should().Be(3146752);
    }

    [Fact]
    public async Task Malformed_bad_ranges_are_dropped_one_by_one()
    {
        var report = await new SidecarDiskImager(FakeChannelFactory.Returning("""
            {"bytes_written": 4096, "bad_sectors": 2, "bad_ranges": [[512, 512], [1024], "x", [2048, "many"], [3072, 512]], "complete": true}
            """).Open).ImageDetailedAsync(Job);

        report.BadRanges.Should().Equal(new UnreadableRange(512, 512), new UnreadableRange(3072, 512));
        report.Result.BadSectors.Should().Be(2);
    }
}
