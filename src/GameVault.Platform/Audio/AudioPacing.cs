namespace GameVault.Platform.Audio;

/// <summary>
/// Audio-driven timing (SPEC §5.3, §5.6) in two parts:
/// <list type="bullet">
/// <item><b>Pacing</b>: emulate another frame only while the device buffer is below its target, so the
/// buffer can never drain or grow without bound.</item>
/// <item><b>Rate control</b>: sound cards' clocks are off by a fraction of a percent. Measure how fast the
/// device actually consumes samples and scale the resampling ratio (within ±0.5 %) to match. The game then
/// runs at the console's true speed, and only the pitch shifts, by an inaudible amount.</item>
/// </list>
/// </summary>
public sealed class AudioPacing
{
    /// <summary>Maximum ratio adjustment (SPEC §5.3: ±0.5 %).</summary>
    public const double MaxAdjustment = 0.005;

    /// <summary>
    /// Queued audio to aim for. One frame (16.7 ms) is added at a time and the device pulls 10 ms periods,
    /// so heard latency averages under 30 ms (SPEC §5.3: ≤ 40 ms; see AudioSoakTests).
    /// </summary>
    public const double DefaultTargetLatencyMs = 20;

    /// <summary>Seconds of measurement before the device rate estimate is trusted.</summary>
    private const double MinimumMeasureSeconds = 2;

    public AudioPacing(int sampleRate, double targetLatencyMs = DefaultTargetLatencyMs)
    {
        SampleRate = sampleRate;
        TargetSamples = (int)(sampleRate * targetLatencyMs / 1000);
    }

    public int SampleRate { get; }

    public int TargetSamples { get; }

    /// <summary>True when the buffer is short enough that another frame should be emulated now.</summary>
    public bool ShouldRunFrame(int bufferedSamples) => bufferedSamples < TargetSamples;

    /// <summary>
    /// Multiplier for the clock rate given to the resampler. It is <c>sampleRate / measured device rate</c>,
    /// clamped to ±<see cref="MaxAdjustment"/>: a fast device (factor below 1) gets more samples per frame.
    /// </summary>
    /// <param name="samplesConsumed">Samples the device has played since measurement started.</param>
    /// <param name="elapsedSeconds">Time since measurement started.</param>
    public double ClockRateFactor(long samplesConsumed, double elapsedSeconds)
    {
        if (elapsedSeconds < MinimumMeasureSeconds || samplesConsumed <= 0)
        {
            return 1.0;
        }

        var deviceRate = samplesConsumed / elapsedSeconds;
        return Math.Clamp(SampleRate / deviceRate, 1 - MaxAdjustment, 1 + MaxAdjustment);
    }
}
