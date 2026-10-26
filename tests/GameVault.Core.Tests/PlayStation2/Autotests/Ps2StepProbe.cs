using GameVault.Core.Tests.Harness;
using Xunit.Abstractions;

namespace GameVault.Core.Tests.PlayStation2.Autotests;

// Temporary probe while bringing the console up.
public sealed class Ps2StepProbe(ITestOutputHelper log)
{
    [Fact]
    public void Steps()
    {
        var name = Environment.GetEnvironmentVariable("PS2_STEP");
        if (name is null)
        {
            return;
        }

        var elf = TestRomLocator.ReadCached(Ps2AutotestRunner.CacheFolder + name + ".elf");
        var console = new GameVault.Core.PlayStation2.Ps2Console(GameVault.Core.PlayStation2.Ps2Boot.FromElf(elf));
        var trigger = Convert.ToInt32(Environment.GetEnvironmentVariable("PS2_STEP_SYSCALL") ?? "5A", 16);
        var armed = false;
        console.Kernel!.SyscallTrace = (n, pc) => armed |= n == trigger;
        for (var i = 0; i < 20_000_000 && !armed; i++)
        {
            console.Cpu.Step();
        }

        for (var i = 0; i < 40000; i++)
        {
            if (console.Cpu.Pc < 0x1050C8 || console.Cpu.Pc > 0x105104) log.WriteLine($"{console.Cpu.Pc:X8} st={console.Cpu.Status:X8} ra={console.Cpu.GetGpr64(31):X}");
            console.Cpu.Step();
        }
    }
}
