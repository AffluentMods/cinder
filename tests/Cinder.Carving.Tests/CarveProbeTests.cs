using System.Buffers.Binary;
using System.Text;
using Cinder.Carving;
using FluentAssertions;
using Xunit;

namespace Cinder.Carving.Tests;

/// <summary>
/// One synthetic object per built-in rule that derives a length, placed inside a larger image
/// with other bytes after it: the carved extent must be the object, no more and no less.
/// </summary>
public sealed class CarveProbeTests
{
    private const int TimeoutMs = 60_000;
    private const int At = 8192;

    private static async Task<List<CarveHit>> CarveWith(string extension, byte[] obj, int imageSize = 64 * 1024, byte after = 0xEE)
    {
        var image = new byte[imageSize];
        Array.Fill(image, after, At + obj.Length, Math.Min(256, imageSize - At - obj.Length));
        obj.CopyTo(image, At);
        var carver = new FileCarver([CarveSignatures.Defaults.Single(s => s.Extension == extension)]);
        var hits = new List<CarveHit>();
        await foreach (var h in carver.CarveAsync(new MemoryStream(image)))
        {
            hits.Add(h);
        }
        return hits;
    }

    private static void ShouldBeExactly(List<CarveHit> hits, int length)
    {
        var hit = hits.Should().ContainSingle().Subject;
        hit.Offset.Should().Be(At);
        hit.Length.Should().Be(length);
        hit.Validated.Should().BeTrue();
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Ole_compound_file_ends_after_its_last_allocated_sector()
    {
        var cfb = new byte[6 * 512];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(cfb, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(cfb.AsSpan(0x1A), 3);        // major version
        cfb[0x1C] = 0xFE; cfb[0x1D] = 0xFF;                                   // byte order
        BinaryPrimitives.WriteUInt16LittleEndian(cfb.AsSpan(0x1E), 9);        // 512-byte sectors
        BinaryPrimitives.WriteUInt16LittleEndian(cfb.AsSpan(0x20), 6);        // mini sector shift
        BinaryPrimitives.WriteUInt32LittleEndian(cfb.AsSpan(0x2C), 1);        // one FAT sector
        cfb.AsSpan(0x4C, 109 * 4).Fill(0xFF);
        BinaryPrimitives.WriteUInt32LittleEndian(cfb.AsSpan(0x4C), 0);        // …which is sector 0
        var fat = cfb.AsSpan(512, 512);
        fat.Fill(0xFF);                                                       // free
        BinaryPrimitives.WriteUInt32LittleEndian(fat, 0xFFFFFFFD);            // sector 0: the FAT itself
        for (int i = 1; i < 5; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fat[(i * 4)..], 0xFFFFFFFE);   // sectors 1–4 in use
        }

        ShouldBeExactly(await CarveWith("doc", cfb), 6 * 512);                // header + 5 sectors
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Rtf_ends_at_the_brace_that_closes_the_document_not_the_first_closing_brace()
    {
        var rtf = Encoding.ASCII.GetBytes(@"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0 braces \{ and \} are escaped}");
        var hits = await CarveWith("rtf", rtf, after: (byte)'}');

        ShouldBeExactly(hits, rtf.Length);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Pcap_ends_after_its_last_plausible_packet_record()
    {
        var pcap = new byte[24 + 3 * (16 + 60)];
        new byte[] { 0xD4, 0xC3, 0xB2, 0xA1 }.CopyTo(pcap, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(pcap.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(pcap.AsSpan(6), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(pcap.AsSpan(16), 65535);     // snaplen
        BinaryPrimitives.WriteUInt32LittleEndian(pcap.AsSpan(20), 1);         // Ethernet
        for (int i = 0; i < 3; i++)
        {
            var record = pcap.AsSpan(24 + i * 76);
            BinaryPrimitives.WriteUInt32LittleEndian(record, 1_700_000_000);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], 123_456);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], 60);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], 60);
            record.Slice(16, 60).Fill(0x45);
        }

        ShouldBeExactly(await CarveWith("pcap", pcap), pcap.Length);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Pcapng_ends_after_its_last_well_formed_block()
    {
        var section = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(section, 0x0A0D0D0A);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(8), 0x1A2B3C4D);
        BinaryPrimitives.WriteUInt16LittleEndian(section.AsSpan(12), 1);
        BinaryPrimitives.WriteInt64LittleEndian(section.AsSpan(16), -1);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(24), 28);
        var iface = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(iface, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(iface.AsSpan(4), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(iface.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(iface.AsSpan(16), 20);

        ShouldBeExactly(await CarveWith("pcapng", [.. section, .. iface]), 48);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Registry_hive_is_sized_by_its_bins_and_rejected_on_a_bad_checksum()
    {
        var hive = new byte[4096 + 8192];
        "regf"u8.CopyTo(hive);
        BinaryPrimitives.WriteUInt32LittleEndian(hive.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hive.AsSpan(0x28), 8192);
        uint checksum = 0;
        for (int i = 0; i < 0x1FC; i += 4)
        {
            checksum ^= BinaryPrimitives.ReadUInt32LittleEndian(hive.AsSpan(i));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(hive.AsSpan(0x1FC), checksum);

        ShouldBeExactly(await CarveWith("hive", hive), hive.Length);

        hive[0x30] ^= 0x01;     // any header byte changes the checksum
        (await CarveWith("hive", hive)).Should().BeEmpty("'regf' followed by a header that fails its own checksum is not a hive");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task SevenZip_is_sized_by_its_next_header_fields()
    {
        var archive = new byte[32 + 1000 + 50];
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 }.CopyTo(archive, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(12), 1000);
        BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(20), 50);

        ShouldBeExactly(await CarveWith("7z", archive), archive.Length);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Elf64_ends_after_whichever_of_its_tables_and_segments_reaches_furthest()
    {
        var elf = new byte[0x2000 + 5 * 64];
        new byte[] { 0x7F, 0x45, 0x4C, 0x46, 2, 1, 1 }.CopyTo(elf, 0);         // ELF64, little-endian, v1
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x10), 3);         // ET_DYN
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(0x20), 0x40);      // e_phoff
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(0x28), 0x2000);    // e_shoff
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x36), 56);        // e_phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x38), 1);         // e_phnum
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x3A), 64);        // e_shentsize
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x3C), 5);         // e_shnum
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(0x40), 1);         // PT_LOAD
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(0x40 + 0x20), 0x1000);   // p_filesz

        ShouldBeExactly(await CarveWith("elf", elf), elf.Length);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Evtx_is_sized_by_its_chunk_count()
    {
        var evtx = new byte[4096 + 2 * 65536];
        "ElfFile\0"u8.CopyTo(evtx);
        BinaryPrimitives.WriteUInt32LittleEndian(evtx.AsSpan(0x20), 128);
        BinaryPrimitives.WriteUInt16LittleEndian(evtx.AsSpan(0x28), 4096);
        BinaryPrimitives.WriteUInt16LittleEndian(evtx.AsSpan(0x2A), 2);

        ShouldBeExactly(await CarveWith("evtx", evtx, imageSize: 256 * 1024), evtx.Length);
    }

    [Theory(Timeout = TimeoutMs)]
    [InlineData("gif", "GIF8 is not followed by a version")]
    [InlineData("mp3", "ID3ÿÿÿÿÿÿÿ")]
    [InlineData("zip", "PK\u0003\u0004ÿÿÿÿÿÿÿÿÿÿÿÿÿÿÿÿÿÿÿÿÿÿÿÿÿÿ")]
    [InlineData("pdf", "%PDF-file is a phrase, not a header")]
    [InlineData("rar", "Rar!\u001A\u0007\u0099\u0099")]
    [InlineData("tiff", "II*\u0000\u0003\u0000\u0000\u0000")]
    public async Task A_magic_number_followed_by_the_wrong_structure_is_not_reported(string extension, string latin1)
    {
        var bytes = Encoding.Latin1.GetBytes(latin1);
        (await CarveWith(extension, bytes, after: 0x00)).Should().BeEmpty();
    }
}
