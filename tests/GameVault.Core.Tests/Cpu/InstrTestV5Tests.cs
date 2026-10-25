using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.Cpu;

/// <summary>blargg's instr_test-v5 single-test ROMs (all NROM). The MMC1 combined ROMs come in Phase 4.</summary>
public class InstrTestV5Tests
{
    public static TheoryData<string> Roms { get; } = new(
        "01-basics", "02-implied", "03-immediate", "04-zero_page", "05-zp_xy", "06-absolute", "07-abs_xy",
        "08-ind_x", "09-ind_y", "10-branches", "11-stack", "12-jmp_jsr", "13-rts", "14-rti", "15-brk", "16-special");

    [TestRomTheory("instr_test-v5/01-basics.nes", "instr_test-v5/16-special.nes")]
    [MemberData(nameof(Roms))]
    public void Passes(string name)
    {
        var result = TestRomRunner.Run(TestRomLocator.ReadCached($"instr_test-v5/{name}.nes"));

        result.Status.Should().Be(TestRomStatus.Passed, result.ToString());
    }
}
