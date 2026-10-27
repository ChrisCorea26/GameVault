using FluentAssertions;
using GameVault.Core.Atari;
using GameVault.Core.Audio;
using GameVault.Core.Cartridge;
using GameVault.Core.Input;

namespace GameVault.Core.Tests.Atari;

/// <summary>6502 test programs for a 32K 5200 cartridge at $4000, with data placed anywhere in the cartridge.</summary>
internal sealed class Asm52
{
    public const ushort Dmactl = 0xD400, Chactl = 0xD401, Dlistl = 0xD402, Dlisth = 0xD403, Hscrol = 0xD404;
    public const ushort Chbase = 0xD409, Wsync = 0xD40A, Vcount = 0xD40B, Nmien = 0xD40E, Nmist = 0xD40F;
    public const ushort Hposp0 = 0xC000, Hposp1 = 0xC001, Grafp0 = 0xC00D, Grafp1 = 0xC00E, Colpm0 = 0xC012, Colpm1 = 0xC013;
    public const ushort Colpf0 = 0xC016, Colpf1 = 0xC017, Colpf2 = 0xC018, Colpf3 = 0xC019, Colbk = 0xC01A, Prior = 0xC01B;
    public const ushort Audf1 = 0xE800, Audc1 = 0xE801, Audctl = 0xE808, Stimer = 0xE809, Irqen = 0xE80E, Skctl = 0xE80F;

    private readonly byte[] _rom = new byte[0x8000];
    private readonly Dictionary<string, int> _labels = [];
    private readonly List<(int At, string Label)> _branches = [];
    private readonly List<(int At, string Label)> _absolutes = [];
    private int _pc = 0x4000;

    public int Here => _pc;

    public Asm52 Label(string name)
    {
        _labels[name] = _pc;
        return this;
    }

    public Asm52 Op(params byte[] bytes)
    {
        foreach (var b in bytes)
        {
            _rom[_pc++ - 0x4000] = b;
        }

        return this;
    }

    /// <summary>Bytes at an address of the cartridge (display lists, screens, character sets).</summary>
    public Asm52 Data(int address, params byte[] bytes)
    {
        bytes.CopyTo(_rom, address - 0x4000);
        return this;
    }

    public Asm52 Lda(byte value) => Op(0xA9, value);

    public Asm52 Ldx(byte value) => Op(0xA2, value);

    public Asm52 Sta(ushort address) => Op(0x8D, (byte)address, (byte)(address >> 8));

    public Asm52 Set(ushort address, byte value) => Lda(value).Sta(address);

    public Asm52 LdaAbs(ushort address) => Op(0xAD, (byte)address, (byte)(address >> 8));

    public Asm52 Inc(ushort address) => Op(0xEE, (byte)address, (byte)(address >> 8));

    public Asm52 Bit(ushort address) => Op(0x2C, (byte)address, (byte)(address >> 8));

    public Asm52 Dex() => Op(0xCA);

    public Asm52 Rti() => Op(0x40);

    public Asm52 Cli() => Op(0x58);

    public Asm52 Bpl(string label) => Branch(0x10, label);

    public Asm52 Bne(string label) => Branch(0xD0, label);

    public Asm52 Jmp(string label)
    {
        _absolutes.Add((_pc + 1, label));
        return Op(0x4C, 0, 0);
    }

    /// <summary>Points the test BIOS's NMI ($0200) or IRQ ($0202) vector at a label.</summary>
    public Asm52 Vector(ushort ram, string label)
    {
        _absolutes.Add((_pc + 1, label));
        Op(0xA9, 0).Sta(ram);
        _absolutes.Add((-(_pc + 1), label)); // the high byte
        return Op(0xA9, 0).Sta((ushort)(ram + 1));
    }

    private Asm52 Branch(byte opcode, string label)
    {
        _branches.Add((_pc + 1, label));
        return Op(opcode, 0);
    }

    public byte[] Build()
    {
        foreach (var (at, label) in _branches)
        {
            _rom[at - 0x4000] = (byte)(sbyte)(_labels[label] - (at + 1));
        }

        foreach (var (at, label) in _absolutes)
        {
            var target = _labels[label];
            if (at < 0)
            {
                _rom[-at - 0x4000] = (byte)(target >> 8);
            }
            else if (_rom[at - 0x4000 - 1] == 0x4C)
            {
                _rom[at - 0x4000] = (byte)target;
                _rom[at - 0x4000 + 1] = (byte)(target >> 8);
            }
            else
            {
                _rom[at - 0x4000] = (byte)target;
            }
        }

        // The cartridge header: no title screen ($BFFD = $FF) and the start address.
        _rom[0x7FFD] = 0xFF;
        _rom[0x7FFE] = 0x00;
        _rom[0x7FFF] = 0x40;
        return _rom;
    }

    /// <summary>
    /// A stand-in BIOS (the real one is the player's own): NMI and IRQ jump through RAM vectors at $0200 and $0202, and
    /// reset jumps through the cartridge's start address at $BFFE.
    /// </summary>
    public static byte[] TestBios()
    {
        var bios = new byte[Atari5200Console.BiosSize];
        bios.AsSpan().Fill(0xEA);
        byte[] stubs = [0x6C, 0x00, 0x02, 0x6C, 0x02, 0x02, 0x6C, 0xFE, 0xBF];
        stubs.CopyTo(bios, 0);
        bios[0x7FA] = 0x00; // NMI → $F800
        bios[0x7FB] = 0xF8;
        bios[0x7FC] = 0x06; // reset → $F806
        bios[0x7FD] = 0xF8;
        bios[0x7FE] = 0x03; // IRQ → $F803
        bios[0x7FF] = 0xF8;
        return bios;
    }

    public static Atari5200Console Console(Asm52 asm)
    {
        var console = new Atari5200Console(new Atari5200Cartridge(asm.Build()), TestBios());
        console.Power();
        return console;
    }
}

public sealed class Atari5200Tests
{
    // Frame row and pixel of a colour clock on a scan line.
    private static int Row(int line) => line - Antic.FirstDisplayLine;

    private static int Pixel(int clock) => (clock - Gtia.FirstClock) * 2;

    private static int At(Atari5200Console console, int line, int pixel) => console.FrameBuffer[Row(line) * Gtia.Width + pixel];

    /// <summary>A display list at $5000: 24 blank lines, then <paramref name="modeLine"/> (with LMS to $6000), then JVB.</summary>
    private static Asm52 Display(byte modeLine, byte dmactl = 0x22)
    {
        var asm = new Asm52();
        asm.Data(0x5000, 0x70, 0x70, 0x70, (byte)(modeLine | 0x40), 0x00, 0x60, 0x41, 0x00, 0x50);
        return asm.Set(Asm52.Dlistl, 0x00).Set(Asm52.Dlisth, 0x50).Set(Asm52.Chbase, 0x70).Set(Asm52.Dmactl, dmactl);
    }

    [Fact]
    public void A_bios_that_isnt_2_kb_is_refused()
    {
        var act = () => new Atari5200Console(new Atari5200Cartridge(new byte[8192]), new byte[4096]);

        act.Should().Throw<RomFormatException>().WithMessage("*2 KB*");
    }

    [Fact]
    public void Cartridges_repeat_across_the_window_and_two_chip_boards_split_it()
    {
        var rom = new byte[16384];
        rom[0] = 1;
        rom[0x2000] = 2;
        var standard = new Atari5200Cartridge(rom);
        var twoChip = new Atari5200Cartridge(rom, "two-chip");

        standard.Read(0x4000).Should().Be(1);
        standard.Read(0x8000).Should().Be(1);
        standard.Read(0x6000).Should().Be(2);
        twoChip.Board.Should().Be(Atari5200Board.TwoChip16K);
        twoChip.Read(0x4000).Should().Be(1);
        twoChip.Read(0x6000).Should().Be(1);
        twoChip.Read(0x8000).Should().Be(2);
        twoChip.Read(0xA000).Should().Be(2);
        var act = () => new Atari5200Cartridge(new byte[40960]);
        act.Should().Throw<RomFormatException>().WithMessage("*40 KB*");
    }

    [Fact]
    public void Mode_2_text_shows_pf1s_luminance_on_pf2_inside_the_window()
    {
        var asm = Display(0x02)
            .Set(Asm52.Colbk, 0x02).Set(Asm52.Colpf2, 0x94).Set(Asm52.Colpf1, 0x0A)
            .Label("loop").Jmp("loop");
        asm.Data(0x6000, 1);
        asm.Data(0x7008, 0xF0, 0xF0, 0xF0, 0xF0, 0xF0, 0xF0, 0xF0, 0xF0);
        var console = Asm52.Console(asm);

        console.RunFrame();
        console.RunFrame();

        // Lines 32–39: the mode line. The window starts at colour clock 48; character 1 lights its left half.
        At(console, 33, Pixel(48)).Should().Be(0x9A);
        At(console, 33, Pixel(49) + 1).Should().Be(0x9A);
        At(console, 33, Pixel(50)).Should().Be(0x94);
        At(console, 33, Pixel(46)).Should().Be(0x02);
        At(console, 20, Pixel(48)).Should().Be(0x02); // a blank line
    }

    [Fact]
    public void Horizontal_scrolling_moves_the_line_right_by_hscrol_colour_clocks()
    {
        // With HSCROL the line is fetched one width wider (48 bytes from clock 32), then shifted right.
        var asm = Display(0x12).Set(Asm52.Hscrol, 2)
            .Set(Asm52.Colbk, 0x00).Set(Asm52.Colpf2, 0x40).Set(Asm52.Colpf1, 0x0E)
            .Label("loop").Jmp("loop");
        asm.Data(0x6004, 1);
        asm.Data(0x7008, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
        var console = Asm52.Console(asm);

        console.RunFrame();
        console.RunFrame();

        At(console, 34, Pixel(50)).Should().Be(0x4E);
        At(console, 34, Pixel(48)).Should().Be(0x40);
    }

    [Fact]
    public void Mode_4_and_mode_6_characters_take_their_colours_from_their_bits()
    {
        // Mode 4: "11" pixels are PF2, or PF3 when the character's bit 7 is set.
        var asm = Display(0x04).Set(Asm52.Colpf2, 0x36).Set(Asm52.Colpf3, 0x58).Set(Asm52.Colpf0, 0x14)
            .Label("loop").Jmp("loop");
        asm.Data(0x6000, 0x01, 0x81);
        asm.Data(0x7008, 0xC4, 0xC4, 0xC4, 0xC4, 0xC4, 0xC4, 0xC4, 0xC4); // 11 00 01 00
        var console = Asm52.Console(asm);
        console.RunFrame();
        console.RunFrame();

        At(console, 33, Pixel(48)).Should().Be(0x36);
        At(console, 33, Pixel(50)).Should().Be(0x14);
        At(console, 33, Pixel(52)).Should().Be(0x58);

        // Mode 6: the top two bits pick PF0–PF3 for the lit pixels (character set at $7000 on a 512-byte boundary).
        var six = Display(0x06).Set(Asm52.Colpf1, 0x26).Label("loop").Jmp("loop");
        six.Data(0x6000, 0x41);
        six.Data(0x7008, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
        var console6 = Asm52.Console(six);
        console6.RunFrame();
        console6.RunFrame();

        At(console6, 33, Pixel(48)).Should().Be(0x26);
        At(console6, 33, Pixel(49)).Should().Be(0x00);
    }

    [Fact]
    public void Players_show_their_colour_and_record_collisions()
    {
        var asm = new Asm52()
            .Set(Asm52.Hposp0, 100).Set(Asm52.Grafp0, 0xFF).Set(Asm52.Colpm0, 0x46)
            .Set(Asm52.Hposp1, 104).Set(Asm52.Grafp1, 0x80).Set(Asm52.Colpm1, 0x88)
            .Set(Asm52.Prior, 0x01)
            .Label("loop").Jmp("loop");
        var console = Asm52.Console(asm);

        console.RunFrame();
        console.RunFrame();

        At(console, 100, Pixel(100)).Should().Be(0x46);
        At(console, 100, Pixel(107) + 1).Should().Be(0x46);
        At(console, 100, Pixel(108)).Should().Be(0x00);
        At(console, 100, Pixel(104)).Should().Be(0x46, "player 0 is in front of player 1");
        console.Gtia.Read(0x0C).Should().Be(0x02, "P0PL: player 0 met player 1");
        console.Gtia.Read(0x0D).Should().Be(0x01);
        console.Gtia.Read(0x0E).Should().Be(0x00);
    }

    [Theory]
    [InlineData(0x01, 0x46)] // players in front of the playfield
    [InlineData(0x04, 0xC4)] // the playfield in front of the players
    [InlineData(0x00, 0xC6)] // both win: their colours ORed
    public void Prior_decides_whether_players_or_playfield_show(byte prior, int expected)
    {
        // Mode E filled with "01" (PF0) pixels, player 0 across it.
        var asm = Display(0x0E).Set(Asm52.Colpf0, 0xC4).Set(Asm52.Hposp0, 100).Set(Asm52.Grafp0, 0xFF)
            .Set(Asm52.Colpm0, 0x46).Set(Asm52.Prior, prior)
            .Label("loop").Jmp("loop");
        var screen = new byte[48];
        screen.AsSpan().Fill(0x55);
        asm.Data(0x6000, screen);
        var console = Asm52.Console(asm);

        console.RunFrame();
        console.RunFrame();

        At(console, 32, Pixel(102)).Should().Be(expected);
        At(console, 32, Pixel(60)).Should().Be(0xC4);
        console.Gtia.Read(0x04).Should().Be(0x01, "P0PF: player 0 met PF0");
    }

    [Fact]
    public void The_vertical_blank_and_display_list_interrupts_reach_the_cpu()
    {
        // The display list's mode line has its DLI bit set (on its last scan line).
        var asm = new Asm52();
        asm.Data(0x5000, 0x70, 0xC2, 0x00, 0x60, 0x41, 0x00, 0x50);
        asm.Vector(0x0200, "nmi").Set(Asm52.Dlistl, 0x00).Set(Asm52.Dlisth, 0x50).Set(Asm52.Dmactl, 0x22)
            .Set(Asm52.Nmien, 0xC0)
            .Label("loop").Jmp("loop")
            .Label("nmi").Bit(Asm52.Nmist).Bpl("vbi").Inc(0x0010).Op(0x8D, 0x0F, 0xD4).Rti()
            .Label("vbi").Inc(0x0011).Op(0x8D, 0x0F, 0xD4).Rti();
        var console = Asm52.Console(asm);

        for (var i = 0; i < 10; i++)
        {
            console.RunFrame();
        }

        console.Ram[0x11].Should().BeInRange(9, 10);
        console.Ram[0x10].Should().BeInRange(9, 10);
    }

    [Fact]
    public void Dma_and_refresh_take_cycles_from_the_cpu()
    {
        var idle = Asm52.Console(new Asm52().Label("loop").Jmp("loop"));
        idle.RunFrame();
        var before = idle.Cpu.CycleCount;
        idle.RunFrame();
        var free = idle.Cpu.CycleCount - before;

        // Refresh alone: nine cycles a line.
        free.Should().BeInRange(262 * (114 - 9) - 6, 262 * (114 - 9) + 6);

        // A screen of mode 2 lines: the names and the characters' rows take about 80 more cycles a line.
        var asm = new Asm52();
        var dl = new List<byte> { 0x42, 0x00, 0x60 };
        dl.AddRange(Enumerable.Repeat((byte)0x02, 23));
        dl.AddRange([0x41, 0x00, 0x50]);
        asm.Data(0x5000, [.. dl]);
        asm.Set(Asm52.Dlistl, 0).Set(Asm52.Dlisth, 0x50).Set(Asm52.Chbase, 0x70).Set(Asm52.Dmactl, 0x22)
            .Label("loop").Jmp("loop");
        var busy = Asm52.Console(asm);
        busy.RunFrame();
        before = busy.Cpu.CycleCount;
        busy.RunFrame();
        (busy.Cpu.CycleCount - before).Should().BeLessThan(free - 192 * 40);
    }

    [Fact]
    public void Wsync_holds_the_cpu_until_cycle_105()
    {
        var antic = new Antic(new FlatBus(), new Gtia());
        antic.Power();
        while (antic.Cycle != 40)
        {
            antic.Tick();
        }

        antic.Write(0x0A, 0);
        var line = antic.Line;
        while (antic.Halted)
        {
            antic.Tick();
        }

        antic.Line.Should().Be(line);
        antic.Cycle.Should().Be(105);
    }

    [Fact]
    public void Vcount_counts_lines_in_twos()
    {
        var antic = new Antic(new FlatBus(), new Gtia());
        antic.Power();
        for (var i = 0; i < 114 * 100; i++)
        {
            antic.Tick();
        }

        antic.Read(0x0B).Should().Be(50);
        for (var i = 0; i < 114 * 162; i++)
        {
            antic.Tick();
        }

        antic.FrameDone.Should().BeTrue();
        antic.Line.Should().Be(0);
    }

    [Fact]
    public void Pokey_timer_one_interrupts_every_period()
    {
        // AUDF1 = 255 on the 64 kHz clock: 256 × 28 cycles, about 4.2 times a frame.
        var asm = new Asm52().Vector(0x0202, "irq")
            .Set(Asm52.Skctl, 0x03).Set(Asm52.Audctl, 0x00).Set(Asm52.Audf1, 0xFF).Set(Asm52.Irqen, 0x01)
            .Op(0x8D, 0x09, 0xE8).Cli()
            .Label("loop").Jmp("loop")
            .Label("irq").Op(0x48).Inc(0x0012).Lda(0).Sta(Asm52.Irqen).Lda(1).Sta(Asm52.Irqen).Op(0x68).Rti();
        var console = Asm52.Console(asm);

        for (var i = 0; i < 10; i++)
        {
            console.RunFrame();
        }

        console.Ram[0x12].Should().BeInRange(40, 43);
    }

    [Fact]
    public void A_pure_tone_toggles_every_audf_plus_one_base_clocks()
    {
        var pokey = new Pokey();
        pokey.Power();
        var sink = new CountingSink();
        pokey.AudioSink = sink;
        pokey.Write(0x0F, 0x03);
        pokey.Write(0x00, 9); // AUDF1
        pokey.Write(0x01, 0xA8); // pure tone, volume 8
        pokey.Write(0x09, 0); // STIMER
        for (var i = 0; i < 28_000; i++)
        {
            pokey.AudioClock = i;
            pokey.Tick();
        }

        // 10 base clocks of 28 cycles: a toggle every 280 cycles.
        sink.Deltas.Should().BeInRange(99, 101);
    }

    [Fact]
    public void The_pots_latch_the_sticks_and_the_keypad_raises_its_interrupt()
    {
        var pokey = new Pokey();
        pokey.Power();
        pokey.Write(0x0F, 0x02);
        pokey.Write(0x0E, 0x40);
        pokey.PotInputs[0] = 50;
        pokey.PotInputs[1] = 220;
        pokey.Write(0x0B, 0); // POTGO
        for (var i = 0; i < 114 * 60; i++)
        {
            pokey.Tick();
        }

        pokey.Read(0).Should().Be(50);
        pokey.Read(1).Should().Be((byte)60, "pot 1's counter is still running");
        pokey.Read(0x08).Should().Be(0xFE, "ALLPOT: pot 0 has latched");
        for (var i = 0; i < 114 * 200; i++)
        {
            pokey.Tick();
        }

        pokey.Read(0).Should().Be(50);
        pokey.Read(1).Should().Be(220);

        pokey.Keys[0] = 0x0A;
        for (var i = 0; i < 114; i++)
        {
            pokey.Tick();
        }

        pokey.Read(0x09).Should().Be(0x14, "KBCODE holds the code in bits 1–4");
        (pokey.Read(0x0E) & 0x40).Should().Be(0, "IRQST is active low");
        (pokey.Read(0x0F) & 0x04).Should().Be(0, "SKSTAT: a key is held");
        pokey.IrqAsserted.Should().BeTrue();
    }

    [Fact]
    public void Controllers_drive_the_pots_triggers_and_keypad()
    {
        var console = Asm52.Console(new Asm52().Label("loop").Jmp("loop"));

        console.SetControllerState(0, ControllerState.Right | ControllerState.B);
        console.SetControllerState(1, ControllerState.None.WithStick(-127, 127).WithKey(Atari5200Key.Five));

        console.Pokey.PotInputs[0].Should().Be(220);
        console.Pokey.PotInputs[1].Should().Be(114);
        console.Pokey.PotInputs[2].Should().Be(8);
        console.Pokey.PotInputs[3].Should().Be(8);
        console.Gtia.Read(0x10).Should().Be(0, "TRIG0: the bottom button is down");
        console.Gtia.Read(0x11).Should().Be(1);
        console.Pokey.Keys[1].Should().Be(0x0A);
        Atari5200Keypad.PokeyCode(ControllerState.Start).Should().Be(0x0C);
        Atari5200Keypad.PokeyCode(ControllerState.Select).Should().Be(0x08);
        Atari5200Keypad.PokeyCode(ControllerState.None).Should().Be(Pokey.NoKey);
    }

    [Fact]
    public void A_save_state_round_trips_mid_frame()
    {
        var asm = Display(0x02).Vector(0x0202, "irq")
            .Set(Asm52.Skctl, 0x03).Set(Asm52.Audf1, 0x40).Set(Asm52.Audc1, 0xAF).Set(Asm52.Irqen, 0x01).Cli()
            .Label("loop").Inc(0x0013).LdaAbs(0x0013).Sta(Asm52.Colpf2).Jmp("loop")
            .Label("irq").Op(0x48).Inc(0x0012).Lda(0).Sta(Asm52.Irqen).Lda(1).Sta(Asm52.Irqen).Op(0x68).Rti();
        asm.Data(0x6000, 1, 2, 3);
        var console = Asm52.Console(asm);
        console.RunFrame();
        for (var i = 0; i < 5000; i++)
        {
            console.Cpu.Step();
        }

        var state = console.SaveSnapshot();
        console.RunFrame();
        console.RunFrame();
        var expected = console.FrameBuffer.ToArray();
        var ram = console.Ram.ToArray();

        console.LoadSnapshot(state);
        console.RunFrame();
        console.RunFrame();

        console.FrameBuffer.ToArray().Should().Equal(expected);
        console.Ram.ToArray().Should().Equal(ram);
    }

    private sealed class FlatBus : IAnticBus
    {
        public byte DmaRead(ushort address) => 0;
    }

    private sealed class CountingSink : IAudioSink
    {
        public int Deltas { get; private set; }

        public void AddDelta(int clock, float delta) => Deltas++;

        public void EndFrame(int clocks)
        {
        }
    }
}
