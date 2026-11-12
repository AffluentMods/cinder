using System.Text;
using Cinder.App.Services;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>
/// The YARA-lite matcher is also the IOC scanner's content search, so every test here is about
/// one of two failures: a pattern that is in the data and is not reported (false negative), or a
/// rule reported as matched whose condition does not hold (false positive).
/// </summary>
public class YaraLiteTests
{
    private static async Task<List<YaraLiteMatch>> ScanAsync(string rules, byte[] data, int bufferSize = 1 << 16)
    {
        var parsed = YaraLiteParser.Parse(rules);
        return await ScanAsync(parsed, data, bufferSize);
    }

    private static async Task<List<YaraLiteMatch>> ScanAsync(IReadOnlyList<YaraLiteRule> rules, byte[] data, int bufferSize = 1 << 16)
    {
        var set = YaraLiteRuleset.Compile(rules);
        var hits = new List<YaraLiteMatch>();
        using var stream = new MemoryStream(data);
        await foreach (var hit in set.ScanAsync(stream, "mem", bufferSize))
        {
            hits.Add(hit);
        }
        return hits;
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    // ------------------------------------------------------------------ parsing

    [Fact]
    public void Parses_meta_strings_and_condition()
    {
        var rule = YaraLiteParser.Parse("""
            rule Sample
            {
                meta:
                    author = "someone"
                    description = "has a \"quote\""
                strings:
                    $a = "evil"
                    $b = { 4D 5A 90 00 }
                condition:
                    $a and $b
            }
            """).Should().ContainSingle().Subject;

        rule.Name.Should().Be("Sample");
        rule.SkipReason.Should().BeNull();
        rule.Meta["author"].Should().Be("someone");
        rule.Meta["description"].Should().Be("has a \"quote\"");
        rule.Condition.Should().Be("$a and $b");
        rule.Strings.Should().HaveCount(2);
        rule.Strings[0].Pattern.Should().Equal(Ascii("evil"));
        rule.Strings[1].IsHex.Should().BeTrue();
        rule.Strings[1].Pattern.Should().Equal(0x4D, 0x5A, 0x90, 0x00);
    }

    [Theory]
    [InlineData("rule Tagged : apt hacktool {\n strings: $a = \"x1\"\n condition: $a\n}")]
    [InlineData("rule OneLine { strings: $a = \"x1\" condition: $a }")]
    [InlineData("rule Indented {\n    strings:\n        $a = \"x1\"\n    condition:\n        $a\n    }")]
    [InlineData("private rule Private\r\n{\r\n  strings:\r\n    $a = \"x1\"\r\n  condition:\r\n    $a\r\n}\r\n")]
    public void Rule_headers_and_layouts_that_used_to_be_dropped_now_parse(string source)
    {
        var rule = YaraLiteParser.Parse(source).Should().ContainSingle().Subject;
        rule.SkipReason.Should().BeNull();
        rule.Strings.Should().ContainSingle().Which.Pattern.Should().Equal(Ascii("x1"));
    }

    [Fact]
    public void Several_rules_in_one_source_are_all_returned_and_kept_apart()
    {
        var rules = YaraLiteParser.Parse("""
            import "pe"
            rule First : tag { strings: $a = "one" condition: $a }
            /* a comment with rule Fake { } inside */
            rule Second {
              strings:
                $b = "two"
              condition:
                any of them
              }
            rule Third { strings: $c = "three" condition: $c }
            """);

        rules.Select(r => r.Name).Should().Equal("First", "Second", "Third");
        rules.Should().OnlyContain(r => r.SkipReason == null);
        rules[1].Strings.Should().ContainSingle().Which.Identifier.Should().Be("$b");
    }

    [Fact]
    public void A_url_inside_a_string_is_not_a_comment()
    {
        var rule = YaraLiteParser.Parse("""
            rule Url {
              strings:
                $u = "http://evil.example/gate.php" // the C2
                $v = "after"   /* block */
              condition:
                any of them
            }
            """).Single();

        rule.SkipReason.Should().BeNull();
        rule.Strings.Select(s => s.Identifier).Should().Equal("$u", "$v");
        rule.Strings[0].Pattern.Should().Equal(Ascii("http://evil.example/gate.php"));
    }

    [Fact]
    public void Escapes_decode_to_the_bytes_they_stand_for()
    {
        var rule = YaraLiteParser.Parse("""rule E { strings: $a = "a\\b\"c\x00\xFFd\n" condition: $a }""").Single();
        rule.SkipReason.Should().BeNull();
        rule.Strings[0].Pattern.Should().Equal((byte)'a', (byte)'\\', (byte)'b', (byte)'"', (byte)'c', 0x00, 0xFF, (byte)'d', (byte)'\n');
    }

    [Fact]
    public void Wide_and_ascii_modifiers_produce_the_right_forms_under_one_identifier()
    {
        var rule = YaraLiteParser.Parse("""
            rule W {
              strings:
                $w = "ab" wide
                $both = "cd" ascii wide nocase
              condition: any of them
            }
            """).Single();

        rule.SkipReason.Should().BeNull();
        rule.Strings.Should().HaveCount(3);
        rule.Strings[0].Pattern.Should().Equal((byte)'a', 0, (byte)'b', 0);
        rule.Strings.Where(s => s.Identifier == "$both").Should().HaveCount(2).And.OnlyContain(s => s.NoCase);
    }

    [Theory]
    [InlineData("$a = { 4D ?? 5A }", "wildcards")]
    [InlineData("$a = { 4D [2-4] 5A }", "wildcards")]
    [InlineData("$a = { 4D ( 5A | 5B ) }", "wildcards")]
    [InlineData("$a = /ev[i1]l/i", "regex")]
    [InlineData("$a = \"evil\" fullword", "fullword")]
    [InlineData("$a = \"evil\" xor(0x01-0xff)", "xor")]
    [InlineData("$a = \"evil\" base64", "base64")]
    public void Strings_the_subset_cannot_match_exactly_skip_the_rule_with_a_reason(string definition, string reasonContains)
    {
        var rule = YaraLiteParser.Parse($"rule R {{ strings: {definition} condition: any of them }}").Single();
        rule.SkipReason.Should().NotBeNull().And.Contain(reasonContains);
    }

    [Theory]
    [InlineData("$a = { 4D 5 }")]
    [InlineData("$a = \"unterminated")]
    [InlineData("$a = \"x\" $a = \"y\"")]
    [InlineData("$a = \"x\" sideways")]
    [InlineData("$a = \"\"")]
    [InlineData("$a \"x\"")]
    public void Malformed_strings_skip_the_rule_instead_of_being_dropped_silently(string definition)
    {
        var rule = YaraLiteParser.Parse($"rule R {{ strings: {definition}\n condition: any of them }}").Single();
        rule.SkipReason.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("uint16(0) == 0x5A4D and $a")]
    [InlineData("filesize < 100KB and $a")]
    [InlineData("not $a")]
    [InlineData("none of them")]
    [InlineData("#a > 2")]
    [InlineData("$a at 0")]
    [InlineData("$a in (0..100)")]
    [InlineData("for any of them : ( $ )")]
    [InlineData("pe.is_dll() and $a")]
    [InlineData("50% of them")]
    [InlineData("0 of them")]
    [InlineData("$missing")]
    [InlineData("any of ($z*)")]
    [InlineData("$a and")]
    [InlineData("($a")]
    [InlineData("true")]
    public void Conditions_outside_the_subset_skip_the_rule_with_a_reason(string condition)
    {
        var rule = YaraLiteParser.Parse($"rule R {{ strings: $a = \"abc\" condition: {condition} }}").Single();
        rule.SkipReason.Should().NotBeNull().And.StartWith("condition not supported");
    }

    [Fact]
    public void A_rule_without_a_condition_or_closing_brace_is_reported_not_guessed_at()
    {
        YaraLiteParser.Parse("rule NoCondition { strings: $a = \"abc\" }").Single()
            .SkipReason.Should().Contain("no condition");
        YaraLiteParser.Parse("rule Open { strings: $a = \"abc\" condition: $a").Single()
            .SkipReason.Should().Contain("unterminated");
    }

    // ------------------------------------------------------------------ conditions

    [Theory]
    [InlineData("any of them", "xx AAA xx", true)]
    [InlineData("any of them", "xx nothing xx", false)]
    [InlineData("all of them", "AAA BBB CCC", true)]
    [InlineData("all of them", "AAA BBB", false)]
    [InlineData("2 of them", "AAA CCC", true)]
    [InlineData("2 of them", "CCC", false)]
    [InlineData("3 of them", "AAA BBB", false)]
    [InlineData("$a and $b", "AAA BBB", true)]
    [InlineData("$a and $b", "AAA", false)]
    [InlineData("$a and $b", "BBB", false)]
    [InlineData("$a or $b", "BBB", true)]
    [InlineData("$a or $b", "CCC", false)]
    [InlineData("$a and ($b or $c)", "AAA CCC", true)]
    [InlineData("$a and ($b or $c)", "BBB CCC", false)]
    [InlineData("$a and $b or $c", "CCC", true)]
    [InlineData("$a and $b or $c", "AAA", false)]
    [InlineData("any of ($a, $b)", "CCC", false)]
    [InlineData("any of ($a, $b)", "BBB", true)]
    [InlineData("all of ($a, $c)", "AAA CCC", true)]
    [InlineData("all of ($a, $c)", "AAA BBB", false)]
    [InlineData("2 of ($a, $b, $c)", "BBB CCC", true)]
    [InlineData("1 of ($a*)", "AAA", true)]
    [InlineData("1 of ($a*)", "BBB", false)]
    public async Task A_rule_reports_only_when_its_condition_holds(string condition, string data, bool expected)
    {
        var rules = $$"""
            rule R {
              strings:
                $a = "AAA"
                $b = "BBB"
                $c = "CCC"
              condition:
                {{condition}}
            }
            """;
        var parsed = YaraLiteParser.Parse(rules);
        parsed.Single().SkipReason.Should().BeNull();

        var hits = await ScanAsync(parsed, Ascii(data));

        if (expected)
        {
            hits.Should().NotBeEmpty().And.OnlyContain(h => h.RuleName == "R");
        }
        else
        {
            hits.Should().BeEmpty("a rule must never be shown as matched when its condition is not satisfied");
        }
    }

    [Fact]
    public async Task Hits_held_back_until_the_condition_holds_are_all_released_with_their_offsets()
    {
        var hits = await ScanAsync(
            "rule R { strings: $a = \"AAA\" $b = \"BBB\" condition: all of them }",
            Ascii("..AAA......BBB.."),
            bufferSize: 4);

        hits.Select(h => (h.Identifier, h.Offset)).Should().BeEquivalentTo(new[] { ("$a", 2L), ("$b", 11L) });
    }

    [Fact]
    public async Task Either_form_of_an_ascii_wide_string_satisfies_its_identifier_once()
    {
        var wide = Encoding.Unicode.GetBytes("secret");
        var hits = await ScanAsync(
            "rule R { strings: $s = \"secret\" ascii wide condition: $s }",
            [.. Ascii("--"), .. wide, .. Ascii("--secret")]);

        hits.Should().ContainSingle().Which.Offset.Should().Be(2);
    }

    [Fact]
    public void Compile_marks_a_hand_built_rule_with_a_bad_condition_as_skipped()
    {
        var set = YaraLiteRuleset.Compile(
        [
            new YaraLiteRule
            {
                Name = "bad",
                Strings = [new YaraLiteString { Identifier = "$a", Pattern = Ascii("abc") }],
                Condition = "filesize > 10",
            },
        ]);

        set.Rules.Single().SkipReason.Should().StartWith("condition not supported");
    }

    // ------------------------------------------------------------------ case-insensitive matching

    [Theory]
    [InlineData("mimikatz.exe")]                 // 11 letters: used to yield no pattern at all
    [InlineData("badsite.net")]
    [InlineData("powershell")]
    [InlineData("a-very-long-malicious-domain-name.example.org")]   // > 16 bytes: used to be case-sensitive
    [InlineData("x")]
    public async Task Nocase_matches_at_any_length_and_in_any_case(string indicator)
    {
        var rules = new[]
        {
            new YaraLiteRule
            {
                Name = "ioc",
                Strings = [new YaraLiteString { Identifier = "$a", Pattern = Ascii(indicator), NoCase = true }],
            },
        };

        foreach (var form in new[] { indicator, indicator.ToUpperInvariant(), Alternate(indicator) })
        {
            var hits = await ScanAsync(rules, Ascii("log line: " + form + " seen"));
            hits.Should().ContainSingle($"'{form}' is the indicator '{indicator}'").Which.Offset.Should().Be(10);
        }
    }

    [Fact]
    public async Task Without_nocase_a_pattern_is_case_sensitive_and_hex_is_always_exact()
    {
        (await ScanAsync("rule R { strings: $a = \"Evil\" condition: $a }", Ascii("evil EVIL"))).Should().BeEmpty();
        (await ScanAsync("rule R { strings: $a = { 4D 5A } condition: $a }", Ascii("mz"))).Should().BeEmpty();
        (await ScanAsync("rule R { strings: $a = { 4D 5A } condition: $a }", Ascii("MZ"))).Should().ContainSingle();
    }

    [Fact]
    public async Task Nocase_folds_ascii_letters_only()
    {
        // 0xC9 and 0xE9 are É / é in Latin-1. YARA's nocase does not relate them, and neither do we.
        var rules = new[]
        {
            new YaraLiteRule
            {
                Name = "r",
                Strings = [new YaraLiteString { Identifier = "$a", Pattern = [(byte)'a', 0xC9], NoCase = true }],
            },
        };
        (await ScanAsync(rules, [(byte)'A', 0xE9])).Should().BeEmpty();
        (await ScanAsync(rules, [(byte)'A', 0xC9])).Should().ContainSingle();
    }

    private static string Alternate(string s) =>
        string.Concat(s.Select((c, i) => i % 2 == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)));

    // ------------------------------------------------------------------ shared literals

    [Fact]
    public async Task A_literal_shared_by_several_rules_reports_for_every_one_of_them()
    {
        var hits = await ScanAsync("""
            rule One   { strings: $x = "cmd.exe" condition: $x }
            rule Two   { strings: $y = "cmd.exe" condition: $y }
            rule Three { strings: $z = "CMD.EXE" nocase condition: $z }
            rule Four  { strings: $h = { 63 6D 64 2E 65 78 65 } condition: $h }
            """, Ascii("run cmd.exe /c"));

        hits.Select(h => h.RuleName).Should().BeEquivalentTo("One", "Two", "Three", "Four");
        hits.Should().OnlyContain(h => h.Offset == 4);
    }

    [Fact]
    public async Task Overlapping_and_nested_patterns_are_all_found()
    {
        var hits = await ScanAsync("""
            rule R {
              strings:
                $a = "he"
                $b = "she"
                $c = "his"
                $d = "hers"
              condition: any of them
            }
            """, Ascii("ushers"));

        hits.Select(h => (h.Identifier, h.Offset)).Should().BeEquivalentTo(new[] { ("$b", 1L), ("$a", 2L), ("$d", 2L) });
    }

    // ------------------------------------------------------------------ read boundaries

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1000)]
    public async Task A_pattern_is_found_wherever_the_read_boundaries_fall(int bufferSize)
    {
        // 200 bytes: longer than the 64-byte overlap the old scanner carried between reads.
        var needle = string.Concat(Enumerable.Range(0, 20).Select(i => $"segment{i:D2}-"));
        needle.Length.Should().Be(200);
        var data = Ascii(new string('.', 777) + needle + new string('.', 500));

        var rules = new[]
        {
            new YaraLiteRule
            {
                Name = "long",
                Strings =
                [
                    new YaraLiteString { Identifier = "$exact", Pattern = Ascii(needle) },
                    new YaraLiteString { Identifier = "$nocase", Pattern = Ascii(needle.ToUpperInvariant()), NoCase = true },
                ],
                Condition = "all of them",
            },
        };

        var hits = await ScanAsync(rules, data, bufferSize);

        hits.Should().HaveCount(2).And.OnlyContain(h => h.Offset == 777);
    }

    [Fact]
    public async Task A_long_utf16_indicator_split_across_the_default_megabyte_read_is_found()
    {
        var indicator = "https://update-check.malicious-domain.example/payload/stage2.bin";   // 128 bytes as UTF-16
        var wide = Encoding.Unicode.GetBytes(indicator);
        var data = new byte[(1 << 20) + 4096];
        var at = (1 << 20) - 100;                // straddles the 1 MiB boundary by 28 bytes
        wide.CopyTo(data, at);
        var path = Path.Combine(Path.GetTempPath(), "cinder-yara-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(path, data);
        try
        {
            var set = YaraLiteRuleset.Compile(
            [
                new YaraLiteRule
                {
                    Name = "ioc",
                    Strings = [new YaraLiteString { Identifier = "$w", Pattern = wide, NoCase = true, Text = indicator }],
                },
            ]);
            set.LongestPattern.Should().Be(128);

            var hits = new List<YaraLiteMatch>();
            await foreach (var hit in set.ScanAsync(path))
            {
                hits.Add(hit);
            }

            var only = hits.Should().ContainSingle().Subject;
            only.Offset.Should().Be(at);
            only.MatchedString.Should().Be(indicator);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void The_automaton_agrees_with_a_naive_search_on_random_input(int seed)
    {
        // A small alphabet makes overlaps, shared prefixes and suffix-of-another-pattern cases
        // (the ones failure links exist for) the norm rather than the exception.
        var random = new Random(seed);
        byte[] Bytes(int length) => Enumerable.Range(0, length).Select(_ => (byte)('a' + random.Next(3))).ToArray();

        var patterns = Enumerable.Range(0, 60).Select(_ => Bytes(1 + random.Next(7))).ToList();
        var text = Bytes(5000);
        var automaton = new ByteAutomaton(patterns);

        var seen = new bool[patterns.Count];
        var found = new Dictionary<int, int>();
        var sink = new List<(int Pattern, int End)>();
        var state = 0;
        var position = 0;
        while (position < text.Length)
        {
            var take = Math.Min(text.Length - position, 1 + random.Next(40));   // ragged reads
            sink.Clear();
            automaton.Scan(text.AsSpan(position, take), ref state, foldAscii: false, seen, sink);
            foreach (var (pattern, end) in sink)
            {
                found.Add(pattern, position + end - patterns[pattern].Length + 1);
            }
            position += take;
        }

        for (int i = 0; i < patterns.Count; i++)
        {
            var expected = text.AsSpan().IndexOf(patterns[i]);
            if (expected < 0)
            {
                found.Should().NotContainKey(i);
            }
            else
            {
                found.Should().Contain(i, expected, $"pattern {i} first occurs at {expected}");
            }
        }
    }

    // ------------------------------------------------------------------ threading

    [Fact]
    public async Task Reading_and_matching_never_run_on_the_callers_synchronization_context()
    {
        var set = YaraLiteRuleset.Compile(YaraLiteParser.Parse("rule R { strings: $a = \"needle\" $b = \"thread\" condition: any of them }"));
        var stream = new ContextRecordingStream(Ascii(new string('.', 300) + "needle" + new string('.', 300) + "thread" + new string('.', 300)));
        var ui = new MarkerContext();
        var hits = 0;

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            // Deliberately no ConfigureAwait: this is what a view-model command on the UI thread does.
            await foreach (var _ in set.ScanAsync(stream, "mem", bufferSize: 64))
            {
                hits++;
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        hits.Should().Be(2);
        ui.Posts.Should().BeGreaterThan(0, "the consumer really was resuming on the marked context");
        stream.Reads.Should().BeGreaterThan(5);
        stream.ReadsOnMarkedContext.Should().Be(0, "file I/O and the Aho-Corasick pass belong on the thread pool, not the UI thread");
    }

    /// <summary>Stands in for the UI thread's context: continuations posted to it run with it current.</summary>
    private sealed class MarkerContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SetSynchronizationContext(this);
                try
                {
                    d(state);
                }
                finally
                {
                    SetSynchronizationContext(null);
                }
            });
        }
    }

    private sealed class ContextRecordingStream(byte[] data) : MemoryStream(data)
    {
        public int Reads { get; private set; }
        public int ReadsOnMarkedContext { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            if (SynchronizationContext.Current is MarkerContext)
            {
                ReadsOnMarkedContext++;
            }
            return base.Read(buffer, offset, count);
        }
    }

    [Fact]
    public async Task Scanning_reports_the_first_occurrence_of_each_string_once()
    {
        var hits = await ScanAsync("rule R { strings: $a = \"ab\" condition: $a }", Ascii("ab ab ab ab"), bufferSize: 3);
        hits.Should().ContainSingle().Which.Offset.Should().Be(0);
    }

    [Fact]
    public async Task Skipped_rules_never_report()
    {
        var hits = await ScanAsync("""
            rule Skipped { strings: $a = "abc" condition: filesize > 0 and $a }
            rule Runs    { strings: $a = "abc" condition: $a }
            """, Ascii("abc"));

        hits.Should().ContainSingle().Which.RuleName.Should().Be("Runs");
    }
}
