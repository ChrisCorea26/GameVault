using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.Cartridge;
using GameVault.Core.Genesis;
using GameVault.Core.Sega32X;
using GameVault.Core.Tests.Genesis;

namespace GameVault.Core.Tests.Sega32X;

/// <summary>The 32X (sega-cd-32x.md, milestone 2), with programs written here and stand-in BIOS files (no Sega code).</summary>
public sealed class Sega32XTests
{
    private static ushort[] MoveW(ushort value, uint address) => [0x33FC, value, (ushort)(address >> 16), (ushort)address];

    private static readonly ushort[] Loop = [0x60FE];

    private static ushort[] Words(params ushort[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>
    /// An SH-2 boot ROM: PC $300 and SP $0603FF00 in its vectors, <paramref name="program"/> at $300 (past the vector
    /// table), and the CMD interrupt's vector (68) pointing at <paramref name="command"/> at $380.
    /// </summary>
    private static byte[] Boot(int size, ushort[] program, ushort[]? command = null)
    {
        var rom = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(rom, 0x300);
        BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(4), 0x0603FF00);
        BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(68 * 4), 0x380);
        for (var i = 0; i < program.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(rom.AsSpan(0x300 + i * 2), program[i]);
        }

        for (var i = 0; i < (command?.Length ?? 0); i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(rom.AsSpan(0x380 + i * 2), command![i]);
        }

        return rom;
    }

    /// <summary>SH-2: R1 = $20004020 + <paramref name="port"/> × 2; R0 = <paramref name="value"/>; MOV.W R0,@R1; loop.</summary>
    private static ushort[] WriteComm(int port, ushort value) =>
    [
        0xD102, // MOV.L @(2,PC),R1 → $30C
        0x9002, // MOV.W @(2,PC),R0 → $30A
        0x2101, // MOV.W R0,@R1
        0xAFFE, // BRA *
        0x0009,
        value,
        (ushort)((0x20004020 + port * 2) >> 16),
        (ushort)(0x20004020 + port * 2),
    ];

    private static Sega32XBios Bios(ushort[]? master = null, ushort[]? slave = null, ushort[]? command = null)
    {
        var genesis = new byte[256];
        genesis[0] = 0x55; // a marker the tests read back
        return new Sega32XBios(genesis, Boot(2048, master ?? [0xAFFE, 0x0009], command), Boot(1024, slave ?? [0xAFFE, 0x0009]));
    }

    private static GenesisConsole Console(ushort[] main, Sega32XBios? bios = null)
    {
        var rom = GenesisTests.Rom(main, size: 0x80000);
        rom[0x10000] = 0xAB;
        var unit = new Sega32XUnit(rom, bios ?? Bios(), pal: false, GenesisConsole.NtscMasterClock);
        var console = new GenesisConsole(new GenesisCartridge(rom), GenesisRegion.Americas, sega32X: unit);
        console.Power();
        return console;
    }

    [Fact]
    public void Bios_files_of_the_wrong_size_are_refused()
    {
        var act = () => new Sega32XUnit(new byte[0x200], new Sega32XBios(new byte[256], new byte[1024], new byte[1024]), false, GenesisConsole.NtscMasterClock);

        act.Should().Throw<RomFormatException>().WithMessage("*2 KB*");
    }

    [Fact]
    public void The_adapter_maps_its_bios_over_the_vectors_and_moves_the_cartridge()
    {
        var console = Console(Words(MoveW(0x0001, 0xA15100), Loop)); // ADEN

        console.ReadByte(0x000000).Should().Be(0xFF, "the cartridge's own stack pointer before the adapter is on");
        console.RunFrame();

        console.System.Should().Be(GameVault.Core.Systems.GameSystem.Sega32X);
        console.Sega32X!.AdapterEnabled.Should().BeTrue();
        console.ReadByte(0x000000).Should().Be(0x55, "the 32X's vector BIOS");
        console.ReadByte(0x890000).Should().Be(0xAB, "the cartridge at $880000");
        console.ReadWord(0xA130EC).Should().Be(0x4D41, "\"MA\" of MARS");
        console.ReadWord(0xA130EE).Should().Be(0x5253);
    }

    [Fact]
    public void Releasing_reset_starts_both_sh2s_from_their_boot_roms()
    {
        var bios = Bios(master: WriteComm(0, 0x4D21), slave: WriteComm(1, 0x5321));
        var console = Console(Words(MoveW(0x0003, 0xA15100), Loop), bios); // ADEN, SH-2s out of reset

        console.RunFrame();

        console.ReadWord(0xA15120).Should().Be(0x4D21, "the master SH-2 wrote communication port 0");
        console.ReadWord(0xA15122).Should().Be(0x5321, "the slave SH-2 wrote port 1");
    }

    [Fact]
    public void The_68000_interrupts_the_master_sh2()
    {
        ushort[] master =
        [
            0xE102, // MOV #2,R1: the CMD interrupt's mask bit
            0xD003, // MOV.L @(3,PC),R0 → $310: $20004000
            0x2011, // MOV.W R1,@R0
            0xE100, // MOV #0,R1
            0x410E, // LDC R1,SR: mask 0
            0xAFFE, // BRA *
            0x0009,
            0x0009,
            0x2000, 0x4000,
        ];
        ushort[] command =
        [
            0xD001, // $380 MOV.L @(1,PC),R0 → $388: $2000401A (clear CMD)
            0x2001, // MOV.W R0,@R0 (any value clears it)
            0xAFFE, // BRA * (stay in the handler: the test only needs to see it ran)
            0x0009,
            0x2000, 0x401A,
        ];
        var bios = Bios(master: master, command: command);
        var console = Console(Words(MoveW(0x0003, 0xA15100), Loop), bios);
        var unit = console.Sega32X!;
        console.RunFrame();
        unit.Master.Pc.Should().BeLessThan(0x380);

        console.WriteWord(0xA15102, 0x0001); // INTM
        console.RunFrame();

        unit.Master.Pc.Should().BeInRange(0x380, 0x38A, "the master SH-2 took the CMD interrupt");
        (console.ReadWord(0xA15102) & 1).Should().Be(0, "the handler acknowledged it");
    }

    [Fact]
    public void A_packed_pixel_frame_buffer_shows_over_the_genesis_picture()
    {
        var console = Console(Words(MoveW(0x0001, 0xA15100), Loop));
        console.RunFrame();

        // The frame buffer being drawn (FS clear: buffer 1): line table, then one line of colour 1 pixels.
        for (var y = 0; y < 256; y++)
        {
            console.WriteWord(0x840000 + (uint)y * 2, 0x0100);
        }

        for (var x = 0; x < 160; x++)
        {
            console.WriteWord(0x840200 + (uint)x * 2, 0x0101);
        }

        console.WriteWord(0xA15202, 0x001F); // palette 1: red
        console.WriteWord(0xA15180, 0x0081); // packed pixels, 32X in front
        console.WriteWord(0xA1518A, 0x0001); // show that buffer from the next vertical blank

        console.RunFrame();
        console.RunFrame();

        console.FrameBuffer[10 * console.ScreenWidth + 5].Should().Be((ushort)(Sega32XPalette.First + 0x001F));
        console.Palette[Sega32XPalette.First + 0x001F].Should().Be(0xFFFF0000);
    }

    [Fact]
    public void A_palette_change_on_a_genesis_h_interrupt_shows_from_that_line_down()
    {
        // The Genesis' H interrupt every 100 lines turns 32X colour 1 blue (its vector is in the 32X's vector BIOS).
        ushort[] main =
        [
            .. MoveW(0x0001, 0xA15100), // ADEN
            .. MoveW(0x8014, 0xC00004), // VDP register 0: H interrupts on
            .. MoveW(0x8A63, 0xC00004), // register 10: every 100 lines
            0x46FC, 0x2000,             // interrupts on
            .. Loop,
        ];
        var rom = GenesisTests.Rom(main, size: 0x80000, patch: r =>
        {
            ushort[] handler = [.. MoveW(0x7C00, 0xA15202), 0x4E73]; // palette 1 = blue; RTE
            for (var i = 0; i < handler.Length; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(0x300 + i * 2), handler[i]);
            }
        });
        var genesisBios = new byte[256];
        BinaryPrimitives.WriteUInt32BigEndian(genesisBios.AsSpan(0x70), 0x300); // level 4
        var bios = new Sega32XBios(genesisBios, Boot(2048, [0xAFFE, 0x0009]), Boot(1024, [0xAFFE, 0x0009]));
        var unit = new Sega32XUnit(rom, bios, pal: false, GenesisConsole.NtscMasterClock);
        var console = new GenesisConsole(new GenesisCartridge(rom), GenesisRegion.Americas, sega32X: unit);
        console.Power();
        console.RunFrame();

        for (var y = 0; y < 256; y++)
        {
            console.WriteWord(0x840000 + (uint)y * 2, 0x0100);
        }

        for (var x = 0; x < 160; x++)
        {
            console.WriteWord(0x840200 + (uint)x * 2, 0x0101); // every pixel colour 1
        }

        console.WriteWord(0xA15180, 0x0081); // packed pixels, 32X in front
        console.WriteWord(0xA1518A, 0x0001);
        console.RunFrame(); // the buffers swap

        console.WriteWord(0xA15202, 0x001F); // colour 1 red again before the frame starts
        console.RunFrame();

        ushort Pixel(int y) => console.FrameBuffer[y * console.ScreenWidth + 40];
        Pixel(10).Should().Be((ushort)(Sega32XPalette.First + 0x001F), "red above the change");
        Pixel(90).Should().Be((ushort)(Sega32XPalette.First + 0x001F));
        Pixel(110).Should().Be((ushort)(Sega32XPalette.First + 0x7C00), "blue from the line after the interrupt");
        Pixel(200).Should().Be((ushort)(Sega32XPalette.First + 0x7C00));
    }

    [Fact]
    public void Turning_the_32x_picture_off_mid_frame_leaves_the_genesis_picture_below()
    {
        var console = Console(Words(MoveW(0x0001, 0xA15100), Loop));
        console.RunFrame();
        for (var y = 0; y < 256; y++)
        {
            console.WriteWord(0x840000 + (uint)y * 2, 0x0100);
        }

        for (var x = 0; x < 160; x++)
        {
            console.WriteWord(0x840200 + (uint)x * 2, 0x0101);
        }

        console.WriteWord(0xA15202, 0x001F);
        console.WriteWord(0xA15180, 0x0081);
        console.WriteWord(0xA1518A, 0x0001);
        console.RunFrame();

        // Run the next frame line by line from the outside: the mode goes to 0 (blank) halfway down.
        var unit = console.Sega32X!;
        var frame = new ushort[320 * 224];
        var backdrop = new bool[320 * 224];
        for (var line = 0; line < 224; line++)
        {
            if (line == 112)
            {
                console.WriteWord(0xA15180, 0x0000);
            }

            unit.ComposeLine(frame, backdrop, 320, line, line);
        }

        frame[50 * 320].Should().Be((ushort)(Sega32XPalette.First + 0x001F));
        frame[150 * 320].Should().Be(0, "nothing was laid over the lower half");
    }

    [Fact]
    public void Direct_colour_and_run_length_lines_and_the_overwrite_image()
    {
        var vdp = new Mars32XVdp();
        vdp.Reset();
        var draw = vdp.DrawBuffer;
        draw[0] = 0x0100;
        draw[1] = 0x0200;
        draw[0x100] = 0x7C00;
        draw[0x101] = 0x8001;
        draw[0x200] = 0x0204; // colour 2, five pixels
        draw[0x201] = 0x03FF; // colour 3, the rest
        vdp.Palette[2] = 0x1234;
        vdp.Palette[3] = 0x0042;
        vdp.WriteRegister(0x0A, 1);
        vdp.SetVBlank(true);
        var line = new ushort[Mars32XVdp.Width];

        vdp.WriteRegister(0x00, 2);
        vdp.RenderLine(0, line).Should().BeTrue();
        line[0].Should().Be(0x7C00);
        line[1].Should().Be(0x8001);

        vdp.WriteRegister(0x00, 3);
        vdp.RenderLine(1, line);
        line[4].Should().Be(0x1234);
        line[5].Should().Be(0x0042);

        vdp.WriteRegister(0x0A, 0); // the other buffer to draw into
        vdp.WriteFrameWord(0, 0x1122);
        vdp.OverwriteWord(0, 0x0033);
        vdp.ReadFrame(0).Should().Be(0x1133, "the overwrite image leaves zero bytes alone");
    }

    [Fact]
    public void Auto_fill_writes_a_run_within_its_block()
    {
        var vdp = new Mars32XVdp();
        vdp.Reset();

        vdp.WriteRegister(0x04, 3); // four words
        vdp.WriteRegister(0x06, 0x01FE);
        vdp.WriteRegister(0x08, 0xABCD);

        vdp.DrawBuffer[0x1FE].Should().Be(0xABCD);
        vdp.DrawBuffer[0x1FF].Should().Be(0xABCD);
        vdp.DrawBuffer[0x100].Should().Be(0xABCD, "the address wraps inside its 256 words");
        vdp.DrawBuffer[0x101].Should().Be(0xABCD);
        vdp.DrawBuffer[0x200].Should().Be(0);
    }

    [Fact]
    public void The_fifo_feeds_the_master_sh2s_dma()
    {
        var console = Console(Words(MoveW(0x0003, 0xA15100), Loop));
        var unit = console.Sega32X!;
        console.RunFrame();
        var master = unit.Master;
        master.Write32(0xFFFFFF80, 0x20004012); // SAR0: the FIFO
        master.Write32(0xFFFFFF84, 0x26000000); // DAR0: SDRAM
        master.Write32(0xFFFFFF88, 3);
        master.Write32(0xFFFFFFB0, 1);
        master.Write32(0xFFFFFF8C, 0x4401); // dest +, source fixed, word, DREQ, DE

        console.WriteWord(0xA15106, 0x0004); // 68S: the FIFO is on
        console.WriteWord(0xA15112, 0x1111);
        console.WriteWord(0xA15112, 0x2222);
        console.WriteWord(0xA15112, 0x3333);

        unit.Sdram[..6].ToArray().Should().Equal(0x11, 0x11, 0x22, 0x22, 0x33, 0x33);
    }

    [Fact]
    public void A_save_state_round_trips_both_sh2s()
    {
        ushort[] counter =
        [
            0xD103, // MOV.L @(3,PC),R1 → $310: $20004024
            0xE000, // MOV #0,R0
            0x7001, // loop: ADD #1,R0
            0x2101, // MOV.W R0,@R1
            0xAFFC, // BRA loop
            0x0009,
            0x0009,
            0x0009,
            0x2000, 0x4024,
        ];
        var console = Console(Words(MoveW(0x0003, 0xA15100), Loop), Bios(master: counter));
        console.RunFrame();

        var state = console.SaveSnapshot();
        console.RunFrame();
        var value = console.ReadWord(0xA15124);

        console.LoadSnapshot(state);
        console.RunFrame();

        console.ReadWord(0xA15124).Should().Be(value);
        value.Should().NotBe(0);
    }
}
