using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.Cartridge;
using GameVault.Core.GameCube;
using GameVault.Core.Input;
using GameVault.Core.Tests.Cpu;

namespace GameVault.Core.Tests.GameCube;

/// <summary>The GameCube (phase-18.md, milestone 1), with DOL programs assembled here.</summary>
public sealed class GameCubeConsoleTests
{
    /// <summary>A DOL with one text section of <paramref name="code"/> at $80003100 (its entry point).</summary>
    internal static byte[] Dol(params uint[] code)
    {
        var dol = new byte[0x100 + code.Length * 4];
        BinaryPrimitives.WriteUInt32BigEndian(dol, 0x100); // text 0's offset
        BinaryPrimitives.WriteUInt32BigEndian(dol.AsSpan(0x48), 0x80003100); // its address
        BinaryPrimitives.WriteUInt32BigEndian(dol.AsSpan(0x90), (uint)code.Length * 4); // its size
        BinaryPrimitives.WriteUInt32BigEndian(dol.AsSpan(0xE0), 0x80003100); // entry
        for (var i = 0; i < code.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dol.AsSpan(0x100 + i * 4), code[i]);
        }

        return dol;
    }

    private static GameCubeConsole Console(byte[] program)
    {
        var console = new GameCubeConsole(program);
        console.Power();
        return console;
    }

    /// <summary>r = (high << 16) | low, with lis and ori.</summary>
    private static uint[] Load(int register, uint value) =>
        [Ppc.Addis(register, 0, (int)(value >> 16)), Ppc.Ori(register, register, (int)(value & 0xFFFF))];

    private static uint[] Code(params uint[][] parts) => [.. parts.SelectMany(p => p)];

    [Fact]
    public void Files_that_arent_gamecube_programs_are_refused()
    {
        var act = () => new GameCubeConsole(new byte[0x200]);

        act.Should().Throw<RomFormatException>().WithMessage("*GameCube program*");
        GcExecutable.IsDol(Dol(Ppc.B(0))).Should().BeTrue();
    }

    [Fact]
    public void A_dol_boots_with_the_os_globals_and_its_xfb_shows()
    {
        // Two white pixels (Y 235, U and V 128) at the top left of an XFB at $00100000, then VI pointed at it.
        var program = Code(
            Load(3, 0x80100000),
            Load(4, 0xEB80EB80),
            [Ppc.Stw(4, 3, 0)],
            Load(5, 0xCC002000),
            Load(6, 0x10008000), // TFBL: POFF, $00100000 >> 5
            [Ppc.Stw(6, 5, 0x1C), Ppc.Addi(7, 0, 1), Ppc.Sth(7, 5, 2)], // DCR: enable
            [Ppc.B(0)]);
        var console = Console(Dol(program));

        console.RunFrame();

        BinaryPrimitives.ReadUInt32BigEndian(console.Ram[0x28..]).Should().Be((uint)GameCubeConsole.RamSize);
        BinaryPrimitives.ReadUInt32BigEndian(console.Ram[0x20..]).Should().Be(0x0D15EA5E);
        console.TrueColorFrame[0].Should().Be(0xFFFFFFFF);
        console.TrueColorFrame[1].Should().Be(0xFFFFFFFF);
        console.TrueColorFrame[2].Should().NotBe(0xFFFFFFFF);
        console.ScreenWidth.Should().Be(640);
        console.ScreenHeight.Should().Be(480);
    }

    [Fact]
    public void The_display_interrupt_reaches_the_cpu_through_the_processor_interface()
    {
        // The external exception handler at $500 (translation off): count, re-arm DI0, return.
        uint[] handler = Code(
            [Ppc.Lwz(9, 0, 0x4000), Ppc.Addi(9, 9, 1), Ppc.Stw(9, 0, 0x4000)],
            Load(10, 0x0C002000),
            Load(11, 0x10640000), // DI0: enabled, line 100, its flag clear
            [Ppc.Stw(11, 10, 0x30), Ppc.Rfi()]);
        var program = Code(
            Load(3, 0xCC002000),
            Load(4, 0x10640000),
            [Ppc.Stw(4, 3, 0x30)], // DI0
            Load(5, 0xCC003000),
            [Ppc.Addi(6, 0, 0x100), Ppc.Stw(6, 5, 4)], // PI mask: VI
            [Ppc.Mfmsr(7), Ppc.Ori(7, 7, 0x8000), Ppc.Mtmsr(7)], // EE
            [Ppc.B(0)]);
        var console = Console(Dol(program));
        for (var i = 0; i < handler.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(console.Ram[(0x500 + i * 4)..], handler[i]);
        }

        for (var i = 0; i < 3; i++)
        {
            console.RunFrame();
        }

        BinaryPrimitives.ReadUInt32BigEndian(console.Ram[0x4000..]).Should().Be(3);
    }

    [Fact]
    public void The_serial_interface_answers_identify_and_polls_the_pad()
    {
        var console = Console(Dol(Ppc.B(0)));
        console.SetControllerState(0, ControllerState.B | ControllerState.Start | ControllerState.None.WithStick(100, -100));

        console.Write32(0x0C006480, 0x00000000); // command: identify
        console.Write32(0x0C006434, 0x80000001 | (1u << 16) | (3u << 8)); // COMCSR: clear TCINT, channel 0, start
        console.Read32(0x0C006480).Should().Be(0x09000000, "a standard controller");
        (console.Read32(0x0C006434) & 0x80000000).Should().NotBe(0, "TCINT");

        var high = console.Read32(0x0C006404);
        (high >> 16).Should().Be((uint)(GcPadState.A | GcPadState.Start));
        ((high >> 8) & 0xFF).Should().Be(0x80 + 100);
        (high & 0xFF).Should().Be(0x80 - 100);
        console.Read32(0x0C006410).Should().Be(0x80000000, "nothing on port 2");
    }

    [Fact]
    public void Exi_reads_the_sram_with_its_checksums()
    {
        var console = Console(Dol(Ppc.B(0)));

        console.Write32(0x0C006800, 1u << 8); // channel 0, device 1 (RTC/SRAM)
        console.Write32(0x0C006810, 0x20000100); // the command: read SRAM
        console.Write32(0x0C00680C, 0x01 | (1 << 2) | (3 << 4)); // immediate, write, four bytes
        console.Write32(0x0C006804, 0x5000);
        console.Write32(0x0C006808, 64);
        console.Write32(0x0C00680C, 0x01 | 0x02); // DMA read

        console.Ram.Slice(0x5000, 64).ToArray().Should().Equal(console.Exi.Sram.ToArray());
        var sum = 0;
        var inverse = 0;
        for (var i = 0x0C; i < 0x14; i += 2)
        {
            var word = BinaryPrimitives.ReadUInt16BigEndian(console.Ram[(0x5000 + i)..]);
            sum += word;
            inverse += (ushort)~word;
        }

        BinaryPrimitives.ReadUInt16BigEndian(console.Ram[0x5000..]).Should().Be((ushort)sum);
        BinaryPrimitives.ReadUInt16BigEndian(console.Ram[0x5002..]).Should().Be((ushort)inverse);
        (console.Read32(0x0C006800) & 0x08).Should().Be(0x08, "TCINT");
    }

    [Fact]
    public void The_dsp_answers_the_boot_handshake_and_aram_dma_runs()
    {
        var console = Console(Dol(Ppc.B(0)));
        console.Ram[0x6000] = 0x5A;

        console.Write16(0x0C00500A, 0x0004 | 0x0001); // halted, reset
        (console.Read16(0x0C00500A) & 0x0001).Should().Be(0, "RES finishes at once");
        console.Write32(0x0C005020, 0x00006000); // ARAM DMA: from main memory $6000
        console.Write32(0x0C005024, 0x00000000); // to ARAM 0
        console.Write32(0x0C005028, 0x00000020); // 32 bytes
        (console.Read16(0x0C00500A) & 0x0020).Should().Be(0x0020, "ARINT");
        console.Dsp.Aram[0].Should().Be(0x5A);

        console.Write16(0x0C00500A, 0x0000); // out of halt
        (console.Read16(0x0C005004) & 0x8000).Should().Be(0x8000, "the DSP has mail for the CPU");
        console.Read16(0x0C005004).Should().Be(0x8071);
        console.Read16(0x0C005006).Should().Be(0xFEED);
        (console.Read16(0x0C005004) & 0x8000).Should().Be(0, "reading the low half took it");
    }

    [Fact]
    public void An_elf_loads_its_program_headers()
    {
        var elf = new byte[0x100];
        elf[0] = 0x7F;
        "ELF"u8.CopyTo(elf.AsSpan(1));
        elf[4] = 1;
        elf[5] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x12), 20); // PowerPC
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x18), 0x80004000); // entry
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x1C), 0x34); // program headers
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x2A), 0x20);
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x2C), 1);
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x34), 1); // PT_LOAD
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x38), 0x80); // offset
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x3C), 0x80004000); // address
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x44), 8); // file size
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x48), 16); // memory size
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x80), Ppc.Addi(3, 0, 42));
        BinaryPrimitives.WriteUInt32BigEndian(elf.AsSpan(0x84), Ppc.B(0));

        var console = Console(elf);
        console.RunFrame();

        console.Cpu.GetGpr(3).Should().Be(42);
        console.Cpu.Pc.Should().Be(0x80004004);
    }

    [Fact]
    public void A_save_state_round_trips()
    {
        var program = Code([Ppc.Addi(3, 3, 1), Ppc.Stw(3, 0, 0x7000), Ppc.B(-8)]);
        var console = Console(Dol(program));
        console.RunFrame();

        var state = console.SaveSnapshot();
        console.RunFrame();
        var count = console.Cpu.GetGpr(3);

        console.LoadSnapshot(state);
        console.RunFrame();

        console.Cpu.GetGpr(3).Should().Be(count);
    }
}
