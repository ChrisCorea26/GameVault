using FluentAssertions;
using GameVault.Core.Cpu.Arm;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Cpu;

/// <summary>The ARM core in ARM state (snes-chips-3.md §4), on programs assembled here (encodings from the ARM ARM).</summary>
public sealed class ArmCpuTests
{
    private sealed class Ram : IArmBus
    {
        public byte[] Bytes { get; } = new byte[0x10000];

        public int Clocks { get; private set; }

        public uint ReadWord(uint address) => BitConverter.ToUInt32(Bytes, (int)(address & 0xFFFC));

        public byte ReadByte(uint address) => Bytes[address & 0xFFFF];

        public void WriteWord(uint address, uint value) => BitConverter.GetBytes(value).CopyTo(Bytes, (int)(address & 0xFFFC));

        public void WriteByte(uint address, byte value) => Bytes[address & 0xFFFF] = value;

        public void Clock(int clocks) => Clocks += clocks;
    }

    private static (ArmCpu Cpu, Ram Ram) Machine(params uint[] program)
    {
        var ram = new Ram();
        for (var i = 0; i < program.Length; i++)
        {
            ram.WriteWord((uint)i * 4, program[i]);
        }

        var cpu = new ArmCpu(ram);
        cpu.Reset();
        return (cpu, ram);
    }

    private static void Run(ArmCpu cpu, int instructions)
    {
        for (var i = 0; i < instructions; i++)
        {
            cpu.Step();
        }
    }

    private const uint FlagN = 0x80000000, FlagZ = 0x40000000, FlagC = 0x20000000, FlagV = 0x10000000;

    [Fact]
    public void Reset_starts_in_supervisor_mode_with_interrupts_off()
    {
        var (cpu, _) = Machine();

        cpu.Pc.Should().Be(0);
        cpu.Mode.Should().Be(ArmCpu.Supervisor);
        (cpu.Cpsr & 0xC0).Should().Be(0xC0);
    }

    [Fact]
    public void Data_processing_with_immediates_shifts_and_flags()
    {
        var (cpu, _) = Machine(
            0xE3A00001, // MOV r0, #1
            0xE2801002, // ADD r1, r0, #2
            0xE1A02201, // MOV r2, r1, LSL #4
            0xE2503002, // SUBS r3, r0, #2 (−1: N set, C clear)
            0x43A04005, // MOVMI r4, #5
            0xE3A054FF, // MOV r5, #0xFF000000 (0xFF ror 8)
            0xE1B06FC5, // MOVS r6, r5, ASR #31
            0xE1A0700F); // MOV r7, pc
        Run(cpu, 8);

        cpu.GetR(1).Should().Be(3);
        cpu.GetR(2).Should().Be(0x30);
        cpu.GetR(3).Should().Be(0xFFFFFFFF);
        cpu.GetR(4).Should().Be(5, "MI held");
        cpu.GetR(5).Should().Be(0xFF000000);
        cpu.GetR(6).Should().Be(0xFFFFFFFF);
        (cpu.Cpsr & (FlagN | FlagC)).Should().Be(FlagN | FlagC, "ASR #31 shifted a 1 out");
        cpu.GetR(7).Should().Be(7 * 4 + 8, "R15 reads 8 ahead");
    }

    [Fact]
    public void Compare_sets_the_flags_and_conditions_follow_them()
    {
        var (cpu, _) = Machine(
            0xE3E00000, // MVN r0, #0 (−1)
            0xE3700001, // CMN r0, #1: zero, carry
            0x03A01007, // MOVEQ r1, #7
            0x23A02008, // MOVCS r2, #8
            0xE3A03102, // MOV r3, #0x80000000
            0xE2533001, // SUBS r3, r3, #1: overflow
            0x63A04009); // MOVVS r4, #9
        Run(cpu, 7);

        cpu.GetR(1).Should().Be(7);
        cpu.GetR(2).Should().Be(8);
        cpu.GetR(3).Should().Be(0x7FFFFFFF);
        cpu.GetR(4).Should().Be(9);
        (cpu.Cpsr & FlagV).Should().Be(FlagV);
    }

    [Fact]
    public void Loads_and_stores_words_and_bytes_with_writeback()
    {
        var (cpu, ram) = Machine(
            0xE3A06C10, // MOV r6, #0x1000
            0xE59F1014, // LDR r1, [pc, #20] → the word at $20
            0xE5861004, // STR r1, [r6, #4]
            0xE5967004, // LDR r7, [r6, #4]
            0xE5C61008, // STRB r1, [r6, #8]
            0xE5D68008, // LDRB r8, [r6, #8]
            0xE5969005, // LDR r9, [r6, #5]: unaligned, rotated
            0xE4B6A004, // LDRT-free post-index: LDR r10, [r6], #4
            0x11223344); // $20: data
        Run(cpu, 8);

        cpu.GetR(1).Should().Be(0x11223344);
        ram.ReadWord(0x1004).Should().Be(0x11223344);
        cpu.GetR(7).Should().Be(0x11223344);
        cpu.GetR(8).Should().Be(0x44);
        cpu.GetR(9).Should().Be(0x44112233, "a load from address+1 rotates the word right by 8");
        cpu.GetR(10).Should().Be(ram.ReadWord(0x1000));
        cpu.GetR(6).Should().Be(0x1004, "post-indexing writes the base back");
    }

    [Fact]
    public void Block_transfers_store_and_load_register_lists()
    {
        var (cpu, ram) = Machine(
            0xE3A06C10, // MOV r6, #0x1000
            0xE3A00001, 0xE3A01002, 0xE3A02003, 0xE3A03004,
            0xE8A6000F, // STMIA r6!, {r0-r3}
            0xE9360780); // LDMDB r6!, {r7-r10}
        Run(cpu, 7);

        ram.ReadWord(0x100C).Should().Be(4);
        cpu.GetR(6).Should().Be(0x1000, "up by 16, then back down by 16");
        (cpu.GetR(7), cpu.GetR(8), cpu.GetR(10)).Should().Be((1u, 2u, 4u));
    }

    [Fact]
    public void Branch_with_link_and_return()
    {
        var (cpu, _) = Machine(
            0xEB000001, // BL +1 → $0C
            0xE3A01001, // MOV r1, #1 (after the return)
            0xEAFFFFFE, // B .
            0xE3A00005, // $0C: MOV r0, #5
            0xE1A0F00E); // MOV pc, lr
        Run(cpu, 5);

        cpu.GetR(0).Should().Be(5);
        cpu.GetR(1).Should().Be(1);
        cpu.GetR(14).Should().Be(4);
        cpu.Pc.Should().Be(8);
    }

    [Fact]
    public void Multiplies_short_and_long()
    {
        var (cpu, _) = Machine(
            0xE3A01C01, // MOV r1, #0x100
            0xE3E02000, // MVN r2, #0 (−1)
            0xE00A0291, // MUL r10, r1, r2
            0xE0854291, // UMULL r4, r5, r1, r2
            0xE0C76291); // SMULL r6, r7, r1, r2
        Run(cpu, 5);

        cpu.GetR(10).Should().Be(0xFFFFFF00);
        (cpu.GetR(4), cpu.GetR(5)).Should().Be((0xFFFFFF00u, 0xFFu), "unsigned: 0x100 × 0xFFFFFFFF");
        (cpu.GetR(6), cpu.GetR(7)).Should().Be((0xFFFFFF00u, 0xFFFFFFFFu), "signed: −256");
    }

    [Fact]
    public void Swap_exchanges_a_register_with_memory()
    {
        var (cpu, ram) = Machine(0xE3A06C10, 0xE3A01009, 0xE1060091); // r6 = $1000; r1 = 9; SWP r0, r1, [r6]
        ram.WriteWord(0x1000, 0xABCD);
        Run(cpu, 3);

        cpu.GetR(0).Should().Be(0xABCD);
        ram.ReadWord(0x1000).Should().Be(9);
    }

    [Fact]
    public void Swi_enters_supervisor_mode_and_movs_returns_with_the_flags()
    {
        var (cpu, _) = Machine(
            0xEA000002, // $00: B $10
            0xE1A00000, // $04
            0xE1B0F00E, // $08: the SWI vector: MOVS pc, lr
            0xE1A00000,
            0xE321F010, // $10: MSR CPSR_c, #0x10 (user mode)
            0xE3700001, // CMN r0, #1 with r0 = 0: flags clear
            0xEF000000, // SWI 0
            0xE3A01001); // MOV r1, #1
        Run(cpu, 3);
        cpu.Mode.Should().Be(ArmCpu.User);

        cpu.Step(); // SWI
        cpu.Mode.Should().Be(ArmCpu.Supervisor);
        cpu.GetR(14).Should().Be(0x1C, "the instruction after the SWI");
        cpu.Pc.Should().Be(8);

        Run(cpu, 2);
        cpu.Mode.Should().Be(ArmCpu.User, "MOVS pc, lr brought back the user mode's CPSR");
        cpu.GetR(1).Should().Be(1);
    }

    [Fact]
    public void Each_mode_has_its_own_stack_pointer_and_fiq_its_own_r8_to_r12()
    {
        var (cpu, _) = Machine(
            0xE3A0D001, // MOV sp, #1 (supervisor)
            0xE3A08002, // MOV r8, #2
            0xE321F011, // MSR CPSR_c, #0x11 (FIQ)
            0xE3A0D003, // MOV sp, #3
            0xE3A08004, // MOV r8, #4
            0xE321F013, // MSR CPSR_c, #0x13 (supervisor)
            0xE10F0000); // MRS r0, CPSR
        Run(cpu, 7);

        cpu.GetR(13).Should().Be(1);
        cpu.GetR(8).Should().Be(2, "FIQ's R8 is its own");
        (cpu.GetR(0) & 0x1F).Should().Be(0x13);
    }

    [Fact]
    public void Undefined_instructions_take_the_undefined_vector()
    {
        var (cpu, _) = Machine(0xEE000010); // a coprocessor transfer: no coprocessor

        cpu.Step();

        cpu.Mode.Should().Be(ArmCpu.Undefined);
        cpu.Pc.Should().Be(4);
    }

    [Fact]
    public void A_save_state_round_trips_the_registers_and_banks()
    {
        var (cpu, ram) = Machine(0xE3A0D001, 0xE321F011, 0xE3A08004, 0xE3A00007);
        Run(cpu, 3);
        var writer = new StateWriter();
        cpu.SaveState(writer);

        var copy = new ArmCpu(ram);
        copy.LoadState(new StateReader(writer.ToArray()));
        copy.Step();

        copy.GetR(0).Should().Be(7);
        copy.GetR(8).Should().Be(4);
        copy.Mode.Should().Be(ArmCpu.Fiq);
    }
}
