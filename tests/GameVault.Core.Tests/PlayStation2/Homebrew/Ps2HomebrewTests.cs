using FluentAssertions;
using GameVault.Core.Snes;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.PlayStation2.Homebrew;

/// <summary>
/// GameVault's PS2 homebrew (tests/Ps2Homebrew, GameVault's own code, committed with its built ELFs) through the whole
/// console: each program signs its status block, the probe points of its README show the colours it lists (to the
/// frame's 15-bit precision), and the frame is pinned as a golden image.
/// </summary>
public sealed class Ps2HomebrewTests
{
    private static void Probe(GameVault.Core.PlayStation2.Ps2Console console, int x, int y, int r, int g, int b, int tolerance = 10)
    {
        var (pr, pg, pb) = Ps2HomebrewRunner.Pixel(console, x, y);
        var error = Math.Max(Math.Abs(pr - r), Math.Max(Math.Abs(pg - g), Math.Abs(pb - b)));
        error.Should().BeLessThanOrEqualTo(tolerance, $"pixel ({x}, {y}) is ({pr}, {pg}, {pb}), expected about ({r}, {g}, {b})");
    }

    private static GameVault.Core.PlayStation2.Ps2Console RunSigned(string name, uint id)
    {
        var console = Ps2HomebrewRunner.Run(name);
        Ps2HomebrewRunner.Word(console, 0).Should().Be(Ps2HomebrewRunner.Signature, "the program finished its drawing");
        Ps2HomebrewRunner.Word(console, 1).Should().Be(id);
        console.ScreenWidth.Should().Be(640);
        console.ScreenHeight.Should().Be(448);
        return console;
    }

    private static void Golden(string name, GameVault.Core.PlayStation2.Ps2Console console) =>
        GoldenImages.AssertMatches("ps2/" + name, console.FrameBuffer, console.ScreenWidth, console.ScreenHeight, SnesPpu.Palette);

    [Fact]
    public void Gs_basic_draws_every_primitive_through_path3()
    {
        var c = RunSigned("gs-basic", 1);
        Probe(c, 300, 220, 16, 16, 64);
        Probe(c, 3, 3, 255, 255, 255);
        Probe(c, 636, 444, 255, 255, 255);
        Probe(c, 80, 80, 255, 0, 0);
        Probe(c, 190, 190, 16, 16, 64);
        Probe(c, 244, 44, 255, 0, 0, 16);
        Probe(c, 316, 120, 6, 121, 128, 10);
        Probe(c, 392, 42, 0, 255, 0, 16);
        Probe(c, 470, 70, 255, 255, 0);
        Probe(c, 570, 170, 0, 255, 255);
        Probe(c, 120, 260, 255, 0, 255);
        Probe(c, 180, 320, 255, 128, 0);
        Probe(c, 120, 380, 255, 255, 255);
        Probe(c, 60, 320, 0, 128, 255);
        Probe(c, 320, 260, 255, 255, 255);
        Probe(c, 420, 320, 0, 255, 0);
        Probe(c, 250, 280, 255, 0, 0, 24);
        Probe(c, 390, 280, 0, 0, 255, 24);
        Probe(c, 260, 340, 255, 255, 0);
        Probe(c, 530, 350, 128, 128, 128);
        int[][] points = [[255, 0, 0], [0, 255, 0], [0, 0, 255], [255, 255, 0], [0, 255, 255], [255, 0, 255], [255, 255, 255], [255, 128, 0]];
        for (var i = 0; i < 8; i++)
        {
            Probe(c, 460 + 20 * i, 260, points[i][0], points[i][1], points[i][2]);
            Probe(c, 470 + 20 * i, 260, 16, 16, 64);
        }

        Golden("gs-basic", c);
    }

    [Fact]
    public void Gs_texture_draws_textures_cluts_filtering_blending_and_z()
    {
        var c = RunSigned("gs-texture", 2);
        Probe(c, 620, 20, 48, 48, 48);
        Probe(c, 40, 40, 255, 255, 255);
        Probe(c, 56, 40, 0, 0, 255);
        Probe(c, 56, 56, 255, 255, 255);
        Probe(c, 200, 40, 127, 255, 255);
        Probe(c, 216, 40, 0, 0, 255);
        Probe(c, 384, 64, 255, 255, 255);
        Probe(c, 448, 64, 0, 0, 255);
        Probe(c, 416, 64, 128, 128, 255, 12);
        Probe(c, 36, 196, 0, 0, 64);
        Probe(c, 156, 196, 255, 0, 64);
        Probe(c, 100, 196, 136, 0, 64);
        Probe(c, 36, 316, 0, 255, 64);
        Probe(c, 100, 260, 136, 136, 64);
        Probe(c, 156, 316, 255, 255, 64);
        Probe(c, 208, 208, 0, 0, 0);
        Probe(c, 240, 208, 255, 0, 0);
        Probe(c, 272, 208, 0, 255, 0);
        Probe(c, 304, 208, 0, 0, 255);
        Probe(c, 208, 240, 255, 255, 0);
        Probe(c, 304, 304, 128, 128, 0);
        Probe(c, 356, 196, 255, 255, 255);
        Probe(c, 356, 316, 0, 0, 255);
        Probe(c, 604, 196, 0, 0, 255);
        Probe(c, 176, 120, 151, 24, 24);
        Probe(c, 140, 170, 151, 24, 24);
        Probe(c, 420, 360, 255, 0, 255);
        Probe(c, 520, 355, 0, 255, 0);
        Probe(c, 380, 420, 0, 255, 0);
        Probe(c, 590, 430, 255, 0, 255);
        Golden("gs-texture", c);
    }

    [Fact]
    public void Vu1_path1_draws_through_vif1_a_vu1_microprogram_and_all_three_paths()
    {
        var c = RunSigned("vu1-path1", 3);
        Probe(c, 40, 400, 24, 24, 24);
        Probe(c, 320, 112, 238, 8, 8, 12);
        Probe(c, 430, 224, 0, 255, 0, 16);
        Probe(c, 210, 224, 0, 0, 255, 16);
        Probe(c, 320, 330, 225, 240, 240, 12);
        Probe(c, 320, 224, 0, 128, 128, 12);
        Probe(c, 500, 390, 255, 255, 0);
        Probe(c, 370, 290, 255, 255, 0);
        Probe(c, 16, 16, 0, 255, 0);
        Golden("vu1-path1", c);
    }

    [Fact]
    public void Mmi_fpu_check_passes_all_sixteen_checks()
    {
        var c = RunSigned("mmi-fpu-check", 4);
        Ps2HomebrewRunner.Word(c, 2).Should().Be(16, "every check passes");
        Ps2HomebrewRunner.Word(c, 3).Should().Be(0, "no check fails");
        for (var i = 0; i < 16; i++)
        {
            Probe(c, 68 + 72 * (i % 8), 160 + 104 * (i / 8), 0, 255, 0);
        }

        Probe(c, 320, 380, 0, 255, 0);
        Golden("mmi-fpu-check", c);
    }

    [Fact]
    public void Two_runs_give_identical_frames()
    {
        var a = Ps2HomebrewRunner.Run("vu1-path1");
        var b = Ps2HomebrewRunner.Run("vu1-path1");
        a.FrameCount.Should().Be(b.FrameCount);
        a.FrameBuffer.SequenceEqual(b.FrameBuffer).Should().BeTrue();
        a.Cpu.Cycles.Should().Be(b.Cpu.Cycles);
        a.SaveSnapshot().AsSpan().SequenceEqual(b.SaveSnapshot()).Should().BeTrue("the whole machine is deterministic");
    }
}
