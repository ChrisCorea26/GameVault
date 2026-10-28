using GameVault.Core.Snes;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Snes;

/// <summary>The Cx4's HG51B169 (snes-chips-3.md §3), on programs assembled here and run from the cartridge ROM.</summary>
public sealed class Cx4Tests
{
    // ---- HG51B instructions ----

    private static ushort LdA(int imm) => (ushort)(0x6400 | imm);
    private static ushort LdAReg(int reg) => (ushort)(0x6000 | reg);
    private static ushort LdMar(int imm) => (ushort)(0x6600 | imm);
    private static ushort LdP(int imm) => (ushort)(0x6700 | imm);
    private static ushort StA(int reg) => (ushort)(0xE000 | reg);
    private static ushort Add(int imm, int shift = 0) => (ushort)(0x8400 | shift << 8 | imm);
    private static ushort Sub(int imm, int shift = 0) => (ushort)(0x9400 | shift << 8 | imm);
    private static ushort Cmp(int imm) => (ushort)(0x5400 | imm);
    private static ushort Mul(int imm) => (ushort)(0x9C00 | imm);
    private static ushort Shl(int imm) => (ushort)(0xDC00 | imm);
    private static ushort Shr(int imm) => (ushort)(0xC400 | imm);
    private static ushort Asr(int imm) => (ushort)(0xCC00 | imm);
    private static ushort Ror(int imm) => (ushort)(0xD400 | imm);
    private static ushort Xor(int imm) => (ushort)(0xAC00 | imm);
    private static ushort Jmp(int to) => (ushort)(0x0800 | to);
    private static ushort JmpEq(int to) => (ushort)(0x0C00 | to);
    private static ushort Jsr(int to) => (ushort)(0x2800 | to);
    private static ushort RdRom(int imm) => (ushort)(0x7400 | imm);
    private static ushort WrRam(int imm) => (ushort)(0xEC00 | imm);
    private static ushort RdRam(int imm) => (ushort)(0x6C00 | imm);
    private const ushort Sxb = 0x5900, IncMar = 0x4000, Rts = 0x3C00, Halt = 0xFC00, SkipZ = 0x2601, SwapR0 = 0xF000, Nop = 0x0000;
    private const int R = 0x60, RegMulLow = 0x02, RegRom = 0x08, RegRam = 0x0C, RegMar = 0x13;

    private const uint ProgramAddress = 0x009000; // ROM offset $1000 in LoROM

    private static byte[] DataRom()
    {
        var data = new byte[Cx4.DataRomSize];
        for (var i = 0; i < 1024; i++)
        {
            var word = 0x010203 * i & 0xFFFFFF;
            data[i * 3] = (byte)word;
            data[i * 3 + 1] = (byte)(word >> 8);
            data[i * 3 + 2] = (byte)(word >> 16);
        }

        return data;
    }

    private static byte[] Rom(params ushort[] program)
    {
        var rom = SnesTests.Rom([0x80, 0xFE], chip: 0xF3, size: 0x20000);
        for (var i = 0; i < program.Length; i++)
        {
            rom[0x1000 + i * 2] = (byte)program[i];
            rom[0x1000 + i * 2 + 1] = (byte)(program[i] >> 8);
        }

        for (var i = 0; i < 256; i++)
        {
            rom[0x2000 + i] = (byte)(i * 7 + 1); // data for the DMA test, at $00A000
        }

        return rom;
    }

    private long _clock;

    private Cx4 Chip(params ushort[] program)
    {
        var cartridge = new SnesCartridge(Rom(program), name => name == "cx4.rom" ? DataRom() : null);
        var cx4 = cartridge.Coprocessor.Should().BeOfType<Cx4>().Subject;
        cx4.Power();
        return cx4;
    }

    private void Run(Cx4 cx4, long masterClocks = 200_000)
    {
        _clock += masterClocks;
        cx4.CatchUp(_clock);
    }

    /// <summary>Points the cache at the program and starts the chip at bank 0, address 0.</summary>
    private void Start(Cx4 cx4)
    {
        cx4.Write(0x007F49, unchecked((byte)ProgramAddress));
        cx4.Write(0x007F4A, (byte)(ProgramAddress >> 8));
        cx4.Write(0x007F4B, (byte)(ProgramAddress >> 16));
        cx4.Write(0x007F4D, 0);
        cx4.Write(0x007F4E, 0);
        cx4.Write(0x007F4F, 0);
        Run(cx4);
    }

    [Fact]
    public void Without_its_data_rom_the_game_says_where_to_put_it()
    {
        var act = () => new SnesCartridge(Rom(Halt));

        act.Should().Throw<MissingChipRomException>().Which.FileNames.Should().Equal("cx4.rom");
    }

    [Fact]
    public void Loads_shifts_adds_and_subtracts_with_flags()
    {
        var cx4 = Chip(
            LdA(0x7F), Shl(16),          // $7F0000
            Add(0x12),                   // $7F0012
            StA(R + 0),
            Sub(0x13),                   // $7EFFFF
            StA(R + 1),
            LdA(0x80), Shl(16), Asr(4),  // $F80000
            StA(R + 2),
            Ror(4),                      // $0F8000
            StA(R + 3),
            Shr(15),                     // $00001F
            Xor(0x1F),                   // 0: Z set
            StA(R + 4),
            Halt);
        Start(cx4);

        cx4.Halted.Should().BeTrue();
        cx4.Register(0).Should().Be(0x7F0012);
        cx4.Register(1).Should().Be(0x7EFFFF);
        cx4.Register(2).Should().Be(0xF80000, "ASR keeps the sign");
        cx4.Register(3).Should().Be(0x0F8000, "ROR brings the low bits round to the top");
        cx4.Register(4).Should().Be(0);
        cx4.Read(0x007F80 + 3, 0).Should().Be(0xFF, "the CPU reads the registers three bytes each");
        cx4.Read(0x007F80 + 5, 0).Should().Be(0x7E);
    }

    [Fact]
    public void Multiplies_signed_into_48_bits()
    {
        var cx4 = Chip(LdA(0xFF), Sxb, Mul(3), LdAReg(RegMulLow), StA(R + 0), LdAReg(0x01), StA(R + 1), Halt);
        Start(cx4);

        cx4.Register(0).Should().Be(0xFFFFFD, "-1 × 3, low half");
        cx4.Register(1).Should().Be(0xFFFFFF, "high half");
    }

    [Fact]
    public void A_loop_counts_down_and_skips_out()
    {
        var cx4 = Chip(
            LdMar(0), LdA(5),
            /* 2 */ IncMar, Sub(1), SkipZ, Jmp(2),
            LdAReg(RegMar), StA(R + 0),
            Halt);
        Start(cx4);

        cx4.Register(0).Should().Be(5, "five times round");
    }

    [Fact]
    public void Subroutines_return_to_the_instruction_after_the_call()
    {
        var cx4 = Chip(
            LdA(1), Jsr(5), Add(0x10), StA(R + 0), Halt,
            /* 5 */ Add(0x01, shift: 1), Rts); // A << 1 + 1
        Start(cx4);

        cx4.Register(0).Should().Be(0x13);
    }

    [Fact]
    public void Compare_and_branch_on_equal()
    {
        var cx4 = Chip(LdA(9), Cmp(9), JmpEq(5), LdA(0), Halt, /* 5 */ StA(R + 0), Halt);
        Start(cx4);

        cx4.Register(0).Should().Be(9);
        cx4.Accumulator.Should().Be(9, "CMP doesn't change A");
    }

    [Fact]
    public void Reads_the_data_rom_and_the_data_ram_the_cpu_shares()
    {
        var cx4 = Chip(
            RdRom(5), LdAReg(RegRom), StA(R + 0),
            LdA(0xAB), StA(RegRam), WrRam(0x10),
            RdRam(0x20), LdAReg(RegRam), StA(R + 1),
            LdA(0x77), SwapR0, StA(R + 2),
            Halt);
        cx4.Write(0x006020, 0x55);
        Start(cx4);

        cx4.Read(0x006010, 0).Should().Be(0xAB, "written into the shared data RAM");
        (cx4.Register(1) & 0xFF).Should().Be(0x55);
        cx4.Register(2).Should().Be(5 * 0x010203 & 0xFFFFFF, "R0 held data ROM word 5, which SWAP brought into A");
        cx4.Register(0).Should().Be(0x77);
    }

    [Fact]
    public void Halting_raises_the_irq_unless_it_is_masked()
    {
        var cx4 = Chip(Halt);
        Start(cx4);
        cx4.Irq.Should().BeTrue();
        (cx4.Read(0x007F5E, 0) & 2).Should().Be(2);

        cx4.Write(0x007F5E, 0);
        (cx4.Read(0x007F5E, 0) & 2).Should().Be(0);
        cx4.Irq.Should().BeTrue("$7F5E doesn't drop the CPU's line");
        cx4.Write(0x007F51, 1);
        cx4.Irq.Should().BeFalse("$7F51 masks and clears it");

        cx4.Write(0x007F4F, 0);
        Run(cx4);
        cx4.Irq.Should().BeFalse("masked");
    }

    [Fact]
    public void Dma_copies_rom_into_the_data_ram_and_holds_the_bus_meanwhile()
    {
        var cx4 = Chip(Halt);
        cx4.Write(0x007F40, 0x00);
        cx4.Write(0x007F41, 0xA0);
        cx4.Write(0x007F42, 0x00); // from $00A000
        cx4.Write(0x007F43, 16);
        cx4.Write(0x007F44, 0);
        cx4.Write(0x007F45, 0x00);
        cx4.Write(0x007F46, 0x60);
        cx4.Write(0x007F47, 0x00); // to $006000: starts

        Run(cx4, 2);
        (cx4.Read(0x007F5E, 0) & 0x80).Should().Be(0x80, "busy while the copy's time runs");
        cx4.Read(0x00C000, 0x5A).Should().Be(0x5A, "the CPU can't reach the ROM then");

        Run(cx4);
        (cx4.Read(0x007F5E, 0) & 0x80).Should().Be(0);
        for (var i = 0; i < 16; i++)
        {
            cx4.DataRam[i].Should().Be((byte)(i * 7 + 1));
        }

        cx4.Read(0x00C000, 0).Should().Be(0, "and can again");
    }

    [Fact]
    public void A_save_state_round_trips_the_chip_mid_program()
    {
        var program = new List<ushort> { LdA(0) };
        for (var i = 0; i < 200; i++)
        {
            program.Add(Add(1));
        }

        program.Add(StA(R + 0));
        program.Add(Halt);
        var cx4 = Chip([.. program]);
        cx4.Write(0x007F49, unchecked((byte)ProgramAddress));
        cx4.Write(0x007F4A, (byte)(ProgramAddress >> 8));
        cx4.Write(0x007F4F, 0);
        Run(cx4, 2300); // the cache load and part of the program

        var writer = new StateWriter();
        cx4.SaveState(writer);
        Run(cx4);
        var finished = cx4.Register(0);

        cx4.LoadState(new StateReader(writer.ToArray()));
        Run(cx4);
        cx4.Register(0).Should().Be(finished).And.Be(200);
    }
}
