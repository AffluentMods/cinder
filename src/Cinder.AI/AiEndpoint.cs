namespace Cinder.AI;

/// <summary>
/// Turns whatever the user typed as a provider endpoint into a base URI that relative request
/// paths resolve against correctly.
///
/// <para>RFC 3986 resolution replaces the last path segment unless the base ends in a slash:
/// <c>https://host/v1</c> + <c>chat/completions</c> is <c>https://host/chat/completions</c>, a
/// 404 that looks like a broken key. People type the endpoint both ways, and often paste the
/// full request URL from a provider's documentation, so both are accepted.</para>
/// </summary>
public static class AiEndpoint
{
    private static readonly string[] RequestPathSuffixes = ["/chat/completions", "/api/chat"];

    public static Uri Normalize(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"AI endpoint must be an absolute http(s) URL, got '{endpoint}'.", nameof(endpoint));
        }

        var path = endpoint.AbsolutePath.TrimEnd('/');
        foreach (var suffix in RequestPathSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^suffix.Length];
                break;
            }
        }

        return new UriBuilder(endpoint) { Path = path + "/", Query = "", Fragment = "" }.Uri;
    }
}
