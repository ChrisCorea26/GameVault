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

    /// <summary>
    /// Presents one frame. <paramref name="indexedPixels"/> holds 256×240 palette indices (with emphasis
    /// bits). <paramref name="paletteArgb"/> maps each index to a 32-bit ARGB color.
    /// </summary>
    void Present(ReadOnlySpan<ushort> indexedPixels, ReadOnlySpan<uint> paletteArgb);
}
