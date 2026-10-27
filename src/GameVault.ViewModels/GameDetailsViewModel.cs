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

    /// <summary>Index N (from 1) is <see cref="InputType"/> N - 1.</summary>
    public IReadOnlyList<string> InputOptions { get; } =
        ["Automatic", "Controller", "Zapper", "Four Score", "Power Pad (keyboard)", "Arkanoid paddle (mouse)"];

    public IReadOnlyList<string> AutoOnOffOptions { get; } = ["Automatic", "On", "Off"];

    /// <summary>Unknown / homebrew games: the region, as <see cref="GameRegion"/> by index.</summary>
    public IReadOnlyList<string> GameRegionOptions { get; } = ["Unknown", "North America", "Europe", "Japan", "World"];

    /// <summary>Unknown / homebrew games: 1–4 players, by index (0 is one player).</summary>
    public IReadOnlyList<string> PlayerCountOptions { get; } = ["1 player", "2 players", "3 players", "4 players"];

    public IReadOnlyList<string> MirroringOptions { get; } = ["Automatic", "Horizontal", "Vertical", "Four-screen"];

    private static readonly string?[] MirroringValues = [null, "horizontal", "vertical", "four-screen"];

    public IReadOnlyList<string> AppleModelOptions { get; } = ["Automatic", "Apple II+", "Apple IIe", "Enhanced Apple IIe"];

    private static readonly string?[] AppleModelValues = [null, "ii+", "iie", "iie-enhanced"];

    public IReadOnlyList<string> GenesisPadOptions { get; } = ["Automatic (6-button)", "6-button", "3-button"];

    private static readonly string?[] GenesisPadValues = [null, "6", "3"];

    public IReadOnlyList<string> N64SaveOptions { get; } =
        ["Automatic", "EEPROM (4 Kbit)", "EEPROM (16 Kbit)", "SRAM", "Flash", "None"];

    private static readonly string?[] N64SaveValues = [null, "eeprom4k", "eeprom16k", "sram", "flash", "none"];

    /// <summary>The TV standards this game's system offers (Properties → Region); empty when it has no choice.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRegionChoice))]
    public partial IReadOnlyList<string> RegionOptions { get; private set; } = [];

    public bool HasRegionChoice => RegionOptions.Count > 0;

    private string?[] _regionValues = [];

    /// <summary>The game is for the NES (or Disk System): controls, flicker, header corrections and codes apply (GV-016).</summary>
    [ObservableProperty]
    public partial bool IsNes { get; private set; }

    [ObservableProperty]
    public partial bool IsAppleII { get; private set; }

    [ObservableProperty]
    public partial bool IsGenesis { get; private set; }

    [ObservableProperty]
    public partial bool IsN64 { get; private set; }

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

    /// <summary>Index into <see cref="RegionOptions"/> (0 is Automatic).</summary>
    [ObservableProperty]
    public partial int RegionIndex { get; set; }

    [ObservableProperty]
    public partial int AppleModelIndex { get; set; }

    [ObservableProperty]
    public partial int GenesisPadIndex { get; set; }

    [ObservableProperty]
    public partial int N64SaveIndex { get; set; }

    /// <summary>NES header correction: the mapper number, or empty to keep the file's. Saved when the box loses focus.</summary>
    [ObservableProperty]
    public partial string MapperText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int MirroringIndex { get; set; }

    /// <summary>NES header correction: battery-backed save RAM (Automatic, On, Off).</summary>
    [ObservableProperty]
    public partial int BatteryIndex { get; set; }

    /// <summary>Why the mapper box's text wasn't used, or empty.</summary>
    [ObservableProperty]
    public partial string MapperError { get; private set; } = string.Empty;

    // ---------------------------------------------------------------- metadata of unknown / homebrew games (GV-011)

    [ObservableProperty]
    public partial string Publisher { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Developer { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReleaseYearText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Genre { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int GameRegionIndex { get; set; }

    [ObservableProperty]
    public partial int PlayerCountIndex { get; set; }

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

        Publisher = game.Publisher ?? string.Empty;
        Developer = game.Developer ?? string.Empty;
        ReleaseYearText = game.ReleaseYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Genre = game.Genre ?? string.Empty;
        GameRegionIndex = (int)game.Region;
        PlayerCountIndex = Math.Clamp(game.Players, 1, 4) - 1;

        ShowPropertiesFor(game.System);
        var overrides = await library.GetProfileOverridesAsync(key);
        InputIndex = overrides?.Input is { } input ? (int)input + 1 : 0;
        RunAheadIndex = ToIndex(overrides?.RunAheadSafe);
        SpriteLimitIndex = ToIndex(overrides?.RemoveSpriteLimit);
        RegionIndex = IndexOf(_regionValues, overrides?.Region);
        AppleModelIndex = IndexOf(AppleModelValues, overrides?.Model);
        GenesisPadIndex = IndexOf(GenesisPadValues, overrides?.Pad);
        N64SaveIndex = IndexOf(N64SaveValues, overrides?.Save);
        MapperText = overrides?.Header?.Mapper?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        MirroringIndex = IndexOf(MirroringValues, overrides?.Header?.Mirroring);
        BatteryIndex = ToIndex(overrides?.Header?.Battery);
        _extraHeader = overrides?.Header;
        MapperError = string.Empty;
        _loading = false;

        timeline.CanSave = false;
        timeline.LoadRequested -= OnLoadRequested;
        timeline.LoadRequested += OnLoadRequested;
        await timeline.ShowAsync(key);
        await passwords.ShowAsync(key);
        await journal.ShowAsync(key);
        await codes.ShowAsync(key, _game?.Title);
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

    /// <summary>
    /// Notes, the title and an unknown game's other details save when a text box loses focus, not on every keystroke.
    /// Cataloged games keep the catalog's details.
    /// </summary>
    [RelayCommand]
    private Task SaveTextAsync() => SaveAsync(g => CanEditTitle
        ? g with
        {
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes,
            Title = !string.IsNullOrWhiteSpace(Title) ? Title.Trim() : g.Title,
            Publisher = NullIfBlank(Publisher),
            Developer = NullIfBlank(Developer),
            Genre = NullIfBlank(Genre),
            ReleaseYear = int.TryParse(ReleaseYearText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1970 and <= 2100
                ? year
                : string.IsNullOrWhiteSpace(ReleaseYearText) ? null : g.ReleaseYear,
        }
        : g with { Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes });

    partial void OnGameRegionIndexChanged(int value)
    {
        if (CanEditTitle && Enum.IsDefined((GameRegion)value))
        {
            _ = SaveAsync(g => g with { Region = (GameRegion)value });
        }
    }

    partial void OnPlayerCountIndexChanged(int value)
    {
        if (CanEditTitle && value is >= 0 and <= 3)
        {
            _ = SaveAsync(g => g with { Players = value + 1 });
        }
    }

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

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

    partial void OnRegionIndexChanged(int value) => _ = SaveOverrides();

    partial void OnAppleModelIndexChanged(int value) => _ = SaveOverrides();

    partial void OnGenesisPadIndexChanged(int value) => _ = SaveOverrides();

    partial void OnN64SaveIndexChanged(int value) => _ = SaveOverrides();

    partial void OnMirroringIndexChanged(int value) => _ = SaveOverrides();

    partial void OnBatteryIndexChanged(int value) => _ = SaveOverrides();

    /// <summary>Called when the mapper box loses focus.</summary>
    [RelayCommand]
    private Task SaveMapperAsync() => SaveOverrides();

    // Header fields this page doesn't edit (submapper, PRG RAM size) are kept as the overrides had them.
    private HeaderCorrection? _extraHeader;

    /// <summary>Shows the properties that apply to the game's system, and the TV standards it can run at.</summary>
    private void ShowPropertiesFor(GameVault.Core.Systems.GameSystem system)
    {
        IsNes = system == GameVault.Core.Systems.GameSystem.Nes;
        IsAppleII = system == GameVault.Core.Systems.GameSystem.AppleII;
        IsGenesis = system == GameVault.Core.Systems.GameSystem.Genesis;
        IsN64 = system == GameVault.Core.Systems.GameSystem.Nintendo64;
        string[][] regions = system switch
        {
            GameVault.Core.Systems.GameSystem.Nes =>
                [["Automatic", "NTSC (60 Hz)", "PAL (50 Hz)", "Dendy (50 Hz)"], ["", "ntsc", "pal", "dendy"]],
            GameVault.Core.Systems.GameSystem.Genesis =>
                [["Automatic", "USA (60 Hz)", "Japan (60 Hz)", "Europe (50 Hz)"], ["", "usa", "japan", "europe"]],
            GameVault.Core.Systems.GameSystem.Snes => [["Automatic", "NTSC (60 Hz)", "PAL (50 Hz)"], ["", "ntsc", "pal"]],
            GameVault.Core.Systems.GameSystem.MasterSystem or GameVault.Core.Systems.GameSystem.Sg1000 =>
                [["Automatic (NTSC, 60 Hz)", "NTSC (60 Hz)", "PAL (50 Hz)"], ["", "ntsc", "pal"]],
            _ => [[], []],
        };
        _regionValues = regions[1];
        RegionOptions = regions[0];
    }

    private static int IndexOf(string?[] values, string? value)
    {
        if (value is null)
        {
            return 0;
        }

        for (var i = 1; i < values.Length; i++)
        {
            if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase)
                || (values[i] == "ii+" && string.Equals(value, "iiplus", StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return 0;
    }

    private static string? ValueAt(string?[] values, int index) => index > 0 && index < values.Length ? values[index] : null;

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

        int? mapper = null;
        MapperError = string.Empty;
        if (!string.IsNullOrWhiteSpace(MapperText))
        {
            if (int.TryParse(MapperText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= 4095)
            {
                mapper = number;
            }
            else
            {
                MapperError = "The mapper is a number from 0 to 4095, or leave it empty to use the file's.";
                mapper = _extraHeader?.Mapper;
            }
        }

        var header = new HeaderCorrection
        {
            Mapper = mapper,
            Submapper = _extraHeader?.Submapper,
            Mirroring = ValueAt(MirroringValues, MirroringIndex),
            Battery = FromIndex(BatteryIndex),
            PrgRamSize = _extraHeader?.PrgRamSize,
        };
        var hasHeader = header.Mapper is not null || header.Submapper is not null || header.Mirroring is not null
            || header.Battery is not null || header.PrgRamSize is not null;
        var overrides = new GameProfile
        {
            Input = InputIndex >= 1 && InputIndex < InputOptions.Count ? (InputType)(InputIndex - 1) : null,
            RunAheadSafe = FromIndex(RunAheadIndex),
            RemoveSpriteLimit = FromIndex(SpriteLimitIndex),
            Region = ValueAt(_regionValues, RegionIndex),
            Model = ValueAt(AppleModelValues, AppleModelIndex),
            Pad = ValueAt(GenesisPadValues, GenesisPadIndex),
            Save = ValueAt(N64SaveValues, N64SaveIndex),
            Header = hasHeader ? header : null,
        };
        var empty = overrides == new GameProfile();
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
