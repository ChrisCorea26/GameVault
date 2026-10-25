using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.Ppu;

/// <summary>
/// PPU accuracy suites (SPEC §4.3), all NROM. Each test also checks the final screen against its golden
/// image, so a regression in rendering shows up even if the ROM's own checks still pass.
/// </summary>
public class PpuTestRomTests
{
    public static TheoryData<string> VblNmi { get; } = new(
        "01-vbl_basics", "02-vbl_set_time", "03-vbl_clear_time", "04-nmi_control", "05-nmi_timing",
        "06-suppression", "07-nmi_on_timing", "08-nmi_off_timing", "09-even_odd_frames", "10-even_odd_timing");

    public static TheoryData<string> SpriteHit { get; } = new(
        "01.basics", "02.alignment", "03.corners", "04.flip", "05.left_clip", "06.right_edge",
        "07.screen_bottom", "08.double_height", "09.timing_basics", "10.timing_order", "11.edge_timing");

    public static TheoryData<string> SpriteOverflow { get; } = new(
        "1.Basics", "2.Details", "3.Timing", "4.Obscure", "5.Emulator");

    public static TheoryData<string> Ppu2005 { get; } = new("palette_ram", "sprite_ram", "vbl_clear_time", "vram_access");

    [TestRomTheory("ppu_vbl_nmi/01-vbl_basics.nes", "ppu_vbl_nmi/10-even_odd_timing.nes")]
    [MemberData(nameof(VblNmi))]
    public void Ppu_vbl_nmi(string name)
    {
        var result = TestRomRunner.Run(TestRomLocator.ReadCached($"ppu_vbl_nmi/{name}.nes"), out var console);

        result.Status.Should().Be(TestRomStatus.Passed, result.ToString());
        GoldenImages.AssertMatches($"ppu_vbl_nmi/{name}", console.Ppu.FrameBuffer);
    }

    [TestRomTheory("sprite_hit_tests_2005.10.05/01.basics.nes")]
    [MemberData(nameof(SpriteHit))]
    public void Sprite_hit(string name)
    {
        var result = TestRomRunner.RunWithZeroPageResult(
            TestRomLocator.ReadCached($"sprite_hit_tests_2005.10.05/{name}.nes"), 0x00F8, out var console);

        result.Status.Should().Be(TestRomStatus.Passed, result.ToString());
        GoldenImages.AssertMatches($"sprite_hit/{name}", console.Ppu.FrameBuffer);
    }

    [TestRomTheory("sprite_overflow_tests/1.Basics.nes")]
    [MemberData(nameof(SpriteOverflow))]
    public void Sprite_overflow(string name)
    {
        var result = TestRomRunner.RunWithZeroPageResult(
            TestRomLocator.ReadCached($"sprite_overflow_tests/{name}.nes"), 0x00F8, out var console);

        result.Status.Should().Be(TestRomStatus.Passed, result.ToString());
        GoldenImages.AssertMatches($"sprite_overflow/{name}", console.Ppu.FrameBuffer);
    }

    [TestRomTheory("blargg_ppu_tests_2005.09.15b/palette_ram.nes")]
    [MemberData(nameof(Ppu2005))]
    public void Blargg_ppu_tests_2005(string name)
    {
        var result = TestRomRunner.RunWithZeroPageResult(
            TestRomLocator.ReadCached($"blargg_ppu_tests_2005.09.15b/{name}.nes"), 0x00F0, out var console);

        result.Status.Should().Be(TestRomStatus.Passed, result.ToString());
        GoldenImages.AssertMatches($"blargg_ppu_tests/{name}", console.Ppu.FrameBuffer);
    }

    [TestRomFact("ppu_open_bus/ppu_open_bus.nes")]
    public void Ppu_open_bus()
    {
        var result = TestRomRunner.Run(TestRomLocator.ReadCached("ppu_open_bus/ppu_open_bus.nes"));

        result.Status.Should().Be(TestRomStatus.Passed, result.ToString());
    }

    [TestRomFact("oam_read/oam_read.nes")]
    public void Oam_read()
    {
        var result = TestRomRunner.Run(TestRomLocator.ReadCached("oam_read/oam_read.nes"), out var console);

        result.Status.Should().Be(TestRomStatus.Passed, result.ToString());
        GoldenImages.AssertMatches("oam_read", console.Ppu.FrameBuffer);
    }

    [TestRomFact("nestest/nestest.nes")]
    public void Nestest_menu_screen()
    {
        var console = NesConsole.FromBytes(TestRomLocator.ReadCached("nestest/nestest.nes"));
        console.Power();
        for (var i = 0; i < 60; i++)
        {
            console.RunFrame();
        }

        GoldenImages.AssertMatches("nestest/menu", console.Ppu.FrameBuffer);
    }
}
