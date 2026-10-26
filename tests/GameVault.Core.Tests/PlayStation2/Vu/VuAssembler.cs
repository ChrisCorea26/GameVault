using System.Buffers.Binary;

namespace GameVault.Core.Tests.PlayStation2.Vu;

/// <summary>
/// VU instruction encoders, a C# port of ps2autotests' tests/vu/assemble/ops.cpp (ISC licence, jpd002/ps2autotests),
/// so the hardware test programs can be rebuilt instruction for instruction. Registers follow its numbering: VF00–VF31
/// are 0–31 and VI00–VI15 are 32–47.
/// </summary>
internal static class VuOps
{
    // Destination masks.
    public const int DestX = 8;
    public const int DestY = 4;
    public const int DestZ = 2;
    public const int DestW = 1;
    public const int DestNone = 0;
    public const int DestXY = DestX | DestY;
    public const int DestXYZ = DestXY | DestZ;
    public const int DestXYZW = DestXYZ | DestW;

    // Fields.
    public const int FieldX = 0;
    public const int FieldY = 1;
    public const int FieldZ = 2;
    public const int FieldW = 3;

    // Upper flags.
    public const uint UpperI = 1u << 31;
    public const uint UpperE = 1u << 30;
    public const uint UpperM = 1u << 29;
    public const uint UpperD = 1u << 28;
    public const uint UpperT = 1u << 27;

    public const uint UpperNop = 0x000002FF;
    public const uint LowerNop = 0x80000030; // IADD vi00, vi00, vi00

    public static int VF(int n) => n;

    public static int VI(int n) => 32 + n;

    private static uint ViField(int r)
    {
        if (r < 32 || r > 47)
        {
            throw new ArgumentOutOfRangeException(nameof(r), "Expected a VI register.");
        }

        return (uint)(r - 32);
    }

    private static uint VfField(int r)
    {
        if (r < 0 || r > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(r), "Expected a VF register.");
        }

        return (uint)r;
    }

    private static uint Simm5(int imm) => (uint)imm & 0x1F;

    private static uint Simm11(int imm) => (uint)imm & 0x7FF;

    private static uint Imm15(int imm) => ((uint)imm & 0x7FF) | (((uint)imm & 0x7800) << 10);

    // Encoding helpers (the field positions of encoding.h).
    private static uint Upper0(uint op, int dest, int bc, int fd, int fs, int ft, uint f) =>
        f | ((uint)dest << 21) | (VfField(ft) << 16) | (VfField(fs) << 11) | (VfField(fd) << 6) | (op << 2) | (uint)bc;

    private static uint Upper1(uint op, int dest, int fd, int fs, int ft, uint f) =>
        f | ((uint)dest << 21) | (VfField(ft) << 16) | (VfField(fs) << 11) | (VfField(fd) << 6) | op;

    private static uint Upper2(uint op, int dest, int bc, int fs, int ft, uint f) =>
        f | ((uint)dest << 21) | (VfField(ft) << 16) | (VfField(fs) << 11) | (op << 2) | (uint)bc;

    private static uint Upper3(uint op, int dest, int fs, int ft, uint f) =>
        f | ((uint)dest << 21) | (VfField(ft) << 16) | (VfField(fs) << 11) | op;

    private static uint Lower1(uint op, uint fd, uint fs, uint ft) => 0x80000000 | (ft << 16) | (fs << 11) | (fd << 6) | op;

    private static uint Lower3(uint op, int dest, uint fs, uint ft) => 0x80000000 | ((uint)dest << 21) | (ft << 16) | (fs << 11) | op;

    private static uint Lower4(uint op, int fsf, uint fs, int ftf, uint ft) =>
        0x80000000 | ((uint)ftf << 23) | ((uint)fsf << 21) | (ft << 16) | (fs << 11) | op;

    private static uint Lower5(uint op, uint fs, uint ft, uint imm5) => 0x80000000 | (ft << 16) | (fs << 11) | (imm5 << 6) | op;

    private static uint Lower7(uint op, int dest, uint fs, uint ft, uint imm11) =>
        (op << 25) | ((uint)dest << 21) | (ft << 16) | (fs << 11) | (imm11 & 0x7FF);

    private static uint Lower8(uint op, uint fs, uint ft, int imm15) => (op << 25) | (ft << 16) | (fs << 11) | Imm15(imm15);

    private static uint Lower9(uint op, uint imm24) => (op << 25) | (imm24 & 0xFFFFFF);

    // Upper instructions.
    public static uint ABS(int dest, int t, int s, uint f = 0) => Upper3(0x1FD, dest, s, t, f);
    public static uint ADD(int dest, int d, int s, int t, uint f = 0) => Upper1(0x28, dest, d, s, t, f);
    public static uint ADDbc(int dest, int bc, int d, int s, int t, uint f = 0) => Upper0(0x0, dest, bc, d, s, t, f);
    public static uint ADDi(int dest, int d, int s, uint f = 0) => Upper1(0x22, dest, d, s, 0, f);
    public static uint ADDq(int dest, int d, int s, uint f = 0) => Upper1(0x20, dest, d, s, 0, f);
    public static uint ADDA(int dest, int s, int t, uint f = 0) => Upper3(0x2BC, dest, s, t, f);
    public static uint ADDAbc(int dest, int bc, int s, int t, uint f = 0) => Upper2(0x0F, dest, bc, s, t, f);
    public static uint CLIP(int s, int t, uint f = 0) => Upper3(0x1FF, DestXYZ, s, t, f);
    public static uint FTOI0(int dest, int t, int s, uint f = 0) => Upper3(0x17C, dest, s, t, f);
    public static uint FTOI4(int dest, int t, int s, uint f = 0) => Upper3(0x17D, dest, s, t, f);
    public static uint FTOI12(int dest, int t, int s, uint f = 0) => Upper3(0x17E, dest, s, t, f);
    public static uint FTOI15(int dest, int t, int s, uint f = 0) => Upper3(0x17F, dest, s, t, f);
    public static uint ITOF0(int dest, int t, int s, uint f = 0) => Upper3(0x13C, dest, s, t, f);
    public static uint ITOF4(int dest, int t, int s, uint f = 0) => Upper3(0x13D, dest, s, t, f);
    public static uint ITOF12(int dest, int t, int s, uint f = 0) => Upper3(0x13E, dest, s, t, f);
    public static uint ITOF15(int dest, int t, int s, uint f = 0) => Upper3(0x13F, dest, s, t, f);
    public static uint MADD(int dest, int d, int s, int t, uint f = 0) => Upper1(0x29, dest, d, s, t, f);
    public static uint MADDbc(int dest, int bc, int d, int s, int t, uint f = 0) => Upper0(0x2, dest, bc, d, s, t, f);
    public static uint MADDA(int dest, int s, int t, uint f = 0) => Upper3(0x2BD, dest, s, t, f);
    public static uint MADDAbc(int dest, int bc, int s, int t, uint f = 0) => Upper2(0x2F, dest, bc, s, t, f);
    public static uint MAX(int dest, int d, int s, int t, uint f = 0) => Upper1(0x2B, dest, d, s, t, f);
    public static uint MAXbc(int dest, int bc, int d, int s, int t, uint f = 0) => Upper0(0x4, dest, bc, d, s, t, f);
    public static uint MAXi(int dest, int d, int s, uint f = 0) => Upper1(0x1D, dest, d, s, 0, f);
    public static uint MINI(int dest, int d, int s, int t, uint f = 0) => Upper1(0x2F, dest, d, s, t, f);
    public static uint MINIbc(int dest, int bc, int d, int s, int t, uint f = 0) => Upper0(0x5, dest, bc, d, s, t, f);
    public static uint MINIi(int dest, int d, int s, uint f = 0) => Upper1(0x1F, dest, d, s, 0, f);
    public static uint MSUB(int dest, int d, int s, int t, uint f = 0) => Upper1(0x2D, dest, d, s, t, f);
    public static uint MSUBA(int dest, int s, int t, uint f = 0) => Upper3(0x2FD, dest, s, t, f);
    public static uint MUL(int dest, int d, int s, int t, uint f = 0) => Upper1(0x2A, dest, d, s, t, f);
    public static uint MULbc(int dest, int bc, int d, int s, int t, uint f = 0) => Upper0(0x6, dest, bc, d, s, t, f);
    public static uint MULi(int dest, int d, int s, uint f = 0) => Upper1(0x1E, dest, d, s, 0, f);
    public static uint MULq(int dest, int d, int s, uint f = 0) => Upper1(0x1C, dest, d, s, 0, f);
    public static uint MULA(int dest, int s, int t, uint f = 0) => Upper3(0x2BE, dest, s, t, f);
    public static uint MULAbc(int dest, int bc, int s, int t, uint f = 0) => Upper2(0x6F, dest, bc, s, t, f);
    public static uint MULAi(int dest, int s, uint f = 0) => Upper3(0x1FE, dest, s, 0, f);
    public static uint NOP(uint f = 0) => Upper3(0x2FF, DestNone, 0, 0, f);
    public static uint OPMULA(int s, int t, uint f = 0) => Upper3(0x2FE, DestXYZ, s, t, f);
    public static uint OPMSUB(int d, int s, int t, uint f = 0) => Upper1(0x2E, DestXYZ, d, s, t, f);
    public static uint SUB(int dest, int d, int s, int t, uint f = 0) => Upper1(0x2C, dest, d, s, t, f);
    public static uint SUBbc(int dest, int bc, int d, int s, int t, uint f = 0) => Upper0(0x1, dest, bc, d, s, t, f);
    public static uint SUBi(int dest, int d, int s, uint f = 0) => Upper1(0x26, dest, d, s, 0, f);
    public static uint SUBA(int dest, int s, int t, uint f = 0) => Upper3(0x2FC, dest, s, t, f);

    // Lower instructions.
    public static uint LowerImm(float imm) => BitConverter.SingleToUInt32Bits(imm);
    public static uint B(int imm11) => Lower7(0x20, DestNone, 0, 0, Simm11(imm11));
    public static uint BAL(int t, int imm11) => Lower7(0x21, DestNone, 0, ViField(t), Simm11(imm11));
    public static uint DIV(int fsf, int s, int ftf, int t) => Lower4(0x3BC, fsf, VfField(s), ftf, VfField(t));
    public static uint EATAN(int fsf, int s) => Lower4(0x7FD, fsf, VfField(s), FieldX, 0);
    public static uint EATANxy(int s) => Lower3(0x77C, DestXY, VfField(s), 0);
    public static uint EATANxz(int s) => Lower3(0x77D, DestX | DestZ, VfField(s), 0);
    public static uint EEXP(int fsf, int s) => Lower4(0x7FE, fsf, VfField(s), FieldX, 0);
    public static uint ELENG(int s) => Lower3(0x73E, DestXYZ, VfField(s), 0);
    public static uint ERCPR(int fsf, int s) => Lower4(0x7BE, fsf, VfField(s), FieldX, 0);
    public static uint ERLENG(int s) => Lower3(0x73F, DestXYZ, VfField(s), 0);
    public static uint ERSADD(int s) => Lower3(0x73D, DestXYZ, VfField(s), 0);
    public static uint ERSQRT(int fsf, int s) => Lower4(0x7BD, fsf, VfField(s), FieldX, 0);
    public static uint ESADD(int s) => Lower3(0x73C, DestXYZ, VfField(s), 0);
    public static uint ESIN(int fsf, int s) => Lower4(0x7FC, fsf, VfField(s), FieldX, 0);
    public static uint ESQRT(int fsf, int s) => Lower4(0x7BC, fsf, VfField(s), FieldX, 0);
    public static uint ESUM(int s) => Lower3(0x77E, DestXYZW, VfField(s), 0);
    public static uint FCAND(uint imm24) => Lower9(0x12, imm24);
    public static uint FCEQ(uint imm24) => Lower9(0x10, imm24);
    public static uint FCGET(int t) => Lower8(0x1C, 0, ViField(t), 0);
    public static uint FCOR(uint imm24) => Lower9(0x13, imm24);
    public static uint FCSET(uint imm24) => Lower9(0x11, imm24);
    public static uint FMAND(int t, int s) => Lower8(0x1A, ViField(s), ViField(t), 0);
    public static uint FMEQ(int t, int s) => Lower8(0x18, ViField(s), ViField(t), 0);
    public static uint FMOR(int t, int s) => Lower8(0x1B, ViField(s), ViField(t), 0);
    public static uint FSAND(int t, int imm12) => Lower8(0x16, 0, ViField(t), imm12);
    public static uint FSEQ(int t, int imm12) => Lower8(0x14, 0, ViField(t), imm12);
    public static uint FSOR(int t, int imm12) => Lower8(0x17, 0, ViField(t), imm12);
    public static uint FSSET(int imm12) => Lower8(0x15, 0, 0, imm12);
    public static uint IADD(int d, int s, int t) => Lower1(0x30, ViField(d), ViField(s), ViField(t));
    public static uint IADDI(int t, int s, int imm5) => Lower5(0x32, ViField(s), ViField(t), Simm5(imm5));
    public static uint IADDIU(int t, int s, int imm15) => Lower8(0x08, ViField(s), ViField(t), imm15);
    public static uint IAND(int d, int s, int t) => Lower1(0x34, ViField(d), ViField(s), ViField(t));
    public static uint IBEQ(int t, int s, int imm11) => Lower7(0x28, DestNone, ViField(s), ViField(t), Simm11(imm11));
    public static uint IBGEZ(int s, int imm11) => Lower7(0x2F, DestNone, ViField(s), 0, Simm11(imm11));
    public static uint IBGTZ(int s, int imm11) => Lower7(0x2D, DestNone, ViField(s), 0, Simm11(imm11));
    public static uint IBLEZ(int s, int imm11) => Lower7(0x2E, DestNone, ViField(s), 0, Simm11(imm11));
    public static uint IBLTZ(int s, int imm11) => Lower7(0x2C, DestNone, ViField(s), 0, Simm11(imm11));
    public static uint IBNE(int t, int s, int imm11) => Lower7(0x29, DestNone, ViField(s), ViField(t), Simm11(imm11));
    public static uint ILW(int dest, int t, int s, int imm11) => Lower7(0x04, dest, ViField(s), ViField(t), Simm11(imm11));
    public static uint ILWR(int dest, int t, int s) => Lower3(0x3FE, dest, ViField(s), ViField(t));
    public static uint IOR(int d, int s, int t) => Lower1(0x35, ViField(d), ViField(s), ViField(t));
    public static uint ISUB(int d, int s, int t) => Lower1(0x31, ViField(d), ViField(s), ViField(t));
    public static uint ISUBIU(int t, int s, int imm15) => Lower8(0x09, ViField(s), ViField(t), imm15);
    public static uint ISW(int dest, int t, int s, int imm11) => Lower7(0x05, dest, ViField(s), ViField(t), Simm11(imm11));
    public static uint ISWR(int dest, int t, int s) => Lower3(0x3FF, dest, ViField(s), ViField(t));
    public static uint JALR(int t, int s) => Lower7(0x25, DestNone, ViField(s), ViField(t), 0);
    public static uint JR(int s) => Lower7(0x24, DestNone, ViField(s), 0, 0);
    public static uint LQ(int dest, int t, int s, int imm11) => Lower7(0x00, dest, ViField(s), VfField(t), Simm11(imm11));
    public static uint LQD(int dest, int t, int s) => Lower3(0x37E, dest, ViField(s), VfField(t));
    public static uint LQI(int dest, int t, int s) => Lower3(0x37C, dest, ViField(s), VfField(t));
    public static uint MFIR(int dest, int t, int s) => Lower3(0x3FD, dest, ViField(s), VfField(t));
    public static uint MFP(int dest, int t) => Lower3(0x67C, dest, 0, VfField(t));
    public static uint MOVE(int dest, int t, int s) => Lower3(0x33C, dest, VfField(s), VfField(t));
    public static uint MR32(int dest, int t, int s) => Lower3(0x33D, dest, VfField(s), VfField(t));
    public static uint MTIR(int t, int fsf, int s) => Lower4(0x3FC, fsf, VfField(s), FieldX, ViField(t));
    public static uint RGET(int dest, int t) => Lower3(0x43D, dest, 0, VfField(t));
    public static uint RINIT(int fsf, int s) => Lower4(0x43E, fsf, VfField(s), FieldX, 0);
    public static uint RNEXT(int dest, int t) => Lower3(0x43C, dest, 0, VfField(t));
    public static uint RSQRT(int fsf, int s, int ftf, int t) => Lower4(0x3BE, fsf, VfField(s), ftf, VfField(t));
    public static uint RXOR(int fsf, int s) => Lower4(0x43F, fsf, VfField(s), FieldX, 0);
    public static uint SQ(int dest, int s, int t, int imm11) => Lower7(0x01, dest, VfField(s), ViField(t), Simm11(imm11));
    public static uint SQD(int dest, int s, int t) => Lower3(0x37F, dest, VfField(s), ViField(t));
    public static uint SQI(int dest, int s, int t) => Lower3(0x37D, dest, VfField(s), ViField(t));
    public static uint SQRT(int ftf, int t) => Lower4(0x3BD, FieldX, 0, ftf, VfField(t));
    public static uint WAITP() => Lower3(0x7BF, DestNone, 0, 0);
    public static uint WAITQ() => Lower3(0x3BF, DestNone, 0, 0);
    public static uint XGKICK(int s) => Lower3(0x6FC, DestNone, ViField(s), 0);
    public static uint XITOP(int t) => Lower3(0x6BD, DestNone, 0, ViField(t));
    public static uint XTOP(int t) => Lower3(0x6BC, DestNone, 0, ViField(t));

    /// <summary>A COP2 macro-mode word for an upper or lower operation (COP2 with the CO bit, then the VU encoding).</summary>
    public static uint Macro(uint vuEncoding) => 0x4A000000 | (vuEncoding & 0x01FFFFFF);
}

/// <summary>A branch whose offset is fixed up once its destination is known (assemble/types.h's Label).</summary>
internal sealed class VuLabel
{
    public int Destination = -1;
    public int Source = -1;
    public uint Upper;
    public Func<int, uint>? Encode;
}

/// <summary>A block of VU instruction pairs written into micro memory: ps2autotests' assemble/block.cpp in C#.</summary>
internal class VuBlock(byte[] micro)
{
    private int _position;

    protected byte[] Micro => micro;

    public int Position => _position;

    public void Wr(uint upper, uint lower)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(micro.AsSpan(_position * 8), lower);
        BinaryPrimitives.WriteUInt32LittleEndian(micro.AsSpan(_position * 8 + 4), upper);
        _position++;
    }

    public void WrLower(uint lower) => Wr(VuOps.NOP(), lower);

    public void WrUpper(uint upper) => Wr(upper, VuOps.LowerNop);

    public void WrImm(float imm) => WrImm(VuOps.NOP(), imm);

    public void WrImm(uint upper, float imm) => Wr(upper | VuOps.UpperI, VuOps.LowerImm(imm));

    public void WrImm(uint upper, uint imm) => Wr(upper | VuOps.UpperI, imm);

    public VuLabel L() => new() { Destination = _position };

    public void L(VuLabel label)
    {
        label.Destination = _position;
        Fixup(label);
    }

    private VuLabel Defer(Func<int, uint> encode, uint upper, uint delayUpper, uint delayLower)
    {
        var label = new VuLabel { Source = _position, Upper = upper, Encode = encode };
        WrUpper(VuOps.NOP(VuOps.UpperE));
        Wr(delayUpper, delayLower);
        return label;
    }

    private void DoBranch(Func<int, uint> encode, uint upper, VuLabel label, uint delayUpper, uint delayLower)
    {
        label.Source = _position;
        label.Upper = upper;
        label.Encode = encode;
        WrUpper(VuOps.NOP(VuOps.UpperE));
        Fixup(label);
        Wr(delayUpper, delayLower);
    }

    private void Fixup(VuLabel label)
    {
        var distance = label.Destination - label.Source - 1; // The 1 is the delay slot.
        var lower = label.Encode!(distance);
        BinaryPrimitives.WriteUInt32LittleEndian(micro.AsSpan(label.Source * 8), lower);
        BinaryPrimitives.WriteUInt32LittleEndian(micro.AsSpan(label.Source * 8 + 4), label.Upper);
    }

    public VuLabel B(uint upper = VuOps.UpperNop, uint delayUpper = VuOps.UpperNop, uint delayLower = VuOps.LowerNop) =>
        Defer(VuOps.B, upper, delayUpper, delayLower);

    public void B(VuLabel label, uint delayUpper = VuOps.UpperNop, uint delayLower = VuOps.LowerNop) =>
        DoBranch(VuOps.B, VuOps.NOP(), label, delayUpper, delayLower);

    public void IBLTZ(uint upper, VuLabel label, int s, uint delayUpper = VuOps.UpperNop, uint delayLower = VuOps.LowerNop) =>
        DoBranch(d => VuOps.IBLTZ(s, d), upper, label, delayUpper, delayLower);

    public VuLabel IBNE(int t, int s, uint upper = VuOps.UpperNop, uint delayUpper = VuOps.UpperNop, uint delayLower = VuOps.LowerNop) =>
        Defer(d => VuOps.IBNE(t, s, d), upper, delayUpper, delayLower);

    public void IBNE(VuLabel label, int t, int s, uint upper = VuOps.UpperNop, uint delayUpper = VuOps.UpperNop, uint delayLower = VuOps.LowerNop) =>
        DoBranch(d => VuOps.IBNE(t, s, d), upper, label, delayUpper, delayLower);

    public void JALR(int t, int s, uint upper = VuOps.UpperNop, uint delayUpper = VuOps.UpperNop, uint delayLower = VuOps.LowerNop)
    {
        Wr(upper, VuOps.JALR(t, s));
        Wr(delayUpper, delayLower);
    }

    public void SafeExit()
    {
        WrUpper(VuOps.NOP(VuOps.UpperE));
        WrUpper(VuOps.NOP());
    }

    public void Reset()
    {
        Array.Clear(micro, 0, _position * 8);
        _position = 0;
    }
}
