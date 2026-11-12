using Cinder.App.Services;
using Cinder.Core.Custody;
using Cinder.Core.Hashing;
using Cinder.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

/// <summary>
/// "Hash a file…": MD5, SHA-1, SHA-256 and BLAKE3 of one file, read once.
///
/// <para>What is hashed is the file itself — its bytes on disk, the same answer
/// <c>sha256sum</c> gives. That is deliberate and it is stated in the dialog, because for a
/// forensic container the other possible answer (the digest of the media <em>inside</em> an
/// E01, AFF4 or VHD) is a different number that means a different thing. Checking the media
/// against its acquisition hash is the Image verify tool's job; this dialog is for the digest
/// you write down when a file changes hands.</para>
///
/// <para>When a case is open the result goes into its custody log — the case that was active
/// when hashing began, even if the examiner switches case before it ends.</para>
/// </summary>
public sealed partial class HashDialogViewModel : ViewModelBase
{
    private static readonly HashAlgorithmKind[] Algorithms =
        [HashAlgorithmKind.Md5, HashAlgorithmKind.Sha1, HashAlgorithmKind.Sha256, HashAlgorithmKind.Blake3];

    private readonly IHashService _hash;
    private readonly Func<CaseSession?> _activeCase;

    [ObservableProperty]
    private string? _path;

    [ObservableProperty]
    private bool _isComputing;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private long _bytesHashed;

    [ObservableProperty]
    private string? _md5;

    [ObservableProperty]
    private string? _sha1;

    [ObservableProperty]
    private string? _sha256;

    [ObservableProperty]
    private string? _blake3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    /// <summary>Set when the file is a forensic container, to say which of its two possible digests this is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string? _note;

    /// <summary>One line under the digests: how much was read, and whether it was recorded in a custody log.</summary>
    [ObservableProperty]
    private string? _statusLine;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <param name="hash">The hashing service; the standard one by default.</param>
    /// <param name="path">A file to hash as soon as the dialog opens, or null to let the examiner choose.</param>
    /// <param name="activeCase">Where the active case comes from; <see cref="ActiveCaseContext.Current"/> by default.</param>
    public HashDialogViewModel(IHashService? hash = null, string? path = null, Func<CaseSession?>? activeCase = null)
    {
        _hash = hash ?? new HashService();
        _path = path;
        _activeCase = activeCase ?? (() => ActiveCaseContext.Current);
    }

    /// <summary>
    /// Hashes <see cref="Path"/>. Never throws: a missing file, a file in use and a cancelled
    /// run all end up in <see cref="Error"/> or <see cref="StatusLine"/>.
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task HashAsync(CancellationToken ct)
    {
        // A path pasted from Explorer's "Copy as path" arrives in quotes.
        var path = Path?.Trim().Trim('"');
        Md5 = Sha1 = Sha256 = Blake3 = null;
        Error = null;
        Note = null;
        StatusLine = null;
        BytesHashed = 0;
        ProgressPercent = 0;

        if (string.IsNullOrWhiteSpace(path))
        {
            Error = "Choose a file to hash.";
            return;
        }
        if (Directory.Exists(path))
        {
            Error = "That is a folder. Choose a file.";
            return;
        }
        if (!File.Exists(path))
        {
            Error = "File not found.";
            return;
        }

        // The case this hash belongs to is the one open now, not the one open when it finishes.
        var session = _activeCase();
        try
        {
            IsComputing = true;
            var full = System.IO.Path.GetFullPath(path);
            var size = new FileInfo(full).Length;
            var progress = new Progress<long>(b =>
            {
                BytesHashed = b;
                ProgressPercent = size > 0 ? Math.Min(100, b * 100.0 / size) : 0;
            });

            // On the thread pool: opening and the first read of a large file must not hold the UI.
            var result = await Task.Run(() => _hash.ComputeFileAsync(full, Algorithms, progress, ct), ct);

            Md5 = result.Md5;
            Sha1 = result.Sha1;
            Sha256 = result.Sha256;
            Blake3 = result.Blake3;
            BytesHashed = result.BytesHashed;
            ProgressPercent = 100;
            Note = ContainerNote(full);

            var recorded = await ActiveCaseContext.LogAsync(session, CustodyAction.EvidenceHashed, new
            {
                Path = full,
                Scope = "file bytes as stored on disk",
                result.BytesHashed,
                result.Md5,
                result.Sha1,
                result.Sha256,
                result.Blake3,
            }, CancellationToken.None);

            StatusLine = $"{result.BytesHashed:N0} bytes hashed. " + (
                recorded ? $"Recorded in the custody log of \"{session!.Name}\"."
                : session?.Path is null ? "No case is open, so this was not recorded in a custody log."
                : $"NOT recorded: the custody log of \"{session.Name}\" could not be written.");
        }
        catch (OperationCanceledException)
        {
            Md5 = Sha1 = Sha256 = Blake3 = null;
            StatusLine = "Cancelled. No digest was produced.";
        }
        catch (Exception ex)
        {
            Md5 = Sha1 = Sha256 = Blake3 = null;
            Error = "Could not hash the file: " + CrashHandler.Describe(ex);
        }
        finally
        {
            IsComputing = false;
        }
    }

    /// <summary>For a forensic container, says that the digest is of the container file and where to get the other one.</summary>
    internal static string? ContainerNote(string path)
    {
        string? kind;
        try
        {
            kind = EvidenceOpener.IsEwf(path) ? "an EWF (E01) segment"
                : EvidenceOpener.IsAff4(path) ? "an AFF4 container"
                : EvidenceOpener.VirtualDiskKind(path) is { } disk ? $"a {disk.ToUpperInvariant()} virtual disk"
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            kind = null;
        }
        return kind is null
            ? null
            : $"This file is {kind}. These are the digests of the file itself, not of the disk image inside it. " +
              "To check the image against its acquisition hash, use the Image verify tool.";
    }
}
