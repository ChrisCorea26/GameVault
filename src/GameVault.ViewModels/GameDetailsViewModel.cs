using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.Library.Models;
using GameVault.Library.Profiles;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>
/// Game Details (SPEC §9.2): metadata, ownership toggles, status, favorite, rating, notes, playtime, the
/// user's cover picture, the save-state timeline, Game Properties overrides and Play. Edits save right away.
/// </summary>
public sealed partial class GameDetailsViewModel(
    ILibraryService library,
    TimelineViewModel timeline,
    INavigationService navigation,
    IGameFilePicker picker,
    PasswordsViewModel passwords,
    JournalViewModel journal,
    CodesViewModel codes) : ObservableObject
{
    /// <summary>Game Genie and RAM codes (SPEC §7.8).</summary>
    public CodesViewModel Codes => codes;

    /// <summary>The Passwords panel (SPEC §7.3).</summary>
    public PasswordsViewModel Passwords => passwords;

    /// <summary>The journal: notes, screenshots, maps (SPEC §7.4).</summary>
    public JournalViewModel Journal => journal;

    private GameEntry? _game;
    private bool _loading;

    public TimelineViewModel Timeline => timeline;

    public IReadOnlyList<string> StatusOptions { get; } = ["Unplayed", "Playing", "Beaten", "100%"];

    public IReadOnlyList<string> InputOptions { get; } = ["Automatic", "Controller", "Zapper", "Four Score"];

    public IReadOnlyList<string> AutoOnOffOptions { get; } = ["Automatic", "On", "Off"];

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>Unknown / homebrew games can be renamed; cataloged titles come from the catalog.</summary>
    [ObservableProperty]
    public partial bool CanEditTitle { get; private set; }

    [ObservableProperty]
    public partial string Subtitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Details { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Initials { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    public partial Uri? CoverUri { get; private set; }

    public bool HasCover => CoverUri is not null;

    [ObservableProperty]
    public partial bool OwnsCartridge { get; set; }

    [ObservableProperty]
    public partial bool OwnsBox { get; set; }

    [ObservableProperty]
    public partial bool OwnsManual { get; set; }

    [ObservableProperty]
    public partial bool OwnsDigitalDump { get; set; }

    [ObservableProperty]
    public partial int StatusIndex { get; set; }

    [ObservableProperty]
    public partial bool IsFavorite { get; set; }

    /// <summary>1–5, or -1 for unrated (what RatingControl uses).</summary>
    [ObservableProperty]
    public partial double Rating { get; set; } = -1;

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlaytimeText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial bool CanPlay { get; private set; }

    [ObservableProperty]
    public partial string PlayHint { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int InputIndex { get; set; }

    [ObservableProperty]
    public partial int RunAheadIndex { get; set; }

    [ObservableProperty]
    public partial int SpriteLimitIndex { get; set; }

    [RelayCommand]
    private async Task LoadAsync(string? key)
    {
        if (key is null || await library.GetGameAsync(key) is not { } game)
        {
            Title = "Game not found";
            CanPlay = false;
            return;
        }

        _loading = true;
        _game = game;
        Title = game.Title;
        CanEditTitle = !game.IsCataloged;
        var tile = new GameTileViewModel(game);
        Initials = tile.Initials;
        Subtitle = tile.Subtitle;
        Details = string.Join(" · ", new[]
        {
            LibraryViewModel.RegionName(game.Region),
            game.Players > 1 ? $"{game.Players} players" : "1 player",
            game.IsCataloged ? (game.IsLicensed ? "Licensed release" : "Unlicensed") : "Unknown / Homebrew",
            game.Developer is { } developer ? $"Developed by {developer}" : null,
        }.Where(s => s is not null));
        CoverUri = game.CoverPath is { } cover ? new Uri(cover) : null;
        OwnsCartridge = game.Owned.HasFlag(Ownership.Cartridge);
        OwnsBox = game.Owned.HasFlag(Ownership.Box);
        OwnsManual = game.Owned.HasFlag(Ownership.Manual);
        OwnsDigitalDump = game.Owned.HasFlag(Ownership.DigitalDump);
        StatusIndex = (int)game.Status;
        IsFavorite = game.IsFavorite;
        Rating = game.Rating ?? -1;
        Notes = game.Notes ?? string.Empty;
        PlaytimeText = game.Playtime >= TimeSpan.FromMinutes(1)
            ? $"{TimelineItemViewModel.FormatPlaytime(game.Playtime)} played" + (game.LastPlayed is { } last ? $" · last played {last.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}" : string.Empty)
            : "Not played yet";
        CanPlay = game.RomPath is not null;
        PlayHint = CanPlay ? string.Empty : "Add a folder with this game's file to play it.";

        var overrides = await library.GetProfileOverridesAsync(key);
        InputIndex = overrides?.Input is { } input ? (int)input + 1 : 0;
        RunAheadIndex = ToIndex(overrides?.RunAheadSafe);
        SpriteLimitIndex = ToIndex(overrides?.RemoveSpriteLimit);
        _loading = false;

        timeline.CanSave = false;
        timeline.LoadRequested -= OnLoadRequested;
        timeline.LoadRequested += OnLoadRequested;
        await timeline.ShowAsync(key);
        await passwords.ShowAsync(key);
        await journal.ShowAsync(key);
        await codes.ShowAsync(key);
    }

    /// <summary>Called when the page is left.</summary>
    public void Detach()
    {
        timeline.LoadRequested -= OnLoadRequested;
        timeline.Detach();
        passwords.Detach();
        journal.Detach();
        codes.Detach();
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        if (_game?.RomPath is { } path)
        {
            navigation.NavigateTo(AppPage.Player, new GameLaunch(path, _game.Key));
        }
    }

    [RelayCommand]
    private async Task ChooseCoverAsync()
    {
        if (await picker.PickImageAsync() is { } path)
        {
            CoverUri = new Uri(path);
            await SaveAsync(g => g with { CoverPath = path });
        }
    }

    [RelayCommand]
    private Task RemoveCoverAsync()
    {
        CoverUri = null;
        return SaveAsync(g => g with { CoverPath = null });
    }

    /// <summary>Notes and the title save when the text box loses focus, not on every keystroke.</summary>
    [RelayCommand]
    private Task SaveTextAsync() => SaveAsync(g => g with
    {
        Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes,
        Title = CanEditTitle && !string.IsNullOrWhiteSpace(Title) ? Title.Trim() : g.Title,
    });

    partial void OnOwnsCartridgeChanged(bool value) => _ = SaveOwnership();

    partial void OnOwnsBoxChanged(bool value) => _ = SaveOwnership();

    partial void OnOwnsManualChanged(bool value) => _ = SaveOwnership();

    partial void OnOwnsDigitalDumpChanged(bool value) => _ = SaveOwnership();

    partial void OnStatusIndexChanged(int value)
    {
        if (value is >= 0 and <= 3)
        {
            _ = SaveAsync(g => g with { Status = (PlayStatus)value });
        }
    }

    partial void OnIsFavoriteChanged(bool value) => _ = SaveAsync(g => g with { IsFavorite = value });

    partial void OnRatingChanged(double value) =>
        _ = SaveAsync(g => g with { Rating = value is >= 1 and <= 5 ? (int)Math.Round(value) : null });

    partial void OnInputIndexChanged(int value) => _ = SaveOverrides();

    partial void OnRunAheadIndexChanged(int value) => _ = SaveOverrides();

    partial void OnSpriteLimitIndexChanged(int value) => _ = SaveOverrides();

    private Task SaveOwnership() => SaveAsync(g => g with
    {
        Owned = (OwnsCartridge ? Ownership.Cartridge : 0)
            | (OwnsBox ? Ownership.Box : 0)
            | (OwnsManual ? Ownership.Manual : 0)
            | (OwnsDigitalDump ? Ownership.DigitalDump : 0),
    });

    private async Task SaveAsync(Func<GameEntry, GameEntry> change)
    {
        if (_loading || _game is null)
        {
            return;
        }

        _game = change(_game);
        await library.SaveGameAsync(_game);
    }

    private async Task SaveOverrides()
    {
        if (_loading || _game is null)
        {
            return;
        }

        var overrides = new GameProfile
        {
            Input = InputIndex is >= 1 and <= 3 ? (InputType)(InputIndex - 1) : null,
            RunAheadSafe = FromIndex(RunAheadIndex),
            RemoveSpriteLimit = FromIndex(SpriteLimitIndex),
        };
        var empty = overrides.Input is null && overrides.RunAheadSafe is null && overrides.RemoveSpriteLimit is null;
        await library.SaveProfileOverridesAsync(_game.Key, empty ? null : overrides);
    }

    private static int ToIndex(bool? value) => value switch { true => 1, false => 2, _ => 0 };

    private static bool? FromIndex(int index) => index switch { 1 => true, 2 => false, _ => null };

    private void OnLoadRequested(object? sender, TimelineItemViewModel item)
    {
        if (_game?.RomPath is { } path)
        {
            navigation.NavigateTo(AppPage.Player, new GameLaunch(path, _game.Key, item.Id));
        }
    }
}
