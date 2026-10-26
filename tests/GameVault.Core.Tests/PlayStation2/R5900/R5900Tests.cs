using FluentAssertions;
using GameVault.Core.Cpu.R5900;
using GameVault.Core.State;

namespace GameVault.Core.Tests.PlayStation2.R5900;

/// <summary>The R5900's COP0 (exceptions, interrupts, the timer, the TLB), COP2 transfers and save states.</summary>
public sealed class R5900Tests
{
    private const uint Origin = R5900TestMachine.Origin;

    private readonly R5900TestMachine _machine = new();
    private readonly List<R5900Exception> _exceptions = [];

    private R5900Cpu Cpu => _machine.Cpu;

    /// <summary>Records exceptions as they're taken (the hook returns false, so the CPU takes them).</summary>
    private void RecordExceptions() => Cpu.ExceptionHook = code =>
    {
        _exceptions.Add(code);
        return false;
    };

    private uint ExcCode => (Cpu.Cause >> 2) & 0x1F;

    // ---- Reset ----

    [Fact]
    public void Reset_starts_at_the_bios_in_kernel_mode_with_erl_and_bev()
    {
        var cpu = new R5900Cpu(new FlatR5900Bus());
        cpu.Pc.Should().Be(0xBFC0_0000);
        cpu.ReadCop0(15).Should().Be(0x2E20, "PRId: implementation $2E");
        (cpu.Status & (R5900Cpu.StatusErl | R5900Cpu.StatusBev)).Should().Be(R5900Cpu.StatusErl | R5900Cpu.StatusBev);
        cpu.Fcr31.Should().Be(0x0100_0001);
    }

    // ---- 128-bit registers ----

    [Fact]
    public void Mips_iii_instructions_leave_the_upper_64_bits_alone_and_lq_sq_move_all_128()
    {
        Cpu.SetGpr(1, new UInt128(0x1111_2222_3333_4444, 0x5555_6666_7777_8888));
        Cpu.SetGpr64(2, 0x1000);
        _machine.Run(
            Ee.Addiu(1, 0, 5),
            Ee.Lui(3, 0x8002),
            Ee.Sq(1, 3, 0x0F), // the low four bits of the address are ignored
            Ee.Lq(4, 3, 0x08));

        Cpu.GetGpr(1).Should().Be(new UInt128(0x1111_2222_3333_4444, 5));
        Cpu.GetGpr(4).Should().Be(new UInt128(0x1111_2222_3333_4444, 5));
        Cpu.GetGpr(0).Should().Be(UInt128.Zero);
    }

    [Fact]
    public void Three_operand_mult_writes_lo_to_rd_and_mflo_waits_for_the_multiplier()
    {
        Cpu.SetGpr64(1, 7);
        Cpu.SetGpr64(2, unchecked((ulong)-3));
        var start = Cpu.Cycles;
        _machine.Run(Ee.Mult(3, 1, 2), Ee.Mflo(4), Ee.Mfhi(5));
        Cpu.GetGpr64(3).Should().Be(unchecked((ulong)-21));
        Cpu.GetGpr64(4).Should().Be(unchecked((ulong)-21));
        Cpu.GetGpr64(5).Should().Be(ulong.MaxValue);
        (Cpu.Cycles - start).Should().Be(1 + 4 + 1, "MFLO stalls until the multiply's 4 cycles are up");
    }

    [Fact]
    public void Div_stalls_hi_lo_reads_for_its_latency()
    {
        Cpu.SetGpr64(1, 100);
        Cpu.SetGpr64(2, 7);
        var start = Cpu.Cycles;
        _machine.Run(Ee.Div(1, 2), Ee.Nop, Ee.Mflo(3), Ee.Mfhi(4));
        Cpu.GetGpr64(3).Should().Be(14);
        Cpu.GetGpr64(4).Should().Be(2);
        (Cpu.Cycles - start).Should().Be(1 + 37 + 1);
    }

    // ---- Exceptions ----

    [Fact]
    public void Overflow_raises_an_exception_without_writing_the_result()
    {
        RecordExceptions();
        Cpu.SetGpr64(1, 0x7FFF_FFFF);
        _machine.Step(1, Ee.Addi(2, 1, 1));
        _exceptions.Should().Equal(R5900Exception.Overflow);
        Cpu.GetGpr64(2).Should().Be(0);
        Cpu.Epc.Should().Be(Origin);
        Cpu.Pc.Should().Be(0x8000_0180, "the common vector with BEV clear");
        (Cpu.Status & R5900Cpu.StatusExl).Should().NotBe(0);
        ExcCode.Should().Be(0x0C);
    }

    [Fact]
    public void An_exception_in_a_delay_slot_saves_the_branch_and_sets_bd()
    {
        RecordExceptions();
        _machine.Step(2, Ee.Beq(0, 0, 4), Ee.Syscall);
        _exceptions.Should().Equal(R5900Exception.Syscall);
        Cpu.Epc.Should().Be(Origin);
        (Cpu.Cause & 0x8000_0000).Should().NotBe(0);
    }

    [Fact]
    public void Bev_moves_the_level_1_vectors_to_the_bios()
    {
        Cpu.Status |= R5900Cpu.StatusBev;
        _machine.Step(1, Ee.Break);
        Cpu.Pc.Should().Be(0xBFC0_0380);
        ExcCode.Should().Be(0x09);
    }

    [Fact]
    public void Traps_reserved_instructions_and_unusable_coprocessors_raise_their_exceptions()
    {
        RecordExceptions();
        Cpu.SetGpr64(1, 5);
        Cpu.SetGpr64(2, 5);
        _machine.Step(1, Ee.Teq(1, 2));
        ExcCode.Should().Be(0x0D);

        Cpu.Status &= ~R5900Cpu.StatusExl;
        _machine.Step(1, 0xC000_0000); // opcode $30: LL doesn't exist on the EE
        ExcCode.Should().Be(0x0A);

        Cpu.Status = (Cpu.Status & ~(R5900Cpu.StatusExl | R5900Cpu.StatusCu1));
        _machine.Step(1, Ee.Mtc1(1, 0));
        ExcCode.Should().Be(0x0B);
        ((Cpu.Cause >> 28) & 3).Should().Be(1, "Cause.CE names coprocessor 1");
        _exceptions.Should().Equal(R5900Exception.Trap, R5900Exception.ReservedInstruction, R5900Exception.CoprocessorUnusable);
    }

    [Fact]
    public void Unaligned_loads_raise_address_errors_with_badvaddr()
    {
        Cpu.SetGpr64(1, 0x8002_0001);
        _machine.Step(1, Ee.Lw(2, 1, 0));
        ExcCode.Should().Be(0x04);
        Cpu.BadVAddr.Should().Be(0x8002_0001);

        Cpu.Status &= ~R5900Cpu.StatusExl;
        _machine.Step(1, Ee.Sd(2, 1, 1));
        ExcCode.Should().Be(0x05);
        Cpu.BadVAddr.Should().Be(0x8002_0002);
    }

    [Fact]
    public void User_mode_cant_reach_kseg0()
    {
        // Run user code in kuseg, mapped by a global TLB entry onto the program's page.
        Cpu.WriteTlbEntry(0, 0, 0x0001_0000, ((0x0001_0000u >> 12) << 6) | 0x1F, ((0x0001_1000u >> 12) << 6) | 0x1F);
        _machine.Load(Origin, Ee.Lw(2, 1, 0));
        Cpu.SetGpr64(1, 0x8000_1000);
        Cpu.Status = (Cpu.Status & ~0x18u) | 0x10; // KSU = user
        Cpu.Jump(0x0001_0000);
        Cpu.Step();
        ExcCode.Should().Be(0x04);
        Cpu.BadVAddr.Should().Be(0x8000_1000);
        Cpu.Epc.Should().Be(0x0001_0000);
    }

    [Fact]
    public void Eret_returns_to_epc_or_erroreepc()
    {
        Cpu.WriteCop0(14, Origin + 0x40);
        Cpu.Status |= R5900Cpu.StatusExl;
        _machine.Step(1, Ee.Eret);
        Cpu.Pc.Should().Be(Origin + 0x40, "ERET has no delay slot");
        (Cpu.Status & R5900Cpu.StatusExl).Should().Be(0);

        Cpu.WriteCop0(30, Origin + 0x80);
        Cpu.Status |= R5900Cpu.StatusErl | R5900Cpu.StatusExl;
        _machine.Step(1, Ee.Eret);
        Cpu.Pc.Should().Be(Origin + 0x80, "ERL takes ErrorEPC first");
        (Cpu.Status & R5900Cpu.StatusErl).Should().Be(0);
        (Cpu.Status & R5900Cpu.StatusExl).Should().NotBe(0);
    }

    [Fact]
    public void The_exception_hook_can_handle_a_syscall_and_carry_on()
    {
        Cpu.ExceptionHook = code =>
        {
            code.Should().Be(R5900Exception.Syscall);
            Cpu.CurrentPc.Should().Be(Origin + 4);
            Cpu.SetGpr64(2, 0x1234);
            return true;
        };

        _machine.Run(Ee.Nop, Ee.Syscall, Ee.Addiu(3, 2, 1));
        Cpu.GetGpr64(3).Should().Be(0x1235, "execution continued after the SYSCALL");
        (Cpu.Status & R5900Cpu.StatusExl).Should().Be(0);
    }

    // ---- Interrupts ----

    private void EnableInterrupts(uint mask) =>
        Cpu.Status = (Cpu.Status & ~0xFF1Fu) | R5900Cpu.StatusIe | R5900Cpu.StatusEie | mask;

    [Fact]
    public void Int0_interrupts_only_with_ie_eie_and_its_mask_bit()
    {
        _machine.Load(Origin, Ee.Nop, Ee.Nop, Ee.Nop, Ee.Nop);
        Cpu.Jump(Origin);
        Cpu.Int0 = true;
        Cpu.Status |= R5900Cpu.StatusIe | R5900Cpu.StatusIm2; // EIE still clear
        Cpu.Step();
        Cpu.Pc.Should().Be(Origin + 4);

        _machine.Load(Origin, Ee.Ei, Ee.Nop);
        Cpu.Jump(Origin);
        Cpu.Step(); // EI sets EIE
        Cpu.Step();
        Cpu.Pc.Should().Be(0x8000_0200, "the interrupt vector");
        Cpu.Epc.Should().Be(Origin + 4);
        ExcCode.Should().Be(0);
        (Cpu.Cause & R5900Cpu.CauseIp2).Should().NotBe(0);
    }

    [Fact]
    public void Di_and_exl_hold_interrupts_off()
    {
        EnableInterrupts(R5900Cpu.StatusIm3);
        _machine.Load(Origin, Ee.Di, Ee.Nop, Ee.Nop);
        Cpu.Jump(Origin);
        Cpu.Step();
        Cpu.Int1 = true;
        Cpu.Step();
        Cpu.Pc.Should().Be(Origin + 8);

        Cpu.Status |= R5900Cpu.StatusEie | R5900Cpu.StatusExl;
        Cpu.Step();
        Cpu.Pc.Should().Be(Origin + 12, "EXL blocks interrupts");
    }

    [Fact]
    public void Count_reaching_compare_raises_the_timer_interrupt_and_writing_compare_clears_it()
    {
        _machine.Load(Origin, Enumerable.Repeat(0u, 64).ToArray());
        Cpu.Jump(Origin);
        EnableInterrupts(R5900Cpu.StatusIm7);
        Cpu.Count = 0;
        Cpu.WriteCop0(11, 20);
        Cpu.Run(Cpu.Cycles + 19);
        (Cpu.Cause & R5900Cpu.CauseIp7).Should().Be(0);
        Cpu.Run(Cpu.Cycles + 2);
        (Cpu.Cause & R5900Cpu.CauseIp7).Should().NotBe(0);
        Cpu.Pc.Should().Be(0x8000_0200);

        Cpu.WriteCop0(11, 1000);
        (Cpu.Cause & R5900Cpu.CauseIp7).Should().Be(0);
    }

    [Fact]
    public void Count_ticks_once_per_cycle()
    {
        _machine.Load(Origin, Enumerable.Repeat(0u, 200).ToArray());
        Cpu.Jump(Origin);
        Cpu.Count = 100;
        Cpu.Run(Cpu.Cycles + 150);
        Cpu.Count.Should().Be(250);
    }

    [Fact]
    public void Run_stops_early_when_asked()
    {
        _machine.Load(Origin, Ee.Nop, Ee.Syscall, Ee.Nop, Ee.Nop);
        Cpu.Jump(Origin);
        Cpu.ExceptionHook = _ =>
        {
            Cpu.StopRequested = true;
            return true;
        };

        var start = Cpu.Cycles;
        Cpu.Run(start + 1000);
        (Cpu.Cycles - start).Should().Be(2);
        Cpu.Pc.Should().Be(Origin + 8);
    }

    [Fact]
    public void Bc0t_follows_cpcond0()
    {
        Cpu.Cpcond0 = true;
        _machine.Run(Ee.Bc0t(2), Ee.Nop, Ee.Ori(1, 0, 1), Ee.Ori(2, 0, 2));
        Cpu.GetGpr64(1).Should().Be(0);
        Cpu.GetGpr64(2).Should().Be(2);

        Cpu.Cpcond0 = false;
        _machine.Run(Ee.Bc0t(2), Ee.Nop, Ee.Ori(1, 0, 1), Ee.Ori(2, 0, 3));
        Cpu.GetGpr64(1).Should().Be(1);
    }

    // ---- The TLB ----

    /// <summary>EntryLo for a 4 KB-aligned physical address: PFN, cached, dirty, valid, global.</summary>
    private static uint Lo(uint physical, bool dirty = true, bool valid = true) =>
        ((physical >> 12) << 6) | (3 << 3) | (dirty ? 4u : 0) | (valid ? 2u : 0) | 1;

    [Fact]
    public void Tlbwi_maps_even_and_odd_pages_and_tlbr_reads_the_entry_back()
    {
        _machine.Bus.WriteWord(0x5000, 0xAAAA_0001);
        _machine.Bus.WriteWord(0x7000, 0xBBBB_0002);
        Cpu.SetGpr64(1, 0x0040_0000); // VPN2 for $400000/$401000
        Cpu.SetGpr64(2, Lo(0x5000));
        Cpu.SetGpr64(3, Lo(0x7000));
        Cpu.SetGpr64(4, 5);
        _machine.Run(
            Ee.Mtc0(0, 5), // PageMask 4 KB
            Ee.Mtc0(1, 10),
            Ee.Mtc0(2, 2),
            Ee.Mtc0(3, 3),
            Ee.Mtc0(4, 0),
            Ee.Tlbwi,
            Ee.Lui(5, 0x0040),
            Ee.Lw(6, 5, 0),
            Ee.Lw(7, 5, 0x1000),
            Ee.Mtc0(0, 2),
            Ee.Mtc0(0, 3),
            Ee.Tlbr,
            Ee.Mfc0(8, 2),
            Ee.Mfc0(9, 10));

        Cpu.GetGpr64(6).Should().Be(unchecked((ulong)(int)0xAAAA_0001));
        Cpu.GetGpr64(7).Should().Be(unchecked((ulong)(int)0xBBBB_0002));
        ((uint)Cpu.GetGpr64(8)).Should().Be(Lo(0x5000));
        ((uint)Cpu.GetGpr64(9)).Should().Be(0x0040_0000);
    }

    [Fact]
    public void Page_masks_make_large_pages()
    {
        _machine.Bus.WriteWord(0x10_0000 + 0x1234 * 4, 0x5151_5151);
        _machine.Bus.WriteWord(0x20_0000 + 0x10, 0x6262_6262);

        // 1 MB pages: $1000000 → $100000 (even), $1100000 → $200000 (odd).
        Cpu.WriteTlbEntry(3, 0x01FF_E000 & (0xFF << 13), 0x0100_0000, Lo(0x10_0000), Lo(0x20_0000));
        Cpu.SetGpr64(1, 0x0100_0000 + 0x1234 * 4);
        Cpu.SetGpr64(2, 0x0110_0010);
        _machine.Run(Ee.Lw(3, 1, 0), Ee.Lw(4, 2, 0));
        ((uint)Cpu.GetGpr64(3)).Should().Be(0x5151_5151);
        ((uint)Cpu.GetGpr64(4)).Should().Be(0x6262_6262);
    }

    [Fact]
    public void A_missing_mapping_is_a_refill_at_the_refill_vector()
    {
        Cpu.SetGpr64(1, 0x1234_5678);
        _machine.Step(1, Ee.Lw(2, 1, 0));
        ExcCode.Should().Be(0x02, "TLB refill on a load");
        Cpu.Pc.Should().Be(0x8000_0000);
        Cpu.BadVAddr.Should().Be(0x1234_5678);
        (Cpu.ReadCop0(10) & 0xFFFF_E000).Should().Be(0x1234_4000, "EntryHi gets the VPN2");
        Cpu.ReadCop0(4).Should().Be((0x1234_5678u >> 13) << 4, "Context gets BadVPN2");

        // A second refill while EXL is set goes to the common vector and keeps EPC.
        Cpu.SetGpr64(1, 0x2000_0000);
        _machine.Load(0x8000_0000, Ee.Sw(0, 1, 0));
        Cpu.Step();
        ExcCode.Should().Be(0x03, "TLB refill on a store");
        Cpu.Pc.Should().Be(0x8000_0180);
        Cpu.Epc.Should().Be(Origin);
    }

    [Fact]
    public void Invalid_and_clean_pages_raise_tlb_invalid_and_modified()
    {
        Cpu.WriteTlbEntry(1, 0, 0x0040_0000, Lo(0x5000, dirty: false), Lo(0x6000, valid: false));
        Cpu.SetGpr64(1, 0x0040_0000);
        _machine.Run(Ee.Lw(2, 1, 0));
        (Cpu.Status & R5900Cpu.StatusExl).Should().Be(0, "the clean page reads fine");

        _machine.Step(1, Ee.Sw(2, 1, 0));
        ExcCode.Should().Be(0x01, "TLB modified");
        Cpu.Pc.Should().Be(0x8000_0180);

        Cpu.Status &= ~R5900Cpu.StatusExl;
        _machine.Step(1, Ee.Lw(2, 1, 0x1000));
        ExcCode.Should().Be(0x02, "TLB invalid, on a load");
        Cpu.Pc.Should().Be(0x8000_0180, "invalid isn't a refill");
    }

    [Fact]
    public void Tlbp_finds_an_entry_or_sets_the_probe_failure_bit()
    {
        Cpu.WriteTlbEntry(7, 0, 0x0040_0000, Lo(0x5000), Lo(0x6000));
        Cpu.SetGpr64(1, 0x0040_1000);
        Cpu.SetGpr64(2, 0x0050_0000);
        _machine.Run(Ee.Mtc0(1, 10), Ee.Tlbp, Ee.Mfc0(3, 0), Ee.Mtc0(2, 10), Ee.Tlbp, Ee.Mfc0(4, 0));
        Cpu.GetGpr64(3).Should().Be(7);
        ((uint)Cpu.GetGpr64(4)).Should().Be(0x8000_0000);
    }

    [Fact]
    public void Asids_select_non_global_entries()
    {
        _machine.Bus.WriteWord(0x5000, 0x1111);
        // Not global: G clear in both EntryLo values; ASID 5.
        Cpu.WriteTlbEntry(0, 0, 0x0040_0005, Lo(0x5000) & ~1u, Lo(0x6000) & ~1u);
        Cpu.SetGpr64(1, 0x0040_0000);
        Cpu.WriteCop0(10, 5);
        _machine.Run(Ee.Lw(2, 1, 0));
        Cpu.GetGpr64(2).Should().Be(0x1111);

        Cpu.WriteCop0(10, 6);
        _machine.Step(1, Ee.Lw(2, 1, 0));
        ExcCode.Should().Be(0x02, "another ASID doesn't match");
    }

    [Fact]
    public void Tlbwr_writes_the_random_entry_between_wired_and_47()
    {
        Cpu.WriteCop0(6, 40);
        var random = Cpu.ReadCop0(1);
        random.Should().BeInRange(40, 47);
        Cpu.SetGpr64(1, 0x0080_0000);
        Cpu.SetGpr64(2, Lo(0x5000));
        _machine.Run(Ee.Mtc0(0, 5), Ee.Mtc0(1, 10), Ee.Mtc0(2, 2), Ee.Mtc0(2, 3), Ee.Tlbwr);
        Cpu.Tlb.Count(e => e.EntryHi == 0x0080_0000).Should().Be(1);
        Array.FindIndex(Cpu.Tlb, e => e.EntryHi == 0x0080_0000).Should().BeInRange(40, 47);
    }

    [Fact]
    public void The_s_bit_maps_the_scratchpad()
    {
        // The kernel's mapping: 16 KB at $70000000 (PageMask 16 KB, S set in EntryLo0).
        Cpu.WriteTlbEntry(1, 0x0000_6000, 0x7000_0000, 0x8000_0007, 0x0000_0007);
        Cpu.SetGpr64(1, 0x7000_3FF0);
        Cpu.SetGpr(2, new UInt128(0xDEAD_BEEF_0000_0001, 0x0123_4567_89AB_CDEF));
        _machine.Run(Ee.Sq(2, 1, 0), Ee.Ld(3, 1, 8));
        Cpu.GetGpr64(3).Should().Be(0xDEAD_BEEF_0000_0001);
        BitConverter.ToUInt64(Cpu.Scratchpad, 0x3FF0).Should().Be(0x0123_4567_89AB_CDEF);
        _machine.Bus.IoWrites.Should().BeEmpty();
        _machine.Bus.Rdram.Skip(0x3FF0).Take(16).Should().OnlyContain(b => b == 0, "RDRAM isn't touched");
    }

    // ---- COP2 ----

    [Fact]
    public void Cop2_transfers_move_whole_vf_registers_and_wait_with_the_interlock_bit()
    {
        var vu = _machine.Cop2;
        vu.Vf[3] = new UInt128(0x4000_0000_3F80_0000, 0x4040_0000_4080_0000);
        vu.Control[21] = 0x3F80_0000;
        vu.MicroRunning = true;
        _machine.Run(Ee.Qmfc2(1, 3), Ee.Cfc2(2, 21));
        vu.Waits.Should().Be(0, "without the I bit the transfers don't wait");
        Cpu.GetGpr(1).Should().Be(vu.Vf[3]);
        Cpu.GetGpr64(2).Should().Be(0x3F80_0000);

        _machine.Run(Ee.Bc2(1, 2), Ee.Nop, Ee.Ori(4, 0, 1));
        Cpu.GetGpr64(4).Should().Be(0, "BC2T: VU0 is running");

        Cpu.SetGpr(5, new UInt128(1, 2));
        _machine.Run(Ee.Qmtc2(5, 7, interlock: true), Ee.Ctc2(5, 27));
        vu.Waits.Should().Be(1);
        vu.Vf[7].Should().Be(new UInt128(1, 2));
        vu.Control[27].Should().Be(2);

        _machine.Run(0x4A00_0000u | 0x2C); // a CO-bit instruction (VADD.xyzw)
        vu.Macros.Should().Equal(0x4A00_002Cu);
    }

    [Fact]
    public void Lqc2_and_sqc2_move_quadwords_between_memory_and_vu0()
    {
        var vu = _machine.Cop2;
        vu.Vf[9] = new UInt128(0x1111, 0x2222);
        Cpu.SetGpr64(1, 0x8002_0000);
        _machine.Run(Ee.Sqc2(9, 1, 0x10), Ee.Lqc2(10, 1, 0x1C));
        vu.Vf[10].Should().Be(new UInt128(0x1111, 0x2222));
    }

    // ---- Speed helpers and save states ----

    [Fact]
    public void An_idle_branch_to_itself_skips_to_the_end_of_the_slice()
    {
        _machine.Load(Origin, Ee.Beq(0, 0, -1), Ee.Nop);
        Cpu.Jump(Origin);
        var start = Cpu.Cycles;
        Cpu.Run(start + 1_000_000);
        Cpu.Cycles.Should().BeGreaterThanOrEqualTo(start + 1_000_000);
        Cpu.Pc.Should().BeOneOf(Origin, Origin + 4);
    }

    [Fact]
    public void Instructions_are_fetched_straight_from_the_bios_when_the_bus_offers_it()
    {
        var bus = new FlatR5900Bus { Bios = new byte[4 * 1024 * 1024] };
        BitConverter.TryWriteBytes(bus.Bios.AsSpan(0), Ee.Ori(1, 0, 0x1234));
        BitConverter.TryWriteBytes(bus.Bios.AsSpan(4), Ee.Addiu(2, 1, 1));
        var cpu = new R5900Cpu(bus);
        cpu.Step();
        cpu.Step();
        cpu.GetGpr64(2).Should().Be(0x1235);
        bus.Reads32.Should().Be(0);
    }

    [Fact]
    public void A_compare_written_inside_a_slice_still_interrupts_on_time()
    {
        // MTC0 Compare = Count + 10 inside a long Run: the interrupt comes 10 cycles later, not at the slice's end.
        _machine.Load(Origin, [Ee.Mfc0(1, 9), Ee.Addiu(1, 1, 10), Ee.Mtc0(1, 11), .. Enumerable.Repeat(0u, 64)]);
        Cpu.Jump(Origin);
        EnableInterrupts(R5900Cpu.StatusIm7);
        Cpu.WriteCop0(11, 0xFFFF_0000);
        Cpu.Run(Cpu.Cycles + 40);
        Cpu.Epc.Should().BeInRange(Origin + 8 + 4 * 9, Origin + 8 + 4 * 11);
    }

    [Fact]
    public void Save_and_load_round_trip_the_whole_cpu()
    {
        Cpu.WriteTlbEntry(1, 0x0000_6000, 0x7000_0000, 0x8000_0007, 0x0000_0007);
        Cpu.SetGpr64(1, 0x7000_0000);
        _machine.Run(Ee.Ori(2, 0, 0x55), Ee.Sw(2, 1, 0), Ee.Mult(3, 2, 2), Ee.Mtc1(2, 4));
        Cpu.Hi1 = 0x1234;
        Cpu.Sa = 7;
        Cpu.Int0 = true;
        Cpu.WriteCop0(11, 0x4000);

        var writer = new StateWriter();
        Cpu.SaveState(writer);
        var saved = writer.ToArray();

        var other = new R5900TestMachine();
        other.Cpu.LoadState(new StateReader(saved));
        var again = new StateWriter();
        other.Cpu.SaveState(again);
        again.ToArray().Should().Equal(saved);

        other.Cpu.GetGpr64(3).Should().Be(0x55 * 0x55);
        other.Cpu.Hi1.Should().Be(0x1234);
        other.Cpu.Sa.Should().Be(7);
        other.Cpu.GetFpr(4).Should().Be(0x55);
        other.Cpu.Int0.Should().BeTrue();
        other.Cpu.Pc.Should().Be(Cpu.Pc);

        // The TLB's page table is rebuilt: the scratchpad mapping and its contents survive.
        other.Cpu.SetGpr64(1, 0x7000_0000);
        other.Run(Ee.Lw(4, 1, 0));
        other.Cpu.GetGpr64(4).Should().Be(0x55);
    }
}
