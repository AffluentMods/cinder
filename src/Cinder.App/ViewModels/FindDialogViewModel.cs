using System.Text.RegularExpressions;
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

    /// <summary>
    /// Runs the search and reports the outcome in <see cref="StatusLine"/>. Nothing may escape:
    /// an exception leaving an async command is rethrown on the UI thread, and a pattern that
    /// trips the regex match timeout is an ordinary thing for an examiner to type.
    /// </summary>
    [RelayCommand]
    private async Task RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Query) || _hex.Buffer is null)
        {
            return;
        }
        if (HexViewModel.ValidateFindQuery(Mode, Query) is { } problem)
        {
            // Not "No matches.": that would be a claim about the evidence.
            StatusLine = "Invalid pattern. " + problem;
            return;
        }
        IsSearching = true;
        StatusLine = "Searching…";
        try
        {
            // A byte pattern is exact whatever the checkbox says; folding it would report
            // 6D 7A as a hit for 4D 5A.
            var caseSensitive = CaseSensitive || Mode == HexSearchKind.Hex;
            var options = new HexSearchOptions(Mode, Query, caseSensitive);
            await _hex.SearchAsync(options, ct);
            StatusLine = _hex.SearchResults.Count switch
            {
                0 => "No matches.",
                1 => "1 match.",
                >= 1000 => "More than 1,000 matches. The first 1,000 are listed; F3 in the Hex Viewer steps through the rest.",
                var n => $"{n} matches.",
            };
            // The dialog lands on the first hit in the file (the inline find lands at the caret).
            if (_hex.SearchResults.Count > 0)
            {
                _hex.SelectedSearchResult = _hex.SearchResults[0];
            }
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Search cancelled.";
        }
        catch (RegexMatchTimeoutException)
        {
            StatusLine = "The pattern took too long to match and was stopped. Simplify it (nested repeats like (a+)+ are the usual cause).";
        }
        catch (Exception ex)
        {
            StatusLine = "Search failed: " + ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }
}
