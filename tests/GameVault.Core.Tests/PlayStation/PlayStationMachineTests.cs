using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.Input;
using GameVault.Core.PlayStation;

namespace GameVault.Core.Tests.PlayStation;

/// <summary>
/// The PlayStation's DMA, timers, video timing, CD-ROM drive, controller port, boot and save states
/// (SPEC §7.11, Phase 15), with programs and discs built in the tests.
/// </summary>
public sealed class PlayStationMachineTests
{
    private const uint Origin = 0x80010000;

    /// <summary>A console running a program that just loops (<c>b .</c>), with interrupts left masked.</summary>
    private static PlayStationConsole Idle(ICdImage? disc = null)
    {
        var console = new PlayStationConsole(disc: disc, exe: PsExe.Parse(PlayStationConsoleTests.Exe(0x1000FFFF, Mips.Nop)));
        console.Power();
        return console;
    }

    private static uint Word(PlayStationConsole console, uint address) =>
        BinaryPrimitives.ReadUInt32LittleEndian(console.Ram[(int)(address & 0x1FFFFF)..]);

    private static void SetWord(PlayStationConsole console, uint address, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(console.Ram[(int)(address & 0x1FFFFF)..], value);

    // ---- Video timing ----

    [Fact]
    public void Ntsc_frames_come_sixty_times_a_second_with_a_vblank_interrupt_each()
    {
        var console = Idle();
        console.Write32(0x1F801074, (uint)PsInterrupt.VBlank);
        var vblanks = 0;
        for (var i = 0; i < 60; i++)
        {
            console.RunFrame();
            if ((console.Read32(0x1F801070) & 1) != 0)
            {
                vblanks++;
                console.Write32(0x1F801070, ~1u);
            }
        }

        vblanks.Should().Be(60);
        console.Cpu.Cycles.Should().BeCloseTo((long)PlayStationConsole.CpuClock, 400_000, "60 NTSC frames take about a second");
    }

    [Fact]
    public void Pal_mode_runs_fifty_frames_a_second()
    {
        var console = Idle();
        console.Gpu.WriteGp1(0x08000009); // 320 wide, PAL
        console.RunFrame();
        var start = console.Cpu.Cycles;
        for (var i = 0; i < 50; i++)
        {
            console.RunFrame();
        }

        (console.Cpu.Cycles - start).Should().BeCloseTo((long)PlayStationConsole.CpuClock, 400_000);
    }

    [Fact]
    public void The_frame_is_the_displayed_part_of_vram()
    {
        var console = Idle();
        var gpu = console.Gpu;
        gpu.WriteGp1(0x08000001); // 320 × 240
        gpu.WriteGp1(0x05000000 | 100); // display starts at x = 100
        gpu.WriteGp1(0x07000000 | 0x100 << 10 | 0x10);
        gpu.WriteGp1(0x03000000); // display on
        gpu.WriteGp0(0x020000F8); // red at (112, 0), 16×1
        gpu.WriteGp0(112);
        gpu.WriteGp0(0x00010010);

        console.RunFrame();

        console.ScreenWidth.Should().Be(320);
        console.ScreenHeight.Should().Be(240);
        console.FrameBuffer[12].Should().Be(0x001F);
        console.FrameBuffer[11].Should().Be(0);
        console.PixelAspect.Should().BeApproximately(1.0, 0.001);
    }

    // ---- DMA ----

    [Fact]
    public void Otc_dma_builds_an_ordering_table_backwards()
    {
        var console = Idle();
        console.Write32(0x1F8010F0, 0x08000000); // enable channel 6
        console.Write32(0x1F8010E0, 0x0010_0010); // MADR: the last entry, at $100010
        console.Write32(0x1F8010E4, 5);
        console.Write32(0x1F8010E8, 0x11000002); // start and trigger

        Word(console, 0x100010).Should().Be(0x10000C);
        Word(console, 0x100004).Should().Be(0x100000);
        Word(console, 0x100000).Should().Be(0xFFFFFF, "the first entry is the end marker");
        (console.Read32(0x1F8010E8) & 0x01000000).Should().Be(0, "the channel finishes");
    }

    [Fact]
    public void Gpu_linked_list_dma_sends_every_packet_and_raises_the_dma_interrupt()
    {
        var console = Idle();

        // Two packets: a fill command, then a draw-mode command, ending with the end marker.
        SetWord(console, 0x100000, 3u << 24 | 0x100100);
        SetWord(console, 0x100004, 0x020000F8);
        SetWord(console, 0x100008, 0x00000000);
        SetWord(console, 0x10000C, 0x00010010);
        SetWord(console, 0x100100, 1u << 24 | 0xFFFFFF);
        SetWord(console, 0x100104, 0xE6000001);

        console.Write32(0x1F801074, (uint)PsInterrupt.Dma);
        console.Write32(0x1F8010F0, 0x00000800); // enable channel 2
        console.Write32(0x1F8010F4, 0x00840000); // enable channel 2's flag and the master
        console.Write32(0x1F8010A0, 0x100000);
        console.Write32(0x1F8010A8, 0x01000401); // linked list, from RAM, start

        console.Gpu.Pixel(0, 0).Should().Be(0x001F);
        (console.Gpu.Status & (1u << 11)).Should().NotBe(0);
        (console.Read32(0x1F8010F4) & 0x80000000).Should().NotBe(0);
        console.Cpu.InterruptLine.Should().BeTrue();

        console.Write32(0x1F8010F4, 0x04840000); // acknowledge channel 2's flag
        (console.Read32(0x1F8010F4) & 0x84000000).Should().Be(0);
    }

    // ---- Timers ----

    [Fact]
    public void A_timer_resets_at_its_target_and_interrupts()
    {
        var console = Idle();
        console.Write32(0x1F801074, (uint)PsInterrupt.Timer2);
        console.Write32(0x1F801128, 1000); // target
        console.Write32(0x1F801124, 0x0258); // reset at target, IRQ at target, repeat, system clock / 8

        console.RunCycles(8 * 1000 + 100);

        (console.Read32(0x1F801070) & (uint)PsInterrupt.Timer2).Should().NotBe(0);
        console.Read32(0x1F801120).Should().BeLessThan(1000);
        (console.Read32(0x1F801124) & 0x800).Should().NotBe(0, "the reached-target flag");
        (console.Read32(0x1F801124) & 0x800).Should().Be(0, "reading the mode cleared it");
    }

    [Fact]
    public void Timer1_counts_hblanks()
    {
        var console = Idle();
        console.Write32(0x1F801114, 0x0100); // H-blank source
        console.RunFrame();
        console.Write32(0x1F801110, 0);
        console.RunFrame();
        var lines = console.Read32(0x1F801110);
        lines.Should().BeInRange(260, 266, "one frame is 263 lines");
    }

    // ---- CD-ROM ----

    /// <summary>Sends a command with parameters, then runs until its response interrupt and returns it.</summary>
    private static (int Type, byte[] Response) Command(PlayStationConsole console, byte command, params byte[] parameters)
    {
        console.Write8(0x1F801800, 0);
        foreach (var p in parameters)
        {
            console.Write8(0x1F801802, p);
        }

        console.Write8(0x1F801801, command);
        return NextResponse(console);
    }

    private static (int Type, byte[] Response) NextResponse(PlayStationConsole console)
    {
        console.Write8(0x1F801800, 1);
        for (var i = 0; i < 2000 && (console.Read8(0x1F801803) & 7) == 0; i++)
        {
            console.RunCycles(10_000);
        }

        var type = console.Read8(0x1F801803) & 7;
        var response = new List<byte>();
        while ((console.Read8(0x1F801800) & 0x20) != 0)
        {
            response.Add(console.Read8(0x1F801801));
        }

        console.Write8(0x1F801800, 1);
        console.Write8(0x1F801803, 0x1F); // acknowledge
        return (type, response.ToArray());
    }

    [Fact]
    public void Getid_reports_a_licensed_disc_and_its_region()
    {
        var console = Idle(TestDisc.Create(PlayStationConsoleTests.Exe(Mips.Nop), "Licensed by Sony Computer Entertainment Europe"));
        Command(console, 0x01); // clears the shell-open bit from inserting the disc

        var first = Command(console, 0x1A);
        var second = NextResponse(console);

        first.Type.Should().Be(3);
        second.Type.Should().Be(2);
        second.Response.Should().Equal(0x02, 0x00, 0x20, 0x00, (byte)'S', (byte)'C', (byte)'E', (byte)'E');
    }

    [Fact]
    public void Gettn_and_gettd_describe_the_tracks()
    {
        var console = Idle(TestDisc.Create(PlayStationConsoleTests.Exe(Mips.Nop), audioSectors: 75 * 3));

        Command(console, 0x13).Response.Should().Equal(0x02 | 0x10, 0x01, 0x02);
        var track2 = Command(console, 0x14, 0x02).Response;
        var disc = console.CdRom.Disc!;
        var start = disc.Tracks[1].Start;
        track2[1].Should().Be((byte)(start / 4500 / 10 << 4 | start / 4500 % 10));
        track2[2].Should().Be((byte)(start / 75 % 60 / 10 << 4 | start / 75 % 60 % 10));
    }

    [Fact]
    public void Reading_a_sector_delivers_its_data_through_dma()
    {
        var disc = TestDisc.Create(PlayStationConsoleTests.Exe(Mips.Nop));
        var console = Idle(disc);
        Command(console, 0x01);
        Command(console, 0x0E, 0x80); // double speed
        Command(console, 0x02, 0x00, 0x02, 0x16); // 00:02:16 = sector 16, the volume descriptor
        Command(console, 0x06).Type.Should().Be(3);

        var ready = NextResponse(console);
        ready.Type.Should().Be(1, "INT1: data ready");
        Command(console, 0x09); // pause
        console.Write8(0x1F801800, 0);
        console.Write8(0x1F801803, 0x80); // load the sector into the data FIFO

        console.Write32(0x1F8010F0, 0x00008000); // enable channel 3
        console.Write32(0x1F8010B0, 0x100000);
        console.Write32(0x1F8010B4, 512);
        console.Write32(0x1F8010B8, 0x11000000);

        var bytes = console.Ram.Slice(0x100000, 6).ToArray();
        bytes.Should().Equal(1, (byte)'C', (byte)'D', (byte)'0', (byte)'0', (byte)'1');
        Command(console, 0x10).Response[3].Should().Be(2, "GetlocL returns the sector's header: mode 2");
    }

    [Fact]
    public void Playing_an_audio_track_feeds_the_spu()
    {
        // The drive on its own, so nothing drains its audio before the test reads it.
        var cd = new PsCdRom();
        cd.InsertDisc(TestDisc.Create(PlayStationConsoleTests.Exe(Mips.Nop), audioSectors: 75));
        cd.Reset();
        cd.Write(2, 0x02); // parameter: track 2
        cd.Write(1, 0x03); // Play
        cd.Advance(PsCdRom.CyclesPerSecond / 10);

        cd.ReadSample(out var left, out var right);
        left.Should().Be(4000);
        right.Should().Be(-4000);
    }

    [Fact]
    public void Xa_adpcm_sectors_are_decoded_into_the_audio_stream()
    {
        // One real-time audio sector: mono, 37.8 kHz, 4-bit, filter 0 and shift 0 in every unit, every nibble 1,
        // so each sample decodes to 1 << 12.
        var data = new byte[2324];
        for (var group = 0; group < 18; group++)
        {
            for (var i = 16; i < 128; i++)
            {
                data[group * 128 + i] = 0x11;
            }
        }

        var raw = new byte[2 * ICdImage.SectorSize];
        TestDisc.WriteMode2Sector(raw.AsSpan(0, ICdImage.SectorSize), 150, new byte[2048]);
        TestDisc.WriteMode2Sector(raw.AsSpan(ICdImage.SectorSize), 151, data, submode: 0x64, file: 1, channel: 2);

        var cd = new PsCdRom();
        cd.InsertDisc(new MemoryCdImage(raw));
        cd.Reset();
        cd.Write(2, 0x48); // Setmode: XA-ADPCM on, filter on
        cd.Write(1, 0x0E);
        cd.Advance(100_000);
        cd.Write(0, 1);
        cd.Write(3, 0x1F);
        cd.Write(0, 0);
        cd.Write(2, 1); // Setfilter: file 1, channel 2
        cd.Write(2, 2);
        cd.Write(1, 0x0D);
        cd.Advance(200_000);
        cd.Write(0, 1);
        cd.Write(3, 0x1F);
        cd.Write(0, 0);
        cd.Write(2, 0x00); // Setloc 00:02:01
        cd.Write(2, 0x02);
        cd.Write(2, 0x01);
        cd.Write(1, 0x02);
        cd.Advance(300_000);
        cd.Write(0, 1);
        cd.Write(3, 0x1F);
        cd.Write(0, 0);
        cd.Write(1, 0x06); // ReadN
        cd.Advance(PsCdRom.CyclesPerSecond / 5);

        cd.ReadSample(out var left, out var right);
        left.Should().Be(4096);
        right.Should().Be(4096, "mono plays on both sides");
    }

    [Fact]
    public void The_disc_files_give_the_boot_executable()
    {
        var exe = PlayStationConsoleTests.Exe(Mips.Nop, Mips.Nop);
        var disc = TestDisc.Create(exe);

        PsDisc.BootPath(disc).Should().Be(@"cdrom:\SLUS_000.01;1");
        PsDisc.FileName(@"cdrom:\SLUS_000.01;1").Should().Be("SLUS_000.01");
        PsDisc.BootExecutable(disc).Should().Equal(exe);
    }

    // ---- Booting ----

    /// <summary>A stand-in BIOS: its reset code jumps to the shell entry, so the boot hook takes over.</summary>
    private static byte[] TinyBios(string version = "System ROM Version 4.1 12/16/97 E")
    {
        var bios = new byte[PlayStationConsole.BiosSize];
        uint[] reset = [Mips.Lui(8, 0x8003), Mips.R(0x08, rs: 8), Mips.Nop];
        for (var i = 0; i < reset.Length; i++)
        {
            BitConverter.GetBytes(reset[i]).CopyTo(bios, i * 4);
        }

        System.Text.Encoding.ASCII.GetBytes(version).CopyTo(bios, 0x7FF00);
        return bios;
    }

    [Fact]
    public void Fast_boot_starts_the_discs_executable_at_the_shell()
    {
        // The boot executable stores a marker and loops.
        var exe = PlayStationConsoleTests.Exe(
            Mips.Lui(1, 0x8000),
            Mips.Ori(2, 0, 0x1234),
            Mips.Sw(2, 1, 0x100),
            0x1000FFFF,
            Mips.Nop);
        var console = new PlayStationConsole(TinyBios(), TestDisc.Create(exe), fastBoot: true);
        console.Power();
        console.RunFrame();

        Word(console, 0x80000100).Should().Be(0x1234);
        console.BiosRegion.Should().Be("SCEE", "the BIOS version string ends in E");
    }

    [Fact]
    public void A_ps_exe_runs_after_the_bios_reaches_its_shell()
    {
        var exe = PsExe.Parse(PlayStationConsoleTests.Exe(Mips.Ori(3, 0, 0x55), 0x1000FFFF, Mips.Nop));
        var console = new PlayStationConsole(TinyBios(), exe: exe);
        console.Power();
        console.Cpu.Pc.Should().Be(0xBFC00000, "the BIOS runs first");
        console.RunFrame();

        console.Cpu.GetRegister(3).Should().Be(0x55);
        console.Cpu.GetRegister(29).Should().Be(PsExe.DefaultStack);
    }

    [Fact]
    public void Bios_putchar_calls_reach_the_tty_hook()
    {
        // putchar('H') through A0h and putchar('i') through B0h; a stub at $A0/$B0 returns at once.
        var exe = PlayStationConsoleTests.Exe(
            Mips.Ori(9, 0, 0x3C),
            Mips.Ori(4, 0, 'H'),
            Mips.Ori(10, 0, 0xA0),
            Mips.R(0x09, rs: 10, rd: 31), // jalr t2
            Mips.Nop,
            Mips.Ori(9, 0, 0x3D),
            Mips.Ori(4, 0, 'i'),
            Mips.Ori(10, 0, 0xB0),
            Mips.R(0x09, rs: 10, rd: 31),
            Mips.Nop,
            0x1000FFFF,
            Mips.Nop);
        var console = new PlayStationConsole(exe: PsExe.Parse(exe));
        console.Power();
        uint[] stub = [Mips.R(0x08, rs: 31), Mips.Nop]; // jr ra
        for (var i = 0; i < 2; i++)
        {
            SetWord(console, 0xA0 + (uint)i * 4, stub[i]);
            SetWord(console, 0xB0 + (uint)i * 4, stub[i]);
        }

        var text = new System.Text.StringBuilder();
        console.TtyOutput = c => text.Append(c);
        console.RunCycles(1000);

        text.ToString().Should().Be("Hi");
    }

    // ---- Controllers and memory cards ----

    /// <summary>Selects a port, exchanges <paramref name="bytes"/> and returns what came back and which bytes were acknowledged.</summary>
    private static (byte[] Received, bool[] Acked) Exchange(PlayStationConsole console, int port, params byte[] bytes)
    {
        console.Write16(0x1F80104A, (uint)(0x1003 | port << 13)); // TX enable, select, ACK interrupt on
        var received = new byte[bytes.Length];
        var acked = new bool[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            console.Write32(0x1F801070, 0); // clear I_STAT
            console.Write8(0x1F801040, bytes[i]);
            console.RunCycles(2000);
            received[i] = console.Read8(0x1F801040);
            acked[i] = (console.Read32(0x1F801070) & (uint)PsInterrupt.Controller) != 0;
            console.Write16(0x1F80104A, (uint)(0x1013 | port << 13)); // acknowledge
        }

        console.Write16(0x1F80104A, 0);
        return (received, acked);
    }

    [Fact]
    public void The_digital_pad_reports_its_buttons_active_low()
    {
        var console = Idle();
        console.SetControllerState(0, ControllerState.B | ControllerState.Start | ControllerState.L2);

        var (received, acked) = Exchange(console, 0, 0x01, 0x42, 0, 0, 0);

        received[1..].Should().Equal(0x41, 0x5A, 0xF7, 0xBE);
        acked.Should().Equal(true, true, true, true, false);
    }

    [Fact]
    public void Nothing_answers_on_the_second_port_without_a_pad_selected()
    {
        var console = Idle();
        var (received, acked) = Exchange(console, 1, 0x81, 0x52);
        received.Should().Equal(0xFF, 0xFF);
        acked.Should().Equal(false, false);
    }

    [Fact]
    public void Memory_cards_write_and_read_frames_with_checksums()
    {
        var console = Idle();
        var frame = Enumerable.Range(0, 128).Select(i => (byte)(i * 3)).ToArray();
        byte checksum = 0x00 ^ 0x40;
        foreach (var b in frame)
        {
            checksum ^= b;
        }

        var write = Exchange(console, 0, [0x81, 0x57, 0, 0, 0x00, 0x40, .. frame, checksum, 0, 0, 0]);
        write.Received[^1].Should().Be((byte)'G');
        console.Battery!.BatteryRam.Slice(0x40 * 128, 128).ToArray().Should().Equal(frame);

        var read = Exchange(console, 0, [0x81, 0x52, 0, 0, 0x00, 0x40, 0, 0, 0, 0, .. new byte[128], 0, 0]);
        read.Received[1].Should().Be(0x00, "the not-yet-written flag cleared after the write");
        read.Received[10..138].Should().Equal(frame);
        read.Received[138].Should().Be(checksum);
        read.Received[139].Should().Be((byte)'G');
    }

    [Fact]
    public void New_memory_cards_are_formatted()
    {
        var card = new PsMemoryCard().BatteryRam;
        card[0].Should().Be((byte)'M');
        card[1].Should().Be((byte)'C');
        card[128].Should().Be(0xA0, "directory entries are free");
        byte checksum = 0;
        for (var i = 0; i < 127; i++)
        {
            checksum ^= card[128 + i];
        }

        card[255].Should().Be(checksum);
    }

    // ---- Save states ----

    [Fact]
    public void A_snapshot_replays_identically()
    {
        // A program that keeps changing RAM and VRAM: a counter, drawn as a fill colour.
        var program = new uint[]
        {
            Mips.Lui(4, 0x1F80),
            Mips.Addiu(5, 5, 1),          // loop: counter++
            Mips.Lui(6, 0x0200),
            Mips.Addu(6, 6, 5),
            Mips.Sw(6, 4, 0x1810),        // GP0: fill with the counter as colour
            Mips.Sw(0, 4, 0x1810),        // at (0, 0)
            Mips.Ori(7, 0, 0x10),
            Mips.Lui(8, 1),
            Mips.Addu(7, 7, 8),
            Mips.Sw(7, 4, 0x1810),        // 16×1
            Mips.Beq(0, 0, -10),
            Mips.Nop,
        };
        var console = new PlayStationConsole(exe: PsExe.Parse(PlayStationConsoleTests.Exe(program)));
        console.Power();
        console.RunFrame();
        console.SetControllerState(0, ControllerState.A);
        var snapshot = console.SaveSnapshot();
        console.RunFrame();
        console.RunFrame();
        var expectedRam = console.Ram.ToArray();
        var expectedVram = console.Gpu.Vram.ToArray();

        console.LoadSnapshot(snapshot);
        console.RunFrame();
        console.RunFrame();

        console.Ram.ToArray().Should().Equal(expectedRam);
        console.Gpu.Vram.ToArray().Should().Equal(expectedVram);
        snapshot.Length.Should().BeLessThan(4 * 1024 * 1024);
    }

    [Fact]
    public void Snapshots_from_another_game_are_refused()
    {
        var a = new PlayStationConsole(exe: PsExe.Parse(PlayStationConsoleTests.Exe(Mips.Nop)));
        var b = new PlayStationConsole(exe: PsExe.Parse(PlayStationConsoleTests.Exe(Mips.Nop, Mips.Nop)));
        a.Power();
        b.Power();

        var load = () => b.LoadSnapshot(a.SaveSnapshot());
        load.Should().Throw<GameVault.Core.State.StateFormatException>();
    }
}
