using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using FluentAssertions;
using GameVault.Core.PlayStation2;
using GameVault.Core.Tests.Harness;
using GameVault.Core.Tests.PlayStation2.Homebrew;
using Xunit.Abstractions;

namespace GameVault.Core.Tests.PlayStation2.Console;

/// <summary>The PS2 machine: its memory map, the ELF loader, boots and save states.</summary>
public sealed class Ps2ConsoleTests(ITestOutputHelper log)
{
    /// <summary>A tiny kernel-mode ELF: the code at $80100000 (physical $100000), with a BSS.</summary>
    internal static byte[] MakeElf(uint[] code, uint bss = 0x100, uint entry = 0x8010_0000, uint paddr = 0x0010_0000)
    {
        var elf = new byte[0x100 + code.Length * 4];
        var s = elf.AsSpan();
        s[0] = 0x7F;
        s[1] = (byte)'E';
        s[2] = (byte)'L';
        s[3] = (byte)'F';
        s[4] = 1; // 32-bit
        s[5] = 1; // little-endian
        s[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(s[16..], 2); // ET_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(s[18..], 8); // EM_MIPS
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], entry);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], 52); // e_phoff
        BinaryPrimitives.WriteUInt16LittleEndian(s[42..], 32); // e_phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(s[44..], 1); // e_phnum
        var ph = s[52..];
        BinaryPrimitives.WriteUInt32LittleEndian(ph, 1); // PT_LOAD
        BinaryPrimitives.WriteUInt32LittleEndian(ph[4..], 0x100);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[8..], entry);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[12..], paddr);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[16..], (uint)code.Length * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[20..], (uint)code.Length * 4 + bss);
        for (var i = 0; i < code.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[(0x100 + i * 4)..], code[i]);
        }

        return elf;
    }

    // MIPS encodings for the small programs below.
    private static uint Lui(int rt, int imm) => 0x3C00_0000u | ((uint)rt << 16) | (uint)(ushort)imm;

    private static uint Ori(int rt, int rs, int imm) => 0x3400_0000u | ((uint)rs << 21) | ((uint)rt << 16) | (uint)(ushort)imm;

    private static uint Sb(int rt, int rs, int offset) => 0xA000_0000u | ((uint)rs << 21) | ((uint)rt << 16) | (uint)(ushort)offset;

    private static uint Sw(int rt, int rs, int offset) => 0xAC00_0000u | ((uint)rs << 21) | ((uint)rt << 16) | (uint)(ushort)offset;

    private static uint Lw(int rt, int rs, int offset) => 0x8C00_0000u | ((uint)rs << 21) | ((uint)rt << 16) | (uint)(ushort)offset;

    private const uint Loop = 0x1000_FFFF; // b .
    private const uint Nop = 0;

    [Fact]
    public void The_elf_loader_copies_segments_by_physical_address_and_clears_the_bss()
    {
        var elf = Ps2Elf.Parse(MakeElf([0x1111_1111, 0x2222_2222], bss: 0x10));
        elf.Entry.Should().Be(0x8010_0000);
        elf.PhysicalRange.Should().Be((0x10_0000u, 0x10_0018u));
        var ram = new byte[0x20_0000];
        ram.AsSpan(0x10_0000, 0x18).Fill(0xEE);
        elf.Load(ram);
        BinaryPrimitives.ReadUInt32LittleEndian(ram.AsSpan(0x10_0004)).Should().Be(0x2222_2222);
        ram.AsSpan(0x10_0008, 0x10).ToArray().Should().OnlyContain(b => b == 0);

        // Without p_paddr the virtual address's segment bits are dropped.
        var noPaddr = Ps2Elf.Parse(MakeElf([0x3333_3333], paddr: 0));
        noPaddr.PhysicalRange.Start.Should().Be(0x10_0000);
        FluentActions.Invoking(() => Ps2Elf.Parse([1, 2, 3])).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void The_debug_uart_prints_and_unmapped_addresses_read_zero()
    {
        // KSEG1: write "Hi" to KPUTCHAR, read an unmapped register into the status word, spin.
        uint[] code =
        [
            Lui(8, 0xB000), Ori(8, 8, 0xF180),
            Ori(9, 0, 'H'), Sb(9, 8, 0),
            Ori(9, 0, 'i'), Sb(9, 8, 0),
            Lui(10, 0xB000), Lw(11, 10, 0x7F00), // $10007F00: nothing there
            Lui(12, 0xA01F), Ori(12, 12, 0xFF00), Sw(11, 12, 0),
            Loop, Nop,
        ];
        var text = new StringBuilder();
        var console = new Ps2Console(Ps2Boot.FromElf(MakeElf(code))) { DebugOutput = c => text.Append(c) };
        console.RunFrame();
        text.ToString().Should().Be("Hi");
        BinaryPrimitives.ReadUInt32LittleEndian(console.Rdram.AsSpan(0x1F_FF00)).Should().Be(0);
        console.Kernel!.Fault.Should().BeNull();
        console.ScreenWidth.Should().Be(640);
    }

    [Fact]
    public void The_memory_map_reaches_vu_memory_the_gs_and_the_iop_ram()
    {
        var console = new Ps2Console(Ps2Boot.FromElf(MakeElf([Loop, Nop])));
        console.Write32(0x1100_4010, 0xDEAD_BEEF);
        console.Vu0.DataMemory[0x10].Should().Be(0xEF);
        console.Write32(0x1100_C000, 0x1234_5678);
        BinaryPrimitives.ReadUInt32LittleEndian(console.Vu1.DataMemory).Should().Be(0x1234_5678);
        console.Write32(0x1C00_0100, 0xCAFE_F00D);
        BinaryPrimitives.ReadUInt32LittleEndian(console.Iop.Ram.AsSpan(0x100)).Should().Be(0xCAFE_F00D);
        console.Write64(0x1200_00E0, 0x00FF_8040); // BGCOLOR
        console.Read64(0x1200_00E0).Should().Be(0x00FF_8040);
        console.Read32(0x1000_F230).Should().Be(Ps2Sif.FlagDmaReady | Ps2Sif.FlagCmdReady | Ps2Sif.FlagIopBooted, "GameVault's IOP has booted");
        console.Read32(0x1234_5678).Should().Be(0);
        console.PeekMemory(Ps2Console.ScratchpadPeekAddress).Should().Be(0);
        console.Cpu.Scratchpad[0] = 0x5A;
        console.PeekMemory(Ps2Console.ScratchpadPeekAddress).Should().Be(0x5A);
    }

    [Fact]
    public void An_alarm_calls_its_handler_after_the_h_blanks_asked_for()
    {
        // SetAlarm(100, handler, 0x77); interrupts on; spin. The handler stores its arg and id in the status block.
        const int handler = 15 * 4;
        uint[] code =
        [
            0x2403_0018, // li v1, SetAlarm
            0x2404_0064, // li a0, 100 H-blanks
            Lui(5, 0x8010), Ori(5, 5, handler),
            0x2406_0077, // li a2, 0x77
            0x0000_000C, // syscall
            0x4008_6000, // mfc0 t0, Status
            0x3508_0001, // ori t0, IE
            0x4088_6000, // mtc0 t0, Status
            Lui(13, 0x8000), Ori(13, 13, 0x0400), // t5: a counter in kernel RAM
            Lw(14, 13, 0), 0x25CE_0001 /* addiu t6, t6, 1 */, Sw(14, 13, 0),
            0x1000_FFFC, // b back to the counter
            Lui(9, 0xA01F), Ori(9, 9, 0xFF00), Sw(6, 9, 0), Sw(4, 9, 4), 0x03E0_0008 /* jr ra */, Nop,
        ];
        code[15].Should().Be(Lui(9, 0xA01F), "the handler's offset");
        var console = new Ps2Console(Ps2Boot.FromElf(MakeElf(code)));
        console.RunCycles(90L * 18743); // 90 lines: not yet
        BinaryPrimitives.ReadUInt32LittleEndian(console.Rdram.AsSpan(0x1F_FF00)).Should().Be(0);
        console.RunCycles(20L * 18743);
        BinaryPrimitives.ReadUInt32LittleEndian(console.Rdram.AsSpan(0x1F_FF00)).Should().Be(0x77);
        BinaryPrimitives.ReadUInt32LittleEndian(console.Rdram.AsSpan(0x1F_FF04)).Should().Be(1, "the alarm's id");
        var counter = BinaryPrimitives.ReadUInt32LittleEndian(console.Rdram.AsSpan(0x400));
        console.RunCycles(10_000);
        BinaryPrimitives.ReadUInt32LittleEndian(console.Rdram.AsSpan(0x400)).Should().BeGreaterThan(counter, "the program carries on after the handler");
        console.Kernel!.Fault.Should().BeNull();
    }

    [Fact]
    public void A_disc_needs_a_bios()
    {
        var disc = new MemoryPs2Disc(new byte[IPs2Disc.SectorSize * 32]);
        FluentActions.Invoking(() => new Ps2Console(Ps2Boot.FromDisc(disc))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Frames_follow_the_video_timing()
    {
        var console = new Ps2Console(Ps2Boot.FromElf(MakeElf([Loop, Nop])));
        console.RunFrame();
        var start = console.Cpu.Cycles;
        console.RunFrame();
        console.RunFrame();
        var perField = (console.Cpu.Cycles - start) / 2.0;
        (294_912_000 / perField).Should().BeApproximately(59.94, 0.05, "NTSC fields");
    }

    /// <summary>
    /// A save state mid-frame, loaded into a new console, continues exactly as the uninterrupted run: kernel threads,
    /// semaphores and interrupt handlers (intc_order runs on the V-blank interrupt), the SIF traffic and the GS.
    /// </summary>
    [TestRomTheory("ps2autotests/kernel/ee/interrupt/intc_order.elf")]
    [InlineData("kernel/ee/interrupt/intc_order", 3, 123_456)]
    [InlineData("dma/vif/stcycl", 0, 4_000_000)]
    public void A_save_state_continues_exactly(string test, int framesBefore, long cyclesBefore)
    {
        var elf = TestRomLocator.ReadCached("ps2autotests/" + test + ".elf");
        var outputA = new StringBuilder();
        var a = new Ps2Console(Ps2Boot.FromElf(elf, "host:test.elf")) { DebugOutput = c => outputA.Append(c) };
        for (var i = 0; i < framesBefore; i++)
        {
            a.RunFrame();
        }

        a.RunCycles(cyclesBefore);
        var saved = a.SaveSnapshot();
        var printedBefore = outputA.Length;
        for (var i = 0; i < 8; i++)
        {
            a.RunFrame();
        }

        var outputB = new StringBuilder();
        var b = new Ps2Console(Ps2Boot.FromElf(elf, "host:test.elf")) { DebugOutput = c => outputB.Append(c) };
        b.LoadSnapshot(saved);
        for (var i = 0; i < 8; i++)
        {
            b.RunFrame();
        }

        outputB.ToString().Should().Be(outputA.ToString()[printedBefore..]);
        b.Cpu.Cycles.Should().Be(a.Cpu.Cycles);
        b.FrameBuffer.SequenceEqual(a.FrameBuffer).Should().BeTrue();
        b.SaveSnapshot().AsSpan().SequenceEqual(a.SaveSnapshot()).Should().BeTrue("the loaded machine is the saved one");
    }

    [Fact]
    public void A_save_state_of_the_homebrew_mid_drawing_continues_exactly_and_its_size_and_speed_are_measured()
    {
        var path = Ps2HomebrewRunner.ElfPath("vu1-path1");
        var elf = File.ReadAllBytes(path);
        var a = new Ps2Console(Ps2Boot.FromElf(elf));
        a.RunCycles(300_000);
        var watch = Stopwatch.StartNew();
        var saved = a.SaveSnapshot();
        var saveTime = watch.Elapsed;
        for (var i = 0; i < 6; i++)
        {
            a.RunFrame();
        }

        var b = new Ps2Console(Ps2Boot.FromElf(elf));
        watch.Restart();
        b.LoadSnapshot(saved);
        var loadTime = watch.Elapsed;
        for (var i = 0; i < 6; i++)
        {
            b.RunFrame();
        }

        log.WriteLine($"PS2 save state: {saved.Length / 1048576.0:F1} MB, save {saveTime.TotalMilliseconds:F0} ms, load {loadTime.TotalMilliseconds:F0} ms");
        b.FrameBuffer.SequenceEqual(a.FrameBuffer).Should().BeTrue();
        b.SaveSnapshot().AsSpan().SequenceEqual(a.SaveSnapshot()).Should().BeTrue();
        Ps2HomebrewRunner.Word(b, 0).Should().Be(Ps2HomebrewRunner.Signature);
        saved.Length.Should().BeLessThan(64 * 1024 * 1024);
        FluentActions.Invoking(() => new Ps2Console(Ps2Boot.FromElf(File.ReadAllBytes(Ps2HomebrewRunner.ElfPath("gs-basic")))).LoadSnapshot(saved))
            .Should().Throw<GameVault.Core.State.StateFormatException>("a state belongs to its program");
    }
}
