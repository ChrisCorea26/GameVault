using System.Runtime.CompilerServices;

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

    /// <summary>The cycle when the upper instruction's operands are ready (FMAC hazards are checked per field).</summary>
    private long UpperReady(uint instr)
    {
        var key = UpperKey(instr);
        var kind = s_upperKind[key];
        if (kind <= UpperKind.Nop)
        {
            return 0;
        }

        var dest = (int)(instr >> 21) & 0xF;
        var ft = (int)(instr >> 16) & 0x1F;
        var fs = (int)(instr >> 11) & 0x1F;
        switch (kind)
        {
            case UpperKind.OpMula:
            case UpperKind.OpMsub:
                return Math.Max(VfReady(fs, 0xE), VfReady(ft, 0xE));
            case UpperKind.Abs:
            case UpperKind.Ftoi:
            case UpperKind.Itof:
                return VfReady(fs, dest);
            case UpperKind.Clip:
                return Math.Max(VfReady(fs, 0xE), VfReady(ft, 0x1));
            default:
                var ready = VfReady(fs, dest);
                var operand = s_upperOperand[key];
                if (operand == OperandVector)
                {
                    ready = Math.Max(ready, VfReady(ft, dest));
                }
                else if (operand == OperandBroadcast)
                {
                    ready = Math.Max(ready, VfReady(ft, 8 >> (int)(instr & 3)));
                }

                return ready;
        }
    }

    /// <summary>
    /// Runs an upper instruction. A VF result goes to the stage (written after the lower instruction has read its
    /// operands); ACC is written at once, since no lower instruction reads it.
    /// </summary>
    private void ExecuteUpper(uint instr)
    {
        var key = UpperKey(instr);
        var kind = s_upperKind[key];
        if (kind <= UpperKind.Nop)
        {
            return;
        }

        var dest = (int)(instr >> 21) & 0xF;
        var ft = (int)(instr >> 16) & 0x1F;
        var fs = (int)(instr >> 11) & 0x1F;
        var fd = (int)(instr >> 6) & 0x1F;
        switch (kind)
        {
            case UpperKind.OpMula:
                OuterProduct(fs, ft, -1);
                break;
            case UpperKind.OpMsub:
                OuterProduct(fs, ft, fd);
                break;
            case UpperKind.Abs:
                for (var lane = 0; lane < 4; lane++)
                {
                    _stage[lane] = _vf[(fs << 2) | lane] & Ps2VuFloat.Max;
                }

                SetStage(ft, dest);
                break;
            case UpperKind.Ftoi:
                var toInt = s_fixedPoint[instr & 3];
                for (var lane = 0; lane < 4; lane++)
                {
                    _stage[lane] = Ps2VuFloat.ToInt(_vf[(fs << 2) | lane], toInt);
                }

                SetStage(ft, dest);
                break;
            case UpperKind.Itof:
                var fromInt = s_fixedPoint[instr & 3];
                for (var lane = 0; lane < 4; lane++)
                {
                    _stage[lane] = Ps2VuFloat.FromInt(_vf[(fs << 2) | lane], fromInt);
                }

                SetStage(ft, dest);
                break;
            case UpperKind.Clip:
                Clip(fs, ft);
                break;
            default:
                Arithmetic(kind, s_upperOperand[key], s_upperToAcc[key], fd, fs, ft, (int)(instr & 3), dest);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetStage(int reg, int mask)
    {
        _stageReg = reg;
        _stageMask = mask;
    }

    private void Arithmetic(UpperKind kind, byte operand, bool toAcc, int fd, int fs, int ft, int bc, int dest)
    {
        var scalar = operand switch
        {
            OperandBroadcast => _vf[(ft << 2) | bc],
            OperandI => _i,
            OperandQ => _q,
            _ => 0u,
        };

        var target = toAcc ? _acc : _stage;
        var mac = 0;
        for (var lane = 0; lane < 4; lane++)
        {
            if ((dest & (8 >> lane)) == 0)
            {
                continue;
            }

            var s = _vf[(fs << 2) | lane];
            var t = operand == OperandVector ? _vf[(ft << 2) | lane] : scalar;
            uint result;
            var ou = 0;
            switch (kind)
            {
                case UpperKind.Add:
                    result = Ps2VuFloat.Add(s, t, out ou);
                    break;
                case UpperKind.Sub:
                    result = Ps2VuFloat.Sub(s, t, out ou);
                    break;
                case UpperKind.Mul:
                    result = Ps2VuFloat.Mul(s, t, out ou);
                    break;
                case UpperKind.Madd:
                    result = Ps2VuFloat.Add(_acc[lane], Ps2VuFloat.Mul(s, t, out _), out ou);
                    break;
                case UpperKind.Msub:
                    result = Ps2VuFloat.Sub(_acc[lane], Ps2VuFloat.Mul(s, t, out _), out ou);
                    break;
                case UpperKind.Max:
                    result = Ps2VuFloat.OrderKey(s) >= Ps2VuFloat.OrderKey(t) ? s : t;
                    break;
                default: // Mini
                    result = Ps2VuFloat.OrderKey(s) < Ps2VuFloat.OrderKey(t) ? s : t;
                    break;
            }

            target[lane] = result;
            mac |= Ps2VuFloat.MacBits(result, ou, lane);
        }

        if (!toAcc)
        {
            SetStage(fd, dest);
        }

        if (kind != UpperKind.Max && kind != UpperKind.Mini)
        {
            QueueFlag(FlagKindMac, mac);
        }
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
