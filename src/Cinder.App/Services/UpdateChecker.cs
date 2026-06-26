// =====================================================================================
// UpdateChecker — non-intrusive "is there a newer release?" probe.
//
// On launch (one-time), hits GitHub's /releases/latest endpoint, compares semver against
// the running assembly version, and surfaces the result via UpdateAvailable. The
// dashboard binds to it to show a "Cinder 0.2.2 is available" banner with a link to the
// GitHub release page. No auto-download: security and transparency. Users click through.
//
// Privacy: this IS the project's one "phone home" — a public GitHub API call with no
// user identifiers. Telemetry section of the README documents it; Settings exposes an
// opt-out toggle that short-circuits CheckAsync.
// =====================================================================================

using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Cinder.App.Services;

public sealed class UpdateChecker
{
    private const string LatestUrl = "https://api.github.com/repos/AffluentMods/cinder/releases/latest";

    public sealed record UpdateInfo(
        string LatestVersion,
        string CurrentVersion,
        bool IsNewer,
        string ReleaseHtmlUrl,
        string? Notes);

    /// <summary>Returns null if check failed, opt-out, or no newer release.</summary>
    public async Task<UpdateInfo?> CheckAsync(bool enabled, CancellationToken ct = default)
    {
        if (!enabled) return null;
        try
        {
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8),
            };
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("Cinder", CurrentVersion()));
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var rsp = await http.GetAsync(LatestUrl, ct);
            if (!rsp.IsSuccessStatusCode) return null;

            var body = await rsp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var html = root.GetProperty("html_url").GetString() ?? "";
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;

            var latest = tag.TrimStart('v');
            var current = CurrentVersion();
            var isNewer = CompareSemver(latest, current) > 0;
            return new UpdateInfo(latest, current, isNewer, html, notes);
        }
        catch
        {
            return null;
        }
    }

    public static string CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v is null) return "0.0.0";
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>Returns positive if a > b, negative if a &lt; b, 0 if equal. Three-part semver only.</summary>
    public static int CompareSemver(string a, string b)
    {
        var pa = a.Split('.', 3);
        var pb = b.Split('.', 3);
        for (int i = 0; i < 3; i++)
        {
            int ai = i < pa.Length && int.TryParse(pa[i].Split('-')[0], out var x) ? x : 0;
            int bi = i < pb.Length && int.TryParse(pb[i].Split('-')[0], out var y) ? y : 0;
            if (ai != bi) return ai.CompareTo(bi);
        }
        return 0;
    }
}
