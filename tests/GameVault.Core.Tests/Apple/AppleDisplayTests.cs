using FluentAssertions;
using GameVault.Core.Apple;
using GameVault.Core.Cartridge;

namespace GameVault.Core.Tests.Apple;

/// <summary>Per-line video, monochrome monitors and the player's character ROM (outstanding-work plan §9).</summary>
public sealed class AppleDisplayTests
{
    private static AppleConsole Machine(byte[] program, byte[]? character = null, AppleMonitor monitor = AppleMonitor.Color)
    {
        var roms = AppleConsoleTests.TestRoms(program) with { Character = character, Monitor = monitor };
        var console = new AppleConsole(roms);
        console.Power();
        return console;
    }

    private static ushort Pixel(AppleConsole console, int x, int y, int dot = 0) => console.FrameBuffer[y * 2 * AppleVideo.Width + x * 2 + dot];

    [Fact]
    public void A_mode_switch_partway_down_the_screen_shows_from_that_line()
    {
        // Lo-res, full screen; wait about 100 lines (65 cycles each), then switch to text, and spin.
        //   LDA $C050; LDA $C052; LDX #$05; LDY #$00; DEY; BNE -3; DEX; BNE -6; LDA $C051; JMP *
        byte[] program =
        [
            0xAD, 0x50, 0xC0, 0xAD, 0x52, 0xC0, 0xA2, 0x05, 0xA0, 0x00, 0x88, 0xD0, 0xFD, 0xCA, 0xD0, 0xF8,
            0xAD, 0x51, 0xC0, 0x4C, 0x13, 0xD0,
        ];
        var console = Machine(program);
        for (var row = 0; row < 24; row++)
        {
            for (var column = 0; column < 40; column++)
            {
                console.Write((ushort)(AppleVideoAddress(row) + column), 0xFF); // lo-res white; text: a lit '?'-ish glyph
            }
        }

        console.RunFrame();

        Pixel(console, 0, 0).Should().Be(15, "the top lines were drawn in lo-res");
        var switched = Enumerable.Range(0, 192).First(y => Pixel(console, 0, y) != 15);
        switched.Should().BeInRange(60, 150, "the bottom lines were drawn after the switch to text");
    }

    private static int AppleVideoAddress(int row) => 0x400 + (row & 7) * 0x80 + (row >> 3) * 0x28;

    [Fact]
    public void A_monochrome_monitor_shows_every_lit_dot_in_its_colour()
    {
        // Hi-res, full screen, a lone violet pixel at (0, 0).
        var console = Machine([0xAD, 0x50, 0xC0, 0xAD, 0x52, 0xC0, 0xAD, 0x57, 0xC0, 0x4C, 0x09, 0xD0], monitor: AppleMonitor.Amber);
        console.Write(0x2000, 0x01);
        console.RunFrame();

        var lit = Pixel(console, 0, 0);
        console.Palette[lit].Should().Be(0xFFFFB000u, "amber");
        console.Palette[Pixel(console, 1, 0)].Should().Be(0xFF000000u, "a monochrome monitor shows no artifact colour");

        console.Monitor = AppleMonitor.Color;
        console.Palette[AppleVideo.Violet].Should().NotBe(0xFFFFB000u);
    }

    /// <summary>A II/II+-style 2 KB ROM made from GameVault's font: indexed by the low six bits, dots active low, leftmost in bit 0.</summary>
    private static byte[] TwoKilobyteRom()
    {
        var rom = new byte[2048];
        for (var ascii = 0x20; ascii < 0x60; ascii++)
        {
            for (var row = 0; row < 7; row++)
            {
                var dots = AppleFont.Row(ascii, row);
                var value = 0;
                for (var d = 0; d < 7; d++)
                {
                    if ((dots & (1 << (6 - d))) != 0)
                    {
                        value |= 1 << d;
                    }
                }

                // Repeat the glyph through every 64-character set, as the 2513's dumps do.
                for (var set = 0; set < 4; set++)
                {
                    rom[((set * 64) + (ascii & 0x3F)) * 8 + row] = (byte)~value;
                }
            }

            for (var set = 0; set < 4; set++)
            {
                rom[((set * 64) + (ascii & 0x3F)) * 8 + 7] = 0xFF;
            }
        }

        return rom;
    }

    [Fact]
    public void A_character_rom_s_layout_is_found_and_its_glyphs_draw_the_text()
    {
        var rom = TwoKilobyteRom();
        for (var set = 0; set < 4; set++)
        {
            rom[(((set * 64) + 0x01) * 8) + 0] = 0x00; // 'A' row 0 in the ROM: every dot lit, unlike GameVault's own .###.
        }

        var font = new AppleCharacterRom(rom);
        font.Row('B', 3).Should().Be(AppleFont.Row('B', 3));
        font.Row('A', 0).Should().Be(0x7F);

        var console = Machine([0x4C, 0x00, 0xD0], rom);
        console.HasCharacterRom.Should().BeTrue();
        console.Write(0x0400, 0xC1); // 'A'
        console.RunFrame();
        Pixel(console, 0, 0).Should().Be(AppleVideo.White, "the ROM's own 'A' is drawn");
    }

    [Fact]
    public void A_file_that_isn_t_a_character_rom_is_refused()
    {
        var act = () => new AppleCharacterRom(new byte[2048]);
        act.Should().Throw<RomFormatException>();
        var size = () => new AppleCharacterRom(new byte[1000]);
        size.Should().Throw<RomFormatException>();
    }
}
