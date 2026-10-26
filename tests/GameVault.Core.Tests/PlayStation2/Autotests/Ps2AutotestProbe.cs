using Xunit.Abstractions;

namespace GameVault.Core.Tests.PlayStation2.Autotests;

// Temporary probe while bringing the console up.
public sealed class Ps2AutotestProbe(ITestOutputHelper log)
{
    [Fact]
    public void Probe()
    {
        var name = Environment.GetEnvironmentVariable("PS2_PROBE");
        if (name is null)
        {
            return;
        }

        var result = Ps2AutotestRunner.Run(
            name,
            int.Parse(Environment.GetEnvironmentVariable("PS2_FRAMES") ?? "120"),
            Environment.GetEnvironmentVariable("PS2_TRACE") is null ? null : log.WriteLine,
            Environment.GetEnvironmentVariable("PS2_DUMP"));
        log.WriteLine(result.ToString());
        if (Environment.GetEnvironmentVariable("PS2_OUT") is { } dir)
        {
            File.WriteAllText(Path.Combine(dir, "out.txt"), result.Output);
            File.WriteAllText(Path.Combine(dir, "exp.txt"), result.Expected);
        }
    }
}
