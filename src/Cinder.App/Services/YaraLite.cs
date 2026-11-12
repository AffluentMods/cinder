// =====================================================================================
// YARA-lite: a pure-managed subset of YARA for pattern scanning.
//
// Cinder ships this so the YARA tool works on a fresh install without libyara.dll. It is
// also the content matcher behind the IOC scanner, so the rule that governs every choice
// in this file is: a missed pattern is a false negative in someone's evidence, and a rule
// shown as matched when its condition is not satisfied is a false positive. Anything the
// subset cannot do exactly is reported as *skipped, with the reason* — never approximated.
//
// Supported:
//   rule NAME [: tag …] { meta: … strings: … condition: … }   (also `private` / `global`)
//   text strings   $a = "literal" [nocase] [ascii] [wide] [private]   (\" \\ \n \r \t \xHH)
//   hex strings    $b = { 4D 5A 90 00 }
//   conditions     $a · any of them · all of them · N of them · N of ($a, $b*) ·
//                  and · or · parentheses
//
// Skipped with a reason: regex strings (/…/), hex wildcards / jumps / alternatives,
// fullword / xor / base64 modifiers, and any condition using something outside the list
// above (not, none, filesize, uint16(0), #a, @a, at, in, for, modules, …).
//
// Matching is a byte-level Aho-Corasick automaton whose state is carried from one read to
// the next, so a pattern of any length that straddles a read boundary is still found.
// `nocase` runs on a second automaton fed ASCII-folded bytes, so it works at any length.
// =====================================================================================

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Cinder.App.Services;

/// <summary>A single YARA-lite rule after parsing.</summary>
public sealed class YaraLiteRule
{
    public required string Name { get; init; }
    public IReadOnlyDictionary<string, string> Meta { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The rule's patterns. One identifier can appear twice: <c>ascii wide</c> yields an ASCII
    /// form and a UTF-16LE form under the same identifier, and either one counts as that
    /// string having matched.
    /// </summary>
    public IReadOnlyList<YaraLiteString> Strings { get; init; } = Array.Empty<YaraLiteString>();

    /// <summary>The condition expression. See the file header for the supported subset.</summary>
    public string Condition { get; init; } = "any of them";

    /// <summary>Why the rule is not being run, or null when it is.</summary>
    public string? SkipReason { get; init; }
}

/// <summary>One pattern inside a rule's <c>strings:</c> block.</summary>
public sealed class YaraLiteString
{
    public required string Identifier { get; init; }   // "$a"
    public required byte[] Pattern { get; init; }      // raw bytes, exactly as they appear in the file

    /// <summary>ASCII letters match in either case. Honoured at any pattern length.</summary>
    public bool NoCase { get; init; }
    public bool IsHex { get; init; }

    /// <summary>The literal as written, for display when <see cref="Pattern"/> is a wide (UTF-16LE) form.</summary>
    public string? Text { get; init; }
}

/// <summary>One hit reported by the scanner.</summary>
public sealed record YaraLiteMatch(string RuleName, string FilePath, long Offset, string MatchedString, string Identifier);

/// <summary>The compiled rule set: one automaton for exact patterns, one for case-insensitive ones.</summary>
public sealed class YaraLiteRuleset
{
    private const int DefaultBufferSize = 1 << 20;

    private sealed record PatternRef(int RuleIndex, YaraLiteString String);

    private readonly ByteAutomaton? _exact;
    private readonly ByteAutomaton? _folded;
    private readonly PatternRef[] _exactPatterns;
    private readonly PatternRef[] _foldedPatterns;
    private readonly YaraCondition?[] _conditions;

    /// <summary>
    /// The rules as compiled. A rule whose condition or patterns cannot be run exactly comes
    /// back with <see cref="YaraLiteRule.SkipReason"/> set, even if the caller passed it without one.
    /// </summary>
    public IReadOnlyList<YaraLiteRule> Rules { get; }

    /// <summary>Length in bytes of the longest pattern in the set (0 when there are none).</summary>
    public int LongestPattern { get; }

    private YaraLiteRuleset(
        IReadOnlyList<YaraLiteRule> rules,
        YaraCondition?[] conditions,
        List<(byte[] Bytes, PatternRef Ref)> exact,
        List<(byte[] Bytes, PatternRef Ref)> folded)
    {
        Rules = rules;
        _conditions = conditions;
        _exactPatterns = exact.Select(p => p.Ref).ToArray();
        _foldedPatterns = folded.Select(p => p.Ref).ToArray();
        _exact = exact.Count > 0 ? new ByteAutomaton(exact.Select(p => p.Bytes).ToList()) : null;
        _folded = folded.Count > 0 ? new ByteAutomaton(folded.Select(p => p.Bytes).ToList()) : null;
        LongestPattern = exact.Concat(folded).Select(p => p.Bytes.Length).DefaultIfEmpty(0).Max();
    }

    public static YaraLiteRuleset Compile(IReadOnlyList<YaraLiteRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var compiled = new List<YaraLiteRule>(rules.Count);
        var conditions = new YaraCondition?[rules.Count];
        var exact = new List<(byte[], PatternRef)>();
        var folded = new List<(byte[], PatternRef)>();

        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule.SkipReason is not null)
            {
                compiled.Add(rule);
                continue;
            }

            string? reason = null;
            if (rule.Strings.Any(s => s.Pattern.Length == 0))
            {
                reason = "a string in this rule is empty";
            }
            var identifiers = rule.Strings.Select(s => s.Identifier).Distinct(StringComparer.Ordinal).ToList();
            if (reason is null && !YaraCondition.TryParse(rule.Condition, identifiers, out conditions[i], out var error))
            {
                reason = error;
            }
            if (reason is not null)
            {
                // Never run a rule we cannot evaluate exactly: say so instead.
                conditions[i] = null;
                compiled.Add(new YaraLiteRule
                {
                    Name = rule.Name,
                    Meta = rule.Meta,
                    Strings = rule.Strings,
                    Condition = rule.Condition,
                    SkipReason = reason,
                });
                continue;
            }

            compiled.Add(rule);
            foreach (var s in rule.Strings)
            {
                // Every (rule, string) pair is its own automaton entry. Two rules that share a
                // literal therefore both report it — nothing is keyed on the pattern text.
                var reference = new PatternRef(i, s);
                if (s.NoCase && !s.IsHex)
                {
                    folded.Add((ByteAutomaton.FoldAscii(s.Pattern), reference));
                }
                else
                {
                    exact.Add((s.Pattern, reference));
                }
            }
        }

        return new YaraLiteRuleset(compiled, conditions, exact, folded);
    }

    /// <summary>
    /// Scan a file against the compiled rules. Streams it in chunks so a 100 GB image does not
    /// load whole into RAM. Reports the first occurrence of each string of each rule, and only
    /// once that rule's condition is satisfied — a rule whose condition never becomes true
    /// reports nothing at all.
    /// </summary>
    public async IAsyncEnumerable<YaraLiteMatch> ScanAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_exact is null && _folded is null)
        {
            yield break;
        }

        // Auto-detect .E01 chains so YARA can scan an EWF image directly without prior conversion.
        await using var fs = Cinder.Imaging.EvidenceOpener.Open(filePath);
        await foreach (var match in ScanAsync(fs, filePath, DefaultBufferSize, ct).ConfigureAwait(false))
        {
            yield return match;
        }
    }

    /// <summary>
    /// Scan an already-open stream. Reading and matching both happen on the thread pool, one
    /// chunk at a time, so a caller on the UI thread stays responsive however large the input.
    /// </summary>
    internal async IAsyncEnumerable<YaraLiteMatch> ScanAsync(
        Stream stream,
        string filePath,
        int bufferSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        if (_exact is null && _folded is null)
        {
            yield break;
        }

        var session = new ScanSession(this, filePath);
        var buffer = new byte[bufferSize];
        var fresh = new List<YaraLiteMatch>();

        while (!session.NothingLeftToFind)
        {
            ct.ThrowIfCancellationRequested();
            var read = await Task.Run(() =>
            {
                var n = stream.Read(buffer, 0, buffer.Length);
                if (n > 0)
                {
                    session.Feed(buffer.AsSpan(0, n), fresh);
                }
                return n;
            }, ct).ConfigureAwait(false);

            if (read <= 0)
            {
                break;
            }
            foreach (var match in fresh)
            {
                yield return match;
            }
            fresh.Clear();
        }
    }

    /// <summary>Per-scan state: where each automaton is, and which strings each rule has seen.</summary>
    private sealed class ScanSession
    {
        private readonly YaraLiteRuleset _set;
        private readonly string _filePath;
        private readonly bool[] _exactSeen;
        private readonly bool[] _foldedSeen;
        private readonly RuleState?[] _rules;
        private readonly List<(int Pattern, int End)> _sink = [];
        private readonly List<(long Offset, PatternRef Ref)> _found = [];
        private int _exactState;
        private int _foldedState;
        private int _unseen;
        private long _position;

        public ScanSession(YaraLiteRuleset set, string filePath)
        {
            _set = set;
            _filePath = filePath;
            _exactSeen = new bool[set._exactPatterns.Length];
            _foldedSeen = new bool[set._foldedPatterns.Length];
            _unseen = _exactSeen.Length + _foldedSeen.Length;
            _rules = new RuleState?[set.Rules.Count];
            for (int i = 0; i < _rules.Length; i++)
            {
                if (set._conditions[i] is { } condition)
                {
                    _rules[i] = new RuleState(condition);
                }
            }
        }

        /// <summary>Every pattern has been seen once, so no further read can add a match.</summary>
        public bool NothingLeftToFind => _unseen == 0;

        public void Feed(ReadOnlySpan<byte> data, List<YaraLiteMatch> fresh)
        {
            _found.Clear();
            Collect(_set._exact, _set._exactPatterns, _exactSeen, ref _exactState, fold: false, data);
            Collect(_set._folded, _set._foldedPatterns, _foldedSeen, ref _foldedState, fold: true, data);
            _position += data.Length;

            // Earliest first, so when a string has both an ASCII and a wide form in the same
            // chunk the offset reported is the one that occurs first in the file.
            _found.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
            foreach (var (offset, reference) in _found)
            {
                Record(offset, reference, fresh);
            }
        }

        private void Collect(ByteAutomaton? automaton, PatternRef[] patterns, bool[] seen, ref int state, bool fold, ReadOnlySpan<byte> data)
        {
            if (automaton is null)
            {
                return;
            }
            _sink.Clear();
            automaton.Scan(data, ref state, fold, seen, _sink);
            foreach (var (pattern, end) in _sink)
            {
                _unseen--;
                var reference = patterns[pattern];
                _found.Add((_position + end - reference.String.Pattern.Length + 1, reference));
            }
        }

        private void Record(long offset, PatternRef reference, List<YaraLiteMatch> fresh)
        {
            var state = _rules[reference.RuleIndex];
            var s = reference.String;
            if (state is null || !state.Hit.Add(s.Identifier))
            {
                return;   // the other form (ascii / wide) of this string already reported
            }

            var match = new YaraLiteMatch(
                RuleName: _set.Rules[reference.RuleIndex].Name,
                FilePath: _filePath,
                Offset: offset,
                MatchedString: Describe(s),
                Identifier: s.Identifier);

            if (state.Satisfied)
            {
                fresh.Add(match);
                return;
            }

            // Held back until the condition holds. Every supported operator is monotone (a
            // string once seen stays seen, and there is no `not`), so the first time the
            // condition is true it is true for good and the held matches can be released.
            state.Pending.Add(match);
            if (state.Condition.Evaluate(state.Hit))
            {
                state.Satisfied = true;
                fresh.AddRange(state.Pending);
                state.Pending.Clear();
            }
        }

        private static string Describe(YaraLiteString s) =>
            s.IsHex ? "0x" + Convert.ToHexString(s.Pattern)
            : s.Text ?? Encoding.Latin1.GetString(s.Pattern);
    }

    private sealed class RuleState(YaraCondition condition)
    {
        public YaraCondition Condition { get; } = condition;
        public HashSet<string> Hit { get; } = new(StringComparer.Ordinal);
        public List<YaraLiteMatch> Pending { get; } = [];
        public bool Satisfied { get; set; }
    }
}

/// <summary>
/// Aho-Corasick over bytes. The caller owns the state, so it can be carried across reads and a
/// pattern split over two reads is matched exactly as if the input were one span.
/// </summary>
internal sealed class ByteAutomaton
{
    private const short None = -1;

    // Transitions. Almost every node of a trie built from distinct strings has exactly one
    // child, so the first child lives in two flat arrays and only branching nodes pay for a
    // dictionary. For an IOC list of thousands of indicators that is the difference between
    // a few megabytes and a few hundred.
    private readonly int[] _root = new int[256];
    private readonly short[] _firstByte;
    private readonly int[] _firstChild;
    private readonly Dictionary<byte, int>?[] _more;
    private readonly int[] _fail;
    private readonly int[] _outLink;
    private readonly int[]?[] _outputs;

    /// <summary>Builds the automaton; pattern <c>i</c> of <paramref name="patterns"/> is reported as id <c>i</c>.</summary>
    public ByteAutomaton(IReadOnlyList<byte[]> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var firstByte = new List<short> { None };
        var firstChild = new List<int> { 0 };
        var more = new List<Dictionary<byte, int>?> { null };
        var outputs = new List<List<int>?> { null };
        for (int id = 0; id < patterns.Count; id++)
        {
            var pattern = patterns[id];
            if (pattern.Length == 0)
            {
                throw new ArgumentException("Patterns must not be empty.", nameof(patterns));
            }
            var node = 0;
            foreach (var b in pattern)
            {
                int next;
                if (firstByte[node] == b)
                {
                    next = firstChild[node];
                }
                else if (more[node] is { } existing && existing.TryGetValue(b, out var found))
                {
                    next = found;
                }
                else
                {
                    next = firstByte.Count;
                    firstByte.Add(None);
                    firstChild.Add(0);
                    more.Add(null);
                    outputs.Add(null);
                    if (firstByte[node] == None)
                    {
                        firstByte[node] = b;
                        firstChild[node] = next;
                    }
                    else
                    {
                        (more[node] ??= [])[b] = next;
                    }
                }
                node = next;
            }
            (outputs[node] ??= []).Add(id);
        }

        var count = firstByte.Count;
        _firstByte = firstByte.ToArray();
        _firstChild = firstChild.ToArray();
        _more = more.ToArray();
        _outputs = outputs.Select(o => o?.ToArray()).ToArray();
        _fail = new int[count];
        _outLink = new int[count];

        // Breadth-first: a node's failure link points at a strictly shallower node, which has
        // therefore already been resolved by the time it is needed.
        var queue = new Queue<int>();
        foreach (var (b, child) in Children(0))
        {
            _root[b] = child;
            queue.Enqueue(child);
        }
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            foreach (var (b, v) in Children(u))
            {
                // The longest proper suffix of v's string that is also in the trie: follow u's
                // failure chain until a node has a transition on b. (u is never the root here;
                // the root's children were given failure link 0 above.)
                var f = _fail[u];
                int target;
                while (true)
                {
                    if (f == 0)
                    {
                        target = _root[b];
                        break;
                    }
                    var step = Next(f, b);
                    if (step != 0)
                    {
                        target = step;
                        break;
                    }
                    f = _fail[f];
                }
                _fail[v] = target;
                _outLink[v] = _outputs[target] is not null ? target : _outLink[target];
                queue.Enqueue(v);
            }
        }
    }

    private IEnumerable<(byte Byte, int Child)> Children(int node)
    {
        if (_firstByte[node] != None)
        {
            yield return ((byte)_firstByte[node], _firstChild[node]);
        }
        if (_more[node] is { } map)
        {
            foreach (var (b, child) in map)
            {
                yield return (b, child);
            }
        }
    }

    /// <summary>The child of a non-root <paramref name="node"/> on <paramref name="b"/>, or 0 when there is none (0 is the root, never a child).</summary>
    private int Next(int node, byte b)
    {
        if (_firstByte[node] == b)
        {
            return _firstChild[node];
        }
        return _more[node] is { } map && map.TryGetValue(b, out var next) ? next : 0;
    }

    /// <summary>Lower-cases ASCII letters; every other byte is unchanged.</summary>
    public static byte[] FoldAscii(byte[] bytes)
    {
        var folded = new byte[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            folded[i] = Fold(bytes[i]);
        }
        return folded;
    }

    private static byte Fold(byte b) => (uint)(b - 'A') <= 'Z' - 'A' ? (byte)(b | 0x20) : b;

    /// <summary>
    /// Advances <paramref name="state"/> over <paramref name="data"/>. The first time a pattern
    /// ends, its id and the index of its last byte within <paramref name="data"/> are appended
    /// to <paramref name="sink"/> and it is marked in <paramref name="seen"/>; later
    /// occurrences are not reported. With <paramref name="foldAscii"/> the input is ASCII
    /// lower-cased on the way in (the patterns must have been folded with <see cref="FoldAscii"/>).
    /// </summary>
    public void Scan(ReadOnlySpan<byte> data, ref int state, bool foldAscii, bool[] seen, List<(int Pattern, int End)> sink)
    {
        var s = state;
        for (int i = 0; i < data.Length; i++)
        {
            var b = foldAscii ? Fold(data[i]) : data[i];
            while (true)
            {
                if (s == 0)
                {
                    s = _root[b];
                    break;
                }
                var next = Next(s, b);
                if (next != 0)
                {
                    s = next;
                    break;
                }
                s = _fail[s];
            }

            for (var t = _outputs[s] is not null ? s : _outLink[s]; t != 0; t = _outLink[t])
            {
                foreach (var id in _outputs[t]!)
                {
                    if (!seen[id])
                    {
                        seen[id] = true;
                        sink.Add((id, i));
                    }
                }
            }
        }
        state = s;
    }
}

/// <summary>
/// A parsed condition over string identifiers: <c>$a</c>, <c>any|all|N of them</c>,
/// <c>N of ($a, $b*)</c>, <c>and</c>, <c>or</c>, parentheses. Anything else fails to parse with
/// a reason, and the rule is reported as skipped rather than guessed at.
/// </summary>
internal abstract class YaraCondition
{
    /// <summary>True when the condition holds given the set of string identifiers seen so far.</summary>
    public abstract bool Evaluate(IReadOnlySet<string> hit);

    public static bool TryParse(string? text, IReadOnlyCollection<string> identifiers, out YaraCondition? condition, out string? error)
    {
        condition = null;
        error = null;
        try
        {
            var parser = new Parser(Tokenize(text ?? ""), identifiers);
            condition = parser.ParseAll();
            return true;
        }
        catch (FormatException ex)
        {
            error = "condition not supported by YARA-lite: " + ex.Message;
            return false;
        }
    }

    private enum TokenKind { Word, Identifier, Number, Open, Close, Comma, End }

    private readonly record struct Token(TokenKind Kind, string Text);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '(')
            {
                tokens.Add(new Token(TokenKind.Open, "("));
                i++;
            }
            else if (c == ')')
            {
                tokens.Add(new Token(TokenKind.Close, ")"));
                i++;
            }
            else if (c == ',')
            {
                tokens.Add(new Token(TokenKind.Comma, ","));
                i++;
            }
            else if (c == '$')
            {
                var start = i++;
                while (i < text.Length && IsWordChar(text[i]))
                {
                    i++;
                }
                if (i < text.Length && text[i] == '*')
                {
                    i++;
                }
                tokens.Add(new Token(TokenKind.Identifier, text[start..i]));
            }
            else if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }
                if (i < text.Length && (IsWordChar(text[i]) || text[i] == '%'))
                {
                    throw new FormatException($"'{ReadWord(text, start)}' (only plain counts are supported)");
                }
                tokens.Add(new Token(TokenKind.Number, text[start..i]));
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                var word = ReadWord(text, i);
                i += word.Length;
                tokens.Add(new Token(TokenKind.Word, word));
            }
            else
            {
                throw new FormatException($"'{c}' is outside the supported subset");
            }
        }
        tokens.Add(new Token(TokenKind.End, ""));
        return tokens;
    }

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static string ReadWord(string text, int start)
    {
        var i = start;
        while (i < text.Length && (IsWordChar(text[i]) || text[i] == '.' || text[i] == '%'))
        {
            i++;
        }
        return text[start..i];
    }

    private sealed class Parser(List<Token> tokens, IReadOnlyCollection<string> identifiers)
    {
        private int _pos;

        private Token Peek => tokens[_pos];

        private Token Next() => tokens[_pos++];

        public YaraCondition ParseAll()
        {
            var expression = ParseOr();
            if (Peek.Kind != TokenKind.End)
            {
                throw new FormatException($"unexpected '{Peek.Text}'");
            }
            return expression;
        }

        private YaraCondition ParseOr()
        {
            var left = ParseAnd();
            while (Peek is { Kind: TokenKind.Word, Text: "or" })
            {
                Next();
                left = new Or(left, ParseAnd());
            }
            return left;
        }

        private YaraCondition ParseAnd()
        {
            var left = ParsePrimary();
            while (Peek is { Kind: TokenKind.Word, Text: "and" })
            {
                Next();
                left = new And(left, ParsePrimary());
            }
            return left;
        }

        private YaraCondition ParsePrimary()
        {
            var token = Next();
            switch (token.Kind)
            {
                case TokenKind.Open:
                    var inner = ParseOr();
                    Expect(TokenKind.Close, "')'");
                    return inner;

                case TokenKind.Identifier:
                    if (token.Text.EndsWith('*'))
                    {
                        throw new FormatException($"'{token.Text}' can only be used inside 'of (…)'");
                    }
                    if (Peek.Kind == TokenKind.Word && Peek.Text is "at" or "in")
                    {
                        throw new FormatException($"'{Peek.Text}' is not supported");
                    }
                    return new Has(Resolve(token.Text).Single());

                case TokenKind.Number:
                    if (!int.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
                    {
                        throw new FormatException($"'{token.Text} of' (the count must be at least 1)");
                    }
                    return ParseOf(n);

                case TokenKind.Word when token.Text == "any":
                    return ParseOf(1);

                case TokenKind.Word when token.Text == "all":
                    return ParseOf(null);

                case TokenKind.Word:
                    throw new FormatException($"'{token.Text}' is not supported");

                case TokenKind.End:
                    throw new FormatException("the condition is empty or incomplete");

                default:
                    throw new FormatException($"unexpected '{token.Text}'");
            }
        }

        /// <summary><paramref name="count"/> is the number required, or null for "all".</summary>
        private Of ParseOf(int? count)
        {
            if (Peek is not { Kind: TokenKind.Word, Text: "of" })
            {
                throw new FormatException($"expected 'of' but found '{Peek.Text}'");
            }
            Next();

            var set = new List<string>();
            var token = Next();
            if (token is { Kind: TokenKind.Word, Text: "them" })
            {
                if (identifiers.Count == 0)
                {
                    throw new FormatException("'them' is used but the rule defines no strings");
                }
                set.AddRange(identifiers);
            }
            else if (token.Kind == TokenKind.Open)
            {
                while (true)
                {
                    var id = Next();
                    if (id.Kind != TokenKind.Identifier)
                    {
                        throw new FormatException($"expected a string identifier but found '{id.Text}'");
                    }
                    set.AddRange(Resolve(id.Text));
                    if (Peek.Kind == TokenKind.Comma)
                    {
                        Next();
                        continue;
                    }
                    Expect(TokenKind.Close, "')'");
                    break;
                }
            }
            else
            {
                throw new FormatException($"expected 'them' or a list of strings but found '{token.Text}'");
            }

            var distinct = set.Distinct(StringComparer.Ordinal).ToArray();
            return new Of(count ?? distinct.Length, distinct);
        }

        private List<string> Resolve(string identifier)
        {
            if (identifier.EndsWith('*'))
            {
                var prefix = identifier[..^1];
                var matches = identifiers.Where(i => i.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                return matches.Count > 0 ? matches : throw new FormatException($"no strings match '{identifier}'");
            }
            return identifiers.Contains(identifier, StringComparer.Ordinal)
                ? [identifier]
                : throw new FormatException($"'{identifier}' is not defined in the strings section");
        }

        private void Expect(TokenKind kind, string what)
        {
            if (Next().Kind != kind)
            {
                throw new FormatException($"expected {what}");
            }
        }
    }

    private sealed class Has(string identifier) : YaraCondition
    {
        public override bool Evaluate(IReadOnlySet<string> hit) => hit.Contains(identifier);
    }

    private sealed class Of(int required, string[] set) : YaraCondition
    {
        public override bool Evaluate(IReadOnlySet<string> hit)
        {
            var seen = 0;
            foreach (var id in set)
            {
                if (hit.Contains(id) && ++seen >= required)
                {
                    return true;
                }
            }
            return false;
        }
    }

    private sealed class And(YaraCondition left, YaraCondition right) : YaraCondition
    {
        public override bool Evaluate(IReadOnlySet<string> hit) => left.Evaluate(hit) && right.Evaluate(hit);
    }

    private sealed class Or(YaraCondition left, YaraCondition right) : YaraCondition
    {
        public override bool Evaluate(IReadOnlySet<string> hit) => left.Evaluate(hit) || right.Evaluate(hit);
    }
}

/// <summary>Parses a `.yar` source into a list of <see cref="YaraLiteRule"/>.</summary>
public static partial class YaraLiteParser
{
    [GeneratedRegex(@"(\w+)\s*=\s*""((?:[^""\\]|\\.)*)""", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex MetaLine();

    /// <summary>
    /// Every <c>rule</c> in the source comes back: either runnable, or with a
    /// <see cref="YaraLiteRule.SkipReason"/> saying why not. Nothing is dropped silently.
    /// </summary>
    public static IReadOnlyList<YaraLiteRule> Parse(string source)
    {
        var text = StripComments(source ?? "");
        var rules = new List<YaraLiteRule>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipQuoted(text, i);   // import "pe" / include "x.yar"
            }
            else if (IsWordStart(c))
            {
                var word = ReadWord(text, i);
                i += word.Length;
                if (word == "rule")
                {
                    rules.Add(ParseRuleAt(text, ref i));
                }
            }
            else
            {
                i++;
            }
        }
        return rules;
    }

    /// <summary>Parses one rule; <paramref name="i"/> is just past the <c>rule</c> keyword on entry and past the rule on exit.</summary>
    private static YaraLiteRule ParseRuleAt(string text, ref int i)
    {
        SkipWhitespace(text, ref i);
        var name = i < text.Length && IsWordStart(text[i]) ? ReadWord(text, i) : "";
        i += name.Length;
        if (name.Length == 0)
        {
            return new YaraLiteRule { Name = "(unnamed)", SkipReason = "malformed rule header: no rule name" };
        }

        SkipWhitespace(text, ref i);
        if (i < text.Length && text[i] == ':')
        {
            // Tags: `rule Name : tag1 tag2 {`. They do not affect matching.
            i++;
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || IsWordChar(text[i])))
            {
                i++;
            }
        }
        if (i >= text.Length || text[i] != '{')
        {
            return new YaraLiteRule { Name = name, SkipReason = "malformed rule header: expected '{' after the rule name" };
        }

        var bodyStart = ++i;
        var depth = 1;
        while (i < text.Length && depth > 0)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipQuoted(text, i);
            }
            else if (c == '/' && FollowsEquals(text, i))
            {
                i = SkipRegex(text, i);
            }
            else
            {
                depth += c == '{' ? 1 : c == '}' ? -1 : 0;
                i++;
            }
        }
        if (depth != 0)
        {
            return new YaraLiteRule { Name = name, SkipReason = "unterminated rule: no closing '}'" };
        }

        var body = text[bodyStart..(i - 1)];
        try
        {
            return ParseBody(name, body);
        }
        catch (Exception ex) when (ex is FormatException or RegexMatchTimeoutException)
        {
            return new YaraLiteRule { Name = name, SkipReason = "parse error: " + ex.Message };
        }
    }

    private static YaraLiteRule ParseBody(string name, string body)
    {
        var sections = FindSections(body);

        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sections.TryGetValue("meta", out var metaText))
        {
            foreach (Match m in MetaLine().Matches(metaText))
            {
                meta[m.Groups[1].Value] = Encoding.UTF8.GetString(Unescape(m.Groups[2].Value));
            }
        }

        string? skip = null;
        var strings = new List<YaraLiteString>();
        if (sections.TryGetValue("strings", out var stringsText))
        {
            try
            {
                ParseStrings(stringsText, strings, ref skip);
            }
            catch (FormatException ex)
            {
                skip = "could not parse the strings section: " + ex.Message;
            }
        }

        string condition;
        if (sections.TryGetValue("condition", out var conditionText) && !string.IsNullOrWhiteSpace(conditionText))
        {
            condition = string.Join(' ', conditionText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        else
        {
            condition = "";
            skip ??= "the rule has no condition";
        }

        if (skip is null)
        {
            var identifiers = strings.Select(s => s.Identifier).Distinct(StringComparer.Ordinal).ToList();
            if (!YaraCondition.TryParse(condition, identifiers, out _, out var error))
            {
                skip = error;
            }
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

    /// <summary>Splits a rule body into its <c>meta</c> / <c>strings</c> / <c>condition</c> sections.</summary>
    private static Dictionary<string, string> FindSections(string body)
    {
        var marks = new List<(string Name, int KeywordStart, int ContentStart)>();
        var i = 0;
        while (i < body.Length)
        {
            var c = body[i];
            if (c == '"')
            {
                i = SkipQuoted(body, i);
            }
            else if (c == '/' && FollowsEquals(body, i))
            {
                i = SkipRegex(body, i);
            }
            else if (IsWordStart(c) && (i == 0 || !IsWordChar(body[i - 1]) && body[i - 1] != '$'))
            {
                var word = ReadWord(body, i);
                var after = i + word.Length;
                if (word is "meta" or "strings" or "condition")
                {
                    var j = after;
                    SkipWhitespace(body, ref j);
                    if (j < body.Length && body[j] == ':')
                    {
                        marks.Add((word, i, j + 1));
                    }
                }
                i = after;
            }
            else
            {
                i++;
            }
        }

        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int m = 0; m < marks.Count; m++)
        {
            var end = m + 1 < marks.Count ? marks[m + 1].KeywordStart : body.Length;
            sections.TryAdd(marks[m].Name, body[marks[m].ContentStart..end]);
        }
        return sections;
    }

    private static void ParseStrings(string text, List<YaraLiteString> strings, ref string? skip)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var anonymous = 0;
        var i = 0;
        while (true)
        {
            SkipWhitespace(text, ref i);
            if (i >= text.Length)
            {
                return;
            }
            if (text[i] != '$')
            {
                throw new FormatException($"unexpected text near '{Excerpt(text, i)}'");
            }

            var idStart = i++;
            while (i < text.Length && IsWordChar(text[i]))
            {
                i++;
            }
            var identifier = text[idStart..i];
            if (identifier == "$")
            {
                // Anonymous strings are distinct strings; give each its own identity so
                // "all of them" still means all of them.
                identifier = "$anonymous_" + (++anonymous).ToString(CultureInfo.InvariantCulture);
            }
            if (!seen.Add(identifier))
            {
                throw new FormatException($"duplicate string identifier {identifier}");
            }

            SkipWhitespace(text, ref i);
            if (i >= text.Length || text[i] != '=')
            {
                throw new FormatException($"expected '=' after {identifier}");
            }
            i++;
            SkipWhitespace(text, ref i);
            if (i >= text.Length)
            {
                throw new FormatException($"{identifier} has no value");
            }

            byte[]? bytes = null;
            string? literal = null;
            var isHex = false;
            switch (text[i])
            {
                case '"':
                    var end = SkipQuoted(text, i);
                    if (end > text.Length || end - i < 2 || text[end - 1] != '"')
                    {
                        throw new FormatException($"{identifier} has an unterminated string");
                    }
                    literal = text[(i + 1)..(end - 1)];
                    bytes = Unescape(literal);
                    i = end;
                    break;

                case '{':
                    var close = text.IndexOf('}', i);
                    if (close < 0)
                    {
                        throw new FormatException($"{identifier} has an unterminated hex string");
                    }
                    bytes = ParseHex(text[(i + 1)..close], identifier, ref skip);
                    isHex = true;
                    i = close + 1;
                    break;

                case '/':
                    i = SkipRegex(text, i);
                    while (i < text.Length && char.IsAsciiLetter(text[i]))
                    {
                        i++;   // regex flags: /…/is
                    }
                    skip ??= $"regex strings (/…/) are not supported ({identifier})";
                    break;

                default:
                    throw new FormatException($"{identifier} has an unrecognised value near '{Excerpt(text, i)}'");
            }

            bool nocase = false, wide = false, ascii = false;
            while (true)
            {
                SkipWhitespace(text, ref i);
                if (i >= text.Length || text[i] == '$')
                {
                    break;
                }
                if (!char.IsAsciiLetter(text[i]))
                {
                    throw new FormatException($"unexpected text after {identifier} near '{Excerpt(text, i)}'");
                }
                var modifier = ReadWord(text, i);
                i += modifier.Length;
                if (i < text.Length && text[i] == '(')
                {
                    var paren = text.IndexOf(')', i);
                    i = paren < 0 ? text.Length : paren + 1;   // xor(0x01-0xff), base64("alphabet")
                }
                switch (modifier)
                {
                    case "nocase": nocase = true; break;
                    case "wide": wide = true; break;
                    case "ascii": ascii = true; break;
                    case "private": break;   // only affects reporting in YARA proper
                    case "fullword" or "xor" or "base64" or "base64wide":
                        skip ??= $"the '{modifier}' modifier is not supported ({identifier})";
                        break;
                    default:
                        throw new FormatException($"unknown modifier '{modifier}' on {identifier}");
                }
            }

            if (bytes is null)
            {
                continue;   // unsupported kind; the rule is already marked skipped
            }
            if (bytes.Length == 0)
            {
                throw new FormatException($"{identifier} is empty");
            }
            if (isHex)
            {
                if (nocase || wide || ascii)
                {
                    throw new FormatException($"text modifiers cannot be applied to the hex string {identifier}");
                }
                strings.Add(new YaraLiteString { Identifier = identifier, Pattern = bytes, IsHex = true });
                continue;
            }
            if (ascii || !wide)
            {
                strings.Add(new YaraLiteString { Identifier = identifier, Pattern = bytes, NoCase = nocase, Text = literal });
            }
            if (wide)
            {
                // YARA's `wide` interleaves a zero after each byte; it is not a UTF-16 transcode.
                var widened = new byte[bytes.Length * 2];
                for (int b = 0; b < bytes.Length; b++)
                {
                    widened[b * 2] = bytes[b];
                }
                strings.Add(new YaraLiteString { Identifier = identifier, Pattern = widened, NoCase = nocase, Text = literal });
            }
        }
    }

    /// <summary>Plain hex bytes only. Returns null (and sets the skip reason) for wildcards, jumps and alternatives.</summary>
    private static byte[]? ParseHex(string content, string identifier, ref string? skip)
    {
        var digits = new StringBuilder(content.Length);
        foreach (var c in content)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }
            if (char.IsAsciiHexDigit(c))
            {
                digits.Append(c);
            }
            else if (c is '?' or '[' or ']' or '(' or ')' or '|' or '-' or '~')
            {
                skip ??= $"hex wildcards, jumps and alternatives are not supported ({identifier})";
                return null;
            }
            else
            {
                throw new FormatException($"'{c}' is not valid in the hex string {identifier}");
            }
        }
        if (digits.Length == 0 || digits.Length % 2 != 0)
        {
            throw new FormatException($"the hex string {identifier} does not contain whole bytes");
        }
        return Convert.FromHexString(digits.ToString());
    }

    /// <summary>Decodes a YARA text literal to the bytes it stands for (<c>\" \\ \n \r \t \xHH</c>; other text as UTF-8).</summary>
    private static byte[] Unescape(string literal)
    {
        var bytes = new List<byte>(literal.Length);
        var run = new StringBuilder();
        void FlushRun()
        {
            if (run.Length > 0)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(run.ToString()));
                run.Clear();
            }
        }

        for (int i = 0; i < literal.Length; i++)
        {
            var c = literal[i];
            if (c != '\\')
            {
                run.Append(c);
                continue;
            }
            if (++i >= literal.Length)
            {
                throw new FormatException("a string ends with a lone backslash");
            }
            FlushRun();
            switch (literal[i])
            {
                case '"': bytes.Add((byte)'"'); break;
                case '\\': bytes.Add((byte)'\\'); break;
                case 'n': bytes.Add((byte)'\n'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'x' when i + 2 < literal.Length
                              && char.IsAsciiHexDigit(literal[i + 1]) && char.IsAsciiHexDigit(literal[i + 2]):
                    bytes.Add(Convert.ToByte(literal.Substring(i + 1, 2), 16));
                    i += 2;
                    break;
                default:
                    throw new FormatException($"unsupported escape '\\{literal[i]}'");
            }
        }
        FlushRun();
        return bytes.ToArray();
    }

    // ---- lexical helpers ----

    private static bool IsWordStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static string ReadWord(string text, int start)
    {
        var i = start;
        while (i < text.Length && IsWordChar(text[i]))
        {
            i++;
        }
        return text[start..i];
    }

    private static void SkipWhitespace(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
    }

    /// <summary>Returns the index just past the closing quote of the literal starting at <paramref name="start"/> (or the end of the line / text if it never closes).</summary>
    private static int SkipQuoted(string text, int start)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\')
            {
                i += 2;
            }
            else if (c == '"')
            {
                return i + 1;
            }
            else if (c == '\n')
            {
                return i;   // YARA literals cannot span lines; do not swallow the rest of the file
            }
            else
            {
                i++;
            }
        }
        return text.Length;
    }

    /// <summary>Returns the index just past the closing slash of the regex literal starting at <paramref name="start"/>.</summary>
    private static int SkipRegex(string text, int start)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\')
            {
                i += 2;
            }
            else if (c == '/')
            {
                return i + 1;
            }
            else if (c == '\n')
            {
                return i;
            }
            else
            {
                i++;
            }
        }
        return text.Length;
    }

    /// <summary>True when the nearest non-blank character before <paramref name="index"/> is '=' — i.e. a '/' here opens a regex string.</summary>
    private static bool FollowsEquals(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            i--;
        }
        return i >= 0 && text[i] == '=';
    }

    private static string Excerpt(string text, int index) =>
        text.Substring(index, Math.Min(24, text.Length - index)).ReplaceLineEndings(" ").Trim();

    /// <summary>
    /// Removes <c>// …</c> and <c>/* … */</c> comments, leaving string and regex literals
    /// untouched — <c>"http://evil.example/"</c> is a string, not a string followed by a comment.
    /// </summary>
    internal static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '"')
            {
                var end = Math.Min(SkipQuoted(source, i), source.Length);
                sb.Append(source, i, end - i);
                i = end;
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? source.Length : close + 2;
                sb.Append(' ');
            }
            else if (c == '/' && FollowsEquals(source, i))
            {
                var end = Math.Min(SkipRegex(source, i), source.Length);
                sb.Append(source, i, end - i);
                i = end;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }
}
