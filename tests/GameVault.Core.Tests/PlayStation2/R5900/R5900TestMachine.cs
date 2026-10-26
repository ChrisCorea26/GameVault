using System.Buffers.Binary;
using GameVault.Core.Cpu.R5900;

namespace GameVault.Core.Tests.PlayStation2.R5900;

/// <summary>A flat RDRAM (4 MB) for the R5900 tests; everything above it reads 0 and records writes.</summary>
internal sealed class FlatR5900Bus : IR5900Bus
{
    public byte[] Rdram { get; } = new byte[4 * 1024 * 1024];

    public readonly List<(uint Address, ulong Value)> IoWrites = [];

    public byte Read8(uint address) => 0;

    public ushort Read16(uint address) => 0;

    public uint Read32(uint address) => 0;

    public ulong Read64(uint address) => 0;

    public UInt128 Read128(uint address) => 0;

    public void Write8(uint address, byte value) => IoWrites.Add((address, value));

    public void Write16(uint address, ushort value) => IoWrites.Add((address, value));

    public void Write32(uint address, uint value) => IoWrites.Add((address, value));

    public void Write64(uint address, ulong value) => IoWrites.Add((address, value));

    public void Write128(uint address, UInt128 value) => IoWrites.Add((address, (ulong)value));

    public uint ReadWord(uint physical) => BinaryPrimitives.ReadUInt32LittleEndian(Rdram.AsSpan((int)physical));

    public void WriteWord(uint physical, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Rdram.AsSpan((int)physical), value);
}

/// <summary>A VU0 stand-in for the COP2 transfers: VF and control registers, and a micro program that "runs".</summary>
internal sealed class FakeCop2 : IR5900Cop2
{
    public readonly UInt128[] Vf = new UInt128[32];
    public readonly uint[] Control = new uint[32];
    public readonly List<uint> Macros = [];
    public int Waits;

    public bool MicroRunning { get; set; }

    public UInt128 ReadVf(int index) => Vf[index];

    public void WriteVf(int index, UInt128 value) => Vf[index] = value;

    public uint ReadControl(int index) => Control[index];

    public void WriteControl(int index, uint value) => Control[index] = value;

    public void ExecuteMacro(uint instruction) => Macros.Add(instruction);

    public void WaitForMicro()
    {
        Waits++;
        MicroRunning = false;
    }
}

/// <summary>
/// Encodes R5900 instructions for tests. Register numbers are MIPS numbers; the assembler operand order is kept, so
/// <c>Sllv(rd, rt, rs)</c> reads like <c>sllv rd, rt, rs</c>.
/// </summary>
internal static class Ee
{
    public static uint R(uint funct, int rd = 0, int rs = 0, int rt = 0, int sa = 0) =>
        (uint)(rs << 21 | rt << 16 | rd << 11 | sa << 6) | funct;

    public static uint I(uint op, int rt, int rs, int imm) => op << 26 | (uint)(rs << 21 | rt << 16) | (ushort)imm;

    public static uint Mmi(uint funct, int rd = 0, int rs = 0, int rt = 0, int sa = 0) => 0x7000_0000 | R(funct, rd, rs, rt, sa);

    public static uint Nop => 0;

    public static uint Lui(int rt, int imm) => I(0x0F, rt, 0, imm);

    public static uint Ori(int rt, int rs, int imm) => I(0x0D, rt, rs, imm);

    public static uint Addiu(int rt, int rs, int imm) => I(0x09, rt, rs, imm);

    public static uint Addi(int rt, int rs, int imm) => I(0x08, rt, rs, imm);

    public static uint Addu(int rd, int rs, int rt) => R(0x21, rd, rs, rt);

    public static uint Add(int rd, int rs, int rt) => R(0x20, rd, rs, rt);

    public static uint Beq(int rs, int rt, int offset) => I(0x04, rt, rs, offset);

    public static uint Bne(int rs, int rt, int offset) => I(0x05, rt, rs, offset);

    public static uint Beql(int rs, int rt, int offset) => I(0x14, rt, rs, offset);

    public static uint J(uint target) => 0x0800_0000 | ((target >> 2) & 0x03FF_FFFF);

    public static uint Jr(int rs) => R(0x08, rs: rs);

    public static uint Lw(int rt, int rs, int offset) => I(0x23, rt, rs, offset);

    public static uint Sw(int rt, int rs, int offset) => I(0x2B, rt, rs, offset);

    public static uint Lq(int rt, int rs, int offset) => I(0x1E, rt, rs, offset);

    public static uint Sq(int rt, int rs, int offset) => I(0x1F, rt, rs, offset);

    public static uint Ld(int rt, int rs, int offset) => I(0x37, rt, rs, offset);

    public static uint Sd(int rt, int rs, int offset) => I(0x3F, rt, rs, offset);

    public static uint Lh(int rt, int rs, int offset) => I(0x21, rt, rs, offset);

    public static uint Syscall => 0x0000_000C;

    public static uint Break => 0x0000_000D;

    public static uint Teq(int rs, int rt) => R(0x34, rs: rs, rt: rt);

    public static uint Mult(int rd, int rs, int rt) => R(0x18, rd, rs, rt);

    public static uint Div(int rs, int rt) => R(0x1A, rs: rs, rt: rt);

    public static uint Mflo(int rd) => R(0x12, rd);

    public static uint Mfhi(int rd) => R(0x10, rd);

    public static uint Mfc0(int rt, int rd) => 0x4000_0000u | (uint)(rt << 16 | rd << 11);

    public static uint Mtc0(int rt, int rd) => 0x4080_0000u | (uint)(rt << 16 | rd << 11);

    public static uint Tlbr => 0x4200_0001;

    public static uint Tlbwi => 0x4200_0002;

    public static uint Tlbwr => 0x4200_0006;

    public static uint Tlbp => 0x4200_0008;

    public static uint Eret => 0x4200_0018;

    public static uint Ei => 0x4200_0038;

    public static uint Di => 0x4200_0039;

    public static uint Bc0f(int offset) => 0x4100_0000u | (ushort)offset;

    public static uint Bc0t(int offset) => 0x4101_0000u | (ushort)offset;

    public static uint Mtc1(int rt, int fs) => 0x4480_0000u | (uint)(rt << 16 | fs << 11);

    public static uint Mfc1(int rt, int fs) => 0x4400_0000u | (uint)(rt << 16 | fs << 11);

    public static uint Cfc1(int rt, int fs) => 0x4440_0000u | (uint)(rt << 16 | fs << 11);

    public static uint Ctc1(int rt, int fs) => 0x44C0_0000u | (uint)(rt << 16 | fs << 11);

    /// <summary>A COP1 S-format operation: fd = fs op ft.</summary>
    public static uint FpuS(uint funct, int fd, int fs, int ft) => 0x4600_0000u | (uint)(ft << 16 | fs << 11 | fd << 6) | funct;

    public static uint CvtSW(int fd, int fs) => 0x4680_0020u | (uint)(fs << 11 | fd << 6);

    public static uint Bc1(int kind, int offset) => 0x4500_0000u | (uint)(kind << 16) | (ushort)offset;

    public static uint Qmfc2(int rt, int fd, bool interlock = false) => 0x4820_0000u | (uint)(rt << 16 | fd << 11) | (interlock ? 1u : 0);

    public static uint Qmtc2(int rt, int fd, bool interlock = false) => 0x48A0_0000u | (uint)(rt << 16 | fd << 11) | (interlock ? 1u : 0);

    public static uint Cfc2(int rt, int id, bool interlock = false) => 0x4840_0000u | (uint)(rt << 16 | id << 11) | (interlock ? 1u : 0);

    public static uint Ctc2(int rt, int id, bool interlock = false) => 0x48C0_0000u | (uint)(rt << 16 | id << 11) | (interlock ? 1u : 0);

    public static uint Bc2(int kind, int offset) => 0x4900_0000u | (uint)(kind << 16) | (ushort)offset;

    public static uint Lqc2(int ft, int rs, int offset) => I(0x36, ft, rs, offset);

    public static uint Sqc2(int ft, int rs, int offset) => I(0x3E, ft, rs, offset);

    public static uint Pcpyld(int rd, int rs, int rt) => Mmi(0x09, rd, rs, rt, 0x0E);

    public static uint Por(int rd, int rs, int rt) => Mmi(0x29, rd, rs, rt, 0x12);

    public static uint Mtsa(int rs) => R(0x29, rs: rs);

    public static uint Mfsa(int rd) => R(0x28, rd);

    public static uint Mtsab(int rs, int imm) => I(0x01, 0x18, rs, imm);

    public static uint Mtsah(int rs, int imm) => I(0x01, 0x19, rs, imm);

    public static uint Qfsrv(int rd, int rs, int rt) => Mmi(0x28, rd, rs, rt, 0x1B);

    /// <summary>
    /// The instructions the ps2autotests programs run, by name, with their operand syntax: <c>d</c>, <c>s</c>,
    /// <c>t</c> registers, <c>i</c> a 16-bit immediate, <c>a</c> a shift amount, in assembler order.
    /// </summary>
    public static readonly Dictionary<string, (string Syntax, Func<int, int, int, int, uint> Encode)> Ops = BuildOps();

    private static Dictionary<string, (string, Func<int, int, int, int, uint>)> BuildOps()
    {
        var ops = new Dictionary<string, (string, Func<int, int, int, int, uint>)>();

        void Special(string name, string syntax, uint funct) => ops[name] = (syntax, (d, s, t, a) => R(funct, d, s, t, a));
        void Imm(string name, uint opcode) => ops[name] = ("t,s,i", (d, s, t, i) => I(opcode, t, s, i));
        void MmiOp(string name, string syntax, uint funct, uint sub = 0) => ops[name] = (syntax, (d, s, t, a) => Mmi(funct, d, s, t, (int)sub));

        foreach (var (name, funct) in new[]
                 {
                     ("addu", 0x21u), ("and", 0x24u), ("daddu", 0x2Du), ("dsubu", 0x2Fu), ("movn", 0x0Bu), ("movz", 0x0Au),
                     ("nor", 0x27u), ("or", 0x25u), ("slt", 0x2Au), ("sltu", 0x2Bu), ("subu", 0x23u), ("xor", 0x26u),
                     ("add", 0x20u), ("sub", 0x22u), ("dadd", 0x2Cu), ("dsub", 0x2Eu),
                 })
        {
            Special(name, "d,s,t", funct);
        }

        foreach (var (name, funct) in new[] { ("sllv", 0x04u), ("srlv", 0x06u), ("srav", 0x07u), ("dsllv", 0x14u), ("dsrlv", 0x16u), ("dsrav", 0x17u) })
        {
            Special(name, "d,t,s", funct);
        }

        foreach (var (name, funct) in new[]
                 {
                     ("sll", 0x00u), ("srl", 0x02u), ("sra", 0x03u), ("dsll", 0x38u), ("dsrl", 0x3Au), ("dsra", 0x3Bu),
                     ("dsll32", 0x3Cu), ("dsrl32", 0x3Eu), ("dsra32", 0x3Fu),
                 })
        {
            Special(name, "d,t,a", funct);
        }

        Imm("addiu", 0x09);
        Imm("andi", 0x0C);
        Imm("daddiu", 0x19);
        Imm("ori", 0x0D);
        Imm("xori", 0x0E);
        Imm("slti", 0x0A);
        Imm("sltiu", 0x0B);
        ops["lui"] = ("t,i", (d, s, t, i) => I(0x0F, t, 0, i));

        Special("div", "s,t", 0x1A);
        Special("divu", "s,t", 0x1B);
        Special("mult", "d,s,t", 0x18);
        Special("multu", "d,s,t", 0x19);
        Special("mfhi", "d", 0x10);
        Special("mflo", "d", 0x12);
        Special("mthi", "s", 0x11);
        Special("mtlo", "s", 0x13);
        MmiOp("div1", "s,t", 0x1A);
        MmiOp("divu1", "s,t", 0x1B);
        MmiOp("mult1", "d,s,t", 0x18);
        MmiOp("multu1", "d,s,t", 0x19);
        MmiOp("madd", "d,s,t", 0x00);
        MmiOp("maddu", "d,s,t", 0x01);
        MmiOp("madd1", "d,s,t", 0x20);
        MmiOp("maddu1", "d,s,t", 0x21);
        MmiOp("mfhi1", "d", 0x10);
        MmiOp("mflo1", "d", 0x12);
        MmiOp("mthi1", "s", 0x11);
        MmiOp("mtlo1", "s", 0x13);
        MmiOp("plzcw", "d,s", 0x04);

        // MMI0
        foreach (var (name, sub) in new[]
                 {
                     ("paddw", 0x00u), ("psubw", 0x01u), ("pcgtw", 0x02u), ("pmaxw", 0x03u), ("paddh", 0x04u), ("psubh", 0x05u),
                     ("pcgth", 0x06u), ("pmaxh", 0x07u), ("paddb", 0x08u), ("psubb", 0x09u), ("pcgtb", 0x0Au), ("paddsw", 0x10u),
                     ("psubsw", 0x11u), ("pextlw", 0x12u), ("ppacw", 0x13u), ("paddsh", 0x14u), ("psubsh", 0x15u), ("pextlh", 0x16u),
                     ("ppach", 0x17u), ("paddsb", 0x18u), ("psubsb", 0x19u), ("pextlb", 0x1Au), ("ppacb", 0x1Bu),
                 })
        {
            MmiOp(name, "d,s,t", 0x08, sub);
        }

        MmiOp("pext5", "d,t", 0x08, 0x1E);
        MmiOp("ppac5", "d,t", 0x08, 0x1F);

        // MMI1
        foreach (var (name, sub) in new[]
                 {
                     ("pceqw", 0x02u), ("pminw", 0x03u), ("padsbh", 0x04u), ("pceqh", 0x06u), ("pminh", 0x07u), ("pceqb", 0x0Au),
                     ("padduw", 0x10u), ("psubuw", 0x11u), ("pextuw", 0x12u), ("padduh", 0x14u), ("psubuh", 0x15u),
                     ("pextuh", 0x16u), ("paddub", 0x18u), ("psubub", 0x19u), ("pextub", 0x1Au), ("qfsrv", 0x1Bu),
                 })
        {
            MmiOp(name, "d,s,t", 0x28, sub);
        }

        MmiOp("pabsw", "d,t", 0x28, 0x01);
        MmiOp("pabsh", "d,t", 0x28, 0x05);

        // MMI2
        foreach (var (name, sub) in new[]
                 {
                     ("pmaddw", 0x00u), ("pmsubw", 0x04u), ("pinth", 0x0Au), ("pmultw", 0x0Cu), ("pcpyld", 0x0Eu), ("pmaddh", 0x10u),
                     ("phmadh", 0x11u), ("pand", 0x12u), ("pxor", 0x13u), ("pmsubh", 0x14u), ("phmsbh", 0x15u), ("pmulth", 0x1Cu),
                 })
        {
            MmiOp(name, "d,s,t", 0x09, sub);
        }

        MmiOp("psllvw", "d,t,s", 0x09, 0x02);
        MmiOp("psrlvw", "d,t,s", 0x09, 0x03);
        MmiOp("pmfhi", "d", 0x09, 0x08);
        MmiOp("pmflo", "d", 0x09, 0x09);
        MmiOp("pdivw", "s,t", 0x09, 0x0D);
        MmiOp("pdivbw", "s,t", 0x09, 0x1D);
        MmiOp("pexeh", "d,t", 0x09, 0x1A);
        MmiOp("prevh", "d,t", 0x09, 0x1B);
        MmiOp("pexew", "d,t", 0x09, 0x1E);
        MmiOp("prot3w", "d,t", 0x09, 0x1F);

        // MMI3
        foreach (var (name, sub) in new[]
                 {
                     ("pmadduw", 0x00u), ("pinteh", 0x0Au), ("pmultuw", 0x0Cu), ("pcpyud", 0x0Eu), ("por", 0x12u), ("pnor", 0x13u),
                 })
        {
            MmiOp(name, "d,s,t", 0x29, sub);
        }

        MmiOp("psravw", "d,t,s", 0x29, 0x03);
        MmiOp("pmthi", "s", 0x29, 0x08);
        MmiOp("pmtlo", "s", 0x29, 0x09);
        MmiOp("pdivuw", "s,t", 0x29, 0x0D);
        MmiOp("pexch", "d,t", 0x29, 0x1A);
        MmiOp("pcpyh", "d,t", 0x29, 0x1B);
        MmiOp("pexcw", "d,t", 0x29, 0x1E);

        // The parallel shifts by an immediate, and PMFHL/PMTHL by format.
        foreach (var (name, funct) in new[] { ("psllh", 0x34u), ("psrlh", 0x36u), ("psrah", 0x37u), ("psllw", 0x3Cu), ("psrlw", 0x3Eu), ("psraw", 0x3Fu) })
        {
            ops[name] = ("d,t,a", (d, s, t, a) => Mmi(funct, d, s, t, a));
        }

        foreach (var (name, format) in new[] { ("pmfhl.lw", 0), ("pmfhl.uw", 1), ("pmfhl.slw", 2), ("pmfhl.lh", 3), ("pmfhl.sh", 4) })
        {
            ops[name] = ("d", (d, s, t, a) => Mmi(0x30, d, s, t, format));
        }

        ops["pmthl.lw"] = ("s", (d, s, t, a) => Mmi(0x31, d, s, t, 0));
        return ops;
    }

    /// <summary>Encodes <paramref name="name"/> with its operands in assembler order (registers or immediates).</summary>
    public static uint Assemble(string name, params int[] operands)
    {
        var (syntax, encode) = Ops[name];
        int d = 0, s = 0, t = 0, i = 0;
        var fields = syntax.Split(',');
        for (var k = 0; k < fields.Length; k++)
        {
            switch (fields[k])
            {
                case "d": d = operands[k]; break;
                case "s": s = operands[k]; break;
                case "t": t = operands[k]; break;
                default: i = operands[k]; break;
            }
        }

        return encode(d, s, t, i);
    }
}

/// <summary>An R5900 on <see cref="FlatR5900Bus"/>, running little programs placed in KSEG0.</summary>
internal sealed class R5900TestMachine
{
    public const uint Origin = 0x8001_0000;

    public readonly FlatR5900Bus Bus = new();
    public readonly FakeCop2 Cop2 = new();
    public readonly R5900Cpu Cpu;

    public R5900TestMachine()
    {
        Cpu = new R5900Cpu(Bus, Cop2);
        Cpu.Status = R5900Cpu.StatusCu0 | R5900Cpu.StatusCu1 | R5900Cpu.StatusCu2; // kernel mode, BEV, EXL, ERL clear
    }

    public void Load(uint address, params uint[] program)
    {
        for (var i = 0; i < program.Length; i++)
        {
            Bus.WriteWord((address & 0x1FFF_FFFF) + (uint)(i * 4), program[i]);
        }
    }

    /// <summary>Runs <paramref name="program"/> from <see cref="Origin"/> until it falls off its end.</summary>
    public void Run(params uint[] program)
    {
        Load(Origin, program);
        Load(Origin + (uint)(program.Length * 4), 0, 0);
        Cpu.Jump(Origin);
        var end = Origin + (uint)(program.Length * 4);
        for (var steps = 0; steps < 10_000 && Cpu.Pc != end; steps++)
        {
            Cpu.Step();
        }
    }

    /// <summary>Runs exactly <paramref name="steps"/> instructions of <paramref name="program"/>.</summary>
    public void Step(int steps, params uint[] program)
    {
        Load(Origin, program);
        Cpu.Jump(Origin);
        for (var i = 0; i < steps; i++)
        {
            Cpu.Step();
        }
    }
}
