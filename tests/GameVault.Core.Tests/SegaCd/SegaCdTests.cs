using FluentAssertions;
using GameVault.Core.Cartridge;
using GameVault.Core.Genesis;
using GameVault.Core.PlayStation;
using GameVault.Core.SegaCd;

namespace GameVault.Core.Tests.SegaCd;

/// <summary>The Sega CD (sega-cd-32x.md, milestone 1), with programs written here and a stand-in BIOS (no Sega code).</summary>
public sealed class SegaCdTests
{
    // ---- 68000 words ----

    private static ushort[] MoveB(byte value, uint address) => [0x13FC, value, (ushort)(address >> 16), (ushort)address];

    private static ushort[] MoveBMem(uint from, uint to) => [0x13F9, (ushort)(from >> 16), (ushort)from, (ushort)(to >> 16), (ushort)to];

    private static ushort[] AddqB(uint address) => [0x5239, (ushort)(address >> 16), (ushort)address];

    private static readonly ushort[] EnableInterrupts = [0x46FC, 0x2000];

    private static readonly ushort[] Loop = [0x60FE];

    private static readonly ushort[] Rte = [0x4E73];

    private static ushort[] Words(params ushort[][] parts) => [.. parts.SelectMany(p => p)];

    private static void Put(Span<byte> memory, int at, ushort[] words)
    {
        for (var i = 0; i < words.Length; i++)
        {
            memory[at + i * 2] = (byte)(words[i] >> 8);
            memory[at + i * 2 + 1] = (byte)words[i];
        }
    }

    private static void Long(Span<byte> memory, int at, uint value)
    {
        memory[at] = (byte)(value >> 24);
        memory[at + 1] = (byte)(value >> 16);
        memory[at + 2] = (byte)(value >> 8);
        memory[at + 3] = (byte)value;
    }

    /// <summary>A stand-in 128 KB BIOS: the main CPU's stack and reset vector, and <paramref name="main"/> at $200.</summary>
    private static byte[] Bios(ushort[] main)
    {
        var bios = new byte[SegaCdUnit.BiosSize];
        Long(bios, 0, 0xFFFFFE00);
        Long(bios, 4, 0x200);
        Put(bios, 0x200, main);
        return bios;
    }

    /// <summary>A Genesis with the Sega CD; the sub CPU's vectors and <paramref name="sub"/> (at $1000) are in PRG-RAM.</summary>
    private static GenesisConsole Console(ushort[] main, ushort[]? sub = null, ICdImage? disc = null, Action<Span<byte>>? prg = null)
    {
        var unit = new SegaCdUnit(Bios(main), disc, GenesisConsole.NtscMasterClock);
        var console = new GenesisConsole(new GenesisCartridge(new byte[0x200]), GenesisRegion.Americas, segaCd: unit);
        console.Power();
        Long(unit.PrgRam, 0, 0x0007FF00);
        Long(unit.PrgRam, 4, 0x1000);
        Put(unit.PrgRam, 0x1000, sub ?? Loop);
        prg?.Invoke(unit.PrgRam);
        return console;
    }

    /// <summary>A disc: track 1 data (mode 1, <paramref name="dataSectors"/> sectors from 00:02:00), track 2 audio after it.</summary>
    private static MemoryCdImage Disc(int dataSectors = 100, int audioSectors = 50)
    {
        var sectors = new byte[(dataSectors + audioSectors) * ICdImage.SectorSize];
        for (var i = 0; i < dataSectors; i++)
        {
            var address = 150 + i;
            var at = i * ICdImage.SectorSize;
            sectors[at] = 0x00;
            sectors.AsSpan(at + 1, 10).Fill(0xFF);
            sectors[at + 11] = 0x00;
            sectors[at + 12] = Bcd(address / 75 / 60);
            sectors[at + 13] = Bcd(address / 75 % 60);
            sectors[at + 14] = Bcd(address % 75);
            sectors[at + 15] = 0x01;
            for (var b = 0; b < 2048; b++)
            {
                sectors[at + 16 + b] = (byte)(i + b);
            }
        }

        return new MemoryCdImage(sectors,
        [
            new CdTrack(1, CdTrackType.Mode1, 150, dataSectors, 150),
            new CdTrack(2, CdTrackType.Audio, 150 + dataSectors, audioSectors),
        ]);
    }

    private static byte Bcd(int value) => (byte)((value / 10 << 4) | (value % 10));

    private static void Command(SegaCdUnit unit, params byte[] nibbles)
    {
        var all = new byte[10];
        nibbles.CopyTo(all, 0);
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += all[i];
        }

        all[9] = (byte)(~sum & 0x0F);
        for (var i = 0; i < 10; i++)
        {
            unit.WriteByte(0xFF8042u + (uint)i, all[i]);
        }
    }

    private static int[] Status(SegaCdUnit unit) =>
        [.. Enumerable.Range(0, 10).Select(i => (int)unit.ReadByte(0xFF8038u + (uint)i))];

    private static void CdcWrite(SegaCdUnit unit, int register, byte value)
    {
        unit.WriteByte(0xFF8005, (byte)register);
        unit.WriteByte(0xFF8007, value);
    }

    // ---- The unit and the two CPUs ----

    [Fact]
    public void A_bios_that_isnt_128_kb_is_refused()
    {
        var act = () => new SegaCdUnit(new byte[0x10000], null, GenesisConsole.NtscMasterClock);

        act.Should().Throw<RomFormatException>().WithMessage("*128 KB*");
    }

    [Fact]
    public void The_genesis_sees_the_unit_and_boots_from_its_bios()
    {
        var console = Console(Words(MoveB(0x42, 0xFFFF00), Loop));

        console.RunFrame();

        console.System.Should().Be(GameVault.Core.Systems.GameSystem.SegaCd);
        (console.ReadByte(0xA10001) & 0x20).Should().Be(0, "the version register's bit 5 says an expansion unit is attached");
        console.WorkRam[0xFF00].Should().Be(0x42);
        console.Battery.Should().BeSameAs(console.SegaCd);
    }

    [Fact]
    public void The_main_cpu_releases_the_sub_cpu_and_they_talk_through_the_communication_words()
    {
        var main = Words(MoveB(0x33, 0xA12010), MoveB(0x01, 0xA12001), Loop);
        var sub = Words(MoveB(0x5A, 0xFF8020), MoveBMem(0xFF8010, 0xFF8021), Loop);
        var console = Console(main, sub);
        var unit = console.SegaCd!;
        unit.SubRunning.Should().BeFalse("the sub CPU starts held in reset");

        console.RunFrame();

        unit.SubRunning.Should().BeTrue();
        unit.MainReadGate(0x20).Should().Be(0x5A);
        unit.MainReadGate(0x21).Should().Be(0x33);
        (unit.MainReadGate(0x01) & 0x01).Should().Be(1, "SRES reads 1 while the sub CPU runs");
    }

    [Fact]
    public void The_bus_request_halts_the_sub_cpu()
    {
        var sub = Words(AddqB(0xFF8020), [0x60F8]); // count, forever
        var console = Console(Words(MoveB(0x03, 0xA12001), Loop), sub);
        var unit = console.SegaCd!;

        console.RunFrame();
        var count = unit.MainReadGate(0x20);
        console.RunFrame();

        unit.SubRunning.Should().BeFalse();
        unit.MainReadGate(0x20).Should().Be(count, "a halted sub CPU doesn't run");
        (unit.MainReadGate(0x01) & 0x02).Should().Be(2);
    }

    [Fact]
    public void The_main_cpu_raises_the_sub_cpus_level_2_interrupt()
    {
        var sub = Words(MoveB(0x04, 0xFF8033), EnableInterrupts, Loop);
        var console = Console(Words(MoveB(0x01, 0xA12001), Loop), sub, prg: prg =>
        {
            Long(prg, 0x68, 0x2000); // level 2 autovector
            Put(prg, 0x2000, Words(AddqB(0xFF8022), Rte));
        });
        var unit = console.SegaCd!;
        console.RunFrame();

        for (var i = 0; i < 5; i++)
        {
            unit.MainWriteGate(0x00, 0x01);
            console.RunFrame();
        }

        unit.MainReadGate(0x22).Should().Be(5);
        (unit.MainReadGate(0x00) & 0x80).Should().Be(0x80, "IEN2 shows in the main CPU's register");
    }

    [Fact]
    public void The_timer_interrupts_every_value_times_384_clocks()
    {
        var sub = Words(MoveB(0x08, 0xFF8033), MoveB(100, 0xFF8031), EnableInterrupts, Loop);
        var console = Console(Words(MoveB(0x01, 0xA12001), Loop), sub, prg: prg =>
        {
            Long(prg, 0x6C, 0x2000); // level 3
            Put(prg, 0x2000, Words(AddqB(0xFF8023), Rte));
        });

        console.RunFrame();
        var first = console.SegaCd!.MainReadGate(0x23);
        console.RunFrame();

        // 12.5 MHz / 60 / (100 × 384) ≈ 5.4 a frame.
        (console.SegaCd.MainReadGate(0x23) - first).Should().BeInRange(5, 6);
    }

    [Fact]
    public void Prg_ram_is_banked_for_the_main_cpu_and_write_protected_for_the_sub_cpu()
    {
        var unit = Console(Loop).SegaCd!;

        unit.MainWriteGate(0x03, 0x40); // bank 1
        unit.MainWriteByte(0x020010, 7);
        unit.MainWriteGate(0x02, 0x10); // the first $2000 bytes are protected
        unit.WriteByte(0x0100, 9);
        unit.WriteByte(0x3000, 9);

        unit.PrgRam[0x20010].Should().Be(7);
        unit.MainReadByte(0x020010).Should().Be(7);
        unit.PrgRam[0x0100].Should().Be(0);
        unit.PrgRam[0x3000].Should().Be(9);
        (unit.MainReadGate(0x03) >> 6).Should().Be(1);
    }

    [Fact]
    public void Word_ram_changes_hands_in_2m_mode()
    {
        var unit = Console(Loop).SegaCd!;
        unit.MainWriteByte(0x200000, 0x11);
        unit.ReadByte(0x080000).Should().Be(0, "the main CPU has Word RAM at power-on");

        unit.MainWriteGate(0x03, 0x02); // DMNA: give it to the sub CPU

        (unit.MainReadGate(0x03) & 0x03).Should().Be(0x02, "RET clear, DMNA set");
        unit.ReadByte(0x080000).Should().Be(0x11);
        unit.MainReadByte(0x200000).Should().Be(0);

        unit.WriteByte(0xFF8003, 0x01); // RET: back to the main CPU
        (unit.MainReadGate(0x03) & 0x03).Should().Be(0x01);
        unit.MainReadByte(0x200000).Should().Be(0x11);
    }

    [Fact]
    public void In_1m_mode_each_cpu_has_a_bank_and_the_dot_image_writes_by_priority()
    {
        var unit = Console(Loop).SegaCd!;
        unit.WriteByte(0xFF8003, 0x04); // 1M, RET clear: the main CPU has bank 0, the sub CPU bank 1

        unit.MainWriteByte(0x200000, 0xAA);
        unit.MainWriteByte(0x200001, 0xBB);
        unit.WriteByte(0x0C0000, 0xCC);

        unit.WordRam[..4].ToArray().Should().Equal(0xAA, 0xBB, 0xCC, 0x00);

        unit.WriteByte(0x080000, 0x05); // the dot image: bank 1's first dot
        unit.WordRam[2].Should().Be(0x5C);
        unit.ReadByte(0x080001).Should().Be(0x0C);

        unit.WriteByte(0xFF8003, 0x04 | 0x08); // priority mode 1: underwrite
        unit.WriteByte(0x080000, 0x07);
        unit.WordRam[2].Should().Be(0x5C, "underwrite keeps a dot that's already set");
        unit.WriteByte(0x080002, 0x07);
        unit.WordRam[3].Should().Be(0x70);
    }

    [Theory]
    [InlineData(0x00000, 32)] // the first 64 KB: columns 32 cells tall
    [InlineData(0x10000, 16)]
    [InlineData(0x18000, 8)]
    [InlineData(0x1C000, 4)]
    [InlineData(0x1E000, 4)]
    public void In_1m_mode_the_cell_image_reads_the_main_cpus_bank_cell_by_cell(int areaStart, int cellsTall)
    {
        var unit = Console(Loop).SegaCd!;
        unit.WriteByte(0xFF8003, 0x04); // 1M mode

        // The bank as a bitmap 512 dots (256 bytes) wide: give each byte its line and its column of cells.
        var lineBase = areaStart / 256;
        for (var line = 0; line < cellsTall * 8; line++)
        {
            for (var column = 0; column < 64; column++)
            {
                for (var b = 0; b < 4; b++)
                {
                    unit.MainWriteByte((uint)(0x200000 + (lineBase + line) * 256 + column * 4 + b), (byte)(line * 4 + b ^ column));
                }
            }
        }

        // In the cell image the area's long words run down each column: 8 lines make a cell, then the next cell below.
        for (var column = 0; column < 64; column += 21)
        {
            for (var line = 0; line < cellsTall * 8; line += 5)
            {
                var image = (uint)(0x220000 + areaStart + (column * cellsTall * 8 + line) * 4);
                for (var b = 0; b < 4; b++)
                {
                    unit.MainReadByte(image + (uint)b).Should().Be((byte)(line * 4 + b ^ column), $"column {column}, line {line}");
                }
            }
        }

        unit.MainWriteByte((uint)(0x220000 + areaStart + 4), 0xEE); // the second long word: line 1 of column 0
        unit.MainReadByte((uint)(0x200000 + (lineBase + 1) * 256)).Should().Be(0xEE, "writes go through the view as well");
    }

    // ---- The drive ----

    [Fact]
    public void The_drive_answers_toc_requests_with_checksummed_status()
    {
        var unit = Console(Loop, disc: Disc()).SegaCd!;

        Command(unit, 0x2, 0, 0, 0x4); // first and last tracks
        var tracks = Status(unit);
        tracks[1].Should().Be(4);
        (tracks[2] * 10 + tracks[3]).Should().Be(1);
        (tracks[4] * 10 + tracks[5]).Should().Be(2);
        tracks[9].Should().Be(~tracks.Take(9).Sum() & 0x0F);

        Command(unit, 0x2, 0, 0, 0x3); // the lead-out: 150 + 150 sectors = 00:04:00
        Status(unit)[2..8].Should().Equal(0, 0, 0, 4, 0, 0);

        Command(unit, 0x2, 0, 0, 0x5, 0, 2); // track 2 starts at 250 = 00:03:25, audio
        Status(unit)[2..9].Should().Equal(0, 0, 0, 3, 2, 5, 2);

        Command(unit, 0x2, 0, 0, 0x5, 0, 1); // track 1 is data: bit 3 of the frames' tens
        (Status(unit)[6] & 0x08).Should().Be(0x08);
    }

    [Fact]
    public void Play_seeks_then_hands_each_sector_to_the_cdc()
    {
        var unit = Console(Loop, disc: Disc()).SegaCd!;
        CdcWrite(unit, 0x0A, 0x84); // CTRL0: decoder on, write to the buffer

        Command(unit, 0x3, 0, 0, 0, 0, 2, 0, 5); // play from 00:02:05
        unit.Drive.State.Should().Be(SegaCdDrive.Seeking);
        for (var i = 0; i < 4 && unit.Drive.State != SegaCdDrive.Playing; i++)
        {
            unit.Drive.Tick(unit.Cdc);
        }

        unit.Drive.State.Should().Be(SegaCdDrive.Playing);
        unit.Drive.Tick(unit.Cdc);

        unit.WriteByte(0xFF8005, 0x04); // HEAD0–3
        var header = Enumerable.Range(0, 4).Select(_ => unit.ReadByte(0xFF8007)).ToArray();
        header.Should().Equal(0x00, 0x02, 0x05, 0x01);
        unit.Drive.Position.Should().Be(156);

        Command(unit, 0x2, 0, 0, 0x0); // the absolute time while playing
        Status(unit)[0].Should().Be(SegaCdDrive.Playing);
        Status(unit)[2..8].Should().Equal(0, 0, 0, 2, 0, 6);
    }

    [Fact]
    public void Audio_tracks_play_as_cd_audio_and_stop_gives_the_stopped_state()
    {
        var unit = Console(Loop, disc: Disc()).SegaCd!;
        Command(unit, 0x3, 0, 0, 0, 0, 3, 2, 5);
        for (var i = 0; i < 6; i++)
        {
            unit.Drive.Tick(unit.Cdc);
        }

        unit.Drive.AudioPlaying.Should().BeTrue();

        Command(unit, 0x1);
        unit.Drive.State.Should().Be(SegaCdDrive.StoppedAfterCommand);
        unit.Drive.AudioPlaying.Should().BeFalse();
    }

    // ---- The CDC ----

    [Fact]
    public void The_cdc_decodes_a_sector_and_dmas_it_to_prg_ram()
    {
        var console = Console(Loop, disc: Disc());
        var unit = console.SegaCd!;
        CdcWrite(unit, 0x01, 0x22); // IFCTRL: decoder interrupt, data out
        CdcWrite(unit, 0x0A, 0x84);
        var sector = new byte[ICdImage.SectorSize];
        unit.Drive.Disc!.ReadSector(160, sector);
        unit.Cdc.Decode(sector);
        unit.Cdc.Interrupt.Should().BeTrue("the decoder interrupt is pending");

        // PT points at the header; the data starts four bytes on.
        unit.WriteByte(0xFF8005, 0x08);
        var pt = unit.ReadByte(0xFF8007) | (unit.ReadByte(0xFF8007) << 8);
        unit.WriteByte(0xFF8004, 5); // destination: PRG-RAM
        unit.WriteByte(0xFF800A, 0x02); // DMA address $1000 / 8
        unit.WriteByte(0xFF800B, 0x00);
        CdcWrite(unit, 0x02, 0xFF); // DBC = 2047
        CdcWrite(unit, 0x03, 0x07);
        CdcWrite(unit, 0x04, (byte)(pt + 4)); // DAC
        CdcWrite(unit, 0x05, (byte)((pt + 4) >> 8));
        CdcWrite(unit, 0x06, 0); // DTTRG

        console.RunFrame();

        unit.PrgRam.Slice(0x1000, 2048).ToArray().Should().Equal(sector.AsSpan(16, 2048).ToArray());
        (unit.ReadByte(0xFF8004) & 0x80).Should().Be(0x80, "EDT: the transfer ended");
    }

    [Fact]
    public void The_main_cpu_can_read_the_cdc_through_its_host_port()
    {
        var unit = Console(Loop, disc: Disc()).SegaCd!;
        CdcWrite(unit, 0x01, 0x02);
        CdcWrite(unit, 0x0A, 0x84);
        var sector = new byte[ICdImage.SectorSize];
        unit.Drive.Disc!.ReadSector(151, sector);
        unit.Cdc.Decode(sector);
        unit.WriteByte(0xFF8004, 2); // destination: the main CPU
        CdcWrite(unit, 0x02, 0x03); // four bytes
        CdcWrite(unit, 0x03, 0x00);
        unit.WriteByte(0xFF8005, 0x08);
        var pt = unit.ReadByte(0xFF8007) | (unit.ReadByte(0xFF8007) << 8);
        CdcWrite(unit, 0x04, (byte)pt); // from the header
        CdcWrite(unit, 0x05, (byte)(pt >> 8));
        CdcWrite(unit, 0x06, 0);

        (unit.MainReadGate(0x04) & 0x40).Should().Be(0x40, "DSR: data is ready");
        unit.MainReadGateWord(0xA12008).Should().Be(0x0002);
        unit.MainReadGateWord(0xA12008).Should().Be(0x0101);
        (unit.MainReadGate(0x04) & 0x80).Should().Be(0x80, "EDT after the last word");
    }

    // ---- PCM, graphics, font ----

    [Fact]
    public void Pcm_channels_play_sign_magnitude_samples_at_their_step_and_loop()
    {
        var pcm = new Rf5c164();
        pcm.Power();
        pcm.Ram[0] = 0x81;
        pcm.Ram[1] = 0x82;
        pcm.Ram[2] = 0xFF;
        pcm.WriteRegister(7, 0xC0); // sound on, channel 0
        pcm.WriteRegister(0, 255);
        pcm.WriteRegister(1, 0xFF);
        pcm.WriteRegister(2, 0x00);
        pcm.WriteRegister(3, 0x08); // step 1.0
        pcm.WriteRegister(8, 0xFE); // channel 0 on

        var left = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            pcm.Sample();
            left.Add(pcm.Left);
        }

        // (magnitude × 255 × 15) >> 5, and the $FF marker loops to 0.
        left.Should().Equal(119, 239, 119, 239);

        pcm.WriteRegister(3, 0x04); // step 0.5: each sample twice
        pcm.WriteRegister(8, 0xFF);
        pcm.WriteRegister(8, 0xFE);
        var half = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            pcm.Sample();
            half.Add(pcm.Left);
        }

        half.Should().Equal(119, 119, 239, 239);
    }

    [Fact]
    public void The_graphics_asic_draws_a_stamp_map_into_the_image_buffer_and_interrupts()
    {
        var sub = Words(MoveB(0x02, 0xFF8033), EnableInterrupts, Loop);
        var console = Console(Words(MoveB(0x01, 0xA12001), MoveB(0x02, 0xA12003), Loop), sub, prg: prg =>
        {
            Long(prg, 0x64, 0x2000); // level 1
            Put(prg, 0x2000, Words(AddqB(0xFF8024), Rte));
        });
        var unit = console.SegaCd!;
        console.RunFrame();

        var ram = unit.WordRam;
        ram.Slice(0x80, 128).Fill(0x12); // stamp 1: dots 1 and 2
        for (var i = 0; i < 16 * 16; i++)
        {
            ram[0x10000 + i * 2 + 1] = 1; // every map entry is stamp 1
        }

        ram[0x20004] = 0x08; // trace: X = Y = 0, ΔX = 1.0, ΔY = 0
        for (var line = 1; line < 8; line++)
        {
            ram[0x20000 + line * 8 + 2] = (byte)((line << 3) >> 8);
            ram[0x20000 + line * 8 + 3] = (byte)(line << 3);
            ram[0x20000 + line * 8 + 4] = 0x08;
        }

        unit.WriteWord(0xFF8058, 0x0000); // 16×16 stamps, a 1×1-screen map, no repeat
        unit.WriteWord(0xFF805A, 0x4000); // the map at $10000
        unit.WriteWord(0xFF805C, 0x0000); // one cell high
        unit.WriteWord(0xFF805E, 0xC000); // the image at $30000
        unit.WriteWord(0xFF8060, 0x0000);
        unit.WriteWord(0xFF8062, 8);
        unit.WriteWord(0xFF8064, 8);
        unit.WriteWord(0xFF8066, 0x8000); // the trace table at $20000: go

        (unit.ReadByte(0xFF8058) & 0x80).Should().Be(0x80, "busy");
        unit.WordRam.Slice(0x30000, 32).ToArray().Should().AllBeEquivalentTo((byte)0x12);
        console.RunFrame();
        unit.MainReadGate(0x24).Should().Be(1);
        (unit.ReadByte(0xFF8058) & 0x80).Should().Be(0);
    }

    [Fact]
    public void The_font_generator_turns_bits_into_colour_nibbles()
    {
        var unit = Console(Loop).SegaCd!;
        unit.WriteByte(0xFF804D, 0xA3); // foreground A, background 3
        unit.WriteWord(0xFF804E, 0b1000_0110_0000_1111);

        unit.ReadWord(0xFF8050).Should().Be(0xA333);
        unit.ReadWord(0xFF8052).Should().Be(0x3AA3);
        unit.ReadWord(0xFF8054).Should().Be(0x3333);
        unit.ReadWord(0xFF8056).Should().Be(0xAAAA);
    }

    [Fact]
    public void Backup_ram_sits_on_the_odd_bytes()
    {
        var unit = Console(Loop).SegaCd!;

        unit.WriteByte(0xFE0001, 0x5A);
        unit.WriteByte(0xFE0003, 0xA5);

        unit.BatteryRam[..2].ToArray().Should().Equal(0x5A, 0xA5);
        unit.ReadByte(0xFE0003).Should().Be(0xA5);
    }

    [Fact]
    public void A_save_state_round_trips_both_cpus_and_the_drive()
    {
        var sub = Words(AddqB(0xFF8020), [0x60F8]);
        var console = Console(Words(MoveB(0x01, 0xA12001), Loop), sub, Disc());
        var unit = console.SegaCd!;
        console.RunFrame();
        Command(unit, 0x3, 0, 0, 0, 0, 2, 1, 0);
        console.RunFrame();

        var state = console.SaveSnapshot();
        console.RunFrame();
        console.RunFrame();
        var count = unit.MainReadGate(0x20);
        var position = unit.Drive.Position;

        console.LoadSnapshot(state);
        console.RunFrame();
        console.RunFrame();

        unit.MainReadGate(0x20).Should().Be(count);
        unit.Drive.Position.Should().Be(position);
    }

    // ---- Mode 1: a cartridge that uses the CD ----

    /// <summary>A Genesis booting a cartridge (<paramref name="main"/> at $200), with the Sega CD in mode 1.</summary>
    private static GenesisConsole Mode1Console(ushort[] main, ushort[]? sub = null, Action<byte[]>? rom = null)
    {
        var cartridge = new byte[0x20000];
        Long(cartridge, 0, 0xFFFFFE00);
        Long(cartridge, 4, 0x200);
        "SEGA GENESIS    "u8.CopyTo(cartridge.AsSpan(0x100));
        Put(cartridge, 0x200, main);
        rom?.Invoke(cartridge);

        var bios = Bios(Loop);
        "SEGA"u8.CopyTo(bios.AsSpan(0x100));
        var unit = new SegaCdUnit(bios, Disc(), GenesisConsole.NtscMasterClock) { Mode1 = true };
        var console = new GenesisConsole(new GenesisCartridge(cartridge), GenesisRegion.Americas, segaCd: unit);
        console.Power();
        Long(unit.PrgRam, 0, 0x0007FF00);
        Long(unit.PrgRam, 4, 0x1000);
        Put(unit.PrgRam, 0x1000, sub ?? Loop);
        return console;
    }

    [Fact]
    public void In_mode_1_the_cartridge_boots_and_the_unit_answers_at_400000()
    {
        // The cartridge copies the BIOS's "SEGA" into RAM, writes PRG-RAM through $420000 and starts the sub CPU.
        var main = Words(
            MoveBMem(0x400100, 0xFFFF00),
            MoveBMem(0x400103, 0xFFFF01),
            MoveB(0x77, 0x420010),
            MoveB(0x33, 0xA12010),
            MoveB(0x01, 0xA12001),
            Loop);
        var sub = Words(MoveBMem(0xFF8010, 0xFF8020), Loop);
        var console = Mode1Console(main, sub);
        var unit = console.SegaCd!;

        console.RunFrame();

        console.System.Should().Be(GameVault.Core.Systems.GameSystem.Genesis, "a mode 1 game is a cartridge");
        console.WorkRam[0xFF00].Should().Be((byte)'S', "the BIOS reads at $400000");
        console.WorkRam[0xFF01].Should().Be((byte)'A');
        unit.PrgRam[0x10].Should().Be(0x77, "PRG-RAM's bank is at $420000");
        unit.SubRunning.Should().BeTrue();
        unit.MainReadGate(0x20).Should().Be(0x33, "the sub CPU read the main CPU's command word");
        console.ReadByte(0x000100).Should().Be((byte)'S', "the cartridge stays at $000000");
        console.ReadByte(0x000101).Should().Be((byte)'E');
        console.RomHash.Should().NotBe(unit.DiscHash, "the game is the cartridge");
    }

    [Fact]
    public void In_mode_1_word_ram_is_at_600000()
    {
        var console = Mode1Console(Words(MoveB(0x5C, 0x600002), MoveBMem(0x600002, 0xFFFF00), Loop));

        console.RunFrame();

        console.SegaCd!.WordRam[2].Should().Be(0x5C);
        console.WorkRam[0xFF00].Should().Be(0x5C);
    }

    [Fact]
    public void In_mode_1_the_cartridges_own_save_wins_over_backup_ram()
    {
        var plain = Mode1Console(Loop);
        plain.Battery.Should().BeSameAs(plain.SegaCd, "a cartridge without a save uses backup RAM");

        var saving = Mode1Console(Loop, rom: rom =>
        {
            "RA"u8.CopyTo(rom.AsSpan(0x1B0));
            rom[0x1B2] = 0xF8;
            rom[0x1B3] = 0x20;
            Long(rom, 0x1B4, 0x200001); // byte-wide SRAM on the odd bytes of $200001–$203FFF
            Long(rom, 0x1B8, 0x203FFF);
        });
        saving.Battery.Should().BeSameAs(saving.Cartridge);
    }

    [Fact]
    public void A_mode_1_save_state_round_trips_the_cartridge_and_the_unit()
    {
        var sub = Words(AddqB(0xFF8020), [0x60F8]);
        var console = Mode1Console(Words(MoveB(0x01, 0xA12001), AddqB(0xFFFF10), [0x60F8]), sub);
        console.RunFrame();

        var state = console.SaveSnapshot();
        console.RunFrame();
        var count = console.SegaCd!.MainReadGate(0x20);
        var ram = console.WorkRam[0xFF10];

        console.LoadSnapshot(state);
        console.RunFrame();

        console.SegaCd.MainReadGate(0x20).Should().Be(count);
        console.WorkRam[0xFF10].Should().Be(ram);
    }
}
