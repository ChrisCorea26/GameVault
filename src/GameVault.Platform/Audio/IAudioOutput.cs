namespace GameVault.Platform.Audio;

/// <summary>An audio device fed from an <see cref="AudioRingBuffer"/> of interleaved stereo frames.</summary>
public interface IAudioOutput : IDisposable
{
    int SampleRate { get; }

    /// <summary>Frames queued and not yet played.</summary>
    int BufferedFrames { get; }

    /// <summary>How many times the device needed samples while playing and the buffer was empty.</summary>
    long Underruns { get; }

    /// <summary>0 to 1.</summary>
    float Volume { get; set; }

    /// <summary>Plays both channels' average on both speakers (Settings → Audio → Mono).</summary>
    bool Mono { get; set; }

    /// <summary>While paused the device plays silence and empty buffers don't count as underruns.</summary>
    bool Paused { get; set; }

    /// <summary>Starts the device. Returns false if no device is available (the host then falls back to timer pacing).</summary>
    bool TryStart();

    /// <summary>Queues interleaved left/right samples; returns how many frames fit.</summary>
    int Write(ReadOnlySpan<float> frames);

    /// <summary>Drops queued audio (after pause or fast-forward).</summary>
    void Flush();
}
