using FluentAssertions;
using GameVault.Core.Input;
using GameVault.Core.Snes;

namespace GameVault.Core.Tests.Snes;

/// <summary>The Super Nintendo (SPEC §7.11, Phase 14), with programs, tiles and samples written here (no third-party ROMs).</summary>
public sealed class SnesTests
{
    /// <summary>A LoROM (or HiROM) image with a valid header and <paramref name="program"/> at the reset vector ($8000).</summary>
    internal static byte[] Rom(byte[] program, bool hiRom = false, int size = 0x10000, byte chip = 0x00, byte ramSize = 0, byte country = 0x01)
    {
        var rom = new byte[size];
        var header = hiRom ? 0xFFC0 : 0x7FC0;
        var codeAt = hiRom ? 0x8000 : 0x0000;
        program.CopyTo(rom, codeAt);
        "GAMEVAULT TEST       "u8.CopyTo(rom.AsSpan(header));
        rom[header + 0x15] = (byte)(hiRom ? 0x21 : 0x20);
        rom[header + 0x16] = chip;
        rom[header + 0x17] = 0x06;
        rom[header + 0x18] = ramSize;
        rom[header + 0x19] = country;
        rom[header + 0x3C] = 0x00; // reset vector $8000
        rom[header + 0x3D] = 0x80;
        rom[header + 0x1C] = 0xFF; // checksum complement $FFFF, checksum 0
        rom[header + 0x1D] = 0xFF;
        return rom;
    }

    /// <summary>CLC; XCE (native mode, 8-bit registers), the given setup, then BRA *.</summary>
    private static byte[] Program(params byte[][] parts) => [0x18, 0xFB, .. parts.SelectMany(p => p), 0x80, 0xFE];

    /// <summary>LDA #value; STA address.</summary>
    private static byte[] Store(int address, byte value) => [0xA9, value, 0x8D, (byte)address, (byte)(address >> 8)];

    private static SnesConsole Console(byte[] rom)
    {
        var console = new SnesConsole(new SnesCartridge(rom));
        console.Power();
        return console;
    }

    // ---- Cartridge ----

    [Fact]
    public void Headers_pick_lorom_or_hirom_and_size_the_sram()
    {
        var lo = new SnesCartridge(Rom([0xEA], ramSize: 3, chip: 0x02));
        lo.Map.Should().Be(SnesMap.LoRom);
        lo.Title.Should().Be("GAMEVAULT TEST");
        lo.HasBattery.Should().BeTrue();
        lo.BatteryRam.Length.Should().Be(8 * 1024);
        lo.Write(0x700005, 0x42);
        lo.TryRead(0x700005, out var saved).Should().BeTrue();
        saved.Should().Be(0x42);
        lo.TryRead(0x808000, out var code).Should().BeTrue();
        code.Should().Be(0xEA, "bank $80 mirrors bank $00's ROM");

        var hiRom = Rom([0xEA], hiRom: true, ramSize: 1, chip: 0x02, country: 0x02);
        var hi = new SnesCartridge(hiRom);
        hi.Map.Should().Be(SnesMap.HiRom);
        hi.Pal.Should().BeTrue();
        hi.TryRead(0xC08000, out var hiCode).Should().BeTrue();
        hiCode.Should().Be(0xEA);
        hi.Write(0x206001, 0x17);
        hi.BatteryRam[1].Should().Be(0x17);

        var withHeader = new byte[512].Concat(Rom([0xEA])).ToArray();
        new SnesCartridge(withHeader).Rom.Length.Should().Be(0x10000, "a copier header is skipped");
        SnesCartridge.LooksLikeSnes(withHeader).Should().BeTrue();

        var act = () => new SnesCartridge(Rom([0xEA], chip: 0x13));
        act.Should().Throw<UnsupportedChipException>().Which.Chip.Should().Be("Super FX");
    }

    // ---- A program on the whole console ----

    [Fact]
    public void A_boot_program_sets_the_backdrop_and_turns_the_screen_on()
    {
        var console = Console(Rom(Program(
            Store(0x2121, 0x00), Store(0x2122, 0x1F), Store(0x2122, 0x00), // colour 0: red
            Store(0x2100, 0x0F))));                                          // full brightness

        console.RunFrame();
        console.RunFrame();

        console.ScreenWidth.Should().Be(256);
        console.ScreenHeight.Should().Be(224);
        console.FrameBuffer[100 * 256 + 100].Should().Be(0x001F);
        console.Palette[0x001F].Should().Be(0xFFFF0000u);
        console.PixelAspect.Should().BeApproximately(4.0 / 3 / (256.0 / 224), 1e-9);
    }

    [Fact]
    public void Dma_copies_rom_to_vram_and_hdma_changes_brightness_per_line()
    {
        // DMA channel 0: mode 1 to $2118, 4 bytes from $00:9000. HDMA channel 1: $2100 from the table at $00:9100.
        var program = Program(
            Store(0x2115, 0x80), Store(0x2116, 0x00), Store(0x2117, 0x10),
            Store(0x4300, 0x01), Store(0x4301, 0x18), Store(0x4302, 0x00), Store(0x4303, 0x90), Store(0x4304, 0x00),
            Store(0x4305, 0x04), Store(0x4306, 0x00), Store(0x420B, 0x01),
            Store(0x2121, 0x00), Store(0x2122, 0xFF), Store(0x2122, 0x7F), // white backdrop
            Store(0x4310, 0x00), Store(0x4311, 0x00), Store(0x4312, 0x00), Store(0x4313, 0x91), Store(0x4314, 0x00),
            Store(0x420C, 0x02), Store(0x2100, 0x0F));
        var rom = Rom(program);
        byte[] data = [0x11, 0x22, 0x33, 0x44];
        data.CopyTo(rom, 0x1000);
        byte[] table = [10, 0x0F, 10, 0x07, 0x01, 0x0F, 0x00];
        table.CopyTo(rom, 0x1100);
        var console = Console(rom);

        console.RunFrame();
        console.RunFrame();

        console.Ppu.Vram[0x1000].Should().Be(0x2211);
        console.Ppu.Vram[0x1001].Should().Be(0x4433);
        console.FrameBuffer[5 * 256].Should().Be(0x7FFF, "lines under the first entry are at full brightness");
        console.FrameBuffer[15 * 256].Should().Be((ushort)(15 | 15 << 5 | 15 << 10), "brightness 7 halves each component");
        console.FrameBuffer[30 * 256].Should().Be(0x7FFF);
    }

    [Fact]
    public void The_multiplier_divider_and_joypad_auto_read_work()
    {
        var console = Console(Rom(Program(Store(0x4200, 0x01))));
        console.Write(0x4202, 12);
        console.Write(0x4203, 11);
        (console.Read(0x4216) | console.Read(0x4217) << 8).Should().Be(132);
        console.Write(0x4204, 0xE8);
        console.Write(0x4205, 0x03); // 1000
        console.Write(0x4206, 7);
        (console.Read(0x4214) | console.Read(0x4215) << 8).Should().Be(142);
        (console.Read(0x4216) | console.Read(0x4217) << 8).Should().Be(6);

        console.SetControllerState(0, ControllerState.B | ControllerState.A | ControllerState.R | ControllerState.Up);
        console.RunFrame();
        console.RunFrame();
        console.Read(0x4219).Should().Be(0x88, "B and Up");
        console.Read(0x4218).Should().Be(0x90, "A and R");

        // The serial port shifts the same bits out, B first.
        console.Write(0x4016, 1);
        console.Write(0x4016, 0);
        (console.Read(0x4016) & 1).Should().Be(1, "B");
        (console.Read(0x4016) & 1).Should().Be(0, "Y");
    }

    // ---- PPU ----

    private static SnesPpu Ppu()
    {
        var ppu = new SnesPpu(pal: false);
        ppu.Power();
        ppu.Write(0x00, 0x0F);
        return ppu;
    }

    private static void Vram(SnesPpu ppu, int wordAddress, params ushort[] words)
    {
        ppu.Write(0x15, 0x80);
        ppu.Write(0x16, (byte)wordAddress);
        ppu.Write(0x17, (byte)(wordAddress >> 8));
        foreach (var word in words)
        {
            ppu.Write(0x18, (byte)word);
            ppu.Write(0x19, (byte)(word >> 8));
        }
    }

    private static void Color(SnesPpu ppu, int index, ushort color)
    {
        ppu.Write(0x21, (byte)index);
        ppu.Write(0x22, (byte)color);
        ppu.Write(0x22, (byte)(color >> 8));
    }

    /// <summary>A solid 4bpp tile of colour <paramref name="pixel"/> at word address <paramref name="at"/>.</summary>
    private static void SolidTile4(SnesPpu ppu, int at, int pixel)
    {
        var words = new ushort[16];
        for (var row = 0; row < 8; row++)
        {
            words[row] = (ushort)(((pixel & 1) != 0 ? 0xFF : 0) | ((pixel & 2) != 0 ? 0xFF00 : 0));
            words[8 + row] = (ushort)(((pixel & 4) != 0 ? 0xFF : 0) | ((pixel & 8) != 0 ? 0xFF00 : 0));
        }

        Vram(ppu, at, words);
    }

    private static ushort[] Draw(SnesPpu ppu, int lines = 16)
    {
        ppu.BeginFrame();
        for (var y = 0; y < lines; y++)
        {
            ppu.RenderLine(y);
        }

        ppu.FinishFrame();
        return ppu.Frame.ToArray();
    }

    [Fact]
    public void Mode_1_draws_a_background_and_sprites_in_front_by_priority()
    {
        var ppu = Ppu();
        ppu.Write(0x05, 0x01);  // mode 1
        ppu.Write(0x07, 0x10);  // BG1 map at word $1000
        ppu.Write(0x0B, 0x02);  // BG1 tiles at word $2000
        ppu.Write(0x0E, 0xFF);  // vertical scroll -1 (so line 0 shows tile row 0)
        ppu.Write(0x0E, 0x03);
        ppu.Write(0x01, 0x03);  // sprites: 8×8/16×16, tiles at word $6000
        ppu.Write(0x2C, 0x11);  // main screen: BG1 and sprites
        Color(ppu, 1, 0x001F);       // BG palette 0 colour 1: red
        Color(ppu, 128 + 2, 0x7C00); // sprite palette 0 colour 2: blue
        SolidTile4(ppu, 0x2000 + 16, 1);
        SolidTile4(ppu, 0x6000 + 16, 2);
        Vram(ppu, 0x1000, 0x0001);  // BG1 cell (0,0): tile 1
        ppu.Write(0x02, 0x00);
        ppu.Write(0x03, 0x00);
        // Sprite 0 at (4, 0), tile 1, priority 3; the rest off-screen at y=240.
        byte[] oam = [4, 0, 1, 0x30];
        foreach (var b in oam)
        {
            ppu.Write(0x04, b);
        }

        for (var i = 1; i < 128; i++)
        {
            foreach (var b in new byte[] { 0, 240, 0, 0 })
            {
                ppu.Write(0x04, b);
            }
        }

        var frame = Draw(ppu);

        frame[0].Should().Be(0x001F);
        frame[4].Should().Be(0x7C00, "the sprite is in front");
        frame[12].Should().Be(0x0000, "past both, the backdrop");
    }

    [Fact]
    public void Color_math_adds_the_fixed_color_and_windows_hide_layers()
    {
        var ppu = Ppu();
        Color(ppu, 0, 0x0010);  // backdrop: dim red
        ppu.Write(0x32, 0x2F);  // fixed colour: red 15
        ppu.Write(0x31, 0x20);  // math on the backdrop: add
        var frame = Draw(ppu, 1);
        frame[0].Should().Be(0x001F, "16 + 15 = 31");

        // Window 1 from x=10 to 20 clips the main screen to black there (CGWSEL bits 6–7 = inside).
        ppu.Write(0x26, 10);
        ppu.Write(0x27, 20);
        ppu.Write(0x25, 0x20);  // colour window: window 1 enabled
        ppu.Write(0x30, 0x80);
        ppu.Write(0x31, 0x00);
        frame = Draw(ppu, 1);
        frame[5].Should().Be(0x0010);
        frame[15].Should().Be(0x0000);
    }

    [Fact]
    public void Mode_7_with_the_identity_matrix_shows_its_tilemap()
    {
        var ppu = Ppu();
        ppu.Write(0x05, 0x07);
        ppu.Write(0x2C, 0x01);
        ppu.Write(0x1B, 0x00); ppu.Write(0x1B, 0x01); // A = 1.0
        ppu.Write(0x1C, 0x00); ppu.Write(0x1C, 0x00); // B = 0
        ppu.Write(0x1D, 0x00); ppu.Write(0x1D, 0x00); // C = 0
        ppu.Write(0x1E, 0x00); ppu.Write(0x1E, 0x01); // D = 1.0
        Color(ppu, 5, 0x03E0);
        // Tilemap entry (0,0) = tile 1 (low bytes); tile 1's pixels = colour 5 (high bytes at word 64–127).
        ppu.Write(0x15, 0x00); // increment after the low byte
        ppu.Write(0x16, 0x00);
        ppu.Write(0x17, 0x00);
        ppu.Write(0x18, 0x01);
        ppu.Write(0x15, 0x80);
        ppu.Write(0x16, 64);
        ppu.Write(0x17, 0);
        for (var i = 0; i < 64; i++)
        {
            ppu.Write(0x19, 5);
        }

        var frame = Draw(ppu, 4);

        frame[0].Should().Be(0x03E0);
        frame[8].Should().Be(0x0000, "tile 0 is empty (transparent shows the backdrop)");
    }

    // ---- Sound ----

    [Fact]
    public void The_boot_program_takes_an_upload_by_the_documented_protocol_and_runs_it()
    {
        SnesApu.BootProgram.Length.Should().Be(64);
        var apu = new SnesApu();
        apu.Power();
        apu.Run(10000); // clearing $01–$EF takes about 2,400 cycles
        apu.CpuRead(0).Should().Be(0xAA);
        apu.CpuRead(1).Should().Be(0xBB);

        void Kick(byte value)
        {
            apu.CpuWrite(0, value);
            for (var i = 0; i < 200 && apu.CpuRead(0) != value; i++)
            {
                apu.Run(32);
            }

            apu.CpuRead(0).Should().Be(value, "the boot program echoes the kick");
        }

        // The program at $0200: MOV A,#$5A; MOV $F7,A (port 3); BRA * — then run it.
        byte[] program = [0xE8, 0x5A, 0xC4, 0xF7, 0x2F, 0xFE];
        apu.CpuWrite(2, 0x00);
        apu.CpuWrite(3, 0x02);
        apu.CpuWrite(1, 0x01);
        Kick(0xCC);
        for (var i = 0; i < program.Length; i++)
        {
            apu.CpuWrite(1, program[i]);
            Kick((byte)i);
        }

        apu.CpuWrite(2, 0x00);
        apu.CpuWrite(3, 0x02);
        apu.CpuWrite(1, 0x00); // run
        Kick((byte)(program.Length + 1));
        apu.Run(200);

        apu.Ram[0x0200].Should().Be(0xE8);
        apu.CpuRead(3).Should().Be(0x5A, "the uploaded program ran");
    }

    [Fact]
    public void A_dsp_voice_plays_a_brr_sample_at_the_pitch_its_register_gives()
    {
        var apu = new SnesApu();
        apu.Power();
        var dsp = apu.Dsp;

        // A looping BRR block: a square wave of 8 high and 8 low samples (16 per block), shift 12.
        void Poke(int address, byte value) => apu.Write((ushort)address, value);
        Poke(0x1000, 0xC3); // shift 12, filter 0, loop + end
        for (var i = 0; i < 4; i++)
        {
            Poke(0x1001 + i, 0x77);
            Poke(0x1005 + i, 0x99);
        }

        Poke(0x0200, 0x00); Poke(0x0201, 0x10); // directory entry 0: start $1000
        Poke(0x0202, 0x00); Poke(0x0203, 0x10); // loop $1000
        void Dsp(int register, byte value)
        {
            apu.Write(0xF2, (byte)register);
            apu.Write(0xF3, value);
        }

        Dsp(0x6C, 0x20); // un-mute, no reset, echo writes off
        Dsp(0x5D, 0x02); // directory at $0200
        Dsp(0x0C, 0x7F); Dsp(0x1C, 0x7F); // master volume
        Dsp(0x00, 0x7F); Dsp(0x01, 0x7F); // voice 0 volume
        Dsp(0x02, 0x00); Dsp(0x03, 0x10); // pitch $1000: one sample per output sample
        Dsp(0x04, 0x00);
        Dsp(0x05, 0x00); Dsp(0x07, 0x7F); // GAIN: direct, full
        Dsp(0x4C, 0x01); // key on

        var crossings = 0;
        var last = 0;
        var samples = 0;
        dsp.SampleReady = (left, _) =>
        {
            if (samples++ > 100 && last < 0 && left >= 0)
            {
                crossings++;
            }

            last = left;
        };
        apu.Run(32 * 32040); // one second of DSP samples (with the SPC700 halted in its boot loop)

        // A 16-sample period at 32,040 Hz is about 2,000 Hz.
        crossings.Should().BeInRange(1900, 2100);
    }

    [Fact]
    public void Save_states_replay_identically()
    {
        // A counting loop in WRAM with NMI incrementing $10.
        var program = Program(
            Store(0x4200, 0x80),
            [0xEE, 0x00, 0x01, 0x80, 0xFB]); // INC $0100; BRA -5
        var rom = Rom(program);
        rom[0x7FEA] = 0x00; rom[0x7FEB] = 0x90; // native NMI vector $9000
        byte[] nmi = [0xE6, 0x10, 0x40]; // INC $10; RTI
        nmi.CopyTo(rom, 0x1000);
        var console = Console(rom);
        console.EnableAudio();
        for (var i = 0; i < 3; i++)
        {
            console.RunFrame();
        }

        var state = console.SaveSnapshot();
        console.RunFrame();
        console.RunFrame();
        var expected = console.Wram.ToArray();

        var copy = Console(rom);
        copy.EnableAudio();
        copy.LoadSnapshot(state);
        copy.RunFrame();
        copy.RunFrame();

        copy.Wram[0x10].Should().BeGreaterThanOrEqualTo(4, "an NMI every frame");
        copy.Wram.ToArray().Should().Equal(expected);
    }
}
