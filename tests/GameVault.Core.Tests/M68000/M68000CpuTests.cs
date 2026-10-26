using FluentAssertions;
using GameVault.Core.Cpu.M68000;
using GameVault.Core.State;

namespace GameVault.Core.Tests.M68000;

/// <summary>
/// The 68000 (SPEC §7.11, Phase 13): what the single-step suite doesn't cover — reset, interrupts, STOP, trace,
/// privilege and save states.
/// </summary>
public sealed class M68000CpuTests
{
    private sealed class Bus : IM68000Bus
    {
        public readonly byte[] Memory = new byte[0x10000];

        public int? Vector { get; set; }

        public List<int> Acknowledged { get; } = [];

        public byte ReadByte(uint address) => Memory[address & 0xFFFF];

        public ushort ReadWord(uint address) => (ushort)((Memory[address & 0xFFFF] << 8) | Memory[(address + 1) & 0xFFFF]);

        public void WriteByte(uint address, byte value) => Memory[address & 0xFFFF] = value;

        public void WriteWord(uint address, ushort value)
        {
            Memory[address & 0xFFFF] = (byte)(value >> 8);
            Memory[(address + 1) & 0xFFFF] = (byte)value;
        }

        public int AcknowledgeInterrupt(int level)
        {
            Acknowledged.Add(level);
            return Vector ?? -1;
        }

        public void Long(uint address, uint value)
        {
            WriteWord(address, (ushort)(value >> 16));
            WriteWord(address + 2, (ushort)value);
        }

        public void Words(uint address, params ushort[] words)
        {
            foreach (var word in words)
            {
                WriteWord(address, word);
                address += 2;
            }
        }
    }

    /// <summary>A CPU after reset: SSP $8000, code at $1000.</summary>
    private static (M68000Cpu Cpu, Bus Bus) Boot(params ushort[] program)
    {
        var bus = new Bus();
        bus.Long(0, 0x8000);
        bus.Long(4, 0x1000);
        bus.Words(0x1000, program);
        var cpu = new M68000Cpu(bus);
        cpu.Reset();
        return (cpu, bus);
    }

    [Fact]
    public void Reset_loads_the_stack_and_program_counter_in_supervisor_mode_with_interrupts_masked()
    {
        var (cpu, _) = Boot(0x4E71);

        cpu.Pc.Should().Be(0x1000u);
        cpu.GetA(7).Should().Be(0x8000u);
        cpu.Sr.Should().Be(0x2700);
        cpu.Step().Should().Be(4, "NOP takes 4 cycles");
    }

    [Fact]
    public void An_autovectored_interrupt_above_the_mask_is_taken_and_rte_returns()
    {
        // MOVE #$2000,SR (unmask), then NOPs; the level 6 autovector (30) points at an RTE.
        var (cpu, bus) = Boot(0x46FC, 0x2000, 0x4E71, 0x4E71);
        bus.Long(30 * 4, 0x2000);
        bus.Words(0x2000, 0x4E73);
        cpu.Step();
        cpu.InterruptLevel = 6;

        cpu.Step().Should().Be(44);

        bus.Acknowledged.Should().Equal(6);
        cpu.Pc.Should().Be(0x2000u);
        cpu.Sr.Should().Be(0x2600, "supervisor mode with the mask raised to the interrupt's level");
        cpu.GetA(7).Should().Be(0x8000u - 6);

        cpu.InterruptLevel = 0;
        cpu.Step();
        cpu.Pc.Should().Be(0x1004u, "RTE returns to the instruction after the MOVE");
        cpu.Sr.Should().Be(0x2000);
    }

    [Fact]
    public void Interrupts_at_or_below_the_mask_wait_and_a_device_vector_is_used_when_given()
    {
        var (cpu, bus) = Boot(0x4E71, 0x46FC, 0x2300, 0x4E71);
        bus.Vector = 64;
        bus.Long(64 * 4, 0x3000);
        cpu.InterruptLevel = 3;
        cpu.Step();
        cpu.Pc.Should().Be(0x1002u, "the mask is 7");
        cpu.Step(); // mask 3: level 3 is still masked
        cpu.Step();
        cpu.Pc.Should().Be(0x1008u);

        cpu.InterruptLevel = 4;
        cpu.Step();

        cpu.Pc.Should().Be(0x3000u, "vector 64 from the device");
    }

    [Fact]
    public void Stop_waits_for_an_interrupt()
    {
        var (cpu, bus) = Boot(0x4E72, 0x2000, 0x4E71);
        bus.Long(25 * 4, 0x2000);
        cpu.Step();
        cpu.Stopped.Should().BeTrue();
        cpu.Step();
        cpu.Pc.Should().Be(0x1004u, "a stopped CPU runs nothing");

        cpu.InterruptLevel = 1;
        cpu.Step();

        cpu.Stopped.Should().BeFalse();
        cpu.Pc.Should().Be(0x2000u);
    }

    [Fact]
    public void Trace_takes_an_exception_after_each_instruction()
    {
        // MOVE #$A000,SR sets T; the next instruction then traps through vector 9.
        var (cpu, bus) = Boot(0x46FC, 0xA000, 0x7001, 0x4E71);
        bus.Long(9 * 4, 0x2000);
        cpu.Step();
        cpu.Pc.Should().Be(0x1004u);

        cpu.Step();

        cpu.GetD(0).Should().Be(1u, "MOVEQ ran first");
        cpu.Pc.Should().Be(0x2000u);
        cpu.Sr.Should().Be(0x2000, "trace is off in the handler");
    }

    [Fact]
    public void Privileged_instructions_in_user_mode_trap()
    {
        // MOVE #$0000,SR drops to user mode; then MOVE #$2700,SR is a privilege violation (vector 8).
        var (cpu, bus) = Boot(0x46FC, 0x0000, 0x46FC, 0x2700);
        bus.Long(8 * 4, 0x2000);
        cpu.Usp = 0x6000;
        cpu.Step();
        cpu.GetA(7).Should().Be(0x6000u, "user mode uses the USP");

        cpu.Step();

        cpu.Pc.Should().Be(0x2000u);
        cpu.Ssp.Should().Be(0x8000u - 6);
        bus.ReadWord(0x8000 - 4).Should().Be(0x0000, "the stacked PC points at the faulting instruction");
        bus.ReadWord(0x8000 - 2).Should().Be(0x1004);
    }

    [Fact]
    public void Save_states_round_trip_and_replay_identically()
    {
        // A loop: ADDQ.L #1,D0; ADD.L D0,D1; BRA.S loop.
        var (cpu, bus) = Boot(0x5280, 0xD280, 0x60FA);
        for (var i = 0; i < 50; i++)
        {
            cpu.Step();
        }

        var writer = new StateWriter();
        cpu.SaveState(writer);
        var saved = writer.ToArray();
        for (var i = 0; i < 30; i++)
        {
            cpu.Step();
        }

        var (d0, d1, pc, cycles) = (cpu.GetD(0), cpu.GetD(1), cpu.Pc, cpu.Cycles);

        var copy = new M68000Cpu(bus);
        copy.LoadState(new StateReader(saved));
        for (var i = 0; i < 30; i++)
        {
            copy.Step();
        }

        copy.GetD(0).Should().Be(d0);
        copy.GetD(1).Should().Be(d1);
        copy.Pc.Should().Be(pc);
        copy.Cycles.Should().Be(cycles);
    }
}
