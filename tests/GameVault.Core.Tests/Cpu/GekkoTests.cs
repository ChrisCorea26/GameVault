using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.Cpu.Gekko;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Cpu;

/// <summary>PowerPC instruction words for test programs.</summary>
internal static class Ppc
{
    private static uint D(uint op, int d, int a, int imm) => (op << 26) | ((uint)d << 21) | ((uint)a << 16) | ((uint)imm & 0xFFFF);

    private static uint X(int d, int a, int b, int xo, bool rc = false, bool oe = false) =>
        (31u << 26) | ((uint)d << 21) | ((uint)a << 16) | ((uint)b << 11) | (oe ? 0x400u : 0) | ((uint)xo << 1) | (rc ? 1u : 0u);

    public static uint Addi(int d, int a, int imm) => D(14, d, a, imm);

    public static uint Addis(int d, int a, int imm) => D(15, d, a, imm);

    public static uint Ori(int a, int s, int imm) => D(24, s, a, imm);

    public static uint Addic(int d, int a, int imm) => D(12, d, a, imm);

    public static uint Mulli(int d, int a, int imm) => D(7, d, a, imm);

    public static uint Cmpwi(int field, int a, int imm) => D(11, field << 2, a, imm);

    public static uint Cmplwi(int field, int a, int imm) => D(10, field << 2, a, imm);

    public static uint Lwz(int d, int a, int offset) => D(32, d, a, offset);

    public static uint Lwzu(int d, int a, int offset) => D(33, d, a, offset);

    public static uint Lbz(int d, int a, int offset) => D(34, d, a, offset);

    public static uint Stw(int s, int a, int offset) => D(36, s, a, offset);

    public static uint Stwu(int s, int a, int offset) => D(37, s, a, offset);

    public static uint Stb(int s, int a, int offset) => D(38, s, a, offset);

    public static uint Lhz(int d, int a, int offset) => D(40, d, a, offset);

    public static uint Lha(int d, int a, int offset) => D(42, d, a, offset);

    public static uint Sth(int s, int a, int offset) => D(44, s, a, offset);

    public static uint Lmw(int d, int a, int offset) => D(46, d, a, offset);

    public static uint Stmw(int s, int a, int offset) => D(47, s, a, offset);

    public static uint Lfs(int d, int a, int offset) => D(48, d, a, offset);

    public static uint Lfd(int d, int a, int offset) => D(50, d, a, offset);

    public static uint Stfs(int s, int a, int offset) => D(52, s, a, offset);

    public static uint Stfd(int s, int a, int offset) => D(54, s, a, offset);

    public static uint Add(int d, int a, int b, bool rc = false, bool oe = false) => X(d, a, b, 266, rc, oe);

    public static uint Addc(int d, int a, int b) => X(d, a, b, 10);

    public static uint Adde(int d, int a, int b) => X(d, a, b, 138);

    public static uint Subf(int d, int a, int b, bool rc = false) => X(d, a, b, 40, rc);

    public static uint Neg(int d, int a) => X(d, a, 0, 104);

    public static uint Mullw(int d, int a, int b) => X(d, a, b, 235);

    public static uint Mulhw(int d, int a, int b) => X(d, a, b, 75);

    public static uint Mulhwu(int d, int a, int b) => X(d, a, b, 11);

    public static uint Divw(int d, int a, int b) => X(d, a, b, 491);

    public static uint Divwu(int d, int a, int b) => X(d, a, b, 459);

    public static uint Or(int a, int s, int b) => X(s, a, b, 444);

    public static uint And(int a, int s, int b) => X(s, a, b, 28);

    public static uint Slw(int a, int s, int b) => X(s, a, b, 24);

    public static uint Srw(int a, int s, int b) => X(s, a, b, 536);

    public static uint Sraw(int a, int s, int b) => X(s, a, b, 792);

    public static uint Srawi(int a, int s, int sh) => X(s, a, sh, 824);

    public static uint Cntlzw(int a, int s) => X(s, a, 0, 26);

    public static uint Extsb(int a, int s) => X(s, a, 0, 954);

    public static uint Lwbrx(int d, int a, int b) => X(d, a, b, 534);

    public static uint Lwzx(int d, int a, int b) => X(d, a, b, 23);

    public static uint Stswi(int s, int a, int n) => X(s, a, n, 725);

    public static uint Lswi(int d, int a, int n) => X(d, a, n, 597);

    public static uint Dcbz(int a, int b) => X(0, a, b, 1014);

    public static uint Rlwinm(int a, int s, int sh, int mb, int me, bool rc = false) =>
        (21u << 26) | ((uint)s << 21) | ((uint)a << 16) | ((uint)sh << 11) | ((uint)mb << 6) | ((uint)me << 1) | (rc ? 1u : 0u);

    public static uint Rlwimi(int a, int s, int sh, int mb, int me) =>
        (20u << 26) | ((uint)s << 21) | ((uint)a << 16) | ((uint)sh << 11) | ((uint)mb << 6) | ((uint)me << 1);

    public static uint Mfspr(int d, int spr) => X(d, spr & 31, spr >> 5, 339);

    public static uint Mtspr(int spr, int s) => X(s, spr & 31, spr >> 5, 467);

    public static uint Mfmsr(int d) => X(d, 0, 0, 83);

    public static uint Mtmsr(int s) => X(s, 0, 0, 146);

    public static uint Mfcr(int d) => X(d, 0, 0, 19);

    public static uint B(int offset, bool link = false) => (18u << 26) | ((uint)offset & 0x03FFFFFC) | (link ? 1u : 0u);

    public static uint Bc(int bo, int bi, int offset) => (16u << 26) | ((uint)bo << 21) | ((uint)bi << 16) | ((uint)offset & 0xFFFC);

    /// <summary>bdnz: decrement CTR, branch while it isn't zero.</summary>
    public static uint Bdnz(int offset) => Bc(16, 0, offset);

    public static uint Blr() => 0x4E800020;

    public static uint Bctr() => 0x4E800420;

    public static uint Rfi() => 0x4C000064;

    public static uint Sc() => 0x44000002;

    public static uint Fp(int xo, int d, int a, int b, int c = 0, uint primary = 63) =>
        (primary << 26) | ((uint)d << 21) | ((uint)a << 16) | ((uint)b << 11) | ((uint)c << 6) | ((uint)xo << 1);

    public static uint Fpx(int xo, int d, int a, int b) =>
        (63u << 26) | ((uint)d << 21) | ((uint)a << 16) | ((uint)b << 11) | ((uint)xo << 1);

    public static uint Ps(int xo, int d, int a, int b, int c = 0) => Fp(xo, d, a, b, c, 4);

    public static uint Psx(int xo, int d, int a, int b) =>
        (4u << 26) | ((uint)d << 21) | ((uint)a << 16) | ((uint)b << 11) | ((uint)xo << 1);

    public static uint PsqL(int d, int a, int offset, bool w, int i) =>
        (56u << 26) | ((uint)d << 21) | ((uint)a << 16) | (w ? 0x8000u : 0) | ((uint)i << 12) | ((uint)offset & 0xFFF);

    public static uint PsqSt(int s, int a, int offset, bool w, int i) =>
        (60u << 26) | ((uint)s << 21) | ((uint)a << 16) | (w ? 0x8000u : 0) | ((uint)i << 12) | ((uint)offset & 0xFFF);
}

public sealed class GekkoTests
{
    private sealed class Ram : IGekkoBus
    {
        public byte[] Bytes { get; } = new byte[0x100000];

        public bool ExternalInterrupt { get; set; }

        public byte Read8(uint address) => Bytes[address & 0xFFFFF];

        public ushort Read16(uint address) => BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan((int)(address & 0xFFFFE)));

        public uint Read32(uint address) => BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)(address & 0xFFFFC)));

        public void Write8(uint address, byte value) => Bytes[address & 0xFFFFF] = value;

        public void Write16(uint address, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Bytes.AsSpan((int)(address & 0xFFFFE)), value);

        public void Write32(uint address, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan((int)(address & 0xFFFFC)), value);
    }

    /// <summary>A Gekko with <paramref name="program"/> at $3000, translation off and the FPU on.</summary>
    private static (GekkoCpu Cpu, Ram Ram) Machine(params uint[] program)
    {
        var ram = new Ram();
        for (var i = 0; i < program.Length; i++)
        {
            ram.Write32(0x3000 + (uint)i * 4, program[i]);
        }

        var cpu = new GekkoCpu(ram);
        cpu.Reset();
        cpu.Pc = 0x3000;
        cpu.Msr = 0x2000; // FP on, exception vectors at 0
        return (cpu, ram);
    }

    private static void Run(GekkoCpu cpu, int count)
    {
        for (var i = 0; i < count; i++)
        {
            cpu.Step();
        }
    }

    [Fact]
    public void Immediates_and_record_forms_set_cr0()
    {
        var (cpu, _) = Machine(
            Ppc.Addis(3, 0, 0x1234),
            Ppc.Ori(3, 3, 0x5678),
            Ppc.Addi(4, 0, -1),
            Ppc.Add(5, 3, 4, rc: true),
            Ppc.Subf(6, 3, 3, rc: true));
        Run(cpu, 4);

        cpu.GetGpr(3).Should().Be(0x12345678);
        cpu.GetGpr(5).Should().Be(0x12345677);
        (cpu.Cr >> 28).Should().Be(4, "CR0: greater than zero");
        Run(cpu, 1);
        (cpu.Cr >> 28).Should().Be(2, "CR0: equal to zero");
    }

    [Fact]
    public void Carries_chain_a_64_bit_add()
    {
        // (r3:r4) + (r5:r6) = $00000001_FFFFFFFF + $00000000_00000001.
        var (cpu, _) = Machine(Ppc.Addc(8, 4, 6), Ppc.Adde(7, 3, 5));
        cpu.SetGpr(3, 1);
        cpu.SetGpr(4, 0xFFFFFFFF);
        cpu.SetGpr(5, 0);
        cpu.SetGpr(6, 1);

        Run(cpu, 2);

        cpu.GetGpr(7).Should().Be(2);
        cpu.GetGpr(8).Should().Be(0);
    }

    [Fact]
    public void Overflow_sets_ov_and_sticky_so()
    {
        var (cpu, _) = Machine(Ppc.Add(5, 3, 3, oe: true), Ppc.Add(6, 4, 4, oe: true));
        cpu.SetGpr(3, 0x7FFFFFFF);
        cpu.SetGpr(4, 1);

        Run(cpu, 1);
        (cpu.Xer >> 30).Should().Be(3, "OV and SO");
        Run(cpu, 1);
        (cpu.Xer >> 30).Should().Be(2, "OV clears, SO stays");
    }

    [Theory]
    [InlineData(235, 7u, unchecked((uint)-3), unchecked((uint)-21))] // mullw
    [InlineData(75, 0x40000000u, 8u, 2u)] // mulhw
    [InlineData(11, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFEu)] // mulhwu
    [InlineData(491, unchecked((uint)-100), 7u, unchecked((uint)-14))] // divw
    [InlineData(459, 0xFFFFFFFFu, 16u, 0x0FFFFFFFu)] // divwu
    [InlineData(444, 0xF0u, 0x0Fu, 0xFFu)] // or
    [InlineData(28, 0xF0u, 0x3Cu, 0x30u)] // and
    public void Two_register_arithmetic_and_logic(int xo, uint a, uint b, uint expected)
    {
        var op = xo is 444 or 28 ? xo == 444 ? Ppc.Or(5, 3, 4) : Ppc.And(5, 3, 4) : (31u << 26) | (5u << 21) | (3u << 16) | (4u << 11) | ((uint)xo << 1);
        var (cpu, _) = Machine(op);
        cpu.SetGpr(3, a);
        cpu.SetGpr(4, b);

        Run(cpu, 1);

        cpu.GetGpr(5).Should().Be(expected);
    }

    [Fact]
    public void Rotates_masks_and_shifts()
    {
        var (cpu, _) = Machine(
            Ppc.Rlwinm(4, 3, 8, 24, 31), // extract the top byte
            Ppc.Rlwimi(5, 3, 0, 16, 23), // insert bits 16–23 of r3
            Ppc.Slw(6, 3, 7),
            Ppc.Srawi(8, 9, 4),
            Ppc.Cntlzw(10, 7),
            Ppc.Extsb(11, 12));
        cpu.SetGpr(3, 0xAABBCCDD);
        cpu.SetGpr(5, 0x11111111);
        cpu.SetGpr(7, 4);
        cpu.SetGpr(9, unchecked((uint)-17));
        cpu.SetGpr(12, 0x80);

        Run(cpu, 6);

        cpu.GetGpr(4).Should().Be(0xAA);
        cpu.GetGpr(5).Should().Be(0x1111CC11);
        cpu.GetGpr(6).Should().Be(0xABBCCDD0);
        cpu.GetGpr(8).Should().Be(unchecked((uint)-2));
        (cpu.Xer & 0x20000000).Should().NotBe(0, "srawi of a negative number that loses 1 bits sets CA");
        cpu.GetGpr(10).Should().Be(29);
        cpu.GetGpr(11).Should().Be(0xFFFFFF80);
    }

    [Fact]
    public void Bdnz_loops_and_bl_blr_link()
    {
        var (cpu, _) = Machine(
            Ppc.Addi(3, 0, 5),
            Ppc.Mtspr(9, 3), // CTR = 5
            Ppc.Addi(4, 4, 2), // $3008: loop body
            Ppc.Bdnz(-4),
            Ppc.B(12, link: true), // $3010 → $301C
            Ppc.Addi(6, 0, 1), // $3014: after the call
            Ppc.B(0), // $3018: stay
            Ppc.Addi(5, 0, 9), // $301C: the call
            Ppc.Blr());
        Run(cpu, 2 + 10 + 4);

        cpu.GetGpr(4).Should().Be(10);
        cpu.GetGpr(5).Should().Be(9);
        cpu.GetGpr(6).Should().Be(1);
        cpu.Lr.Should().Be(0x3014);
    }

    [Fact]
    public void Compares_and_conditional_branches()
    {
        var (cpu, _) = Machine(
            Ppc.Cmpwi(1, 3, -5), // cr1: r3 (-10) < -5
            Ppc.Bc(12, 4, 8), // blt cr1 → $300C
            Ppc.Addi(4, 0, 1), // skipped
            Ppc.Cmplwi(0, 3, 5), // cr0: unsigned, $FFFFFFF6 > 5
            Ppc.Mfcr(5));
        cpu.SetGpr(3, unchecked((uint)-10));

        Run(cpu, 4);

        cpu.GetGpr(4).Should().Be(0);
        (cpu.GetGpr(5) >> 24).Should().Be(0x48, "cr0 GT, cr1 LT");
    }

    [Fact]
    public void Loads_and_stores_of_every_width_and_form()
    {
        var (cpu, ram) = Machine(
            Ppc.Stw(3, 0, 0x4000),
            Ppc.Lhz(4, 0, 0x4000),
            Ppc.Lha(5, 0, 0x4000),
            Ppc.Lbz(6, 0, 0x4003),
            Ppc.Stwu(3, 10, 8), // r10 = $5008
            Ppc.Lwbrx(7, 0, 11),
            Ppc.Sth(3, 0, 0x4010),
            Ppc.Stb(3, 0, 0x4013),
            Ppc.Stmw(29, 0, 0x4100),
            Ppc.Lmw(27, 0, 0x4100),
            Ppc.Stswi(3, 12, 6),
            Ppc.Dcbz(0, 13));
        cpu.SetGpr(3, 0x89ABCDEF);
        cpu.SetGpr(10, 0x5000);
        cpu.SetGpr(11, 0x4000);
        cpu.SetGpr(12, 0x4200);
        cpu.SetGpr(13, 0x4310);
        cpu.SetGpr(29, 1);
        cpu.SetGpr(30, 2);
        cpu.SetGpr(31, 3);
        ram.Bytes.AsSpan(0x4300, 64).Fill(0xEE);

        Run(cpu, 12);

        cpu.GetGpr(4).Should().Be(0x89AB);
        cpu.GetGpr(5).Should().Be(0xFFFF89AB);
        cpu.GetGpr(6).Should().Be(0xEF);
        cpu.GetGpr(10).Should().Be(0x5008);
        ram.Read32(0x5008).Should().Be(0x89ABCDEF);
        cpu.GetGpr(7).Should().Be(0xEFCDAB89);
        ram.Read16(0x4010).Should().Be(0xCDEF);
        ram.Bytes[0x4013].Should().Be(0xEF);
        cpu.GetGpr(27).Should().Be(1, "lmw reads back what stmw stored");
        cpu.GetGpr(28).Should().Be(2);
        cpu.GetGpr(29).Should().Be(3);
        ram.Bytes.AsSpan(0x4200, 6).ToArray().Should().Equal(0x89, 0xAB, 0xCD, 0xEF, 0x00, 0x00); // r3, then r4 ($89AB)
        ram.Bytes.AsSpan(0x4300, 32).ToArray().Should().AllBeEquivalentTo((byte)0, "dcbz clears the 32-byte block");
        ram.Bytes[0x4320].Should().Be(0xEE);
    }

    [Fact]
    public void The_fpu_computes_in_double_and_rounds_singles()
    {
        var (cpu, ram) = Machine(
            Ppc.Lfs(1, 0, 0x4000),
            Ppc.Lfd(2, 0, 0x4008),
            Ppc.Fp(21, 3, 1, 2), // fadd
            Ppc.Fp(25, 4, 1, 0, 2), // fmul f4 = f1 × f2
            Ppc.Fp(29, 5, 1, 2, 2, primary: 59), // fmadds f5 = f1 × f2 + f2
            Ppc.Fpx(15, 6, 0, 4), // fctiwz
            Ppc.Stfd(6, 0, 0x4010),
            Ppc.Fpx(0, 4, 1, 2), // fcmpu cr1, f1, f2
            Ppc.Stfs(3, 0, 0x4020));
        ram.Write32(0x4000, (uint)BitConverter.SingleToInt32Bits(1.5f));
        BinaryPrimitives.WriteInt64BigEndian(ram.Bytes.AsSpan(0x4008), BitConverter.DoubleToInt64Bits(-2.25));

        Run(cpu, 9);

        cpu.GetPs0(3).Should().Be(-0.75);
        cpu.GetPs0(4).Should().Be(-3.375);
        cpu.GetPs0(5).Should().Be(-5.625);
        cpu.GetPs1(5).Should().Be(-5.625, "single results go to both halves");
        ram.Read32(0x4014).Should().Be(unchecked((uint)-3), "fctiwz truncates −3.375 to −3");
        ((cpu.Cr >> 24) & 0xF).Should().Be(4, "cr1: f1 > f2");
        ram.Read32(0x4020).Should().Be((uint)BitConverter.SingleToInt32Bits(-0.75f));
    }

    [Fact]
    public void Paired_singles_and_quantised_loads_and_stores()
    {
        var (cpu, ram) = Machine(
            Ppc.PsqL(1, 3, 0, w: false, i: 1), // two unsigned bytes, scaled down by 2^4
            Ppc.Ps(21, 2, 1, 1), // ps_add f2 = f1 + f1
            Ppc.Psx(528, 3, 2, 1), // ps_merge00: f2.ps0, f1.ps0
            Ppc.PsqSt(2, 4, 0, w: false, i: 2)); // two signed halfwords, scaled up by 2^8
        cpu.SetGpr(3, 0x4000);
        cpu.SetGpr(4, 0x4100);
        cpu.SetGqr(1, (4u << 16) | (4u << 24)); // load: u8, scale 4
        cpu.SetGqr(2, 7u | (8u << 8)); // store: s16, scale 8
        ram.Bytes[0x4000] = 32; // 2.0
        ram.Bytes[0x4001] = 8; // 0.5

        Run(cpu, 4);

        cpu.GetPs0(1).Should().Be(2.0);
        cpu.GetPs1(1).Should().Be(0.5);
        cpu.GetPs1(2).Should().Be(1.0);
        cpu.GetPs0(3).Should().Be(4.0);
        cpu.GetPs1(3).Should().Be(2.0);
        ram.Read16(0x4100).Should().Be(4 * 256);
        ram.Read16(0x4102).Should().Be(256);
    }

    [Fact]
    public void System_call_and_rfi()
    {
        var (cpu, ram) = Machine(Ppc.Sc(), Ppc.Addi(4, 0, 7));
        ram.Write32(0xC00, Ppc.Addi(3, 0, 1));
        ram.Write32(0xC04, Ppc.Rfi());
        cpu.Msr = 0xA000; // EE and FP

        Run(cpu, 1);
        cpu.Pc.Should().Be(0xC00);
        cpu.Srr0.Should().Be(0x3004);
        cpu.Msr.Should().Be(0, "interrupts and the FPU are off in the handler");
        Run(cpu, 3);

        cpu.GetGpr(3).Should().Be(1);
        cpu.GetGpr(4).Should().Be(7);
        cpu.Msr.Should().Be(0xA000);
    }

    [Fact]
    public void Illegal_instructions_and_the_fpu_being_off_raise_exceptions()
    {
        var (cpu, _) = Machine(0x00000000, Ppc.Fp(21, 1, 1, 1));

        Run(cpu, 1);
        cpu.Pc.Should().Be(0x700);
        cpu.Srr0.Should().Be(0x3000);
        (cpu.Srr1 & 0x00080000).Should().NotBe(0, "illegal instruction");

        cpu.Pc = 0x3004;
        cpu.Msr = 0;
        Run(cpu, 1);
        cpu.Pc.Should().Be(0x800);
    }

    [Fact]
    public void The_decrementer_and_external_interrupts_wait_for_msr_ee()
    {
        var (cpu, ram) = Machine(Ppc.B(0));
        ram.Write32(0x900, Ppc.B(0));
        ram.Write32(0x500, Ppc.B(0));
        cpu.Dec = 2;
        Run(cpu, 48);
        cpu.Pc.Should().Be(0x3000, "MSR[EE] is off");

        cpu.Msr |= 0x8000;
        Run(cpu, 1);
        cpu.Pc.Should().Be(0x900);

        ram.ExternalInterrupt = true;
        cpu.Pc = 0x3000;
        cpu.Msr |= 0x8000;
        Run(cpu, 1);
        cpu.Pc.Should().Be(0x500);
    }

    [Fact]
    public void Bats_map_the_cached_and_uncached_mirrors()
    {
        var (cpu, ram) = Machine(Ppc.Stw(3, 4, 0), Ppc.Lwz(5, 6, 0x10));
        // DBAT0: $80000000, 256 MB (BL $7FF), supervisor-valid → physical 0; DBAT1 the same for $C0000000.
        cpu.SetBat(data: true, 0, 0x80001FFE, 0x00000002);
        cpu.SetBat(data: true, 1, 0xC0001FFE, 0x0000002A);
        cpu.Msr = 0x2010; // DR on
        cpu.SetGpr(3, 0xCAFEF00D);
        cpu.SetGpr(4, 0x80004000);
        cpu.SetGpr(6, 0xC0003FF0);

        Run(cpu, 2);

        ram.Read32(0x4000).Should().Be(0xCAFEF00D);
        cpu.GetGpr(5).Should().Be(0xCAFEF00D);
    }

    [Fact]
    public void Sprs_time_base_and_the_pvr()
    {
        var (cpu, _) = Machine(
            Ppc.Mtspr(8, 3), // LR
            Ppc.Mfspr(4, 8),
            Ppc.Mfspr(5, 287), // PVR
            Ppc.Mtspr(913, 3), // GQR1
            Ppc.Mfspr(6, 913),
            (31u << 26) | (7u << 21) | (12u << 16) | (8u << 11) | (371u << 1)); // mftb r7 (TBL)
        cpu.SetGpr(3, 0x12345678);

        Run(cpu, 6);

        cpu.GetGpr(4).Should().Be(0x12345678);
        (cpu.GetGpr(5) >> 16).Should().Be(0x0008, "the 750 family");
        cpu.GetGpr(6).Should().Be(0x12345678);
        cpu.GetGpr(7).Should().Be((uint)cpu.TimeBase);
    }

    [Fact]
    public void A_save_state_round_trips()
    {
        var (cpu, ram) = Machine(Ppc.Addi(3, 3, 1), Ppc.Fp(21, 1, 1, 2), Ppc.B(-8));
        cpu.SetFpr(2, 0.5, 0.25);
        Run(cpu, 20);
        var writer = new StateWriter();
        cpu.SaveState(writer);
        Run(cpu, 30);
        var expected = (cpu.GetGpr(3), cpu.GetPs0(1), cpu.Pc, cpu.TimeBase);

        var copy = new GekkoCpu(ram);
        copy.LoadState(new StateReader(writer.ToArray()));
        Run(copy, 30);

        (copy.GetGpr(3), copy.GetPs0(1), copy.Pc, copy.TimeBase).Should().Be(expected);
    }
}
