using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.Cpu.Sh2;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Cpu;

/// <summary>The SH-2 (sega-cd-32x.md, milestone 2), on hand-assembled programs; the division programs are the SH-2 Programming Manual's.</summary>
public sealed class Sh2CpuTests
{
    /// <summary>64 KB of big-endian RAM: the vectors at 0, programs at $1000.</summary>
    private sealed class Ram : ISh2Bus
    {
        public byte[] Bytes { get; } = new byte[0x10000];

        public byte Read8(uint address) => Bytes[address & 0xFFFF];

        public ushort Read16(uint address) => BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan((int)(address & 0xFFFE)));

        public uint Read32(uint address) => BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)(address & 0xFFFC)));

        public void Write8(uint address, byte value) => Bytes[address & 0xFFFF] = value;

        public void Write16(uint address, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Bytes.AsSpan((int)(address & 0xFFFE)), value);

        public void Write32(uint address, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan((int)(address & 0xFFFC)), value);
    }

    private static (Sh2Cpu Cpu, Ram Ram) Machine(params ushort[] program)
    {
        var ram = new Ram();
        ram.Write32(0, 0x1000); // PC
        ram.Write32(4, 0xF000); // R15
        for (var i = 0; i < program.Length; i++)
        {
            ram.Write16(0x1000 + (uint)i * 2, program[i]);
        }

        var cpu = new Sh2Cpu(ram);
        cpu.PowerOn();
        return (cpu, ram);
    }

    private static void Run(Sh2Cpu cpu, int instructions)
    {
        for (var i = 0; i < instructions; i++)
        {
            cpu.Step();
        }
    }

    private static ushort Op(int pattern, int n = 0, int m = 0) => (ushort)(pattern | (n << 8) | (m << 4));

    [Fact]
    public void Reset_takes_pc_and_the_stack_from_the_vectors()
    {
        var (cpu, _) = Machine(0x0009);

        cpu.Pc.Should().Be(0x1000);
        cpu.GetR(15).Should().Be(0xF000);
        cpu.Mask.Should().Be(15);
    }

    [Fact]
    public void Moves_adds_and_carries()
    {
        var (cpu, _) = Machine(
            0xE1FF, // MOV #-1,R1
            0xE201, // MOV #1,R2
            0x0018, // SETT
            Op(0x300E, 1, 2), // ADDC R2,R1: -1 + 1 + T = 1, carry out
            Op(0x7005, 3), // ADD #5,R3
            Op(0x300A, 3, 2), // SUBC R2,R3: 5 - 1 - T(1) = 3
            Op(0x600A, 4, 2)); // NEGC R2,R4: 0 - 1 - 0 = -1, T = 1
        Run(cpu, 7);

        cpu.GetR(1).Should().Be(1);
        cpu.GetR(3).Should().Be(3);
        cpu.GetR(4).Should().Be(0xFFFFFFFF);
        cpu.T.Should().BeTrue();
    }

    [Theory]
    [InlineData(0x2000 | 0x0D, 0x11223344u, 0x55667788u, 0x77881122u)] // XTRCT R2,R1
    [InlineData(0x6000 | 0x08, 0x11223344u, 0x11223344u, 0x11224433u)] // SWAP.B R2,R1
    [InlineData(0x6000 | 0x09, 0x11223344u, 0x11223344u, 0x33441122u)] // SWAP.W R2,R1
    [InlineData(0x6000 | 0x0E, 0u, 0x80u, 0xFFFFFF80u)] // EXTS.B R2,R1
    [InlineData(0x6000 | 0x0F, 0u, 0x8001u, 0xFFFF8001u)] // EXTS.W
    [InlineData(0x6000 | 0x0C, 0u, 0x1234u, 0x34u)] // EXTU.B
    [InlineData(0x6000 | 0x07, 0u, 0x0F0F0F0Fu, 0xF0F0F0F0u)] // NOT
    [InlineData(0x6000 | 0x0B, 0u, 5u, 0xFFFFFFFBu)] // NEG
    [InlineData(0x2000 | 0x09, 0xFF00FF00u, 0x0FF00FF0u, 0x0F000F00u)] // AND
    [InlineData(0x2000 | 0x0B, 0xFF000000u, 0x000000FFu, 0xFF0000FFu)] // OR
    [InlineData(0x2000 | 0x0A, 0xFFFF0000u, 0xFF00FF00u, 0x00FFFF00u)] // XOR
    [InlineData(0x3000 | 0x08, 10u, 3u, 7u)] // SUB
    public void Two_register_operations(int pattern, uint rn, uint rm, uint expected)
    {
        var (cpu, _) = Machine(Op(pattern, 1, 2));
        cpu.SetR(1, rn);
        cpu.SetR(2, rm);

        Run(cpu, 1);

        cpu.GetR(1).Should().Be(expected);
    }

    [Theory]
    [InlineData(0x4021, 0xFFFFFFFEu, 0xFFFFFFFFu, false)] // SHAR
    [InlineData(0x4001, 3u, 1u, true)] // SHLR
    [InlineData(0x4000, 0x80000001u, 2u, true)] // SHLL
    [InlineData(0x4004, 0x80000001u, 3u, true)] // ROTL
    [InlineData(0x4005, 1u, 0x80000000u, true)] // ROTR
    [InlineData(0x4010, 1u, 0u, true)] // DT
    [InlineData(0x4010, 2u, 1u, false)]
    [InlineData(0x4018, 0x12u, 0x1200u, false)] // SHLL8
    [InlineData(0x4029, 0x12340000u, 0x1234u, false)] // SHLR16
    public void Shifts_and_rotates(int pattern, uint value, uint expected, bool t)
    {
        var (cpu, _) = Machine(Op(pattern, 1));
        cpu.SetR(1, value);

        Run(cpu, 1);

        cpu.GetR(1).Should().Be(expected);
        cpu.T.Should().Be(t);
    }

    [Fact]
    public void Memory_forms_and_conditional_branches()
    {
        var (cpu, ram) = Machine(
            0xD102, // $1000 MOV.L @(2,PC),R1: ($1004 & ~3) + 8 = $100C
            0xC701, // $1002 MOVA @(1,PC),R0: $1004 + 4 = $1008
            Op(0x401B, 3), // $1004 TAS.B @R3
            0x8901, // $1006 BT +1: T set by TAS (the byte was 0), to $100C
            0x0009, // $1008 (word data, never run)
            0x0009,
            0x1234, // $100C: the long's high word... runs as MOV.L R3,@(4,R2) — harmless
            0x5678);
        cpu.SetR(3, 0x3000);
        cpu.SetR(2, 0x3100);
        Run(cpu, 4);

        cpu.GetR(1).Should().Be(0x12345678);
        cpu.GetR(0).Should().Be(0x1008);
        ram.Bytes[0x3000].Should().Be(0x80);
        cpu.T.Should().BeTrue();
        cpu.Pc.Should().Be(0x100C);
    }

    [Fact]
    public void Mac_w_multiplies_words_into_mac()
    {
        var (cpu, ram) = Machine(0x0028, Op(0x400F, 1, 2), Op(0x400F, 1, 2)); // CLRMAC; MAC.W @R2+,@R1+ twice
        ram.Write16(0x2000, 2);
        ram.Write16(0x2002, 0xFFFF);
        ram.Write16(0x2100, 3);
        ram.Write16(0x2102, 4);
        cpu.SetR(1, 0x2000);
        cpu.SetR(2, 0x2100);

        Run(cpu, 3);

        ((int)cpu.Macl).Should().Be(2 * 3 + -1 * 4);
        cpu.GetR(1).Should().Be(0x2004);
    }

    [Fact]
    public void A_delayed_branch_runs_its_slot_first()
    {
        var (cpu, _) = Machine(
            0xA002, // BRA +2 words past PC+4: $1008
            0xE107, // (slot) MOV #7,R1
            0xE2FF, // skipped
            0xE3FF, // skipped
            0xE409); // $1008: MOV #9,R4
        Run(cpu, 2);

        cpu.GetR(1).Should().Be(7);
        cpu.GetR(2).Should().Be(0);
        cpu.GetR(4).Should().Be(9);
    }

    [Fact]
    public void Bsr_and_rts_go_through_pr()
    {
        var (cpu, _) = Machine(
            0xB003, // BSR $100A
            0x0009, // NOP (slot)
            0xE101, // $1004: MOV #1,R1 (after returning)
            0xAFFE, // BRA * (stay)
            0x0009,
            0xE202, // $100A: MOV #2,R2
            0x000B, // RTS
            0x0009);
        Run(cpu, 4);

        cpu.GetR(2).Should().Be(2);
        cpu.Pr.Should().Be(0x1004);
        cpu.GetR(1).Should().Be(1);
    }

    [Fact]
    public void Div1_divides_unsigned_32_by_16_as_the_manual_shows()
    {
        // R1 (32 bits) / R0 (16 bits): SHLL16 R0; DIV0U; 16 × DIV1 R0,R1; ROTCL R1; EXTU.W R1,R1.
        var program = new List<ushort> { 0xE007, Op(0x4028, 0), 0x0019 }; // MOV #7,R0
        program.AddRange(Enumerable.Repeat(Op(0x3004, 1, 0), 16));
        program.Add(Op(0x4024, 1));
        program.Add(Op(0x600D, 1, 1));
        var (cpu, _) = Machine([.. program]);
        cpu.SetR(1, 1000);

        Run(cpu, program.Count - 1 + 1);

        cpu.GetR(1).Should().Be(142);
    }

    [Fact]
    public void Div1_divides_unsigned_64_by_32()
    {
        // R1:R2 / R0: DIV0U; 32 × (ROTCL R2; DIV1 R0,R1); ROTCL R2.
        var program = new List<ushort> { 0x0019 };
        for (var i = 0; i < 32; i++)
        {
            program.Add(Op(0x4024, 2));
            program.Add(Op(0x3004, 1, 0));
        }

        program.Add(Op(0x4024, 2));
        var (cpu, _) = Machine([.. program]);
        cpu.SetR(0, 3);
        cpu.SetR(1, 1);
        cpu.SetR(2, 0);

        Run(cpu, program.Count);

        cpu.GetR(2).Should().Be(0x55555555);
    }

    [Fact]
    public void Div0s_sets_q_and_m_from_the_signs()
    {
        var (cpu, _) = Machine(Op(0x2007, 1, 0)); // DIV0S R0,R1
        cpu.SetR(0, 7);
        cpu.SetR(1, 0x80000000);

        Run(cpu, 1);

        cpu.Sr.Should().Be(0xF0 | 0x100 | 0x001, "Q from R1's sign, M from R0's, T = Q ≠ M");
    }

    [Fact]
    public void Multiplies_and_mac_l_accumulate_64_bits()
    {
        var (cpu, ram) = Machine(
            0xE1FD, // MOV #-3,R1
            0xE205, // MOV #5,R2
            Op(0x300D, 1, 2), // DMULS.L R2,R1
            Op(0x000A, 3), // STS MACH,R3
            Op(0x001A, 4), // STS MACL,R4
            0x0028, // CLRMAC
            Op(0x000F, 6, 7), // MAC.L @R7+,@R6+
            0x0009);
        ram.Write32(0x2000, 0x10000);
        ram.Write32(0x2004, 0x10000);
        cpu.SetR(6, 0x2004);
        cpu.SetR(7, 0x2000);
        Run(cpu, 5);

        cpu.GetR(3).Should().Be(0xFFFFFFFF);
        cpu.GetR(4).Should().Be(unchecked((uint)-15));

        Run(cpu, 2);
        cpu.Mach.Should().Be(1);
        cpu.Macl.Should().Be(0);
    }

    [Fact]
    public void Trapa_and_rte_use_the_vector_table_and_the_stack()
    {
        var (cpu, ram) = Machine(
            0xC320, // TRAPA #$20
            0xE105, // MOV #5,R1 (after the handler)
            0x0009);
        ram.Write32(0x20 * 4, 0x1100);
        ram.Write16(0x1100, 0xE209); // MOV #9,R2
        ram.Write16(0x1102, 0x002B); // RTE
        ram.Write16(0x1104, 0x0009);

        Run(cpu, 1);
        cpu.Pc.Should().Be(0x1100);
        cpu.GetR(15).Should().Be(0xF000 - 8);
        Run(cpu, 3);

        cpu.GetR(2).Should().Be(9);
        cpu.GetR(1).Should().Be(5);
        cpu.GetR(15).Should().Be(0xF000);
    }

    [Fact]
    public void An_external_interrupt_above_the_mask_is_taken_and_raises_it()
    {
        var (cpu, ram) = Machine(
            0xE000, // MOV #0,R0
            Op(0x400E, 0), // LDC R0,SR: mask 0
            0x001B, // SLEEP
            0x0009);
        ram.Write32(71 * 4, 0x1200); // level 14 auto-vector: 64 + 7
        ram.Write16(0x1200, 0xE30E); // MOV #14,R3
        Run(cpu, 3);
        cpu.Sleeping.Should().BeTrue();

        cpu.InterruptLevel = 14;
        Run(cpu, 2);

        cpu.Sleeping.Should().BeFalse();
        cpu.GetR(3).Should().Be(14);
        cpu.Mask.Should().Be(14);
    }

    [Fact]
    public void The_division_unit_divides_32_and_64_bits()
    {
        var (cpu, _) = Machine(0x0009);

        cpu.Write32(0xFFFFFF00, 7); // DVSR
        cpu.Write32(0xFFFFFF04, unchecked((uint)-100)); // DVDNT: 32/32
        ((int)cpu.Read32(0xFFFFFF04)).Should().Be(-14);
        ((int)cpu.Read32(0xFFFFFF10)).Should().Be(-2);

        cpu.Write32(0xFFFFFF10, 1); // DVDNTH
        cpu.Write32(0xFFFFFF14, 0); // DVDNTL: 64/32
        cpu.Read32(0xFFFFFF14).Should().Be(0x24924924);

        cpu.Write32(0xFFFFFF00, 0);
        cpu.Write32(0xFFFFFF04, 5);
        (cpu.Read32(0xFFFFFF08) & 1).Should().Be(1, "dividing by zero sets OVF");
    }

    [Fact]
    public void The_dma_controller_copies_on_auto_request_and_on_dreq()
    {
        var (cpu, ram) = Machine(0x0009);
        for (var i = 0; i < 16; i++)
        {
            ram.Bytes[0x3000 + i] = (byte)(i + 1);
        }

        cpu.Write32(0xFFFFFF80, 0x3000); // SAR0
        cpu.Write32(0xFFFFFF84, 0x4000); // DAR0
        cpu.Write32(0xFFFFFF88, 4); // TCR0: four longs
        cpu.Write32(0xFFFFFFB0, 1); // DMAOR: DME
        cpu.Write32(0xFFFFFF8C, 0x5A01); // CHCR0: dest +, source +, long, auto request, DE

        ram.Bytes.AsSpan(0x4000, 16).ToArray().Should().Equal(ram.Bytes.AsSpan(0x3000, 16).ToArray());
        (cpu.Read32(0xFFFFFF8C) & 2).Should().Be(2, "TE: the transfer ended");

        cpu.Write32(0xFFFFFF90, 0x3000); // SAR1
        cpu.Write32(0xFFFFFF94, 0x5000); // DAR1: fixed (like a FIFO)
        cpu.Write32(0xFFFFFF98, 2);
        cpu.Write32(0xFFFFFF9C, 0x1401); // CHCR1: dest fixed, source +, word, DREQ, DE
        cpu.OnChip.WaitingForDreq(1).Should().BeTrue();
        cpu.OnChip.Dreq(1).Should().BeTrue();
        ram.Read16(0x5000).Should().Be(0x0102);
        cpu.OnChip.Dreq(1).Should().BeTrue();
        ram.Read16(0x5000).Should().Be(0x0304);
        cpu.OnChip.Dreq(1).Should().BeFalse("the count ran out");
    }

    [Fact]
    public void The_free_running_timer_matches_output_compare_a()
    {
        var (cpu, _) = Machine(0x0009);
        cpu.Write8(0xFFFFFE14, 0x00); // OCRA = 10 (high byte through TEMP)
        cpu.Write8(0xFFFFFE15, 10);
        cpu.Write8(0xFFFFFE16, 0x00); // TCR: clock / 8

        cpu.OnChip.Run(8 * 10);

        (cpu.Read8(0xFFFFFE11) & 0x08).Should().Be(0x08, "OCFA");
    }

    [Fact]
    public void A_save_state_round_trips_the_registers()
    {
        var (cpu, ram) = Machine(0xE10C, 0x7101, 0xAFFD, 0x0009); // MOV #12,R1; loop: ADD #1,R1; BRA loop
        Run(cpu, 5);
        var writer = new StateWriter();
        cpu.SaveState(writer);
        Run(cpu, 10);
        var r1 = cpu.GetR(1);

        var copy = new Sh2Cpu(ram);
        copy.LoadState(new StateReader(writer.ToArray()));
        Run(copy, 10);

        copy.GetR(1).Should().Be(r1);
        copy.Pc.Should().Be(cpu.Pc);
    }
}
