using FluentAssertions;
using GameVault.Core.Genesis;
using GameVault.Core.Input;

namespace GameVault.Core.Tests.Genesis;

/// <summary>The Genesis / Mega Drive (SPEC §7.11, Phase 13), with programs and tiles written here (no third-party ROMs).</summary>
public sealed class GenesisTests
{
    // ---- Test cartridges ----

    /// <summary>A ROM with a header ("SEGA", region), SSP $FFFE00, and <paramref name="program"/> (68000 words) at $200.</summary>
    internal static byte[] Rom(ushort[] program, string region = "U", int size = 0x10000, Action<byte[]>? patch = null)
    {
        var rom = new byte[size];
        void Long(int at, uint value)
        {
            rom[at] = (byte)(value >> 24);
            rom[at + 1] = (byte)(value >> 16);
            rom[at + 2] = (byte)(value >> 8);
            rom[at + 3] = (byte)value;
        }

        Long(0, 0xFFFFFE00);
        Long(4, 0x200);
        "SEGA GENESIS    "u8.CopyTo(rom.AsSpan(0x100));
        "GAMEVAULT TEST"u8.CopyTo(rom.AsSpan(0x150));
        System.Text.Encoding.ASCII.GetBytes(region.PadRight(3)).CopyTo(rom, 0x1F0);
        for (var i = 0; i < program.Length; i++)
        {
            rom[0x200 + i * 2] = (byte)(program[i] >> 8);
            rom[0x200 + i * 2 + 1] = (byte)program[i];
        }

        patch?.Invoke(rom);
        return rom;
    }

    /// <summary>MOVE.W #value,$C00004 (a VDP register or command word).</summary>
    private static ushort[] VdpControl(ushort value) => [0x33FC, value, 0x00C0, 0x0004];

    private static ushort[] VdpData(ushort value) => [0x33FC, value, 0x00C0, 0x0000];

    /// <summary>MOVE.L #command,$C00004.</summary>
    private static ushort[] VdpCommand(uint command) => [0x23FC, (ushort)(command >> 16), (ushort)command, 0x00C0, 0x0004];

    private static ushort[] Program(params ushort[][] parts) => [.. parts.SelectMany(p => p), 0x60FE]; // ... BRA.S *

    // ---- Cartridge ----

    [Fact]
    public void Smd_images_are_deinterleaved_and_the_header_gives_region_and_sram()
    {
        var rom = Rom([0x4E71], region: "E", size: 0x8000, patch: r =>
        {
            "RA"u8.CopyTo(r.AsSpan(0x1B0));
            r[0x1B2] = 0xF8;
            r[0x1B3] = 0x20;
            r[0x1B4] = 0x00; r[0x1B5] = 0x20; r[0x1B6] = 0x00; r[0x1B7] = 0x01; // $200001
            r[0x1B8] = 0x00; r[0x1B9] = 0x20; r[0x1BA] = 0x3F; r[0x1BB] = 0xFF; // $203FFF
        });

        // Build the .smd: a 512-byte header ($AA $BB at 8–9), then odd bytes, then even bytes, per 16 KB.
        var smd = new byte[0x200 + rom.Length];
        smd[8] = 0xAA;
        smd[9] = 0xBB;
        for (var block = 0; block < rom.Length / 0x4000; block++)
        {
            for (var i = 0; i < 0x2000; i++)
            {
                smd[0x200 + block * 0x4000 + i] = rom[block * 0x4000 + i * 2 + 1];
                smd[0x200 + block * 0x4000 + 0x2000 + i] = rom[block * 0x4000 + i * 2];
            }
        }

        var cartridge = new GenesisCartridge(smd);

        cartridge.Rom.ToArray().Should().Equal(rom);
        cartridge.Region.Should().Be(GenesisRegion.Europe);
        cartridge.Title.Should().Be("GAMEVAULT TEST");
        cartridge.HasBattery.Should().BeTrue();
        cartridge.BatteryRam.Length.Should().Be(0x2000, "8 KB on the odd bytes of $200000–$203FFF");
        cartridge.WriteByte(0x200001, 0x5A);
        cartridge.WriteByte(0x200002, 0x77); // even: not connected
        cartridge.ReadByte(0x200001).Should().Be(0x5A);
        cartridge.BatteryRam[0].Should().Be(0x5A);
        cartridge.BatteryRam[1].Should().Be(0xFF);
        GenesisCartridge.LooksLikeGenesis(rom).Should().BeTrue();
    }

    [Fact]
    public void Roms_over_4_mb_use_the_ssf2_banks()
    {
        var rom = Rom([0x4E71], size: 0x500000);
        rom[0x480000] = 0xAB; // page 9
        var cartridge = new GenesisCartridge(rom);
        cartridge.ReadByte(0x380000).Should().Be(0x00, "window 7 shows page 7 at first");

        cartridge.WriteControl(0xA130FF, 9);

        cartridge.ReadByte(0x380000).Should().Be(0xAB);
    }

    // ---- VDP ----

    private static GenesisVdp Vdp(params ushort[] registers)
    {
        var vdp = new GenesisVdp(pal: false);
        vdp.Power();
        Register(vdp, 0, 0x04);
        Register(vdp, 1, 0x44);  // display on, mode 5
        Register(vdp, 2, 0x30);  // plane A at $C000
        Register(vdp, 4, 0x07);  // plane B at $E000
        Register(vdp, 5, 0x78);  // sprites at $F000
        Register(vdp, 12, 0x81); // H40
        Register(vdp, 13, 0x3F); // H scroll at $FC00
        Register(vdp, 15, 0x02);
        Register(vdp, 16, 0x01); // 64×32 planes
        for (var i = 0; i + 1 < registers.Length; i += 2)
        {
            Register(vdp, registers[i], (byte)registers[i + 1]);
        }

        return vdp;
    }

    private static void Register(GenesisVdp vdp, int index, byte value) => vdp.WriteControl((ushort)(0x8000 | (index << 8) | value));

    private static void Command(GenesisVdp vdp, uint command)
    {
        vdp.WriteControl((ushort)(command >> 16));
        vdp.WriteControl((ushort)command);
    }

    private static uint VramWrite(int address) => 0x40000000u | ((uint)(address & 0x3FFF) << 16) | (uint)(address >> 14);

    private static uint CramWrite(int address) => 0xC0000000u | ((uint)address << 16);

    private static void WriteVram(GenesisVdp vdp, int address, params ushort[] words)
    {
        Command(vdp, VramWrite(address));
        foreach (var word in words)
        {
            vdp.WriteData(word);
        }
    }

    private static void SetColor(GenesisVdp vdp, int index, ushort color)
    {
        Command(vdp, CramWrite(index * 2));
        vdp.WriteData(color);
    }

    /// <summary>A solid tile of colour <paramref name="color"/> at tile <paramref name="tile"/>.</summary>
    private static void SolidTile(GenesisVdp vdp, int tile, int color)
    {
        var word = (ushort)(color * 0x1111);
        WriteVram(vdp, tile * 32, Enumerable.Repeat(word, 16).ToArray());
    }

    private static ushort Pixel(GenesisVdp vdp, int x, int y) => vdp.Frame[y * vdp.FrameWidth + x];

    private static ushort Normal(ushort cram) => (ushort)(512 + (((cram >> 1) & 7) | (((cram >> 5) & 7) << 3) | (((cram >> 9) & 7) << 6)));

    private static void Render(GenesisVdp vdp, int lines = 16)
    {
        for (var line = 0; line < lines; line++)
        {
            vdp.BeginLine(line);
            vdp.BeginHBlank();
        }
    }

    [Fact]
    public void Interlace_mode_2_draws_8x16_tiles_into_a_448_line_picture_one_field_at_a_time()
    {
        var vdp = Vdp(12, 0x87, 7, 0x00); // H40 and LSM = 11: interlace mode 2
        SetColor(vdp, 1, 0x000E); // red
        SetColor(vdp, 2, 0x00E0); // green
        // Tile 1 (64 bytes at $40): rows 0–7 colour 1, rows 8–15 colour 2. Plane A's first entry uses it.
        WriteVram(vdp, 64, [.. Enumerable.Repeat((ushort)0x1111, 16), .. Enumerable.Repeat((ushort)0x2222, 16)]);
        WriteVram(vdp, 0xC000, 0x0001);

        // Two frames: one even field and one odd, each drawing every other line.
        for (var frame = 0; frame < 2; frame++)
        {
            for (var line = 0; line < 262; line++)
            {
                vdp.BeginLine(line);
                vdp.BeginHBlank();
            }
        }

        vdp.Interlace2.Should().BeTrue();
        vdp.FrameHeight.Should().Be(448);
        for (var y = 0; y < 16; y++)
        {
            Pixel(vdp, 3, y).Should().Be(Normal(y < 8 ? (ushort)0x000E : (ushort)0x00E0), $"line {y} of the doubled picture");
        }

        Pixel(vdp, 3, 16).Should().Be(Normal(0x0000), "the next tile row down is tile 0, transparent");
    }

    [Fact]
    public void Planes_draw_tiles_with_priority_over_each_other_and_the_background()
    {
        var vdp = Vdp(7, 0x00);
        SetColor(vdp, 0, 0x0000);
        SetColor(vdp, 1, 0x000E);  // red
        SetColor(vdp, 17, 0x00E0); // palette 1 colour 1: green
        SolidTile(vdp, 1, 1);
        WriteVram(vdp, 0xE000, 0x0001);          // plane B cell (0,0): tile 1, palette 0
        WriteVram(vdp, 0xC000 + 2, 0x2001);      // plane A cell (1,0): tile 1, palette 1
        WriteVram(vdp, 0xE000 + 4, 0x8001);      // plane B cell (2,0) high priority...
        WriteVram(vdp, 0xC000 + 4, 0x2001);      // ...over plane A low

        Render(vdp);

        vdp.FrameWidth.Should().Be(320);
        vdp.FrameHeight.Should().Be(224);
        Pixel(vdp, 0, 0).Should().Be(Normal(0x000E));
        Pixel(vdp, 8, 3).Should().Be(Normal(0x00E0));
        Pixel(vdp, 16, 7).Should().Be(Normal(0x000E), "B's priority beats A's");
        Pixel(vdp, 40, 0).Should().Be(Normal(0x0000), "empty cells show the background colour");
    }

    [Fact]
    public void Horizontal_scroll_per_line_and_vertical_scroll_move_the_plane()
    {
        var vdp = Vdp(11, 0x03); // per-line horizontal scroll
        SetColor(vdp, 1, 0x000E);
        SolidTile(vdp, 1, 1);
        WriteVram(vdp, 0xC000, 0x0001); // plane A cell (0,0)
        WriteVram(vdp, 0xFC00 + 4 * 2, 0x0005); // line 2: plane A scrolled right by 5

        Render(vdp);

        Pixel(vdp, 0, 0).Should().Be(Normal(0x000E));
        Pixel(vdp, 4, 2).Should().Be(Normal(0x0000), "line 2 moved right by 5");
        Pixel(vdp, 5, 2).Should().Be(Normal(0x000E));

        // Vertical scroll: VSRAM 0 = 4 moves plane A up by 4 lines.
        Command(vdp, 0x40000010);
        vdp.WriteData(4);
        Render(vdp);
        Pixel(vdp, 0, 3).Should().Be(Normal(0x000E));
        Pixel(vdp, 0, 4).Should().Be(Normal(0x0000));
    }

    [Fact]
    public void Sprites_draw_over_planes_and_follow_the_link_list()
    {
        var vdp = Vdp();
        SetColor(vdp, 1, 0x000E);
        SetColor(vdp, 2, 0x0E00); // blue
        SolidTile(vdp, 1, 1);
        SolidTile(vdp, 2, 2);
        SolidTile(vdp, 3, 2); // a two-cell sprite's second column uses the next tile
        WriteVram(vdp, 0xC000, 0x8001); // plane A high priority at (0,0)
        // Sprite 0: y=128+0, 1×1, link 1, tile 2, x=128+4 (low priority). Sprite 1: y=128+8, 2×1, link 0, tile 2 high, x=128+16.
        WriteVram(vdp, 0xF000, 128, 0x0001, 0x0002, 128 + 4, 128 + 8, 0x0400, 0x8002, 128 + 16);

        Render(vdp);

        Pixel(vdp, 4, 0).Should().Be(Normal(0x000E), "a high-priority plane covers a low-priority sprite");
        Pixel(vdp, 9, 0).Should().Be(Normal(0x0E00), "beyond the plane's tile the sprite shows");
        Pixel(vdp, 16, 8).Should().Be(Normal(0x0E00));
        Pixel(vdp, 31, 8).Should().Be(Normal(0x0E00), "the sprite is two cells wide");
        Pixel(vdp, 32, 8).Should().Be(Normal(0x0000));
    }

    [Fact]
    public void The_window_replaces_plane_a_and_ignores_scrolling()
    {
        var vdp = Vdp(3, 0x34, 17, 0x01); // window at $D000, left 16 pixels
        SetColor(vdp, 1, 0x000E);
        SolidTile(vdp, 1, 1);
        WriteVram(vdp, 0xD000, 0x0001);
        WriteVram(vdp, 0xFC00, 0x0003); // plane A scrolls (the window doesn't)

        Render(vdp);

        Pixel(vdp, 0, 0).Should().Be(Normal(0x000E));
        Pixel(vdp, 7, 0).Should().Be(Normal(0x000E));
        Pixel(vdp, 16, 0).Should().Be(Normal(0x0000), "past the window's 16 pixels plane A shows (empty)");
    }

    [Fact]
    public void Shadow_and_highlight_darken_low_priority_pixels_and_sprite_operators_change_them()
    {
        var vdp = Vdp(12, 0x89); // H40 with shadow/highlight
        SetColor(vdp, 1, 0x000E);
        SolidTile(vdp, 1, 1);
        SolidTile(vdp, 3, 14);
        WriteVram(vdp, 0xC000, 0x0001, 0x8001); // (0,0) low priority, (1,0) high
        // A sprite of palette 3 colour 14 (highlight) over cell (2,0) of a low-priority tile.
        WriteVram(vdp, 0xC000 + 4, 0x0001);
        WriteVram(vdp, 0xF000, 128, 0x0000, 0x6003, 128 + 16);

        Render(vdp);

        Pixel(vdp, 0, 0).Should().Be((ushort)(Normal(0x000E) - 512), "shadowed");
        Pixel(vdp, 8, 0).Should().Be(Normal(0x000E), "priority pixels are normal");
        Pixel(vdp, 16, 0).Should().Be(Normal(0x000E), "the highlight operator lifts a shadowed pixel to normal");
    }

    [Fact]
    public void Dma_copies_68000_memory_fills_and_copies_vram()
    {
        var vdp = Vdp(1, 0x54); // DMA enabled
        var memory = new ushort[0x100];
        memory[0] = 0x0EEE;
        memory[1] = 0x000E;
        vdp.DmaRead = address => memory[(address >> 1) & 0xFF];

        // 68000 → CRAM: 2 words from address 0.
        Register(vdp, 19, 2);
        Register(vdp, 20, 0);
        Register(vdp, 21, 0);
        Register(vdp, 22, 0);
        Register(vdp, 23, 0);
        Command(vdp, 0xC0000080);
        vdp.Cram[0].Should().Be(0x0EEE);
        vdp.Cram[1].Should().Be(0x000E);
        vdp.DmaStallCycles.Should().BeGreaterThan(0, "the 68000 waits for the transfer");

        // Fill: 4 bytes of $AB from $100.
        Register(vdp, 19, 4);
        Register(vdp, 20, 0);
        Register(vdp, 23, 0x80);
        Register(vdp, 15, 1);
        Command(vdp, VramWrite(0x100) | 0x80);
        vdp.WriteData(0xAB00);
        vdp.Vram[0x101].Should().Be(0xAB);
        vdp.Vram[0x103].Should().Be(0xAB);

        // Copy those bytes to $200.
        Register(vdp, 19, 4);
        Register(vdp, 21, 0x00);
        Register(vdp, 22, 0x01);
        Register(vdp, 23, 0xC0);
        Command(vdp, 0x000000C0 | (0x200u << 16));
        vdp.Vram.Slice(0x200, 4).ToArray().Should().Equal(vdp.Vram.Slice(0x100, 4).ToArray());
    }

    [Fact]
    public void Status_interrupts_and_the_hv_counter_follow_the_frame()
    {
        var vdp = Vdp(0, 0x14, 1, 0x64, 10, 3); // H-int every 4 lines, V-int on
        vdp.BeginLine(0);
        vdp.BeginHBlank();
        vdp.InterruptLevel.Should().Be(0);
        for (var line = 1; line < 4; line++)
        {
            vdp.BeginLine(line);
            vdp.BeginHBlank();
        }

        vdp.InterruptLevel.Should().Be(4, "the H-interrupt counter ran out");
        vdp.Acknowledge(4);
        vdp.InterruptLevel.Should().Be(0);

        for (var line = 4; line <= 224; line++)
        {
            vdp.BeginLine(line);
        }

        vdp.InterruptLevel.Should().Be(6, "vertical blank");
        (vdp.ReadStatus(0.1) & 0x88).Should().Be(0x88, "V-int pending and in the vertical blank");
        vdp.Z80Interrupt.Should().BeTrue();
        (vdp.HvCounter(0) >> 8).Should().Be(224);

        for (var line = 225; line < 240; line++)
        {
            vdp.BeginLine(line);
        }

        (vdp.HvCounter(0) >> 8).Should().Be(0xE5 + (239 - 0xEB), "the NTSC counter jumps from $EA to $E5");
    }

    // ---- YM2612 ----

    private static void Ym(Ym2612 ym, int part, int register, byte value)
    {
        ym.Write(part * 2, (byte)register);
        ym.Write(part * 2 + 1, value);
    }

    /// <summary>Channel 1 as a lone sine: algorithm 7, only operator 4 audible, instant attack.</summary>
    private static Ym2612 SineChannel(int fnumber, int block)
    {
        var ym = new Ym2612();
        Ym(ym, 0, 0xB0, 0x07);
        for (var op = 0; op < 4; op++)
        {
            var offset = op * 4;
            Ym(ym, 0, 0x30 + offset, 0x01);                 // MUL 1
            Ym(ym, 0, 0x40 + offset, (byte)(op == 3 ? 0 : 127)); // only operator 4 (offset 12) is loud
            Ym(ym, 0, 0x50 + offset, 0x1F);                 // fastest attack
            Ym(ym, 0, 0x80 + offset, 0x0F);
        }

        Ym(ym, 0, 0xA4, (byte)((block << 3) | (fnumber >> 8)));
        Ym(ym, 0, 0xA0, (byte)fnumber);
        Ym(ym, 0, 0x28, 0xF0); // key on, channel 1
        return ym;
    }

    [Fact]
    public void A_lone_operator_plays_the_frequency_its_f_number_and_block_give()
    {
        // F-number 1081 at block 4 is A4 (440 Hz) at the NTSC clock.
        var ym = SineChannel(1081, 4);
        var samples = new int[53267];
        for (var i = 0; i < samples.Length; i++)
        {
            ym.Clock(144);
            samples[i] = ym.Left;
        }

        var crossings = 0;
        for (var i = 1000; i < samples.Length; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
            {
                crossings++;
            }
        }

        // 52,267 samples ≈ 0.981 s: about 432 rising crossings at 440 Hz.
        crossings.Should().BeInRange(425, 440);
        samples.Max().Should().BeGreaterThan(6000, "a full-level operator is loud");
    }

    [Fact]
    public void Key_off_releases_and_timers_set_their_flags()
    {
        var ym = SineChannel(1081, 4);
        ym.Clock(144 * 2000);
        Ym(ym, 0, 0x28, 0x00); // key off
        Ym(ym, 0, 0x80 + 12, 0x0F); // release rate 15: fast
        ym.Clock(144 * 20000);
        var peak = 0;
        for (var i = 0; i < 1000; i++)
        {
            ym.Clock(144);
            peak = Math.Max(peak, Math.Abs(ym.Left));
        }

        peak.Should().Be(0, "the note has released to silence");

        // Timer A at 1023: it overflows every half sample, so after a sample its flag is up.
        Ym(ym, 0, 0x24, 0xFF);
        Ym(ym, 0, 0x25, 0x03);
        Ym(ym, 0, 0x27, 0x05);
        ym.Clock(144);
        (ym.ReadStatus() & 1).Should().Be(1);
        Ym(ym, 0, 0x27, 0x15); // reset the flag
        (ym.ReadStatus() & 1).Should().Be(0);
    }

    [Fact]
    public void The_dac_replaces_channel_6()
    {
        var ym = new Ym2612();
        Ym(ym, 0, 0x2B, 0x80);
        Ym(ym, 0, 0x2A, 0xFF);
        ym.Clock(144);

        ym.Left.Should().Be((0x7F << 6) & ~0x1F);
        ym.Right.Should().Be(ym.Left, "both speakers are on after reset");
    }

    // ---- The whole console ----

    private static GenesisConsole Console(byte[] rom, bool sixButton = true)
    {
        var console = new GenesisConsole(new GenesisCartridge(rom), sixButton: sixButton);
        console.Power();
        return console;
    }

    [Fact]
    public void Fm_panning_reaches_the_left_and_right_channels()
    {
        var console = Console(Rom([0x60FE]));
        var audio = console.EnableAudio();
        Ym(console.Fm, 0, 0x2B, 0x80); // the DAC replaces channel 6
        Ym(console.Fm, 0, 0x2A, 0xFF); // at full scale
        Ym(console.Fm, 1, 0xB6, 0x80); // channel 6 on the left speaker only
        console.RunFrame();
        console.RunFrame();

        var frames = new float[16384];
        var count = audio.ReadStereo(frames);
        count.Should().BeGreaterThan(1000);
        frames[2 * (count - 1)].Should().BeGreaterThan(0.1f, "the DAC plays on the left");
        frames[2 * (count - 1) + 1].Should().BeApproximately(0, 1e-3f, "and not on the right");
    }

    [Fact]
    public void A_68000_program_sets_up_the_vdp_and_the_frame_shows_its_colour()
    {
        var rom = Rom(Program(
            VdpControl(0x8104), VdpControl(0x8C81), VdpControl(0x8700),
            VdpCommand(0xC0000000), VdpData(0x000E),
            VdpControl(0x8144)));
        var console = Console(rom);

        console.RunFrame();
        console.RunFrame();

        console.ScreenWidth.Should().Be(320);
        console.ScreenHeight.Should().Be(224);
        console.PixelAspect.Should().BeApproximately(4.0 / 3 / (320.0 / 224), 1e-9);
        var pixel = console.FrameBuffer[100 * 320 + 100];
        console.Palette[pixel].Should().Be(0xFFFF0000u, "red, at full level");
    }

    [Fact]
    public void Three_and_six_button_pads_answer_the_th_protocol()
    {
        var console = Console(Rom([0x60FE]));
        console.SetControllerState(0, ControllerState.Up | ControllerState.C | ControllerState.B | ControllerState.Start | ControllerState.X);
        console.WriteByte(0xA10009, 0x40); // TH is an output

        console.WriteByte(0xA10003, 0x40);
        (console.ReadByte(0xA10003) & 0x3F).Should().Be(0b01_1110, "C pressed (bit 5 low), Up pressed (bit 0 low)");
        console.WriteByte(0xA10003, 0x00);
        (console.ReadByte(0xA10003) & 0x3F).Should().Be(0b00_0010, "Start and A pressed (the pad's A is B), Up pressed");

        // Six-button: TH high, low, high, low, high, low (0000 marks the pad), high (X Y Z Mode).
        console.WriteByte(0xA10003, 0x40);
        console.WriteByte(0xA10003, 0x00);
        console.WriteByte(0xA10003, 0x40);
        console.WriteByte(0xA10003, 0x00);
        (console.ReadByte(0xA10003) & 0x0F).Should().Be(0);
        console.WriteByte(0xA10003, 0x40);
        (console.ReadByte(0xA10003) & 0x0F).Should().Be(0b1011, "X pressed (bit 2 low); Y, Z and Mode up");

        var three = Console(Rom([0x60FE]), sixButton: false);
        three.SetControllerState(0, ControllerState.X);
        three.WriteByte(0xA10009, 0x40);
        for (var i = 0; i < 7; i++)
        {
            three.WriteByte(0xA10003, (byte)(i % 2 == 0 ? 0x40 : 0x00));
        }

        (three.ReadByte(0xA10003) & 0x0F).Should().Be(0x0F, "a 3-button pad has no X");
    }

    [Fact]
    public void The_68000_loads_a_z80_program_that_writes_the_ym2612_and_its_ram()
    {
        var console = Console(Rom([0x60FE]));
        console.WriteWord(0xA11100, 0x0100); // bus request
        console.WriteWord(0xA11200, 0x0100); // release reset
        console.ReadWord(0xA11100).Should().Be(0x0000, "the 68000 has the Z80's bus");

        // LD A,$2B; LD ($4000),A; LD A,$80; LD ($4001),A; LD A,$42; LD ($1000),A; HALT
        byte[] program = [0x3E, 0x2B, 0x32, 0x00, 0x40, 0x3E, 0x80, 0x32, 0x01, 0x40, 0x3E, 0x42, 0x32, 0x00, 0x10, 0x76];
        for (var i = 0; i < program.Length; i++)
        {
            console.WriteByte((uint)(0xA00000 + i), program[i]);
        }

        console.WriteWord(0xA11200, 0x0000); // reset the Z80 so it starts at $0000...
        console.WriteWord(0xA11200, 0x0100);
        console.WriteWord(0xA11100, 0x0000); // ...and let it run
        console.RunFrame();

        console.WriteWord(0xA11100, 0x0100);
        console.ReadByte(0xA01000).Should().Be(0x42);
    }

    [Fact]
    public void Save_states_replay_identically()
    {
        // A loop that counts in D0 and writes it to RAM, with the V-interrupt changing D1.
        var rom = Rom(Program(VdpControl(0x8164), [0x46FC, 0x2000, 0x5280, 0x33C0, 0x00FF, 0x0000, 0x60F6]), patch: r =>
        {
            r[0x78] = 0x00; r[0x79] = 0x00; r[0x7A] = 0x03; r[0x7B] = 0x00; // level 6 autovector → $300
            r[0x300] = 0x52; r[0x301] = 0x81; // ADDQ.L #1,D1
            r[0x302] = 0x4E; r[0x303] = 0x73; // RTE
        });
        var console = Console(rom);
        console.EnableAudio();
        for (var i = 0; i < 3; i++)
        {
            console.RunFrame();
        }

        var state = console.SaveSnapshot();
        console.RunFrame();
        console.RunFrame();
        var expected = (console.Cpu.GetD(0), console.Cpu.GetD(1), console.WorkRam.ToArray());

        var copy = Console(rom);
        copy.EnableAudio();
        copy.LoadSnapshot(state);
        copy.RunFrame();
        copy.RunFrame();

        copy.Cpu.GetD(1).Should().Be(expected.Item2);
        copy.Cpu.GetD(1).Should().BeGreaterThanOrEqualTo(4, "a V-interrupt each frame");
        copy.Cpu.GetD(0).Should().Be(expected.Item1);
        copy.WorkRam.ToArray().Should().Equal(expected.Item3);
    }
}
