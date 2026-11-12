using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Cinder.Plugins;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// The plugin trust boundary, exercised with real assemblies (emitted on the fly, so no binary
/// fixtures are checked in):
/// <list type="bullet">
/// <item>trust files placed in the plugin folder approve nothing;</item>
/// <item>an approved plugin loads; its dependencies load only if each is approved too, and a
/// dependency swapped after approval is refused;</item>
/// <item>a signer is shown as verified only when the signature actually covers the file.</item>
/// </list>
/// </summary>
public sealed class PluginTrustBoundaryTests : IDisposable
{
    private const string HelperAssemblyName = "CinderTestHelper";

    private readonly string _root = Directory.CreateTempSubdirectory("cinder-plugin-trust").FullName;
    private readonly string _plugins;
    private readonly PluginTrustStore _store;

    public PluginTrustBoundaryTests()
    {
        _plugins = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(_plugins);
        _store = new PluginTrustStore(Path.Combine(_root, "profile", "plugin-trust.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string Sha256Of(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    // ---- emitting real plugin assemblies ----------------------------------------------------

    /// <summary>Writes <c>CinderTestHelper.dll</c> exposing <c>Helper.Ping()</c> returning <paramref name="pingValue"/>.</summary>
    private string EmitHelper(string pingValue, string? directory = null)
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(HelperAssemblyName) { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule(HelperAssemblyName);
        var type = module.DefineType(HelperAssemblyName + ".Helper", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var ping = type.DefineMethod("Ping", MethodAttributes.Public | MethodAttributes.Static, typeof(string), Type.EmptyTypes);
        var il = ping.GetILGenerator();
        il.Emit(OpCodes.Ldstr, pingValue);
        il.Emit(OpCodes.Ret);
        type.CreateType();

        var path = Path.Combine(directory ?? _plugins, HelperAssemblyName + ".dll");
        assembly.Save(path);
        return path;
    }

    /// <summary>
    /// Writes a plugin assembly with one public <see cref="IPlugin"/>. When
    /// <paramref name="helperPath"/> is given, the plugin's <c>Id</c> is whatever
    /// <c>Helper.Ping()</c> returns — so the plugin has a genuine reference to the helper
    /// assembly and its behaviour shows which helper it ran against.
    /// </summary>
    private string EmitPlugin(string assemblyName, string id, string? helperPath = null)
    {
        MethodInfo? ping = null;
        AssemblyLoadContext? scratch = null;
        if (helperPath is not null)
        {
            // Loaded in a throwaway context purely to obtain a MethodInfo to emit a call to.
            // Not the default context: an assembly already loaded there counts as host-provided.
            scratch = new AssemblyLoadContext("emit-scratch", isCollectible: true);
            var helper = scratch.LoadFromStream(new MemoryStream(File.ReadAllBytes(helperPath)));
            ping = helper.GetType(HelperAssemblyName + ".Helper", throwOnError: true)!.GetMethod("Ping")!;
        }

        var assembly = new PersistedAssemblyBuilder(new AssemblyName(assemblyName) { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule(assemblyName);
        var type = module.DefineType(assemblyName + ".Plugin",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed, typeof(object), [typeof(IPlugin)]);
        type.DefineDefaultConstructor(MethodAttributes.Public);

        const MethodAttributes Implementation =
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot;

        void StringGetter(string property, Action<ILGenerator> body)
        {
            var contract = typeof(IPlugin).GetProperty(property)!.GetMethod!;
            var method = type.DefineMethod(contract.Name, Implementation | MethodAttributes.SpecialName, typeof(string), Type.EmptyTypes);
            var gen = method.GetILGenerator();
            body(gen);
            gen.Emit(OpCodes.Ret);
            type.DefineMethodOverride(method, contract);
        }

        StringGetter(nameof(IPlugin.Id), gen =>
        {
            if (ping is not null) gen.Emit(OpCodes.Call, ping);
            else gen.Emit(OpCodes.Ldstr, id);
        });
        StringGetter(nameof(IPlugin.DisplayName), gen => gen.Emit(OpCodes.Ldstr, "Test plugin " + id));
        StringGetter(nameof(IPlugin.Author), gen => gen.Emit(OpCodes.Ldstr, "tests"));
        StringGetter(nameof(IPlugin.Version), gen => gen.Emit(OpCodes.Ldstr, "1.0"));

        var registerContract = typeof(IPlugin).GetMethod(nameof(IPlugin.Register))!;
        var register = type.DefineMethod(registerContract.Name, Implementation, typeof(void), [typeof(IPluginContext)]);
        register.GetILGenerator().Emit(OpCodes.Ret);
        type.DefineMethodOverride(register, registerContract);
        type.CreateType();

        var path = Path.Combine(_plugins, assemblyName + ".dll");
        assembly.Save(path);
        scratch?.Unload();
        return path;
    }

    // ---- trust lives outside the plugin folder ----------------------------------------------

    [Fact]
    public void The_default_store_is_under_the_users_local_application_data()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        PluginTrustStore.DefaultPath.Should().StartWith(Path.Combine(local, "Cinder") + Path.DirectorySeparatorChar);
        new PluginLoader().TrustStore.FilePath.Should().Be(Path.GetFullPath(PluginTrustStore.DefaultPath));
    }

    [Fact]
    public void A_sentinel_and_manifest_dropped_next_to_the_dll_approve_nothing()
    {
        // Exactly what an attacker — or a helpful colleague's zip file — would ship: the plugin,
        // the opt-in sentinel, and a manifest that lists the plugin's own hash.
        var plugin = EmitPlugin("SelfApproved", "self-approved");
        File.WriteAllText(Path.Combine(_plugins, PluginLoader.TrustSentinelFile), "");
        File.WriteAllText(Path.Combine(_plugins, PluginLoader.TrustManifestFile), $"{Sha256Of(plugin)}  SelfApproved.dll\n");
        var loader = new PluginLoader(_store);

        loader.LoadFromDirectory(_plugins).Should().BeEmpty("the folder's own sentinel is not the examiner's opt-in");

        _store.SetPluginsEnabled(true);
        var results = loader.LoadFromDirectory(_plugins);

        results.Should().ContainSingle();
        results[0].Status.Should().Be("untrusted", "the folder's own manifest is not the examiner's approval");
        results[0].Plugin.Should().BeNull();
        PluginLoader.HasLegacyTrustFiles(_plugins).Should().BeTrue();
    }

    [Fact]
    public void Approving_the_hash_in_the_store_is_what_lets_a_plugin_load()
    {
        var plugin = EmitPlugin("Approved", "approved-plugin");
        _store.SetPluginsEnabled(true);
        var loader = new PluginLoader(_store);

        var before = loader.LoadFromDirectory(_plugins).Single();
        before.Status.Should().Be("untrusted");
        before.Sha256.Should().Be(Sha256Of(plugin));

        loader.TrustStore.Trust(before.Sha256!, before.FileName);
        var after = loader.LoadFromDirectory(_plugins).Single();

        after.Status.Should().Be("loaded");
        after.FileName.Should().Be("Approved.dll");
        after.Plugin.Should().NotBeNull();
        after.Plugin!.Id.Should().Be("approved-plugin");
        after.Plugin.DisplayName.Should().Be("Test plugin approved-plugin");
    }

    [Fact]
    public void Switching_plugins_off_again_stops_an_approved_plugin_loading()
    {
        var plugin = EmitPlugin("Approved", "approved-plugin");
        _store.SetPluginsEnabled(true);
        _store.Trust(Sha256Of(plugin));
        new PluginLoader(_store).LoadFromDirectory(_plugins).Single().Status.Should().Be("loaded");

        _store.SetPluginsEnabled(false);

        new PluginLoader(_store).LoadFromDirectory(_plugins).Should().BeEmpty();
    }

    [Fact]
    public void A_modified_plugin_is_untrusted_again()
    {
        var plugin = EmitPlugin("Approved", "approved-plugin");
        _store.SetPluginsEnabled(true);
        _store.Trust(Sha256Of(plugin));

        File.AppendAllText(plugin, "tampered");   // trailing bytes: still a loadable PE, different hash

        new PluginLoader(_store).LoadFromDirectory(_plugins).Single().Status.Should().Be("untrusted");
    }

    // ---- every assembly a plugin loads is hash-checked --------------------------------------

    [Fact]
    public void A_plugin_whose_dependency_is_not_approved_does_not_load()
    {
        var helper = EmitHelper("helper-v1");
        var plugin = EmitPlugin("NeedsHelper", "unused", helper);
        _store.SetPluginsEnabled(true);
        _store.Trust(Sha256Of(plugin));

        var results = new PluginLoader(_store).LoadFromDirectory(_plugins);

        var pluginRow = results.Single(r => r.FileName == "NeedsHelper.dll");
        pluginRow.Status.Should().Be("failed");
        pluginRow.Plugin.Should().BeNull("no plugin object may exist when part of its code is unapproved");
        pluginRow.Sha256.Should().Be(Sha256Of(plugin));
        pluginRow.Error.Should().Contain(HelperAssemblyName + ".dll").And.Contain(Sha256Of(helper));

        var helperRow = results.Single(r => r.FileName == HelperAssemblyName + ".dll");
        helperRow.Status.Should().Be("untrusted");
        helperRow.Sha256.Should().Be(Sha256Of(helper), "the examiner approves a dependency the same way as a plugin");
    }

    [Fact]
    public void A_plugin_loads_and_runs_against_its_dependency_once_both_are_approved()
    {
        var helper = EmitHelper("helper-v1");
        var plugin = EmitPlugin("NeedsHelper", "unused", helper);
        _store.SetPluginsEnabled(true);
        _store.Trust(Sha256Of(plugin));
        _store.Trust(Sha256Of(helper));

        var results = new PluginLoader(_store).LoadFromDirectory(_plugins);

        var loaded = results.Single(r => r.Status == "loaded");
        loaded.FileName.Should().Be("NeedsHelper.dll");
        loaded.Plugin!.Id.Should().Be("helper-v1", "Id is implemented as a call into the helper assembly");
        results.Should().NotContain(r => r.Status == "untrusted" || r.Status == "failed");
    }

    [Fact]
    public void A_dependency_swapped_after_approval_is_refused()
    {
        // Approve plugin + helper, then replace the helper. The plugin's own bytes — and so its
        // approved hash — are unchanged; with Assembly.LoadFrom the new helper simply ran.
        var helper = EmitHelper("helper-v1");
        var plugin = EmitPlugin("NeedsHelper", "unused", helper);
        _store.SetPluginsEnabled(true);
        _store.Trust(Sha256Of(plugin));
        _store.Trust(Sha256Of(helper));

        var swapped = EmitHelper("EVIL");
        Sha256Of(swapped).Should().NotBe(_store.TrustedPlugins[1].Sha256);

        var results = new PluginLoader(_store).LoadFromDirectory(_plugins);

        results.Should().NotContain(r => r.Status == "loaded");
        var pluginRow = results.Single(r => r.FileName == "NeedsHelper.dll");
        pluginRow.Status.Should().Be("failed");
        pluginRow.Error.Should().Contain(Sha256Of(swapped));
        results.Single(r => r.FileName == HelperAssemblyName + ".dll").Status.Should().Be("untrusted");
    }

    [Fact]
    public void A_copy_of_a_host_assembly_in_the_folder_is_never_used_in_place_of_the_hosts()
    {
        // Build output of a real plugin project includes Cinder.Plugins.dll. If the loader took
        // that copy, the plugin's IPlugin would be a different type from the host's and nothing
        // would be recognised — and a doctored copy would run. The host's always wins.
        var plugin = EmitPlugin("Approved", "approved-plugin");
        var hostContract = typeof(IPlugin).Assembly.Location;
        File.Copy(hostContract, Path.Combine(_plugins, Path.GetFileName(hostContract)));
        _store.SetPluginsEnabled(true);
        _store.Trust(Sha256Of(plugin));

        var results = new PluginLoader(_store).LoadFromDirectory(_plugins);

        var loaded = results.Single(r => r.Status == "loaded");
        loaded.Plugin.Should().BeAssignableTo<IPlugin>();
        loaded.Plugin!.GetType().GetInterfaces().Should().Contain(typeof(IPlugin));
        results.Single(r => r.FileName == Path.GetFileName(hostContract)).Status.Should().Be("untrusted");
    }

    // ---- the store itself --------------------------------------------------------------------

    [Fact]
    public void A_new_store_trusts_nothing_and_has_plugins_off()
    {
        _store.PluginsEnabled.Should().BeFalse();
        _store.TrustedPlugins.Should().BeEmpty();
        _store.IsTrusted(new string('a', 64)).Should().BeFalse();
        File.Exists(_store.FilePath).Should().BeFalse("reading must not create the file");
    }

    [Fact]
    public void Decisions_persist_across_instances_and_can_be_revoked()
    {
        var hash = new string('a', 32) + new string('B', 32);
        _store.SetPluginsEnabled(true);
        _store.Trust(hash, @"C:\somewhere\plugin.dll");
        _store.Trust(hash);   // idempotent

        var reopened = new PluginTrustStore(_store.FilePath);
        reopened.PluginsEnabled.Should().BeTrue();
        reopened.IsTrusted(hash.ToUpperInvariant()).Should().BeTrue("hashes compare case-insensitively");
        reopened.TrustedPlugins.Should().ContainSingle().Which.Sha256.Should().Be(hash.ToLowerInvariant());
        reopened.TrustedPlugins[0].FileName.Should().Be("plugin.dll");

        reopened.Revoke(hash).Should().BeTrue();
        _store.IsTrusted(hash).Should().BeFalse("the first instance reads the file afresh");
        reopened.Revoke(hash).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("deadbeef")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("../../somewhere")]
    public void Only_a_sha256_digest_can_be_trusted(string notAHash)
    {
        var act = () => _store.Trust(notAHash);
        act.Should().Throw<ArgumentException>();
        _store.TrustedPlugins.Should().BeEmpty();
    }

    [Fact]
    public void A_corrupt_store_fails_closed()
    {
        _store.SetPluginsEnabled(true);
        _store.Trust(new string('c', 64));
        File.WriteAllText(_store.FilePath, "{ \"enabled\": true, \"trusted\": [ { \"sha256\": ");

        _store.PluginsEnabled.Should().BeFalse();
        _store.IsTrusted(new string('c', 64)).Should().BeFalse();
    }

    // ---- signatures ---------------------------------------------------------------------------

    [Fact]
    public void An_unsigned_file_has_no_signature()
    {
        var plugin = EmitPlugin("Unsigned", "unsigned");
        AuthenticodeVerifier.Inspect(plugin).Should().BeNull();
        PluginLoadResult.Untrusted("Unsigned.dll", Sha256Of(plugin)).SignatureDisplay.Should().Be("unsigned");
    }

    [Fact]
    public void A_signature_that_does_not_cover_the_file_is_not_shown_as_verified()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Authenticode is a Windows mechanism
        }

        // The runtime's own core library is Authenticode-signed in Microsoft's builds. (A
        // source-built runtime is not; there is then nothing to tamper with, and nothing to test.)
        var signedOriginal = typeof(object).Assembly.Location;
        var original = AuthenticodeVerifier.Inspect(signedOriginal);
        if (original is null)
        {
            return;
        }
        original.ChainValid.Should().BeTrue("an untouched Microsoft-signed runtime file must verify, or the verifier itself is broken");
        original.FileIntact.Should().BeTrue();
        new PluginLoadResult("x.dll", "h", null, "untrusted", null, original).SignatureDisplay.Should().EndWith("✓");

        // Same certificate table, different code: change one byte in the middle of the image.
        var tamperedPath = Path.Combine(_root, "tampered-signed.dll");
        var bytes = File.ReadAllBytes(signedOriginal);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(tamperedPath, bytes);

        var tampered = AuthenticodeVerifier.Inspect(tamperedPath);

        tampered.Should().NotBeNull("the certificate is still embedded, which is the point");
        tampered!.Subject.Should().Be(original.Subject);
        tampered.ChainValid.Should().BeFalse("a valid certificate on an altered file verifies nothing");
        tampered.FileIntact.Should().BeFalse();
        var display = new PluginLoadResult("x.dll", "h", null, "untrusted", null, tampered).SignatureDisplay;
        display.Should().NotContain("✓");
        display.Should().Contain("does not match file");
    }
}
