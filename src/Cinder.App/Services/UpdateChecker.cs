// =====================================================================================
// UpdateChecker — "is there a newer release?", asked only when the examiner asks.
//
// Cinder runs on evidence workstations, many of them deliberately offline, and it does not
// call out on its own: not at launch, not on a timer, not in the background. The single
// network request in this file happens when someone runs the "Check for updates" command.
//
// That request is one GET to GitHub's public releases API. It carries a generic
// User-Agent (GitHub rejects requests without one) and an Accept header, and nothing
// else: no version, no machine or user name, no cookies, no credentials. The answer is
// reported as text. Nothing is downloaded and no browser is opened.
// =====================================================================================

using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Cinder.App.Services;

public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
}

/// <summary>
/// The outcome of one check. <see cref="Message"/> is the whole thing in a sentence, ready for
/// the status bar; for <see cref="UpdateStatus.UpdateAvailable"/> it includes the release page
/// address, which is also in <see cref="ReleaseUrl"/>.
/// </summary>
public sealed record UpdateCheckResult(
    UpdateStatus Status,
    string Message,
    string CurrentVersion,
    string? LatestVersion = null,
    string? ReleaseUrl = null);

public sealed class UpdateChecker
{
    /// <summary>Where releases are published. Shown to the examiner; never opened by Cinder.</summary>
    public const string ReleasesPage = "https://github.com/AffluentMods/cinder/releases";

    internal const string LatestUrl = "https://api.github.com/repos/AffluentMods/cinder/releases/latest";

    /// <summary>The only identification sent: a product name with no version, as GitHub's API requires some User-Agent.</summary>
    internal const string UserAgent = "Cinder";

    /// <summary>A release description can be long; the two fields read here sit in the first few kilobytes.</summary>
    private const int MaxResponseBytes = 1 << 20;

    private readonly HttpMessageHandler? _handler;
    private readonly string _currentVersion;
    private readonly TimeSpan _timeout;

    /// <param name="handler">Transport override for tests. The caller keeps ownership.</param>
    /// <param name="currentVersion">Version to compare against; the running assembly's by default.</param>
    /// <param name="timeout">How long to wait for GitHub; ten seconds by default.</param>
    public UpdateChecker(HttpMessageHandler? handler = null, string? currentVersion = null, TimeSpan? timeout = null)
    {
        _handler = handler;
        _currentVersion = currentVersion ?? CurrentVersion();
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Asks GitHub for the latest published release and compares it with this build. Never
    /// throws for a failed check — offline, refused, rate-limited, timed out and unintelligible
    /// answers all come back as <see cref="UpdateStatus.Failed"/> with a plain reason. Only a
    /// cancellation requested through <paramref name="ct"/> propagates.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var ownedHandler = _handler is null
                ? new SocketsHttpHandler
                {
                    // Nothing persistent, nothing ambient: no cookie jar, no Windows credentials.
                    UseCookies = false,
                    Credentials = null,
                    MaxAutomaticRedirections = 3,
                    ConnectTimeout = _timeout,
                }
                : null;
            using var http = new HttpClient(_handler ?? ownedHandler!, disposeHandler: false) { Timeout = _timeout };

            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Failed(response.StatusCode switch
                {
                    HttpStatusCode.NotFound => "no published release was found.",
                    HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
                        "GitHub is limiting requests from this network address. Try again later.",
                    _ => $"GitHub answered with an error ({(int)response.StatusCode}).",
                });
            }

            var body = await ReadBoundedAsync(response.Content, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
                CleanVersion(tag.GetString()) is not { } latest)
            {
                return Failed("the answer from GitHub was not in the expected form.");
            }

            var comparison = CompareSemver(latest, _currentVersion);
            if (comparison <= 0)
            {
                return new UpdateCheckResult(
                    UpdateStatus.UpToDate,
                    comparison == 0
                        ? $"Cinder {_currentVersion} is up to date."
                        : $"Cinder {_currentVersion} is up to date (it is newer than the latest release, {latest}).",
                    _currentVersion,
                    latest);
            }

            var page = root.TryGetProperty("html_url", out var html) && html.ValueKind == JsonValueKind.String
                ? TrustedReleaseUrl(html.GetString())
                : ReleasesPage;
            return new UpdateCheckResult(
                UpdateStatus.UpdateAvailable,
                $"Cinder {latest} is available (this is {_currentVersion}). Release page: {page}",
                _currentVersion,
                latest,
                page);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient reports its own timeout as a cancellation.
            var seconds = Math.Max(1, (int)Math.Ceiling(_timeout.TotalSeconds));
            return Failed($"GitHub did not answer within {seconds} second{(seconds == 1 ? "" : "s")}.");
        }
        catch (HttpRequestException)
        {
            return Failed("GitHub could not be reached. This workstation may be offline, which is fine.");
        }
        catch (JsonException)
        {
            return Failed("the answer from GitHub was not in the expected form.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            return Failed(CrashHandler.Describe(ex));
        }
    }

    private UpdateCheckResult Failed(string reason) =>
        new(UpdateStatus.Failed, "Could not check for updates: " + reason, _currentVersion);

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > MaxResponseBytes)
            {
                throw new JsonException("The response is larger than a release description can be.");
            }
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// The tag as a version ("v0.3.0" → "0.3.0"), or null when it is not one. The tag comes
    /// from the network and ends up in a sentence shown to the examiner, so only the characters
    /// a version can contain are allowed through.
    /// </summary>
    internal static string? CleanVersion(string? tag)
    {
        var version = tag?.Trim().TrimStart('v', 'V');
        if (string.IsNullOrEmpty(version) || version.Length > 40 || !char.IsAsciiDigit(version[0]))
        {
            return null;
        }
        return version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+') ? version : null;
    }

    /// <summary>
    /// The release page to show. Whatever the API returned is used only if it is an HTTPS
    /// address inside this project on github.com; anything else is replaced by the releases
    /// page, so a tampered answer cannot put some other address in front of the examiner.
    /// </summary>
    internal static string TrustedReleaseUrl(string? candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.StartsWith("/AffluentMods/cinder/releases", StringComparison.OrdinalIgnoreCase)
            ? uri.GetLeftPart(UriPartial.Path)
            : ReleasesPage;

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
