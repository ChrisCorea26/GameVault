using GameVault.Core.Cpu;

namespace GameVault.Core.Tests.Cpu;

public class CpuInstructionTests : CpuTestBase
{
    [Fact]
    public void Power_runs_the_reset_sequence()
    {
        Boot(0xEA);

        Cpu.PC.Should().Be(Origin);
        Cpu.SP.Should().Be(0xFD);
        Cpu.InterruptDisable.Should().BeTrue();
        Cpu.Status.Should().Be(0x24);
        Cpu.CycleCount.Should().Be(7);
    }

    [Fact]
    public void Reset_keeps_registers_and_lowers_sp_by_three()
    {
        Boot(0xA9, 0x42, 0x78); // LDA #$42, SEI
        Cpu.InterruptDisable = false;
        Cpu.Step();

        Cpu.Reset();

        Cpu.A.Should().Be(0x42);
        Cpu.SP.Should().Be(0xFA);
        Cpu.InterruptDisable.Should().BeTrue();
        Cpu.PC.Should().Be(Origin);
    }

    [Theory]
    [InlineData(0x00, true, false)]
    [InlineData(0x80, false, true)]
    [InlineData(0x42, false, false)]
    public void Lda_immediate_sets_zero_and_negative(byte value, bool zero, bool negative)
    {
        Boot(0xA9, value);

        Step().Should().Be(2);

        Cpu.A.Should().Be(value);
        Cpu.Zero.Should().Be(zero);
        Cpu.Negative.Should().Be(negative);
    }

    [Fact]
    public void Implied_instructions_dummy_read_the_next_byte()
    {
        Boot(0xE8, 0x99); // INX

        Step().Should().Be(2);

        Bus.Log.Should().Equal(new BusAccess(0x8000, 0xE8, false), new BusAccess(0x8001, 0x99, false));
        Cpu.X.Should().Be(1);
        Cpu.PC.Should().Be(0x8001);
    }

    [Fact]
    public void Absolute_x_read_without_page_cross_takes_4_cycles()
    {
        Boot(0xA2, 0x01, 0xBD, 0x00, 0x02); // LDX #1; LDA $0200,X
        Bus.Memory[0x0201] = 0x55;
        Cpu.Step();

        Step().Should().Be(4);
        Cpu.A.Should().Be(0x55);
    }

    [Fact]
    public void Absolute_x_read_with_page_cross_dummy_reads_the_unfixed_address()
    {
        Boot(0xA2, 0x01, 0xBD, 0xFF, 0x02); // LDX #1; LDA $02FF,X
        Bus.Memory[0x0300] = 0x77;
        Cpu.Step();

        Step().Should().Be(5);

        Bus.Log[3].Should().Be(new BusAccess(0x0200, 0x00, false));
        Bus.Log[4].Should().Be(new BusAccess(0x0300, 0x77, false));
        Cpu.A.Should().Be(0x77);
    }

    [Fact]
    public void Absolute_x_store_always_takes_5_cycles_with_a_dummy_read()
    {
        Boot(0xA9, 0x11, 0x9D, 0x00, 0x02); // LDA #$11; STA $0200,X
        Cpu.Step();

        Step().Should().Be(5);

        Bus.Log[3].IsWrite.Should().BeFalse();
        Bus.Log[4].Should().Be(new BusAccess(0x0200, 0x11, true));
    }

    [Fact]
    public void Read_modify_write_writes_the_old_value_then_the_new_one()
    {
        Boot(0xE6, 0x10); // INC $10
        Bus.Memory[0x10] = 0x7F;

        Step().Should().Be(5);

        Bus.Log.Skip(2).Should().Equal(
            new BusAccess(0x0010, 0x7F, false),
            new BusAccess(0x0010, 0x7F, true),
            new BusAccess(0x0010, 0x80, true));
        Cpu.Negative.Should().BeTrue();
    }

    [Fact]
    public void Zero_page_x_wraps_within_the_zero_page()
    {
        Boot(0xA2, 0x20, 0xB5, 0xF0); // LDX #$20; LDA $F0,X
        Bus.Memory[0x0010] = 0x33;
        Cpu.Step();

        Step().Should().Be(4);
        Cpu.A.Should().Be(0x33);
    }

    [Fact]
    public void Indirect_x_reads_pointer_from_zero_page_with_wraparound()
    {
        Boot(0xA2, 0x01, 0xA1, 0xFE); // LDX #1; LDA ($FE,X)
        Bus.Memory[0xFF] = 0x34;
        Bus.Memory[0x00] = 0x12;
        Bus.Memory[0x1234] = 0x99;
        Cpu.Step();

        Step().Should().Be(6);
        Cpu.A.Should().Be(0x99);
    }

    [Fact]
    public void Indirect_y_adds_a_cycle_on_page_cross()
    {
        Boot(0xA0, 0x10, 0xB1, 0x40); // LDY #$10; LDA ($40),Y
        Bus.Memory[0x40] = 0xF8;
        Bus.Memory[0x41] = 0x12;
        Bus.Memory[0x1308] = 0x66;
        Cpu.Step();

        Step().Should().Be(6);
        Cpu.A.Should().Be(0x66);
    }

    [Theory]
    [InlineData(0x50, 0x10, false, 0x60, false, false)]
    [InlineData(0x50, 0x50, false, 0xA0, false, true)]
    [InlineData(0xD0, 0x90, false, 0x60, true, true)]
    [InlineData(0xFF, 0x00, true, 0x00, true, false)]
    public void Adc_sets_carry_and_overflow(byte a, byte operand, bool carryIn, byte result, bool carry, bool overflow)
    {
        Boot(0xA9, a, 0x69, operand); // LDA #a; ADC #operand
        Cpu.Step();
        Cpu.Carry = carryIn;

        Cpu.Step();

        Cpu.A.Should().Be(result);
        Cpu.Carry.Should().Be(carry);
        Cpu.Overflow.Should().Be(overflow);
    }

    [Fact]
    public void Adc_ignores_decimal_mode()
    {
        Boot(0xF8, 0xA9, 0x09, 0x69, 0x01); // SED; LDA #$09; ADC #$01
        StepN(3);

        Cpu.A.Should().Be(0x0A);
        Cpu.Decimal.Should().BeTrue();
    }

    [Theory]
    [InlineData(0x50, 0xF0, 0x60, false, false)]
    [InlineData(0x50, 0xB0, 0xA0, false, true)]
    [InlineData(0x50, 0x30, 0x20, true, false)]
    public void Sbc_with_carry_set_subtracts(byte a, byte operand, byte result, bool carry, bool overflow)
    {
        Boot(0x38, 0xA9, a, 0xE9, operand); // SEC; LDA; SBC
        StepN(3);

        Cpu.A.Should().Be(result);
        Cpu.Carry.Should().Be(carry);
        Cpu.Overflow.Should().Be(overflow);
    }

    [Fact]
    public void Compare_sets_carry_zero_and_negative()
    {
        Boot(0xA9, 0x40, 0xC9, 0x40, 0xC9, 0x41); // LDA #$40; CMP #$40; CMP #$41
        StepN(2);
        Cpu.Carry.Should().BeTrue();
        Cpu.Zero.Should().BeTrue();

        Cpu.Step();
        Cpu.Carry.Should().BeFalse();
        Cpu.Negative.Should().BeTrue();
    }

    [Fact]
    public void Bit_copies_bits_7_and_6_and_tests_the_mask()
    {
        Boot(0xA9, 0x01, 0x24, 0x10); // LDA #1; BIT $10
        Bus.Memory[0x10] = 0xC0;
        StepN(2);

        Cpu.Zero.Should().BeTrue();
        Cpu.Negative.Should().BeTrue();
        Cpu.Overflow.Should().BeTrue();
    }

    [Theory]
    [InlineData(0x0A, 0x81, 0x02, true)] // ASL A
    [InlineData(0x4A, 0x81, 0x40, true)] // LSR A
    public void Shifts_on_the_accumulator(byte opcode, byte a, byte result, bool carry)
    {
        Boot(0xA9, a, opcode);
        Cpu.Step();

        Step().Should().Be(2);

        Cpu.A.Should().Be(result);
        Cpu.Carry.Should().Be(carry);
    }

    [Fact]
    public void Rotates_go_through_carry()
    {
        Boot(0x38, 0xA9, 0x80, 0x2A, 0x6A); // SEC; LDA #$80; ROL A; ROR A
        StepN(3);
        Cpu.A.Should().Be(0x01);
        Cpu.Carry.Should().BeTrue();

        Cpu.Step();
        Cpu.A.Should().Be(0x80);
        Cpu.Carry.Should().BeTrue();
    }

    [Theory]
    [InlineData(0xF0, true, 3)]  // BEQ taken, same page
    [InlineData(0xD0, true, 2)]  // BNE not taken
    public void Branch_timing(byte opcode, bool zero, int cycles)
    {
        Boot(opcode, 0x10);
        Cpu.Zero = zero;

        Step().Should().Be(cycles);
    }

    [Fact]
    public void Branch_across_a_page_takes_4_cycles()
    {
        Bus.Memory[0x80F0] = 0x00;
        Boot();
        Cpu.PC = 0x80F0;
        Bus.Memory[0x80F0] = 0xF0; // BEQ +$20 -> $8112
        Bus.Memory[0x80F1] = 0x20;
        Cpu.Zero = true;

        Step().Should().Be(4);
        Cpu.PC.Should().Be(0x8112);
    }

    [Fact]
    public void Backward_branch_uses_a_signed_offset()
    {
        Boot(0xEA, 0xEA, 0xD0, 0xFC); // NOP; NOP; BNE -4
        StepN(2);
        Cpu.Zero = false;

        Cpu.Step();

        Cpu.PC.Should().Be(Origin);
    }

    [Fact]
    public void Jsr_and_rts_take_6_cycles_and_round_trip()
    {
        Boot(0x20, 0x00, 0x90); // JSR $9000
        Bus.Memory[0x9000] = 0x60; // RTS

        Step().Should().Be(6);
        Cpu.PC.Should().Be(0x9000);
        Bus.Memory[0x01FD].Should().Be(0x80);
        Bus.Memory[0x01FC].Should().Be(0x02);

        Step().Should().Be(6);
        Cpu.PC.Should().Be(0x8003);
        Cpu.SP.Should().Be(0xFD);
    }

    [Fact]
    public void Jmp_indirect_wraps_within_the_page()
    {
        Boot(0x6C, 0xFF, 0x02); // JMP ($02FF)
        Bus.Memory[0x02FF] = 0x34;
        Bus.Memory[0x0200] = 0x12;
        Bus.Memory[0x0300] = 0x99;

        Step().Should().Be(5);
        Cpu.PC.Should().Be(0x1234);
    }

    [Fact]
    public void Php_pushes_break_and_unused_bits_and_plp_ignores_them()
    {
        Boot(0x08, 0xA9, 0x00, 0x28); // PHP; LDA #0; PLP
        Step().Should().Be(3);
        Bus.Memory[0x01FD].Should().Be(0x34);

        Cpu.Step();
        Step().Should().Be(4);
        Cpu.Status.Should().Be(0x24);
    }

    [Fact]
    public void Pha_and_pla_round_trip_and_set_flags()
    {
        Boot(0xA9, 0x80, 0x48, 0xA9, 0x00, 0x68); // LDA #$80; PHA; LDA #0; PLA
        StepN(4);

        Cpu.A.Should().Be(0x80);
        Cpu.Negative.Should().BeTrue();
        Cpu.SP.Should().Be(0xFD);
    }

    [Fact]
    public void Brk_pushes_pc_plus_two_and_status_with_break_then_rti_returns()
    {
        Boot(0x00, 0xFF, 0xEA); // BRK (padding byte); NOP
        Bus.SetVector(0xFFFE, 0x9000);
        Bus.Memory[0x9000] = 0x40; // RTI

        Step().Should().Be(7);
        Cpu.PC.Should().Be(0x9000);
        Cpu.InterruptDisable.Should().BeTrue();
        Bus.Memory[0x01FD].Should().Be(0x80);
        Bus.Memory[0x01FC].Should().Be(0x02);
        (Bus.Memory[0x01FB] & 0x30).Should().Be(0x30);

        Step().Should().Be(6);
        Cpu.PC.Should().Be(0x8002);
    }

    [Fact]
    public void Transfers_update_flags_except_txs()
    {
        Boot(0xA2, 0x00, 0x9A, 0xBA); // LDX #0; TXS; TSX
        Cpu.Step();
        Cpu.Zero.Should().BeTrue();
        Cpu.Step();
        Cpu.SP.Should().Be(0x00);

        Cpu.Step();
        Cpu.X.Should().Be(0x00);
        Cpu.Zero.Should().BeTrue();
    }

    [Fact]
    public void Jam_halts_the_cpu()
    {
        Boot(0x02, 0xE8); // JAM; INX

        Cpu.Step();
        StepN(5);

        Cpu.IsJammed.Should().BeTrue();
        Cpu.X.Should().Be(0);
    }
}
