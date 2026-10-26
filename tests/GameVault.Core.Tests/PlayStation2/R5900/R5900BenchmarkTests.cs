using GameVault.Core.Cpu.R5900;
using Xunit.Abstractions;

namespace GameVault.Core.Tests.PlayStation2.R5900;

/// <summary>The R5900 interpreter's speed.</summary>
public sealed class R5900BenchmarkTests(ITestOutputHelper output)
{
    /// <summary>
    /// Reports how many instructions a second a game-like loop runs: in kuseg through the TLB (as the kernel maps
    /// programs), with loads, stores, ALU, a multiply, MMI, FPU and a branch. Only with
    /// <c>GAMEVAULT_EE_BENCHMARK=1</c>, best in Release; it makes no timing assertion. The real EE runs up to
    /// 294.912 M instructions a second (more with dual issue).
    /// </summary>
    [Fact]
    public void Benchmark_reports_instructions_per_second()
    {
        if (Environment.GetEnvironmentVariable("GAMEVAULT_EE_BENCHMARK") != "1")
        {
            return;
        }

        var machine = new R5900TestMachine();
        var cpu = machine.Cpu;

        // The program and its data in kuseg at $100000, mapped onto RDRAM's first 4 MB with 1 MB pages.
        for (var i = 0; i < 2; i++)
        {
            var virtualBase = (uint)i * 0x20_0000;
            cpu.WriteTlbEntry(i, 0xFF << 13, virtualBase, ((virtualBase >> 12) << 6) | 0x1F, (((virtualBase + 0x10_0000) >> 12) << 6) | 0x1F);
        }

        uint[] loop =
        [
            Ee.Lw(5, 4, 0),
            Ee.Addu(6, 6, 5),
            Ee.Sw(6, 4, 4),
            Ee.Mmi(0x09, 7, 7, 6, 0x0C), // PMULTW
            Ee.Mmi(0x08, 8, 8, 7, 0x00), // PADDW
            Ee.Lq(9, 4, 16),
            Ee.FpuS(0x00, 1, 1, 2), // ADD.S
            Ee.FpuS(0x02, 3, 1, 2), // MUL.S
            Ee.Addiu(10, 10, -1),
            Ee.Bne(10, 0, -10),
            Ee.Addiu(4, 4, 0), // delay slot
            Ee.Beq(0, 0, -12), // back to the top, reloading the counter
            Ee.Lui(10, 0x10),
        ];
        machine.Load(0x8010_0000, loop);
        cpu.SetGpr64(4, 0x0020_0000);
        cpu.SetGpr64(10, 0x10_0000);
        cpu.Jump(0x0010_0000);

        foreach (var (name, start) in new[] { ("kuseg (TLB)", 0x0010_0000u), ("KSEG0", 0x8010_0000u) })
        {
            cpu.Jump(start);
            cpu.Run(cpu.Cycles + 5_000_000); // warm up
            const long total = 200_000_000;
            const int slice = 100_000;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var target = cpu.Cycles + total;
            while (cpu.Cycles < target)
            {
                cpu.Run(cpu.Cycles + slice);
            }

            watch.Stop();
            output.WriteLine($"EE, {name}: {total / watch.Elapsed.TotalSeconds / 1e6:F1} M instructions/s");
        }
    }
}
