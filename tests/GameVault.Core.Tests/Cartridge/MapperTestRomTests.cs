using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.Cartridge;

/// <summary>Tier 1 mapper suites (SPEC §12 Phase 4) and the combined ROMs that need those boards.</summary>
public class MapperTestRomTests
{
    public static TheoryData<string> Mmc3 { get; } = new(
        "1-clocking", "2-details", "3-A12_clocking", "4-scanline_timing", "5-MMC3");

    public static TheoryData<string> Combined { get; } = new(
        "instr_test-v5/all_instrs",
        "instr_test-v5/official_only",
        "ppu_vbl_nmi/ppu_vbl_nmi",
        "ppu_read_buffer/test_ppu_read_buffer",
        "cpu_dummy_writes/cpu_dummy_writes_oam",
        "cpu_dummy_writes/cpu_dummy_writes_ppumem");

    [TestRomTheory("mmc3_test_2/1-clocking.nes")]
    [MemberData(nameof(Mmc3))]
    public void Mmc3_test_2(string name) => AssertPasses($"mmc3_test_2/{name}.nes");

    [TestRomTheory("instr_test-v5/all_instrs.nes")]
    [MemberData(nameof(Combined))]
    public void Combined_roms(string name) => AssertPasses($"{name}.nes", maxFrames: 60 * 60 * 3);

    /// <summary>
    /// 6-MMC3_alt expects the old (NEC "MMC3A") IRQ behavior. The iNES header can't say so, so the test
    /// rewrites it as NES 2.0 submapper 4, which is how a correct dump would declare that board.
    /// </summary>
    [TestRomFact("mmc3_test_2/6-MMC3_alt.nes")]
    public void Mmc3_alt_passes_as_submapper_4()
    {
        var rom = TestRomLocator.ReadCached("mmc3_test_2/6-MMC3_alt.nes");
        rom[7] = (byte)((rom[7] & 0xF0) | 0x08); // NES 2.0
        rom[8] = 0x40;                            // submapper 4
        rom[9] = 0x00;
        rom[10] = 0x07;                           // 8 KB PRG-RAM
        rom[11] = rom[12] = rom[13] = rom[14] = rom[15] = 0;

        var result = TestRomRunner.Run(rom);
        result.Status.Should().Be(TestRomStatus.Passed, result.ToString());
    }

    /// <summary>cpu_dummy_reads (CNROM) only reports on screen, so its final screen is checked.</summary>
    [TestRomFact("cpu_dummy_reads/cpu_dummy_reads.nes")]
    public void Cpu_dummy_reads()
    {
        var console = NesConsole.FromBytes(TestRomLocator.ReadCached("cpu_dummy_reads/cpu_dummy_reads.nes"));
        console.Power();
        for (var frame = 0; frame < 600; frame++)
        {
            console.RunFrame();
        }

        GoldenImages.AssertMatches("cpu_dummy_reads", console.Ppu.FrameBuffer);
    }

    private static void AssertPasses(string path, int maxFrames = 60 * 60)
    {
        var result = TestRomRunner.Run(TestRomLocator.ReadCached(path), maxFrames);
        result.Status.Should().Be(TestRomStatus.Passed, result.ToString());
    }
}
