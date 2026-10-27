using System.Text;
using FluentAssertions;
using GameVault.Core.PlayStation2;
using GameVault.Core.Tests.Harness;
using GameVault.Core.Tests.PlayStation2.Homebrew;
using Xunit.Abstractions;

namespace GameVault.Core.Tests.PlayStation2.Console;

/// <summary>
/// The whole machine runs the same with the EE's and the VUs' recompilers as with the interpreters: every ps2autotests
/// program and the homebrew, frame by frame, with the same output, picture and save state.
/// </summary>
public sealed class Ps2RecompilerConsoleTests(ITestOutputHelper log)
{
    private const int Frames = 90;
    private const int CompareEvery = 15;

    public static TheoryData<string> Autotests() =>
    [
        "cpu/ee/alu", "cpu/ee/branch", "cpu/ee/branchdelay", "cpu/ee/lsu", "cpu/ee/lsudelay", "cpu/ee/muldiv",
        "cpu/ee_cop0/performance", "cpu/ee_fpu/arithmetic", "cpu/ee_fpu/branch", "cpu/ee_fpu/branchdelay",
        "cpu/ee_fpu/compare", "cpu/ee_fpu/convert", "cpu/ee_fpu/fcr", "cpu/ee_fpu/muldiv", "cpu/ee_fpu/sqrt",
        "cpu/ee_fpu/transfer", "cpu/ee_simd/arithmetic", "cpu/ee_simd/compare", "cpu/ee_simd/funnel",
        "cpu/ee_simd/logic", "cpu/ee_simd/muldiv", "cpu/ee_simd/shuffle", "cpu/vu0_macro/integer",
        "cpu/vu0_macro/transfer", "dma/dmac/cpcond", "dma/dmac/tagintr", "dma/spr/chain", "dma/spr/interleave",
        "dma/spr/normal", "dma/vif/basic", "dma/vif/intstall", "dma/vif/mskpath3", "dma/vif/stcycl", "dma/vif/stmod",
        "dma/vif/unpack", "gs/label", "gs/signal", "kernel/ee/interrupt/dmac_order", "kernel/ee/interrupt/intc_order",
        "kernel/ee/thread/delete", "kernel/ee/thread/exit", "kernel/ee/thread/resume", "kernel/ee/thread/sleep",
        "kernel/ee/thread/stat", "kernel/ee/thread/suspend", "kernel/ee/thread/terminate", "kernel/ee/thread/wakeup",
        "memory/ee/default", "vu/games/triace", "vu/lower/branch", "vu/lower/efu", "vu/lower/fdivdelay",
        "vu/lower/integer", "vu/lower/random", "vu/upper/clip",
    ];

    [TestRomTheory("ps2autotests/cpu/ee/alu.elf")]
    [MemberData(nameof(Autotests))]
    public void An_autotest_runs_the_same_recompiled(string test) =>
        Compare(test, TestRomLocator.ReadCached("ps2autotests/" + test + ".elf"));

    [Theory]
    [InlineData("vu1-path1")]
    [InlineData("gs-basic")]
    [InlineData("gs-texture")]
    [InlineData("mmi-fpu-check")]
    public void The_homebrew_runs_the_same_recompiled(string name) =>
        Compare(name, File.ReadAllBytes(Ps2HomebrewRunner.ElfPath(name)));

    private void Compare(string name, byte[] elf)
    {
        var interpretedOutput = new StringBuilder();
        var recompiledOutput = new StringBuilder();
        var interpreted = new Ps2Console(Ps2Boot.FromElf(elf, "host:test.elf")) { DebugOutput = c => interpretedOutput.Append(c) };
        var recompiled = new Ps2Console(Ps2Boot.FromElf(elf, "host:test.elf")) { DebugOutput = c => recompiledOutput.Append(c) };
        interpreted.Cpu.RecompilerEnabled = false;
        interpreted.Vu0.RecompilerEnabled = false;
        interpreted.Vu1.RecompilerEnabled = false;
        recompiled.Cpu.RecompilerEnabled.Should().BeTrue("the recompiler is on by default");
        recompiled.Vu1.RecompilerEnabled.Should().BeTrue("the VUs' recompiler is on by default too");

        for (var frame = 1; frame <= Frames; frame++)
        {
            interpreted.RunFrame();
            recompiled.RunFrame();
            if (frame % CompareEvery != 0 && frame != Frames)
            {
                continue;
            }

            recompiledOutput.ToString().Should().Be(interpretedOutput.ToString(), $"{name}, frame {frame}: the program's output");
            recompiled.Cpu.Cycles.Should().Be(interpreted.Cpu.Cycles, $"{name}, frame {frame}");
            recompiled.FrameBuffer.SequenceEqual(interpreted.FrameBuffer).Should().BeTrue($"{name}, frame {frame}: the picture");
            var a = interpreted.SaveSnapshot();
            var b = recompiled.SaveSnapshot();
            if (!a.AsSpan().SequenceEqual(b))
            {
                throw new Xunit.Sdk.XunitException($"{name}, frame {frame}: the save states differ at byte {a.AsSpan().CommonPrefixLength(b)} of {a.Length}");
            }
        }

        log.WriteLine($"{name}: {recompiled.Cpu.CompiledBlocks} blocks, {recompiled.Cpu.RecompiledInstructions * 100.0 / recompiled.Cpu.Cycles:F1}% of the EE's cycles in them; "
                      + $"VU0 {recompiled.Vu0.CompiledBlocks} blocks, VU1 {recompiled.Vu1.CompiledBlocks} blocks ({recompiled.Vu1.RecompiledCycles} of {recompiled.Vu1.Cycles} cycles)");
    }
}
