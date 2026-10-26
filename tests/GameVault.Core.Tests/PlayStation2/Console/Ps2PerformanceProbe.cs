using System.Diagnostics;
using GameVault.Core.PlayStation2;
using GameVault.Core.Tests.Harness;
using GameVault.Core.Tests.PlayStation2.Homebrew;
using Xunit.Abstractions;

namespace GameVault.Core.Tests.PlayStation2.Console;

/// <summary>
/// Measures emulated frames per second (run with <c>GAMEVAULT_PS2_PERF=1</c>, in Release): the homebrew (idle after its
/// drawing), a CPU-bound autotest looping its test body, and the save state's size and speed.
/// </summary>
public sealed class Ps2PerformanceProbe(ITestOutputHelper log)
{
    [Fact]
    public void Measure()
    {
        if (Environment.GetEnvironmentVariable("GAMEVAULT_PS2_PERF") != "1")
        {
            return;
        }

        foreach (var name in new[] { "vu1-path1", "gs-texture" })
        {
            var console = new Ps2Console(Ps2Boot.FromElf(File.ReadAllBytes(Ps2HomebrewRunner.ElfPath(name))));
            Report(name, console, 300);
        }

        // A CPU-heavy program: the busy part is the test itself, so rerun it from boot until the time is up.
        foreach (var test in new[] { "cpu/ee_simd/muldiv", "vu/lower/integer", "kernel/ee/interrupt/intc_order" })
        {
            var elf = TestRomLocator.ReadCached("ps2autotests/" + test + ".elf");
            var watch = Stopwatch.StartNew();
            long frames = 0, cycles = 0;
            while (watch.ElapsedMilliseconds < 3000)
            {
                var console = new Ps2Console(Ps2Boot.FromElf(elf));
                while (!console.Exited && console.FrameCount < 600)
                {
                    console.RunFrame();
                    frames++;
                }

                cycles += console.Cpu.Cycles;
            }

            log.WriteLine($"{test}: {frames / watch.Elapsed.TotalSeconds:F1} frames/s, {cycles / watch.Elapsed.TotalSeconds / 1e6:F1} M EE cycles/s ({cycles * 100.0 / watch.Elapsed.TotalSeconds / 294_912_000:F1}% of real time)");
        }

        var state = new Ps2Console(Ps2Boot.FromElf(File.ReadAllBytes(Ps2HomebrewRunner.ElfPath("vu1-path1"))));
        state.RunFrame();
        var save = Stopwatch.StartNew();
        var bytes = state.SaveSnapshot();
        var saveMs = save.Elapsed.TotalMilliseconds;
        save.Restart();
        state.LoadSnapshot(bytes);
        log.WriteLine($"save state: {bytes.Length / 1048576.0:F1} MB, save {saveMs:F1} ms, load {save.Elapsed.TotalMilliseconds:F1} ms");
    }

    private void Report(string name, Ps2Console console, int frames)
    {
        console.RunFrame();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++)
        {
            console.RunFrame();
        }

        log.WriteLine($"{name}: {frames / watch.Elapsed.TotalSeconds:F1} frames/s");
    }
}
