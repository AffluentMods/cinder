namespace Cinder.App.ViewModels.Tools;

/// <summary>
/// Reads the header block of every message in an MBOX file. One reader for every tool that
/// needs who-wrote-to-whom from an MBOX (Email, Comm graph), so they cannot drift apart again.
///
/// <para>Two rules matter and are easy to get wrong:</para>
/// <list type="bullet">
/// <item>RFC 5322 folding. A header line that starts with a space or tab continues the previous
/// header. Long To / Cc lists are almost always folded, so ignoring this silently drops
/// recipients.</item>
/// <item>The separator. A message starts at a line beginning <c>From </c> that is the first line
/// of the file or follows a blank line. A body line that merely begins with "From " does not
/// start a new message.</item>
/// </list>
/// </summary>
internal static class MboxHeaderReader
{
    /// <summary>
    /// Yields one case-insensitive header map per message, in file order. A header that occurs
    /// more than once keeps its first value, except the address lists (To, Cc, Bcc), which are
    /// joined with commas. Bodies are skipped without being stored.
    /// </summary>
    public static IEnumerable<IReadOnlyDictionary<string, string>> ReadMessages(TextReader reader, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        Dictionary<string, string>? headers = null;
        string? lastHeader = null;
        var inHeaders = false;
        var previousBlank = true;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();

            if (previousBlank && line.StartsWith("From ", StringComparison.Ordinal))
            {
                if (headers is { Count: > 0 })
                {
                    yield return headers;
                }
                headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                lastHeader = null;
                inHeaders = true;
                previousBlank = false;
                continue;
            }

            previousBlank = line.Length == 0;
            if (!inHeaders || headers is null)
            {
                continue;
            }
            if (line.Length == 0)
            {
                // Blank line: the header block is over, the body follows.
                inHeaders = false;
                continue;
            }
            if (line[0] is ' ' or '\t')
            {
                if (lastHeader is not null)
                {
                    headers[lastHeader] = (headers[lastHeader] + " " + line.Trim()).Trim();
                }
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                lastHeader = null;
                continue;
            }
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (!headers.TryGetValue(name, out var existing))
            {
                headers[name] = value;
                lastHeader = name;
            }
            else if (IsAddressList(name))
            {
                headers[name] = existing.Length == 0 ? value : existing + ", " + value;
                lastHeader = name;
            }
            else
            {
                // A repeated single-valued header (Received, mostly): keep the first, and do not
                // let its continuation lines attach to it.
                lastHeader = null;
            }
        }

        if (headers is { Count: > 0 })
        {
            yield return headers;
        }
    }

    /// <summary>The value of <paramref name="name"/>, or an empty string when the message lacks it.</summary>
    public static string Get(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var v) ? v : "";

    private static bool IsAddressList(string name) =>
        name.Equals("To", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Cc", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Bcc", StringComparison.OrdinalIgnoreCase);
}
