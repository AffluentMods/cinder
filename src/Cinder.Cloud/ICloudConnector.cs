using Cinder.Artifacts;

namespace Cinder.Cloud;

public sealed record CloudFile(
    string Provider, string AccountId, string Path, long Size,
    DateTimeOffset? Created, DateTimeOffset? Modified, string? Sha256, string? RemoteId)
    : ArtifactBase($"cloud.{Provider}.file", AccountId, Modified ?? Created, Path);

public sealed record CloudActivity(
    string Provider, string AccountId, string Action, string Target,
    DateTimeOffset? Timestamp, string? IpAddress, string? UserAgent)
    : ArtifactBase($"cloud.{Provider}.activity", AccountId, Timestamp, $"{Action} {Target}");

public sealed record CloudAuthorization(
    string Provider, string AccountId, string AccessToken, string? RefreshToken,
    DateTimeOffset? ExpiresUtc, string Scope);

public interface ICloudConnector
{
    string ProviderId { get; }
    string DisplayName { get; }

    /// <summary>Returns the OAuth/PKCE authorization URL the user must visit; the connector
    /// completes the flow once Cinder receives the redirect on the loopback listener.</summary>
    Task<Uri> BeginAuthAsync(string redirectLoopbackUri, CancellationToken ct);

    Task<CloudAuthorization> CompleteAuthAsync(string authorizationCode, string codeVerifier, string redirectLoopbackUri, CancellationToken ct);

    IAsyncEnumerable<CloudFile> ListFilesAsync(CloudAuthorization auth, CancellationToken ct);
    IAsyncEnumerable<CloudActivity> ListActivityAsync(CloudAuthorization auth, DateTimeOffset since, CancellationToken ct);
}
