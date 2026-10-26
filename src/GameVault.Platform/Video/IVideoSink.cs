namespace GameVault.Platform.Video;

/// <summary>
/// Receives finished frames from the emulation thread (SPEC §5.2). Implementations present them
/// directly to a swap chain and must never touch XAML objects.
/// Phase 2 adds the Win2D sink; Phase 4 adds the Direct3D 11 sink.
/// </summary>
public interface IVideoSink
{
    public const int Width = 256;
    public const int Height = 240;

    /// <summary>The largest frame any system produces: 640 wide (the PlayStation, N64 and PS2), 512 lines (the PS2 in PAL).</summary>
    public const int MaxWidth = 640;
    public const int MaxHeight = 512;

    /// <summary>
    /// Presents one frame: <paramref name="width"/>×<paramref name="height"/> palette indices (256×240 for the NES,
    /// with emphasis bits). <paramref name="paletteArgb"/> maps each index to a 32-bit ARGB color.
    /// <paramref name="pixelAspect"/> is a pixel's width over its height on the original screen (1 for the NES
    /// and Sega consoles, whose TV shape is a display setting).
    /// </summary>
    void Present(ReadOnlySpan<ushort> indexedPixels, ReadOnlySpan<uint> paletteArgb, int width = Width, int height = Height, double pixelAspect = 1.0);
}

/// <summary>One frame in the <see cref="FrameSink"/>: palette indices at the frame's own size.</summary>
public sealed class VideoFrame
{
    public ushort[] Pixels { get; } = new ushort[IVideoSink.MaxWidth * IVideoSink.MaxHeight];

    public int Width { get; set; } = IVideoSink.Width;

    public int Height { get; set; } = IVideoSink.Height;

    public double PixelAspect { get; set; } = 1.0;

    /// <summary>True for the NES-sized 256×240 frame, whose overscan can be cropped.</summary>
    public bool IsStandard => Width == IVideoSink.Width && Height == IVideoSink.Height;

    public ReadOnlySpan<ushort> Span => Pixels.AsSpan(0, Width * Height);
}
