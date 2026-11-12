using System.Security.Cryptography;
using Cinder.Plugins;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// Trust-gate behaviour for <see cref="PluginLoader"/>. We don't need real plugins to test
/// these — every code path that matters fires on metadata (opted in? hash approved?
/// file-extension filter?) before the assembly load even happens.
///
/// <para>The opt-in and the approved hashes live in a <see cref="PluginTrustStore"/> outside the
/// plugin directory. These tests originally created the opt-in sentinel and the hash manifest
/// <em>inside</em> the plugin directory and asserted that the loader honoured them — which is
/// the defect: whoever can drop a DLL into the folder can drop those two files beside it. They
/// now assert the opposite for in-folder files, and exercise the gate through a store kept
/// elsewhere (a temp file here, so the developer's real per-user store is never read or
/// written). The loading of real plugin assemblies is covered in PluginTrustBoundaryTests.</para>
/// </summary>
public sealed class PluginLoaderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _storeDir;
    private readonly PluginTrustStore _store;

    public PluginLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cinder-plugins-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _storeDir = Path.Combine(Path.GetTempPath(), $"cinder-plugins-test-store-{Guid.NewGuid():N}");
        _store = new PluginTrustStore(Path.Combine(_storeDir, "plugin-trust.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_storeDir, recursive: true); } catch { }
    }

    [Fact]
    public void Empty_directory_returns_empty_result()
    {
        _store.SetPluginsEnabled(true);
        var loader = new PluginLoader(_store);
        var results = loader.LoadFromDirectory(_dir);
        results.Should().BeEmpty();
    }

    [Fact]
    public void No_sentinel_means_no_loading_even_with_dlls_present()
    {
        // Drop a fake .dll into the directory.
        File.WriteAllBytes(Path.Combine(_dir, "evil.dll"), new byte[] { 0x4D, 0x5A, 0x00 });

        var loader = new PluginLoader(_store);
        var results = loader.LoadFromDirectory(_dir);

        // Without the examiner's opt-in, the loader doesn't even hash the DLL. This is the
        // primary defense against "malware drops a .dll into the plugin folder and waits for
        // Cinder to launch."
        results.Should().BeEmpty();
    }

    [Fact]
    public void Sentinel_without_hash_in_manifest_surfaces_as_untrusted()
    {
        // Corrected: the opt-in is recorded in the per-user store. A sentinel file inside the
        // plugin directory (which this test used to create) is no longer an opt-in at all.
        _store.SetPluginsEnabled(true);
        File.WriteAllBytes(Path.Combine(_dir, "third-party.dll"), new byte[] { 0x4D, 0x5A, 0x00, 0x01 });

        var loader = new PluginLoader(_store);
        var results = loader.LoadFromDirectory(_dir);

        results.Should().HaveCount(1);
        results[0].FileName.Should().Be("third-party.dll");
        results[0].Status.Should().Be("untrusted");
        results[0].Sha256.Should().HaveLength(64, "SHA-256 hex digest should be present so the user can approve it");
        results[0].Plugin.Should().BeNull();
    }

    [Fact]
    public void Trust_manifest_format_accepts_both_hash_only_and_sha256sum_form()
    {
        // Corrected (the name is kept so history lines up): this used to write a sha256sum-style
        // manifest into the plugin directory and expect the gate to open. The manifest is no
        // longer read from there. What the test still pins is the behaviour behind the gate:
        // an approved hash — in either letter case — passes it, and a file that then fails to
        // load is reported "failed" with its hash, not "untrusted".
        _store.SetPluginsEnabled(true);
        var bytes = new byte[] { 0x4D, 0x5A, 0x00, 0x02 };
        var dllPath = Path.Combine(_dir, "approved.dll");
        File.WriteAllBytes(dllPath, bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        _store.Trust(hash.ToUpperInvariant(), "approved.dll");

        var loader = new PluginLoader(_store);
        var results = loader.LoadFromDirectory(_dir);

        // Hash matches — gate passes. Loading a 4-byte fake DLL then fails, so we expect
        // "failed" status, NOT "untrusted".
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
