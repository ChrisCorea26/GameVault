namespace GameVault.Platform.Audio;

/// <summary>
/// Lock-free single-producer/single-consumer ring of float samples: the emulation thread writes,
/// the audio device thread reads (SPEC §5.1).
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _samples;
    private readonly int _mask;
    private long _written;
    private long _read;

    /// <param name="capacity">Rounded up to a power of two.</param>
    public AudioRingBuffer(int capacity)
    {
        var size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)capacity);
        _samples = new float[size];
        _mask = size - 1;
    }

    public int Capacity => _samples.Length;

    /// <summary>Samples waiting to be read.</summary>
    public int Count => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _read));

    /// <summary>Writes as many samples as fit; returns how many were written.</summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        var written = Volatile.Read(ref _written);
        var free = _samples.Length - (int)(written - Volatile.Read(ref _read));
        var count = Math.Min(free, samples.Length);
        for (var i = 0; i < count; i++)
        {
            _samples[(written + i) & _mask] = samples[i];
        }

        Volatile.Write(ref _written, written + count);
        return count;
    }

    /// <summary>Reads up to <paramref name="destination"/>.Length samples; returns how many were read.</summary>
    public int Read(Span<float> destination)
    {
        var read = Volatile.Read(ref _read);
        var available = (int)(Volatile.Read(ref _written) - read);
        var count = Math.Min(available, destination.Length);
        for (var i = 0; i < count; i++)
        {
            destination[i] = _samples[(read + i) & _mask];
        }

        Volatile.Write(ref _read, read + count);
        return count;
    }

    /// <summary>Drops everything buffered (for example after a pause, so stale sound isn't played).</summary>
    public void Clear() => Volatile.Write(ref _read, Volatile.Read(ref _written));
}
