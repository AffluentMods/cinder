using Xunit;
using Cinder.App.Services;
using FluentAssertions;

namespace Cinder.App.Tests;

public class CommandSearchTests
{
    private static CommandDescriptor Cmd(string title, string category = "Tools", string? subtitle = null) =>
        new(title.ToLowerInvariant(), title, subtitle, category, _ => Task.CompletedTask);

    [Fact]
    public void Score_ranks_prefix_over_word_start_over_substring_over_subsequence()
    {
        CommandRegistry.Score(Cmd("Hex Viewer"), "hex").Should().Be(100);
        CommandRegistry.Score(Cmd("Open Hex Viewer"), "hex").Should().Be(80);
        CommandRegistry.Score(Cmd("Shex tool"), "hex").Should().Be(60);
        CommandRegistry.Score(Cmd("Hash sets"), "hst").Should().Be(40);
        CommandRegistry.Score(Cmd("Timeline", category: "Examine"), "exam").Should().Be(25);
        CommandRegistry.Score(Cmd("Timeline", subtitle: "Super-timeline of everything"), "super").Should().Be(15);
        CommandRegistry.Score(Cmd("Timeline"), "zzz").Should().Be(0);
    }

    [Fact]
    public void Subsequence_needs_three_characters_so_two_letters_do_not_match_everything()
    {
        CommandRegistry.Score(Cmd("Registry"), "ry").Should().Be(60); // substring, fine
        CommandRegistry.Score(Cmd("Registry"), "rt").Should().Be(0);  // a subsequence, too short to count
    }

    [Fact]
    public void Word_start_splits_on_punctuation_used_in_titles()
    {
        CommandRegistry.Score(Cmd("$LogFile"), "log").Should().Be(80);
        CommandRegistry.Score(Cmd("Email (PST/MBOX)"), "mbox").Should().Be(80);
    }
}
