using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.PlayStation;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.PlayStation;

/// <summary>
/// PeterLemon's bare-metal PSX <c>CPUTest</c> programs (downloaded at test time, see tests/TestRoms/SOURCES.md): each
/// runs an instruction on known values and draws PASS in a green font or FAIL in a red one in the column at x = 280,
/// so the harness counts the green and red cells there. They need no BIOS.
/// </summary>
public sealed class PeterLemonCpuTests
{
    private const string CacheFolder = "psx-peterlemon/CPUTest/";
    private const ushort Green = 0x8200;
    private const ushort Red = 0x801F;
    private const uint BranchToSelf = 0x1000FFFF; // b . (where every program ends)

    [TestRomTheory("psx-peterlemon/CPUTest/CPU/ADD/CPUADD.exe")]
    [InlineData("CPU/ADD/CPUADD.exe", 7)]
    [InlineData("CPU/ADDI/CPUADDI.exe", 7)]
    [InlineData("CPU/ADDIU/CPUADDIU.exe", 7)]
    [InlineData("CPU/ADDU/CPUADDU.exe", 7)]
    [InlineData("CPU/AND/CPUAND.exe", 7)]
    [InlineData("CPU/ANDI/CPUANDI.exe", 7)]
    [InlineData("CPU/DIV/CPUDIV.exe", 14)]
    [InlineData("CPU/DIVU/CPUDIVU.exe", 14)]
    [InlineData("CPU/LOADSTORE/LB/CPULB.exe", 14)]
    [InlineData("CPU/LOADSTORE/LH/CPULH.exe", 14)]
    [InlineData("CPU/LOADSTORE/LW/CPULW.exe", 23)]
    [InlineData("CPU/LOADSTORE/SB/CPUSB.exe", 7)]
    [InlineData("CPU/LOADSTORE/SH/CPUSH.exe", 7)]
    [InlineData("CPU/LOADSTORE/SW/CPUSW.exe", 23)]
    [InlineData("CPU/MULT/CPUMULT.exe", 14)]
    [InlineData("CPU/MULTU/CPUMULTU.exe", 14)]
    [InlineData("CPU/NOR/CPUNOR.exe", 7)]
    [InlineData("CPU/OR/CPUOR.exe", 7)]
    [InlineData("CPU/ORI/CPUORI.exe", 7)]
    [InlineData("CPU/SHIFT/SLL/CPUSLL.exe", 32)]
    [InlineData("CPU/SHIFT/SLLV/CPUSLLV.exe", 32)]
    [InlineData("CPU/SHIFT/SRA/CPUSRA.exe", 32)]
    [InlineData("CPU/SHIFT/SRAV/CPUSRAV.exe", 32)]
    [InlineData("CPU/SHIFT/SRL/CPUSRL.exe", 32)]
    [InlineData("CPU/SHIFT/SRLV/CPUSRLV.exe", 32)]
    [InlineData("CPU/SUB/CPUSUB.exe", 7)]
    [InlineData("CPU/SUBU/CPUSUBU.exe", 7)]
    [InlineData("CPU/XOR/CPUXOR.exe", 7)]
    [InlineData("CPU/XORI/CPUXORI.exe", 7)]
    [InlineData("GTE/AVSZ/GTEAVSZ.exe", 4)]
    [InlineData("GTE/NCLIP/GTENCLIP.exe", 2)]
    public void Every_check_passes(string program, int checks)
    {
        var console = new PlayStationConsole();
        console.Power();
        console.LoadExe(PsExe.Parse(TestRomLocator.ReadCached(CacheFolder + program)));

        const long budget = 200_000_000;
        while (console.Cpu.Cycles < budget && !InFinalLoop(console))
        {
            console.RunCycles(10_000);
        }

        console.Cpu.Cycles.Should().BeLessThan(budget, "the program should reach its final loop");
        var (passes, failures) = CountResults(console.Gpu);
        failures.Should().Be(0, $"{program} drew FAIL");
        passes.Should().Be(checks);
    }

    /// <summary>At the final <c>b .</c> or in its delay slot.</summary>
    private static bool InFinalLoop(PlayStationConsole console)
    {
        var pc = console.Cpu.Pc;
        return Instruction(console, pc) == BranchToSelf || Instruction(console, pc - 4) == BranchToSelf;
    }

    private static uint Instruction(PlayStationConsole console, uint pc) =>
        BinaryPrimitives.ReadUInt32LittleEndian(console.Ram[(int)(pc & 0x1FFFFC)..]);

    /// <summary>
    /// Counts the PASS and FAIL cells in the result column below the header: each is 8 rows tall, and rows of
    /// results touch, so the rows holding each colour are counted and divided by 8.
    /// </summary>
    private static (int Passes, int Failures) CountResults(PsGpu gpu)
    {
        var greenRows = 0;
        var redRows = 0;
        for (var y = 16; y < PsGpu.VramHeight; y++)
        {
            bool green = false, red = false;
            for (var x = 280; x < 312; x++)
            {
                var pixel = gpu.Pixel(x, y);
                green |= pixel == Green;
                red |= pixel == Red;
            }

            greenRows += green ? 1 : 0;
            redRows += red ? 1 : 0;
        }

        return (greenRows / 8, (redRows + 7) / 8);
    }
}
