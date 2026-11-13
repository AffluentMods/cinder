using Cinder.App.Services;
using Cinder.App.ViewModels;
using Cinder.App.ViewModels.Tools;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>The custody table shows actions and details in words; the stored text is untouched.</summary>
public sealed class CustodyTextTests
{
    [Theory]
    [InlineData("case.created", "Case created")]
    [InlineData("case.opened", "Case opened")]
    [InlineData("evidence.imaged", "Evidence imaged")]
    [InlineData("parser.ran", "Tool run")]
    [InlineData("annotation", "Note")]
    public void Known_actions_are_shown_in_words(string stored, string shown) =>
        CustodyText.Action(stored).Should().Be(shown);

    [Fact]
    public void An_action_this_build_does_not_know_is_shown_as_stored() =>
        CustodyText.Action("plugin.custom-step").Should().Be("plugin.custom-step");

    [Fact]
    public void A_flat_object_becomes_labelled_values_in_stored_order()
    {
        var shown = CustodyText.Details("""{"Tool":"filesystem","Evidence":"R:\\evidence\\usb.img","Rows":39}""");

        shown.Should().Be(@"Tool: filesystem  ·  Evidence: R:\evidence\usb.img  ·  Rows: 39");
    }

    [Fact]
    public void Compound_names_are_split_and_empty_values_left_out()
    {
        var shown = CustodyText.Details("""{"BytesWritten":1024,"Description":null,"Note":"","Padded":true,"MD5":"abc"}""");

        shown.Should().Be("Bytes written: 1024  ·  Padded: yes  ·  MD5: abc");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{}")]
    public void Anything_that_is_not_a_filled_object_is_shown_unchanged(string stored) =>
        CustodyText.Details(stored).Should().Be(stored);

    [Fact]
    public void Nothing_stored_shows_nothing()
    {
        CustodyText.Details(null).Should().BeEmpty();
        CustodyText.Action(null).Should().BeEmpty();
    }
}

/// <summary>Settings is a page now: Save writes the file, Discard reads it back.</summary>
[Collection(ActiveCaseCollection.Name)]
public sealed class SettingsPageTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "cinder-settings-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly Func<SettingsStore> _previous = SettingsTool.StoreFactory;

    public SettingsPageTests() => SettingsTool.StoreFactory = () => new SettingsStore(_path);

    public void Dispose()
    {
        SettingsTool.StoreFactory = _previous;
        File.Delete(_path);
    }

    [Fact]
    public void Save_writes_the_file_and_says_so()
    {
        var tool = new SettingsTool();
        tool.Form.TimestampAuthorityUrl = "https://tsa.example/tsr";

        tool.SaveCommand.Execute(null);

        tool.Form.HasSaveError.Should().BeFalse();
        tool.StatusLine.Should().StartWith("Settings saved.");
        new SettingsStore(_path).Load().TimestampAuthorityUrl.Should().Be("https://tsa.example/tsr");
    }

    [Fact]
    public void Discard_puts_the_fields_back_to_what_is_saved()
    {
        var tool = new SettingsTool();
        tool.Form.TimestampAuthorityUrl = "https://tsa.example/tsr";
        tool.SaveCommand.Execute(null);
        tool.Form.TimestampAuthorityUrl = "https://typed-but-not-saved.example";

        tool.ReloadCommand.Execute(null);

        tool.Form.TimestampAuthorityUrl.Should().Be("https://tsa.example/tsr");
        tool.StatusLine.Should().StartWith("Changes discarded.");
    }

    [Fact]
    public void The_page_shows_where_the_settings_file_is() =>
        new SettingsTool().Form.SettingsPath.Should().Be(_path);
}

/// <summary>Home follows the open case and can send the examiner on to the next tool.</summary>
[Collection(ActiveCaseCollection.Name)]
public sealed class HomeOpenCaseTests : IDisposable
{
    private readonly Action<string>? _previous = AppNavigator.ShowTool;

    public HomeOpenCaseTests() => ActiveCaseContext.Set(null);

    public void Dispose()
    {
        ActiveCaseContext.Set(null);
        AppNavigator.ShowTool = _previous;
    }

    [Fact]
    public void Home_shows_the_case_that_is_open_and_clears_when_it_closes()
    {
        var home = new DashboardTool();
        home.HasOpenCase.Should().BeFalse();
        var session = new CaseSession(Guid.NewGuid(), "Demo", "examiner", @"C:\cases\demo.cinder", DateTimeOffset.UtcNow);

        ActiveCaseContext.Set(session);

        home.HasOpenCase.Should().BeTrue();
        home.OpenCase.Should().BeSameAs(session);

        ActiveCaseContext.Set(null);

        home.HasOpenCase.Should().BeFalse();
    }

    [Fact]
    public void A_next_step_button_asks_the_shell_for_that_tool()
    {
        string? asked = null;
        AppNavigator.ShowTool = kind => asked = kind;

        new DashboardTool().ShowToolCommand.Execute("reports");

        asked.Should().Be("reports");
    }

    [Fact]
    public void Tools_that_cannot_do_their_job_yet_are_not_offered()
    {
        var workspace = new WorkspaceViewModel();

        workspace.FindByKind("ramcapture").Should().BeNull();
        workspace.FindByKind("cloud").Should().BeNull();
        workspace.FindByKind("imager").Should().NotBeNull();
    }
}
