using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace GameVault.Core.PlayStation2;

// The upper (FMAC) instructions (ps2tek: "COP2 Special1" and "COP2 Special2", which are the upper opcodes too).
public sealed partial class Ps2Vu
{
    private enum UpperKind : byte
    {
        Invalid,
        Nop,
        Add,
        Sub,
        Madd,
        Msub,
        Max,
        Mini,
        Mul,
        OpMula,
        OpMsub,
        Abs,
        Ftoi,
        Itof,
        Clip,
    }

    // Where the second operand comes from.
    private const byte OperandVector = 0;
    private const byte OperandBroadcast = 1;
    private const byte OperandI = 2;
    private const byte OperandQ = 3;

    private const int UpperTableSize = 64 + 128;

    private static readonly UpperKind[] s_upperKind = new UpperKind[UpperTableSize];
    private static readonly byte[] s_upperOperand = new byte[UpperTableSize];
    private static readonly bool[] s_upperToAcc = new bool[UpperTableSize];
    private static readonly int[] s_fixedPoint = [0, 4, 12, 15];

    static Ps2Vu()
    {
        UpperKind[] broadcastKinds = [UpperKind.Add, UpperKind.Sub, UpperKind.Madd, UpperKind.Msub, UpperKind.Max, UpperKind.Mini, UpperKind.Mul];
        for (var op = 0; op < 0x1C; op++)
        {
            SetUpper(op, broadcastKinds[op >> 2], OperandBroadcast, false);
        }

        SetUpper(0x1C, UpperKind.Mul, OperandQ, false);
        SetUpper(0x1D, UpperKind.Max, OperandI, false);
        SetUpper(0x1E, UpperKind.Mul, OperandI, false);
        SetUpper(0x1F, UpperKind.Mini, OperandI, false);
        SetUpper(0x20, UpperKind.Add, OperandQ, false);
        SetUpper(0x21, UpperKind.Madd, OperandQ, false);
        SetUpper(0x22, UpperKind.Add, OperandI, false);
        SetUpper(0x23, UpperKind.Madd, OperandI, false);
        SetUpper(0x24, UpperKind.Sub, OperandQ, false);
        SetUpper(0x25, UpperKind.Msub, OperandQ, false);
        SetUpper(0x26, UpperKind.Sub, OperandI, false);
        SetUpper(0x27, UpperKind.Msub, OperandI, false);
        SetUpper(0x28, UpperKind.Add, OperandVector, false);
        SetUpper(0x29, UpperKind.Madd, OperandVector, false);
        SetUpper(0x2A, UpperKind.Mul, OperandVector, false);
        SetUpper(0x2B, UpperKind.Max, OperandVector, false);
        SetUpper(0x2C, UpperKind.Sub, OperandVector, false);
        SetUpper(0x2D, UpperKind.Msub, OperandVector, false);
        SetUpper(0x2E, UpperKind.OpMsub, OperandVector, false);
        SetUpper(0x2F, UpperKind.Mini, OperandVector, false);

        // Special2: the accumulator forms and the one-operand instructions, keyed 64 + (fhi × 4 + flo).
        UpperKind[] accBroadcastKinds = [UpperKind.Add, UpperKind.Sub, UpperKind.Madd, UpperKind.Msub];
        for (var op = 0; op < 0x10; op++)
        {
            SetUpper(64 + op, accBroadcastKinds[op >> 2], OperandBroadcast, true);
        }

        for (var op = 0x10; op < 0x14; op++)
        {
            SetUpper(64 + op, UpperKind.Itof, OperandVector, false);
            SetUpper(64 + op + 4, UpperKind.Ftoi, OperandVector, false);
        }

        for (var op = 0x18; op < 0x1C; op++)
        {
            SetUpper(64 + op, UpperKind.Mul, OperandBroadcast, true);
        }

        SetUpper(64 + 0x1C, UpperKind.Mul, OperandQ, true);
        SetUpper(64 + 0x1D, UpperKind.Abs, OperandVector, false);
        SetUpper(64 + 0x1E, UpperKind.Mul, OperandI, true);
        SetUpper(64 + 0x1F, UpperKind.Clip, OperandVector, false);
        SetUpper(64 + 0x20, UpperKind.Add, OperandQ, true);
        SetUpper(64 + 0x21, UpperKind.Madd, OperandQ, true);
        SetUpper(64 + 0x22, UpperKind.Add, OperandI, true);
        SetUpper(64 + 0x23, UpperKind.Madd, OperandI, true);
        SetUpper(64 + 0x24, UpperKind.Sub, OperandQ, true);
        SetUpper(64 + 0x25, UpperKind.Msub, OperandQ, true);
        SetUpper(64 + 0x26, UpperKind.Sub, OperandI, true);
        SetUpper(64 + 0x27, UpperKind.Msub, OperandI, true);
        SetUpper(64 + 0x28, UpperKind.Add, OperandVector, true);
        SetUpper(64 + 0x29, UpperKind.Madd, OperandVector, true);
        SetUpper(64 + 0x2A, UpperKind.Mul, OperandVector, true);
        SetUpper(64 + 0x2C, UpperKind.Sub, OperandVector, true);
        SetUpper(64 + 0x2D, UpperKind.Msub, OperandVector, true);
        SetUpper(64 + 0x2E, UpperKind.OpMula, OperandVector, true);
        SetUpper(64 + 0x2F, UpperKind.Nop, OperandVector, false);
    }

    private static void SetUpper(int key, UpperKind kind, byte operand, bool toAcc)
    {
        s_upperKind[key] = kind;
        s_upperOperand[key] = operand;
        s_upperToAcc[key] = toAcc;
    }

    /// <summary>The upper table's key: the Special1 function, or 64 + the Special2 opcode (fhi × 4 + flo).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int UpperKey(uint instr)
    {
        var function = (int)(instr & 0x3F);
        return function < 0x3C ? function : 64 + (int)((instr & 3) | ((instr >> 4) & 0x7C));
    }

    /// <summary>Decodes an upper instruction and the FMAC hazards it checks (per field: ps2tek "FMAC Pipeline").</summary>
    private static void DecodeUpper(ref DecodedPair d, uint instr)
    {
        var key = UpperKey(instr);
        var kind = s_upperKind[key];
        var dest = (byte)((instr >> 21) & 0xF);
        var ft = (byte)((instr >> 16) & 0x1F);
        var fs = (byte)((instr >> 11) & 0x1F);
        d.Upper = kind;
        d.Operand = s_upperOperand[key];
        d.ToAcc = s_upperToAcc[key];
        d.UDest = dest;
        d.Ft = ft;
        d.Fs = fs;
        d.Fd = (byte)((instr >> 6) & 0x1F);
        d.Bc = (byte)(instr & 3);
        d.UReadA = d.UMaskA = d.UReadB = d.UMaskB = 0;
        switch (kind)
        {
            case UpperKind.Invalid:
            case UpperKind.Nop:
                d.Upper = UpperKind.Nop;
                break;
            case UpperKind.OpMula:
            case UpperKind.OpMsub:
                SetUpperReads(ref d, fs, 0xE, ft, 0xE);
                break;
            case UpperKind.Abs:
            case UpperKind.Ftoi:
            case UpperKind.Itof:
                SetUpperReads(ref d, fs, dest, 0, 0);
                break;
            case UpperKind.Clip:
                SetUpperReads(ref d, fs, 0xE, ft, 0x1);
                break;
            default:
                var tMask = d.Operand switch
                {
                    OperandVector => dest,
                    OperandBroadcast => 8 >> d.Bc,
                    _ => 0,
                };
                SetUpperReads(ref d, fs, dest, tMask == 0 ? (byte)0 : ft, (byte)tMask);
                break;
        }
    }

    private static void SetUpperReads(ref DecodedPair d, byte a, int maskA, byte b, int maskB)
    {
        d.UReadA = a;
        d.UMaskA = a == 0 ? (byte)0 : (byte)maskA;
        d.UReadB = b;
        d.UMaskB = b == 0 ? (byte)0 : (byte)maskB;
    }

    /// <summary>
    /// Runs an upper instruction. A VF result goes to the stage (written after the lower instruction has read its
    /// operands); ACC is written at once, since no lower instruction reads it.
    /// </summary>
    private void ExecuteUpper(ref DecodedPair d)
    {
        switch (d.Upper)
        {
            case UpperKind.Nop:
                return;
            case UpperKind.OpMula:
                UpperOpMula(d.Fs, d.Ft, d.Fd, d.UDest, d.Bc);
                return;
            case UpperKind.OpMsub:
                UpperOpMsub(d.Fs, d.Ft, d.Fd, d.UDest, d.Bc);
                return;
            case UpperKind.Abs:
                UpperAbs(d.Fs, d.Ft, d.Fd, d.UDest, d.Bc);
                return;
            case UpperKind.Ftoi:
                UpperFtoi(d.Fs, d.Ft, d.Fd, d.UDest, d.Bc);
                return;
            case UpperKind.Itof:
                UpperItof(d.Fs, d.Ft, d.Fd, d.UDest, d.Bc);
                return;
            case UpperKind.Clip:
                UpperClip(d.Fs, d.Ft, d.Fd, d.UDest, d.Bc);
                return;
            default:
                Arithmetic(d.Upper, d.Operand, d.ToAcc, d.Fs, d.Ft, d.Fd, d.UDest, d.Bc);
                return;
        }
    }

    // One method per upper instruction, all with the same parameters, so the recompiler can call each with constants.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpperOpMula(int fs, int ft, int fd, int dest, int bc) => OuterProduct(fs, ft, -1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpperOpMsub(int fs, int ft, int fd, int dest, int bc) => OuterProduct(fs, ft, fd);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpperAbs(int fs, int ft, int fd, int dest, int bc)
    {
        for (var lane = 0; lane < 4; lane++)
        {
            _stage[lane] = _vf[(fs << 2) | lane] & Ps2VuFloat.Max;
        }

        SetStage(ft, dest);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpperFtoi(int fs, int ft, int fd, int dest, int bc)
    {
        var toInt = s_fixedPoint[bc];
        for (var lane = 0; lane < 4; lane++)
        {
            _stage[lane] = Ps2VuFloat.ToInt(_vf[(fs << 2) | lane], toInt);
        }

        SetStage(ft, dest);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpperItof(int fs, int ft, int fd, int dest, int bc)
    {
        var fromInt = s_fixedPoint[bc];
        for (var lane = 0; lane < 4; lane++)
        {
            _stage[lane] = Ps2VuFloat.FromInt(_vf[(fs << 2) | lane], fromInt);
        }

        SetStage(ft, dest);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpperClip(int fs, int ft, int fd, int dest, int bc) => Clip(fs, ft);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetStage(int reg, int mask)
    {
        _stageReg = reg;
        _stageMask = mask;
    }

    /// <summary>
    /// ADD/SUB/MUL/MADD/MSUB/MAX/MINI in every operand form, to a VF register or ACC, four lanes at once. Inlined with
    /// constant arguments (a recompiled block), the kind and operand switches fold away.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Arithmetic(UpperKind kind, byte operand, bool toAcc, int fs, int ft, int fd, int dest, int bc)
    {
        ref var vf = ref MemoryMarshal.GetArrayDataReference(_vf);
        var s = Vector128.LoadUnsafe(ref vf, (nuint)(fs << 2));
        var t = operand switch
        {
            OperandVector => Vector128.LoadUnsafe(ref vf, (nuint)(ft << 2)),
            OperandBroadcast => Vector128.Create(_vf[(ft << 2) | bc]),
            OperandI => Vector128.Create(_i),
            _ => Vector128.Create(_q),
        };

        ref var accRef = ref MemoryMarshal.GetArrayDataReference(_acc);
        var sign = Vector128.Create(Ps2VuFloat.SignBit);
        Vector128<uint> result;
        Vector128<uint> overflow;
        Vector128<uint> underflow;
        switch (kind)
        {
            case UpperKind.Add:
                result = Ps2VuFloat.Add4(s, t, out overflow, out underflow);
                break;
            case UpperKind.Sub:
                result = Ps2VuFloat.Add4(s, t ^ sign, out overflow, out underflow);
                break;
            case UpperKind.Mul:
                result = Ps2VuFloat.Mul4(s, t, out overflow, out underflow);
                break;
            case UpperKind.Madd:
                result = Ps2VuFloat.Add4(Vector128.LoadUnsafe(ref accRef), Ps2VuFloat.Mul4(s, t, out _, out _), out overflow, out underflow);
                break;
            case UpperKind.Msub:
                result = Ps2VuFloat.Add4(Vector128.LoadUnsafe(ref accRef), Ps2VuFloat.Mul4(s, t, out _, out _) ^ sign, out overflow, out underflow);
                break;
            case UpperKind.Max:
                result = Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(OrderKey4(s), OrderKey4(t)).AsUInt32(), s, t);
                overflow = underflow = Vector128<uint>.Zero;
                break;
            default: // Mini
                result = Vector128.ConditionalSelect(Vector128.LessThan(OrderKey4(s), OrderKey4(t)).AsUInt32(), s, t);
                overflow = underflow = Vector128<uint>.Zero;
                break;
        }

        if (toAcc)
        {
            Vector128.ConditionalSelect(s_laneMasks[dest], result, Vector128.LoadUnsafe(ref accRef)).StoreUnsafe(ref accRef);
        }
        else
        {
            result.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_stage));
            SetStage(fd, dest);
        }

        if (kind != UpperKind.Max && kind != UpperKind.Mini)
        {
            QueueFlag(FlagKindMac, Ps2VuFloat.MacBits4(result, overflow, underflow, dest));
        }
    }

    /// <summary><see cref="Ps2VuFloat.OrderKey"/> on four lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> OrderKey4(Vector128<uint> v) =>
        (v ^ ((v.AsInt32() >> 31).AsUInt32() & Vector128.Create(Ps2VuFloat.Max))).AsInt32();

    /// <summary>A dest field mask (x = 8 … w = 1) as lane masks.</summary>
    private static readonly Vector128<uint>[] s_laneMasks = CreateLaneMasks();

    private static Vector128<uint>[] CreateLaneMasks()
    {
        var masks = new Vector128<uint>[16];
        for (var dest = 0; dest < 16; dest++)
        {
            masks[dest] = Vector128.Create(
                (dest & 8) != 0 ? ~0u : 0,
                (dest & 4) != 0 ? ~0u : 0,
                (dest & 2) != 0 ? ~0u : 0,
                (dest & 1) != 0 ? ~0u : 0);
        }

        return masks;
    }

    /// <summary>OPMULA (<paramref name="fd"/> &lt; 0: ACC = fs × ft) or OPMSUB (fd = ACC − fs × ft), the cross product's halves.</summary>
    private void OuterProduct(int fs, int ft, int fd)
    {
        var s = fs << 2;
        var t = ft << 2;
        var mac = 0;
        for (var lane = 0; lane < 3; lane++)
        {
            // x = s.y·t.z, y = s.z·t.x, z = s.x·t.y
            var a = _vf[s + ((lane + 1) % 3)];
            var b = _vf[t + ((lane + 2) % 3)];
            var product = Ps2VuFloat.Mul(a, b, out var ou);
            uint result;
            if (fd < 0)
            {
                result = product;
                _acc[lane] = result;
            }
            else
            {
                result = Ps2VuFloat.Sub(_acc[lane], product, out ou);
                _stage[lane] = result;
            }

            mac |= Ps2VuFloat.MacBits(result, ou, lane);
        }

        if (fd >= 0)
        {
            SetStage(fd, 0xE);
        }

        QueueFlag(FlagKindMac, mac);
    }

    /// <summary>CLIP: fs.xyz against ±|ft.w|, six judgement bits (+x, −x, +y, −y, +z, −z) into the clipping flags.</summary>
    private void Clip(int fs, int ft)
    {
        var w = FlushDenormal(_vf[(ft << 2) | 3]) & Ps2VuFloat.Max;
        var upper = Ps2VuFloat.OrderKey(w);
        var lower = Ps2VuFloat.OrderKey(w | Ps2VuFloat.SignBit);
        var bits = 0;
        for (var lane = 0; lane < 3; lane++)
        {
            var key = Ps2VuFloat.OrderKey(FlushDenormal(_vf[(fs << 2) | lane]));
            if (key > upper)
            {
                bits |= 1 << (lane * 2);
            }
            else if (key < lower)
            {
                bits |= 2 << (lane * 2);
            }
        }

        QueueFlag(FlagKindClip, bits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint FlushDenormal(uint v) => (v & 0x7F800000) == 0 ? v & Ps2VuFloat.SignBit : v;
}
