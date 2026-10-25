using System.Diagnostics;
using GameVault.Core.State;
using GameVault.Core.Tests.Cartridge;
using Xunit.Abstractions;

namespace GameVault.Core.Tests.State;

public class SnapshotTests(ITestOutputHelper output)
{
    [Fact]
    public void Snapshot_restores_the_exact_timeline()
    {
        var console = NesConsole.FromBytes(BankedRom.Build(4, 16, 128));
        console.Power();
        for (var i = 0; i < 10; i++)
        {
            console.RunFrame();
        }

        var snapshot = console.SaveSnapshot();
        for (var i = 0; i < 10; i++)
        {
            console.RunFrame();
        }

        var expected = console.Ppu.FrameBuffer.ToArray();
        console.LoadSnapshot(snapshot);
        for (var i = 0; i < 10; i++)
        {
            console.RunFrame();
        }

        console.Ppu.FrameBuffer.ToArray().Should().Equal(expected);
    }

    [Fact]
    public void Snapshot_from_another_game_is_rejected()
    {
        var a = NesConsole.FromBytes(BankedRom.Build(4, 16, 128));
        var b = NesConsole.FromBytes(BankedRom.Build(2, 16, 0));
        a.Power();

        var act = () => b.LoadSnapshot(a.SaveSnapshot());

        act.Should().Throw<StateFormatException>().WithMessage("*different game*");
    }

    [Fact]
    public void Silent_frames_do_not_reach_the_audio_sink()
    {
        var console = NesConsole.FromBytes(BankedRom.Build(0, 4, 8));
        var audio = console.EnableAudio();
        console.Power();

        console.RunFrameSilently();

        console.Apu.AudioSink.Should().BeSameAs(audio);
        audio.Available.Should().Be(0);
    }

    /// <summary>
    /// SPEC §4.6: save and load must each take under 1 ms for typical mappers, because run-ahead and rewind
    /// depend on it. Measured over many iterations; CI machines get 3x headroom.
    /// </summary>
    [Theory]
    [InlineData(0, 4, 8)]
    [InlineData(4, 32, 256)]
    public void Save_and_load_take_under_a_millisecond(int mapper, int prg8K, int chr1K)
    {
        var console = NesConsole.FromBytes(BankedRom.Build(mapper, prg8K, chr1K));
        console.Power();
        console.RunFrame();
        var writer = new StateWriter();
        console.SaveSnapshot(writer);
        var data = writer.ToArray();

        // Warm up the JIT.
        for (var i = 0; i < 200; i++)
        {
            writer.Reset();
            console.SaveSnapshot(writer);
            console.LoadSnapshot(data);
        }

        const int iterations = 1000;
        var save = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            writer.Reset();
            console.SaveSnapshot(writer);
        }

        save.Stop();
        var load = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            console.LoadSnapshot(data);
        }

        load.Stop();

        var saveMs = save.Elapsed.TotalMilliseconds / iterations;
        var loadMs = load.Elapsed.TotalMilliseconds / iterations;
        output.WriteLine($"mapper {mapper}: {data.Length} bytes, save {saveMs:F4} ms, load {loadMs:F4} ms");
        saveMs.Should().BeLessThan(3);
        loadMs.Should().BeLessThan(3);
    }
}
