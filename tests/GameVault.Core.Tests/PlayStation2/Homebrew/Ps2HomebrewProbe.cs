using Xunit.Abstractions;

namespace GameVault.Core.Tests.PlayStation2.Homebrew;

// Temporary probe while bringing the homebrew up: PS2_HB=<name> writes <name>.png to PS2_OUT.
public sealed class Ps2HomebrewProbe(ITestOutputHelper log)
{
    [Fact]
    public void Probe()
    {
        var name = Environment.GetEnvironmentVariable("PS2_HB");
        if (name is null)
        {
            return;
        }

        var console = Ps2HomebrewRunner.Run(name);
        log.WriteLine($"status {Ps2HomebrewRunner.Word(console, 0):X8} {Ps2HomebrewRunner.Word(console, 1):X8} {Ps2HomebrewRunner.Word(console, 2):X8} {Ps2HomebrewRunner.Word(console, 3):X8} frames={console.FrameCount} size={console.ScreenWidth}x{console.ScreenHeight} fault={console.Kernel?.Fault} pc={console.Cpu.Pc:X8}");
        foreach (var spec in (Environment.GetEnvironmentVariable("PS2_PIX") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var xy = spec.Split(',');
            log.WriteLine($"({spec}) = {Ps2HomebrewRunner.Pixel(console, int.Parse(xy[0]), int.Parse(xy[1]))}");
        }

        if (Environment.GetEnvironmentVariable("PS2_OUT") is { } dir)
        {
            Ps2HomebrewRunner.SavePng(console, Path.Combine(dir, name + ".png"));
        }
    }
}
