using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>The Settings screen. Every change is saved immediately; there is no Apply button.</summary>
public sealed partial class SettingsViewModel(
    ISettingsStore settingsStore, InputSettingsViewModel input, LibrarySettingsViewModel library, FamilyViewModel? family = null)
    : ObservableObject
{
    /// <summary>Settings → Family: profiles, daily limits and the parent PIN (SPEC §7.9).</summary>
    public FamilyViewModel? Family { get; } = family;

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

    /// <summary>Index into <see cref="RewindMemoryOptions"/>.</summary>
    [ObservableProperty]
    public partial int RewindMemoryIndex { get; set; } = 2;

    /// <summary>The Settings → Input section.</summary>
    public InputSettingsViewModel Input { get; } = input;

    private bool _isLoaded;

    [ObservableProperty]
    public partial bool CropOverscan { get; set; } = true;

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

    /// <summary>0–100. Bound to a Slider (double), so it is a double here.</summary>
    [ObservableProperty]
    public partial double Volume { get; set; } = 80;

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        _isLoaded = false;
        var settings = await settingsStore.LoadAsync(cancellationToken);
        CropOverscan = settings.CropOverscan;
        DisplayPresetIndex = (int)settings.DisplayPreset;
        TvPixelShape = settings.TvPixelShape;
        RunAheadFrames = Math.Clamp(settings.RunAheadFrames, 0, AppSettings.MaxRunAheadFrames);
        PauseWhenInactive = settings.PauseWhenInactive;
        PlayFullScreen = settings.PlayFullScreen;
        AllowOppositeDirections = settings.AllowOppositeDirections;
        Volume = Math.Clamp(settings.Volume, 0, 100);
        RewindMemoryIndex = Math.Max(0, Array.IndexOf(RewindMemoryValues, settings.RewindMemoryMb) is var i and >= 0 ? i : 2);
        DumperPath = settings.DumperPath;
        FdsBiosPath = settings.FdsBiosPath;
        _isLoaded = true;
        await Library.LoadCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private Task ResetToDefaultsAsync()
    {
        var defaults = new AppSettings();
        _isLoaded = false;
        CropOverscan = defaults.CropOverscan;
        DisplayPresetIndex = (int)defaults.DisplayPreset;
        TvPixelShape = defaults.TvPixelShape;
        RunAheadFrames = defaults.RunAheadFrames;
        PauseWhenInactive = defaults.PauseWhenInactive;
        PlayFullScreen = defaults.PlayFullScreen;
        AllowOppositeDirections = defaults.AllowOppositeDirections;
        Volume = defaults.Volume;
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
            DisplayPreset = Enum.IsDefined((DisplayPreset)DisplayPresetIndex) ? (DisplayPreset)DisplayPresetIndex : current.DisplayPreset,
            TvPixelShape = TvPixelShape,
            RunAheadFrames = Math.Clamp(RunAheadFrames, 0, AppSettings.MaxRunAheadFrames),
            PauseWhenInactive = PauseWhenInactive,
            PlayFullScreen = PlayFullScreen,
            AllowOppositeDirections = AllowOppositeDirections,
            Volume = (int)Math.Round(Math.Clamp(Volume, 0, 100)),
            DumperPath = DumperPath.Trim().Trim('"'),
            FdsBiosPath = FdsBiosPath.Trim().Trim('"'),
            RewindMemoryMb = RewindMemoryIndex >= 0 && RewindMemoryIndex < RewindMemoryValues.Length
                ? RewindMemoryValues[RewindMemoryIndex]
                : current.RewindMemoryMb,
        });
    }
}
