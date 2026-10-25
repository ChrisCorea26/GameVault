namespace GameVault.ViewModels.Services;

/// <summary>Colorblind-friendly palettes (SPEC §7.6): colors are shifted so red/green (or blue/yellow) differences stay visible.</summary>
public enum ColorVision
{
    Standard,
    Deuteranopia,
    Protanopia,
    Tritanopia,
}

/// <summary>
/// Assist mode (SPEC §7.6): a per-session toggle, never forced. Slow motion slows the game and its sound
/// together; unlimited rewind lifts the memory cap; flicker removal draws every sprite on a line.
/// </summary>
public sealed record AssistOptions(
    int SpeedPercent = 100,
    bool UnlimitedRewind = false,
    bool RemoveFlicker = false,
    ColorVision Palette = ColorVision.Standard)
{
    public static AssistOptions Off { get; } = new();
}
