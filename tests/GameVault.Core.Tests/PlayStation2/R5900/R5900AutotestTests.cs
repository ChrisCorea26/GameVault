using FluentAssertions;
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
}
