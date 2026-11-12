using Cinder.App.ViewModels.Tools;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>
/// The Imager, Convert and Carver tools driven the way their screens drive them: set the
/// fields, run the command, read the status. These cover the wording and the decisions the
/// tools add on top of the imaging library (refusing to overwrite, saying what was recovered).
/// </summary>
[Collection(ActiveCaseCollection.Name)]
public sealed class AcquisitionToolFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-acq-" + Guid.NewGuid().ToString("N"));

    public AcquisitionToolFlowTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A handle released a moment late on Windows; a leftover temp folder is harmless.
        }
    }

    /// <summary>A source that is a whole number of sectors, so no padding note is expected.</summary>
    private string Source(int bytes = 256 * 1024, int seed = 7)
    {
        var data = new byte[bytes];
        new Random(seed).NextBytes(data);
        var path = Path.Combine(_dir, $"source-{seed}.bin");
        File.WriteAllBytes(path, data);
        return path;
    }

    [Theory]
    [InlineData("Raw", "out.dd")]
    [InlineData("E01", "out.E01")]
    [InlineData("AFF4", "out.aff4")]
    public async Task Imaging_a_file_completes_reports_hashes_and_leaves_no_partial_file(string format, string name)
    {
        var output = Path.Combine(_dir, name);
        var tool = new ImagerTool { Source = Source(), Output = output, Format = format };

        await tool.StartCommand.ExecuteAsync(null);

        tool.StatusLine.Should().StartWith("Image complete:");
        tool.StatusLine.Should().Contain("SHA-256").And.Contain("MD5");
        tool.StatusLine.Should().NotContain("WARNING");
        File.Exists(output).Should().BeTrue();
        Directory.GetFiles(_dir, "*.partial").Should().BeEmpty("a finished image is renamed into place");
    }

    [Fact]
    public async Task Imaging_refuses_to_replace_an_existing_image_unless_asked()
    {
        var output = Path.Combine(_dir, "evidence.dd");
        var first = new ImagerTool { Source = Source(seed: 1), Output = output, Format = "Raw" };
        await first.StartCommand.ExecuteAsync(null);
        var original = File.ReadAllBytes(output);

        var second = new ImagerTool { Source = Source(seed: 2), Output = output, Format = "Raw" };
        await second.StartCommand.ExecuteAsync(null);

        second.StatusLine.Should().StartWith("Not started:");
        second.StatusLine.Should().Contain("evidence.dd");
        File.ReadAllBytes(output).Should().Equal(original, "the existing image must be untouched");

        second.Overwrite = true;
        await second.StartCommand.ExecuteAsync(null);

        second.StatusLine.Should().StartWith("Image complete:");
        File.ReadAllBytes(output).Should().NotEqual(original);
    }

    [Fact]
    public async Task Imaging_a_source_that_is_not_whole_sectors_into_E01_says_padding_was_added()
    {
        var output = Path.Combine(_dir, "ragged.E01");
        var tool = new ImagerTool { Source = Source(bytes: 100_100), Output = output, Format = "E01" };

        await tool.StartCommand.ExecuteAsync(null);

        tool.StatusLine.Should().StartWith("Image complete:");
        tool.StatusLine.Should().Contain("zero bytes were added to fill the last one");
    }

    [Fact]
    public async Task Imaging_without_a_source_or_output_says_what_is_missing_and_does_nothing()
    {
        var tool = new ImagerTool();

        await tool.StartCommand.ExecuteAsync(null);

        tool.StatusLine.Should().Be("Choose what to image and where to save the image.");
    }

    [Fact]
    public async Task Converting_raw_to_E01_checks_the_output_and_says_the_source_had_no_recorded_hash()
    {
        var output = Path.Combine(_dir, "converted.E01");
        var tool = new ConvertTool { Source = Source(), Output = output, Format = "E01" };

        await tool.RunCommand.ExecuteAsync(null);

        tool.StatusLine.Should().StartWith("Converted:");
        tool.StatusLine.Should().Contain("Output check: passed.");
        tool.StatusLine.Should().Contain("Source check: not possible.", "a plain file carries no acquisition hash");
        File.Exists(output).Should().BeTrue();
    }

    [Fact]
    public async Task Converting_refuses_to_replace_an_existing_output()
    {
        var output = Path.Combine(_dir, "taken.dd");
        File.WriteAllText(output, "an earlier image");
        var tool = new ConvertTool { Source = Source(), Output = output, Format = "Raw" };

        await tool.RunCommand.ExecuteAsync(null);

        tool.StatusLine.Should().StartWith("Not started:");
        File.ReadAllText(output).Should().Be("an earlier image");
    }

    [Fact]
    public async Task Carving_recovers_an_embedded_png_and_counts_only_files_it_saved()
    {
        // A minimal valid PNG between runs of zeros.
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82,
        ];
        var image = new byte[64 * 1024];
        png.CopyTo(image, 4096);
        var source = Path.Combine(_dir, "disk.bin");
        File.WriteAllBytes(source, image);
        var outDir = Path.Combine(_dir, "carved");

        var tool = new CarverTool { Source = source, OutputDir = outDir };
        await tool.RunCommand.ExecuteAsync(null);

        tool.StatusLine.Should().StartWith("Recovered 1 file into");
        var hit = tool.Hits.Should().ContainSingle(h => h.OutputPath != null).Subject;
        hit.Offset.Should().Be(4096);
        hit.Extension.Should().Be("png");
        File.ReadAllBytes(hit.OutputPath!).Should().Equal(png);
    }

    [Fact]
    public async Task Carving_into_the_folder_that_holds_the_evidence_is_refused()
    {
        var source = Source();
        var tool = new CarverTool { Source = source, OutputDir = _dir };

        await tool.RunCommand.ExecuteAsync(null);

        tool.StatusLine.Should().Contain("should not be written next to the evidence");
        tool.Hits.Should().BeEmpty();
    }
}
