using Cinder.Artifacts;

namespace Cinder.Cloud;

/// <summary>
/// One file as a cloud provider lists it.
/// </summary>
/// <param name="Provider">Connector id, e.g. <c>dropbox</c>.</param>
/// <param name="AccountId">The authorised account.</param>
/// <param name="Path">Path within the drive.</param>
/// <param name="Size">Size in bytes as reported by the provider.</param>
/// <param name="Created">Creation time, when the provider reports one.</param>
/// <param name="Modified">Last-modified time, when the provider reports one.</param>
/// <param name="Sha256">
/// The SHA-256 of the file's bytes, and only that. It is compared against hash sets and against
/// locally computed hashes, so a provider's own digest must never be put here — it would simply
/// never match. Null when the provider does not publish a real SHA-256.
/// </param>
/// <param name="RemoteId">The provider's stable id for the item.</param>
/// <param name="ProviderHash">A provider-specific content digest that is <em>not</em> a plain
/// SHA-256 (Dropbox <c>content_hash</c>, OneDrive <c>quickXorHash</c>), kept for comparison
/// against the same provider's later listings.</param>
/// <param name="ProviderHashAlgorithm">What <paramref name="ProviderHash"/> is, e.g.
/// <c>dropbox-content-hash</c>.</param>
public sealed record CloudFile(
    string Provider, string AccountId, string Path, long Size,
    DateTimeOffset? Created, DateTimeOffset? Modified, string? Sha256, string? RemoteId,
    string? ProviderHash = null, string? ProviderHashAlgorithm = null)
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

    /// <summary>The <c>state</c> nonce the last <see cref="BeginAuthAsync"/> put in the URL; pass it to
    /// <see cref="OAuthPkceHelper.AwaitRedirectCodeAsync"/> so a foreign callback is rejected.</summary>
    string? PendingState { get; }

    Task<CloudAuthorization> CompleteAuthAsync(string authorizationCode, string codeVerifier, string redirectLoopbackUri, CancellationToken ct);

    IAsyncEnumerable<CloudFile> ListFilesAsync(CloudAuthorization auth, CancellationToken ct);
    IAsyncEnumerable<CloudActivity> ListActivityAsync(CloudAuthorization auth, DateTimeOffset since, CancellationToken ct);
}
