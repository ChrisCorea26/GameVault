using System.Numerics;
using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.Nintendo64;

/// <summary>
/// The RSP's vector unit (COP2): 32 registers of eight 16-bit lanes, a 48-bit accumulator per lane, the VCO, VCC and
/// VCE flags, and the divide unit behind VRCP/VRSQ.
/// </summary>
/// <remarks>
/// References: n64brew "Reality Signal Processor/CPU Core" (element selectors, the instructions, the reciprocal ROMs) and
/// the hardware results recorded in PeterLemon's RSPTest programs (the reserved opcodes, VSAR, VRCP/VRSQ).
/// Lane <c>n</c> of a register is bytes <c>2n</c> and <c>2n+1</c> of its big-endian image; lane <c>n</c> of a flag
/// register is bit <c>n</c> (VCO's and VCC's upper halves are bits 8–15).
/// </remarks>
public sealed partial class N64Rsp
{
    /// <summary>Lane <c>n</c> of each element selector <c>e</c>: whole, quarters, halves, then a single lane.</summary>
    private static readonly byte[] ElementLanes = BuildElementLanes();

    private readonly ushort[] _vr = new ushort[32 * 8];
    private readonly long[] _acc = new long[8]; // 48-bit, sign-extended
    private readonly ushort[] _vte = new ushort[8];
    private readonly ushort[] _scratch = new ushort[8];

    // The previous vector instruction's vt operand (with its element selector), which some reserved opcodes add.
    private readonly ushort[] _lastVte = new ushort[8];

    private ushort _vco; // bits 0–7: carry (VCOL), 8–15: not-equal (VCOH)
    private ushort _vcc; // bits 0–7: compare (VCCL), 8–15: clip (VCCH)
    private byte _vce;

    private short _divIn;
    private short _divOut;
    private bool _divInLoaded;

    /// <summary>A lane of a vector register, for tests and debugging.</summary>
    public ushort Vector(int register, int lane) => _vr[register * 8 + lane];

    /// <summary>A lane's 48-bit accumulator, sign-extended.</summary>
    public long Accumulator(int lane) => _acc[lane];

    public ushort Vco => _vco;

    public ushort Vcc => _vcc;

    public byte Vce => _vce;

    private static byte[] BuildElementLanes()
    {
        var lanes = new byte[16 * 8];
        for (var e = 0; e < 16; e++)
        {
            for (var n = 0; n < 8; n++)
            {
                lanes[e * 8 + n] = (byte)(e switch
                {
                    < 2 => n,
                    < 4 => (n & 6) | (e & 1),
                    < 8 => (n & 4) | (e & 3),
                    _ => e & 7,
                });
            }
        }

        return lanes;
    }

    private void ResetVector()
    {
        Array.Clear(_vr);
        Array.Clear(_acc);
        Array.Clear(_lastVte);
        _vco = _vcc = 0;
        _vce = 0;
        _divIn = _divOut = 0;
        _divInLoaded = false;
    }

    // ---- Register bytes -------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetByte(int register, int index)
    {
        var lane = _vr[register * 8 + (index >> 1)];
        return (byte)((index & 1) == 0 ? lane >> 8 : lane);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetByte(int register, int index, byte value)
    {
        ref var lane = ref _vr[register * 8 + (index >> 1)];
        lane = (index & 1) == 0 ? (ushort)((lane & 0x00FF) | (value << 8)) : (ushort)((lane & 0xFF00) | value);
    }

    // ---- Accumulator ----------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Wrap48(long value) => (value << 16) >> 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort AccHigh(int n) => (ushort)(_acc[n] >> 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort AccMid(int n) => (ushort)(_acc[n] >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort AccLow(int n) => (ushort)_acc[n];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetAccLow(int n, int value) => _acc[n] = (_acc[n] & ~0xFFFFL) | (ushort)value;

    /// <summary>
    /// Clamps the accumulator's upper 32 bits to a signed 16-bit value: <paramref name="negative"/> or
    /// <paramref name="positive"/> when out of range, otherwise its middle (or, with <paramref name="low"/>, low) lane.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort Saturate(int n, bool low, ushort negative, ushort positive)
    {
        var upper = _acc[n] >> 16;
        if (upper < short.MinValue)
        {
            return negative;
        }

        if (upper > short.MaxValue)
        {
            return positive;
        }

        return low ? AccLow(n) : AccMid(n);
    }

    /// <summary>The unsigned clamp of VMULU and VMACU: 0 when negative, $FFFF past 15 bits, otherwise the middle lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort SaturateUnsigned(int n)
    {
        var high = (short)AccHigh(n);
        if (high < 0)
        {
            return 0;
        }

        return high != 0 || (short)AccMid(n) < 0 ? (ushort)0xFFFF : AccMid(n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ClampSigned(int value) => (ushort)Math.Clamp(value, short.MinValue, short.MaxValue);

    // ---- COP2 -----------------------------------------------------------------------------------------------------

    private void ExecuteCop2(uint instruction, int rs, int rt)
    {
        if ((instruction & (1u << 25)) != 0)
        {
            ExecuteVector(instruction);
            return;
        }

        var rd = (int)((instruction >> 11) & 31);
        var element = (int)((instruction >> 7) & 15);
        switch (rs)
        {
            case 0x00: // MFC2
                _gpr[rt] = (uint)(short)(GetByte(rd, element) << 8 | GetByte(rd, (element + 1) & 15));
                break;
            case 0x02: // CFC2
                _gpr[rt] = (rd & 3) switch
                {
                    0 => (uint)(short)_vco,
                    1 => (uint)(short)_vcc,
                    _ => _vce,
                };
                break;
            case 0x04: // MTC2
                SetByte(rd, element, (byte)(_gpr[rt] >> 8));
                if (element != 15)
                {
                    SetByte(rd, element + 1, (byte)_gpr[rt]);
                }

                break;
            case 0x06: // CTC2
                switch (rd & 3)
                {
                    case 0: _vco = (ushort)_gpr[rt]; break;
                    case 1: _vcc = (ushort)_gpr[rt]; break;
                    default: _vce = (byte)_gpr[rt]; break;
                }

                break;
        }
    }

    private void ExecuteVector(uint instruction)
    {
        var e = (int)((instruction >> 21) & 15);
        var vt = (int)((instruction >> 16) & 31);
        var vs = (int)((instruction >> 11) & 31);
        var vd = (int)((instruction >> 6) & 31);

        // vt with the element selector applied.
        var lanes = e * 8;
        for (var n = 0; n < 8; n++)
        {
            _vte[n] = _vr[vt * 8 + ElementLanes[lanes + n]];
        }

        var s = vs * 8;
        var d = vd * 8;
        switch (instruction & 0x3F)
        {
            case 0x00: Multiply(s, d, unsigned: false); break; // VMULF
            case 0x01: Multiply(s, d, unsigned: true); break; // VMULU
            case 0x02: Round(vs, d, positive: true); break; // VRNDP
            case 0x03: MultiplyQuantized(s, d); break; // VMULQ
            case 0x04: // VMUDL
                for (var n = 0; n < 8; n++)
                {
                    _acc[n] = (long)(((uint)_vr[s + n] * _vte[n]) >> 16);
                    _vr[d + n] = AccLow(n);
                }

                break;
            case 0x05: // VMUDM
                for (var n = 0; n < 8; n++)
                {
                    _acc[n] = (long)(short)_vr[s + n] * _vte[n];
                    _vr[d + n] = AccMid(n);
                }

                break;
            case 0x06: // VMUDN
                for (var n = 0; n < 8; n++)
                {
                    _acc[n] = (long)_vr[s + n] * (short)_vte[n];
                    _vr[d + n] = AccLow(n);
                }

                break;
            case 0x07: // VMUDH
                for (var n = 0; n < 8; n++)
                {
                    _acc[n] = ((long)(short)_vr[s + n] * (short)_vte[n]) << 16;
                    _vr[d + n] = Saturate(n, false, 0x8000, 0x7FFF);
                }

                break;
            case 0x08: MultiplyAccumulate(s, d, unsigned: false); break; // VMACF
            case 0x09: MultiplyAccumulate(s, d, unsigned: true); break; // VMACU
            case 0x0A: Round(vs, d, positive: false); break; // VRNDN
            case 0x0B: MultiplyAccumulateQuantized(d); break; // VMACQ
            case 0x0C: // VMADL
                for (var n = 0; n < 8; n++)
                {
                    _acc[n] = Wrap48(_acc[n] + (long)(((uint)_vr[s + n] * _vte[n]) >> 16));
                    _vr[d + n] = Saturate(n, true, 0x0000, 0xFFFF);
                }

                break;
            case 0x0D: // VMADM
                for (var n = 0; n < 8; n++)
                {
                    _acc[n] = Wrap48(_acc[n] + (long)(short)_vr[s + n] * _vte[n]);
                    _vr[d + n] = Saturate(n, false, 0x8000, 0x7FFF);
                }

                break;
            case 0x0E: // VMADN
                for (var n = 0; n < 8; n++)
                {
                    _acc[n] = Wrap48(_acc[n] + (long)_vr[s + n] * (short)_vte[n]);
                    _vr[d + n] = Saturate(n, true, 0x0000, 0xFFFF);
                }

                break;
            case 0x0F: // VMADH
                for (var n = 0; n < 8; n++)
                {
                    _acc[n] = Wrap48(_acc[n] + (((long)(short)_vr[s + n] * (short)_vte[n]) << 16));
                    _vr[d + n] = Saturate(n, false, 0x8000, 0x7FFF);
                }

                break;
            case 0x10: // VADD
                for (var n = 0; n < 8; n++)
                {
                    var result = (short)_vr[s + n] + (short)_vte[n] + ((_vco >> n) & 1);
                    SetAccLow(n, result);
                    _vr[d + n] = ClampSigned(result);
                }

                _vco = 0;
                break;
            case 0x11: // VSUB
                for (var n = 0; n < 8; n++)
                {
                    var result = (short)_vr[s + n] - (short)_vte[n] - ((_vco >> n) & 1);
                    SetAccLow(n, result);
                    _vr[d + n] = ClampSigned(result);
                }

                _vco = 0;
                break;
            case 0x13: // VABS
                for (var n = 0; n < 8; n++)
                {
                    var sign = (short)_vr[s + n];
                    var value = (short)_vte[n];
                    if (sign < 0)
                    {
                        if (value == short.MinValue)
                        {
                            SetAccLow(n, 0x8000);
                            _vr[d + n] = 0x7FFF;
                        }
                        else
                        {
                            SetAccLow(n, -value);
                            _vr[d + n] = (ushort)-value;
                        }
                    }
                    else
                    {
                        var result = sign > 0 ? value : 0;
                        SetAccLow(n, result);
                        _vr[d + n] = (ushort)result;
                    }
                }

                break;
            case 0x14: // VADDC
                {
                    var carry = 0;
                    for (var n = 0; n < 8; n++)
                    {
                        var result = _vr[s + n] + _vte[n];
                        SetAccLow(n, result);
                        _vr[d + n] = (ushort)result;
                        carry |= (result >> 16) << n;
                    }

                    _vco = (ushort)carry;
                    break;
                }

            case 0x15: // VSUBC
                {
                    var flags = 0;
                    for (var n = 0; n < 8; n++)
                    {
                        var result = _vr[s + n] - _vte[n];
                        SetAccLow(n, result);
                        _vr[d + n] = (ushort)result;
                        flags |= (result < 0 ? 1 : 0) << n | (result != 0 ? 1 : 0) << (n + 8);
                    }

                    _vco = (ushort)flags;
                    break;
                }

            case 0x1D: // VSAR: reads the accumulator; leaves it unchanged
                for (var n = 0; n < 8; n++)
                {
                    _vr[d + n] = e switch
                    {
                        8 => AccHigh(n),
                        9 => AccMid(n),
                        10 => AccLow(n),
                        _ => 0,
                    };
                }

                break;
            case 0x20: Compare(s, d, CompareKind.LessThan); break; // VLT
            case 0x21: Compare(s, d, CompareKind.Equal); break; // VEQ
            case 0x22: Compare(s, d, CompareKind.NotEqual); break; // VNE
            case 0x23: Compare(s, d, CompareKind.GreaterOrEqual); break; // VGE
            case 0x24: ClipLow(s, d); break; // VCL
            case 0x25: ClipHigh(s, d); break; // VCH
            case 0x26: ClipReciprocal(s, d); break; // VCR
            case 0x27: // VMRG
                for (var n = 0; n < 8; n++)
                {
                    var result = ((_vcc >> n) & 1) != 0 ? _vr[s + n] : _vte[n];
                    SetAccLow(n, result);
                    _vr[d + n] = result;
                }

                _vco = 0;
                break;
            case 0x28: Logical(s, d, 0); break; // VAND
            case 0x29: Logical(s, d, 1); break; // VNAND
            case 0x2A: Logical(s, d, 2); break; // VOR
            case 0x2B: Logical(s, d, 3); break; // VNOR
            case 0x2C: Logical(s, d, 4); break; // VXOR
            case 0x2D: Logical(s, d, 5); break; // VNXOR
            case 0x30: Reciprocal(vt, e, vs, d, squareRoot: false, low: false); break; // VRCP
            case 0x31: Reciprocal(vt, e, vs, d, squareRoot: false, low: true); break; // VRCPL
            case 0x32: // VRCPH
            case 0x36: ReciprocalHigh(vt, e, vs, d); break; // VRSQH
            case 0x33: // VMOV
                {
                    var de = vs & 7;
                    _vr[d + de] = _vte[de];
                    for (var n = 0; n < 8; n++)
                    {
                        SetAccLow(n, _vte[n]);
                    }

                    break;
                }

            case 0x34: Reciprocal(vt, e, vs, d, squareRoot: true, low: false); break; // VRSQ
            case 0x35: Reciprocal(vt, e, vs, d, squareRoot: true, low: true); break; // VRSQL
            case 0x37: // VNOP
                return;
            case 0x3F: // VNULL: clears the accumulator's low lane
                for (var n = 0; n < 8; n++)
                {
                    SetAccLow(n, 0);
                }

                return;
            case 0x1C: // VSUM
            case 0x1E:
            case 0x1F:
            case 0x3C: // VINST
            case 0x3D: // VINSQ
            case 0x3E: // VINSN
                // These reserved opcodes add the previous vector instruction's vt operand into the accumulator's low
                // lane (PeterLemon's RSPTest VSAC, VACC, VEXTT/Q/N record this), and write zero.
                for (var n = 0; n < 8; n++)
                {
                    SetAccLow(n, AccLow(n) + _lastVte[n]);
                    _vr[d + n] = 0;
                }

                break;
            default:
                // The other reserved opcodes (VSUT, VADDB, VSUBB, VACCB, VSUCB, VSAD, VSAC, VEXTT/Q/N and the unnamed
                // ones): the adder still runs into the accumulator's low lane, and the result is zero.
                for (var n = 0; n < 8; n++)
                {
                    SetAccLow(n, (short)_vr[s + n] + (short)_vte[n]);
                    _vr[d + n] = 0;
                }

                break;
        }

        // VSAR reads the accumulator without the datapath, so it leaves the latched operand alone.
        if ((instruction & 0x3F) != 0x1D)
        {
            Array.Copy(_vte, _lastVte, 8);
        }
    }

    /// <summary>VMULF/VMULU: the fraction product, doubled and rounded.</summary>
    private void Multiply(int s, int d, bool unsigned)
    {
        for (var n = 0; n < 8; n++)
        {
            _acc[n] = (long)(short)_vr[s + n] * (short)_vte[n] * 2 + 0x8000;
            _vr[d + n] = unsigned ? SaturateUnsigned(n) : Saturate(n, false, 0x8000, 0x7FFF);
        }
    }

    /// <summary>VMACF/VMACU: the doubled fraction product added to the accumulator.</summary>
    private void MultiplyAccumulate(int s, int d, bool unsigned)
    {
        for (var n = 0; n < 8; n++)
        {
            _acc[n] = Wrap48(_acc[n] + (long)(short)_vr[s + n] * (short)_vte[n] * 2);
            _vr[d + n] = unsigned ? SaturateUnsigned(n) : Saturate(n, false, 0x8000, 0x7FFF);
        }
    }

    /// <summary>VMULQ (MPEG): the product, rounded toward zero by 31 when negative, into the accumulator's upper 32 bits.</summary>
    private void MultiplyQuantized(int s, int d)
    {
        for (var n = 0; n < 8; n++)
        {
            var product = (short)_vr[s + n] * (short)_vte[n];
            if (product < 0)
            {
                product += 31;
            }

            _acc[n] = (long)product << 16;
            _vr[d + n] = (ushort)(ClampSigned(product >> 1) & ~15);
        }
    }

    /// <summary>VMACQ (MPEG): rounds the accumulator's upper 32 bits by 32 toward zero, unless bit 5 is set.</summary>
    private void MultiplyAccumulateQuantized(int d)
    {
        for (var n = 0; n < 8; n++)
        {
            var product = (int)(_acc[n] >> 16);
            if (product < 0 && (product & (1 << 5)) == 0)
            {
                product += 32;
            }
            else if (product >= 32 && (product & (1 << 5)) == 0)
            {
                product -= 32;
            }

            _acc[n] = ((long)product << 16) | (ushort)_acc[n];
            _vr[d + n] = (ushort)(ClampSigned(product >> 1) & ~15);
        }
    }

    /// <summary>
    /// VRNDP/VRNDN (MPEG): adds vt (shifted up 16 when the vs field is odd) to accumulators that are non-negative
    /// (VRNDP) or negative (VRNDN).
    /// </summary>
    private void Round(int vsField, int d, bool positive)
    {
        for (var n = 0; n < 8; n++)
        {
            long product = (short)_vte[n];
            if ((vsField & 1) != 0)
            {
                product <<= 16;
            }

            var acc = _acc[n];
            if (positive ? acc >= 0 : acc < 0)
            {
                _acc[n] = Wrap48(acc + product);
            }

            _vr[d + n] = Saturate(n, false, 0x8000, 0x7FFF);
        }
    }

    private enum CompareKind
    {
        LessThan,
        Equal,
        NotEqual,
        GreaterOrEqual,
    }

    /// <summary>VLT/VEQ/VNE/VGE: sets VCC's low half, selects into the accumulator and vd, clears VCO and VCC's high half.</summary>
    private void Compare(int s, int d, CompareKind kind)
    {
        var vcc = 0;
        for (var n = 0; n < 8; n++)
        {
            var a = (short)_vr[s + n];
            var b = (short)_vte[n];
            var carry = ((_vco >> n) & 1) != 0;
            var notEqual = ((_vco >> (n + 8)) & 1) != 0;
            var result = kind switch
            {
                CompareKind.LessThan => a < b || (a == b && carry && notEqual),
                CompareKind.Equal => a == b && !notEqual,
                CompareKind.NotEqual => a != b || notEqual,
                _ => a > b || (a == b && (!carry || !notEqual)),
            };

            var selected = result ? _vr[s + n] : _vte[n];
            SetAccLow(n, selected);
            _vr[d + n] = selected;
            vcc |= (result ? 1 : 0) << n;
        }

        _vcc = (ushort)vcc;
        _vco = 0;
    }

    /// <summary>VCL: the low half of a double-precision clip, using the flags VCH left.</summary>
    private void ClipLow(int s, int d)
    {
        var vcc = _vcc;
        for (var n = 0; n < 8; n++)
        {
            var a = _vr[s + n];
            var b = _vte[n];
            var low = 1 << n;
            var high = 1 << (n + 8);
            ushort result;
            if ((_vco & low) != 0)
            {
                if ((_vco & high) != 0)
                {
                    result = (vcc & low) != 0 ? (ushort)-b : a;
                }
                else
                {
                    var sum = a + b;
                    var carry = sum > 0xFFFF;
                    var zero = (ushort)sum == 0;
                    var lessOrEqual = (_vce & (1 << n)) != 0 ? zero || !carry : zero && !carry;
                    vcc = (ushort)(lessOrEqual ? vcc | low : vcc & ~low);
                    result = lessOrEqual ? (ushort)-b : a;
                }
            }
            else
            {
                if ((_vco & high) != 0)
                {
                    result = (vcc & high) != 0 ? b : a;
                }
                else
                {
                    var greaterOrEqual = a - b >= 0;
                    vcc = (ushort)(greaterOrEqual ? vcc | high : vcc & ~high);
                    result = greaterOrEqual ? b : a;
                }
            }

            SetAccLow(n, result);
            _vr[d + n] = result;
        }

        _vcc = vcc;
        _vco = 0;
        _vce = 0;
    }

    /// <summary>VCH: the high half of a double-precision clip (or a single-precision clip), setting VCO, VCC and VCE.</summary>
    private void ClipHigh(int s, int d)
    {
        int vco = 0, vcc = 0, vce = 0;
        for (var n = 0; n < 8; n++)
        {
            var a = (short)_vr[s + n];
            var b = (short)_vte[n];
            short result;
            if ((a ^ b) < 0)
            {
                var sum = (short)(a + b);
                result = sum <= 0 ? (short)-b : a;
                vcc |= (sum <= 0 ? 1 : 0) << n | (b < 0 ? 1 : 0) << (n + 8);
                vco |= 1 << n | (sum != 0 && (ushort)a != ((ushort)b ^ 0xFFFF) ? 1 : 0) << (n + 8);
                vce |= (sum == -1 ? 1 : 0) << n;
            }
            else
            {
                var difference = (short)(a - b);
                result = difference >= 0 ? b : a;
                vcc |= (b < 0 ? 1 : 0) << n | (difference >= 0 ? 1 : 0) << (n + 8);
                vco |= (difference != 0 && (ushort)a != ((ushort)b ^ 0xFFFF) ? 1 : 0) << (n + 8);
            }

            SetAccLow(n, result);
            _vr[d + n] = (ushort)result;
        }

        _vco = (ushort)vco;
        _vcc = (ushort)vcc;
        _vce = (byte)vce;
    }

    /// <summary>VCR: a single-precision clip against a one's-complement range.</summary>
    private void ClipReciprocal(int s, int d)
    {
        var vcc = 0;
        for (var n = 0; n < 8; n++)
        {
            var a = (short)_vr[s + n];
            var b = (short)_vte[n];
            ushort result;
            if ((a ^ b) < 0)
            {
                var lessOrEqual = a + b + 1 <= 0;
                vcc |= (lessOrEqual ? 1 : 0) << n | (b < 0 ? 1 : 0) << (n + 8);
                result = lessOrEqual ? (ushort)~b : (ushort)a;
            }
            else
            {
                var greaterOrEqual = a - b >= 0;
                vcc |= (b < 0 ? 1 : 0) << n | (greaterOrEqual ? 1 : 0) << (n + 8);
                result = greaterOrEqual ? (ushort)b : (ushort)a;
            }

            SetAccLow(n, result);
            _vr[d + n] = result;
        }

        _vcc = (ushort)vcc;
        _vco = 0;
        _vce = 0;
    }

    private void Logical(int s, int d, int kind)
    {
        for (var n = 0; n < 8; n++)
        {
            var a = _vr[s + n];
            var b = _vte[n];
            var result = kind switch
            {
                0 => a & b,
                1 => ~(a & b),
                2 => a | b,
                3 => ~(a | b),
                4 => a ^ b,
                _ => ~(a ^ b),
            };

            SetAccLow(n, result);
            _vr[d + n] = (ushort)result;
        }
    }

    /// <summary>
    /// VRCP/VRCPL/VRSQ/VRSQL: looks the input up in the reciprocal (or inverse square root) ROM and writes the low 16
    /// bits of the 32-bit result to lane <paramref name="vsField"/> of vd; the high 16 bits wait in DIVOUT. The "L" forms
    /// take a 32-bit input when VRCPH/VRSQH loaded the high half first.
    /// </summary>
    private void Reciprocal(int vt, int e, int vsField, int d, bool squareRoot, bool low)
    {
        var lane = _vr[vt * 8 + (e & 7)];
        var input = low && _divInLoaded ? _divIn << 16 | lane : (short)lane;
        var mask = input >> 31;
        var data = input ^ mask;
        if (input > short.MinValue)
        {
            data -= mask;
        }

        int result;
        if (data == 0)
        {
            result = 0x7FFFFFFF;
        }
        else if (input == short.MinValue)
        {
            result = unchecked((int)0xFFFF0000);
        }
        else
        {
            var shift = BitOperations.LeadingZeroCount((uint)data);
            var index = (int)((((ulong)(uint)data << shift) & 0x7FC00000) >> 22);
            if (squareRoot)
            {
                result = (0x10000 | InverseSquareRoots[(index & 0x1FE) | (shift & 1)]) << 14;
                result = (int)((uint)result >> ((31 - shift) >> 1)) ^ mask;
            }
            else
            {
                result = (0x10000 | Reciprocals[index]) << 14;
                result = (int)((uint)result >> (31 - shift)) ^ mask;
            }
        }

        _divInLoaded = false;
        _divOut = (short)(result >> 16);
        for (var n = 0; n < 8; n++)
        {
            SetAccLow(n, _vte[n]);
        }

        _vr[d + (vsField & 7)] = (ushort)result;
    }

    /// <summary>VRCPH/VRSQH: loads the high half of a 32-bit input and reads the last result's high half.</summary>
    private void ReciprocalHigh(int vt, int e, int vsField, int d)
    {
        for (var n = 0; n < 8; n++)
        {
            SetAccLow(n, _vte[n]);
        }

        _divInLoaded = true;
        _divIn = (short)_vr[vt * 8 + (e & 7)];
        _vr[d + (vsField & 7)] = (ushort)_divOut;
    }

    // ---- Loads and stores (LWC2/SWC2) -----------------------------------------------------------------------------

    private void LoadVector(uint instruction, int rs, int vt)
    {
        var e = (int)((instruction >> 7) & 15);
        var offset = (int)((instruction & 0x7F) ^ 0x40) - 0x40; // signed 7 bits
        var baseAddress = _gpr[rs];
        switch ((instruction >> 11) & 31)
        {
            case 0x00: LoadBytes(vt, e, baseAddress + (uint)offset, 1); break; // LBV
            case 0x01: LoadBytes(vt, e, baseAddress + (uint)(offset * 2), 2); break; // LSV
            case 0x02: LoadBytes(vt, e, baseAddress + (uint)(offset * 4), 4); break; // LLV
            case 0x03: LoadBytes(vt, e, baseAddress + (uint)(offset * 8), 8); break; // LDV
            case 0x04: // LQV: up to the end of the 16-byte block
                {
                    var address = baseAddress + (uint)(offset * 16);
                    var end = Math.Min(16 + e - (int)(address & 15), 16);
                    for (var i = e; i < end; i++)
                    {
                        SetByte(vt, i & 15, Dmem[address++ & 0xFFF]);
                    }

                    break;
                }

            case 0x05: // LRV: from the start of the 16-byte block up to the address
                {
                    var address = baseAddress + (uint)(offset * 16);
                    var start = 16 - ((int)(address & 15) - e);
                    address &= ~15u;
                    for (var i = start; i < 16; i++)
                    {
                        SetByte(vt, i & 15, Dmem[address++ & 0xFFF]);
                    }

                    break;
                }

            case 0x06: LoadPacked(vt, e, baseAddress + (uint)(offset * 8), 8, 1); break; // LPV
            case 0x07: LoadPacked(vt, e, baseAddress + (uint)(offset * 8), 7, 1); break; // LUV
            case 0x08: LoadPacked(vt, e, baseAddress + (uint)(offset * 16), 7, 2); break; // LHV
            case 0x09: LoadFourth(vt, e, baseAddress + (uint)(offset * 16)); break; // LFV
            case 0x0A: break; // LWV: does nothing on hardware (PeterLemon RSPTest LWV)
            case 0x0B: LoadTranspose(vt, e, baseAddress + (uint)(offset * 16)); break; // LTV
            default: break;
        }
    }

    private void LoadBytes(int vt, int e, uint address, int count)
    {
        var end = Math.Min(e + count, 16);
        for (var i = e; i < end; i++)
        {
            SetByte(vt, i, Dmem[address++ & 0xFFF]);
        }
    }

    /// <summary>LPV/LUV/LHV: one byte per lane, from an 8-byte-aligned block rotated by the element.</summary>
    private void LoadPacked(int vt, int e, uint address, int shift, int stride)
    {
        var index = (int)(address & 7) - e;
        address &= ~7u;
        for (var n = 0; n < 8; n++)
        {
            _vr[vt * 8 + n] = (ushort)(Dmem[(address + (uint)((index + n * stride) & 15)) & 0xFFF] << shift);
        }
    }

    /// <summary>LFV: every fourth byte into four lanes, then the bytes from the element on copied into vt.</summary>
    private void LoadFourth(int vt, int e, uint address)
    {
        var index = (int)(address & 7) - e;
        address &= ~7u;
        for (var n = 0; n < 4; n++)
        {
            _scratch[n] = (ushort)(Dmem[(address + (uint)((index + n * 4) & 15)) & 0xFFF] << 7);
            _scratch[n + 4] = (ushort)(Dmem[(address + (uint)((index + n * 4 + 8) & 15)) & 0xFFF] << 7);
        }

        var end = Math.Min(e + 8, 16);
        for (var i = e; i < end; i++)
        {
            var lane = _scratch[i >> 1];
            SetByte(vt, i, (byte)((i & 1) == 0 ? lane >> 8 : lane));
        }
    }

    /// <summary>LTV: a diagonal of a block of eight registers, read from a 16-byte block that wraps.</summary>
    private void LoadTranspose(int vt, int e, uint address)
    {
        var begin = address & ~7u;
        address = begin + (uint)((e + (int)(address & 8)) & 15);
        var registerBase = vt & ~7;
        var registerOffset = e >> 1;
        for (var i = 0; i < 8; i++)
        {
            var register = registerBase + registerOffset;
            SetByte(register, i * 2, Dmem[address++ & 0xFFF]);
            if (address == begin + 16)
            {
                address = begin;
            }

            SetByte(register, i * 2 + 1, Dmem[address++ & 0xFFF]);
            if (address == begin + 16)
            {
                address = begin;
            }

            registerOffset = (registerOffset + 1) & 7;
        }
    }

    private void StoreVector(uint instruction, int rs, int vt)
    {
        var e = (int)((instruction >> 7) & 15);
        var offset = (int)((instruction & 0x7F) ^ 0x40) - 0x40;
        var baseAddress = _gpr[rs];
        switch ((instruction >> 11) & 31)
        {
            case 0x00: StoreBytes(vt, e, baseAddress + (uint)offset, 1); break; // SBV
            case 0x01: StoreBytes(vt, e, baseAddress + (uint)(offset * 2), 2); break; // SSV
            case 0x02: StoreBytes(vt, e, baseAddress + (uint)(offset * 4), 4); break; // SLV
            case 0x03: StoreBytes(vt, e, baseAddress + (uint)(offset * 8), 8); break; // SDV
            case 0x04: // SQV
                {
                    var address = baseAddress + (uint)(offset * 16);
                    var end = e + (16 - (int)(address & 15));
                    for (var i = e; i < end; i++)
                    {
                        Dmem[address++ & 0xFFF] = GetByte(vt, i & 15);
                    }

                    break;
                }

            case 0x05: // SRV
                {
                    var address = baseAddress + (uint)(offset * 16);
                    var end = e + (int)(address & 15);
                    var rotate = 16 - (int)(address & 15);
                    address &= ~15u;
                    for (var i = e; i < end; i++)
                    {
                        Dmem[address++ & 0xFFF] = GetByte(vt, (i + rotate) & 15);
                    }

                    break;
                }

            case 0x06: StorePacked(vt, e, baseAddress + (uint)(offset * 8), unsigned: false); break; // SPV
            case 0x07: StorePacked(vt, e, baseAddress + (uint)(offset * 8), unsigned: true); break; // SUV
            case 0x08: // SHV
                {
                    var address = baseAddress + (uint)(offset * 16);
                    var index = (int)(address & 7);
                    address &= ~7u;
                    for (var n = 0; n < 8; n++)
                    {
                        var b = e + n * 2;
                        var value = (byte)(GetByte(vt, b & 15) << 1 | GetByte(vt, (b + 1) & 15) >> 7);
                        Dmem[(address + (uint)((index + n * 2) & 15)) & 0xFFF] = value;
                    }

                    break;
                }

            case 0x09: StoreFourth(vt, e, baseAddress + (uint)(offset * 16)); break; // SFV
            case 0x0A: // SWV: the register rotated by the element, wrapping within an 8-byte-aligned 16-byte block
                {
                    var address = baseAddress + (uint)(offset * 16);
                    var position = (int)(address & 7);
                    address &= ~7u;
                    for (var i = e; i < e + 16; i++)
                    {
                        Dmem[(address + (uint)(position & 15)) & 0xFFF] = GetByte(vt, i & 15);
                        position++;
                    }

                    break;
                }

            case 0x0B: StoreTranspose(vt, e, baseAddress + (uint)(offset * 16)); break; // STV
            default: break;
        }
    }

    private void StoreBytes(int vt, int e, uint address, int count)
    {
        for (var i = e; i < e + count; i++)
        {
            Dmem[address++ & 0xFFF] = GetByte(vt, i & 15);
        }
    }

    /// <summary>SPV/SUV: lanes 0–7 packed one byte each, as signed (SPV, the upper byte) or unsigned (SUV, bits 14–7).</summary>
    private void StorePacked(int vt, int e, uint address, bool unsigned)
    {
        for (var i = e; i < e + 8; i++)
        {
            var lane = _vr[vt * 8 + (i & 7)];
            var upperByte = (byte)(lane >> 8);
            var shifted = (byte)(lane >> 7);
            var firstHalf = (i & 15) < 8;
            Dmem[address++ & 0xFFF] = firstHalf != unsigned ? upperByte : shifted;
        }
    }

    /// <summary>SFV: four lanes (bits 14–7), every fourth byte; which lanes depends on the element, and others store zeros.</summary>
    private void StoreFourth(int vt, int e, uint address)
    {
        var index = (int)(address & 7);
        address &= ~7u;
        ReadOnlySpan<int> order = e switch
        {
            0 or 15 => [0, 1, 2, 3],
            1 => [6, 7, 4, 5],
            4 => [1, 2, 3, 0],
            5 => [7, 4, 5, 6],
            8 => [4, 5, 6, 7],
            11 => [3, 0, 1, 2],
            12 => [5, 6, 7, 4],
            _ => [-1, -1, -1, -1],
        };

        for (var n = 0; n < 4; n++)
        {
            var value = order[n] < 0 ? (byte)0 : (byte)(_vr[vt * 8 + order[n]] >> 7);
            Dmem[(address + (uint)((index + n * 4) & 15)) & 0xFFF] = value;
        }
    }

    /// <summary>STV: a diagonal of a block of eight registers, written to a 16-byte block that wraps.</summary>
    private void StoreTranspose(int vt, int e, uint address)
    {
        var start = vt & ~7;
        var element = 16 - (e & ~1);
        var position = (int)(address & 7) - (e & ~1);
        address &= ~7u;
        for (var register = start; register < start + 8; register++)
        {
            Dmem[(address + (uint)(position++ & 15)) & 0xFFF] = GetByte(register, element++ & 15);
            Dmem[(address + (uint)(position++ & 15)) & 0xFFF] = GetByte(register, element++ & 15);
        }
    }

    private void SaveVectorState(StateWriter writer)
    {
        foreach (var lane in _vr)
        {
            writer.WriteUInt16(lane);
        }

        foreach (var acc in _acc)
        {
            writer.WriteInt64(acc);
        }

        writer.WriteUInt16(_vco);
        writer.WriteUInt16(_vcc);
        writer.WriteByte(_vce);
        writer.WriteUInt16((ushort)_divIn);
        writer.WriteUInt16((ushort)_divOut);
        writer.WriteBool(_divInLoaded);
        foreach (var lane in _lastVte)
        {
            writer.WriteUInt16(lane);
        }
    }

    private void LoadVectorState(StateReader reader)
    {
        for (var i = 0; i < _vr.Length; i++)
        {
            _vr[i] = reader.ReadUInt16();
        }

        for (var i = 0; i < _acc.Length; i++)
        {
            _acc[i] = Wrap48(reader.ReadInt64());
        }

        _vco = reader.ReadUInt16();
        _vcc = reader.ReadUInt16();
        _vce = reader.ReadByte();
        _divIn = (short)reader.ReadUInt16();
        _divOut = (short)reader.ReadUInt16();
        _divInLoaded = reader.ReadBool();
        for (var i = 0; i < _lastVte.Length; i++)
        {
            _lastVte[i] = reader.ReadUInt16();
        }
    }
}
