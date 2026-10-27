using GameVault.Core.Audio;
using GameVault.Platform.Audio;

namespace GameVault.Platform.Tests;

/// <summary>
/// SPEC §12 Phase 3: "30-minute audio soak test has no underruns". Runs 30 minutes of virtual time through
/// the real resampler, pacing and ring buffer against a simulated WASAPI device. The device pulls 10 ms
/// periods from a clock that drifts from nominal, and the emulation thread wakes every millisecond with
/// random scheduling stalls. It finishes in seconds, so it runs in CI on every push.
/// </summary>
public class AudioSoakTests
{
    private const int SampleRate = 48000;
    private const double CpuClock = 236250000.0 / 11 / 12;
    private const int DevicePeriodMs = 10;
    private const int Minutes = 30;

    [Theory]
    [InlineData(0.0)]
    [InlineData(+0.003)] // sound card 0.3 % fast
    [InlineData(-0.003)] // sound card 0.3 % slow
    public void Thirty_minutes_play_without_underruns(double deviceDrift)
    {
        var result = Soak(deviceDrift, seed: 2026);

        result.Underruns.Should().Be(0);
        result.AverageBufferedMs.Should().BeLessThan(40, "SPEC §5.3: target latency of 40 ms or less");
        result.MaxBufferedMs.Should().BeLessThan(50, "the peak is the target plus one frame of samples");
        // Rate control absorbs the device's drift with a tiny pitch change (≤ 0.5 %), so the game itself
        // keeps running at the console's real speed.
        var nominal = Minutes * 60 * 60.0988;
        ((double)result.FramesEmulated).Should().BeApproximately(nominal, nominal * AudioPacing.MaxAdjustment);
    }

    private sealed record SoakResult(long Underruns, double MaxBufferedMs, double AverageBufferedMs, long FramesEmulated);

    private static SoakResult Soak(double deviceDrift, int seed)
    {
        var random = new Random(seed);
        var resampler = new BandLimitedResampler(CpuClock, SampleRate);
        var pacing = new AudioPacing(SampleRate);
        var ring = new AudioRingBuffer(SampleRate / 4);
        var scratch = new float[8192 * 2];
        var period = new float[2048 * 2];

        long underruns = 0, frames = 0, bufferedSum = 0, bufferedSamples = 0, consumed = 0;
        var deviceStartMs = 0;
        var maxBuffered = 0;
        var deviceStarted = false;
        double deviceDebt = 0;
        var nextWake = 0;
        var toneClock = 0L;
        float level = 0;

        for (var ms = 0; ms < Minutes * 60 * 1000; ms++)
        {
            // Emulation thread: wakes about every millisecond, sometimes late (preemption, GC, etc.).
            if (ms >= nextWake)
            {
                while (pacing.ShouldRunFrame(ring.Count))
                {
                    var elapsed = deviceStarted ? (ms - deviceStartMs) / 1000.0 : 0;
                    resampler.ClockRate = CpuClock * pacing.ClockRateFactor(consumed, elapsed);
                    var clocks = frames % 3 == 0 ? 29780 : 29781;

                    // A 440 Hz square wave stands in for the APU.
                    for (var c = 0; c < clocks; c += 64, toneClock += 64)
                    {
                        var next = (toneClock / 2034) % 2 == 0 ? 0.2f : 0f;
                        if (next != level)
                        {
                            resampler.AddDelta(c, next - level);
                            level = next;
                        }
                    }

                    resampler.EndFrame(clocks);
                    var n = resampler.ReadStereo(scratch);
                    ring.Write(scratch.AsSpan(0, n * 2));
                    frames++;
                }

                nextWake = ms + 1 + (random.Next(200) == 0 ? random.Next(1, 8) : 0);
            }

            // Device: every 10 ms its own clock asks for one period of samples.
            if (ms % DevicePeriodMs == 0)
            {
                if (!deviceStarted && ring.Count >= pacing.TargetSamples)
                {
                    deviceStarted = true;
                    deviceStartMs = ms;
                }

                if (deviceStarted)
                {
                    deviceDebt += SampleRate * DevicePeriodMs / 1000.0 * (1 + deviceDrift);
                    var want = (int)deviceDebt;
                    deviceDebt -= want;
                    var got = ring.Read(period.AsSpan(0, want * 2));
                    consumed += want;
                    if (got < want)
                    {
                        underruns++;
                    }

                    maxBuffered = Math.Max(maxBuffered, ring.Count);
                    bufferedSum += ring.Count;
                    bufferedSamples++;
                }
            }
        }

        // Latency the player hears: what's queued in our ring plus the device's own period.
        var toMs = 1000.0 / SampleRate;
        return new SoakResult(
            underruns,
            maxBuffered * toMs + DevicePeriodMs,
            (double)bufferedSum / bufferedSamples * toMs + DevicePeriodMs,
            frames);
    }
}
