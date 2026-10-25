namespace GameVault.Platform.Audio;

/// <summary>An audio device fed from an <see cref="AudioRingBuffer"/> of mono samples.</summary>
public interface IAudioOutput : IDisposable
{
    int SampleRate { get; }

    /// <summary>Mono samples queued and not yet played.</summary>
    int BufferedSamples { get; }

    /// <summary>How many times the device needed samples while playing and the buffer was empty.</summary>
    long Underruns { get; }

    /// <summary>0 to 1.</summary>
    float Volume { get; set; }

    /// <summary>While paused the device plays silence and empty buffers don't count as underruns.</summary>
    bool Paused { get; set; }

    /// <summary>Starts the device. Returns false if no device is available (the host then falls back to timer pacing).</summary>
    bool TryStart();

    /// <summary>Queues samples; returns how many fit.</summary>
    int Write(ReadOnlySpan<float> samples);

    /// <summary>Drops queued audio (after pause or fast-forward).</summary>
    void Flush();
}
