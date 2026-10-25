using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>The Settings screen. Every change is saved immediately; there is no Apply button.</summary>
public sealed partial class SettingsViewModel(ISettingsStore settingsStore) : ObservableObject
{
    private bool _isLoaded;

    [ObservableProperty]
    public partial bool CropOverscan { get; set; } = true;

    /// <summary>0–3. Bound to a RadioButtons group by index.</summary>
    [ObservableProperty]
    public partial int RunAheadFrames { get; set; } = 1;

    [ObservableProperty]
    public partial bool PauseWhenInactive { get; set; } = true;

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
        RunAheadFrames = Math.Clamp(settings.RunAheadFrames, 0, AppSettings.MaxRunAheadFrames);
        PauseWhenInactive = settings.PauseWhenInactive;
        AllowOppositeDirections = settings.AllowOppositeDirections;
        Volume = Math.Clamp(settings.Volume, 0, 100);
        _isLoaded = true;
    }

    [RelayCommand]
    private Task ResetToDefaultsAsync()
    {
        var defaults = new AppSettings();
        _isLoaded = false;
        CropOverscan = defaults.CropOverscan;
        RunAheadFrames = defaults.RunAheadFrames;
        PauseWhenInactive = defaults.PauseWhenInactive;
        AllowOppositeDirections = defaults.AllowOppositeDirections;
        Volume = defaults.Volume;
        _isLoaded = true;
        return SaveAsync();
    }

    partial void OnCropOverscanChanged(bool value) => SaveIfLoaded();

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

    partial void OnAllowOppositeDirectionsChanged(bool value) => SaveIfLoaded();

    partial void OnVolumeChanged(double value) => SaveIfLoaded();

    private void SaveIfLoaded()
    {
        if (_isLoaded)
        {
            _ = SaveAsync();
        }
    }

    private Task SaveAsync() => settingsStore.SaveAsync(new AppSettings
    {
        CropOverscan = CropOverscan,
        RunAheadFrames = Math.Clamp(RunAheadFrames, 0, AppSettings.MaxRunAheadFrames),
        PauseWhenInactive = PauseWhenInactive,
        AllowOppositeDirections = AllowOppositeDirections,
        Volume = (int)Math.Round(Math.Clamp(Volume, 0, 100)),
    });
}
