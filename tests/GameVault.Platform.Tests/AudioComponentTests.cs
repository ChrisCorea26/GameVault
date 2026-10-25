using GameVault.Platform.Audio;

namespace GameVault.Platform.Tests;

public class AudioComponentTests
{
    [Fact]
    public void Ring_buffer_reads_back_what_was_written_and_reports_count()
    {
        var ring = new AudioRingBuffer(8);
        ring.Write([1, 2, 3]).Should().Be(3);
        ring.Count.Should().Be(3);

        var out1 = new float[2];
        ring.Read(out1).Should().Be(2);
        out1.Should().Equal(1f, 2f);
        ring.Count.Should().Be(1);
    }

    [Fact]
    public void Ring_buffer_refuses_writes_beyond_capacity_and_wraps()
    {
        var ring = new AudioRingBuffer(4);
        ring.Write([1, 2, 3, 4, 5]).Should().Be(4);
        ring.Read(new float[3]);
        ring.Write([6, 7]).Should().Be(2);

        var all = new float[4];
        ring.Read(all).Should().Be(3);
        all[..3].Should().Equal(4f, 6f, 7f);
    }

    [Fact]
    public void Ring_buffer_clear_drops_everything()
    {
        var ring = new AudioRingBuffer(16);
        ring.Write([1, 2, 3]);

        ring.Clear();

        ring.Count.Should().Be(0);
    }

    [Fact]
    public void Pacing_runs_frames_only_below_target()
    {
        var pacing = new AudioPacing(48000);

        pacing.TargetSamples.Should().Be(960);
        pacing.ShouldRunFrame(900).Should().BeTrue();
        pacing.ShouldRunFrame(1000).Should().BeFalse();
    }

    [Theory]
    [InlineData(48000 * 1.003, 1 / 1.003)]   // fast card: more samples per frame
    [InlineData(48000 * 0.997, 1 / 0.997)]   // slow card: fewer
    [InlineData(48000 * 1.02, 1 - AudioPacing.MaxAdjustment)] // clamped to ±0.5 %
    [InlineData(48000 * 0.98, 1 + AudioPacing.MaxAdjustment)]
    public void Rate_factor_matches_the_measured_device_rate(double deviceRate, double expected)
    {
        var pacing = new AudioPacing(48000);

        pacing.ClockRateFactor((long)Math.Round(deviceRate * 10), 10).Should().BeApproximately(expected, 1e-5);
    }

    [Fact]
    public void Rate_factor_is_neutral_until_enough_time_is_measured()
    {
        var pacing = new AudioPacing(48000);

        pacing.ClockRateFactor(50_000, 1.0).Should().Be(1.0);
    }
}
