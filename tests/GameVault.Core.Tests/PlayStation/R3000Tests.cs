using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.Cpu.R3000;
using GameVault.Core.State;

namespace GameVault.Core.Tests.PlayStation;

/// <summary>A flat 2 MB RAM at physical 0 (mirrored), for running hand-assembled R3000A programs.</summary>
internal sealed class FlatR3000Bus : IR3000Bus
{
    public readonly byte[] Ram = new byte[2 * 1024 * 1024];

    public int Fetches;

    private int Offset(uint address) => (int)(address & 0x1FFFFF);

    public uint Read32(uint address) => BinaryPrimitives.ReadUInt32LittleEndian(Ram.AsSpan(Offset(address)));

    public ushort Read16(uint address) => BinaryPrimitives.ReadUInt16LittleEndian(Ram.AsSpan(Offset(address)));

    public byte Read8(uint address) => Ram[Offset(address)];

    public void Write32(uint address, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Ram.AsSpan(Offset(address)), value);

    public void Write16(uint address, uint value) => BinaryPrimitives.WriteUInt16LittleEndian(Ram.AsSpan(Offset(address)), (ushort)value);

    public void Write8(uint address, uint value) => Ram[Offset(address)] = (byte)value;

    public uint Fetch(uint address)
    {
        Fetches++;
        return Read32(address);
    }
}

/// <summary>Encodes R3000A instructions for tests (register numbers as in MIPS: 1 = at, 31 = ra).</summary>
internal static class Mips
{
    public static uint R(uint funct, int rs = 0, int rt = 0, int rd = 0, int shamt = 0) =>
        (uint)(rs << 21 | rt << 16 | rd << 11 | shamt << 6) | funct;

    public static uint I(uint op, int rs, int rt, int imm) => op << 26 | (uint)(rs << 21 | rt << 16) | (ushort)imm;

    public static uint Lui(int rt, int imm) => I(0x0F, 0, rt, imm);

    public static uint Ori(int rt, int rs, int imm) => I(0x0D, rs, rt, imm);

    public static uint Addiu(int rt, int rs, int imm) => I(0x09, rs, rt, imm);

    public static uint Addi(int rt, int rs, int imm) => I(0x08, rs, rt, imm);

    public static uint Addu(int rd, int rs, int rt) => R(0x21, rs, rt, rd);

    public static uint Add(int rd, int rs, int rt) => R(0x20, rs, rt, rd);

    public static uint Lw(int rt, int rs, int offset) => I(0x23, rs, rt, offset);

    public static uint Lwl(int rt, int rs, int offset) => I(0x22, rs, rt, offset);

    public static uint Lwr(int rt, int rs, int offset) => I(0x26, rs, rt, offset);

    public static uint Sw(int rt, int rs, int offset) => I(0x2B, rs, rt, offset);

    public static uint Beq(int rs, int rt, int offset) => I(0x04, rs, rt, offset);

    public static uint Bltzal(int rs, int offset) => I(0x01, rs, 0x10, offset);

    public static uint Mult(int rs, int rt) => R(0x18, rs, rt);

    public static uint Div(int rs, int rt) => R(0x1A, rs, rt);

    public static uint Mflo(int rd) => R(0x12, rd: rd);

    public static uint Mfhi(int rd) => R(0x10, rd: rd);

    public static uint Mtc0(int rt, int rd) => 0x40800000u | (uint)(rt << 16 | rd << 11);

    public static uint Mfc0(int rt, int rd) => 0x40000000u | (uint)(rt << 16 | rd << 11);

    public static uint Rfe => 0x42000010;

    public static uint Mtc2(int rt, int rd) => 0x48800000u | (uint)(rt << 16 | rd << 11);

    public static uint Mfc2(int rt, int rd) => 0x48000000u | (uint)(rt << 16 | rd << 11);

    public static uint Ctc2(int rt, int rd) => 0x48C00000u | (uint)(rt << 16 | rd << 11);

    public static uint Cfc2(int rt, int rd) => 0x48400000u | (uint)(rt << 16 | rd << 11);

    /// <summary>A GTE command: <paramref name="opcode"/> with the sf (shift 12) and lm bits.</summary>
    public static uint Cop2(uint opcode, bool sf = true, bool lm = false) =>
        0x4A000000u | (sf ? 1u << 19 : 0) | (lm ? 1u << 10 : 0) | opcode;

    public static uint Syscall => 0x0000000C;

    public static uint Nop => 0;
}

/// <summary>The R3000A CPU (SPEC §7.11, Phase 15), with programs assembled in the tests.</summary>
public sealed class R3000Tests
{
    private const uint Origin = 0x80010000;

    private readonly FlatR3000Bus _bus = new();
    private readonly R3000Cpu _cpu;
    private readonly List<R3000Exception> _exceptions = [];

    public R3000Tests()
    {
        _cpu = new R3000Cpu(_bus);
        _cpu.Reset();
        _cpu.Sr = 0; // BEV clear: exceptions go to $80000080.
        _cpu.ExceptionHook = _exceptions.Add;
    }

    private void Load(uint address, params uint[] program)
    {
        for (var i = 0; i < program.Length; i++)
        {
            _bus.Write32((address & 0x1FFFFFFF) + (uint)(i * 4), program[i]);
        }
    }

    private void Run(params uint[] program)
    {
        Load(Origin, program);
        _cpu.Jump(Origin);
        for (var i = 0; i < program.Length; i++)
        {
            _cpu.Step();
        }
    }

    private uint Reg(int index) => _cpu.GetRegister(index);

    // ---- Delay slots ----

    [Fact]
    public void A_load_reaches_its_register_after_the_next_instruction()
    {
        _bus.Write32(0x1000, 0x12345678);
        _cpu.SetRegister(1, 0xAAAA);
        Run(
            Mips.Lw(1, 0, 0x1000),
            Mips.Addu(2, 1, 0), // the delay slot sees the old value
            Mips.Addu(3, 1, 0));

        Reg(2).Should().Be(0xAAAA);
        Reg(3).Should().Be(0x12345678);
    }

    [Fact]
    public void An_alu_write_in_the_load_delay_slot_wins_over_the_load()
    {
        _bus.Write32(0x1000, 0x12345678);
        Run(
            Mips.Lw(1, 0, 0x1000),
            Mips.Ori(1, 0, 0x55),
            Mips.Nop);

        Reg(1).Should().Be(0x55, "psx-spx: the load is discarded when its delay slot writes the same register");
    }

    [Fact]
    public void Lwl_and_lwr_merge_an_unaligned_word_through_the_load_delay()
    {
        _bus.Write32(0x1000, 0x44332211);
        _bus.Write32(0x1004, 0x88776655);
        Run(
            Mips.Lwr(1, 0, 0x1001), // the low bytes from $1001…
            Mips.Lwl(1, 0, 0x1004), // …merged with the high bytes, while the LWR is still landing
            Mips.Nop);

        Reg(1).Should().Be(0x55443322);
    }

    [Fact]
    public void The_delay_slot_runs_and_bltzal_links_even_when_not_taken()
    {
        _cpu.SetRegister(4, 1);
        Run(
            Mips.Beq(0, 0, 2), // to Origin + 12
            Mips.Ori(5, 0, 7), // delay slot
            Mips.Ori(6, 0, 9), // skipped
            Mips.Bltzal(4, 4)); // 1 isn't < 0: not taken, but ra is still written

        Reg(5).Should().Be(7);
        Reg(6).Should().Be(0);
        Reg(31).Should().Be(Origin + 20);
        _cpu.Pc.Should().Be(Origin + 20, "not taken: execution falls through");
    }

    // ---- Exceptions ----

    [Fact]
    public void Add_overflow_raises_an_exception_without_writing_the_result()
    {
        _cpu.SetRegister(1, 0x7FFFFFFF);
        Run(
            Mips.Ori(3, 0, 1),
            Mips.Add(2, 1, 3));

        Reg(2).Should().Be(0);
        _exceptions.Should().Equal(R3000Exception.Overflow);
        _cpu.Epc.Should().Be(Origin + 4);
        _cpu.Pc.Should().Be(0x80000080);
        ((_cpu.Cause >> 2) & 0x1F).Should().Be(12);
    }

    [Fact]
    public void An_exception_in_a_delay_slot_points_epc_at_the_branch()
    {
        _cpu.SetRegister(1, 0x7FFFFFFF);
        Run(
            Mips.Beq(0, 0, 4),
            Mips.Addi(2, 1, 1));

        _exceptions.Should().Equal(R3000Exception.Overflow);
        _cpu.Epc.Should().Be(Origin);
        (_cpu.Cause & 0x80000000).Should().NotBe(0, "CAUSE.BD marks a delay slot");
        (_cpu.Cause & 0x40000000).Should().NotBe(0, "CAUSE.BT: the branch was taken");
    }

    [Fact]
    public void Unaligned_loads_raise_an_address_error_with_badvaddr()
    {
        Run(Mips.Lw(1, 0, 0x1002));

        _exceptions.Should().Equal(R3000Exception.AddressErrorLoad);
        _cpu.BadVaddr.Should().Be(0x1002);
    }

    [Fact]
    public void Bev_moves_the_exception_vector_into_the_bios()
    {
        _cpu.Sr = R3000Cpu.SrBev;
        Run(Mips.Syscall);

        _cpu.Pc.Should().Be(0xBFC00180);
    }

    [Fact]
    public void Exceptions_push_and_rfe_pops_the_interrupt_enable_stack()
    {
        _cpu.Sr = 0b000001; // IEc
        Run(Mips.Syscall);
        (_cpu.Sr & 0x3F).Should().Be(0b000100, "IEc moves to IEp, and the CPU runs in kernel mode with interrupts off");

        Load(0x80000080, Mips.Rfe);
        _cpu.Step();
        (_cpu.Sr & 0x3F).Should().Be(0b000001);
    }

    [Fact]
    public void A_hardware_interrupt_is_taken_when_enabled_and_unmasked()
    {
        _cpu.Sr = 0x401; // IEc and IM2
        Load(Origin, Mips.Nop, Mips.Nop);
        _cpu.Jump(Origin);
        _cpu.Step();
        _exceptions.Should().BeEmpty();

        _cpu.InterruptLine = true;
        _cpu.Step();
        _exceptions.Should().Equal(R3000Exception.Interrupt);
        _cpu.Epc.Should().Be(Origin + 4, "the interrupted instruction runs again after RFE");
    }

    // ---- Multiplier and divider ----

    [Fact]
    public void Division_by_zero_and_overflow_give_the_documented_results()
    {
        _cpu.SetRegister(1, unchecked((uint)-5));
        _cpu.SetRegister(2, 0x80000000);
        _cpu.SetRegister(3, 0xFFFFFFFF);
        Run(
            Mips.Div(1, 0),
            Mips.Mflo(4),
            Mips.Mfhi(5),
            Mips.Div(2, 3),
            Mips.Mflo(6),
            Mips.Mfhi(7));

        Reg(4).Should().Be(1, "a negative dividend over zero gives 1");
        Reg(5).Should().Be(unchecked((uint)-5));
        Reg(6).Should().Be(0x80000000);
        Reg(7).Should().Be(0);
    }

    [Fact]
    public void Mflo_waits_for_the_multiplier()
    {
        _cpu.SetRegister(1, 3);
        _cpu.SetRegister(2, 0x01000000);
        Run(Mips.Mult(1, 1), Mips.Mflo(3));
        var small = _cpu.Cycles;

        var cpu2 = new R3000Cpu(_bus);
        cpu2.Reset();
        cpu2.SetRegister(1, 0x01000000);
        cpu2.Jump(Origin);
        cpu2.Step();
        cpu2.Step();

        Reg(3).Should().Be(9);
        small.Should().Be(1 + 6, "a small operand finishes in 6 cycles");
        cpu2.Cycles.Should().Be(1 + 13, "a large operand takes 13");
    }

    // ---- Coprocessors ----

    [Fact]
    public void Coprocessors_follow_their_usable_bits()
    {
        Run(Mips.Mfc2(1, 0));
        _exceptions.Should().Equal(R3000Exception.CoprocessorUnusable);
        ((_cpu.Cause >> 28) & 3).Should().Be(2);

        _exceptions.Clear();
        _cpu.Sr = 0xF0000000;
        Run(
            0x44000000, // COP1 with CU1 set: nothing
            0x4FE00000, // COP3 with CU3 set: nothing
            0xE0010000); // SWC0 with CU0 set: nothing
        _exceptions.Should().BeEmpty();

        _cpu.Sr = 0;
        Run(0xE0010000);
        _exceptions.Should().Equal([R3000Exception.CoprocessorUnusable], "SWC0 needs CU0 even in kernel mode (ps1-tests cpu/cop)");
    }

    [Fact]
    public void Mfc0_reads_the_processor_id_through_the_load_delay()
    {
        _cpu.SetRegister(1, 0xDEAD);
        Run(
            Mips.Mfc0(1, 15),
            Mips.Addu(2, 1, 0),
            Mips.Addu(3, 1, 0));

        Reg(2).Should().Be(0xDEAD);
        Reg(3).Should().Be(R3000Cpu.ProcessorId);
    }

    [Fact]
    public void Stores_with_the_cache_isolated_never_reach_memory()
    {
        _cpu.Sr = R3000Cpu.SrIsolateCache;
        _cpu.SetRegister(1, 0x1234);
        Run(Mips.Sw(1, 0, 0x2000));

        _bus.Read32(0x2000).Should().Be(0);
    }

    [Fact]
    public void Cached_code_keeps_running_until_the_cache_is_flushed()
    {
        _cpu.CacheControl = 0x800;
        Load(Origin, Mips.Ori(1, 0, 1), Mips.Beq(0, 0, -2), Mips.Nop);
        _cpu.Jump(Origin);
        for (var i = 0; i < 3; i++)
        {
            _cpu.Step();
        }

        var fetches = _bus.Fetches;
        Load(Origin, Mips.Ori(1, 0, 2)); // patched behind the cache's back
        _cpu.Step();

        Reg(1).Should().Be(1, "the instruction comes from the cache");
        _bus.Fetches.Should().Be(fetches);
    }

    // ---- GTE ----

    [Fact]
    public void Gte_registers_have_their_read_and_write_quirks()
    {
        var gte = _cpu.Gte;
        gte.WriteData(28, 0x7FFF); // IRGB → IR1..IR3
        gte.ReadData(9).Should().Be(0x1F << 7);
        gte.ReadData(29).Should().Be(0x7FFF, "ORGB reads back IR / 0x80");

        gte.WriteData(30, 0x0000F000);
        gte.ReadData(31).Should().Be(16, "LZCR counts leading zeros");
        gte.WriteData(30, 0xFFFF0000);
        gte.ReadData(31).Should().Be(16, "…or leading ones of a negative LZCS");

        gte.WriteData(15, 0x00020001);
        gte.WriteData(15, 0x00040003);
        gte.ReadData(13).Should().Be(0x00020001, "SXYP pushes the screen-coordinate FIFO");
        gte.ReadData(14).Should().Be(0x00040003);

        gte.WriteControl(26, 0x8000);
        gte.ReadControl(26).Should().Be(0xFFFF8000, "H reads sign-extended");

        gte.WriteControl(31, 0xFFFFFFFF);
        gte.ReadControl(31).Should().Be(0xFFFFF000);
    }

    [Fact]
    public void Rtps_projects_a_vertex_and_avsz3_averages_depth()
    {
        // Identity rotation (1.0 = 0x1000), translation (0, 0, 1000), H = 1000, screen offset (160, 120).
        _cpu.Sr = R3000Cpu.SrCu2;
        var gte = _cpu.Gte;
        gte.WriteControl(0, 0x1000);
        gte.WriteControl(2, 0x1000);
        gte.WriteControl(4, 0x1000);
        gte.WriteControl(7, 1000);
        gte.WriteControl(24, 160 << 16);
        gte.WriteControl(25, 120 << 16);
        gte.WriteControl(26, 1000);
        gte.WriteData(0, 100u | 0xFFCEu << 16); // VX0 = 100, VY0 = −50
        gte.WriteData(1, 0);

        Run(Mips.Cop2(0x01)); // RTPS, sf=1

        gte.ReadData(19).Should().Be(1000, "SZ3 is the depth");
        var sxy = gte.ReadData(14);
        ((short)sxy).Should().Be(260, "x: 100 × 1000 / 1000 + 160");
        ((short)(sxy >> 16)).Should().Be(70, "y: −50 + 120");
        gte.ReadControl(31).Should().Be(0);

        gte.WriteData(17, 30);
        gte.WriteData(18, 60);
        gte.WriteData(19, 90);
        gte.WriteControl(29, 0x1000 / 3);
        gte.Execute(Mips.Cop2(0x2D));
        gte.ReadData(7).Should().Be(59, "OTZ = (30 + 60 + 90) × ZSF3 / 0x1000");
    }

    [Fact]
    public void Nclip_gives_the_winding_of_the_screen_triangle()
    {
        var gte = _cpu.Gte;
        gte.WriteData(12, 0);
        gte.WriteData(13, 10);
        gte.WriteData(14, 10u << 16);
        gte.Execute(Mips.Cop2(0x06));

        ((int)gte.ReadData(24)).Should().Be(100);
    }

    [Fact]
    public void Rtps_flags_a_division_overflow()
    {
        var gte = _cpu.Gte;
        gte.WriteControl(26, 1000);
        gte.Execute(Mips.Cop2(0x01)); // depth 0

        (gte.ReadControl(31) & (1 << 17)).Should().NotBe(0);
        (gte.ReadControl(31) & 0x80000000).Should().NotBe(0);
    }

    // ---- State ----

    [Fact]
    public void Save_states_round_trip_mid_delay_slot()
    {
        _bus.Write32(0x1000, 0xCAFE);
        Load(Origin, Mips.Lw(1, 0, 0x1000), Mips.Addu(2, 1, 0), Mips.Addu(3, 1, 0));
        _cpu.Jump(Origin);
        _cpu.Step();

        var writer = new StateWriter();
        _cpu.SaveState(writer);
        var copy = new R3000Cpu(_bus);
        copy.LoadState(new StateReader(writer.ToArray()));
        copy.Step();
        copy.Step();

        copy.GetRegister(2).Should().Be(0, "the load was still pending when the state was saved");
        copy.GetRegister(3).Should().Be(0xCAFE);
    }
}
