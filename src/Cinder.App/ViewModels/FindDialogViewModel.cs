using Cinder.Hex;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

public sealed partial class FindDialogViewModel : ViewModelBase
{
    private readonly HexViewModel _hex;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private HexSearchKind _mode = HexSearchKind.Ascii;

    [ObservableProperty]
    private bool _caseSensitive = true;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string? _statusLine;

    public IReadOnlyList<HexSearchKind> AvailableModes { get; } =
        [HexSearchKind.Ascii, HexSearchKind.Utf16Le, HexSearchKind.Utf16Be, HexSearchKind.Hex, HexSearchKind.Regex];

    public HexViewModel Hex => _hex;

    public FindDialogViewModel(HexViewModel hex)
    {
        _hex = hex ?? throw new ArgumentNullException(nameof(hex));
    }

    [RelayCommand]
    private async Task RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Query) || _hex.Buffer is null)
        {
            return;
        }
        IsSearching = true;
        StatusLine = "Searching…";
        try
        {
            var options = new HexSearchOptions(Mode, Query, CaseSensitive);
            await _hex.SearchAsync(options, ct);
            StatusLine = _hex.SearchResults.Count switch
            {
                0 => "No matches.",
                1 => "1 match.",
                >= 1000 => "1000+ matches (capped).",
                var n => $"{n} matches.",
            };
        }
        finally
        {
            IsSearching = false;
        }
    }
}
