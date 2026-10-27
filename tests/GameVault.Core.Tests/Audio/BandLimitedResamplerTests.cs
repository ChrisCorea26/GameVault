using GameVault.Core.Audio;

namespace GameVault.Core.Tests.Audio;

public class BandLimitedResamplerTests
{
    private const double NtscClock = 39375000.0 / 22.0; // 1789772.7 Hz
    private const int FrameClocks = 29781;

    private static float[] Render(BandLimitedResampler resampler, int frames, Func<int, float?> levelAt)
    {
        var samples = new List<float>();
        var buffer = new float[4096];
        var clock = 0;
        float level = 0;
        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < FrameClocks; c++, clock++)
            {
                if (levelAt(clock) is { } next && next != level)
                {
                    resampler.AddDelta(c, next - level);
                    level = next;
                }
            }

            resampler.EndFrame(FrameClocks);
            var n = resampler.Read(buffer);
            samples.AddRange(buffer.AsSpan(0, n).ToArray());
        }

        return [.. samples];
    }

    /// <summary>Magnitude of one frequency in a signal (single-bin DFT).</summary>
    private static double Magnitude(float[] signal, double hz, int sampleRate)
    {
        double re = 0, im = 0;
        for (var i = 0; i < signal.Length; i++)
        {
            var angle = 2 * Math.PI * hz * i / sampleRate;
            re += signal[i] * Math.Cos(angle);
            im -= signal[i] * Math.Sin(angle);
        }

        return Math.Sqrt(re * re + im * im) / signal.Length;
    }

    [Fact]
    public void Produces_the_right_number_of_samples_per_frame()
    {
        var resampler = new BandLimitedResampler(NtscClock);

        var samples = Render(resampler, 600, _ => null);

        var expected = 600.0 * FrameClocks * 48000 / NtscClock;
        samples.Length.Should().BeInRange((int)expected - 1, (int)expected + 1);
    }

    [Fact]
    public void Silence_stays_silent()
    {
        var samples = Render(new BandLimitedResampler(NtscClock), 10, _ => null);

        samples.Should().OnlyContain(s => s == 0);
    }

    [Fact]
    public void A_step_settles_at_its_level_without_filters()
    {
        var resampler = new BandLimitedResampler(NtscClock) { ApplyNesFilters = false };

        var samples = Render(resampler, 2, c => c >= 1000 ? 0.5f : null);

        samples[^1].Should().BeApproximately(0.5f, 1e-4f);
        samples.Max().Should().BeLessThan(0.56f, "ringing stays small");
    }

    [Fact]
    public void Nes_filters_remove_dc()
    {
        var resampler = new BandLimitedResampler(NtscClock);

        var samples = Render(resampler, 60, c => c >= 1000 ? 0.5f : null);

        Math.Abs(samples[^1]).Should().BeLessThan(0.001f);
    }

    [Fact]
    public void A_1khz_square_wave_keeps_its_fundamental()
    {
        var resampler = new BandLimitedResampler(NtscClock) { ApplyNesFilters = false };
        var halfPeriod = NtscClock / 2000.0;

        var samples = Render(resampler, 30, c => ((int)(c / halfPeriod) & 1) == 1 ? 0.25f : 0f);

        var fundamental = Magnitude(samples, 1000, 48000);
        var offFrequency = Magnitude(samples, 1500, 48000);
        fundamental.Should().BeGreaterThan(0.07); // a 0.25 square's fundamental is 0.25 * 2/pi / 2 ≈ 0.08
        offFrequency.Should().BeLessThan(fundamental / 100);
    }

    [Fact]
    public void A_15khz_square_wave_does_not_alias()
    {
        // The third harmonic (45 kHz) would fold down to 3 kHz in a naive resampler.
        var resampler = new BandLimitedResampler(NtscClock) { ApplyNesFilters = false };
        var halfPeriod = NtscClock / 30000.0;

        var samples = Render(resampler, 30, c => ((int)(c / halfPeriod) & 1) == 1 ? 0.25f : 0f);

        var fundamental = Magnitude(samples, 15000, 48000);
        var alias = Magnitude(samples, 3000, 48000);
        (alias / fundamental).Should().BeLessThan(0.01, "aliasing should be at least 40 dB down");
    }

    [Fact]
    public void Raising_the_clock_rate_yields_proportionally_fewer_samples()
    {
        var normal = Render(new BandLimitedResampler(NtscClock), 300, _ => null).Length;
        var fast = Render(new BandLimitedResampler(NtscClock * 1.005), 300, _ => null).Length;

        ((double)fast / normal).Should().BeApproximately(1 / 1.005, 0.0005);
    }

    private static (float[] Left, float[] Right) RenderStereo(BandLimitedResampler resampler, int frames, Action<BandLimitedResampler, int> feed)
    {
        var left = new List<float>();
        var right = new List<float>();
        var buffer = new float[8192];
        for (var f = 0; f < frames; f++)
        {
            feed(resampler, f);
            resampler.EndFrame(FrameClocks);
            var n = resampler.ReadStereo(buffer);
            for (var i = 0; i < n; i++)
            {
                left.Add(buffer[2 * i]);
                right.Add(buffer[2 * i + 1]);
            }
        }

        return ([.. left], [.. right]);
    }

    [Fact]
    public void A_stereo_step_reaches_only_its_own_channel()
    {
        var resampler = new BandLimitedResampler(NtscClock) { ApplyNesFilters = false };

        var (left, right) = RenderStereo(resampler, 2, (r, f) =>
        {
            if (f == 0)
            {
                r.AddDelta(1000, 0.5f, 0);
                r.AddDelta(5000, 0, -0.25f);
            }
        });

        resampler.IsStereo.Should().BeTrue();
        left[^1].Should().BeApproximately(0.5f, 1e-4f);
        right[^1].Should().BeApproximately(-0.25f, 1e-4f);
        right[..20].Should().OnlyContain(s => Math.Abs(s) < 1e-6f, "the left step never reached the right channel");
    }

    [Fact]
    public void Mono_deltas_reach_both_channels_before_and_after_stereo_starts()
    {
        var resampler = new BandLimitedResampler(NtscClock) { ApplyNesFilters = false };

        var (left, right) = RenderStereo(resampler, 3, (r, f) =>
        {
            switch (f)
            {
                case 0:
                    r.AddDelta(100, 0.3f); // mono: the right channel copies the left
                    break;
                case 1:
                    r.AddDelta(100, 0.2f, 0); // now stereo; the right keeps what it had
                    r.AddDelta(9000, 0.1f); // mono again: both move
                    break;
            }
        });

        resampler.IsStereo.Should().BeTrue();
        left.Length.Should().Be(right.Length);
        var firstFrame = left.Length / 3;
        for (var i = 0; i < firstFrame; i++)
        {
            right[i].Should().Be(left[i], "a mono source is the same on both channels");
        }

        left[^1].Should().BeApproximately(0.6f, 1e-4f);
        right[^1].Should().BeApproximately(0.4f, 1e-4f);
    }

    [Fact]
    public void Read_mixes_stereo_down_to_mono()
    {
        var resampler = new BandLimitedResampler(NtscClock) { ApplyNesFilters = false };
        resampler.AddDelta(100, 0.8f, 0);
        resampler.EndFrame(FrameClocks);
        resampler.EndFrame(FrameClocks);

        var buffer = new float[4096];
        var n = resampler.Read(buffer);

        n.Should().BeGreaterThan(1000);
        buffer[n - 1].Should().BeApproximately(0.4f, 1e-4f);
        resampler.Available.Should().Be(0);
    }
}
