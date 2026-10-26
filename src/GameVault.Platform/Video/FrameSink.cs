using GameVault.Core.Ppu;

namespace GameVault.Platform.Video;

/// <summary>
/// The emulation thread's video output: copies each finished frame into a triple buffer and wakes the
/// renderer, which presents it to the swap chain on its own thread (SPEC §5.1, §5.2).
/// </summary>
public sealed class FrameSink : IVideoSink
{
    private readonly TripleBuffer<VideoFrame> _frames = new(() => new VideoFrame());
    private readonly AutoResetEvent _frameReady = new(false);
    private uint[] _palette = NtscPalette.Generate();

    /// <summary>Signaled whenever a new frame is published.</summary>
    public WaitHandle FrameReady => _frameReady;

    /// <summary>The palette of the most recent frame, as ARGB (512 entries: 64 colors × 8 emphasis).</summary>
    public ReadOnlySpan<uint> Palette => _palette;

    public void Present(ReadOnlySpan<ushort> indexedPixels, ReadOnlySpan<uint> paletteArgb, int width = IVideoSink.Width, int height = IVideoSink.Height, double pixelAspect = 1.0)
    {
        var frame = _frames.BackBuffer;
        frame.Width = width;
        frame.Height = height;
        frame.PixelAspect = pixelAspect;
        indexedPixels[..(width * height)].CopyTo(frame.Pixels);
        if (!paletteArgb.IsEmpty && !paletteArgb.SequenceEqual(_palette))
        {
            _palette = paletteArgb.ToArray();
        }

        _frames.Publish();
        _frameReady.Set();
    }

    /// <summary>Renderer side: gets the newest frame, with its size, if a new one arrived since the last call.</summary>
    public bool TryGetVideoFrame(out VideoFrame frame) => _frames.TryAcquire(out frame);

    /// <summary>A copy of the newest frame's pixels, at the frame's size (tests and diagnostics; it allocates).</summary>
    public bool TryGetFrame(out ushort[] frame)
    {
        var acquired = _frames.TryAcquire(out var video);
        frame = video.Span.ToArray();
        return acquired;
    }
}
