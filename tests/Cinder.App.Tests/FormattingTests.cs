using Xunit;
using Cinder.App.Controls;
using Cinder.App.ViewModels;
using FluentAssertions;

namespace Cinder.App.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1,023 B")]
    [InlineData(1024, "1.0 KiB")]
    [InlineData(360_448, "352 KiB")]
    [InlineData(1_572_864, "1.5 MiB")]
    [InlineData(2L << 30, "2.0 GiB")]
    [InlineData(5L << 40, "5.0 TiB")]
    [InlineData(5000L << 40, "5000 TiB")]
    public void FormatSize_picks_the_unit_and_precision(long bytes, string expected) =>
        HexViewModel.FormatSize(bytes).Should().Be(expected);

    [Theory]
    [InlineData("MftRecord", "Mft record")]
    [InlineData("IsDeleted", "Is deleted")]
    [InlineData("SHA256", "SHA256")]
    [InlineData("USNRecord", "USN record")]
    [InlineData("Path", "Path")]
    [InlineData("ParentPath", "Parent path")]
    public void Humanize_splits_words_but_keeps_acronyms(string name, string expected) =>
        AutoColumns.Humanize(name).Should().Be(expected);
}
