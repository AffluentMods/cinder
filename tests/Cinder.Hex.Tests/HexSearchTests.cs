using System.Text;
using Cinder.Hex;
using FluentAssertions;
using Xunit;

namespace Cinder.Hex.Tests;

public sealed class HexSearchTests
{
    [Fact]
    public void Finds_ascii_substring()
    {
        var data = Encoding.ASCII.GetBytes("hello cruel cinder world");
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "cinder")).ToList();
        hits.Should().ContainSingle();
        hits[0].Offset.Should().Be(12);
        hits[0].Length.Should().Be(6);
    }

    [Fact]
    public void Finds_overlapping_hits()
    {
        var data = Encoding.ASCII.GetBytes("aaaa");
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "aa")).ToList();
        hits.Should().HaveCount(3); // 0, 1, 2
    }

    [Fact]
    public void Finds_hex_pattern()
    {
        byte[] data = [0x01, 0x02, 0x89, 0x50, 0x4E, 0x47, 0xFF];
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Hex, "89 50 4E 47")).ToList();
        hits.Should().ContainSingle();
        hits[0].Offset.Should().Be(2);
        hits[0].Length.Should().Be(4);
    }

    [Fact]
    public void Case_insensitive_ascii()
    {
        var data = Encoding.ASCII.GetBytes("Cinder");
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "CINDER", CaseSensitive: false)).ToList();
        hits.Should().ContainSingle();
    }

    [Fact]
    public void Utf16le_finds_string()
    {
        var data = Encoding.Unicode.GetBytes("\0\0Cinder\0Pad");
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Utf16Le, "Cinder")).ToList();
        hits.Should().ContainSingle();
    }

    [Fact]
    public void Mmap_buffer_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cinder-mmap-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3]);
            using var mmap = new MmapHexBuffer(path);
            mmap.Length.Should().Be(8);
            var buf = new byte[4];
            var read = mmap.Read(0, buf);
            read.Should().Be(4);
            buf.Should().Equal(0x89, 0x50, 0x4E, 0x47);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
