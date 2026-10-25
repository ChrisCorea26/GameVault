using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.Library.Models;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>The Library (home) screen: search, owned filter, grid/list toggle and collection summary.</summary>
public sealed partial class LibraryViewModel(ILibraryService libraryService) : ObservableObject
{
    private IReadOnlyList<GameTileViewModel> _allGames = [];

    /// <summary>The tiles that match the current search and filters.</summary>
    public ObservableCollection<GameTileViewModel> Games { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyLibrary), nameof(HasNoMatches))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowOwnedOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView))]
    public partial bool IsGridView { get; set; } = true;

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double CompletionFraction { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyLibrary), nameof(HasNoMatches))]
    public partial int TotalGameCount { get; set; }

    public bool IsListView => !IsGridView;

    /// <summary>True when the library has no games at all: show the "add a ROM folder" guidance.</summary>
    public bool IsEmptyLibrary => !IsLoading && TotalGameCount == 0;

    /// <summary>True when the library has games but none match the search or filters.</summary>
    public bool HasNoMatches => !IsLoading && TotalGameCount > 0 && Games.Count == 0;

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        try
        {
            var games = await libraryService.GetGamesAsync(cancellationToken);
            _allGames = games
                .OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new GameTileViewModel(g))
                .ToList();

            var summary = CollectionSummary.Compute(games);
            SummaryText = summary.ToString();
            CompletionFraction = summary.Fraction;
            TotalGameCount = _allGames.Count;
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ToggleView() => IsGridView = !IsGridView;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowOwnedOnlyChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        Games.Clear();
        foreach (var tile in _allGames)
        {
            if ((!ShowOwnedOnly || tile.IsOwned) && tile.Matches(SearchText))
            {
                Games.Add(tile);
            }
        }

        OnPropertyChanged(nameof(HasNoMatches));
    }
}
