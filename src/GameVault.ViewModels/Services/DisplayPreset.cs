namespace GameVault.ViewModels.Services;

/// <summary>How the picture is scaled and filtered (SPEC §5.2). Shown to users with plain names.</summary>
public enum DisplayPreset
{
    /// <summary>Whole-number scaling, crisp pixels.</summary>
    SharpPixels,

    /// <summary>Fills the space with bilinear filtering.</summary>
    Smooth,

    /// <summary>Scanlines, a light aperture mask and a little bloom.</summary>
    LivingRoomCrt,

    /// <summary>A stronger mask and screen curvature.</summary>
    ArcadeMonitor,
}
