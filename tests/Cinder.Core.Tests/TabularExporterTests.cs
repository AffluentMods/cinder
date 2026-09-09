using System.Text;
using System.Text.Json;
using Cinder.Core.Export;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

public sealed class TabularExporterTests
{
    [Fact]
    public void Csv_has_a_header_from_the_first_rows_properties_and_one_line_per_row()
    {
        var rows = new object[]
        {
            new { Name = "a.txt", Size = 12L, Modified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) },
            new { Name = "b.txt", Size = 34L, Modified = new DateTimeOffset(2026, 1, 2, 3, 4, 6, TimeSpan.Zero) },
        };

        var csv = TabularExporter.ToCsv(rows);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();

        lines.Should().HaveCount(3);
        lines[0].Should().Be("Name,Size,Modified");
        lines[1].Should().Be("a.txt,12,2026-01-02T03:04:05.0000000+00:00");
    }

    [Fact]
    public void Csv_quotes_commas_quotes_and_newlines()
    {
        var rows = new object[] { new { Value = "has, comma and \"quote\"\nand newline" } };
        var csv = TabularExporter.ToCsv(rows);
        csv.Should().Contain("\"has, comma and \"\"quote\"\"\nand newline\"");
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+cmd")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tleading tab")]
    public void Csv_neutralises_formula_injection_in_string_cells(string hostile)
    {
        // A filename or registry value starting with = + - @ executes as a formula when the
        // export is opened in Excel/LibreOffice. Evidence is attacker-authored, so this is a
        // realistic path from "examiner opens their own export" to code execution.
        var csv = TabularExporter.ToCsv(new object[] { new { Value = hostile } });
        var cell = csv.Split('\n')[1].TrimEnd('\r');
        cell.TrimStart('"').Should().StartWith("'");
    }

    [Fact]
    public void Csv_leaves_numeric_cells_raw_so_negatives_stay_numbers()
    {
        var csv = TabularExporter.ToCsv(new object[] { new { Delta = -42, Ratio = -0.5 } });
        csv.Split('\n')[1].TrimEnd('\r').Should().Be("-42,-0.5");
    }

    [Fact]
    public void Csv_writes_nothing_but_returns_zero_for_no_rows()
    {
        TabularExporter.ToCsv([]).Should().BeEmpty();
    }

    [Fact]
    public void Json_is_an_array_of_objects_keyed_by_property_name()
    {
        var rows = new object[]
        {
            new { Key = "HKLM\\Run", Count = 3, When = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero), Flag = true },
        };

        using var ms = new MemoryStream();
        TabularExporter.WriteJson(rows, ms).Should().Be(1);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
        var first = doc.RootElement[0];
        first.GetProperty("Key").GetString().Should().Be("HKLM\\Run");
        first.GetProperty("Count").GetInt32().Should().Be(3);
        first.GetProperty("Flag").GetBoolean().Should().BeTrue();
        first.GetProperty("When").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero));
    }

    [Fact]
    public void Null_rows_are_skipped()
    {
        var rows = new object?[] { null, new { A = 1 }, null };
        TabularExporter.ToCsv(rows!).Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2);
    }
}
