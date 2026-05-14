using Cinder.Core.Hashing;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

public sealed class HashServiceTests
{
    // Known-answer values for the empty input.
    private const string EmptyMd5 = "d41d8cd98f00b204e9800998ecf8427e";
    private const string EmptySha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709";
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string EmptyBlake3 = "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262";

    // Known-answer values for the ASCII string "abc".
    private const string AbcMd5 = "900150983cd24fb0d6963f7d28e17f72";
    private const string AbcSha1 = "a9993e364706816aba3e25717850c26c9cd0d89d";
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string AbcBlake3 = "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85";

    [Fact]
    public async Task Empty_input_yields_known_digests()
    {
        var sut = new HashService();
        using var input = new MemoryStream([]);

        var result = await sut.ComputeAsync(input,
            [HashAlgorithmKind.Md5, HashAlgorithmKind.Sha1, HashAlgorithmKind.Sha256, HashAlgorithmKind.Blake3]);

        result.BytesHashed.Should().Be(0);
        result.Md5.Should().Be(EmptyMd5);
        result.Sha1.Should().Be(EmptySha1);
        result.Sha256.Should().Be(EmptySha256);
        result.Blake3.Should().Be(EmptyBlake3);
    }

    [Fact]
    public async Task Abc_input_yields_known_digests()
    {
        var sut = new HashService();
        using var input = new MemoryStream("abc"u8.ToArray());

        var result = await sut.ComputeAsync(input,
            [HashAlgorithmKind.Md5, HashAlgorithmKind.Sha1, HashAlgorithmKind.Sha256, HashAlgorithmKind.Blake3]);

        result.BytesHashed.Should().Be(3);
        result.Md5.Should().Be(AbcMd5);
        result.Sha1.Should().Be(AbcSha1);
        result.Sha256.Should().Be(AbcSha256);
        result.Blake3.Should().Be(AbcBlake3);
    }

    [Fact]
    public async Task Only_requested_algorithms_are_computed()
    {
        var sut = new HashService();
        using var input = new MemoryStream("abc"u8.ToArray());

        var result = await sut.ComputeAsync(input, [HashAlgorithmKind.Sha256]);

        result.Sha256.Should().Be(AbcSha256);
        result.Md5.Should().BeNull();
        result.Sha1.Should().BeNull();
        result.Blake3.Should().BeNull();
    }

    [Fact]
    public async Task Empty_algorithm_set_throws()
    {
        var sut = new HashService();
        using var input = new MemoryStream();

        var act = async () => await sut.ComputeAsync(input, []);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Progress_reports_total_bytes_read()
    {
        var sut = new HashService();
        var bytes = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        using var input = new MemoryStream(bytes);

        // Use a synchronous IProgress implementation. The built-in Progress<T> queues callbacks
        // through SynchronizationContext.Post which is non-deterministic in unit tests —
        // callbacks may still be in-flight after ComputeAsync awaits. A direct IProgress<long>
        // runs the Report inline on the thread that calls it, which is exactly what we want
        // for a test that asserts "every progress tick was delivered."
        var reports = new SynchronousProgress<long>();
        var result = await sut.ComputeAsync(input, [HashAlgorithmKind.Sha256], reports);

        result.BytesHashed.Should().Be(bytes.Length);
        reports.Values.Should().NotBeEmpty();
        reports.Values[^1].Should().Be(bytes.Length);
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = new();
        public void Report(T value) => Values.Add(value);
    }
}
