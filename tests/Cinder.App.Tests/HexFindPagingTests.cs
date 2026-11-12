using System.Text;
using Cinder.App.ViewModels;
using Cinder.Hex;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>
/// Find keeps 1,000 hits at a time. "Next" on the last of them used to wrap to the first, so in a
/// file with more matches the later ones could never be reached.
/// </summary>
public class HexFindPagingTests
{
    private const int Hits = 2500;

    /// <summary>"ab" every 4 bytes: hit i (0-based) is at offset 4 * i.</summary>
    private static HexViewModel ManyHits()
    {
        var bytes = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ab..", Hits)));
        return new HexViewModel { Buffer = new ByteArrayHexBuffer(bytes, "many.bin") };
    }

    private static async Task Next(HexViewModel hex, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await hex.FindNextCommand.ExecuteAsync(null);
        }
    }

    [Fact]
    public async Task Next_past_the_thousandth_hit_continues_instead_of_wrapping()
    {
        using var hex = ManyHits();
        hex.FindQuery = "ab";
        await hex.RunFindCommand.ExecuteAsync(null);
        hex.FindStatus.Should().Be("1 of 1,000+");

        await Next(hex, 999);
        hex.CaretOffset.Should().Be(4 * 999);
        hex.FindStatus.Should().Be("1,000 of 1,000+");

        await Next(hex, 1);
        hex.CaretOffset.Should().Be(4 * 1000, "the 1,001st match exists and must be reachable");
        hex.FindStatus.Should().Be("1,001 of 2,000+");
    }

    [Fact]
    public async Task The_last_match_is_reached_counted_and_then_next_wraps_to_the_first()
    {
        using var hex = ManyHits();
        hex.FindQuery = "ab";
        await hex.RunFindCommand.ExecuteAsync(null);

        await Next(hex, Hits - 1);
        hex.CaretOffset.Should().Be(4 * (Hits - 1));
        hex.FindStatus.Should().Be("2,500 of 2,500", "once the end is reached the total is known and the plus sign goes");

        await Next(hex, 1);
        hex.CaretOffset.Should().Be(0);
        hex.FindStatus.Should().Be("1 of 1,000+");
    }

    [Fact]
    public async Task Previous_from_the_first_match_wraps_to_the_real_last_match()
    {
        using var hex = ManyHits();
        hex.FindQuery = "ab";
        await hex.RunFindCommand.ExecuteAsync(null);

        await hex.FindPreviousCommand.ExecuteAsync(null);

        hex.CaretOffset.Should().Be(4 * (Hits - 1), "not the 1,000th hit, which is only where the first page ends");
        hex.FindStatus.Should().Be("last match");

        await hex.FindPreviousCommand.ExecuteAsync(null);
        hex.CaretOffset.Should().Be(4 * (Hits - 2));
        hex.FindStatus.Should().Be("1 before last");
    }

    [Fact]
    public async Task Previous_steps_back_across_a_page_boundary_and_recovers_the_count_at_the_start()
    {
        using var hex = ManyHits();
        hex.FindQuery = "ab";
        await hex.RunFindCommand.ExecuteAsync(null);
        await Next(hex, 1000);                       // on hit 1,001, first of the second page
        hex.CaretOffset.Should().Be(4 * 1000);

        await hex.FindPreviousCommand.ExecuteAsync(null);

        hex.CaretOffset.Should().Be(4 * 999);
        hex.FindStatus.Should().Be("1,000 of 1,000+");
    }

    [Fact]
    public async Task A_find_started_past_the_first_page_lands_on_the_next_match_after_the_caret()
    {
        using var hex = ManyHits();
        hex.CaretOffset = 4 * 1800 + 1;              // just after hit 1,801
        hex.FindQuery = "ab";

        await hex.RunFindCommand.ExecuteAsync(null);

        hex.CaretOffset.Should().Be(4 * 1801, "the first match at or after the caret, even though it is beyond the first 1,000");
    }

    [Fact]
    public async Task A_small_result_still_wraps_within_itself()
    {
        var bytes = Encoding.ASCII.GetBytes("ab..ab..ab..");
        using var hex = new HexViewModel { Buffer = new ByteArrayHexBuffer(bytes, "few.bin") };
        hex.FindQuery = "ab";
        await hex.RunFindCommand.ExecuteAsync(null);
        hex.FindStatus.Should().Be("1 of 3");

        await Next(hex, 3);
        hex.CaretOffset.Should().Be(0);
        hex.FindStatus.Should().Be("1 of 3");

        await hex.FindPreviousCommand.ExecuteAsync(null);
        hex.CaretOffset.Should().Be(8);
        hex.FindStatus.Should().Be("3 of 3");
    }
}
