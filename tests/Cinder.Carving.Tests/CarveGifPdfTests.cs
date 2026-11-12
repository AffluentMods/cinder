using System.Text;
using Cinder.Carving;
using FluentAssertions;
using Xunit;

namespace Cinder.Carving.Tests;

/// <summary>
/// GIF and PDF both have an end marker that also occurs before the end. Each test builds an
/// object with an early marker inside it, puts more bytes (including yet another marker) after
/// it, and requires the carved file to be the object exactly.
/// </summary>
public sealed class CarveGifPdfTests : IDisposable
{
    private const int TimeoutMs = 60_000;
    private const int At = 4096;

    private readonly string _out = Directory.CreateTempSubdirectory("cinder-carve-gifpdf").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_out, recursive: true); } catch { }
    }

    private async Task<List<CarveHit>> Carve(string extension, byte[] image)
    {
        var carver = new FileCarver([CarveSignatures.Defaults.Single(s => s.Extension == extension)]);
        var hits = new List<CarveHit>();
        await foreach (var h in carver.CarveAsync(new MemoryStream(image), _out))
        {
            hits.Add(h);
        }
        return hits;
    }

    private static byte[] ImageWith(byte[] obj, byte[] after, int size = 64 * 1024)
    {
        var image = new byte[size];
        obj.CopyTo(image, At);
        after.CopyTo(image, At + obj.Length);
        return image;
    }

    private async Task ShouldCarveExactly(string extension, byte[] obj, byte[] after)
    {
        var hits = await Carve(extension, ImageWith(obj, after));
        var hit = hits.Should().ContainSingle().Subject;
        hit.Offset.Should().Be(At);
        hit.Validated.Should().BeTrue();
        hit.Length.Should().Be(obj.Length);
        (await File.ReadAllBytesAsync(hit.OutputPath!)).Should().Equal(obj);
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    // =========================================================================== GIF ====

    private static readonly byte[] GraphicControl = [0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00];

    /// <summary>Image descriptor, LZW code size, then <paramref name="data"/> as one or more sub-blocks.</summary>
    private static byte[] GifImage(byte[] data, bool localColourTable = false)
    {
        var parts = new List<byte[]>
        {
            new byte[] { 0x2C, 0, 0, 0, 0, 2, 0, 2, 0, (byte)(localColourTable ? 0x80 : 0x00) },
        };
        if (localColourTable)
        {
            parts.Add(new byte[6]);                 // two entries
        }
        parts.Add([0x02]);                          // LZW minimum code size
        for (int i = 0; i < data.Length; i += 255)
        {
            var n = Math.Min(255, data.Length - i);
            parts.Add([(byte)n]);
            parts.Add(data.AsSpan(i, n).ToArray());
        }
        parts.Add([0x00]);                          // block terminator
        return Concat([.. parts]);
    }

    private static byte[] GifHeader(string version, bool globalColourTable)
    {
        var lsd = new byte[] { 2, 0, 2, 0, (byte)(globalColourTable ? 0x80 : 0x00), 0, 0 };
        return Concat(Ascii(version), lsd, globalColourTable ? new byte[6] : []);
    }

    /// <summary>Compressed-looking data in which "00 3B" — the old end marker — occurs several times.</summary>
    private static byte[] DataWithEarlyFooters(int length)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        foreach (var at in new[] { 3, 100, 254, 255, 300, length - 2 })   // 254/255 straddles a sub-block boundary
        {
            data[at] = 0x00;
            data[at + 1] = 0x3B;
        }
        return data;
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task A_gif_whose_image_data_contains_the_trailer_bytes_is_carved_to_its_real_trailer()
    {
        var gif = Concat(
            GifHeader("GIF89a", globalColourTable: true),
            GraphicControl,
            GifImage(DataWithEarlyFooters(700)),
            [0x3B]);

        // Cutting at the first "00 3B" ended this file 3 bytes into its image data.
        await ShouldCarveExactly("gif", gif, after: [0xAA, 0x00, 0x3B, 0xAA, 0x00, 0x3B]);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task An_animated_gif_with_extensions_and_local_colour_tables_is_carved_whole()
    {
        var comment = Concat([0x21, 0xFE], [0x05], Ascii("hello"), [0x00]);
        var loop = Concat([0x21, 0xFF, 0x0B], Ascii("NETSCAPE2.0"), [0x03, 0x01, 0x00, 0x00], [0x00]);
        var gif = Concat(
            GifHeader("GIF87a", globalColourTable: false),
            loop,
            comment,
            GraphicControl,
            GifImage(DataWithEarlyFooters(400), localColourTable: true),
            GraphicControl,
            GifImage(DataWithEarlyFooters(1300)),
            [0x3B]);

        await ShouldCarveExactly("gif", gif, after: [0x00, 0x3B, 0x00, 0x3B]);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task A_gif_that_is_cut_off_before_its_trailer_is_reported_but_not_written()
    {
        var whole = Concat(GifHeader("GIF89a", true), GraphicControl, GifImage(DataWithEarlyFooters(700)), [0x3B]);
        var cut = whole[..(whole.Length - 300)];

        // What follows the cut is not GIF structure, though it does contain "00 3B".
        var hits = await Carve("gif", ImageWith(cut, after: [0x99, 0x99, 0x00, 0x3B]));

        var hit = hits.Should().ContainSingle().Subject;
        hit.Validated.Should().BeFalse();
        hit.OutputPath.Should().BeNull();
        Directory.GetFiles(_out).Should().BeEmpty();
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task A_gif_header_with_no_image_before_the_trailer_is_not_written()
    {
        var hits = await Carve("gif", ImageWith(Concat(GifHeader("GIF89a", false), [0x3B]), after: []));
        hits.Should().ContainSingle().Which.Validated.Should().BeFalse();
        Directory.GetFiles(_out).Should().BeEmpty();
    }

    // =========================================================================== PDF ====

    /// <summary>Assembles PDF text, substituting each <c>{name}</c> with the offset recorded under that name.</summary>
    private sealed class PdfBuilder
    {
        private readonly StringBuilder _text = new();
        private readonly Dictionary<string, int> _marks = [];

        public PdfBuilder Add(string s)
        {
            _text.Append(s);
            return this;
        }

        public PdfBuilder Mark(string name)
        {
            _marks[name] = _text.Length;
            return this;
        }

        /// <summary><c>startxref</c>, the offset recorded under <paramref name="mark"/> (or a literal number), <c>%%EOF</c>.</summary>
        public PdfBuilder End(string mark, bool trailingNewline = true)
        {
            var offset = _marks.TryGetValue(mark, out var o) ? o.ToString(System.Globalization.CultureInfo.InvariantCulture) : mark;
            _text.Append("startxref\n").Append(offset).Append("\n%%EOF");
            if (trailingNewline)
            {
                _text.Append('\n');
            }
            return this;
        }

        public int Length => _text.Length;

        public byte[] Bytes() => Encoding.ASCII.GetBytes(_text.ToString());
    }

    private static PdfBuilder BasePdf() => new PdfBuilder()
        .Add("%PDF-1.7\n")
        .Add("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n")
        .Add("2 0 obj\n<< /Type /Pages /Count 0 /Kids [] >>\nendobj\n")
        .Mark("xref1")
        .Add("xref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n")
        .Add("trailer\n<< /Size 3 /Root 1 0 R >>\n");

    [Fact(Timeout = TimeoutMs)]
    public async Task A_pdf_with_an_incremental_update_is_carved_through_its_last_end_marker()
    {
        var pdf = BasePdf().End("xref1")
            // First save ends above. A later save appends a new object, its own xref and marker.
            .Add("3 0 obj\n<< /Type /Annot /Contents (added in a later save) >>\nendobj\n")
            .Mark("xref2")
            .Add("xref\n3 1\n0000000200 00000 n \n")
            .Add("trailer\n<< /Size 4 /Root 1 0 R /Prev 107 >>\n")
            .End("xref2")
            // And another.
            .Add("4 0 obj\n<< /Type /Annot /Contents (and again) >>\nendobj\n")
            .Mark("xref3")
            .Add("xref\n4 1\n0000000400 00000 n \n")
            .Add("trailer\n<< /Size 5 /Root 1 0 R /Prev 300 >>\n")
            .End("xref3", trailingNewline: false)
            .Bytes();

        // Stopping at the first %%EOF carved the document as it was before both updates.
        await ShouldCarveExactly("pdf", pdf, after: Ascii("\nsome other text that happens to say %%EOF again"));
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task A_linearised_pdf_is_not_cut_at_the_marker_after_its_first_page_section()
    {
        var pdf = new PdfBuilder()
            .Add("%PDF-1.5\n")
            .Add("1 0 obj\n<< /Linearized 1 /L 9999 /N 1 >>\nendobj\n")
            .Mark("firstXref")
            .Add("xref\n1 1\n0000000009 00000 n \n")
            .Add("trailer\n<< /Size 5 /Root 2 0 R >>\n")
            .End("0")                                   // the first-page section ends with "startxref 0"
            .Add("2 0 obj\n<< /Type /Catalog /Pages 3 0 R >>\nendobj\n")
            .Add("3 0 obj\n<< /Type /Pages /Count 1 /Kids [4 0 R] >>\nendobj\n")
            .Add("4 0 obj\n<< /Type /Page /Parent 3 0 R >>\nendobj\n")
            .Add("xref\n0 1\n0000000000 65535 f \n")
            .Add("trailer\n<< /Size 1 >>\n")
            .End("firstXref", trailingNewline: false)   // the real end points back at the first table
            .Bytes();

        await ShouldCarveExactly("pdf", pdf, after: Ascii("\n\0\0\0\0binary that is not PDF, then %%EOF"));
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task A_pdf_with_an_end_marker_inside_a_stream_is_carved_to_its_own_end()
    {
        // An attached file, stored uncompressed, that is itself a PDF: its %%EOF comes first.
        const string embedded = "%PDF-1.4\n1 0 obj\n<< >>\nendobj\nxref\n0 1\n0000000000 65535 f \ntrailer\n<< >>\nstartxref\n31\n%%EOF\n";
        var pdf = new PdfBuilder()
            .Add("%PDF-1.7\n")
            .Add("1 0 obj\n<< /Type /Catalog >>\nendobj\n")
            .Add("2 0 obj\n<< /Type /EmbeddedFile /Length " + embedded.Length + " >>\nstream\n")
            .Add(embedded)
            .Add("endstream\nendobj\n")
            .Mark("xref")
            .Add("xref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000045 00000 n \n")
            .Add("trailer\n<< /Size 3 /Root 1 0 R >>\n")
            .End("xref", trailingNewline: false)
            .Bytes();

        var hits = await Carve("pdf", ImageWith(pdf, after: Ascii("\n")));

        // One document, whole. (The embedded header lies inside the carved extent and is not
        // carved a second time.)
        var hit = hits.Should().ContainSingle().Subject;
        hit.Length.Should().Be(pdf.Length);
        (await File.ReadAllBytesAsync(hit.OutputPath!)).Should().Equal(pdf);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Two_pdfs_stored_back_to_back_are_two_files()
    {
        var first = BasePdf().End("xref1").Bytes();
        var second = BasePdf().End("xref1", trailingNewline: false).Bytes();

        var hits = await Carve("pdf", ImageWith(Concat(first, second), after: []));

        // The second file's marker is valid PDF and directly follows — but it follows a new
        // header, and its startxref counts from that header, not from the first file's.
        hits.Select(h => (h.Offset, h.Length)).Should().Equal(
            ((long)At, first.Length - 1L),              // the carve ends at %%EOF, before the newline
            ((long)At + first.Length, (long)second.Length));
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task A_later_marker_that_does_not_point_into_the_document_is_not_part_of_it()
    {
        var pdf = BasePdf().End("xref1", trailingNewline: false).Bytes();

        // PDF-looking bytes from somewhere else follow: objects and a marker, but its
        // startxref (an offset in some other file) does not land on a table in this one.
        var stray = Ascii("\n9 0 obj\n<< /Stray true >>\nendobj\nxref\n9 1\n0000000001 00000 n \ntrailer\n<< >>\nstartxref\n77\n%%EOF\n");

        await ShouldCarveExactly("pdf", pdf, after: stray);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task A_pdf_whose_only_marker_cannot_be_verified_is_still_carved_to_it()
    {
        // startxref is wrong (readers rebuild the table); the first marker is the best answer.
        var pdf = BasePdf().End("5", trailingNewline: false).Bytes();
        await ShouldCarveExactly("pdf", pdf, after: Ascii("\n\n\nplain text follows"));
    }
}
