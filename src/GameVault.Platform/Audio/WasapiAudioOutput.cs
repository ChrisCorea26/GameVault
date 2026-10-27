using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GameVault.Platform.Audio;

/// <summary>
/// WASAPI shared-mode output through NAudio's <see cref="WasapiPlayer"/>: 48 kHz stereo float (SPEC §5.3), or both
/// channels' average on both speakers when <see cref="Mono"/> is set. The device thread (MMCSS "Games" priority, low-latency where
/// IAudioClient3 allows it) pulls samples from the ring buffer.
/// </summary>
public sealed class WasapiAudioOutput : IAudioOutput, IWaveProvider
{
    private const int DeviceLatencyMs = 20;

    private readonly AudioRingBuffer _ring;
    private readonly float[] _frames = new float[8192 * 2];
    private WasapiPlayer? _device;
    private long _underruns;
    private volatile bool _playing;

    public WasapiAudioOutput(int sampleRate = 48000)
    {
        SampleRate = sampleRate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _ring = new AudioRingBuffer(sampleRate / 4, channels: 2); // 250 ms of headroom
    }

    public int SampleRate { get; }

    public WaveFormat WaveFormat { get; }

    public int BufferedFrames => _ring.Count;

    public long Underruns => Interlocked.Read(ref _underruns);

    public float Volume { get; set; } = 1.0f;

    public bool Mono { get; set; }

    public bool Paused { get; set; }

    public bool TryStart()
    {
        if (_device is not null)
        {
            return true;
        }

        try
        {
            var device = new WasapiPlayerBuilder()
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(DeviceLatencyMs)
                .WithLowLatency(required: false)
                .WithCategory(AudioStreamCategory.GameMedia)
                .WithMmcssThreadPriority("Games")
                .Build();
            device.Init(this);
            device.Play();
            _device = device;
            _playing = true;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false; // no audio device (e.g. a CI runner, or a remote session)
        }
    }

    public int Write(ReadOnlySpan<float> frames) => _ring.Write(frames);

    public void Flush() => _ring.Clear();

    /// <summary>Called on NAudio's device thread.</summary>
    public int Read(Span<byte> buffer)
    {
        var stereo = MemoryMarshal.Cast<byte, float>(buffer);
        var frames = stereo.Length / 2;
        var queued = _frames.AsSpan(0, Math.Min(frames, _frames.Length / 2) * 2);
        var got = Paused ? 0 : _ring.Read(queued);
        if (got < queued.Length / 2 && _playing && !Paused)
        {
            Interlocked.Increment(ref _underruns);
        }

        var volume = Volume;
        var mono = Mono;
        for (var i = 0; i < frames; i++)
        {
            float left = 0, right = 0;
            if (i < got)
            {
                left = queued[2 * i] * volume;
                right = queued[2 * i + 1] * volume;
                if (mono)
                {
                    left = right = (left + right) * 0.5f;
                }
            }

            stereo[2 * i] = left;
            stereo[2 * i + 1] = right;
        }

        return buffer.Length;
    }

    public void Dispose()
    {
        _playing = false;
        _device?.Stop();
        _device?.Dispose();
        _device = null;
    }
}
