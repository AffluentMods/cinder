using System.Buffers.Binary;
using Cinder.App.Services;
using Cinder.App.ViewModels;
using Cinder.App.ViewModels.Tools;
using Cinder.Hex;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

public class ShellRoundTwoTests
{
    // ------------------------------------------------------------------ data inspector basis

    [Fact]
    public void The_inspector_decodes_from_the_start_of_a_selection_not_from_its_last_byte()
    {
        // A FILETIME at offset 8. Dragging across it leaves the caret on byte 15.
        var bytes = new byte[32];
        var when = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), when.ToFileTimeUtc());
        using var hex = new HexViewModel { Buffer = new ByteArrayHexBuffer(bytes, "t.bin") };

        hex.SelectionStart = 8;
        hex.SelectionEnd = 15;
        hex.CaretOffset = 15;

        hex.InspectorOffset.Should().Be(8);
        hex.InspectorBasis.Should().Be("from selection");
        hex.InspectorRows.Single(r => r.Label == "FILETIME").Value.Should().Be("2024-01-01 00:00:00");
    }

    [Fact]
    public void Without_a_selection_the_inspector_decodes_from_the_caret()
    {
        var bytes = new byte[32];
        bytes[5] = 0x2A;
        using var hex = new HexViewModel { Buffer = new ByteArrayHexBuffer(bytes, "t.bin") };

        hex.CaretOffset = 5;

        hex.InspectorOffset.Should().Be(5);
        hex.InspectorBasis.Should().Be("from caret");
        hex.InspectorRows.Single(r => r.Label == "uint8").Value.Should().Be("42");
    }

    // ------------------------------------------------------------------ rail groups

    [Fact]
    public void Filtering_opens_every_group_and_clearing_the_filter_restores_the_examiners_arrangement()
    {
        var ws = new WorkspaceViewModel();
        string[] Open() => ws.RailSections.Where(s => s.IsExpanded).Select(s => s.Title).ToArray();

        Open().Should().Equal("Examine");

        ws.RailFilter = "a";
        ws.RailSections.Should().OnlyContain(s => s.IsExpanded, "every group shows its matches while filtering");

        ws.RailFilter = null;
        Open().Should().Equal(["Examine"], "the filter must not leave every group open behind it");
    }

    [Fact]
    public void Group_arrangement_round_trips_and_always_keeps_the_selected_tools_group_open()
    {
        var ws = new WorkspaceViewModel();
        var acquire = ws.RailSections.Single(s => s.Title == "Acquire");
        ws.ToggleSectionCommand.Execute(acquire);
        ws.ExpandedSections.Should().BeEquivalentTo("Examine", "Acquire");

        var restored = new WorkspaceViewModel();
        restored.SelectedTool = restored.FindByKind("custody");
        restored.RestoreExpandedSections(["Acquire", "No such group"]);

        restored.RailSections.Where(s => s.IsExpanded).Select(s => s.Title)
            .Should().BeEquivalentTo("Acquire", "Case");
    }

    [Fact]
    public void Window_layout_written_before_groups_were_saved_still_loads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cinder-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "window.json");
            File.WriteAllText(path,
                """{"X":10,"Y":20,"Width":1400,"Height":900,"Maximized":false,"RailWidth":232,"InspectorWidth":300,"RailOpen":true,"InspectorOpen":true}""");

            var layout = new WindowLayoutStore(path).Load();

            layout.Should().NotBeNull();
            layout!.ExpandedSections.Should().BeNull("an older file simply has no saved arrangement");

            new WindowLayoutStore(path).Save(layout with { ExpandedSections = ["Examine", "Case"] });
            new WindowLayoutStore(path).Load()!.ExpandedSections.Should().Equal("Examine", "Case");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
