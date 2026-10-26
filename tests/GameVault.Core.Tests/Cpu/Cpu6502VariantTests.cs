using FluentAssertions;
using GameVault.Core.Cpu;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.Cpu;

/// <summary>The NMOS 6502 with decimal mode and the 65C02 (SPEC §7.11, Phase 12: the Apple II).</summary>
public sealed class Cpu6502VariantTests
{
    private sealed class FlatBus(byte[] memory) : ICpuBus
    {
        public byte Read(ushort address) => memory[address];

        public void Write(ushort address, byte value) => memory[address] = value;
    }

    /// <summary>
    /// Runs Klaus Dormann's test from $0400 until the program counter stops moving: every failure is a trap
    /// (a branch or jump to itself), and so is success, at a known address.
    /// </summary>
    private static ushort RunUntilTrapped(byte[] image, CpuVariant variant, long maxInstructions = 200_000_000)
    {
        var memory = image.ToArray();
        var cpu = new Cpu6502(new FlatBus(memory), variant) { PC = 0x0400 };
        for (long i = 0; i < maxInstructions; i++)
        {
            var pc = cpu.PC;
            cpu.Step();
            if (cpu.PC == pc)
            {
                return pc;
            }
        }

        throw new TimeoutException($"Still running at ${cpu.PC:X4}.");
    }

    [TestRomFact("6502/6502_functional_test.bin")]
    public void The_nmos_6502_passes_the_functional_test_including_decimal_mode() =>
        RunUntilTrapped(TestRomLocator.ReadCached("6502/6502_functional_test.bin"), CpuVariant.Nmos6502)
            .Should().Be(0x3469, "that is the success trap; anything else is a failing test's trap");

    [TestRomFact("6502/65C02_extended_opcodes_test.bin")]
    public void The_65c02_passes_the_extended_opcodes_test() =>
        RunUntilTrapped(TestRomLocator.ReadCached("6502/65C02_extended_opcodes_test.bin"), CpuVariant.Cmos65C02)
            .Should().Be(0x24F1, "that is the success trap; anything else is a failing test's trap");

    [Theory]
    [InlineData(CpuVariant.Nmos6502, 0x58, 0x46, false, 0x05, true)]   // 58 + 46 + 1 = 105
    [InlineData(CpuVariant.Cmos65C02, 0x12, 0x34, false, 0x46, false)]
    [InlineData(CpuVariant.Ricoh2A03, 0x09, 0x01, false, 0x0A, false)] // the NES ignores decimal mode
    public void Decimal_adc_adds_bcd(CpuVariant variant, byte a, byte operand, bool carryIn, byte expected, bool carryOut)
    {
        // SED; SEC or CLC; LDA #a; ADC #operand
        var memory = new byte[0x10000];
        byte[] program = [0xF8, carryIn ? (byte)0x38 : (byte)0x18, 0xA9, a, 0x69, operand];
        if (variant == CpuVariant.Nmos6502)
        {
            program[1] = 0x38; // 58 + 46 + 1
        }

        program.CopyTo(memory, 0x200);
        var cpu = new Cpu6502(new FlatBus(memory), variant) { PC = 0x200 };
        for (var i = 0; i < 4; i++)
        {
            cpu.Step();
        }

        cpu.A.Should().Be(expected);
        cpu.Carry.Should().Be(carryOut);
    }

    [Fact]
    public void The_65c02_fixes_jmp_indirect_across_a_page()
    {
        var memory = new byte[0x10000];
        byte[] program = [0x6C, 0xFF, 0x10]; // JMP ($10FF)
        program.CopyTo(memory, 0x200);
        memory[0x10FF] = 0x34;
        memory[0x1100] = 0x12; // the 65C02 reads here
        memory[0x1000] = 0x56; // the NMOS 6502 reads here

        var cmos = new Cpu6502(new FlatBus(memory.ToArray()), CpuVariant.Cmos65C02) { PC = 0x200 };
        cmos.Step();
        cmos.PC.Should().Be(0x1234);

        var nmos = new Cpu6502(new FlatBus(memory.ToArray()), CpuVariant.Nmos6502) { PC = 0x200 };
        nmos.Step();
        nmos.PC.Should().Be(0x5634);
    }
}
