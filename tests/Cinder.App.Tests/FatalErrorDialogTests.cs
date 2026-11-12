using Cinder.App.Services;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

public class FatalErrorDialogTests
{
    /// <summary>Text with everything a shell or a script interpreter would trip over.</summary>
    private const string Hostile = "It's \"broken\": $(rm -rf ~) `id` \\ <b>bold</b> & more;\nsecond line -- with dashes";

    [Fact]
    public void Linux_helpers_are_tried_most_capable_first()
    {
        var commands = FatalErrorDialog.CommandsFor(FatalErrorDialog.Desktop.Linux, "Cinder", "message");

        commands.Select(c => c.Program).Should().Equal("zenity", "kdialog", "xmessage", "gxmessage");
    }

    [Theory]
    [InlineData("Linux")]
    [InlineData("MacOS")]
    public void The_message_is_always_one_whole_argument_never_part_of_a_script(string desktop)
    {
        var commands = FatalErrorDialog.CommandsFor(Enum.Parse<FatalErrorDialog.Desktop>(desktop), "Cinder", Hostile);

        commands.Should().NotBeEmpty();
        foreach (var command in commands)
        {
            // Exactly one argument carries the text, intact — either on its own or as the
            // value of a single --text= option. It appears nowhere else.
            var carriers = command.Arguments.Where(a => a.Contains(Hostile, StringComparison.Ordinal)).ToList();
            carriers.Should().ContainSingle();
            carriers[0].Should().BeOneOf(Hostile, "--text=" + Hostile);
            command.Arguments.Should().Contain(a => a == "Cinder" || a == "--title=Cinder");
        }
    }

    [Fact]
    public void On_macOS_the_script_is_fixed_and_the_text_arrives_as_an_argument()
    {
        var command = FatalErrorDialog.CommandsFor(FatalErrorDialog.Desktop.MacOS, "Cinder", Hostile).Single();

        command.Program.Should().Be("/usr/bin/osascript");
        command.Arguments.TakeLast(2).Should().Equal("Cinder", Hostile);
        var script = command.Arguments.SkipLast(2).ToList();
        script.Should().NotContain(a => a.Contains("broken"), "nothing from the message may be compiled as AppleScript");
        script.Should().Contain(a => a.Contains("item 2 of argv") && a.Contains("item 1 of argv"));
    }

    [Fact]
    public void The_first_helper_that_works_is_used_and_later_ones_are_not_started()
    {
        var commands = FatalErrorDialog.CommandsFor(FatalErrorDialog.Desktop.Linux, "Cinder", "message");
        var started = new List<string>();

        var shown = FatalErrorDialog.TryShow(commands, command =>
        {
            started.Add(command.Program);
            return command.Program == "kdialog";   // zenity is "not installed"
        });

        shown.Should().BeTrue();
        started.Should().Equal("zenity", "kdialog");
    }

    [Fact]
    public void A_helper_that_throws_does_not_stop_the_next_one_being_tried()
    {
        var commands = FatalErrorDialog.CommandsFor(FatalErrorDialog.Desktop.Linux, "Cinder", "message");
        var started = new List<string>();

        var shown = FatalErrorDialog.TryShow(commands, command =>
        {
            started.Add(command.Program);
            return command.Program switch
            {
                "zenity" => throw new InvalidOperationException("cannot start"),
                "kdialog" => throw new System.ComponentModel.Win32Exception(2),
                _ => true,
            };
        });

        shown.Should().BeTrue();
        started.Should().Equal("zenity", "kdialog", "xmessage");
    }

    [Fact]
    public void When_nothing_can_show_the_message_the_caller_is_told_so_it_can_fall_back()
    {
        var commands = FatalErrorDialog.CommandsFor(FatalErrorDialog.Desktop.Linux, "Cinder", "message");

        FatalErrorDialog.TryShow(commands, _ => false).Should().BeFalse();
        FatalErrorDialog.TryShow(commands, _ => throw new IOException("no display")).Should().BeFalse();
        FatalErrorDialog.TryShow([], _ => true).Should().BeFalse();
    }

    [Fact]
    public void The_fatal_message_says_what_happened_and_where_the_details_are()
    {
        var bundle = Path.Combine(Path.GetTempPath(), "Cinder", "logs", "crash-20260101T000000Z-1.txt");

        var text = CrashHandler.FatalMessage(new InvalidOperationException("Sequence contains no elements"), bundle);

        text.Should().Contain("Cinder has stopped")
            .And.Contain("Sequence contains no elements.")
            .And.Contain(bundle)
            .And.Contain("Nothing has been sent anywhere");

        CrashHandler.FatalMessage(new InvalidOperationException("x"), bundle: null)
            .Should().Contain("could not be written to disk");
    }

    [Fact]
    public void An_unattended_run_reports_without_starting_any_dialog()
    {
        // interactive: false is what the screenshot harness passes. This must return at once
        // (a dialog here would hang CI) and must not throw whatever it is given.
        var act = () => CrashHandler.ReportFatal(new InvalidOperationException("boom"), bundle: null, interactive: false);

        act.ExecutionTime().Should().BeLessThan(TimeSpan.FromSeconds(2));
        act.Should().NotThrow();
    }
}
