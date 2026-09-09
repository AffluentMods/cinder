using System.Text.RegularExpressions;

namespace Cinder.Search;

/// <summary>
/// Attaches MITRE ATT&amp;CK technique identifiers to timeline events whose source and content
/// make the mapping defensible. The point is triage, not attribution: a filter on
/// <c>T1543.003</c> surfaces every service-install event across every ingested log in one
/// query, which is the question an incident responder actually asks.
///
/// <para>Only high-confidence mappings are included. A Windows logon (4624) is <c>T1078</c>
/// because the technique <em>is</em> the use of a valid account; a generic process-creation
/// event is not tagged, because on its own it does not indicate any technique. Tactic
/// identifiers (<c>TA0002</c> Execution) are used where the evidence proves the tactic but not
/// a specific technique — Prefetch proves something ran, not how it came to run.</para>
/// </summary>
public static partial class MitreTagger
{
    [GeneratedRegex(@"EventID=(\d+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex EventIdPattern();

    [GeneratedRegex(@"^\[([^\]]+)\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex ChannelPattern();

    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["TA0002"] = "Execution",
        ["T1003.001"] = "OS Credential Dumping: LSASS Memory",
        ["T1021.001"] = "Remote Services: Remote Desktop Protocol",
        ["T1021.002"] = "Remote Services: SMB/Windows Admin Shares",
        ["T1053.005"] = "Scheduled Task/Job: Scheduled Task",
        ["T1055"] = "Process Injection",
        ["T1059.001"] = "Command and Scripting Interpreter: PowerShell",
        ["T1070.001"] = "Indicator Removal: Clear Windows Event Logs",
        ["T1070.004"] = "Indicator Removal: File Deletion",
        ["T1078"] = "Valid Accounts",
        ["T1098"] = "Account Manipulation",
        ["T1110"] = "Brute Force",
        ["T1112"] = "Modify Registry",
        ["T1136.001"] = "Create Account: Local Account",
        ["T1222"] = "File and Directory Permissions Modification",
        ["T1543.003"] = "Create or Modify System Process: Windows Service",
        ["T1562.002"] = "Impair Defenses: Disable Windows Event Logging",
    };

    /// <summary>Human-readable name for a technique or tactic id, or the id itself if unknown.</summary>
    public static string Describe(string id) => Names.TryGetValue(id, out var n) ? $"{id} {n}" : id;

    /// <summary>All ids this tagger can emit, for building a filter picker.</summary>
    public static IReadOnlyCollection<string> KnownIds => Names.Keys.ToArray();

    /// <summary>
    /// Returns the technique/tactic ids that apply to an event, or an empty list. Never throws:
    /// a tag is a hint for triage and must not stop an ingest.
    /// </summary>
    public static IReadOnlyList<string> Tag(string source, string summary)
    {
        if (string.IsNullOrEmpty(source))
        {
            return [];
        }

        var tags = new List<string>(2);
        var lowerSource = source.ToLowerInvariant();

        if (lowerSource.StartsWith("evtx", StringComparison.Ordinal))
        {
            TagWindowsEvent(summary ?? "", tags);
        }
        else if (lowerSource is "prefetch" or "registry.userassist")
        {
            tags.Add("TA0002");
        }
        else if (lowerSource == "recyclebin")
        {
            tags.Add("T1070.004");
        }

        return tags;
    }

    private static void TagWindowsEvent(string summary, List<string> tags)
    {
        int eventId;
        try
        {
            var m = EventIdPattern().Match(summary);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out eventId))
            {
                return;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return;
        }

        var channel = "";
        try
        {
            var c = ChannelPattern().Match(summary);
            if (c.Success)
            {
                channel = c.Groups[1].Value;
            }
        }
        catch (RegexMatchTimeoutException) { }

        var isSysmon = channel.Contains("Sysmon", StringComparison.OrdinalIgnoreCase);
        var isPowerShell = channel.Contains("PowerShell", StringComparison.OrdinalIgnoreCase);

        if (isSysmon)
        {
            switch (eventId)
            {
                case 8: tags.Add("T1055"); break;
                case 12 or 13 or 14: tags.Add("T1112"); break;
                case 10 when summary.Contains("lsass", StringComparison.OrdinalIgnoreCase): tags.Add("T1003.001"); break;
            }
            return;
        }

        if (isPowerShell)
        {
            if (eventId is 4103 or 4104)
            {
                tags.Add("T1059.001");
            }
            return;
        }

        switch (eventId)
        {
            case 4624 or 4648 or 4672 or 4776 or 4771:
                tags.Add("T1078");
                break;
            case 4625:
                tags.Add("T1078");
                tags.Add("T1110");
                break;
            case 4740:
                tags.Add("T1110");
                break;
            case 4698 or 4699 or 4700 or 4701 or 4702:
                tags.Add("T1053.005");
                break;
            case 7045 or 4697:
                tags.Add("T1543.003");
                break;
            case 4720 or 4722 or 4726:
                tags.Add("T1136.001");
                break;
            case 4728 or 4732 or 4756:
                tags.Add("T1098");
                break;
            case 1102 or 104:
                tags.Add("T1070.001");
                break;
            case 4719:
                tags.Add("T1562.002");
                break;
            case 4657:
                tags.Add("T1112");
                break;
            case 4670:
                tags.Add("T1222");
                break;
            case 5140 or 5145:
                tags.Add("T1021.002");
                break;
            case 4778 or 4779 or 1149:
                tags.Add("T1021.001");
                break;
        }
    }
}
