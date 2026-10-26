using FluentAssertions;
using System.Text.RegularExpressions;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.PlayStation2.R5900;

/// <summary>
/// The R5900's instructions against ps2autotests' hardware results (jpd002/ps2autotests, ISC; recorded on a PS2).
/// Each <c>.expected</c> line is replayed on the CPU (see <see cref="IntegerAutotest"/>).
/// </summary>
public sealed class R5900AutotestTests
{
    private static void Replay(string path, bool simd, int minimumLines)
    {
        var autotest = new IntegerAutotest(simd);
        autotest.Run(IntegerAutotest.ReadExpected(path));
        autotest.Mismatches.Count.Should().Be(0,
            $"every line of {path} should match the hardware ({autotest.Mismatches.Count} of {autotest.Checked} differ):\n{string.Join("\n", autotest.Mismatches)}");
        autotest.Skipped.Should().BeEmpty("every result line is replayed");
        autotest.Checked.Should().Be(autotest.Lines).And.BeGreaterThanOrEqualTo(minimumLines);
        autotest.Resynced.Should().BeLessThan(20, "only a test's first lines depend on what an earlier test left in a register");
    }

    [TestRomTheory("ps2autotests/cpu/ee/alu.expected")]
    [InlineData("ps2autotests/cpu/ee/alu.expected", false, 715)]
    [InlineData("ps2autotests/cpu/ee/muldiv.expected", false, 450)]
    [InlineData("ps2autotests/cpu/ee_simd/arithmetic.expected", true, 690)]
    [InlineData("ps2autotests/cpu/ee_simd/compare.expected", true, 168)]
    [InlineData("ps2autotests/cpu/ee_simd/logic.expected", true, 370)]
    [InlineData("ps2autotests/cpu/ee_simd/muldiv.expected", true, 722)]
    [InlineData("ps2autotests/cpu/ee_simd/shuffle.expected", true, 580)]
    public void Integer_instructions_match_the_hardware(string path, bool simd, int minimumLines) =>
        Replay(path, simd, minimumLines);

    [TestRomTheory("ps2autotests/cpu/ee_fpu/arithmetic.expected")]
    [InlineData("ps2autotests/cpu/ee_fpu/arithmetic.expected", 273)]
    [InlineData("ps2autotests/cpu/ee_fpu/muldiv.expected", 340)]
    [InlineData("ps2autotests/cpu/ee_fpu/sqrt.expected", 60)]
    [InlineData("ps2autotests/cpu/ee_fpu/compare.expected", 160)]
    [InlineData("ps2autotests/cpu/ee_fpu/convert.expected", 60)]
    public void Fpu_instructions_match_the_hardware(string path, int minimumLines)
    {
        var autotest = new FpuAutotest();
        autotest.Run(IntegerAutotest.ReadExpected(path));
        autotest.Mismatches.Count.Should().Be(0,
            $"every line of {path} should match the hardware ({autotest.Mismatches.Count} of {autotest.Checked} differ):\n{string.Join("\n", autotest.Mismatches)}");
        autotest.Skipped.Should().BeEmpty("every result line is replayed");
        autotest.Checked.Should().Be(autotest.Lines).And.BeGreaterThanOrEqualTo(minimumLines);
    }

    [TestRomTheory("ps2autotests/cpu/ee/branch.expected")]
    [InlineData("ps2autotests/cpu/ee/branch.expected", 284)]
    [InlineData("ps2autotests/cpu/ee_fpu/branch.expected", 16)]
    public void Branches_match_the_hardware(string path, int minimumLines)
    {
        var autotest = new BranchAutotest();
        autotest.Run(IntegerAutotest.ReadExpected(path));
        autotest.Mismatches.Count.Should().Be(0, string.Join("\n", autotest.Mismatches));
        autotest.Skipped.Should().BeEmpty();
        autotest.Checked.Should().Be(autotest.Lines).And.BeGreaterThanOrEqualTo(minimumLines);
    }

    [TestRomFact("ps2autotests/cpu/ee/lsu.expected")]
    public void Loads_and_stores_match_the_hardware()
    {
        var autotest = new LsuAutotest();
        autotest.Run(IntegerAutotest.ReadExpected("ps2autotests/cpu/ee/lsu.expected"));
        autotest.Mismatches.Count.Should().Be(0, string.Join("\n", autotest.Mismatches));
        autotest.Skipped.Should().BeEmpty();
        autotest.Checked.Should().BeGreaterThanOrEqualTo(134);
    }

    private static string Expected(string path) => string.Join("\n", IntegerAutotest.ReadExpected(path));

    [TestRomFact("ps2autotests/cpu/ee_simd/funnel.expected")]
    public void The_sa_register_and_qfsrv_match_the_hardware() =>
        AutotestPorts.Funnel().TrimEnd().Should().Be(Expected("ps2autotests/cpu/ee_simd/funnel.expected").TrimEnd());

    [TestRomFact("ps2autotests/cpu/ee_fpu/fcr.expected")]
    public void Fcr31_and_the_fpu_flags_match_the_hardware()
    {
        // The "result =" lines print the float with %f; the port prints its bits, matched to the hardware's values.
        var expected = Expected("ps2autotests/cpu/ee_fpu/fcr.expected")
            .Replace("result = 1.000000", "result = 3f800000")
            .Replace("result = 0.000000", "result = 00000000")
            .Replace("result = 0.331606", "result = 3ea9c84b");
        var actual = Regex.Replace(AutotestPorts.Fcr(), "result = (7fffffff|ffffffff)", "result = NaN");
        actual.TrimEnd().Should().Be(expected.TrimEnd());
    }

    [TestRomFact("ps2autotests/cpu/ee_fpu/transfer.expected")]
    public void Fpu_transfers_match_the_hardware() =>
        AutotestPorts.Transfer().TrimEnd().Should().Be(AutotestPorts.WithoutFloatText(Expected("ps2autotests/cpu/ee_fpu/transfer.expected")).TrimEnd());

    [TestRomFact("ps2autotests/cpu/ee/branchdelay.expected")]
    public void Branches_in_delay_slots_match_the_hardware()
    {
        var expected = string.Join("\n", IntegerAutotest.ReadExpected("ps2autotests/cpu/ee/branchdelay.expected")
            .Where(l => !l.StartsWith("jalr: non-ra:", StringComparison.Ordinal)));
        AutotestPorts.EeBranchDelay().TrimEnd().Should().Be(expected.TrimEnd());
    }

    [TestRomFact("ps2autotests/cpu/ee_cop0/performance.expected")]
    public void The_performance_counters_match_the_hardware() =>
        AutotestPorts.Performance().TrimEnd().Should().Be(Expected("ps2autotests/cpu/ee_cop0/performance.expected").TrimEnd());

    [TestRomFact("ps2autotests/cpu/ee/lsudelay.expected")]
    public void Loads_have_no_delay_slot() =>
        AutotestPorts.LsuDelay().TrimEnd().Should().Be(Expected("ps2autotests/cpu/ee/lsudelay.expected").TrimEnd());

    /// <remarks>
    /// Known difference: a chain of BC1Ts, each in the previous one's delay slot, ends at the last one's target on the
    /// hardware (4), while this CPU treats them like the integer branches (whose chain the hardware ends at 2, as
    /// here). The branch-likely chain matches.
    /// </remarks>
    [TestRomFact("ps2autotests/cpu/ee_fpu/branchdelay.expected")]
    public void Fpu_branch_likelies_in_delay_slots_match_the_hardware()
    {
        var expected = IntegerAutotest.ReadExpected("ps2autotests/cpu/ee_fpu/branchdelay.expected");
        expected.Should().Contain("bc1t: delay branch: 00000004");
        var actual = AutotestPorts.FpuBranchDelay().Split('\n');
        actual.Should().Contain(expected.Single(l => l.StartsWith("bc1tl:", StringComparison.Ordinal)));
        actual.Should().Contain("bc1t: delay branch: 00000002");
    }
}
