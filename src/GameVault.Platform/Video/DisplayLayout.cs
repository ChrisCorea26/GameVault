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

    /// <param name="frameWidth">The frame's width (256 except for systems like the Apple II).</param>
    /// <param name="frameHeight">The frame's height; overscan cropping and the TV pixel shape only apply to 256×240 frames.</param>
    /// <param name="framePixelAspect">Other frames' own pixel shape (width over height).</param>
    public static DisplayRect Compute(
        int outputWidth,
        int outputHeight,
        DisplayPreset preset,
        bool cropOverscan,
        bool tvPixelShape,
        int frameWidth = FrameWidth,
        int frameHeight = FrameHeight,
        double framePixelAspect = 1.0)
    {
        outputWidth = Math.Max(1, outputWidth);
        outputHeight = Math.Max(1, outputHeight);
        var standard = frameWidth == FrameWidth && frameHeight == FrameHeight;
        var sourceTop = standard && cropOverscan ? OverscanLines : 0;
        var sourceHeight = frameHeight - 2 * sourceTop;
        var pixelAspect = standard ? (tvPixelShape ? TvPixelAspect : 1.0) : framePixelAspect;
        var square = Math.Abs(pixelAspect - 1.0) < 0.001;
        var naturalWidth = frameWidth * pixelAspect;

        double width, height;
        if (preset == DisplayPreset.SharpPixels)
        {
            // Vertical scale is a whole number; horizontal follows the pixel shape (whole too when square).
            var scale = Math.Max(1, (int)Math.Min(outputWidth / naturalWidth, (double)outputHeight / sourceHeight));
            height = sourceHeight * scale;
            width = square ? frameWidth * scale : Math.Round(naturalWidth * scale);
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
