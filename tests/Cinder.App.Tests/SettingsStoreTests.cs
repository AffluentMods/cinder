using System.Text.Json;
using Cinder.App.Services;
using Cinder.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public SettingsStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CinderSettings Configured() => new()
    {
        Theme = "Light",
        Density = "Compact",
        VimModeInHex = true,
        CheckForUpdates = false,
        PythonExecutable = "/opt/python/bin/python3",
        ParsersDirectory = "/opt/cinder/parsers",
        HashSetDatabase = "/evidence/nsrl.db",
        TimestampAuthorityUrl = "https://tsa.example/tsr",
        EnabledPlugins = ["plugin.one", "plugin.two"],
        AiProvider = new Dictionary<string, string> { ["id"] = "openai", ["apiKey"] = "sk-secret-value", ["temperature"] = "0.2" },
        CloudClientIds = new Dictionary<string, string> { ["dropbox"] = "dbx", ["box"] = "someone-elses-key" },
    };

    // ------------------------------------------------------------------ atomic writes

    [Fact]
    public void AtomicFile_replaces_the_target_and_leaves_nothing_else_behind()
    {
        AtomicFile.WriteAllText(_path, "first");
        AtomicFile.WriteAllText(_path, "second, and longer than the first");

        File.ReadAllText(_path).Should().Be("second, and longer than the first");
        Directory.GetFiles(_dir).Should().Equal(_path);
        File.ReadAllBytes(_path).Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "no byte-order mark");
    }

    [Fact]
    public void AtomicFile_creates_missing_directories()
    {
        var nested = Path.Combine(_dir, "a", "b", "file.json");
        AtomicFile.WriteAllText(nested, "{}");
        File.ReadAllText(nested).Should().Be("{}");
    }

    [Fact]
    public void A_failed_atomic_write_leaves_the_existing_file_untouched_and_no_temp_file()
    {
        AtomicFile.WriteAllText(_path, "the examiner's settings");

        // The rename is the last step; hold the target open so it cannot be replaced.
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => AtomicFile.WriteAllText(_path, "replacement");
            if (OperatingSystem.IsWindows())
            {
                act.Should().Throw<Exception>();
            }
            else
            {
                // POSIX rename succeeds over an open file; there is no failure to observe.
                act.Should().NotThrow();
                return;
            }
        }

        File.ReadAllText(_path).Should().Be("the examiner's settings");
        Directory.GetFiles(_dir).Should().Equal(_path);
    }

    // ------------------------------------------------------------------ settings store

    [Fact]
    public void Settings_round_trip_and_the_api_key_is_not_stored_in_the_clear()
    {
        var store = new SettingsStore(_path);
        store.Save(Configured());

        var onDisk = File.ReadAllText(_path);
        onDisk.Should().NotContain("sk-secret-value").And.Contain(SettingsStore.EncryptedPrefix);
        Directory.GetFiles(_dir).Should().Equal(_path);

        var loaded = store.Load();
        loaded.Should().BeEquivalentTo(Configured());
    }

    [Fact]
    public void A_missing_or_empty_file_means_defaults_and_can_be_saved_over()
    {
        var store = new SettingsStore(_path);
        store.TryLoad(out var none, out var error).Should().BeTrue();
        error.Should().BeNull();
        none.Should().BeEquivalentTo(new CinderSettings());

        File.WriteAllText(_path, "");
        store.TryLoad(out _, out _).Should().BeTrue("a zero-length file holds no settings to lose");
        store.Save(new CinderSettings { Theme = "Light" });
        store.Load().Theme.Should().Be("Light");
    }

    [Theory]
    [InlineData("{ \"Theme\": \"Light\", \"AiProvider\": { \"id\": \"openai\"")]   // cut off mid-write
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    public void A_file_that_exists_but_cannot_be_read_is_never_overwritten(string damaged)
    {
        File.WriteAllText(_path, damaged);
        var store = new SettingsStore(_path);

        store.TryLoad(out var settings, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
        settings.Should().BeEquivalentTo(new CinderSettings());
        store.Load().Should().BeEquivalentTo(new CinderSettings(), "readers still get defaults");

        var save = () => store.Save(new CinderSettings { HashSetDatabase = "/new/path.db" });

        save.Should().Throw<SettingsUnreadableException>().Which.SettingsPath.Should().Be(_path);
        save.Should().Throw<IOException>("callers that already handle I/O failures handle this one");
        File.ReadAllText(_path).Should().Be(damaged, "whatever was in the file is still there for the examiner to recover");
        Directory.GetFiles(_dir).Should().Equal(_path);
    }

    [Fact]
    public void A_file_locked_by_another_process_is_not_treated_as_empty_settings()
    {
        var store = new SettingsStore(_path);
        store.Save(Configured());
        var before = File.ReadAllText(_path);

        using (new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
            {
                return;   // share modes are advisory elsewhere; the Windows case is the real one
            }
            store.TryLoad(out _, out var error).Should().BeFalse();
            error.Should().NotBeNullOrEmpty();
            var save = () => store.Save(new CinderSettings());
            save.Should().Throw<SettingsUnreadableException>();
        }

        File.ReadAllText(_path).Should().Be(before);
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("ApiKey")]
    [InlineData("apiKey")]
    public void The_api_key_loads_under_one_name_whichever_spelling_the_file_used(string spelling)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(new
        {
            Theme = "Dark",
            AiProvider = new Dictionary<string, string> { ["id"] = "openai", [spelling] = "plain-legacy-key" },
        }));
        var store = new SettingsStore(_path);

        var loaded = store.Load();
        loaded.AiProvider.Should().Contain(SettingsStore.ApiKeyName, "plain-legacy-key");
        loaded.AiProvider.Keys.Should().BeEquivalentTo("id", SettingsStore.ApiKeyName);

        store.Save(loaded);
        var onDisk = File.ReadAllText(_path);
        onDisk.Should().NotContain("plain-legacy-key").And.NotContain("api_key").And.NotContain("\"ApiKey\"");
        store.Load().AiProvider[SettingsStore.ApiKeyName].Should().Be("plain-legacy-key");
    }

    // ------------------------------------------------------------------ settings dialog

    [Fact]
    public void Saving_from_the_dialog_keeps_everything_the_dialog_does_not_show()
    {
        var store = new SettingsStore(_path);
        store.Save(Configured());

        var dialog = new SettingsDialogViewModel(store);
        dialog.AiApiKey.Should().Be("sk-secret-value");
        dialog.Theme = "Dark";
        dialog.AiModel = "gpt-4o-mini";
        dialog.DropboxAppKey = null;
        dialog.SaveCommand.Execute(null);

        dialog.SaveError.Should().BeNull();
        var expected = Configured() with
        {
            Theme = "Dark",
            AiProvider = new Dictionary<string, string>
            {
                ["id"] = "openai", ["apiKey"] = "sk-secret-value", ["temperature"] = "0.2", ["model"] = "gpt-4o-mini",
            },
            CloudClientIds = new Dictionary<string, string> { ["box"] = "someone-elses-key" },
        };
        store.Load().Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void The_dialog_writes_the_api_key_under_the_name_the_ai_tool_reads()
    {
        var store = new SettingsStore(_path);
        var dialog = new SettingsDialogViewModel(store) { AiProviderId = "openai", AiApiKey = "sk-new" };

        dialog.SaveCommand.Execute(null);

        dialog.SaveError.Should().BeNull();
        store.Load().AiProvider.Should().Contain("apiKey", "sk-new").And.NotContainKey("api_key");
    }

    [Fact]
    public void A_setting_changed_elsewhere_while_the_dialog_was_open_survives_its_save()
    {
        var store = new SettingsStore(_path);
        store.Save(new CinderSettings());
        var dialog = new SettingsDialogViewModel(store);

        // e.g. a plug-in enabled from its own tool while the dialog is up
        store.Save(store.Load() with { EnabledPlugins = ["added.meanwhile"] });
        dialog.SaveCommand.Execute(null);

        store.Load().EnabledPlugins.Should().Equal("added.meanwhile");
    }

    [Fact]
    public void Saving_over_an_unreadable_file_reports_the_failure_and_cannot_throw()
    {
        File.WriteAllText(_path, "{ damaged");
        var store = new SettingsStore(_path);
        var notices = new List<string>();
        var previousSink = ShellNotice.ErrorSink;
        ShellNotice.ErrorSink = notices.Add;
        try
        {
            var dialog = new SettingsDialogViewModel(store);
            dialog.HasSaveError.Should().BeTrue("the dialog knows from the start that it is showing defaults");
            dialog.Theme = "Light";

            var save = () => dialog.SaveCommand.Execute(null);

            save.Should().NotThrow("the dialog closes straight after Save; an exception here would end the app");
            dialog.SaveError.Should().Contain("not saved");
            notices.Should().ContainSingle().Which.Should().Contain("not saved");
            File.ReadAllText(_path).Should().Be("{ damaged");
        }
        finally
        {
            ShellNotice.ErrorSink = previousSink;
        }
    }

    // ------------------------------------------------------------------ the other stores

    [Fact]
    public void Recents_are_written_atomically_and_round_trip()
    {
        var path = Path.Combine(_dir, "recents.json");
        var store = new RecentsStore(path);
        var snapshot = new RecentsSnapshot
        {
            Cases = [new RecentCaseEntry(Guid.NewGuid(), "Case", "Examiner", "/cases/a.cinder", DateTimeOffset.UnixEpoch)],
            Evidence = [new RecentEvidenceEntry("/evidence/disk.E01", DateTimeOffset.UnixEpoch)],
        };

        store.Save(snapshot);
        store.Save(snapshot);

        store.Load().Should().BeEquivalentTo(snapshot);
        Directory.GetFiles(_dir).Should().Equal(path);
    }

    [Fact]
    public void Window_layout_is_written_atomically()
    {
        var path = Path.Combine(_dir, "window.json");
        var store = new WindowLayoutStore(path);
        var layout = new WindowLayout(10, 20, 1400, 900, false, 250, 320, true, true);

        store.Save(layout);
        store.Save(layout with { Width = 1600 });

        store.Load().Should().Be(layout with { Width = 1600 });
        Directory.GetFiles(_dir).Should().Equal(path);
    }

    [Theory]
    [InlineData("Light", "Light")]
    [InlineData("light", "Light")]
    [InlineData("System", "System")]
    [InlineData("Dark", "Dark")]
    [InlineData("", "Dark")]
    [InlineData(null, "Dark")]
    [InlineData("Solarized", "Dark")]
    public void Theme_names_resolve_to_a_variant_and_back(string? saved, string expected)
    {
        ThemeService.NameOf(ThemeService.Resolve(saved)).Should().Be(expected);
    }
}
