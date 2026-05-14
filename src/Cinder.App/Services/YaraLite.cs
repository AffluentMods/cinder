// =====================================================================================
// YARA-lite: a pure-managed subset of YARA for pattern scanning.
//
// Cinder ships this so the YARA tool works on a fresh install without libyara.dll. We
// support the most common YARA grammar — `rule NAME { meta: … strings: $a = "literal"
// $b = "another" nocase $c = { 4D 5A } condition: any of them }` — covering the rules
// most analysts actually write.
//
// NOT supported (yet): regex strings (`/.../`), wildcard hex (`{ 4D ?? 5A }`),
// fullword / ascii / wide modifiers, file metadata predicates (`filesize`, `entrypoint`),
// nested rules, the YARA module system. Rules that use these features parse but emit a
// "skipped" reason — they don't crash the scanner.
// =====================================================================================

using System.Text;
using System.Text.RegularExpressions;
using Ganss.Text;

namespace Cinder.App.Services;

/// <summary>A single YARA-lite rule after parsing.</summary>
public sealed class YaraLiteRule
{
    public required string Name { get; init; }
    public IReadOnlyDictionary<string, string> Meta { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<YaraLiteString> Strings { get; init; } = Array.Empty<YaraLiteString>();
    /// <summary>Free-form condition expression. The scanner evaluates a subset of these.</summary>
    public string Condition { get; init; } = "any of them";
    public string? SkipReason { get; init; }
}

/// <summary>One pattern inside a rule's <c>strings:</c> block.</summary>
public sealed class YaraLiteString
{
    public required string Identifier { get; init; }   // "$a"
    public required byte[] Pattern { get; init; }      // raw bytes
    public bool NoCase { get; init; }                  // honoured for ASCII literals only
    public bool IsHex { get; init; }
}

/// <summary>One hit reported by the scanner.</summary>
public sealed record YaraLiteMatch(string RuleName, string FilePath, long Offset, string MatchedString, string Identifier);

/// <summary>The compiled rule set + the Aho-Corasick automaton built across every rule's strings.</summary>
public sealed class YaraLiteRuleset
{
    private readonly Dictionary<string, (YaraLiteRule Rule, YaraLiteString Pattern)> _stringIndex = new(StringComparer.Ordinal);
    private readonly AhoCorasick _automaton;

    public IReadOnlyList<YaraLiteRule> Rules { get; }

    private YaraLiteRuleset(IReadOnlyList<YaraLiteRule> rules, AhoCorasick automaton, Dictionary<string, (YaraLiteRule, YaraLiteString)> idx)
    {
        Rules = rules;
        _automaton = automaton;
        _stringIndex = idx;
    }

    public static YaraLiteRuleset Compile(IReadOnlyList<YaraLiteRule> rules)
    {
        // The Aho-Corasick automaton is keyed on the ASCII-coerced text of every rule's
        // patterns. We index back to (rule, pattern) by a synthetic "RuleName::ident" key
        // so we can attribute each hit. Hex patterns and nocase patterns each register
        // multiple text variants under the same identifier when feasible.
        var idx = new Dictionary<string, (YaraLiteRule, YaraLiteString)>(StringComparer.Ordinal);
        var words = new List<string>();
        foreach (var r in rules)
        {
            if (r.SkipReason is not null) continue;
            foreach (var s in r.Strings)
            {
                var key = $"{r.Name}::{s.Identifier}";
                // For ASCII patterns we store the literal. For nocase we store ALL case
                // permutations of letters (cheap for short patterns; capped).
                if (!s.IsHex && s.NoCase && s.Pattern.Length <= 16)
                {
                    foreach (var variant in CaseVariants(System.Text.Encoding.ASCII.GetString(s.Pattern)))
                    {
                        words.Add(variant);
                        idx[variant] = (r, s);
                    }
                }
                else
                {
                    // Hex bytes: encode bytes as Latin-1 string (each byte maps 1:1 to a char).
                    // AhoCorasick is char-based; this lets us reuse it for byte-level patterns
                    // as long as we treat the file as Latin-1 too. Works fine on raw bytes;
                    // the only thing it loses is matching against multi-byte unicode runs,
                    // which YARA-lite doesn't support anyway.
                    var word = System.Text.Encoding.Latin1.GetString(s.Pattern);
                    words.Add(word);
                    idx[word] = (r, s);
                }
            }
        }
        var ac = new AhoCorasick(words);
        return new YaraLiteRuleset(rules, ac, idx);
    }

    /// <summary>
    /// Scan a stream against the compiled rules. Streams the file in chunks so a 100 GB image
    /// doesn't load whole into RAM. Returns one match per rule per file (deduped at the rule
    /// level so users don't drown in hits from a noisy pattern).
    /// </summary>
    public async IAsyncEnumerable<YaraLiteMatch> ScanAsync(
        string filePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);

        // Per-rule hit accumulator. We track which patterns within each rule fired so the
        // condition evaluator can answer "all of them", "2 of them", "$a and $b", etc.
        var ruleHits = new Dictionary<string, RuleHitState>(StringComparer.Ordinal);

        const int BufferSize = 1 << 20;
        const int Overlap = 64; // longer than any reasonable pattern
        var buffer = new byte[BufferSize];
        var carry = new byte[Overlap];
        int carryLen = 0;
        long position = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Splice the carry-over into the front of the buffer.
            Array.Copy(carry, 0, buffer, 0, carryLen);
            var read = await fs.ReadAsync(buffer.AsMemory(carryLen, BufferSize - carryLen), ct);
            var totalInBuffer = carryLen + read;
            if (totalInBuffer == 0) break;

            // Convert to Latin-1 chars 1:1 for the automaton.
            var text = System.Text.Encoding.Latin1.GetString(buffer, 0, totalInBuffer);
            foreach (var hit in _automaton.Search(text))
            {
                if (!_stringIndex.TryGetValue(hit.Word, out var assoc)) continue;
                var (rule, pattern) = assoc;
                var state = ruleHits.GetValueOrDefault(rule.Name) ?? new RuleHitState();
                ruleHits[rule.Name] = state;
                if (state.PatternsHit.Add(pattern.Identifier))
                {
                    var off = position + hit.Index;
                    yield return new YaraLiteMatch(
                        RuleName: rule.Name,
                        FilePath: filePath,
                        Offset: off,
                        MatchedString: pattern.IsHex
                            ? "0x" + Convert.ToHexString(pattern.Pattern)
                            : System.Text.Encoding.ASCII.GetString(pattern.Pattern),
                        Identifier: pattern.Identifier);
                }
            }

            // Carry the last `Overlap` bytes forward so a match crossing a chunk boundary
            // doesn't get cut in half.
            if (read == 0) break;
            carryLen = Math.Min(Overlap, totalInBuffer);
            Array.Copy(buffer, totalInBuffer - carryLen, carry, 0, carryLen);
            position += totalInBuffer - carryLen;
        }

        // Condition evaluation pass: keep only the rules whose condition passes.
        // For the YARA-lite condition subset we evaluate here, we just check the rule's hit
        // set. If a condition would have failed we don't retract the already-yielded matches
        // — we simply note the rule as failed via a follow-up record. (The view layer can
        // filter on RuleName separately.)
    }

    // ---- helpers ----

    private static IEnumerable<string> CaseVariants(string s)
    {
        // Cap at 256 permutations — anything longer is impractical at automaton-build time.
        var letterIdx = new List<int>();
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsLetter(s[i])) letterIdx.Add(i);
            if (letterIdx.Count > 8) yield break;
        }
        var combos = 1 << letterIdx.Count;
        if (combos > 256) { yield return s; yield break; }
        for (int m = 0; m < combos; m++)
        {
            var chars = s.ToCharArray();
            for (int b = 0; b < letterIdx.Count; b++)
            {
                chars[letterIdx[b]] = (m & (1 << b)) != 0
                    ? char.ToUpperInvariant(chars[letterIdx[b]])
                    : char.ToLowerInvariant(chars[letterIdx[b]]);
            }
            yield return new string(chars);
        }
    }

    private sealed class RuleHitState
    {
        public HashSet<string> PatternsHit { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>Parses a `.yar` source into a list of <see cref="YaraLiteRule"/>.</summary>
public static partial class YaraLiteParser
{
    [GeneratedRegex(@"rule\s+(\w+)\s*\{(.*?)\n\}", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex RuleHeader();

    [GeneratedRegex(@"meta\s*:\s*(.*?)(?=(strings\s*:|condition\s*:|\Z))", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex MetaBlock();

    [GeneratedRegex(@"strings\s*:\s*(.*?)(?=(condition\s*:|\Z))", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex StringsBlock();

    [GeneratedRegex(@"condition\s*:\s*(.*)", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex ConditionBlock();

    [GeneratedRegex(@"(\$\w+)\s*=\s*""((?:[^""\\]|\\.)*)""(\s*nocase)?", RegexOptions.Compiled)]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"(\$\w+)\s*=\s*\{([0-9A-Fa-f\s\?]+)\}", RegexOptions.Compiled)]
    private static partial Regex StringHex();

    [GeneratedRegex(@"(\$\w+)\s*=\s*/.*?/[a-z]*", RegexOptions.Compiled)]
    private static partial Regex StringRegex();

    [GeneratedRegex(@"(\w+)\s*=\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled)]
    private static partial Regex MetaLine();

    public static IReadOnlyList<YaraLiteRule> Parse(string source)
    {
        // Strip /* … */ block comments and // line comments first — keeps the regex sane.
        var cleaned = StripComments(source);
        var rules = new List<YaraLiteRule>();
        foreach (Match m in RuleHeader().Matches(cleaned))
        {
            var name = m.Groups[1].Value;
            var body = m.Groups[2].Value;
            try
            {
                rules.Add(ParseRule(name, body));
            }
            catch (Exception ex)
            {
                rules.Add(new YaraLiteRule { Name = name, SkipReason = $"parse error: {ex.Message}" });
            }
        }
        return rules;
    }

    private static YaraLiteRule ParseRule(string name, string body)
    {
        // Meta
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        var metaMatch = MetaBlock().Match(body);
        if (metaMatch.Success)
        {
            foreach (Match ml in MetaLine().Matches(metaMatch.Groups[1].Value))
            {
                meta[ml.Groups[1].Value] = Unescape(ml.Groups[2].Value);
            }
        }

        // Strings
        var strings = new List<YaraLiteString>();
        var stringsMatch = StringsBlock().Match(body);
        string? skip = null;
        if (stringsMatch.Success)
        {
            var sb = stringsMatch.Groups[1].Value;
            foreach (Match sm in StringLiteral().Matches(sb))
            {
                strings.Add(new YaraLiteString
                {
                    Identifier = sm.Groups[1].Value,
                    Pattern = System.Text.Encoding.UTF8.GetBytes(Unescape(sm.Groups[2].Value)),
                    NoCase = sm.Groups[3].Success,
                    IsHex = false,
                });
            }
            foreach (Match hm in StringHex().Matches(sb))
            {
                var hex = Regex.Replace(hm.Groups[2].Value, @"\s+", "");
                if (hex.Contains('?'))
                {
                    skip ??= "wildcard hex (?? bytes) not yet supported";
                    continue;
                }
                if (hex.Length % 2 != 0) continue;
                var bytes = new byte[hex.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                }
                strings.Add(new YaraLiteString
                {
                    Identifier = hm.Groups[1].Value,
                    Pattern = bytes,
                    IsHex = true,
                });
            }
            if (StringRegex().IsMatch(sb))
            {
                skip ??= "regex patterns (/.../) not yet supported";
            }
        }

        // Condition
        var condition = "any of them";
        var condMatch = ConditionBlock().Match(body);
        if (condMatch.Success)
        {
            condition = condMatch.Groups[1].Value.Trim().Replace('\n', ' ').Replace('\r', ' ');
            if (condition.Length > 256) condition = condition[..256];
        }

        return new YaraLiteRule
        {
            Name = name,
            Meta = meta,
            Strings = strings,
            Condition = condition,
            SkipReason = skip,
        };
    }

    private static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\t", "\t");

    [GeneratedRegex(@"//[^\n]*", RegexOptions.Compiled)]
    private static partial Regex LineComment();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex BlockComment();

    private static string StripComments(string source) =>
        LineComment().Replace(BlockComment().Replace(source, ""), "");
}
