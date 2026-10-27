using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.Library.Bios;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>The Settings screen. Every change is saved immediately; there is no Apply button.</summary>
public sealed partial class SettingsViewModel(
    ISettingsStore settingsStore,
    InputSettingsViewModel input,
    LibrarySettingsViewModel library,
    FamilyViewModel? family = null,
    RetroAchievementsSettingsViewModel? retroAchievements = null,
    IGameFilePicker? picker = null,
    BiosImporter? biosImporter = null)
    : ObservableObject
{
    // "Choose…" next to each path box (GV-009): a picker works with a mouse or a controller; typing still works too.
    // Extensions are what the files are usually called; any file can be chosen, as dumps are named many ways.

    [RelayCommand]
    private async Task ChooseDumperAsync()
    {
        if (await PickAsync(".exe") is { } path)
        {
            DumperPath = path;
            SaveIfLoaded();
        }
    }

    [RelayCommand]
    private async Task ChooseFdsBiosAsync()
    {
        if (await PickAsync(".rom", ".bin") is { } path)
        {
            FdsBiosPath = await ImportAsync(BiosKind.FamicomDiskSystem, path);
            SaveIfLoaded();
        }
    }

    [RelayCommand]
    private async Task ChooseSegaCdBiosAsync()
    {
        if (await PickAsync(".bin", ".rom", ".md") is { } path)
        {
            SegaCdBiosPath = await ImportAsync(BiosKind.SegaCd, path);
            SaveIfLoaded();
        }
    }

    [RelayCommand]
    private async Task ChooseSega32XGenesisBiosAsync()
    {
        if (await PickAsync(".bin", ".rom") is { } path)
        {
            Sega32XGenesisBiosPath = await ImportAsync(BiosKind.Sega32XGenesis, path);
            SaveIfLoaded();
        }
    }

    [RelayCommand]
    private async Task ChooseSega32XMasterBiosAsync()
    {
        if (await PickAsync(".bin", ".rom") is { } path)
        {
            Sega32XMasterBiosPath = await ImportAsync(BiosKind.Sega32XMaster, path);
            SaveIfLoaded();
        }
    }

    [RelayCommand]
    private async Task ChooseSega32XSlaveBiosAsync()
    {
        if (await PickAsync(".bin", ".rom") is { } path)
        {
            Sega32XSlaveBiosPath = await ImportAsync(BiosKind.Sega32XSlave, path);
            SaveIfLoaded();
        }
    }

    [RelayCommand]
    private async Task ChooseAtari5200BiosAsync()
    {
        if (await PickAsync(".rom", ".bin") is { } path)
        {
            Atari5200BiosPath = await ImportAsync(BiosKind.Atari5200, path);
            SaveIfLoaded();
        }
    }

    [RelayCommand]
    private async Task ChooseAppleRomAsync()
    {
        if (await PickAsync(".rom", ".bin") is { } path)
        {
            AppleIIRomPath = path;
            SaveIfLoaded();
        }
    }

    [RelayCommand]
    private async Task ChooseAppleDiskRomAsync()
    {
        if (await PickAsync(".rom", ".bin") is { } path)
        {
            AppleIIDiskRomPath = path;
            SaveIfLoaded();
        }
    }

    [RelayCommand]
    private async Task ChooseAppleCharacterRomAsync()
    {
        if (await PickAsync(".rom", ".bin") is { } path)
        {
            AppleIICharacterRomPath = path;
            SaveIfLoaded();
        }
    }

    [RelayCommand]
    private async Task ChooseSnesChipRomFolderAsync()
    {
        if (picker is not null && await picker.PickFolderAsync() is { } folder)
        {
            SnesChipRomFolder = folder;
            SaveIfLoaded();
        }
    }

    [RelayCommand]
    private async Task ChoosePlayStationBiosAsync()
    {
        if (await PickAsync(".bin", ".rom") is { } path)
        {
            PlayStationBiosPath = await ImportAsync(BiosKind.PlayStation, path);
            SaveIfLoaded();
        }
    }

    [RelayCommand]
    private async Task ChoosePlayStation2BiosAsync()
    {
        if (await PickAsync(".bin", ".rom") is { } path)
        {
            PlayStation2BiosPath = await ImportAsync(BiosKind.PlayStation2, path);
            await SavePlayStation2BiosPathAsync();
        }
    }

    /// <summary>
    /// A chosen BIOS is checked and copied into GameVault's own folder (<c>%LOCALAPPDATA%\GameVault\bios\</c>), so it
    /// keeps working when the original moves; the copy's path is kept. A file that fails the check keeps its own path,
    /// so the usual warning or message explains what's wrong with it.
    /// </summary>
    private async Task<string> ImportAsync(BiosKind kind, string path)
    {
        if (biosImporter is null)
        {
            return path;
        }

        var import = await Task.Run(() => biosImporter.Import(kind, path));
        return import.Succeeded && import.Path is { } copy ? copy : path;
    }

    private Task<string?> PickAsync(params string[] extensions) =>
        picker is null ? Task.FromResult<string?>(null) : picker.PickSystemFileAsync([.. extensions, "*"]);

    /// <summary>Settings → Family: profiles, daily limits and the parent PIN (SPEC §7.9).</summary>
    public FamilyViewModel? Family { get; } = family;

    /// <summary>Settings → RetroAchievements (SPEC §7.10).</summary>
    public RetroAchievementsSettingsViewModel? RetroAchievements { get; } = retroAchievements;

    /// <summary>The Settings → Library section.</summary>
    public LibrarySettingsViewModel Library { get; } = library;

    public IReadOnlyList<string> RewindMemoryOptions { get; } = ["64 MB", "128 MB", "256 MB", "512 MB", "1 GB"];

    private static readonly int[] RewindMemoryValues = [64, 128, 256, 512, 1024];

    /// <summary>Where inlretro.exe is (SPEC §8). Saved when the text box loses focus.</summary>
    [ObservableProperty]
    public partial string DumperPath { get; set; } = string.Empty;

    /// <summary>
    /// The player's own Famicom Disk System BIOS (disksys.rom). GameVault never includes it. Saved when the
    /// text box loses focus.
    /// </summary>
    [ObservableProperty]
    public partial string FdsBiosPath { get; set; } = string.Empty;

    /// <summary>The player's own Atari 5200 BIOS (5200.rom). GameVault never includes it. Saved when the text box loses focus.</summary>
    [ObservableProperty]
    public partial string Atari5200BiosPath { get; set; } = string.Empty;
    /// <summary>The player's own Sega CD BIOS. GameVault never includes it. Saved when the text box loses focus.</summary>
    [ObservableProperty]
    public partial string SegaCdBiosPath { get; set; } = string.Empty;
    /// <summary>The netplay relay server ("host:port"), or empty to connect directly. Saved when the text box loses focus.</summary>
    [ObservableProperty]
    public partial string NetplayRelayServer { get; set; } = string.Empty;

    /// <summary>The player's own 32X BIOS files. GameVault never includes them. Saved when a text box loses focus.</summary>
    [ObservableProperty]
    public partial string Sega32XGenesisBiosPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Sega32XMasterBiosPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Sega32XSlaveBiosPath { get; set; } = string.Empty;

    /// <summary>The player's own Apple II ROM (SPEC §7.11). Saved when the text box loses focus.</summary>
    [ObservableProperty]
    public partial string AppleIIRomPath { get; set; } = string.Empty;

    /// <summary>The player's own Disk II boot ROM. Saved when the text box loses focus.</summary>
    [ObservableProperty]
    public partial string AppleIIDiskRomPath { get; set; } = string.Empty;

    /// <summary>The player's own Apple II character ROM, optional. Saved when the text box loses focus.</summary>
    [ObservableProperty]
    public partial string AppleIICharacterRomPath { get; set; } = string.Empty;

    /// <summary>The Apple II monitors, in the order of <see cref="AppleIIMonitorIndex"/>.</summary>
    public IReadOnlyList<string> AppleIIMonitors { get; } = ["Colour TV", "Green monitor", "Amber monitor", "White monitor"];

    /// <summary>The Apple II's monitor; applies from the next game started.</summary>
    [ObservableProperty]
    public partial int AppleIIMonitorIndex { get; set; }

    /// <summary>The folder of the player's own Super Nintendo enhancement chip ROMs. Saved when the text box loses focus.</summary>
    [ObservableProperty]
    public partial string SnesChipRomFolder { get; set; } = string.Empty;

    /// <summary>The player's own PlayStation BIOS (SPEC §7.11). Saved when the text box loses focus.</summary>
    [ObservableProperty]
    public partial string PlayStationBiosPath { get; set; } = string.Empty;

    /// <summary>Skip the PlayStation BIOS's intro.</summary>
    [ObservableProperty]
    public partial bool PlayStationFastBoot { get; set; }

    /// <summary>PlayStation controllers start in analog mode (takes effect the next time a game starts).</summary>
    [ObservableProperty]
    public partial bool PlayStationAnalog { get; set; }

    public IReadOnlyList<string> PlayStationResolutionOptions { get; } = ["1× (original)", "2×", "3×"];

    /// <summary>The PlayStation's internal resolution, as an index into <see cref="PlayStationResolutionOptions"/>.</summary>
    [ObservableProperty]
    public partial int PlayStationResolutionIndex { get; set; }

    /// <summary>N64 games get the Expansion Pak (takes effect the next time a game starts).</summary>
    [ObservableProperty]
    public partial bool N64ExpansionPak { get; set; } = true;

    /// <summary>N64 controllers get a Rumble Pak instead of the Controller Pak (takes effect the next time a game starts).</summary>
    [ObservableProperty]
    public partial bool N64RumblePak { get; set; }

    /// <summary>The player's own PlayStation 2 BIOS (SPEC §7.11, Phase 17). Saved when the text box loses focus.</summary>
    [ObservableProperty]
    public partial string PlayStation2BiosPath { get; set; } = string.Empty;

    /// <summary>What the chosen PlayStation 2 BIOS is (its version), or a hint when none is chosen; empty when it has a problem.</summary>
    [ObservableProperty]
    public partial string PlayStation2BiosStatus { get; set; } = string.Empty;

    /// <summary>What's wrong with the chosen PlayStation 2 BIOS (missing, not 4 MB, unreadable); empty when it's fine.</summary>
    [ObservableProperty]
    public partial string PlayStation2BiosProblem { get; set; } = string.Empty;

    /// <summary>True while <see cref="PlayStation2BiosProblem"/> has something to say (Settings shows it as a warning).</summary>
    [ObservableProperty]
    public partial bool HasPlayStation2BiosProblem { get; set; }

    /// <summary>Skip the PlayStation 2 BIOS's startup screens.</summary>
    [ObservableProperty]
    public partial bool PlayStation2FastBoot { get; set; }

    /// <summary>Index into <see cref="RewindMemoryOptions"/>.</summary>
    [ObservableProperty]
    public partial int RewindMemoryIndex { get; set; } = 2;

    /// <summary>The Settings → Input section.</summary>
    public InputSettingsViewModel Input { get; } = input;

    private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverscanIndex))]
    public partial bool CropOverscan { get; set; } = true;

    /// <summary>Lines hidden at the top and at the bottom while <see cref="CropOverscan"/> is on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverscanIndex))]
    public partial int OverscanCropLines { get; set; } = 8;

    /// <summary>How much of the screen edges to hide (SPEC §5.2), matching <see cref="OverscanIndex"/>.</summary>
    public IReadOnlyList<string> OverscanOptions { get; } =
        ["Show the whole picture", "4 lines", "8 lines, like most TVs (default)", "12 lines", "16 lines"];

    private static readonly int[] OverscanValues = [0, 4, 8, 12, 16];

    /// <summary>Settings → Video → Hide screen edges, as an index into <see cref="OverscanOptions"/> (GV-020).</summary>
    public int OverscanIndex
    {
        get => CropOverscan ? Math.Max(1, Array.IndexOf(OverscanValues, OverscanCropLines)) : 0;
        set
        {
            if (value < 0 || value >= OverscanValues.Length || value == OverscanIndex)
            {
                return;
            }

            if (value > 0)
            {
                OverscanCropLines = OverscanValues[value];
            }

            CropOverscan = value > 0;
            OnPropertyChanged();
            SaveIfLoaded();
        }
    }

    /// <summary>A <see cref="Services.DisplayPreset"/> as an index, for a RadioButtons group.</summary>
    [ObservableProperty]
    public partial int DisplayPresetIndex { get; set; }

    [ObservableProperty]
    public partial bool TvPixelShape { get; set; }

    /// <summary>0–3. Bound to a RadioButtons group by index.</summary>
    [ObservableProperty]
    public partial int RunAheadFrames { get; set; } = 1;

    [ObservableProperty]
    public partial bool PauseWhenInactive { get; set; } = true;

    /// <summary>Start games in full screen.</summary>
    [ObservableProperty]
    public partial bool PlayFullScreen { get; set; }

    [ObservableProperty]
    public partial bool AllowOppositeDirections { get; set; }

    /// <summary>Keep cartridge saves next to the game files (SPEC §4.5 opt-in, GV-012).</summary>
    [ObservableProperty]
    public partial bool SavesNextToRom { get; set; }

    partial void OnSavesNextToRomChanged(bool value) => SaveIfLoaded();

    /// <summary>Both stereo channels on both speakers.</summary>
    [ObservableProperty]
    public partial bool MonoAudio { get; set; }

    partial void OnMonoAudioChanged(bool value) => SaveIfLoaded();

    /// <summary>0–100. Bound to a Slider (double), so it is a double here.</summary>
    [ObservableProperty]
    public partial double Volume { get; set; } = 80;

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        _isLoaded = false;
        var settings = await settingsStore.LoadAsync(cancellationToken);
        CropOverscan = settings.CropOverscan;
        OverscanCropLines = Array.IndexOf(OverscanValues, settings.OverscanCropLines) > 0 ? settings.OverscanCropLines : 8;
        DisplayPresetIndex = (int)settings.DisplayPreset;
        TvPixelShape = settings.TvPixelShape;
        RunAheadFrames = Math.Clamp(settings.RunAheadFrames, 0, AppSettings.MaxRunAheadFrames);
        PauseWhenInactive = settings.PauseWhenInactive;
        PlayFullScreen = settings.PlayFullScreen;
        AllowOppositeDirections = settings.AllowOppositeDirections;
        SavesNextToRom = settings.SavesNextToRom;
        Volume = Math.Clamp(settings.Volume, 0, 100);
        MonoAudio = settings.MonoAudio;
        RewindMemoryIndex = Math.Max(0, Array.IndexOf(RewindMemoryValues, settings.RewindMemoryMb) is var i and >= 0 ? i : 2);
        DumperPath = settings.DumperPath;
        FdsBiosPath = settings.FdsBiosPath;
        Atari5200BiosPath = settings.Atari5200BiosPath;
        SegaCdBiosPath = settings.SegaCdBiosPath;
        Sega32XGenesisBiosPath = settings.Sega32XGenesisBiosPath;
        Sega32XMasterBiosPath = settings.Sega32XMasterBiosPath;
        Sega32XSlaveBiosPath = settings.Sega32XSlaveBiosPath;
        NetplayRelayServer = settings.NetplayRelayServer;
        AppleIIRomPath = settings.AppleIIRomPath;
        AppleIIDiskRomPath = settings.AppleIIDiskRomPath;
        AppleIICharacterRomPath = settings.AppleIICharacterRomPath;
        AppleIIMonitorIndex = Math.Clamp(settings.AppleIIMonitor, 0, AppleIIMonitors.Count - 1);
        PlayStationBiosPath = settings.PlayStationBiosPath;
        SnesChipRomFolder = settings.SnesChipRomFolder;
        PlayStationFastBoot = settings.PlayStationFastBoot;
        PlayStationAnalog = settings.PlayStationAnalog;
        PlayStationResolutionIndex = Math.Clamp(settings.PlayStationInternalResolution, 1, 3) - 1;
        N64ExpansionPak = settings.N64ExpansionPak;
        N64RumblePak = settings.N64RumblePak;
        PlayStation2BiosPath = settings.PlayStation2BiosPath;
        PlayStation2FastBoot = settings.PlayStation2FastBoot;
        await CheckPlayStation2BiosAsync();
        _isLoaded = true;
        await Library.LoadCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private Task ResetToDefaultsAsync()
    {
        var defaults = new AppSettings();
        _isLoaded = false;
        CropOverscan = defaults.CropOverscan;
        OverscanCropLines = defaults.OverscanCropLines;
        DisplayPresetIndex = (int)defaults.DisplayPreset;
        TvPixelShape = defaults.TvPixelShape;
        RunAheadFrames = defaults.RunAheadFrames;
        PauseWhenInactive = defaults.PauseWhenInactive;
        PlayFullScreen = defaults.PlayFullScreen;
        AllowOppositeDirections = defaults.AllowOppositeDirections;
        Volume = defaults.Volume;
        MonoAudio = defaults.MonoAudio;
        RewindMemoryIndex = Array.IndexOf(RewindMemoryValues, defaults.RewindMemoryMb);
        _isLoaded = true;
        return SaveAsync();
    }

    partial void OnCropOverscanChanged(bool value) => SaveIfLoaded();

    partial void OnDisplayPresetIndexChanged(int value)
    {
        if (Enum.IsDefined((DisplayPreset)value))
        {
            SaveIfLoaded();
        }
    }

    partial void OnTvPixelShapeChanged(bool value) => SaveIfLoaded();

    partial void OnRunAheadFramesChanged(int value)
    {
        // RadioButtons reports -1 while its selection is being cleared; ignore that.
        if (value is < 0 or > AppSettings.MaxRunAheadFrames)
        {
            return;
        }

        SaveIfLoaded();
    }

    partial void OnPauseWhenInactiveChanged(bool value) => SaveIfLoaded();

    partial void OnPlayFullScreenChanged(bool value) => SaveIfLoaded();

    partial void OnAllowOppositeDirectionsChanged(bool value) => SaveIfLoaded();

    partial void OnVolumeChanged(double value) => SaveIfLoaded();

    /// <summary>Called when the dumper path box loses focus (saving on every keystroke would be wasteful).</summary>
    [RelayCommand]
    private void SaveDumperPath() => SaveIfLoaded();

    [RelayCommand]
    private void SaveFdsBiosPath() => SaveIfLoaded();

    [RelayCommand]
    private void SaveAtari5200BiosPath() => SaveIfLoaded();

    [RelayCommand]
    private void SaveSegaCdBiosPath() => SaveIfLoaded();

    [RelayCommand]
    private void SaveSega32XBiosPaths() => SaveIfLoaded();

    [RelayCommand]
    private void SaveNetplayRelayServer() => SaveIfLoaded();

    [RelayCommand]
    private void SaveAppleRomPaths() => SaveIfLoaded();

    partial void OnAppleIIMonitorIndexChanged(int value) => SaveIfLoaded();

    [RelayCommand]
    private void SavePlayStationBiosPath() => SaveIfLoaded();

    [RelayCommand]
    private void SaveSnesChipRomFolder() => SaveIfLoaded();

    partial void OnPlayStationFastBootChanged(bool value) => SaveIfLoaded();

    partial void OnPlayStationAnalogChanged(bool value) => SaveIfLoaded();

    partial void OnPlayStationResolutionIndexChanged(int value) => SaveIfLoaded();

    partial void OnN64ExpansionPakChanged(bool value) => SaveIfLoaded();

    partial void OnN64RumblePakChanged(bool value) => SaveIfLoaded();

    /// <summary>Called when the PS2 BIOS path box loses focus: saves it and checks the file (size and version).</summary>
    [RelayCommand]
    private async Task SavePlayStation2BiosPathAsync()
    {
        SaveIfLoaded();
        await CheckPlayStation2BiosAsync();
    }

    partial void OnPlayStation2FastBootChanged(bool value) => SaveIfLoaded();

    private async Task CheckPlayStation2BiosAsync()
    {
        var path = PlayStation2BiosPath;
        var check = await Task.Run(() => GameVault.Library.Bios.Ps2Bios.Check(path));
        var fine = check.IsValid || path.Trim().Trim('"').Length == 0;
        PlayStation2BiosStatus = fine ? check.Message : string.Empty;
        PlayStation2BiosProblem = fine ? string.Empty : check.Message;
        HasPlayStation2BiosProblem = !fine;
    }

    partial void OnRewindMemoryIndexChanged(int value)
    {
        if (value >= 0 && value < RewindMemoryValues.Length)
        {
            SaveIfLoaded();
        }
    }

    private void SaveIfLoaded()
    {
        if (_isLoaded)
        {
            _ = SaveAsync();
        }
    }

    private async Task SaveAsync()
    {
        // Keep fields this page doesn't edit (input preset, pins).
        var current = await settingsStore.LoadAsync();
        await settingsStore.SaveAsync(current with
        {
            CropOverscan = CropOverscan,
            OverscanCropLines = OverscanCropLines,
            DisplayPreset = Enum.IsDefined((DisplayPreset)DisplayPresetIndex) ? (DisplayPreset)DisplayPresetIndex : current.DisplayPreset,
            TvPixelShape = TvPixelShape,
            RunAheadFrames = Math.Clamp(RunAheadFrames, 0, AppSettings.MaxRunAheadFrames),
            PauseWhenInactive = PauseWhenInactive,
            PlayFullScreen = PlayFullScreen,
            AllowOppositeDirections = AllowOppositeDirections,
            SavesNextToRom = SavesNextToRom,
            Volume = (int)Math.Round(Math.Clamp(Volume, 0, 100)),
            MonoAudio = MonoAudio,
            DumperPath = DumperPath.Trim().Trim('"'),
            FdsBiosPath = FdsBiosPath.Trim().Trim('"'),
            Atari5200BiosPath = Atari5200BiosPath.Trim().Trim('"'),
            SegaCdBiosPath = SegaCdBiosPath.Trim().Trim('"'),
            Sega32XGenesisBiosPath = Sega32XGenesisBiosPath.Trim().Trim('"'),
            Sega32XMasterBiosPath = Sega32XMasterBiosPath.Trim().Trim('"'),
            Sega32XSlaveBiosPath = Sega32XSlaveBiosPath.Trim().Trim('"'),
            NetplayRelayServer = NetplayRelayServer.Trim(),
            AppleIIRomPath = AppleIIRomPath.Trim().Trim('"'),
            AppleIIDiskRomPath = AppleIIDiskRomPath.Trim().Trim('"'),
            AppleIICharacterRomPath = AppleIICharacterRomPath.Trim().Trim('"'),
            AppleIIMonitor = Math.Clamp(AppleIIMonitorIndex, 0, AppleIIMonitors.Count - 1),
            PlayStationBiosPath = PlayStationBiosPath.Trim().Trim('"'),
            SnesChipRomFolder = SnesChipRomFolder.Trim().Trim('"'),
            PlayStationFastBoot = PlayStationFastBoot,
            PlayStationAnalog = PlayStationAnalog,
            PlayStationInternalResolution = Math.Clamp(PlayStationResolutionIndex, 0, 2) + 1,
            N64ExpansionPak = N64ExpansionPak,
            N64RumblePak = N64RumblePak,
            PlayStation2BiosPath = PlayStation2BiosPath.Trim().Trim('"'),
            PlayStation2FastBoot = PlayStation2FastBoot,
            RewindMemoryMb = RewindMemoryIndex >= 0 && RewindMemoryIndex < RewindMemoryValues.Length
                ? RewindMemoryValues[RewindMemoryIndex]
                : current.RewindMemoryMb,
        });
    }
}
