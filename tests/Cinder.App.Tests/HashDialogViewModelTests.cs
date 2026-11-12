using System.Security.Cryptography;
using System.Text;
using Cinder.App.Services;
using Cinder.App.ViewModels;
using Cinder.Core.Cases;
using Cinder.Core.Custody;
using Cinder.Core.Hashing;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Cinder.App.Tests;

[Collection(ShellNoticeCollection.Name)]
public class HashDialogViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-hashdlg-" + Guid.NewGuid().ToString("N"));

    public HashDialogViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        ActiveCaseContext.ResetFailureTracking();
    }

    public void Dispose()
    {
        ActiveCaseContext.ResetFailureTracking();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static HashDialogViewModel NoCase(string? path) => new(path: path, activeCase: () => null);

    [Fact]
    public async Task All_four_digests_of_a_known_file_are_the_published_values()
    {
        var vm = NoCase(Write("hello.txt", Encoding.ASCII.GetBytes("hello")));

        await vm.HashCommand.ExecuteAsync(null);

        vm.Error.Should().BeNull();
        vm.Md5.Should().BeEquivalentTo("5d41402abc4b2a76b9719d911017c592");
        vm.Sha1.Should().BeEquivalentTo("aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d");
        vm.Sha256.Should().BeEquivalentTo("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
        vm.Blake3.Should().BeEquivalentTo("ea8f163db38682925e4491c5e58d4bb3506ef8c14eb78a86e908c5624a67200f");
        vm.BytesHashed.Should().Be(5);
        vm.IsComputing.Should().BeFalse();
        vm.HasNote.Should().BeFalse("a text file is not a container");
        vm.StatusLine.Should().Contain("5 bytes hashed").And.Contain("No case is open");
    }

    [Fact]
    public async Task A_path_pasted_in_quotes_is_accepted()
    {
        var path = Write("quoted name.bin", [1, 2, 3]);
        var vm = NoCase($"  \"{path}\"  ");

        await vm.HashCommand.ExecuteAsync(null);

        vm.Error.Should().BeNull();
        vm.Sha256.Should().BeEquivalentTo(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })));
    }

    [Theory]
    [InlineData(null, "Choose a file")]
    [InlineData("   ", "Choose a file")]
    [InlineData("<dir>", "folder")]
    [InlineData("<missing>", "not found")]
    public async Task Something_that_cannot_be_hashed_is_explained_and_leaves_no_digest(string? input, string expected)
    {
        var path = input switch
        {
            "<dir>" => _dir,
            "<missing>" => Path.Combine(_dir, "nope.bin"),
            _ => input,
        };
        var vm = NoCase(path);

        var run = () => vm.HashCommand.ExecuteAsync(null);

        await run.Should().NotThrowAsync();
        vm.Error.Should().Contain(expected);
        vm.Sha256.Should().BeNull();
        vm.IsComputing.Should().BeFalse();
    }

    [Fact]
    public async Task A_second_run_clears_the_first_runs_digests_before_it_can_fail()
    {
        var vm = NoCase(Write("first.bin", [9, 9, 9]));
        await vm.HashCommand.ExecuteAsync(null);
        vm.Sha256.Should().NotBeNull();

        vm.Path = Path.Combine(_dir, "second-does-not-exist.bin");
        await vm.HashCommand.ExecuteAsync(null);

        vm.Error.Should().Contain("not found");
        vm.Md5.Should().BeNull("digests of the previous file must not sit beside the new path");
        vm.Sha1.Should().BeNull();
        vm.Sha256.Should().BeNull();
        vm.Blake3.Should().BeNull();
    }

    [Fact]
    public async Task A_file_in_use_is_reported_not_thrown()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // share modes are advisory elsewhere
        }
        var path = Write("locked.bin", [1, 2, 3]);
        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var vm = NoCase(path);

        var run = () => vm.HashCommand.ExecuteAsync(null);

        await run.Should().NotThrowAsync();
        vm.Error.Should().StartWith("Could not hash the file");
        vm.Sha256.Should().BeNull();
    }

    [Fact]
    public async Task For_a_forensic_container_the_digest_is_of_the_file_and_the_dialog_says_so()
    {
        // The EWF signature followed by filler: enough to be recognised as an E01 segment.
        byte[] content = [0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00, .. new byte[4096]];
        var vm = NoCase(Write("disk.E01", content));

        await vm.HashCommand.ExecuteAsync(null);

        vm.Error.Should().BeNull();
        vm.Sha256.Should().BeEquivalentTo(Convert.ToHexString(SHA256.HashData(content)),
            "this dialog hashes the bytes on disk; the media inside is the Image verify tool's business");
        vm.BytesHashed.Should().Be(content.Length);
        vm.Note.Should().Contain("EWF").And.Contain("not of the disk image inside it").And.Contain("Image verify");
    }

    [Fact]
    public async Task With_a_case_open_the_digests_go_into_its_custody_log()
    {
        var casePath = Path.Combine(_dir, "case.cinder");
        var store = new CaseStore(casePath);
        var created = await new CaseService(store, new CustodyLog(store)).CreateAsync("Hash case", "examiner", null);
        var session = new CaseSession(created.Id, created.Name, created.Examiner, casePath, DateTimeOffset.UtcNow);
        var file = Write("evidence.bin", Encoding.ASCII.GetBytes("hello"));
        // The case is open when hashing starts and gone when it ends: the entry still belongs to it.
        var open = true;
        var vm = new HashDialogViewModel(path: file, activeCase: () => open ? session : null);

        var run = vm.HashCommand.ExecuteAsync(null);
        open = false;
        await run;

        vm.StatusLine.Should().Contain("Recorded in the custody log of \"Hash case\"");
        var entry = (await new CustodyLog(store).ListAsync(created.Id))
            .Should().ContainSingle(e => e.Action == CustodyAction.EvidenceHashed).Subject;
        entry.DetailsJson.Should().ContainEquivalentOf("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")
            .And.Contain("evidence.bin")
            .And.Contain("file bytes as stored on disk");
    }

    [Fact]
    public async Task If_the_custody_log_cannot_be_written_the_dialog_does_not_claim_it_was()
    {
        var session = new CaseSession(Guid.NewGuid(), "Unplugged", "examiner", Path.Combine(_dir, "gone.cinder"), DateTimeOffset.UtcNow);
        var vm = new HashDialogViewModel(path: Write("evidence.bin", [1, 2, 3]), activeCase: () => session);

        await vm.HashCommand.ExecuteAsync(null);

        vm.Sha256.Should().NotBeNull("the digest is still correct and still shown");
        vm.StatusLine.Should().Contain("NOT recorded").And.Contain("Unplugged");
    }

    [Fact]
    public async Task Cancelling_stops_the_run_and_shows_no_partial_result()
    {
        var started = new TaskCompletionSource();
        var vm = new HashDialogViewModel(new NeverFinishes(started), Write("big.bin", [1, 2, 3]), () => null);

        var run = vm.HashCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.IsComputing.Should().BeTrue();
        vm.HashCancelCommand.Execute(null);     // what closing the dialog does
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        vm.IsComputing.Should().BeFalse();
        vm.StatusLine.Should().StartWith("Cancelled");
        vm.Error.Should().BeNull();
        vm.Sha256.Should().BeNull();
    }

    /// <summary>A hash that runs until it is cancelled, like a multi-gigabyte file would.</summary>
    private sealed class NeverFinishes(TaskCompletionSource started) : IHashService
    {
        public Task<MultiHashResult> ComputeAsync(
            Stream input, IReadOnlyCollection<HashAlgorithmKind> algorithms, IProgress<long>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task<MultiHashResult> ComputeFileAsync(
            string path, IReadOnlyCollection<HashAlgorithmKind> algorithms, IProgress<long>? progress = null, CancellationToken ct = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }
}
