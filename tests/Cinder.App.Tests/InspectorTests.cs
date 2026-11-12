using Xunit;
using System.Buffers.Binary;
using Cinder.App.ViewModels;
using FluentAssertions;

namespace Cinder.App.Tests;

public class InspectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static InspectorRow Row(IReadOnlyList<InspectorRow> rows, string label) => rows.Single(r => r.Label == label);

    [Fact]
    public void Integers_follow_the_endian_toggle()
    {
        byte[] b = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        Row(Inspector.Decode(b, littleEndian: true, Now), "uint16").Value.Should().Be("513");
        Row(Inspector.Decode(b, littleEndian: false, Now), "uint16").Value.Should().Be("258");
        Row(Inspector.Decode(b, littleEndian: true, Now), "int8").Value.Should().Be("1");
        Row(Inspector.Decode([0xFF], true, Now), "int8").Value.Should().Be("-1");
    }

    [Fact]
    public void Short_input_asks_for_more_bytes_instead_of_throwing()
    {
        var rows = Inspector.Decode([0x41, 0x42], true, Now);
        Row(rows, "uint16").Value.Should().Be("16961");
        Row(rows, "int32").Value.Should().Be("needs more bytes");
        Row(rows, "int32").IsDim.Should().BeTrue();
        Row(rows, "GUID").Value.Should().Be("needs more bytes");
        Row(rows, "FILETIME").Value.Should().Be("needs more bytes");
    }

    [Fact]
    public void Plausible_unix_time_has_no_note()
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1_700_000_000); // 2023-11-14
        var row = Row(Inspector.Decode(b, true, Now), "Unix time");
        row.Value.Should().Be("2023-11-14 22:13:20");
        row.HasNote.Should().BeFalse();
        row.IsDim.Should().BeFalse();
    }

    [Fact]
    public void Future_and_pre_epoch_timestamps_are_flagged()
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 4_000_000_000); // 2096
        Row(Inspector.Decode(b, true, Now), "Unix time").Note.Should().Be("future");

        BinaryPrimitives.WriteUInt32LittleEndian(b, 1000); // 1970
        Row(Inspector.Decode(b, true, Now), "Unix time").Note.Should().Be("unlikely");
    }

    [Fact]
    public void Filetime_and_webkit_reject_zero_and_overflow()
    {
        var zero = new byte[8];
        Row(Inspector.Decode(zero, true, Now), "FILETIME").Value.Should().Be("out of range");
        Row(Inspector.Decode(zero, true, Now), "WebKit time").Value.Should().Be("out of range");

        var b = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, long.MaxValue);
        Row(Inspector.Decode(b, true, Now), "FILETIME").Value.Should().Be("out of range");

        BinaryPrimitives.WriteInt64LittleEndian(b, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc());
        Row(Inspector.Decode(b, true, Now), "FILETIME").Value.Should().Be("2024-01-01 00:00:00");
    }

    [Fact]
    public void Dos_date_rejects_impossible_fields()
    {
        // Date word 0x59A0: month field = 13.
        byte[] bad = [0x00, 0x00, 0xA0, 0x59];
        Row(Inspector.Decode(bad, true, Now), "DOS date").Value.Should().Be("out of range");

        // 2020-02-29 12:30:00: time 0x63C0, date 0x505D.
        byte[] ok = [0xC0, 0x63, 0x5D, 0x50];
        Row(Inspector.Decode(ok, true, Now), "DOS date").Value.Should().Be("2020-02-29 12:30:00");
    }

    [Fact]
    public void Guid_is_labelled_mixed_endian()
    {
        var g = Guid.NewGuid();
        var row = Row(Inspector.Decode(g.ToByteArray(), true, Now), "GUID");
        row.Value.Should().Be(g.ToString("D"));
        row.Note.Should().Be("mixed");
    }

    [Fact]
    public void Floats_use_scientific_only_when_extreme()
    {
        var b = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, 1.5);
        Row(Inspector.Decode(b, true, Now), "float64").Value.Should().Be("1.5");
        BinaryPrimitives.WriteDoubleLittleEndian(b, 1.5e20);
        Row(Inspector.Decode(b, true, Now), "float64").Value.Should().Be("1.5e+20");
    }
}
