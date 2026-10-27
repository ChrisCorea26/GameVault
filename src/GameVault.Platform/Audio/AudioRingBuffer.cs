namespace GameVault.Platform.Audio;

/// <summary>
/// Lock-free single-producer/single-consumer ring of audio frames (one float per channel, interleaved): the emulation
/// thread writes, the audio device thread reads (SPEC §5.1). Counts are in frames; only whole frames move.
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _samples;
    private readonly int _mask;
    private long _written; // in samples, always a whole number of frames
    private long _read;

    /// <param name="capacity">Frames, rounded up to a power of two.</param>
    /// <param name="channels">Samples per frame: 2 for interleaved stereo.</param>
    public AudioRingBuffer(int capacity, int channels = 2)
    {
        Channels = channels;
        var size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(capacity * channels));
        _samples = new float[size];
        _mask = size - 1;
    }

    public int Channels { get; }

    /// <summary>Frames that fit.</summary>
    public int Capacity => _samples.Length / Channels;

    /// <summary>Frames waiting to be read.</summary>
    public int Count => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _read)) / Channels;

    /// <summary>Writes as many whole frames of <paramref name="samples"/> as fit; returns how many frames were written.</summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        var written = Volatile.Read(ref _written);
        var free = _samples.Length - (int)(written - Volatile.Read(ref _read));
        var count = Math.Min(free, samples.Length) / Channels * Channels;
        for (var i = 0; i < count; i++)
        {
            _samples[(written + i) & _mask] = samples[i];
        }

        Volatile.Write(ref _written, written + count);
        return count / Channels;
    }

    /// <summary>Reads up to <paramref name="destination"/>.Length / <see cref="Channels"/> frames; returns how many frames were read.</summary>
    public int Read(Span<float> destination)
    {
        var read = Volatile.Read(ref _read);
        var available = (int)(Volatile.Read(ref _written) - read);
        var count = Math.Min(available, destination.Length) / Channels * Channels;
        for (var i = 0; i < count; i++)
        {
            destination[i] = _samples[(read + i) & _mask];
        }

        Volatile.Write(ref _read, read + count);
        return count / Channels;
    }

    /// <summary>Drops everything buffered (for example after a pause, so stale sound isn't played).</summary>
    public void Clear() => Volatile.Write(ref _read, Volatile.Read(ref _written));
}
