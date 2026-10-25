using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.Library.Models;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

public enum DumpStep
{
    ChooseGame,
    Insert,
    Dumping,
    Done,
}

/// <summary>
/// The Dump Cartridge wizard (SPEC §8, §9.2):
/// 1. Pick the game, searching the catalog.
/// 2. Pick the board and sizes.
/// 3. Insert the cartridge.
/// 4. Dump.
/// 5. See whether it matched the catalog.
/// </summary>
public sealed partial class DumpCartridgeViewModel(IDumperService dumper, ILibraryService library, INavigationService navigation)
    : ObservableObject
{
    private IReadOnlyList<GameEntry> _catalog = [];

    public ObservableCollection<GameEntry> Matches { get; } = [];

    public IReadOnlyList<DumpBoard> Boards => dumper.Boards;

    public bool HasDumper => dumper.DumperName is not null;

    public string DumperText => dumper.DumperName is { } name
        ? $"Using your {name}."
        : "No dumper is set up. GameVault supports the INL Retro-Prog; install its software and connect it to dump your cartridges.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing), nameof(IsInserting), nameof(IsDumping), nameof(IsDone))]
    public partial DumpStep Step { get; private set; }

    public bool IsChoosing => Step == DumpStep.ChooseGame;

    public bool IsInserting => Step == DumpStep.Insert;

    public bool IsDumping => Step == DumpStep.Dumping;

    public bool IsDone => Step == DumpStep.Done;

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedGameItem))]
    public partial GameEntry? SelectedGame { get; set; }

    /// <summary>Object-typed bridges for two-way list and combo box bindings.</summary>
    public object? SelectedGameItem
    {
        get => SelectedGame;
        set => SelectedGame = value as GameEntry;
    }

    public object? SelectedBoardItem
    {
        get => SelectedBoard;
        set => SelectedBoard = value as DumpBoard;
    }

    public object? PrgChoice
    {
        get => PrgKb;
        set => PrgKb = value as int? ?? PrgKb;
    }

    public object? ChrChoice
    {
        get => ChrKb;
        set => ChrKb = value as int? ?? ChrKb;
    }

    /// <summary>For games the catalog doesn't list (homebrew, unknown carts).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial string CustomTitle { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrgSizes), nameof(ChrSizes), nameof(SelectedBoardItem))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial DumpBoard? SelectedBoard { get; set; }

    public IReadOnlyList<int> PrgSizes => SelectedBoard?.PrgSizesKb ?? [];

    public IReadOnlyList<int> ChrSizes => SelectedBoard?.ChrSizesKb ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrgChoice))]
    public partial int PrgKb { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChrChoice))]
    public partial int ChrKb { get; set; }

    [ObservableProperty]
    public partial string Progress { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutcomeMessage))]
    public partial DumpOutcome? Outcome { get; private set; }

    public string OutcomeMessage => Outcome?.Message ?? string.Empty;

    public string Title => SelectedGame?.Title ?? (string.IsNullOrWhiteSpace(CustomTitle) ? "Unknown cartridge" : CustomTitle.Trim());

    [RelayCommand]
    private async Task LoadAsync()
    {
        _catalog = (await library.GetGamesAsync()).Where(g => g.IsCataloged).OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        SelectedBoard ??= Boards.FirstOrDefault();
        ApplySearch();
    }

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void Next() => Step = DumpStep.Insert;

    private bool CanContinue() => HasDumper && SelectedBoard is not null && (SelectedGame is not null || !string.IsNullOrWhiteSpace(CustomTitle));

    [RelayCommand]
    private async Task DumpAsync()
    {
        if (SelectedBoard is not { } board)
        {
            return;
        }

        Step = DumpStep.Dumping;
        Progress = "Starting…";
        var progress = new Progress<string>(p => Progress = p);
        Outcome = await dumper.DumpAsync(SelectedGame?.Key, Title, board, PrgKb, ChrKb, progress);
        Step = DumpStep.Done;
    }

    [RelayCommand]
    private void TryAgain() => Step = DumpStep.Insert;

    [RelayCommand]
    private void OpenGame()
    {
        if (Outcome?.GameKey is { } key)
        {
            navigation.NavigateTo(AppPage.GameDetails, key);
        }
    }

    [RelayCommand]
    private void Back() => Step = DumpStep.ChooseGame;

    partial void OnSearchChanged(string value) => ApplySearch();

    partial void OnSelectedBoardChanged(DumpBoard? value)
    {
        PrgKb = value?.PrgSizesKb.FirstOrDefault() ?? 0;
        ChrKb = value?.ChrSizesKb.FirstOrDefault() ?? 0;
    }

    private void ApplySearch()
    {
        Matches.Clear();
        foreach (var game in _catalog.Where(g => string.IsNullOrWhiteSpace(Search) || g.Title.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase)).Take(50))
        {
            Matches.Add(game);
        }
    }
}
