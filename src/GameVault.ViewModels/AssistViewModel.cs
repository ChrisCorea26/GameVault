using CommunityToolkit.Mvvm.ComponentModel;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>
/// Assist mode (SPEC §7.6): off by default and only for this session. Every change is applied right away.
/// </summary>
public sealed partial class AssistViewModel(IEmulatorService emulator) : ObservableObject
{
    private static readonly int[] Speeds = [100, 50, 25];

    public IReadOnlyList<string> SpeedOptions { get; } = ["Normal speed", "Half speed", "Quarter speed"];

    public IReadOnlyList<string> PaletteOptions { get; } = ["Standard colors", "Red-green (deuteranopia)", "Red-green (protanopia)", "Blue-yellow (tritanopia)"];

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial int SpeedIndex { get; set; }

    [ObservableProperty]
    public partial bool UnlimitedRewind { get; set; }

    [ObservableProperty]
    public partial bool RemoveFlicker { get; set; }

    [ObservableProperty]
    public partial int PaletteIndex { get; set; }

    /// <summary>What is sent to the emulator (also used by tests).</summary>
    public AssistOptions Options => IsEnabled
        ? new AssistOptions(
            Speeds[Math.Clamp(SpeedIndex, 0, Speeds.Length - 1)],
            UnlimitedRewind,
            RemoveFlicker,
            (ColorVision)Math.Clamp(PaletteIndex, 0, 3))
        : AssistOptions.Off;

    partial void OnIsEnabledChanged(bool value) => Apply();

    partial void OnSpeedIndexChanged(int value) => Apply();

    partial void OnUnlimitedRewindChanged(bool value) => Apply();

    partial void OnRemoveFlickerChanged(bool value) => Apply();

    partial void OnPaletteIndexChanged(int value) => Apply();

    private void Apply() => emulator.SetAssist(Options);
}
