using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Cinder.Reports;

/// <summary>
/// The boundary between evidence text and report markup.
///
/// <para>A report mixes two kinds of string: Markdown the examiner wrote (section bodies, exhibit
/// descriptions) and plain text that came out of evidence (a file name, a page title, a registry
/// value). The second kind is hostile by definition. <see cref="EscapeInline"/> and
/// <see cref="CodeSpan"/> turn a plain-text value into Markdown that renders as exactly that
/// text, so it cannot open a link, forge a table cell or start a heading.
/// <see cref="ToSafeHtml"/> renders Markdown with raw HTML switched off and every link checked,
/// so that even Markdown that slipped through unescaped cannot run script or pull in content
/// from the network or the local disk.</para>
/// </summary>
public static class ReportMarkdown
{
    /// <summary>
    /// Content-Security-Policy for an exported report: nothing loads and nothing runs. Inline
    /// style is allowed because the report carries its own stylesheet; images only as
    /// <c>data:</c> URIs, which are part of the file.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'";

    // Every extension UseAdvancedExtensions() enables except the three that create markup the
    // author did not write out: GenericAttributes (arbitrary HTML attributes via {…}),
    // MediaLinks (turns a link into an <iframe>/<video>) and Diagrams. Raw HTML is disabled.
    private static readonly MarkdownPipeline SafePipeline = new MarkdownPipelineBuilder()
        .UseAbbreviations()
        .UseAutoIdentifiers()
        .UseCitations()
        .UseCustomContainers()
        .UseDefinitionLists()
        .UseEmphasisExtras()
        .UseFigures()
        .UseFooters()
        .UseFootnotes()
        .UseGridTables()
        .UseMathematics()
        .UsePipeTables()
        .UseListExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    /// <summary>
    /// Escapes a plain-text value so it renders verbatim inside a Markdown line — a heading, a
    /// list item or a table cell. Line breaks become spaces (a value cannot start a new block),
    /// other control characters are dropped, and every character that could begin inline markup
    /// or split a table cell is backslash-escaped.
    /// </summary>
    public static string EscapeInline(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var flat = Flatten(text);
        var sb = new StringBuilder(flat.Length + 8);
        for (int i = 0; i < flat.Length; i++)
        {
            var c = flat[i];
            switch (c)
            {
                case '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '~' or '^'
                    or '$' or '&' or '#' or '!' or '{' or '}':
                    sb.Append('\\').Append(c);
                    break;

                // "++inserted++", "==marked==" and ""cited"" only mean something doubled, so a
                // lone '+', '=' or '"' is left readable.
                case '+' or '=' or '"':
                    var doubled = (i + 1 < flat.Length && flat[i + 1] == c) || (i > 0 && flat[i - 1] == c);
                    if (doubled)
                    {
                        sb.Append('\\');
                    }
                    sb.Append(c);
                    break;

                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Wraps a plain-text value in a Markdown code span whose fence is longer than any backtick
    /// run inside it, so the value cannot close the span early. Not for table cells — a pipe
    /// inside a code span still splits the row; use <see cref="EscapeInline"/> there.
    /// </summary>
    public static string CodeSpan(string? text)
    {
        var flat = Flatten(text ?? "");
        if (flat.Length == 0)
        {
            return "";
        }

        int longest = 0, run = 0;
        foreach (var c in flat)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', longest + 1);

        // CommonMark strips one leading and one trailing space when both are present, which is
        // what lets a value that begins or ends with a backtick sit next to the fence.
        var pad = flat[0] == '`' || flat[^1] == '`' || flat[0] == ' ' || flat[^1] == ' ';
        return pad ? $"{fence} {flat} {fence}" : $"{fence}{flat}{fence}";
    }

    /// <summary>
    /// Renders Markdown to an HTML fragment that is inert: raw HTML in the source comes out as
    /// visible text, links may only be http(s), mailto, in-page or relative, and images may only
    /// be embedded <c>data:</c> bitmaps. Anything else is neutralised in the output rather than
    /// silently dropped, so the reader can see something was there.
    /// </summary>
    public static string ToSafeHtml(string markdown)
    {
        var document = Markdown.Parse(markdown ?? "", SafePipeline);

        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? "";
            if (link.IsImage)
            {
                if (!IsEmbeddedBitmap(url))
                {
                    link.ReplaceBy(new LiteralInline($"[image not embedded: {Shorten(url)}]"));
                }
                continue;
            }
            if (!IsAllowedLink(url))
            {
                link.GetDynamicUrl = null;
                link.Url = "#";
                link.Title = "link removed: " + Shorten(url);
            }
        }

        foreach (var auto in document.Descendants<AutolinkInline>().ToList())
        {
            var target = auto.IsEmail ? "mailto:" + auto.Url : auto.Url;
            if (!IsAllowedLink(target))
            {
                auto.ReplaceBy(new LiteralInline(auto.Url));
            }
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        SafePipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>
    /// True when a link target is one a report may carry: an in-page fragment, a relative
    /// reference, or an absolute http, https or mailto URL. <c>javascript:</c>, <c>data:</c>,
    /// <c>file:</c>, drive paths and UNC/protocol-relative targets are all refused.
    /// </summary>
    public static bool IsAllowedLink(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return true;
        }

        // Browsers ignore whitespace and control characters inside a URL, so "java\tscript:"
        // is a scheme. Judge the string the browser would.
        var compact = new string([.. url.Where(c => c > ' ' && c != '\u007F')]);
        if (compact.Length == 0 || compact[0] == '#')
        {
            return true;
        }
        if (compact.StartsWith("//", StringComparison.Ordinal) ||
            compact.StartsWith('\\') ||
            compact.StartsWith("/\\", StringComparison.Ordinal))
        {
            return false;
        }

        var scheme = SchemeOf(compact);
        if (scheme is null)
        {
            return true; // relative reference
        }
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEmbeddedBitmap(string url)
    {
        foreach (var type in (ReadOnlySpan<string>)["png", "jpeg", "jpg", "gif", "webp", "bmp"])
        {
            if (url.StartsWith($"data:image/{type};base64,", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The URI scheme if the string starts with one (RFC 3986 §3.1), else null.</summary>
    private static string? SchemeOf(string s)
    {
        if (!char.IsAsciiLetter(s[0]))
        {
            return null;
        }
        for (int i = 1; i < s.Length; i++)
        {
            var c = s[i];
            if (c == ':')
            {
                return s[..i];
            }
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
            {
                return null;
            }
        }
        return null;
    }

    private static string Shorten(string s) => s.Length <= 80 ? s : s[..77] + "...";

    /// <summary>One line, no control characters: CR/LF/TAB become a space, the rest are dropped.</summary>
    private static string Flatten(string text)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var isBreak = c is '\r' or '\n' or '\t' or (char)0x85 or (char)0x2028 or (char)0x2029;
            if (!isBreak && !char.IsControl(c))
            {
                sb?.Append(c);
                continue;
            }
            sb ??= new StringBuilder(text, 0, i, text.Length);
            if (isBreak)
            {
                if (c == '\n' && i > 0 && text[i - 1] == '\r')
                {
                    continue; // CRLF is one break
                }
                sb.Append(' ');
            }
        }
        return sb?.ToString() ?? text;
    }
}
