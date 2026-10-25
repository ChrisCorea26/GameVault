using GameVault.Core.Ppu;

namespace GameVault.Platform.Video;

/// <summary>
/// The emulation thread's video output: copies each finished frame into a triple buffer and wakes the
/// renderer, which presents it to the swap chain on its own thread (SPEC §5.1, §5.2).
/// </summary>
public sealed class FrameSink : IVideoSink
{
    private readonly TripleBuffer<ushort[]> _frames = new(() => new ushort[IVideoSink.Width * IVideoSink.Height]);
    private readonly AutoResetEvent _frameReady = new(false);
    private uint[] _palette = NtscPalette.Generate();

    /// <summary>Signaled whenever a new frame is published.</summary>
    public WaitHandle FrameReady => _frameReady;

    /// <summary>The palette of the most recent frame, as ARGB (512 entries: 64 colors × 8 emphasis).</summary>
    public ReadOnlySpan<uint> Palette => _palette;

    public void Present(ReadOnlySpan<ushort> indexedPixels, ReadOnlySpan<uint> paletteArgb)
    {
        indexedPixels.CopyTo(_frames.BackBuffer);
        if (!paletteArgb.IsEmpty && !paletteArgb.SequenceEqual(_palette))
        {
            _palette = paletteArgb.ToArray();
        }

        _frames.Publish();
        _frameReady.Set();
    }

    /// <summary>Renderer side: gets the newest frame, if a new one arrived since the last call.</summary>
    public bool TryGetFrame(out ushort[] frame) => _frames.TryAcquire(out frame);
}
