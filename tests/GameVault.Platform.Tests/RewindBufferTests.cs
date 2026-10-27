using GameVault.Core;
using GameVault.Core.Tests.Cartridge;
using GameVault.Platform.Emulation;

namespace GameVault.Platform.Tests;

public class RewindBufferTests
{
    private static NesConsole NewConsole()
    {
        var console = NesConsole.FromBytes(BankedRom.Build(4, 16, 128));
        console.Power();
        return console;
    }

    [Fact]
    public void Stepping_back_restores_the_exact_earlier_state()
    {
        var console = NewConsole();
        var rewind = new RewindBuffer();
        var snapshots = new List<byte[]>();
        for (var frame = 0; frame < 200; frame++)
        {
            if (frame % RewindBuffer.Interval == 0)
            {
                snapshots.Add(console.SaveSnapshot());
            }

            rewind.OnFrame(console);
            console.RunFrame();
        }

        // Walk all the way back; each step must equal the snapshot taken at that point.
        for (var i = snapshots.Count - 1; i >= 0; i--)
        {
            rewind.StepBack(console).Should().BeTrue();
            console.SaveSnapshot().Should().Equal(snapshots[i], $"entry {i}");
        }

        rewind.StepBack(console).Should().BeFalse();
    }

    [Fact]
    public void Scrubbing_moves_both_ways_and_play_continues_from_the_chosen_point()
    {
        var console = NewConsole();
        var rewind = new RewindBuffer();
        var snapshots = new List<byte[]>();
        for (var frame = 0; frame < 400; frame++)
        {
            if (frame % RewindBuffer.Interval == 0)
            {
                snapshots.Add(console.SaveSnapshot());
            }

            rewind.OnFrame(console);
            console.RunFrame();
        }

        // Back across a keyframe group, forward again, then back: nothing is removed while scrubbing.
        foreach (var index in new[] { 10, 150, 61, 199, 0, 120 })
        {
            rewind.Restore(index, console);
            console.SaveSnapshot().Should().Equal(snapshots[index], $"entry {index}");
        }

        rewind.Count.Should().Be(200);
        rewind.TruncateAfter(120);
        rewind.Count.Should().Be(121);

        // New captures continue from entry 120 (already stored, so the next one is an interval later) and restore correctly.
        var later = new List<byte[]>();
        for (var frame = 0; frame < 200; frame++)
        {
            if (frame % RewindBuffer.Interval == 1)
            {
                later.Add(console.SaveSnapshot());
            }

            rewind.OnFrame(console);
            console.RunFrame();
        }

        for (var i = later.Count - 1; i >= 0; i--)
        {
            rewind.StepBack(console).Should().BeTrue();
            console.SaveSnapshot().Should().Equal(later[i], $"new entry {i}");
        }

        rewind.StepBack(console).Should().BeTrue();
        console.SaveSnapshot().Should().Equal(snapshots[120]);
    }

    [Fact]
    public void A_longer_interval_compressed_in_the_background_restores_the_same_states()
    {
        // The PlayStation 2's settings (SystemBudgets): a capture every 30 frames, compressed on a worker.
        var console = NewConsole();
        var rewind = new RewindBuffer { CaptureInterval = 30, CompressInBackground = true };
        var snapshots = new List<byte[]>();
        for (var frame = 0; frame < 3000; frame++)
        {
            if (frame % 30 == 0)
            {
                snapshots.Add(console.SaveSnapshot());
            }

            rewind.OnFrame(console);
            console.RunFrame();
        }

        rewind.Count.Should().Be(100, "one capture every 30 frames, past a keyframe group of 60");
        rewind.Duration.TotalSeconds.Should().BeApproximately(3000 / EmulatorHost.NtscFrameRate, 0.01);
        for (var i = snapshots.Count - 1; i >= 0; i--)
        {
            rewind.StepBack(console).Should().BeTrue();
            console.SaveSnapshot().Should().Equal(snapshots[i], $"entry {i}");
        }

        rewind.StepBack(console).Should().BeFalse();
    }

    [Fact]
    public void Capturing_resumes_cleanly_after_rewinding()
    {
        var console = NewConsole();
        var rewind = new RewindBuffer();
        for (var frame = 0; frame < 150; frame++)
        {
            rewind.OnFrame(console);
            console.RunFrame();
        }

        for (var i = 0; i < 40; i++)
        {
            rewind.StepBack(console);
        }

        var expected = new List<byte[]>();
        for (var frame = 0; frame < 80; frame++)
        {
            var before = console.SaveSnapshot();
            var count = rewind.Count;
            rewind.OnFrame(console);
            if (rewind.Count > count)
            {
                expected.Add(before);
            }

            console.RunFrame();
        }

        expected.Should().HaveCount(40);

        for (var i = expected.Count - 1; i >= 0; i--)
        {
            rewind.StepBack(console);
            console.SaveSnapshot().Should().Equal(expected[i]);
        }
    }

    [Fact]
    public void A_minute_fits_easily_in_the_default_cap_and_small_caps_are_respected()
    {
        var console = NewConsole();
        var rewind = new RewindBuffer();
        for (var frame = 0; frame < 600; frame++)
        {
            rewind.OnFrame(console);
            console.RunFrame();
        }

        // SPEC §7.2: at least 60 s within 256 MB. Extrapolate from the measured size per entry.
        var bytesPerEntry = (double)rewind.BytesUsed / rewind.Count;
        var minuteBytes = bytesPerEntry * 60 * EmulatorHost.NtscFrameRate / RewindBuffer.Interval;
        minuteBytes.Should().BeLessThan(RewindBuffer.DefaultCapacity / 16.0, $"{bytesPerEntry:F0} bytes per entry");
        rewind.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(300 / EmulatorHost.NtscFrameRate * 2), TimeSpan.FromMilliseconds(50));

        rewind.CapacityBytes = 64 * 1024;
        for (var frame = 0; frame < 400; frame++)
        {
            rewind.OnFrame(console);
            console.RunFrame();
        }

        rewind.BytesUsed.Should().BeLessThanOrEqualTo(64 * 1024);
        rewind.StepBack(console).Should().BeTrue();
    }
}
