using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.Library.Models;
using GameVault.Library.Scanning;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>
/// The Library (home) screen (SPEC §6, §9.2): the user's games or the complete catalog checklist, search,
/// filters, grid/list toggle and the collection summary. With nothing in the library it offers
/// <see cref="AddFolderCommand"/>, the zero-config path (SPEC §9.3).
/// </summary>
public sealed partial class LibraryViewModel(
    ILibraryService libraryService,
    IGameFilePicker filePicker,
    INavigationService navigation) : ObservableObject
{
    public const string Any = "Any";

    private IReadOnlyList<GameTileViewModel> _allGames = [];
    private bool _attached;

    /// <summary>The tiles that match the current search and filters.</summary>
    public ObservableCollection<GameTileViewModel> Games { get; } = [];

    public ObservableCollection<string> Regions { get; } = [Any];

    public ObservableCollection<string> Genres { get; } = [Any];

    public ObservableCollection<string> Publishers { get; } = [Any];

    public ObservableCollection<string> Years { get; } = [Any];

    public IReadOnlyList<string> Statuses { get; } = [Any, "Unplayed", "Playing", "Beaten", "100%"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyLibrary), nameof(HasNoMatches), nameof(HasNoOwnGames))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowOwnedOnly { get; set; }

    /// <summary>False: the user's games (with a file, or owned). True: every cataloged game (SPEC §6.1).</summary>
    [ObservableProperty]
    public partial bool ShowChecklist { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RegionChoice))]
    public partial string SelectedRegion { get; set; } = Any;

    /// <summary><see cref="SelectedRegion"/> as an object, for a ComboBox's two-way SelectedItem binding.</summary>
    public object? RegionChoice
    {
        get => SelectedRegion;
        set => SelectedRegion = value as string ?? Any;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenreChoice))]
    public partial string SelectedGenre { get; set; } = Any;

    /// <summary><see cref="SelectedGenre"/> as an object, for a ComboBox's two-way SelectedItem binding.</summary>
    public object? GenreChoice
    {
        get => SelectedGenre;
        set => SelectedGenre = value as string ?? Any;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublisherChoice))]
    public partial string SelectedPublisher { get; set; } = Any;

    /// <summary><see cref="SelectedPublisher"/> as an object, for a ComboBox's two-way SelectedItem binding.</summary>
    public object? PublisherChoice
    {
        get => SelectedPublisher;
        set => SelectedPublisher = value as string ?? Any;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YearChoice))]
    public partial string SelectedYear { get; set; } = Any;

    /// <summary><see cref="SelectedYear"/> as an object, for a ComboBox's two-way SelectedItem binding.</summary>
    public object? YearChoice
    {
        get => SelectedYear;
        set => SelectedYear = value as string ?? Any;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusChoice))]
    public partial string SelectedStatus { get; set; } = Any;

    /// <summary><see cref="SelectedStatus"/> as an object, for a ComboBox's two-way SelectedItem binding.</summary>
    public object? StatusChoice
    {
        get => SelectedStatus;
        set => SelectedStatus = value as string ?? Any;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView))]
    public partial bool IsGridView { get; set; } = true;

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double CompletionFraction { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyLibrary), nameof(HasNoMatches), nameof(HasNoOwnGames))]
    public partial int TotalGameCount { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddFolderCommand))]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    public partial string ScanStatus { get; private set; } = string.Empty;

    /// <summary>0–1 while scanning.</summary>
    [ObservableProperty]
    public partial double ScanFraction { get; private set; }

    public bool IsListView => !IsGridView;

    /// <summary>True when the library has no games at all: show the "add a ROM folder" guidance.</summary>
    public bool IsEmptyLibrary => !IsLoading && TotalGameCount == 0;

    /// <summary>A catalog is imported, but none of its games are the user's yet (in "My games" view).</summary>
    public bool HasNoOwnGames => !IsLoading && TotalGameCount > 0 && !ShowChecklist && _allGames.All(t => !IsMine(t));

    /// <summary>True when the library has games but none match the search or filters.</summary>
    public bool HasNoMatches => !IsLoading && TotalGameCount > 0 && Games.Count == 0 && !HasNoOwnGames;

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!_attached)
        {
            libraryService.Changed += OnLibraryChanged;
            _attached = true;
        }

        IsLoading = true;
        try
        {
            var games = await libraryService.GetGamesAsync(cancellationToken);
            _allGames = games
                .OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new GameTileViewModel(g))
                .ToList();

            var summary = CollectionSummary.Compute(games.Where(g => g.IsCataloged));
            SummaryText = summary.ToString();
            CompletionFraction = summary.Fraction;
            TotalGameCount = _allGames.Count;
            RefreshOptions(games);
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Stops listening for library changes (when the page is left).</summary>
    public void Detach()
    {
        if (_attached)
        {
            libraryService.Changed -= OnLibraryChanged;
            _attached = false;
        }
    }

    [RelayCommand]
    private void ToggleView() => IsGridView = !IsGridView;

    /// <summary>Picks a folder, scans it and shows the games found (SPEC §9.3 zero-config path).</summary>
    [RelayCommand(CanExecute = nameof(CanAddFolder))]
    private async Task AddFolderAsync()
    {
        var folder = await filePicker.PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        IsScanning = true;
        ScanStatus = "Looking for games…";
        ScanFraction = 0;
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                ScanFraction = p.FilesFound == 0 ? 1 : (double)p.FilesHashed / p.FilesFound;
                ScanStatus = $"Checking {p.FilesHashed} of {p.FilesFound} files…";
            });
            var result = await libraryService.AddFolderAsync(folder, progress);
            ScanStatus = result.Total == 1 ? "Found 1 game" : $"Found {result.Total} games";
        }
        finally
        {
            IsScanning = false;
        }

        await LoadAsync(CancellationToken.None);
    }

    private bool CanAddFolder() => !IsScanning;

    /// <summary>Lets the user pick any single .nes file and plays it.</summary>
    [RelayCommand]
    private async Task OpenGameFileAsync()
    {
        var path = await filePicker.PickGameFileAsync();
        if (path is not null)
        {
            navigation.NavigateTo(AppPage.Player, path);
        }
    }

    [RelayCommand]
    private void ImportCatalog() => navigation.NavigateTo(AppPage.CatalogImport);

    [RelayCommand]
    private void DumpCartridge() => navigation.NavigateTo(AppPage.DumpCartridge);

    /// <summary>Plays a library game that has a ROM file.</summary>
    [RelayCommand]
    private void Play(GameTileViewModel? tile)
    {
        if (tile?.Game.RomPath is { } path)
        {
            navigation.NavigateTo(AppPage.Player, new GameLaunch(path, tile.Game.Key));
        }
    }

    /// <summary>Opens Game Details: metadata, collection, timeline and Play.</summary>
    [RelayCommand]
    private void OpenDetails(GameTileViewModel? tile)
    {
        if (tile is not null)
        {
            navigation.NavigateTo(AppPage.GameDetails, tile.Game.Key);
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowOwnedOnlyChanged(bool value) => ApplyFilter();

    partial void OnShowChecklistChanged(bool value) => ApplyFilter();

    partial void OnSelectedRegionChanged(string value) => ApplyFilter();

    partial void OnSelectedGenreChanged(string value) => ApplyFilter();

    partial void OnSelectedPublisherChanged(string value) => ApplyFilter();

    partial void OnSelectedYearChanged(string value) => ApplyFilter();

    partial void OnSelectedStatusChanged(string value) => ApplyFilter();

    private static bool IsMine(GameTileViewModel tile) => tile.IsPlayable || tile.IsOwned;

    private void ApplyFilter()
    {
        Games.Clear();
        foreach (var tile in _allGames)
        {
            var game = tile.Game;
            if ((ShowChecklist || IsMine(tile))
                && (!ShowOwnedOnly || tile.IsOwned)
                && tile.Matches(SearchText)
                && Is(SelectedRegion, RegionName(game.Region))
                && Is(SelectedGenre, game.Genre)
                && Is(SelectedPublisher, game.Publisher)
                && Is(SelectedYear, game.ReleaseYear?.ToString(CultureInfo.InvariantCulture))
                && Is(SelectedStatus, tile.StatusText))
            {
                Games.Add(tile);
            }
        }

        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(HasNoOwnGames));
    }

    private static bool Is(string? filter, string? value) => filter is null or Any || filter == value;

    public static string RegionName(GameRegion region) => region switch
    {
        GameRegion.NorthAmerica => "North America",
        GameRegion.Europe => "Europe",
        GameRegion.Japan => "Japan",
        GameRegion.World => "World",
        _ => "Unknown",
    };

    private void RefreshOptions(IReadOnlyList<GameEntry> games)
    {
        Replace(Regions, games.Select(g => RegionName(g.Region)));
        Replace(Genres, games.Select(g => g.Genre));
        Replace(Publishers, games.Select(g => g.Publisher));
        Replace(Years, games.Select(g => g.ReleaseYear?.ToString(CultureInfo.InvariantCulture)));
    }

    private static void Replace(ObservableCollection<string> options, IEnumerable<string?> values)
    {
        var distinct = values.OfType<string>().Where(v => v.Length > 0).Distinct().Order(StringComparer.CurrentCulture).ToList();
        if (options.Skip(1).SequenceEqual(distinct))
        {
            return;
        }

        while (options.Count > 1)
        {
            options.RemoveAt(options.Count - 1);
        }

        foreach (var value in distinct)
        {
            options.Add(value);
        }
    }

    private async void OnLibraryChanged(object? sender, EventArgs e)
    {
        if (!IsScanning)
        {
            await LoadAsync(CancellationToken.None);
        }
    }
}
