using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.Nintendo64;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.Nintendo64;

/// <summary>
/// A test that needs PeterLemon's N64 programs (tests/TestRoms/manifest-n64.tsv). They are about 160 MB, so only the
/// Nintendo 64 CI job fetches them (with <c>GAMEVAULT_RUN_N64=1</c>); elsewhere the tests run when they are cached.
/// </summary>
public sealed class N64TestRomTheoryAttribute : TheoryAttribute
{
    public N64TestRomTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("GAMEVAULT_RUN_N64") != "1" && !TestRomLocator.IsCached(PeterLemonN64Tests.CacheFolder + "CPUTest/CPU/ADD/CPUADD.N64"))
        {
            Skip = "PeterLemon's N64 programs aren't downloaded. Run tests/TestRoms/fetch-test-roms.sh manifest-n64.tsv to enable these tests.";
        }
    }
}

/// <summary>
/// PeterLemon's bare-metal N64 test programs (public domain; downloaded at test time, see tests/TestRoms/SOURCES.md).
/// Each runs instructions on known values and prints PASS in a green font or FAIL in a red one into a 32-bit
/// framebuffer at $00100000; the harness reads those words back by matching the font's glyphs. They boot with krom's
/// own boot code through the simulated PIF.
/// </summary>
public sealed class PeterLemonN64Tests
{
    internal const string CacheFolder = "n64-peterlemon/";
    private const int Framebuffer = 0x100000;

    private static readonly Lazy<(uint[] Green, uint[] Red)> Fonts = new(() => (
        ReadFont("CPUTest/CPU/ADD/FontGreen8x8.bin"),
        ReadFont("CPUTest/CPU/ADD/FontRed8x8.bin")));

    [N64TestRomTheory]
    [InlineData("CPUTest/CPU/ADD/CPUADD.N64", 14)]
    [InlineData("CPUTest/CPU/ADDU/CPUADDU.N64", 14)]
    [InlineData("CPUTest/CPU/AND/CPUAND.N64", 14)]
    [InlineData("CPUTest/CPU/DADD/CPUDADD.N64", 14)]
    [InlineData("CPUTest/CPU/DADDU/CPUDADDU.N64", 14)]
    [InlineData("CPUTest/CPU/DDIV/CPUDDIV.N64", 20)]
    [InlineData("CPUTest/CPU/DDIVU/CPUDDIVU.N64", 18)]
    [InlineData("CPUTest/CPU/DIV/CPUDIV.N64", 20)]
    [InlineData("CPUTest/CPU/DIVU/CPUDIVU.N64", 18)]
    [InlineData("CPUTest/CPU/DMULT/CPUDMULT.N64", 14)]
    [InlineData("CPUTest/CPU/DMULTU/CPUDMULTU.N64", 18)]
    [InlineData("CPUTest/CPU/DSUB/CPUDSUB.N64", 7)]
    [InlineData("CPUTest/CPU/DSUBU/CPUDSUBU.N64", 7)]
    [InlineData("CPUTest/CPU/LOADSTORE/LB/CPULB.N64", 14)]
    [InlineData("CPUTest/CPU/LOADSTORE/LD/CPULD.N64", 39)]
    [InlineData("CPUTest/CPU/LOADSTORE/LH/CPULH.N64", 14)]
    [InlineData("CPUTest/CPU/LOADSTORE/LW/CPULW.N64", 30)]
    [InlineData("CPUTest/CPU/LOADSTORE/SB/CPUSB.N64", 11)]
    [InlineData("CPUTest/CPU/LOADSTORE/SD/CPUSD.N64", 39)]
    [InlineData("CPUTest/CPU/LOADSTORE/SH/CPUSH.N64", 9)]
    [InlineData("CPUTest/CPU/LOADSTORE/SW/CPUSW.N64", 23)]
    [InlineData("CPUTest/CPU/MULT/CPUMULT.N64", 14)]
    [InlineData("CPUTest/CPU/MULTU/CPUMULTU.N64", 14)]
    [InlineData("CPUTest/CPU/NOR/CPUNOR.N64", 7)]
    [InlineData("CPUTest/CPU/OR/CPUOR.N64", 14)]
    [InlineData("CPUTest/CPU/SHIFT/DSLL/CPUDSLL.N64", 32)]
    [InlineData("CPUTest/CPU/SHIFT/DSLL32/CPUDSLL32.N64", 32)]
    [InlineData("CPUTest/CPU/SHIFT/DSLLV/CPUDSLLV.N64", 32)]
    [InlineData("CPUTest/CPU/SHIFT/DSRA/CPUDSRA.N64", 32)]
    [InlineData("CPUTest/CPU/SHIFT/DSRA32/CPUDSRA32.N64", 32)]
    [InlineData("CPUTest/CPU/SHIFT/DSRAV/CPUDSRAV.N64", 32)]
    [InlineData("CPUTest/CPU/SHIFT/DSRL/CPUDSRL.N64", 32)]
    [InlineData("CPUTest/CPU/SHIFT/DSRL32/CPUDSRL32.N64", 32)]
    [InlineData("CPUTest/CPU/SHIFT/DSRLV/CPUDSRLV.N64", 32)]
    [InlineData("CPUTest/CPU/SHIFT/SLL/CPUSLL.N64", 32)]
    [InlineData("CPUTest/CPU/SHIFT/SLLV/CPUSLLV.N64", 32)]
    [InlineData("CPUTest/CPU/SHIFT/SRA/CPUSRA.N64", 32)]
    [InlineData("CPUTest/CPU/SHIFT/SRAV/CPUSRAV.N64", 32)]
    [InlineData("CPUTest/CPU/SHIFT/SRL/CPUSRL.N64", 32)]
    [InlineData("CPUTest/CPU/SHIFT/SRLV/CPUSRLV.N64", 32)]
    [InlineData("CPUTest/CPU/SUB/CPUSUB.N64", 7)]
    [InlineData("CPUTest/CPU/SUBU/CPUSUBU.N64", 7)]
    [InlineData("CPUTest/CPU/XOR/CPUXOR.N64", 14)]
    [InlineData("CPUTest/CPU/LOADSTORE/LL_LLD_SC_SCD/LL_LLD_SC_SCD.N64", 9)]
    public void Cpu_programs_pass(string program, int checks) => AssertAllPass(program, checks, 640);

    /// <summary>COP0's registers and Cause as a cold boot leaves them, recorded on hardware.</summary>
    [N64TestRomTheory]
    [InlineData("CPUTest/CP0/COP0Cause/COP0Cause.N64", 5)]
    [InlineData("CPUTest/CP0/COP0Register/COP0Register.N64", 13)]
    public void Cop0_programs_pass(string program, int checks) => AssertAllPass(program, checks, 320);

    /// <summary>PI DMA from the cartridge with unaligned addresses and odd lengths, and the CPU's narrow ROM reads.</summary>
    [N64TestRomTheory]
    [InlineData("CPUTest/DMAAlignment-PI-cart/DMAAlignment-PI-ROM-FROM.N64", 28)]
    [InlineData("CPUTest/DMAAlignment-PI-cart/DMAAlignment-PI-ROM-FROM_large_2.N64", 17)]
    [InlineData("CPUTest/DMAAlignment-PI-cart/DMAAlignment-PI-ROM-FROM_large_4.N64", 17)]
    [InlineData("CPUTest/DMAAlignment-PI-cart/DMAAlignment-PI-ROM-FROM_large_6.N64", 17)]
    public void Pi_dma_programs_pass(string program, int checks) => AssertAllPass(program, checks, 640);

    [N64TestRomTheory]
    [InlineData("CPUTest/CP1/ABS/CP1ABS.N64", 14)]
    [InlineData("CPUTest/CP1/ADD/CP1ADD.N64", 14)]
    [InlineData("CPUTest/CP1/C/EQ/CP1CEQ.N64", 18)]
    [InlineData("CPUTest/CP1/C/F/CP1CF.N64", 18)]
    [InlineData("CPUTest/CP1/C/LE/CP1CLE.N64", 18)]
    [InlineData("CPUTest/CP1/C/LT/CP1CLT.N64", 18)]
    [InlineData("CPUTest/CP1/C/NGE/CP1CNGE.N64", 18)]
    [InlineData("CPUTest/CP1/C/NGL/CP1CNGL.N64", 18)]
    [InlineData("CPUTest/CP1/C/NGLE/CP1CNGLE.N64", 18)]
    [InlineData("CPUTest/CP1/C/NGT/CP1CNGT.N64", 18)]
    [InlineData("CPUTest/CP1/C/OLE/CP1COLE.N64", 18)]
    [InlineData("CPUTest/CP1/C/OLT/CP1COLT.N64", 18)]
    [InlineData("CPUTest/CP1/C/SEQ/CP1CSEQ.N64", 18)]
    [InlineData("CPUTest/CP1/C/SF/CP1CSF.N64", 18)]
    [InlineData("CPUTest/CP1/C/UEQ/CP1CUEQ.N64", 18)]
    [InlineData("CPUTest/CP1/C/ULE/CP1CULE.N64", 18)]
    [InlineData("CPUTest/CP1/C/ULT/CP1CULT.N64", 18)]
    [InlineData("CPUTest/CP1/C/UN/CP1CUN.N64", 18)]
    [InlineData("CPUTest/CP1/CEIL/CP1CEIL.N64", 28)]
    [InlineData("CPUTest/CP1/CVT/CP1CVT.N64", 30)]
    [InlineData("CPUTest/CP1/DIV/CP1DIV.N64", 14)]
    [InlineData("CPUTest/CP1/FLOOR/CP1FLOOR.N64", 28)]
    [InlineData("CPUTest/CP1/MUL/CP1MUL.N64", 14)]
    [InlineData("CPUTest/CP1/NEG/CP1NEG.N64", 14)]
    [InlineData("CPUTest/CP1/ROUND/CP1ROUND.N64", 28)]
    [InlineData("CPUTest/CP1/SQRT/CP1SQRT.N64", 8)]
    [InlineData("CPUTest/CP1/SUB/CP1SUB.N64", 14)]
    [InlineData("CPUTest/CP1/TRUNC/CP1TRUNC.N64", 28)]
    public void Fpu_programs_pass(string program, int checks) => AssertAllPass(program, checks, 640);

    [N64TestRomTheory]
    [InlineData("RSPTest/CPU/ADD/RSPCPUADD.N64", 14, 640)]
    [InlineData("RSPTest/CPU/ADDU/RSPCPUADDU.N64", 14, 640)]
    [InlineData("RSPTest/CPU/AND/RSPCPUAND.N64", 14, 640)]
    [InlineData("RSPTest/CPU/NOR/RSPCPUNOR.N64", 7, 640)]
    [InlineData("RSPTest/CPU/OR/RSPCPUOR.N64", 14, 640)]
    [InlineData("RSPTest/CPU/SHIFT/SLL/RSPCPUSLL.N64", 32, 640)]
    [InlineData("RSPTest/CPU/SHIFT/SLLV/RSPCPUSLLV.N64", 32, 640)]
    [InlineData("RSPTest/CPU/SHIFT/SRA/RSPCPUSRA.N64", 32, 640)]
    [InlineData("RSPTest/CPU/SHIFT/SRAV/RSPCPUSRAV.N64", 32, 640)]
    [InlineData("RSPTest/CPU/SHIFT/SRL/RSPCPUSRL.N64", 32, 640)]
    [InlineData("RSPTest/CPU/SHIFT/SRLV/RSPCPUSRLV.N64", 32, 640)]
    [InlineData("RSPTest/CPU/SUB/RSPCPUSUB.N64", 7, 640)]
    [InlineData("RSPTest/CPU/SUBU/RSPCPUSUBU.N64", 7, 640)]
    [InlineData("RSPTest/CPU/XOR/RSPCPUXOR.N64", 14, 640)]
    [InlineData("RSPTest/MEM/IMEM/RSPIMEM.N64", 2, 320)]
    [InlineData("RSPTest/CP2/SORT/RSPSORT.N64", 3, 320)]
    [InlineData("RSPTest/CP2/LOADSTORE/LTV/RSPCP2LTV.N64", 16, 640)]
    [InlineData("RSPTest/CP2/LOADSTORE/LWV/RSPCP2LWV.N64", 16, 640)]
    [InlineData("RSPTest/CP2/LOADSTORE/TransposeMatrix/RSPTransposeMatrix.N64", 16, 640)]
    [InlineData("RSPTest/CP2/LOADSTORE/TransposeMatrixVMOV/RSPTransposeMatrixVMOV.N64", 16, 640)]
    [InlineData("RSPTest/CP2/RESERVED/V056/RSPCP2V056.N64", 8, 640)]
    [InlineData("RSPTest/CP2/RESERVED/V073/RSPCP2V073.N64", 8, 640)]
    [InlineData("RSPTest/CP2/RESERVED/VACC/RSPCP2VACC.N64", 8, 640)]
    [InlineData("RSPTest/CP2/RESERVED/VADDB/RSPCP2VADDB.N64", 8, 640)]
    [InlineData("RSPTest/CP2/RESERVED/VEXTN/RSPCP2VEXTN.N64", 8, 640)]
    [InlineData("RSPTest/CP2/RESERVED/VEXTQ/RSPCP2VEXTQ.N64", 8, 640)]
    [InlineData("RSPTest/CP2/RESERVED/VEXTT/RSPCP2VEXTT.N64", 8, 640)]
    [InlineData("RSPTest/CP2/RESERVED/VMULQ/RSPCP2VMULQ.N64", 8, 640)]
    [InlineData("RSPTest/CP2/RESERVED/VRNDP/RSPCP2VRNDP.N64", 8, 640)]
    [InlineData("RSPTest/CP2/RESERVED/VSAC/RSPCP2VSAC.N64", 8, 640)]
    [InlineData("RSPTest/CP2/RESERVED/VSUBB/RSPCP2VSUBB.N64", 8, 640)]
    [InlineData("RSPTest/CP2/RESERVED/VSUT/RSPCP2VSUT.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VABS/RSPCP2VABS.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VADD/RSPCP2VADD.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VAND/RSPCP2VAND.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VCL/RSPCP2VCL.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VCR/RSPCP2VCR.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VEQ/RSPCP2VEQ.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VLT/RSPCP2VLT.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VMACF/RSPCP2VMACF.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VMADL/RSPCP2VMADL.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VMADN/RSPCP2VMADN.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VMUDL/RSPCP2VMUDL.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VMUDN/RSPCP2VMUDN.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VMULF/RSPCP2VMULF.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VNOP/RSPCP2VNOP.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VOR/RSPCP2VOR.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VRCP/RSPCP2VRCP.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VRCPH/RSPCP2VRCPH.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VRCPL/RSPCP2VRCPL.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VSAR/RSPCP2VSAR.N64", 6, 640)]
    [InlineData("RSPTest/CP2/VSUB/RSPCP2VSUB.N64", 8, 640)]
    [InlineData("RSPTest/CP2/VXOR/RSPCP2VXOR.N64", 8, 640)]
    public void Rsp_programs_pass(string program, int checks, int width) => AssertAllPass(program, checks, width);

    private static void AssertAllPass(string program, int checks, int width)
    {
        var console = Boot(program);
        var (passes, failures) = (0, 0);
        for (var frame = 0; frame < 600; frame++)
        {
            console.RunFrame();
            if (frame % 10 == 9)
            {
                (passes, failures) = CountResults(console.Rdram, width);
                if (passes + failures >= checks)
                {
                    break;
                }
            }
        }

        failures.Should().Be(0, $"{program} printed FAIL");
        passes.Should().Be(checks, $"{program} should print PASS for every check");
    }

    internal static N64Console Boot(string program)
    {
        var console = new N64Console(new N64Cartridge(TestRomLocator.ReadCached(CacheFolder + program)));
        console.Power();
        return console;
    }

    /// <summary>Counts the words PASS (green) and FAIL (red) in a 32-bit framebuffer at $00100000.</summary>
    internal static (int Passes, int Failures) CountResults(byte[] rdram, int width)
    {
        var (green, red) = Fonts.Value;
        var passes = 0;
        var failures = 0;
        var height = width * 3 / 4;
        for (var y = 0; y + 8 <= height; y++)
        {
            for (var x = 0; x + 32 <= width; x++)
            {
                if (Word(rdram, width, x, y, green, "PASS"))
                {
                    passes++;
                }
                else if (Word(rdram, width, x, y, red, "FAIL"))
                {
                    failures++;
                }
            }
        }

        return (passes, failures);
    }

    private static bool Word(byte[] rdram, int width, int x, int y, uint[] font, string word)
    {
        for (var c = 0; c < word.Length; c++)
        {
            var glyph = word[c] * 64;
            for (var row = 0; row < 8; row++)
            {
                for (var column = 0; column < 8; column++)
                {
                    var at = Framebuffer + (((y + row) * width) + x + c * 8 + column) * 4;
                    if (BinaryPrimitives.ReadUInt32BigEndian(rdram.AsSpan(at)) != font[glyph + row * 8 + column])
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static uint[] ReadFont(string path)
    {
        var bytes = TestRomLocator.ReadCached(CacheFolder + path);
        var words = new uint[bytes.Length / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i * 4));
        }

        return words;
    }
}
