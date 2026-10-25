using GameVault.ViewModels.Services;

namespace GameVault.Platform.Video;

/// <summary>Where the picture goes in the output, in physical pixels, and which source lines it shows.</summary>
public readonly record struct DisplayRect(int X, int Y, int Width, int Height, int SourceTop, int SourceHeight);

/// <summary>
/// Picture placement for each display preset (SPEC §5.2). Pure math, shared by the renderers and tested on
/// its own. Sharp Pixels uses whole-number scales so every NES pixel is the same size; the other presets
/// fill the space at the right shape.
/// </summary>
public static class DisplayLayout
{
    public const int FrameWidth = 256;
    public const int FrameHeight = 240;
    public const int OverscanLines = 8;

    /// <summary>NES pixels on a TV were a little wider than tall: 8:7 (NESdev Wiki: "Overscan", PAR).</summary>
    public const double TvPixelAspect = 8.0 / 7.0;

    public static DisplayRect Compute(int outputWidth, int outputHeight, DisplayPreset preset, bool cropOverscan, bool tvPixelShape)
    {
        outputWidth = Math.Max(1, outputWidth);
        outputHeight = Math.Max(1, outputHeight);
        var sourceTop = cropOverscan ? OverscanLines : 0;
        var sourceHeight = FrameHeight - 2 * sourceTop;
        var pixelAspect = tvPixelShape ? TvPixelAspect : 1.0;
        var naturalWidth = FrameWidth * pixelAspect;

        double width, height;
        if (preset == DisplayPreset.SharpPixels)
        {
            // Vertical scale is a whole number; horizontal follows the pixel shape (whole too when square).
            var scale = Math.Max(1, (int)Math.Min(outputWidth / naturalWidth, (double)outputHeight / sourceHeight));
            height = sourceHeight * scale;
            width = tvPixelShape ? Math.Round(naturalWidth * scale) : FrameWidth * scale;
        }
        else
        {
            var scale = Math.Min(outputWidth / naturalWidth, (double)outputHeight / sourceHeight);
            width = Math.Floor(naturalWidth * scale);
            height = Math.Floor(sourceHeight * scale);
        }

        var w = (int)Math.Min(width, outputWidth);
        var h = (int)Math.Min(height, outputHeight);
        return new DisplayRect((outputWidth - w) / 2, (outputHeight - h) / 2, w, h, sourceTop, sourceHeight);
    }
}
