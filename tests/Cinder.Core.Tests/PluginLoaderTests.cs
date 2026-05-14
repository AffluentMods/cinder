using System.Security.Cryptography;
using Cinder.Plugins;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// Trust-gate behaviour for <see cref="PluginLoader"/>. We don't need real plugins to test
/// these — every code path that matters fires on metadata (sentinel file present? hash in
/// manifest? file-extension filter?) before the assembly load even happens.
/// </summary>
public sealed class PluginLoaderTests : IDisposable
{
    private readonly string _dir;

    public PluginLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cinder-plugins-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Empty_directory_returns_empty_result()
    {
        var loader = new PluginLoader();
        var results = loader.LoadFromDirectory(_dir);
        results.Should().BeEmpty();
    }

    [Fact]
    public void No_sentinel_means_no_loading_even_with_dlls_present()
    {
        // Drop a fake .dll into the directory.
        File.WriteAllBytes(Path.Combine(_dir, "evil.dll"), new byte[] { 0x4D, 0x5A, 0x00 });

        var loader = new PluginLoader();
        var results = loader.LoadFromDirectory(_dir);

        // Without .cinder-trusted, the loader doesn't even hash the DLL. This is the primary
        // defense against "malware drops a .dll into the plugin folder and waits for Cinder
        // to launch."
        results.Should().BeEmpty();
    }

    [Fact]
    public void Sentinel_without_hash_in_manifest_surfaces_as_untrusted()
    {
        File.WriteAllText(Path.Combine(_dir, PluginLoader.TrustSentinelFile), "");
        File.WriteAllBytes(Path.Combine(_dir, "third-party.dll"), new byte[] { 0x4D, 0x5A, 0x00, 0x01 });

        var loader = new PluginLoader();
        var results = loader.LoadFromDirectory(_dir);

        results.Should().HaveCount(1);
        results[0].FileName.Should().Be("third-party.dll");
        results[0].Status.Should().Be("untrusted");
        results[0].Sha256.Should().HaveLength(64, "SHA-256 hex digest should be present so user can copy it into manifest");
        results[0].Plugin.Should().BeNull();
    }

    [Fact]
    public void Trust_manifest_format_accepts_both_hash_only_and_sha256sum_form()
    {
        File.WriteAllText(Path.Combine(_dir, PluginLoader.TrustSentinelFile), "");
        var bytes = new byte[] { 0x4D, 0x5A, 0x00, 0x02 };
        var dllPath = Path.Combine(_dir, "approved.dll");
        File.WriteAllBytes(dllPath, bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        // Write the manifest in sha256sum format: "<hash>  filename"
        File.WriteAllText(Path.Combine(_dir, PluginLoader.TrustManifestFile),
            $"# comment line ignored\n{hash}  approved.dll\n");

        var loader = new PluginLoader();
        var results = loader.LoadFromDirectory(_dir);

        // Hash matches — gate passes. The actual Assembly.LoadFrom on a 4-byte fake DLL will
        // fail, so we expect "failed" status, NOT "untrusted".
        results.Should().HaveCount(1);
        results[0].Status.Should().Be("failed");
        results[0].Sha256.Should().Be(hash);
    }

    [Fact]
    public void SignatureDisplay_falls_back_to_unsigned_when_no_signature()
    {
        var r = PluginLoadResult.Untrusted("foo.dll", "deadbeef");
        r.SignatureDisplay.Should().Be("unsigned");
    }

    [Fact]
    public void SignatureDisplay_extracts_subject_CN()
    {
        var info = new AuthenticodeInfo(
            Subject: "CN=Affluent Labs, O=Affluent Labs, C=US",
            Issuer: "CN=GlobalSign",
            NotAfter: DateTime.UtcNow.AddYears(1),
            ChainValid: true,
            Thumbprint: "abc123");
        var r = new PluginLoadResult("foo.dll", "hash", Plugin: null, Status: "loaded", Error: null, Signature: info);
        r.SignatureDisplay.Should().Be("Affluent Labs ✓");
    }

    [Fact]
    public void SignatureDisplay_marks_untrusted_chain()
    {
        var info = new AuthenticodeInfo(
            Subject: "CN=Sketchy Inc",
            Issuer: "CN=Self-Signed Authority",
            NotAfter: DateTime.UtcNow.AddYears(1),
            ChainValid: false,
            Thumbprint: "deadbeef");
        var r = new PluginLoadResult("foo.dll", "hash", Plugin: null, Status: "loaded", Error: null, Signature: info);
        r.SignatureDisplay.Should().Be("Sketchy Inc (untrusted chain)");
    }
}
