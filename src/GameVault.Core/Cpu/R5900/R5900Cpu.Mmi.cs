using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GameVault.Core.Cpu.R5900;

/// <summary>
/// The MultiMedia Instructions (ps2tek: "EE Instruction Decoding", MMI and MMI0–3): 128-bit SIMD integer operations
/// on bytes, halfwords and words, the pipeline-1 multiply/divide forms and the parallel multiplies into the 128-bit
/// HI/LO (whose upper halves are HI1/LO1).
/// </summary>
public sealed partial class R5900Cpu
{
    /// <summary>A 128-bit register viewed as lanes. Copies the register, so a result can overwrite a source.</summary>
    [InlineArray(16)]
    private struct Quad
    {
        private byte _element;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Quad Read(int register)
    {
        var quad = default(Quad);
        Unsafe.As<Quad, ulong>(ref quad) = Gpr(register);
        Unsafe.Add(ref Unsafe.As<Quad, ulong>(ref quad), 1) = GprHigh(register);
        return quad;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Write(int register, ref Quad value)
    {
        Gpr(register) = Unsafe.As<Quad, ulong>(ref value);
        GprHigh(register) = Unsafe.Add(ref Unsafe.As<Quad, ulong>(ref value), 1);
    }

    private static Span<short> H(ref Quad q) => MemoryMarshal.CreateSpan(ref Unsafe.As<Quad, short>(ref q), 8);

    private static Span<int> W(ref Quad q) => MemoryMarshal.CreateSpan(ref Unsafe.As<Quad, int>(ref q), 4);

    private static Span<sbyte> B(ref Quad q) => MemoryMarshal.CreateSpan(ref Unsafe.As<Quad, sbyte>(ref q), 16);

    private static Span<ulong> D(ref Quad q) => MemoryMarshal.CreateSpan(ref Unsafe.As<Quad, ulong>(ref q), 2);

    // The 128-bit LO and HI as words: words 0–1 are LO/HI, words 2–3 LO1/HI1.
    private uint LoWord(int i) => i switch { 0 => (uint)_lo, 1 => (uint)(_lo >> 32), 2 => (uint)_lo1, _ => (uint)(_lo1 >> 32) };

    private uint HiWord(int i) => i switch { 0 => (uint)_hi, 1 => (uint)(_hi >> 32), 2 => (uint)_hi1, _ => (uint)(_hi1 >> 32) };

    private void SetLoWord(int i, uint value)
    {
        switch (i)
        {
            case 0: _lo = (_lo & 0xFFFF_FFFF_0000_0000) | value; break;
            case 1: _lo = (_lo & 0xFFFF_FFFF) | ((ulong)value << 32); break;
            case 2: _lo1 = (_lo1 & 0xFFFF_FFFF_0000_0000) | value; break;
            default: _lo1 = (_lo1 & 0xFFFF_FFFF) | ((ulong)value << 32); break;
        }
    }

    private void SetHiWord(int i, uint value)
    {
        switch (i)
        {
            case 0: _hi = (_hi & 0xFFFF_FFFF_0000_0000) | value; break;
            case 1: _hi = (_hi & 0xFFFF_FFFF) | ((ulong)value << 32); break;
            case 2: _hi1 = (_hi1 & 0xFFFF_FFFF_0000_0000) | value; break;
            default: _hi1 = (_hi1 & 0xFFFF_FFFF) | ((ulong)value << 32); break;
        }
    }

    private static short SaturateHalf(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    private static sbyte SaturateByte(int value) => (sbyte)Math.Clamp(value, sbyte.MinValue, sbyte.MaxValue);

    private static int SaturateWord(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    private void ExecuteMmi(uint op, int rs, int rt)
    {
        var rd = (int)(op >> 11) & 31;
        var sa = (int)(op >> 6) & 31;
        switch (op & 0x3F)
        {
            case 0x00: // MADD
            {
                WaitHiLo();
                var accumulator = (long)(((ulong)(uint)_hi << 32) | (uint)_lo);
                SetHiLo(accumulator + (long)(int)Gpr(rs) * (int)Gpr(rt), rd);
                break;
            }

            case 0x01: // MADDU
            {
                WaitHiLo();
                var accumulator = ((ulong)(uint)_hi << 32) | (uint)_lo;
                SetHiLo((long)(accumulator + (ulong)(uint)Gpr(rs) * (uint)Gpr(rt)), rd);
                break;
            }

            case 0x04: // PLZCW: leading bits equal to the sign bit, minus one, of each low word
            {
                var value = Gpr(rs);
                Gpr(rd) = LeadingSignBits((uint)value) | ((ulong)LeadingSignBits((uint)(value >> 32)) << 32);
                break;
            }

            case 0x08:
                ExecuteMmi0(op, rs, rt, rd);
                break;
            case 0x09:
                ExecuteMmi2(op, rs, rt, rd);
                break;
            case 0x10: // MFHI1
                WaitHiLo1();
                Gpr(rd) = _hi1;
                break;
            case 0x11: // MTHI1
                _hi1 = Gpr(rs);
                break;
            case 0x12: // MFLO1
                WaitHiLo1();
                Gpr(rd) = _lo1;
                break;
            case 0x13: // MTLO1
                _lo1 = Gpr(rs);
                break;
            case 0x18: // MULT1
                SetHiLo1((long)(int)Gpr(rs) * (int)Gpr(rt), rd);
                break;
            case 0x19: // MULTU1
                SetHiLo1((long)((ulong)(uint)Gpr(rs) * (uint)Gpr(rt)), rd);
                break;
            case 0x1A: // DIV1
                Divide((int)Gpr(rs), (int)Gpr(rt), out _lo1, out _hi1);
                _hiLo1Ready = Cycles + DivCycles;
                break;
            case 0x1B: // DIVU1
                DivideUnsigned((uint)Gpr(rs), (uint)Gpr(rt), out _lo1, out _hi1);
                _hiLo1Ready = Cycles + DivCycles;
                break;
            case 0x20: // MADD1
            {
                WaitHiLo1();
                var accumulator = (long)(((ulong)(uint)_hi1 << 32) | (uint)_lo1);
                SetHiLo1(accumulator + (long)(int)Gpr(rs) * (int)Gpr(rt), rd);
                break;
            }

            case 0x21: // MADDU1
            {
                WaitHiLo1();
                var accumulator = ((ulong)(uint)_hi1 << 32) | (uint)_lo1;
                SetHiLo1((long)(accumulator + (ulong)(uint)Gpr(rs) * (uint)Gpr(rt)), rd);
                break;
            }

            case 0x28:
                ExecuteMmi1(op, rs, rt, rd);
                break;
            case 0x29:
                ExecuteMmi3(op, rs, rt, rd);
                break;
            case 0x30:
                Pmfhl(sa, rd);
                break;
            case 0x31: // PMTHL.LW
            {
                if (sa != 0)
                {
                    RaiseException(R5900Exception.ReservedInstruction);
                    return;
                }

                var s = Read(rs);
                var w = W(ref s);
                SetLoWord(0, (uint)w[0]);
                SetHiWord(0, (uint)w[1]);
                SetLoWord(2, (uint)w[2]);
                SetHiWord(2, (uint)w[3]);
                break;
            }

            case 0x34: // PSLLH
            case 0x36: // PSRLH
            case 0x37: // PSRAH
            {
                var t = Read(rt);
                var h = H(ref t);
                var shift = sa & 15;
                for (var i = 0; i < 8; i++)
                {
                    h[i] = (op & 0x3F) switch
                    {
                        0x34 => (short)(h[i] << shift),
                        0x36 => (short)((ushort)h[i] >> shift),
                        _ => (short)(h[i] >> shift),
                    };
                }

                Write(rd, ref t);
                break;
            }

            case 0x3C: // PSLLW
            case 0x3E: // PSRLW
            case 0x3F: // PSRAW
            {
                var t = Read(rt);
                var w = W(ref t);
                for (var i = 0; i < 4; i++)
                {
                    w[i] = (op & 0x3F) switch
                    {
                        0x3C => w[i] << sa,
                        0x3E => (int)((uint)w[i] >> sa),
                        _ => w[i] >> sa,
                    };
                }

                Write(rd, ref t);
                break;
            }

            default:
                RaiseException(R5900Exception.ReservedInstruction);
                break;
        }
    }

    private static uint LeadingSignBits(uint value)
    {
        var bits = (value & 0x8000_0000) != 0 ? ~value : value;
        return (uint)(BitOperations.LeadingZeroCount(bits) - 1);
    }

    /// <summary>PMFHL: LO/HI's words, upper words, saturated words, halfwords or saturated halfwords into rd.</summary>
    private void Pmfhl(int format, int rd)
    {
        WaitHiLo();
        WaitHiLo1();
        var d = default(Quad);
        var w = W(ref d);
        switch (format)
        {
            case 0: // LW
                w[0] = (int)LoWord(0);
                w[1] = (int)HiWord(0);
                w[2] = (int)LoWord(2);
                w[3] = (int)HiWord(2);
                break;
            case 1: // UW
                w[0] = (int)LoWord(1);
                w[1] = (int)HiWord(1);
                w[2] = (int)LoWord(3);
                w[3] = (int)HiWord(3);
                break;
            case 2: // SLW: HI:LO's low words as a 64-bit value, saturated to 32 bits and sign-extended
            {
                var dd = D(ref d);
                dd[0] = (ulong)(long)SaturateWord((long)(((ulong)HiWord(0) << 32) | LoWord(0)));
                dd[1] = (ulong)(long)SaturateWord((long)(((ulong)HiWord(2) << 32) | LoWord(2)));
                break;
            }

            case 3: // LH
            {
                var h = H(ref d);
                h[0] = (short)LoWord(0);
                h[1] = (short)LoWord(1);
                h[2] = (short)HiWord(0);
                h[3] = (short)HiWord(1);
                h[4] = (short)LoWord(2);
                h[5] = (short)LoWord(3);
                h[6] = (short)HiWord(2);
                h[7] = (short)HiWord(3);
                break;
            }

            case 4: // SH
            {
                var h = H(ref d);
                h[0] = SaturateHalf((int)LoWord(0));
                h[1] = SaturateHalf((int)LoWord(1));
                h[2] = SaturateHalf((int)HiWord(0));
                h[3] = SaturateHalf((int)HiWord(1));
                h[4] = SaturateHalf((int)LoWord(2));
                h[5] = SaturateHalf((int)LoWord(3));
                h[6] = SaturateHalf((int)HiWord(2));
                h[7] = SaturateHalf((int)HiWord(3));
                break;
            }

            default:
                RaiseException(R5900Exception.ReservedInstruction);
                return;
        }

        Write(rd, ref d);
    }

    private void ExecuteMmi0(uint op, int rs, int rt, int rd)
    {
        var s = Read(rs);
        var t = Read(rt);
        var d = default(Quad);
        switch ((op >> 6) & 0x1F)
        {
            case 0x00: // PADDW
                for (var i = 0; i < 4; i++) W(ref d)[i] = W(ref s)[i] + W(ref t)[i];
                break;
            case 0x01: // PSUBW
                for (var i = 0; i < 4; i++) W(ref d)[i] = W(ref s)[i] - W(ref t)[i];
                break;
            case 0x02: // PCGTW
                for (var i = 0; i < 4; i++) W(ref d)[i] = W(ref s)[i] > W(ref t)[i] ? -1 : 0;
                break;
            case 0x03: // PMAXW
                for (var i = 0; i < 4; i++) W(ref d)[i] = Math.Max(W(ref s)[i], W(ref t)[i]);
                break;
            case 0x04: // PADDH
                for (var i = 0; i < 8; i++) H(ref d)[i] = (short)(H(ref s)[i] + H(ref t)[i]);
                break;
            case 0x05: // PSUBH
                for (var i = 0; i < 8; i++) H(ref d)[i] = (short)(H(ref s)[i] - H(ref t)[i]);
                break;
            case 0x06: // PCGTH
                for (var i = 0; i < 8; i++) H(ref d)[i] = H(ref s)[i] > H(ref t)[i] ? (short)-1 : (short)0;
                break;
            case 0x07: // PMAXH
                for (var i = 0; i < 8; i++) H(ref d)[i] = Math.Max(H(ref s)[i], H(ref t)[i]);
                break;
            case 0x08: // PADDB
                for (var i = 0; i < 16; i++) B(ref d)[i] = (sbyte)(B(ref s)[i] + B(ref t)[i]);
                break;
            case 0x09: // PSUBB
                for (var i = 0; i < 16; i++) B(ref d)[i] = (sbyte)(B(ref s)[i] - B(ref t)[i]);
                break;
            case 0x0A: // PCGTB
                for (var i = 0; i < 16; i++) B(ref d)[i] = B(ref s)[i] > B(ref t)[i] ? (sbyte)-1 : (sbyte)0;
                break;
            case 0x10: // PADDSW
                for (var i = 0; i < 4; i++) W(ref d)[i] = SaturateWord((long)W(ref s)[i] + W(ref t)[i]);
                break;
            case 0x11: // PSUBSW
                for (var i = 0; i < 4; i++) W(ref d)[i] = SaturateWord((long)W(ref s)[i] - W(ref t)[i]);
                break;
            case 0x12: // PEXTLW
            {
                var sw = W(ref s);
                var tw = W(ref t);
                var dw = W(ref d);
                dw[0] = tw[0];
                dw[1] = sw[0];
                dw[2] = tw[1];
                dw[3] = sw[1];
                break;
            }

            case 0x13: // PPACW
            {
                var sw = W(ref s);
                var tw = W(ref t);
                var dw = W(ref d);
                dw[0] = tw[0];
                dw[1] = tw[2];
                dw[2] = sw[0];
                dw[3] = sw[2];
                break;
            }

            case 0x14: // PADDSH
                for (var i = 0; i < 8; i++) H(ref d)[i] = SaturateHalf(H(ref s)[i] + H(ref t)[i]);
                break;
            case 0x15: // PSUBSH
                for (var i = 0; i < 8; i++) H(ref d)[i] = SaturateHalf(H(ref s)[i] - H(ref t)[i]);
                break;
            case 0x16: // PEXTLH
                for (var i = 0; i < 4; i++)
                {
                    H(ref d)[i * 2] = H(ref t)[i];
                    H(ref d)[i * 2 + 1] = H(ref s)[i];
                }

                break;
            case 0x17: // PPACH
                for (var i = 0; i < 4; i++)
                {
                    H(ref d)[i] = H(ref t)[i * 2];
                    H(ref d)[i + 4] = H(ref s)[i * 2];
                }

                break;
            case 0x18: // PADDSB
                for (var i = 0; i < 16; i++) B(ref d)[i] = SaturateByte(B(ref s)[i] + B(ref t)[i]);
                break;
            case 0x19: // PSUBSB
                for (var i = 0; i < 16; i++) B(ref d)[i] = SaturateByte(B(ref s)[i] - B(ref t)[i]);
                break;
            case 0x1A: // PEXTLB
                for (var i = 0; i < 8; i++)
                {
                    d[i * 2] = t[i];
                    d[i * 2 + 1] = s[i];
                }

                break;
            case 0x1B: // PPACB
                for (var i = 0; i < 8; i++)
                {
                    d[i] = t[i * 2];
                    d[i + 8] = s[i * 2];
                }

                break;
            case 0x1E: // PEXT5: 1-5-5-5 halfwords to 8-bit-per-channel words
                for (var i = 0; i < 4; i++)
                {
                    var c = (uint)W(ref t)[i];
                    W(ref d)[i] = (int)(((c & 0x1F) << 3) | ((c & 0x3E0) << 6) | ((c & 0x7C00) << 9) | ((c & 0x8000) << 16));
                }

                break;
            case 0x1F: // PPAC5: the reverse
                for (var i = 0; i < 4; i++)
                {
                    var c = (uint)W(ref t)[i];
                    W(ref d)[i] = (int)(((c >> 3) & 0x1F) | (((c >> 11) & 0x1F) << 5) | (((c >> 19) & 0x1F) << 10) | ((c >> 16) & 0x8000));
                }

                break;
            default:
                RaiseException(R5900Exception.ReservedInstruction);
                return;
        }

        Write(rd, ref d);
    }

    private void ExecuteMmi1(uint op, int rs, int rt, int rd)
    {
        var s = Read(rs);
        var t = Read(rt);
        var d = default(Quad);
        switch ((op >> 6) & 0x1F)
        {
            case 0x01: // PABSW (−2³¹ saturates to 2³¹−1)
                for (var i = 0; i < 4; i++)
                {
                    var v = W(ref t)[i];
                    W(ref d)[i] = v == int.MinValue ? int.MaxValue : Math.Abs(v);
                }

                break;
            case 0x02: // PCEQW
                for (var i = 0; i < 4; i++) W(ref d)[i] = W(ref s)[i] == W(ref t)[i] ? -1 : 0;
                break;
            case 0x03: // PMINW
                for (var i = 0; i < 4; i++) W(ref d)[i] = Math.Min(W(ref s)[i], W(ref t)[i]);
                break;
            case 0x04: // PADSBH: subtract the low four halfwords, add the high four
                for (var i = 0; i < 4; i++)
                {
                    H(ref d)[i] = (short)(H(ref s)[i] - H(ref t)[i]);
                    H(ref d)[i + 4] = (short)(H(ref s)[i + 4] + H(ref t)[i + 4]);
                }

                break;
            case 0x05: // PABSH
                for (var i = 0; i < 8; i++)
                {
                    var v = H(ref t)[i];
                    H(ref d)[i] = v == short.MinValue ? short.MaxValue : Math.Abs(v);
                }

                break;
            case 0x06: // PCEQH
                for (var i = 0; i < 8; i++) H(ref d)[i] = H(ref s)[i] == H(ref t)[i] ? (short)-1 : (short)0;
                break;
            case 0x07: // PMINH
                for (var i = 0; i < 8; i++) H(ref d)[i] = Math.Min(H(ref s)[i], H(ref t)[i]);
                break;
            case 0x0A: // PCEQB
                for (var i = 0; i < 16; i++) d[i] = s[i] == t[i] ? (byte)0xFF : (byte)0;
                break;
            case 0x10: // PADDUW
                for (var i = 0; i < 4; i++)
                {
                    var sum = (ulong)(uint)W(ref s)[i] + (uint)W(ref t)[i];
                    W(ref d)[i] = (int)(uint)Math.Min(sum, uint.MaxValue);
                }

                break;
            case 0x11: // PSUBUW
                for (var i = 0; i < 4; i++)
                {
                    var difference = (long)(uint)W(ref s)[i] - (uint)W(ref t)[i];
                    W(ref d)[i] = difference < 0 ? 0 : (int)(uint)difference;
                }

                break;
            case 0x12: // PEXTUW
            {
                var sw = W(ref s);
                var tw = W(ref t);
                var dw = W(ref d);
                dw[0] = tw[2];
                dw[1] = sw[2];
                dw[2] = tw[3];
                dw[3] = sw[3];
                break;
            }

            case 0x14: // PADDUH
                for (var i = 0; i < 8; i++)
                {
                    H(ref d)[i] = (short)Math.Min((ushort)H(ref s)[i] + (ushort)H(ref t)[i], ushort.MaxValue);
                }

                break;
            case 0x15: // PSUBUH
                for (var i = 0; i < 8; i++)
                {
                    H(ref d)[i] = (short)Math.Max((ushort)H(ref s)[i] - (ushort)H(ref t)[i], 0);
                }

                break;
            case 0x16: // PEXTUH
                for (var i = 0; i < 4; i++)
                {
                    H(ref d)[i * 2] = H(ref t)[i + 4];
                    H(ref d)[i * 2 + 1] = H(ref s)[i + 4];
                }

                break;
            case 0x18: // PADDUB
                for (var i = 0; i < 16; i++) d[i] = (byte)Math.Min(s[i] + t[i], 255);
                break;
            case 0x19: // PSUBUB
                for (var i = 0; i < 16; i++) d[i] = (byte)Math.Max(s[i] - t[i], 0);
                break;
            case 0x1A: // PEXTUB
                for (var i = 0; i < 8; i++)
                {
                    d[i * 2] = t[i + 8];
                    d[i * 2 + 1] = s[i + 8];
                }

                break;
            case 0x1B: // QFSRV: rs:rt shifted right by SA bytes
            {
                var shift = (int)_sa * 8;
                var value = new UInt128(GprHigh(rt), Gpr(rt));
                var upper = new UInt128(GprHigh(rs), Gpr(rs));
                var result = shift == 0 ? value : (value >> shift) | (upper << (128 - shift));
                D(ref d)[0] = (ulong)result;
                D(ref d)[1] = (ulong)(result >> 64);
                break;
            }

            default:
                RaiseException(R5900Exception.ReservedInstruction);
                return;
        }

        Write(rd, ref d);
    }

    private void ExecuteMmi2(uint op, int rs, int rt, int rd)
    {
        var s = Read(rs);
        var t = Read(rt);
        var d = default(Quad);
        switch ((op >> 6) & 0x1F)
        {
            case 0x00: // PMADDW
            case 0x04: // PMSUBW
            {
                WaitHiLo();
                WaitHiLo1();
                var subtract = ((op >> 6) & 0x1F) == 0x04;
                for (var i = 0; i < 2; i++)
                {
                    var accumulator = (long)(((ulong)HiWord(i * 2) << 32) | LoWord(i * 2));
                    var product = (long)W(ref s)[i * 2] * W(ref t)[i * 2];
                    var result = subtract ? accumulator - product : accumulator + product;
                    SetParallelHiLo(i, result);
                    D(ref d)[i] = (ulong)result;
                }

                MarkParallelMultiply(MultCycles);
                break;
            }

            case 0x02: // PSLLVW
                for (var i = 0; i < 2; i++)
                {
                    D(ref d)[i] = SignExtend32((uint)W(ref t)[i * 2] << (W(ref s)[i * 2] & 31));
                }

                break;
            case 0x03: // PSRLVW
                for (var i = 0; i < 2; i++)
                {
                    D(ref d)[i] = SignExtend32((uint)W(ref t)[i * 2] >> (W(ref s)[i * 2] & 31));
                }

                break;
            case 0x08: // PMFHI
                WaitHiLo();
                WaitHiLo1();
                D(ref d)[0] = _hi;
                D(ref d)[1] = _hi1;
                break;
            case 0x09: // PMFLO
                WaitHiLo();
                WaitHiLo1();
                D(ref d)[0] = _lo;
                D(ref d)[1] = _lo1;
                break;
            case 0x0A: // PINTH
                for (var i = 0; i < 4; i++)
                {
                    H(ref d)[i * 2] = H(ref t)[i];
                    H(ref d)[i * 2 + 1] = H(ref s)[i + 4];
                }

                break;
            case 0x0C: // PMULTW
                for (var i = 0; i < 2; i++)
                {
                    var product = (long)W(ref s)[i * 2] * W(ref t)[i * 2];
                    SetParallelHiLo(i, product);
                    D(ref d)[i] = (ulong)product;
                }

                MarkParallelMultiply(MultCycles);
                break;
            case 0x0D: // PDIVW
                for (var i = 0; i < 2; i++)
                {
                    Divide(W(ref s)[i * 2], W(ref t)[i * 2], out var lo, out var hi);
                    SetParallelLoHi(i, lo, hi);
                }

                MarkParallelMultiply(DivCycles);
                return;
            case 0x0E: // PCPYLD
                D(ref d)[0] = D(ref t)[0];
                D(ref d)[1] = D(ref s)[0];
                break;
            case 0x10: // PMADDH
            case 0x14: // PMSUBH
            case 0x1C: // PMULTH
            {
                WaitHiLo();
                WaitHiLo1();
                var function = (op >> 6) & 0x1F;
                for (var i = 0; i < 8; i++)
                {
                    var product = H(ref s)[i] * H(ref t)[i];
                    var current = (int)HalfwordProductTarget(i, out var isHi, out var word);
                    var value = function switch
                    {
                        0x10 => current + product,
                        0x14 => current - product,
                        _ => product,
                    };

                    if (isHi)
                    {
                        SetHiWord(word, (uint)value);
                    }
                    else
                    {
                        SetLoWord(word, (uint)value);
                    }
                }

                var dw = W(ref d);
                dw[0] = (int)LoWord(0);
                dw[1] = (int)HiWord(0);
                dw[2] = (int)LoWord(2);
                dw[3] = (int)HiWord(2);
                MarkParallelMultiply(MultCycles);
                break;
            }

            case 0x11: // PHMADH
            case 0x15: // PHMSBH
            {
                var subtract = ((op >> 6) & 0x1F) == 0x15;
                for (var i = 0; i < 4; i++)
                {
                    var high = H(ref s)[i * 2 + 1] * H(ref t)[i * 2 + 1];
                    var low = H(ref s)[i * 2] * H(ref t)[i * 2];
                    var value = subtract ? high - low : high + low;
                    W(ref d)[i] = value;
                    var word = (i >> 1) * 2;
                    if ((i & 1) == 0)
                    {
                        SetLoWord(word, (uint)value);
                        SetLoWord(word + 1, (uint)high);
                    }
                    else
                    {
                        SetHiWord(word, (uint)value);
                        SetHiWord(word + 1, (uint)high);
                    }
                }

                MarkParallelMultiply(MultCycles);
                break;
            }

            case 0x12: // PAND
                D(ref d)[0] = D(ref s)[0] & D(ref t)[0];
                D(ref d)[1] = D(ref s)[1] & D(ref t)[1];
                break;
            case 0x13: // PXOR
                D(ref d)[0] = D(ref s)[0] ^ D(ref t)[0];
                D(ref d)[1] = D(ref s)[1] ^ D(ref t)[1];
                break;
            case 0x1A: // PEXEH
            {
                var th = H(ref t);
                var dh = H(ref d);
                dh[0] = th[2];
                dh[1] = th[1];
                dh[2] = th[0];
                dh[3] = th[3];
                dh[4] = th[6];
                dh[5] = th[5];
                dh[6] = th[4];
                dh[7] = th[7];
                break;
            }

            case 0x1B: // PREVH
            {
                var th = H(ref t);
                var dh = H(ref d);
                for (var i = 0; i < 4; i++)
                {
                    dh[i] = th[3 - i];
                    dh[i + 4] = th[7 - i];
                }

                break;
            }

            case 0x1D: // PDIVBW: each word of rs by rt's low halfword
            {
                var divisor = (int)H(ref t)[0];
                for (var i = 0; i < 4; i++)
                {
                    Divide(W(ref s)[i], divisor, out var lo, out var hi);
                    SetLoWord(i, (uint)lo);
                    SetHiWord(i, (uint)hi);
                }

                MarkParallelMultiply(DivCycles);
                return;
            }

            case 0x1E: // PEXEW
            {
                var tw = W(ref t);
                var dw = W(ref d);
                dw[0] = tw[2];
                dw[1] = tw[1];
                dw[2] = tw[0];
                dw[3] = tw[3];
                break;
            }

            case 0x1F: // PROT3W
            {
                var tw = W(ref t);
                var dw = W(ref d);
                dw[0] = tw[1];
                dw[1] = tw[2];
                dw[2] = tw[0];
                dw[3] = tw[3];
                break;
            }

            default:
                RaiseException(R5900Exception.ReservedInstruction);
                return;
        }

        Write(rd, ref d);
    }

    private void ExecuteMmi3(uint op, int rs, int rt, int rd)
    {
        var s = Read(rs);
        var t = Read(rt);
        var d = default(Quad);
        switch ((op >> 6) & 0x1F)
        {
            case 0x00: // PMADDUW
            {
                WaitHiLo();
                WaitHiLo1();
                for (var i = 0; i < 2; i++)
                {
                    var accumulator = ((ulong)HiWord(i * 2) << 32) | LoWord(i * 2);
                    var result = accumulator + (ulong)(uint)W(ref s)[i * 2] * (uint)W(ref t)[i * 2];
                    SetParallelHiLo(i, (long)result);
                    D(ref d)[i] = result;
                }

                MarkParallelMultiply(MultCycles);
                break;
            }

            case 0x03: // PSRAVW
                for (var i = 0; i < 2; i++)
                {
                    D(ref d)[i] = (ulong)(long)(W(ref t)[i * 2] >> (W(ref s)[i * 2] & 31));
                }

                break;
            case 0x08: // PMTHI
                _hi = D(ref s)[0];
                _hi1 = D(ref s)[1];
                return;
            case 0x09: // PMTLO
                _lo = D(ref s)[0];
                _lo1 = D(ref s)[1];
                return;
            case 0x0A: // PINTEH
                for (var i = 0; i < 4; i++)
                {
                    H(ref d)[i * 2] = H(ref t)[i * 2];
                    H(ref d)[i * 2 + 1] = H(ref s)[i * 2];
                }

                break;
            case 0x0C: // PMULTUW
                for (var i = 0; i < 2; i++)
                {
                    var product = (ulong)(uint)W(ref s)[i * 2] * (uint)W(ref t)[i * 2];
                    SetParallelHiLo(i, (long)product);
                    D(ref d)[i] = product;
                }

                MarkParallelMultiply(MultCycles);
                break;
            case 0x0D: // PDIVUW
                for (var i = 0; i < 2; i++)
                {
                    DivideUnsigned((uint)W(ref s)[i * 2], (uint)W(ref t)[i * 2], out var lo, out var hi);
                    SetParallelLoHi(i, lo, hi);
                }

                MarkParallelMultiply(DivCycles);
                return;
            case 0x0E: // PCPYUD
                D(ref d)[0] = D(ref s)[1];
                D(ref d)[1] = D(ref t)[1];
                break;
            case 0x12: // POR
                D(ref d)[0] = D(ref s)[0] | D(ref t)[0];
                D(ref d)[1] = D(ref s)[1] | D(ref t)[1];
                break;
            case 0x13: // PNOR
                D(ref d)[0] = ~(D(ref s)[0] | D(ref t)[0]);
                D(ref d)[1] = ~(D(ref s)[1] | D(ref t)[1]);
                break;
            case 0x1A: // PEXCH
            {
                var th = H(ref t);
                var dh = H(ref d);
                dh[0] = th[0];
                dh[1] = th[2];
                dh[2] = th[1];
                dh[3] = th[3];
                dh[4] = th[4];
                dh[5] = th[6];
                dh[6] = th[5];
                dh[7] = th[7];
                break;
            }

            case 0x1B: // PCPYH
            {
                var th = H(ref t);
                var dh = H(ref d);
                var low = th[0];
                var high = th[4];
                for (var i = 0; i < 4; i++)
                {
                    dh[i] = low;
                    dh[i + 4] = high;
                }

                break;
            }

            case 0x1E: // PEXCW
            {
                var tw = W(ref t);
                var dw = W(ref d);
                dw[0] = tw[0];
                dw[1] = tw[2];
                dw[2] = tw[1];
                dw[3] = tw[3];
                break;
            }

            default:
                RaiseException(R5900Exception.ReservedInstruction);
                return;
        }

        Write(rd, ref d);
    }

    /// <summary>The parallel word multiplies' result <paramref name="lane"/> (0: HI/LO, 1: HI1/LO1), as MULT stores it.</summary>
    private void SetParallelHiLo(int lane, long value)
    {
        if (lane == 0)
        {
            _lo = SignExtend32((uint)value);
            _hi = SignExtend32((uint)(value >> 32));
        }
        else
        {
            _lo1 = SignExtend32((uint)value);
            _hi1 = SignExtend32((uint)(value >> 32));
        }
    }

    private void SetParallelLoHi(int lane, ulong lo, ulong hi)
    {
        if (lane == 0)
        {
            _lo = lo;
            _hi = hi;
        }
        else
        {
            _lo1 = lo;
            _hi1 = hi;
        }
    }

    private void MarkParallelMultiply(int latency)
    {
        _hiLoReady = Cycles + latency;
        _hiLo1Ready = Cycles + latency;
    }

    /// <summary>
    /// Where PMADDH/PMSUBH/PMULTH put halfword product <paramref name="i"/>: LO's words 0–1, HI's 0–1, LO's 2–3,
    /// HI's 2–3 for products 0–1, 2–3, 4–5, 6–7.
    /// </summary>
    private uint HalfwordProductTarget(int i, out bool isHi, out int word)
    {
        isHi = ((i >> 1) & 1) != 0;
        word = (i & 1) + ((i >> 2) * 2);
        return isHi ? HiWord(word) : LoWord(word);
    }
}
