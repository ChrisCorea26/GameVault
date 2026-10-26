using System.Diagnostics;
using System.Text;
using FluentAssertions;
using GameVault.Core.Cpu.Z80;
using GameVault.Core.State;
using GameVault.Core.Tests.Harness;
using Xunit.Abstractions;

namespace GameVault.Core.Tests.Z80;

/// <summary>Runs only with <c>GAMEVAULT_RUN_ZEX=1</c> (and the test ROMs fetched).</summary>
public sealed class ZexTheoryAttribute : TheoryAttribute
{
    public ZexTheoryAttribute()
    {
        Skip = Environment.GetEnvironmentVariable("GAMEVAULT_RUN_ZEX") == "1"
            ? TestRomSkip.Reason(["z80/zexdoc.com", "z80/zexall.com"])
            : "Set GAMEVAULT_RUN_ZEX=1 to run the Z80 exercisers (several minutes each).";
    }
}

/// <summary>The Z80 (SPEC §7.11, Phase 11).</summary>
public sealed class Z80Tests(ITestOutputHelper output)
{
    /// <summary>64 KB of RAM and a port log.</summary>
    internal sealed class FlatBus : IZ80Bus
    {
        public byte[] Memory { get; } = new byte[0x10000];

        public List<(ushort Port, byte Value)> Outs { get; } = [];

        public byte InValue { get; set; } = 0xFF;

        public byte Read(ushort address) => Memory[address];

        public void Write(ushort address, byte value) => Memory[address] = value;

        public byte In(ushort port) => InValue;

        public void Out(ushort port, byte value) => Outs.Add((port, value));
    }

    private static (Z80Cpu Cpu, FlatBus Bus) Machine(params byte[] program)
    {
        var bus = new FlatBus();
        program.CopyTo(bus.Memory, 0);
        var cpu = new Z80Cpu(bus);
        cpu.Power();
        cpu.SP = 0xF000;
        return (cpu, bus);
    }

    /// <summary>
    /// Runs a CP/M program: loaded at $0100, BDOS calls at $0005 (functions 2 and 9 print), and a jump to
    /// $0000 ends it. Returns what it printed.
    /// </summary>
    private static string RunCpm(byte[] program, out long instructions)
    {
        var bus = new FlatBus();
        program.CopyTo(bus.Memory, 0x100);
        bus.Memory[0x0005] = 0xC9; // RET, after the BDOS call is handled here
        var cpu = new Z80Cpu(bus);
        cpu.Power();
        cpu.PC = 0x100;
        cpu.SP = 0xF000;
        var text = new StringBuilder();
        instructions = 0;
        while (true)
        {
            if (cpu.PC == 0x0005)
            {
                switch (cpu.C)
                {
                    case 2:
                        text.Append((char)cpu.E);
                        break;
                    case 9:
                        for (var address = cpu.DE; bus.Memory[address] != '$'; address++)
                        {
                            text.Append((char)bus.Memory[address]);
                        }

                        break;
                }
            }
            else if (cpu.PC == 0x0000)
            {
                return text.ToString();
            }

            cpu.Step();
            instructions++;
        }
    }

    /// <summary>
    /// zexdoc and zexall run billions of instructions (about 6 minutes each in a Release build), so they only run
    /// when <c>GAMEVAULT_RUN_ZEX=1</c>; CI runs them in their own Release job.
    /// </summary>
    [ZexTheory]
    [InlineData("z80/zexdoc.com")]
    [InlineData("z80/zexall.com")]
    public void Zex_exercises_every_instruction_without_errors(string path)
    {
        var watch = Stopwatch.StartNew();
        var printed = RunCpm(TestRomLocator.ReadCached(path), out var instructions);
        output.WriteLine(printed);
        output.WriteLine($"{instructions:N0} instructions in {watch.Elapsed.TotalSeconds:F1} s");

        printed.Should().Contain("Tests complete");
        printed.Should().NotContain("ERROR");
    }

    [Fact]
    public void Arithmetic_sets_the_documented_and_undocumented_flags()
    {
        // LD A,$7F; ADD A,1  →  $80: S, H, overflow; X and Y from the result.
        var (cpu, _) = Machine(0x3E, 0x7F, 0xC6, 0x01);
        cpu.Step();
        cpu.Step().Should().Be(7);
        cpu.A.Should().Be(0x80);
        cpu.F.Should().Be(Z80Cpu.FlagS | Z80Cpu.FlagH | Z80Cpu.FlagPV);

        // LD A,$10; CP $28: X and Y come from the operand.
        (cpu, _) = Machine(0x3E, 0x10, 0xFE, 0x28);
        cpu.Step();
        cpu.Step();
        (cpu.F & (Z80Cpu.FlagX | Z80Cpu.FlagY)).Should().Be(0x28);
        (cpu.F & Z80Cpu.FlagC).Should().Be(Z80Cpu.FlagC);
    }

    [Fact]
    public void Daa_corrects_after_addition_and_subtraction()
    {
        // LD A,$15; ADD A,$27; DAA → $42.  SUB $13; DAA → $29.
        var (cpu, _) = Machine(0x3E, 0x15, 0xC6, 0x27, 0x27, 0xD6, 0x13, 0x27);
        for (var i = 0; i < 3; i++)
        {
            cpu.Step();
        }

        cpu.A.Should().Be(0x42);
        cpu.Step();
        cpu.Step();
        cpu.A.Should().Be(0x29);
        (cpu.F & Z80Cpu.FlagN).Should().Be(Z80Cpu.FlagN);
    }

    [Fact]
    public void Ldir_copies_and_takes_21_cycles_per_repeat()
    {
        // LD HL,$1000; LD DE,$2000; LD BC,3; LDIR
        var (cpu, bus) = Machine(0x21, 0x00, 0x10, 0x11, 0x00, 0x20, 0x01, 0x03, 0x00, 0xED, 0xB0);
        bus.Memory[0x1000] = 1;
        bus.Memory[0x1001] = 2;
        bus.Memory[0x1002] = 3;
        cpu.Step();
        cpu.Step();
        cpu.Step();
        cpu.Step().Should().Be(21);
        cpu.Step().Should().Be(21);
        cpu.Step().Should().Be(16);
        bus.Memory.AsSpan(0x2000, 3).ToArray().Should().Equal(1, 2, 3);
        cpu.BC.Should().Be(0);
        cpu.PC.Should().Be(11);
        (cpu.F & Z80Cpu.FlagPV).Should().Be(0);
    }

    [Fact]
    public void Index_registers_use_displacements_and_their_halves()
    {
        // LD IX,$3000; LD (IX+5),$AA; LD A,(IX+5); LD IXH,$12 (DD 26 12); LD H,(IX-2) keeps the real H.
        var (cpu, bus) = Machine(0xDD, 0x21, 0x00, 0x30, 0xDD, 0x36, 0x05, 0xAA, 0xDD, 0x7E, 0x05, 0xDD, 0x26, 0x12, 0xDD, 0x66, 0xFE);
        bus.Memory[0x2FFE] = 0x77;
        cpu.Step().Should().Be(14);
        cpu.Step().Should().Be(19);
        cpu.Step().Should().Be(19);
        cpu.A.Should().Be(0xAA);
        cpu.Step().Should().Be(11);
        cpu.IX.Should().Be(0x1200);
        cpu.Step();
        cpu.H.Should().Be(0x00, "IX is now $1200, so (IX-2) is $11FE, which is 0");
        cpu.IX.Should().Be(0x1200);
    }

    [Fact]
    public void Interrupts_wait_for_ei_and_im1_jumps_to_38h()
    {
        // IM 1; EI; NOP; NOP
        var (cpu, _) = Machine(0xED, 0x56, 0xFB, 0x00, 0x00);
        cpu.IrqLine = true;
        cpu.Step();
        cpu.Step(); // EI
        cpu.Step().Should().Be(4, "the instruction after EI runs before the interrupt");
        cpu.Step().Should().Be(13);
        cpu.PC.Should().Be(0x38);
        cpu.Iff1.Should().BeFalse();
    }

    [Fact]
    public void Halt_waits_for_an_interrupt_and_nmi_jumps_to_66h()
    {
        var (cpu, _) = Machine(0x76, 0x00);
        cpu.Step();
        cpu.Halted.Should().BeTrue();
        cpu.Step().Should().Be(4);
        cpu.PC.Should().Be(1);
        cpu.RaiseNmi();
        cpu.Step().Should().Be(11);
        cpu.Halted.Should().BeFalse();
        cpu.PC.Should().Be(0x66);
    }

    [Fact]
    public void R_counts_opcode_fetches_including_prefixes()
    {
        // NOP; LD IX,0 (2 fetches); BIT 0,(IX+0) (DD CB: 2 fetches)
        var (cpu, _) = Machine(0x00, 0xDD, 0x21, 0x00, 0x00, 0xDD, 0xCB, 0x00, 0x46);
        cpu.Step();
        cpu.Step();
        cpu.Step();
        cpu.R.Should().Be(5);
    }

    [Fact]
    public void State_round_trips()
    {
        var (cpu, bus) = Machine(0x3E, 0x12, 0x06, 0x34, 0xD9, 0xFB);
        for (var i = 0; i < 4; i++)
        {
            cpu.Step();
        }

        var writer = new StateWriter();
        cpu.SaveState(writer);
        var copy = new Z80Cpu(bus);
        copy.LoadState(new StateReader(writer.ToArray()));
        copy.AF.Should().Be(cpu.AF);
        copy.AltBC.Should().Be(cpu.AltBC);
        copy.PC.Should().Be(cpu.PC);
        copy.Iff1.Should().BeTrue();
        copy.Cycles.Should().Be(cpu.Cycles);
    }
}
