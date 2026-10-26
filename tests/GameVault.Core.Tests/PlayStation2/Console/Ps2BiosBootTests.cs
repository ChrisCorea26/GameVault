using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using GameVault.Core.PlayStation2;
using static GameVault.Core.Tests.PlayStation2.Console.MipsAssembler;

namespace GameVault.Core.Tests.PlayStation2.Console;

/// <summary>
/// Booting through the BIOS (Phase 17 plan §9, "BIOS boot"), with the hand-assembled <see cref="StandInBios"/>: both
/// processors run it from the reset vector, the SIF's mailbox and DMA between the two real CPUs, the CDVD, the GS, the
/// two consoles' text, and the fast boot of an ELF or a disc's boot executable when the BIOS reaches EELOAD.
/// </summary>
public sealed class Ps2BiosBootTests
{
    private static readonly byte[] Bios = StandInBios.Build();

    /// <summary>The stand-in without its disc read (the drive's spin-up), for the tests about fast boot.</summary>
    private static readonly byte[] QuickBios = StandInBios.Build(readDisc: false);

    private const string BootFile = @"cdrom0:\SLUS_000.01;1";

    /// <summary>A console with its EE and IOP text captured separately (they print at the same time).</summary>
    private sealed class Machine
    {
        public Machine(Ps2Boot boot, Ps2ConsoleOptions? options = null, bool readDisc = false)
        {
            Console = new Ps2Console(boot, readDisc ? Bios : QuickBios, null, options ?? Ps2ConsoleOptions.Default) { DebugOutput = c => Ee.Append(c) };
            Console.Iop.DebugOutput = c => Iop.Append(c);
        }

        public Ps2Console Console { get; }

        public StringBuilder Ee { get; } = new();

        public StringBuilder Iop { get; } = new();

        /// <summary>Runs frames until <paramref name="done"/> (at most <paramref name="frames"/>), then one more.</summary>
        public void RunUntil(Func<bool> done, int frames = 60)
        {
            for (var i = 0; i < frames && !done(); i++)
            {
                Console.RunFrameSilently();
            }

            done().Should().BeTrue($"EE: \"{Ee}\", IOP: \"{Iop}\"");
            Console.RunFrameSilently();
        }
    }

    /// <summary>A program that prints argc and its arguments on the EE's UART: "3 host:args.elf one two\n".</summary>
    private static byte[] ArgumentsElf()
    {
        var a = new MipsAssembler(0x8010_0000);
        a.Move(S0, A0);
        a.Move(S1, A1);
        a.Li(S2, 0xB000_F180);
        a.Addiu(T0, S0, '0');
        a.Sb(T0, S2, 0);
        a.Label("next");
        a.Blez(S0, "done");
        a.Lw(T1, S1, 0);
        a.Addiu(T0, Zero, ' ');
        a.Sb(T0, S2, 0);
        a.Label("char");
        a.Lbu(T0, T1, 0);
        a.Beq(T0, Zero, "end");
        a.Sb(T0, S2, 0);
        a.Addiu(T1, T1, 1);
        a.B("char");
        a.Label("end");
        a.Addiu(S1, S1, 4);
        a.Addiu(S0, S0, -1);
        a.B("next");
        a.Label("done");
        a.Addiu(T0, Zero, '\n');
        a.Sb(T0, S2, 0);
        a.Halt();
        return Ps2ConsoleTests.MakeElf(a.Assemble());
    }

    /// <summary>
    /// An ISO 9660 image: the volume descriptors at 16 and 17, the root directory at 18 with SYSTEM.CNF (19) and, when
    /// given, the boot ELF (from 20), then numbered sectors up to 64.
    /// </summary>
    private static byte[] Iso(byte[]? elf, string? systemCnf)
    {
        const int sectors = 64;
        var image = new byte[sectors * 2048];
        for (var sector = 18; sector < sectors; sector++)
        {
            for (var i = 0; i < 2048; i++)
            {
                image[(sector * 2048) + i] = (byte)(sector * 7 + i);
            }
        }

        var pvd = image.AsSpan(16 * 2048, 2048);
        pvd.Clear();
        pvd[0] = 1;
        "CD001"u8.CopyTo(pvd[1..]);
        pvd[6] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(pvd[80..], sectors);
        DirectoryRecord(pvd[156..], 18, 2048, "\0", directory: true);
        var terminator = image.AsSpan(17 * 2048, 2048);
        terminator.Clear();
        terminator[0] = 0xFF;
        "CD001"u8.CopyTo(terminator[1..]);

        var root = image.AsSpan(18 * 2048, 2048);
        root.Clear();
        var at = DirectoryRecord(root, 18, 2048, "\0", directory: true);
        at += DirectoryRecord(root[at..], 18, 2048, "\u0001", directory: true);
        if (systemCnf is not null)
        {
            var text = Encoding.ASCII.GetBytes(systemCnf);
            image.AsSpan(19 * 2048, 2048).Clear();
            text.CopyTo(image, 19 * 2048);
            at += DirectoryRecord(root[at..], 19, text.Length, "SYSTEM.CNF;1");
        }

        if (elf is not null)
        {
            image.AsSpan(20 * 2048, (elf.Length + 2047) / 2048 * 2048).Clear();
            elf.CopyTo(image, 20 * 2048);
            DirectoryRecord(root[at..], 20, elf.Length, "SLUS_000.01;1");
        }

        return image;
    }

    private static int DirectoryRecord(Span<byte> record, int lba, int size, string name, bool directory = false)
    {
        var length = 33 + name.Length + ((name.Length & 1) == 0 ? 1 : 0);
        record[0] = (byte)length;
        BinaryPrimitives.WriteInt32LittleEndian(record[2..], lba);
        BinaryPrimitives.WriteInt32BigEndian(record[6..], lba);
        BinaryPrimitives.WriteInt32LittleEndian(record[10..], size);
        BinaryPrimitives.WriteInt32BigEndian(record[14..], size);
        record[25] = (byte)(directory ? 2 : 0);
        record[32] = (byte)name.Length;
        Encoding.ASCII.GetBytes(name).CopyTo(record[33..]);
        return length;
    }

    private static uint Word(byte[] memory, uint address) => BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan((int)address));

    private static ushort Bgr555(uint abgr) =>
        (ushort)(((abgr & 0xFF) >> 3) | ((((abgr >> 8) & 0xFF) >> 3) << 5) | ((((abgr >> 16) & 0xFF) >> 3) << 10));

    [Fact]
    public void A_disc_boots_with_both_processors_running_the_bios_and_talking_through_the_sif()
    {
        var disc = new MemoryPs2Disc(Iso(null, null), Ps2DiscType.Cd);
        var m = new Machine(Ps2Boot.FromDisc(disc), readDisc: true);
        var console = m.Console;

        console.BootsThroughBios.Should().BeTrue();
        console.Kernel.Should().BeNull("the BIOS's own kernel runs, not GameVault's");
        console.IopServices.Should().BeNull();
        console.Cpu.Pc.Should().Be(0xBFC0_0000, "the EE starts at the reset vector");
        console.Iop.Cpu.Pc.Should().Be(0xBFC0_0000, "and so does the IOP");
        console.Iop.Halted.Should().BeFalse();

        m.RunUntil(() => m.Ee.ToString().Contains("EELOAD"));

        m.Ee.ToString().Should().Be("EE\nEE done\nEELOAD\n", "the EE's UART, then the BIOS's EELOAD runs (no fast boot)");
        m.Iop.ToString().Should().Be("IOP\nIOP read the disc\nIOP done\n", "the IOP's TTY at $1F80380C");

        Word(console.Rdram, 0x10000).Should().Be(StandInBios.IopGreeting, "the EE read SMCOM after the IOP set SMFLG");
        Word(console.Iop.Ram, 0x10000).Should().Be(StandInBios.EeGreeting, "the IOP read MSCOM after the EE set MSFLG");
        console.Sif.Smflg.Should().Be(0x40000u, "the EE cleared $10000; the IOP's done flag stays");
        console.Sif.Msflg.Should().Be(0u, "the IOP cleared it");

        for (uint i = 0; i < 8; i++)
        {
            Word(console.Iop.Ram, 0x30000 + (i * 4)).Should().Be(StandInBios.Sif1Data[i], "SIF1: DMAC channel 6 → IOP channel 10");
        }

        for (uint i = 0; i < 4; i++)
        {
            Word(console.Rdram, 0x30000 + (i * 4)).Should().Be(~StandInBios.Sif1Data[i], "SIF0: IOP channel 9 → DMAC channel 5");
        }

        var sector = new byte[2048];
        disc.ReadSector(StandInBios.CdSector, sector);
        console.Iop.Ram.AsSpan(0x60000, 2048).ToArray().Should().Equal(sector, "ReadCd by DMA channel 3");

        console.ScreenWidth.Should().Be(640);
        console.ScreenHeight.Should().Be(448);
        var colour = Bgr555(StandInBios.SpriteColour);
        console.FrameBuffer[0].Should().Be(colour, "the EE drew the sprite through PATH3");
        console.FrameBuffer[(447 * 640) + 639].Should().Be(colour);
    }

    [Fact]
    public void An_elf_with_a_bios_boots_through_it_and_starts_at_eeload_with_its_arguments()
    {
        var m = new Machine(Ps2Boot.FromElf(ArgumentsElf(), "host:args.elf", "one", "two"));
        m.Console.Kernel.Should().BeNull();
        m.Console.BootsThroughBios.Should().BeTrue();

        m.RunUntil(() => m.Ee.ToString().EndsWith("two\n", StringComparison.Ordinal));

        m.Ee.ToString().Should().Be("EE\nEE done\n3 host:args.elf one two\n", "the ELF ran in EELOAD's place, through ExecPS2");
        m.Iop.ToString().Should().Be("IOP\nIOP done\n", "no disc to read");
        m.Console.Cpu.AtBreakpoint.Should().BeFalse();
    }

    [Fact]
    public void An_elf_can_still_run_on_gamevaults_kernel_when_a_bios_is_given()
    {
        var console = new Ps2Console(Ps2Boot.FromElf(ArgumentsElf()), Bios, null, new Ps2ConsoleOptions { ElfThroughBios = false });
        console.BootsThroughBios.Should().BeFalse();
        console.Kernel.Should().NotBeNull();
        console.Iop.Halted.Should().BeTrue();
        console.Cpu.Pc.Should().NotBe(0xBFC0_0000u);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fast_boot_starts_the_discs_boot2_executable_at_eeload(bool fastBoot)
    {
        var iso = Iso(ArgumentsElf(), $"BOOT2 = {BootFile}\r\nVER = 1.00\r\nVMODE = NTSC\r\n");
        var disc = new MemoryPs2Disc(iso, Ps2DiscType.Dvd);
        Ps2DiscFiles.ReadBoot2(disc).Should().Be(BootFile);
        Ps2DiscFiles.ReadFile(disc, BootFile).Should().Equal(ArgumentsElf());

        var m = new Machine(Ps2Boot.FromDisc(disc), new Ps2ConsoleOptions { FastBoot = fastBoot });
        m.RunUntil(() => m.Ee.ToString().Contains("EE done\n") && m.Ee.ToString().EndsWith("\n", StringComparison.Ordinal) &&
                         m.Ee.ToString() != "EE\nEE done\n");

        m.Ee.ToString().Should().Be(fastBoot ? $"EE\nEE done\n1 {BootFile}\n" : "EE\nEE done\nEELOAD\n");
    }

    [Fact]
    public void A_disc_without_a_boot2_line_boots_normally_with_fast_boot_on()
    {
        var disc = new MemoryPs2Disc(Iso(ArgumentsElf(), "BOOT = cdrom:\\SLUS_000.01;1\r\n"), Ps2DiscType.Dvd);
        var m = new Machine(Ps2Boot.FromDisc(disc), new Ps2ConsoleOptions { FastBoot = true });
        m.RunUntil(() => m.Ee.ToString().Contains("EELOAD"));
        m.Ee.ToString().Should().Be("EE\nEE done\nEELOAD\n");
    }

    [Fact]
    public void A_save_state_before_eeload_keeps_the_fast_boot_hook()
    {
        var elf = ArgumentsElf();
        var a = new Machine(Ps2Boot.FromElf(elf, "host:args.elf", "one", "two"));
        a.Console.RunCycles(3_000);
        a.Ee.ToString().Should().NotContain("EE done");
        var state = a.Console.SaveSnapshot();

        var b = new Machine(Ps2Boot.FromElf(elf, "host:args.elf", "one", "two"));
        b.Console.LoadSnapshot(state);
        b.Ee.Append(a.Ee);
        b.Iop.Append(a.Iop);

        a.RunUntil(() => a.Ee.ToString().EndsWith("two\n", StringComparison.Ordinal));
        b.RunUntil(() => b.Ee.ToString().EndsWith("two\n", StringComparison.Ordinal));
        b.Ee.ToString().Should().Be(a.Ee.ToString());
        b.Iop.ToString().Should().Be(a.Iop.ToString());
        b.Console.Cpu.Cycles.Should().Be(a.Console.Cpu.Cycles);
        b.Console.Rdram.AsSpan().SequenceEqual(a.Console.Rdram).Should().BeTrue();
    }
}
