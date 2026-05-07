using Cinder.Core.Custody;
using Cinder.Core.Hashing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

/// <summary>
/// Streaming hash dialog. Drop a file in, get MD5 / SHA-1 / SHA-256 / BLAKE3 + ssdeep* and an
/// auto-logged custody entry. *(ssdeep / TLSH wired up in Phase 6.)*
/// </summary>
public sealed partial class HashDialogViewModel : ViewModelBase
{
    private readonly IHashService _hash;
    private readonly ICustodyLog? _custody;
    private readonly Func<Guid?> _activeCaseAccessor;

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
    private string? _error;

    public HashDialogViewModel(IHashService hash, ICustodyLog? custody = null, Func<Guid?>? activeCaseAccessor = null)
    {
        _hash = hash ?? throw new ArgumentNullException(nameof(hash));
        _custody = custody;
        _activeCaseAccessor = activeCaseAccessor ?? (() => null);
    }

    [RelayCommand]
    private async Task HashAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Path) || !File.Exists(Path))
        {
            Error = "File not found.";
            return;
        }

        try
        {
            Error = null;
            IsComputing = true;
            ProgressPercent = 0;
            BytesHashed = 0;

            var size = new FileInfo(Path).Length;
            var progress = new Progress<long>(b =>
            {
                BytesHashed = b;
                ProgressPercent = size > 0 ? Math.Min(100, b * 100.0 / size) : 0;
            });

            var result = await _hash.ComputeFileAsync(
                Path,
                [HashAlgorithmKind.Md5, HashAlgorithmKind.Sha1, HashAlgorithmKind.Sha256, HashAlgorithmKind.Blake3],
                progress,
                ct).ConfigureAwait(false);

            Md5 = result.Md5;
            Sha1 = result.Sha1;
            Sha256 = result.Sha256;
            Blake3 = result.Blake3;

            if (_custody is not null && _activeCaseAccessor() is { } caseId)
            {
                var details = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Path,
                    BytesHashed = result.BytesHashed,
                    Md5 = result.Md5,
                    Sha1 = result.Sha1,
                    Sha256 = result.Sha256,
                    Blake3 = result.Blake3,
                });
                await _custody.AppendAsync(caseId, Environment.UserName, CustodyAction.EvidenceHashed, details, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsComputing = false;
        }
    }
}
