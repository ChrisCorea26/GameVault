using FluentAssertions;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.PlayStation2.Autotests;

/// <summary>
/// ps2autotests' EE programs (jpd002/ps2autotests, ISC; their output recorded on a PS2) run through the whole console on
/// GameVault's kernel: each prints through printf → fileio over SIF RPC, and the text must equal its <c>.expected</c>.
/// The IOP programs (<c>.irx</c>) need an IOP kernel and aren't run (left for later).
/// </summary>
public sealed class Ps2AutotestsTests
{
    public static TheoryData<string> Passing => new(
        "cpu/ee/alu", "cpu/ee/branch", "cpu/ee/branchdelay", "cpu/ee/lsu", "cpu/ee/lsudelay", "cpu/ee/muldiv",
        "cpu/ee_cop0/performance",
        "cpu/ee_fpu/arithmetic", "cpu/ee_fpu/branch", "cpu/ee_fpu/branchdelay", "cpu/ee_fpu/compare", "cpu/ee_fpu/convert",
        "cpu/ee_fpu/fcr", "cpu/ee_fpu/muldiv", "cpu/ee_fpu/sqrt", "cpu/ee_fpu/transfer",
        "cpu/ee_simd/arithmetic", "cpu/ee_simd/compare", "cpu/ee_simd/funnel", "cpu/ee_simd/logic", "cpu/ee_simd/muldiv",
        "cpu/ee_simd/shuffle",
        "cpu/vu0_macro/integer", "cpu/vu0_macro/transfer",
        "dma/dmac/cpcond", "dma/dmac/tagintr", "dma/spr/chain", "dma/spr/interleave", "dma/spr/normal",
        "dma/vif/basic", "dma/vif/mskpath3", "dma/vif/stcycl", "dma/vif/stmod", "dma/vif/unpack",
        "gs/label", "gs/signal",
        "kernel/ee/interrupt/dmac_order", "kernel/ee/interrupt/intc_order",
        "kernel/ee/thread/delete", "kernel/ee/thread/exit", "kernel/ee/thread/resume", "kernel/ee/thread/sleep",
        "kernel/ee/thread/stat", "kernel/ee/thread/suspend", "kernel/ee/thread/terminate", "kernel/ee/thread/wakeup",
        "memory/ee/default",
        "vu/games/triace", "vu/lower/branch", "vu/lower/fdivdelay", "vu/lower/integer", "vu/lower/random",
        "vu/upper/clip");

    /// <summary>
    /// The programs whose output still differs from the hardware's, with the reason, how many lines differ, and a hash of
    /// GameVault's whole output (so any change, better or worse, is noticed and the pin updated).
    /// </summary>
    public static readonly Dictionary<string, (string Reason, int DifferingLines, string OutputHash)> KnownDifferences = new()
    {
        ["dma/vif/intstall"] = (
            "VIFn_STAT.FQC: after the I-bit stall the hardware reads 0 quadwords in the FIFO, GameVault the ones left there",
            4, "C7C77491E375E84C"),
        ["vu/lower/efu"] = (
            "the EFU's series (EATAN, EEXP, ESIN, ELENG...) differ in the last bits; their exact algorithms aren't published",
            24, "1EC10F50D2F9D0F1"),
    };

    public static TheoryData<string> Pinned => new([.. KnownDifferences.Keys]);

    [TestRomTheory("ps2autotests/cpu/ee/alu.elf")]
    [MemberData(nameof(Pinned))]
    public void Program_differs_from_the_hardware_only_as_pinned(string test)
    {
        var result = Ps2AutotestRunner.Run(test);
        var (reason, differingLines, hash) = KnownDifferences[test];
        result.Fault.Should().BeNull(result.ToString());
        result.Exited.Should().BeTrue();
        var actual = result.Output.Split('\n');
        var expected = result.Expected.Split('\n');
        actual.Length.Should().Be(expected.Length, reason);
        var differing = actual.Zip(expected).Count(pair => pair.First != pair.Second);
        var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(result.Output)))[..16];
        $"{actualHash} {differing}".Should().Be($"{hash} {differingLines}", $"{reason}; the output changed:\n{result}");
    }

    [TestRomTheory("ps2autotests/cpu/ee/alu.elf")]
    [MemberData(nameof(Passing))]
    public void Program_prints_what_the_hardware_printed(string test)
    {
        var result = Ps2AutotestRunner.Run(test);
        result.Fault.Should().BeNull(result.ToString());
        result.Output.Should().Be(result.Expected, result.ToString());
        result.Exited.Should().BeTrue();
    }
}
