namespace GameVault.Core.Cpu.Gekko;

public sealed partial class GekkoCpu
{
    /// <summary>A double rounded to single precision, as the Gekko stores single results.</summary>
    private static double Single(double value) => (float)value;

    private uint FprBits(int index) => (uint)BitConverter.DoubleToInt64Bits(_ps0[index]);

    /// <summary>FP instructions need MSR[FP]; without it they raise the FP unavailable exception.</summary>
    private bool FloatingPointAvailable()
    {
        if ((Msr & MsrFp) != 0)
        {
            return true;
        }

        Exception(VectorFpUnavailable, Pc);
        return false;
    }

    /// <summary>A single-precision result: rounded, and in both halves (paired singles).</summary>
    private void SetSingle(int d, double value)
    {
        var rounded = Single(value);
        _ps0[d] = rounded;
        _ps1[d] = rounded;
    }

    private void SetFpCompare(int field, double a, double b)
    {
        uint result = double.IsNaN(a) || double.IsNaN(b) ? 1u : a < b ? 8u : a > b ? 4u : 2u;
        SetCrField(field, result);
        Fpscr = (Fpscr & ~0xF000u) | (result << 12); // FPCC
    }

    private void RecordFloat(uint op)
    {
        if ((op & 1) != 0)
        {
            SetCrField(1, Fpscr >> 28); // CR1 copies FX, FEX, VX and OX
        }
    }

    /// <returns>False when an exception was taken (PC already set).</returns>
    private bool FloatingPoint(uint op)
    {
        if (!FloatingPointAvailable())
        {
            return false;
        }

        int d = Rd(op), a = Ra(op), b = Rb(op), c = Rc(op);
        var primary = op >> 26;
        switch (primary)
        {
            case 48: SetSingle(d, BitConverter.Int32BitsToSingle((int)Read32(RaOrZero(op) + Simm(op)))); break; // lfs
            case 49: // lfsu
            {
                var ea = _gpr[a] + Simm(op);
                SetSingle(d, BitConverter.Int32BitsToSingle((int)Read32(ea)));
                _gpr[a] = ea;
                break;
            }

            case 50: _ps0[d] = BitConverter.Int64BitsToDouble((long)Read64(RaOrZero(op) + Simm(op))); break; // lfd
            case 51:
            {
                var ea = _gpr[a] + Simm(op);
                _ps0[d] = BitConverter.Int64BitsToDouble((long)Read64(ea));
                _gpr[a] = ea;
                break;
            }

            case 52: Write32(RaOrZero(op) + Simm(op), (uint)BitConverter.SingleToInt32Bits((float)_ps0[d])); break; // stfs
            case 53:
            {
                var ea = _gpr[a] + Simm(op);
                Write32(ea, (uint)BitConverter.SingleToInt32Bits((float)_ps0[d]));
                _gpr[a] = ea;
                break;
            }

            case 54: Write64(RaOrZero(op) + Simm(op), (ulong)BitConverter.DoubleToInt64Bits(_ps0[d])); break; // stfd
            case 55:
            {
                var ea = _gpr[a] + Simm(op);
                Write64(ea, (ulong)BitConverter.DoubleToInt64Bits(_ps0[d]));
                _gpr[a] = ea;
                break;
            }

            case 56: // psq_l
            case 57: // psq_lu
            {
                var offset = (uint)(((int)(op << 20)) >> 20);
                var ea = (primary == 57 ? _gpr[a] : RaOrZero(op)) + offset;
                QuantizedLoad(d, ea, (op & 0x8000) != 0, (int)((op >> 12) & 7));
                if (primary == 57)
                {
                    _gpr[a] = ea;
                }

                break;
            }

            case 60: // psq_st
            case 61: // psq_stu
            {
                var offset = (uint)(((int)(op << 20)) >> 20);
                var ea = (primary == 61 ? _gpr[a] : RaOrZero(op)) + offset;
                QuantizedStore(d, ea, (op & 0x8000) != 0, (int)((op >> 12) & 7));
                if (primary == 61)
                {
                    _gpr[a] = ea;
                }

                break;
            }

            case 59:
                switch ((op >> 1) & 31)
                {
                    case 18: SetSingle(d, _ps0[a] / _ps0[b]); break; // fdivs
                    case 20: SetSingle(d, _ps0[a] - _ps0[b]); break; // fsubs
                    case 21: SetSingle(d, _ps0[a] + _ps0[b]); break; // fadds
                    case 24: SetSingle(d, 1.0 / _ps0[b]); break; // fres
                    case 25: SetSingle(d, _ps0[a] * _ps0[c]); break; // fmuls
                    case 28: SetSingle(d, _ps0[a] * _ps0[c] - _ps0[b]); break; // fmsubs
                    case 29: SetSingle(d, _ps0[a] * _ps0[c] + _ps0[b]); break; // fmadds
                    case 30: SetSingle(d, -(_ps0[a] * _ps0[c] - _ps0[b])); break; // fnmsubs
                    case 31: SetSingle(d, -(_ps0[a] * _ps0[c] + _ps0[b])); break; // fnmadds
                    default:
                        Program(ProgramIllegal);
                        return false;
                }

                RecordFloat(op);
                break;
            case 63:
                if (!Double(op, d, a, b, c))
                {
                    return false;
                }

                break;
        }

        return true;
    }

    private bool Double(uint op, int d, int a, int b, int c)
    {
        var ps0 = _ps0;
        switch ((op >> 1) & 31)
        {
            case 18: ps0[d] = ps0[a] / ps0[b]; RecordFloat(op); return true; // fdiv
            case 20: ps0[d] = ps0[a] - ps0[b]; RecordFloat(op); return true;
            case 21: ps0[d] = ps0[a] + ps0[b]; RecordFloat(op); return true;
            case 23: ps0[d] = ps0[a] >= 0 ? ps0[c] : ps0[b]; RecordFloat(op); return true; // fsel
            case 25: ps0[d] = ps0[a] * ps0[c]; RecordFloat(op); return true; // fmul
            case 26: ps0[d] = 1.0 / Math.Sqrt(ps0[b]); RecordFloat(op); return true; // frsqrte
            case 28: ps0[d] = ps0[a] * ps0[c] - ps0[b]; RecordFloat(op); return true;
            case 29: ps0[d] = ps0[a] * ps0[c] + ps0[b]; RecordFloat(op); return true;
            case 30: ps0[d] = -(ps0[a] * ps0[c] - ps0[b]); RecordFloat(op); return true;
            case 31: ps0[d] = -(ps0[a] * ps0[c] + ps0[b]); RecordFloat(op); return true;
        }

        switch ((op >> 1) & 0x3FF)
        {
            case 0: SetFpCompare(d >> 2, ps0[a], ps0[b]); break; // fcmpu
            case 32: SetFpCompare(d >> 2, ps0[a], ps0[b]); break; // fcmpo
            case 12: ps0[d] = Single(ps0[b]); break; // frsp
            case 14: // fctiw: by FPSCR's rounding mode
            case 15: // fctiwz: towards zero
            {
                var value = ps0[b];
                var rounded = ((op >> 1) & 0x3FF) == 15 ? Math.Truncate(value) : (Fpscr & 3) switch
                {
                    1 => Math.Truncate(value),
                    2 => Math.Ceiling(value),
                    3 => Math.Floor(value),
                    _ => Math.Round(value, MidpointRounding.ToEven),
                };
                var integer = double.IsNaN(value) ? int.MinValue
                    : rounded >= int.MaxValue ? int.MaxValue
                    : rounded <= int.MinValue ? int.MinValue
                    : (int)rounded;
                ps0[d] = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000000UL) | (uint)integer);
                break;
            }

            case 40: ps0[d] = -ps0[b]; break; // fneg
            case 72: ps0[d] = ps0[b]; break; // fmr
            case 136: ps0[d] = -Math.Abs(ps0[b]); break; // fnabs
            case 264: ps0[d] = Math.Abs(ps0[b]); break; // fabs
            case 38: Fpscr |= 0x80000000u >> d; break; // mtfsb1
            case 70: Fpscr &= ~(0x80000000u >> d); break; // mtfsb0
            case 64: // mcrfs
            {
                var shift = (7 - (a >> 2)) * 4;
                SetCrField(d >> 2, Fpscr >> shift);
                Fpscr &= ~((0xFu << shift) & 0x9FF80700u); // copied exception bits clear
                break;
            }

            case 134: // mtfsfi
            {
                var shift = (7 - (d >> 2)) * 4;
                Fpscr = (Fpscr & ~(0xFu << shift)) | (((op >> 12) & 0xF) << shift);
                break;
            }

            case 583: ps0[d] = BitConverter.Int64BitsToDouble((long)(0xFFF8000000000000UL | Fpscr)); break; // mffs
            case 711: // mtfsf
            {
                var fm = (op >> 17) & 0xFF;
                uint mask = 0;
                for (var i = 0; i < 8; i++)
                {
                    if ((fm & (0x80u >> i)) != 0)
                    {
                        mask |= 0xF0000000u >> (i * 4);
                    }
                }

                Fpscr = (Fpscr & ~mask) | (FprBits(b) & mask);
                break;
            }

            default:
                Program(ProgramIllegal);
                return false;
        }

        RecordFloat(op);
        return true;
    }

    /// <returns>False when the FP unavailable exception was taken.</returns>
    private bool IndexedFloat(uint op, uint xo)
    {
        if (!FloatingPointAvailable())
        {
            return false;
        }

        int d = Rd(op), a = Ra(op), b = Rb(op);
        var update = xo is 567 or 631 or 695 or 759;
        var ea = (update ? _gpr[a] : RaOrZero(op)) + _gpr[b];
        switch (xo)
        {
            case 535: case 567: SetSingle(d, BitConverter.Int32BitsToSingle((int)Read32(ea))); break; // lfsx, lfsux
            case 599: case 631: _ps0[d] = BitConverter.Int64BitsToDouble((long)Read64(ea)); break; // lfdx, lfdux
            case 663: case 695: Write32(ea, (uint)BitConverter.SingleToInt32Bits((float)_ps0[d])); break; // stfsx, stfsux
            case 727: case 759: Write64(ea, (ulong)BitConverter.DoubleToInt64Bits(_ps0[d])); break; // stfdx, stfdux
            case 983: Write32(ea, FprBits(d)); break; // stfiwx
        }

        if (update)
        {
            _gpr[a] = ea;
        }

        return true;
    }

    // ---- Paired singles ----

    private static double Dequantize(uint raw, int type, int scale) => type switch
    {
        4 => (byte)raw,
        5 => (ushort)raw,
        6 => (sbyte)raw,
        7 => (short)raw,
        _ => BitConverter.Int32BitsToSingle((int)raw),
    } / (type is 4 or 5 or 6 or 7 ? Math.Pow(2, scale) : 1);

    private static uint Quantize(double value, int type, int scale)
    {
        if (type is not (4 or 5 or 6 or 7))
        {
            return (uint)BitConverter.SingleToInt32Bits((float)value);
        }

        var scaled = value * Math.Pow(2, scale);
        return type switch
        {
            4 => (uint)Math.Clamp(scaled, 0, 255),
            5 => (uint)Math.Clamp(scaled, 0, 65535),
            6 => (uint)(int)Math.Clamp(scaled, -128, 127) & 0xFF,
            _ => (uint)(int)Math.Clamp(scaled, -32768, 32767) & 0xFFFF,
        };
    }

    private static int SignedScale(uint gqrBits) => ((int)(gqrBits << 26)) >> 26; // six-bit signed

    private static int Size(int type) => type switch { 4 or 6 => 1, 5 or 7 => 2, _ => 4 };

    /// <summary>psq_l: one or two values of the GQR's load type, scaled down by its load scale (W: ps1 becomes 1.0).</summary>
    private void QuantizedLoad(int d, uint ea, bool single, int gqrIndex)
    {
        var gqr = _gqr[gqrIndex];
        var type = (int)((gqr >> 16) & 7);
        var scale = SignedScale(gqr >> 24);
        var size = Size(type);
        uint ReadSized(uint address) => size switch { 1 => Read8(address), 2 => Read16(address), _ => Read32(address) };
        _ps0[d] = Single(Dequantize(ReadSized(ea), type, scale));
        _ps1[d] = single ? 1.0 : Single(Dequantize(ReadSized(ea + (uint)size), type, scale));
    }

    private void QuantizedStore(int d, uint ea, bool single, int gqrIndex)
    {
        var gqr = _gqr[gqrIndex];
        var type = (int)(gqr & 7);
        var scale = SignedScale(gqr >> 8);
        var size = Size(type);
        void WriteSized(uint address, uint value)
        {
            switch (size)
            {
                case 1: Write8(address, (byte)value); break;
                case 2: Write16(address, (ushort)value); break;
                default: Write32(address, value); break;
            }
        }

        WriteSized(ea, Quantize(_ps0[d], type, scale));
        if (!single)
        {
            WriteSized(ea + (uint)size, Quantize(_ps1[d], type, scale));
        }
    }

    /// <summary>Opcode 4: the paired-single arithmetic, compares, moves and merges, the indexed quantised loads and stores, dcbz_l.</summary>
    /// <returns>False when an exception was taken.</returns>
    private bool PairedSingle(uint op)
    {
        if (!FloatingPointAvailable())
        {
            return false;
        }

        int d = Rd(op), a = Ra(op), b = Rb(op), c = Rc(op);
        double[] p0 = _ps0, p1 = _ps1;
        var xo6 = (op >> 1) & 63;
        if (xo6 is 6 or 38 or 7 or 39)
        {
            // psq_lx, psq_lux, psq_stx, psq_stux: W in bit 10, the GQR in bits 7–9.
            var update = xo6 is 38 or 39;
            var ea = (update ? _gpr[a] : RaOrZero(op)) + _gpr[b];
            var w = (op & 0x400) != 0;
            var i = (int)((op >> 7) & 7);
            if (xo6 is 6 or 38)
            {
                QuantizedLoad(d, ea, w, i);
            }
            else
            {
                QuantizedStore(d, ea, w, i);
            }

            if (update)
            {
                _gpr[a] = ea;
            }

            return true;
        }

        void Set(double x, double y)
        {
            p0[d] = Single(x);
            p1[d] = Single(y);
        }

        switch ((op >> 1) & 31)
        {
            case 10: Set(p0[a] + p1[b], p1[c]); RecordFloat(op); return true; // ps_sum0
            case 11: Set(p0[c], p0[a] + p1[b]); RecordFloat(op); return true; // ps_sum1
            case 12: Set(p0[a] * p0[c], p1[a] * p0[c]); RecordFloat(op); return true; // ps_muls0
            case 13: Set(p0[a] * p1[c], p1[a] * p1[c]); RecordFloat(op); return true; // ps_muls1
            case 14: Set(p0[a] * p0[c] + p0[b], p1[a] * p0[c] + p1[b]); RecordFloat(op); return true; // ps_madds0
            case 15: Set(p0[a] * p1[c] + p0[b], p1[a] * p1[c] + p1[b]); RecordFloat(op); return true; // ps_madds1
            case 18: Set(p0[a] / p0[b], p1[a] / p1[b]); RecordFloat(op); return true; // ps_div
            case 20: Set(p0[a] - p0[b], p1[a] - p1[b]); RecordFloat(op); return true; // ps_sub
            case 21: Set(p0[a] + p0[b], p1[a] + p1[b]); RecordFloat(op); return true; // ps_add
            case 23: Set(p0[a] >= 0 ? p0[c] : p0[b], p1[a] >= 0 ? p1[c] : p1[b]); RecordFloat(op); return true; // ps_sel
            case 24: Set(1.0 / p0[b], 1.0 / p1[b]); RecordFloat(op); return true; // ps_res
            case 25: Set(p0[a] * p0[c], p1[a] * p1[c]); RecordFloat(op); return true; // ps_mul
            case 26: Set(1.0 / Math.Sqrt(p0[b]), 1.0 / Math.Sqrt(p1[b])); RecordFloat(op); return true; // ps_rsqrte
            case 28: Set(p0[a] * p0[c] - p0[b], p1[a] * p1[c] - p1[b]); RecordFloat(op); return true; // ps_msub
            case 29: Set(p0[a] * p0[c] + p0[b], p1[a] * p1[c] + p1[b]); RecordFloat(op); return true; // ps_madd
            case 30: Set(-(p0[a] * p0[c] - p0[b]), -(p1[a] * p1[c] - p1[b])); RecordFloat(op); return true; // ps_nmsub
            case 31: Set(-(p0[a] * p0[c] + p0[b]), -(p1[a] * p1[c] + p1[b])); RecordFloat(op); return true; // ps_nmadd
        }

        switch ((op >> 1) & 0x3FF)
        {
            case 0: case 32: SetFpCompare(d >> 2, p0[a], p0[b]); break; // ps_cmpu0, ps_cmpo0
            case 64: case 96: SetFpCompare(d >> 2, p1[a], p1[b]); break; // ps_cmpu1, ps_cmpo1
            case 40: p0[d] = -p0[b]; p1[d] = -p1[b]; break; // ps_neg
            case 72: p0[d] = p0[b]; p1[d] = p1[b]; break; // ps_mr
            case 136: p0[d] = -Math.Abs(p0[b]); p1[d] = -Math.Abs(p1[b]); break; // ps_nabs
            case 264: p0[d] = Math.Abs(p0[b]); p1[d] = Math.Abs(p1[b]); break; // ps_abs
            case 528: Set(p0[a], p0[b]); break; // ps_merge00
            case 560: Set(p0[a], p1[b]); break; // ps_merge01
            case 592: Set(p1[a], p0[b]); break; // ps_merge10
            case 624: Set(p1[a], p1[b]); break; // ps_merge11
            case 1014: // dcbz_l: the locked cache's 32 bytes
            {
                var ea = (RaOrZero(op) + _gpr[b]) & ~31u;
                for (var i = 0u; i < 32; i++)
                {
                    Write8(ea + i, 0);
                }

                break;
            }

            default:
                Program(ProgramIllegal);
                return false;
        }

        RecordFloat(op);
        return true;
    }
}
