using System.Numerics;
using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.Cpu.R3000;

/// <summary>
/// The Geometry Transformation Engine, the PlayStation's coprocessor 2 (SPEC §7.11, Phase 15): 64 registers and the
/// fixed-point vector commands (perspective transform, lighting, colour interpolation, averaging and the outer
/// product), with the hardware's 44-bit accumulators, saturation and FLAG bits.
/// </summary>
/// <remarks>
/// References: psx-spx "Geometry Transformation Engine (GTE)" for every command's formula, the saturation rules and
/// the register quirks; the UNR division table is computed from the formula psx-spx gives. Command timing (the CPU
/// stalling on an unfinished command) isn't modelled.
/// </remarks>
public sealed class Gte : IStateful
{
    private static readonly StateTag Tag = StateTag.From("GTE ");

    /// <summary>The reciprocal table of the UNR divider: max(0, (0x40000 / (i + 0x100) + 1) / 2 − 0x101).</summary>
    private static readonly byte[] UnrTable = BuildUnrTable();

    // ---- Data registers ----
    private readonly short[] _v = new short[9]; // VX0, VY0, VZ0, VX1, ..., VZ2
    private uint _rgbc;
    private ushort _otz;
    private readonly short[] _ir = new short[4]; // IR0..IR3
    private readonly short[] _sx = new short[3];
    private readonly short[] _sy = new short[3];
    private readonly ushort[] _sz = new ushort[4];
    private readonly uint[] _rgb = new uint[3];
    private uint _res1;
    private readonly int[] _mac = new int[4]; // MAC0..MAC3
    private uint _lzcs;

    // ---- Control registers ----
    private readonly short[] _rt = new short[9];
    private readonly int[] _tr = new int[3];
    private readonly short[] _llm = new short[9];
    private readonly int[] _bk = new int[3];
    private readonly short[] _lcm = new short[9];
    private readonly int[] _fc = new int[3];
    private int _ofx;
    private int _ofy;
    private ushort _h;
    private short _dqa;
    private int _dqb;
    private short _zsf3;
    private short _zsf4;
    private uint _flag;

    // Scratch for MVMVA's matrix and vector, so commands don't allocate.
    private readonly short[] _matrix = new short[9];
    private readonly short[] _vector = new short[3];
    private readonly long[] _translation = new long[3];
    private static readonly long[] NoTranslation = new long[3];

    // ---- Register access ----

    public uint ReadData(int index)
    {
        switch (index)
        {
            case 0:
            case 2:
            case 4:
                return (ushort)_v[index / 2 * 3] | (uint)(ushort)_v[index / 2 * 3 + 1] << 16;
            case 1:
            case 3:
            case 5:
                return (uint)(int)_v[index / 2 * 3 + 2];
            case 6:
                return _rgbc;
            case 7:
                return _otz;
            case >= 8 and <= 11:
                return (uint)(int)_ir[index - 8];
            case >= 12 and <= 14:
                return (ushort)_sx[index - 12] | (uint)(ushort)_sy[index - 12] << 16;
            case 15:
                return (ushort)_sx[2] | (uint)(ushort)_sy[2] << 16;
            case >= 16 and <= 19:
                return _sz[index - 16];
            case >= 20 and <= 22:
                return _rgb[index - 20];
            case 23:
                return _res1;
            case >= 24 and <= 27:
                return (uint)_mac[index - 24];
            case 28:
            case 29:
                return Orgb();
            case 30:
                return _lzcs;
            default:
                // LZCR: leading zeros of LZCS, or leading ones when it is negative.
                return (uint)BitOperations.LeadingZeroCount((int)_lzcs < 0 ? ~_lzcs : _lzcs);
        }
    }

    public void WriteData(int index, uint value)
    {
        switch (index)
        {
            case 0:
            case 2:
            case 4:
                _v[index / 2 * 3] = (short)value;
                _v[index / 2 * 3 + 1] = (short)(value >> 16);
                break;
            case 1:
            case 3:
            case 5:
                _v[index / 2 * 3 + 2] = (short)value;
                break;
            case 6:
                _rgbc = value;
                break;
            case 7:
                _otz = (ushort)value;
                break;
            case >= 8 and <= 11:
                _ir[index - 8] = (short)value;
                break;
            case >= 12 and <= 14:
                _sx[index - 12] = (short)value;
                _sy[index - 12] = (short)(value >> 16);
                break;
            case 15:
                PushSxy((short)value, (short)(value >> 16));
                break;
            case >= 16 and <= 19:
                _sz[index - 16] = (ushort)value;
                break;
            case >= 20 and <= 22:
                _rgb[index - 20] = value;
                break;
            case 23:
                _res1 = value;
                break;
            case >= 24 and <= 27:
                _mac[index - 24] = (int)value;
                break;
            case 28: // IRGB: five bits per colour, expanded into IR1–IR3
                _ir[1] = (short)((value & 0x1F) << 7);
                _ir[2] = (short)(((value >> 5) & 0x1F) << 7);
                _ir[3] = (short)(((value >> 10) & 0x1F) << 7);
                break;
            case 30:
                _lzcs = value;
                break;

            // ORGB (29) and LZCR (31) are read-only.
        }
    }

    public uint ReadControl(int index)
    {
        switch (index)
        {
            case <= 4:
                return ReadMatrix(_rt, index);
            case <= 7:
                return (uint)_tr[index - 5];
            case <= 12:
                return ReadMatrix(_llm, index - 8);
            case <= 15:
                return (uint)_bk[index - 13];
            case <= 20:
                return ReadMatrix(_lcm, index - 16);
            case <= 23:
                return (uint)_fc[index - 21];
            case 24:
                return (uint)_ofx;
            case 25:
                return (uint)_ofy;
            case 26:
                return (uint)(int)(short)_h; // H is unsigned but reads sign-extended (psx-spx).
            case 27:
                return (uint)(int)_dqa;
            case 28:
                return (uint)_dqb;
            case 29:
                return (uint)(int)_zsf3;
            case 30:
                return (uint)(int)_zsf4;
            default:
                return _flag;
        }
    }

    public void WriteControl(int index, uint value)
    {
        switch (index)
        {
            case <= 4:
                WriteMatrix(_rt, index, value);
                break;
            case <= 7:
                _tr[index - 5] = (int)value;
                break;
            case <= 12:
                WriteMatrix(_llm, index - 8, value);
                break;
            case <= 15:
                _bk[index - 13] = (int)value;
                break;
            case <= 20:
                WriteMatrix(_lcm, index - 16, value);
                break;
            case <= 23:
                _fc[index - 21] = (int)value;
                break;
            case 24:
                _ofx = (int)value;
                break;
            case 25:
                _ofy = (int)value;
                break;
            case 26:
                _h = (ushort)value;
                break;
            case 27:
                _dqa = (short)value;
                break;
            case 28:
                _dqb = (int)value;
                break;
            case 29:
                _zsf3 = (short)value;
                break;
            case 30:
                _zsf4 = (short)value;
                break;
            default:
                _flag = value & 0x7FFFF000;
                UpdateErrorBit();
                break;
        }
    }

    private static uint ReadMatrix(short[] m, int index) =>
        index == 4 ? (uint)(int)m[8] : (ushort)m[index * 2] | (uint)(ushort)m[index * 2 + 1] << 16;

    private static void WriteMatrix(short[] m, int index, uint value)
    {
        m[index * 2] = (short)value;
        if (index < 4)
        {
            m[index * 2 + 1] = (short)(value >> 16);
        }
    }

    private uint Orgb()
    {
        static uint Five(short ir) => (uint)Math.Clamp(ir >> 7, 0, 0x1F);
        return Five(_ir[1]) | Five(_ir[2]) << 5 | Five(_ir[3]) << 10;
    }

    // ---- Commands ----

    /// <summary>Runs a GTE command (a COP2 instruction with bit 25 set).</summary>
    public void Execute(uint instruction)
    {
        var shift = (instruction & (1 << 19)) != 0 ? 12 : 0;
        var lm = (instruction & (1 << 10)) != 0;
        _flag = 0;

        switch (instruction & 0x3F)
        {
            case 0x01:
                Rtp(0, shift, lm, last: true);
                break;
            case 0x06:
                Nclip();
                break;
            case 0x0C:
                OuterProduct(shift, lm);
                break;
            case 0x10:
                Dpcs(_rgbc, shift, lm);
                break;
            case 0x11: // INTPL
                InterpolateColor((long)_ir[1] << 12, (long)_ir[2] << 12, (long)_ir[3] << 12, shift, lm);
                PushColorFromMac();
                break;
            case 0x12:
                Mvmva(instruction, shift, lm);
                break;
            case 0x13:
                NormalColor(0, shift, lm, ColorMode.DepthCue);
                break;
            case 0x14: // CDP
                LightColor(shift, lm);
                ColorDepthCue(shift, lm);
                break;
            case 0x16:
                for (var i = 0; i < 3; i++)
                {
                    NormalColor(i, shift, lm, ColorMode.DepthCue);
                }

                break;
            case 0x1B:
                NormalColor(0, shift, lm, ColorMode.Color);
                break;
            case 0x1C: // CC
                LightColor(shift, lm);
                ColorMultiply(shift, lm);
                break;
            case 0x1E:
                NormalColor(0, shift, lm, ColorMode.Plain);
                break;
            case 0x20:
                for (var i = 0; i < 3; i++)
                {
                    NormalColor(i, shift, lm, ColorMode.Plain);
                }

                break;
            case 0x28: // SQR
                for (var i = 1; i <= 3; i++)
                {
                    SetMacAndIr(i, (long)_ir[i] * _ir[i], shift, lm);
                }

                break;
            case 0x29: // DCPL
                ColorDepthCue(shift, lm);
                break;
            case 0x2A: // DPCT: three colours from the colour FIFO
                for (var i = 0; i < 3; i++)
                {
                    Dpcs(_rgb[0], shift, lm);
                }

                break;
            case 0x2D: // AVSZ3
                AverageZ((long)_zsf3 * (_sz[1] + _sz[2] + _sz[3]));
                break;
            case 0x2E: // AVSZ4
                AverageZ((long)_zsf4 * (_sz[0] + _sz[1] + _sz[2] + _sz[3]));
                break;
            case 0x30:
                Rtp(0, shift, lm, last: false);
                Rtp(1, shift, lm, last: false);
                Rtp(2, shift, lm, last: true);
                break;
            case 0x3D: // GPF
                for (var i = 1; i <= 3; i++)
                {
                    SetMacAndIr(i, (long)_ir[i] * _ir[0], shift, lm);
                }

                PushColorFromMac();
                break;
            case 0x3E: // GPL
                for (var i = 1; i <= 3; i++)
                {
                    SetMacAndIr(i, ((long)_mac[i] << shift) + (long)_ir[i] * _ir[0], shift, lm);
                }

                PushColorFromMac();
                break;
            case 0x3F:
                for (var i = 0; i < 3; i++)
                {
                    NormalColor(i, shift, lm, ColorMode.Color);
                }

                break;
        }

        UpdateErrorBit();
    }

    /// <summary>RTPS/RTPT for one vertex: rotate, translate, then project onto the screen.</summary>
    private void Rtp(int vertex, int shift, bool lm, bool last)
    {
        var vx = _v[vertex * 3];
        var vy = _v[vertex * 3 + 1];
        var vz = _v[vertex * 3 + 2];

        var x = Mac(1, Mac(1, Mac(1, ((long)_tr[0] << 12) + _rt[0] * vx) + _rt[1] * vy) + _rt[2] * vz);
        var y = Mac(2, Mac(2, Mac(2, ((long)_tr[1] << 12) + _rt[3] * vx) + _rt[4] * vy) + _rt[5] * vz);
        var z = Mac(3, Mac(3, Mac(3, ((long)_tr[2] << 12) + _rt[6] * vx) + _rt[7] * vy) + _rt[8] * vz);
        _mac[1] = (int)(x >> shift);
        _mac[2] = (int)(y >> shift);
        _mac[3] = (int)(z >> shift);
        SetIr(1, _mac[1], lm);
        SetIr(2, _mac[2], lm);

        // IR3 saturates on MAC3, but its flag follows MAC3 SAR 12 whatever sf is (psx-spx: "RTPS/RTPT").
        var z12 = z >> 12;
        if (z12 < -0x8000 || z12 > 0x7FFF)
        {
            _flag |= 1 << 22;
        }

        _ir[3] = (short)Math.Clamp(_mac[3], lm ? 0 : -0x8000, 0x7FFF);

        PushSz(z12);
        var div = (long)Divide(_h, _sz[3]);

        var sx = Mac0(div * _ir[1] + _ofx);
        var sy = Mac0(div * _ir[2] + _ofy);
        PushSxy(SaturateScreen(sx >> 16, 14), SaturateScreen(sy >> 16, 13));

        if (last)
        {
            var depth = Mac0(div * _dqa + _dqb);
            _mac[0] = (int)depth;
            SetIr0(depth >> 12);
        }
    }

    private void Nclip()
    {
        var value = (long)_sx[0] * _sy[1] + (long)_sx[1] * _sy[2] + (long)_sx[2] * _sy[0]
            - (long)_sx[0] * _sy[2] - (long)_sx[1] * _sy[0] - (long)_sx[2] * _sy[1];
        _mac[0] = (int)Mac0(value);
    }

    /// <summary>OP: the cross product of RT's diagonal with IR.</summary>
    private void OuterProduct(int shift, bool lm)
    {
        long d1 = _rt[0], d2 = _rt[4], d3 = _rt[8];
        long ir1 = _ir[1], ir2 = _ir[2], ir3 = _ir[3];
        SetMacAndIr(1, ir3 * d2 - ir2 * d3, shift, lm);
        SetMacAndIr(2, ir1 * d3 - ir3 * d1, shift, lm);
        SetMacAndIr(3, ir2 * d1 - ir1 * d2, shift, lm);
    }

    /// <summary>DPCS (and each step of DPCT): a colour moved towards the far colour by IR0.</summary>
    private void Dpcs(uint color, int shift, bool lm)
    {
        InterpolateColor((long)(color & 0xFF) << 16, (long)((color >> 8) & 0xFF) << 16, (long)((color >> 16) & 0xFF) << 16, shift, lm);
        PushColorFromMac();
    }

    private void AverageZ(long value)
    {
        _mac[0] = (int)Mac0(value);
        var otz = value >> 12;
        if (otz < 0 || otz > 0xFFFF)
        {
            _flag |= 1 << 18;
        }

        _otz = (ushort)Math.Clamp(otz, 0, 0xFFFF);
    }

    private void Mvmva(uint instruction, int shift, bool lm)
    {
        var mx = (instruction >> 17) & 3;
        var vx = (instruction >> 15) & 3;
        var tx = (instruction >> 13) & 3;

        switch (mx)
        {
            case 0:
                _rt.CopyTo(_matrix, 0);
                break;
            case 1:
                _llm.CopyTo(_matrix, 0);
                break;
            case 2:
                _lcm.CopyTo(_matrix, 0);
                break;
            default:
                // The "garbage" matrix MVMVA selects with mx=3 (psx-spx).
                var r = (short)((_rgbc & 0xFF) << 4);
                _matrix[0] = (short)-r;
                _matrix[1] = r;
                _matrix[2] = _ir[0];
                _matrix[3] = _matrix[4] = _matrix[5] = _rt[2];
                _matrix[6] = _matrix[7] = _matrix[8] = _rt[4];
                break;
        }

        if (vx == 3)
        {
            _vector[0] = _ir[1];
            _vector[1] = _ir[2];
            _vector[2] = _ir[3];
        }
        else
        {
            _vector[0] = _v[vx * 3];
            _vector[1] = _v[vx * 3 + 1];
            _vector[2] = _v[vx * 3 + 2];
        }

        var translation = tx switch
        {
            0 => _tr,
            1 => _bk,
            2 => _fc,
            _ => null,
        };

        if (tx == 2)
        {
            // With the far colour as translation, the first column's sum only sets flags, and the result is the
            // other two columns alone (a hardware bug, psx-spx: "MVMVA").
            for (var i = 0; i < 3; i++)
            {
                var first = Mac(i + 1, ((long)_fc[i] << 12) + _matrix[i * 3] * _vector[0]);
                SaturateIrFlagOnly(i + 1, first >> shift, false);
                var result = Mac(i + 1, Mac(i + 1, (long)_matrix[i * 3 + 1] * _vector[1]) + (long)_matrix[i * 3 + 2] * _vector[2]);
                SetMacAndIr(i + 1, result, shift, lm);
            }

            return;
        }

        for (var i = 0; i < 3; i++)
        {
            _translation[i] = translation is null ? 0 : translation[i];
        }

        MultiplyMatrixByVector(_matrix, _vector, _translation, shift, lm);
    }

    private void MultiplyMatrixByVector(short[] m, short[] v, long[] t, int shift, bool lm)
    {
        for (var i = 0; i < 3; i++)
        {
            var sum = Mac(i + 1, Mac(i + 1, Mac(i + 1, (t[i] << 12) + m[i * 3] * v[0]) + m[i * 3 + 1] * v[1]) + m[i * 3 + 2] * v[2]);
            SetMacAndIr(i + 1, sum, shift, lm);
        }
    }

    private enum ColorMode
    {
        /// <summary>NCS/NCT: the lit colour.</summary>
        Plain,

        /// <summary>NCCS/NCCT: the lit colour times RGBC.</summary>
        Color,

        /// <summary>NCDS/NCDT: the lit colour times RGBC, moved towards the far colour.</summary>
        DepthCue,
    }

    /// <summary>NCS/NCCS/NCDS for one normal vector: light it with LLM, colour it with LCM and BK.</summary>
    private void NormalColor(int vertex, int shift, bool lm, ColorMode mode)
    {
        _vector[0] = _v[vertex * 3];
        _vector[1] = _v[vertex * 3 + 1];
        _vector[2] = _v[vertex * 3 + 2];
        MultiplyMatrixByVector(_llm, _vector, NoTranslation, shift, lm);
        LightColor(shift, lm);

        switch (mode)
        {
            case ColorMode.Plain:
                PushColorFromMac();
                break;
            case ColorMode.Color:
                ColorMultiply(shift, lm);
                break;
            default:
                ColorDepthCue(shift, lm);
                break;
        }
    }

    /// <summary>[IR1..3] = BK + LCM × IR.</summary>
    private void LightColor(int shift, bool lm)
    {
        _vector[0] = _ir[1];
        _vector[1] = _ir[2];
        _vector[2] = _ir[3];
        for (var i = 0; i < 3; i++)
        {
            _translation[i] = _bk[i];
        }

        MultiplyMatrixByVector(_lcm, _vector, _translation, shift, lm);
    }

    /// <summary>[MAC1..3] = [R×IR1, G×IR2, B×IR3] SHL 4, then pushed to the colour FIFO.</summary>
    private void ColorMultiply(int shift, bool lm)
    {
        SetMacAndIr(1, (long)(_rgbc & 0xFF) * _ir[1] << 4, shift, lm);
        SetMacAndIr(2, (long)((_rgbc >> 8) & 0xFF) * _ir[2] << 4, shift, lm);
        SetMacAndIr(3, (long)((_rgbc >> 16) & 0xFF) * _ir[3] << 4, shift, lm);
        PushColorFromMac();
    }

    /// <summary>DCPL and the end of NCDS/CDP: RGBC × IR, moved towards the far colour, then pushed.</summary>
    private void ColorDepthCue(int shift, bool lm)
    {
        InterpolateColor(
            (long)(_rgbc & 0xFF) * _ir[1] << 4,
            (long)((_rgbc >> 8) & 0xFF) * _ir[2] << 4,
            (long)((_rgbc >> 16) & 0xFF) * _ir[3] << 4,
            shift,
            lm);
        PushColorFromMac();
    }

    /// <summary>
    /// [IR1..3] = (FC SHL 12 − MAC) SAR sf, saturated without lm; then [MAC1..3] = (IR × IR0 + MAC) SAR sf.
    /// </summary>
    private void InterpolateColor(long mac1, long mac2, long mac3, int shift, bool lm)
    {
        SetMacAndIr(1, ((long)_fc[0] << 12) - mac1, shift, false);
        SetMacAndIr(2, ((long)_fc[1] << 12) - mac2, shift, false);
        SetMacAndIr(3, ((long)_fc[2] << 12) - mac3, shift, false);
        SetMacAndIr(1, (long)_ir[1] * _ir[0] + mac1, shift, lm);
        SetMacAndIr(2, (long)_ir[2] * _ir[0] + mac2, shift, lm);
        SetMacAndIr(3, (long)_ir[3] * _ir[0] + mac3, shift, lm);
    }

    // ---- Saturation and flags ----

    /// <summary>Checks a MAC1–3 sum against the 44-bit accumulator, setting its overflow flag, and wraps it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long Mac(int index, long value)
    {
        if (value > 0x7FFFFFFFFFFL)
        {
            _flag |= 1u << (31 - index);
        }
        else if (value < -0x80000000000L)
        {
            _flag |= 1u << (28 - index);
        }

        return value << 20 >> 20;
    }

    private long Mac0(long value)
    {
        if (value > int.MaxValue)
        {
            _flag |= 1 << 16;
        }
        else if (value < int.MinValue)
        {
            _flag |= 1 << 15;
        }

        return value;
    }

    private void SetMacAndIr(int index, long value, int shift, bool lm)
    {
        value = Mac(index, value);
        _mac[index] = (int)(value >> shift);
        SetIr(index, _mac[index], lm);
    }

    private void SetIr(int index, long value, bool lm)
    {
        var min = lm ? 0 : -0x8000;
        if (value < min || value > 0x7FFF)
        {
            _flag |= 1u << (25 - index);
        }

        _ir[index] = (short)Math.Clamp(value, min, 0x7FFF);
    }

    private void SaturateIrFlagOnly(int index, long value, bool lm)
    {
        if (value < (lm ? 0 : -0x8000) || value > 0x7FFF)
        {
            _flag |= 1u << (25 - index);
        }
    }

    private void SetIr0(long value)
    {
        if (value < 0 || value > 0x1000)
        {
            _flag |= 1 << 12;
        }

        _ir[0] = (short)Math.Clamp(value, 0, 0x1000);
    }

    private short SaturateScreen(long value, int flagBit)
    {
        if (value < -0x400 || value > 0x3FF)
        {
            _flag |= 1u << flagBit;
        }

        return (short)Math.Clamp(value, -0x400, 0x3FF);
    }

    private void PushSz(long value)
    {
        if (value < 0 || value > 0xFFFF)
        {
            _flag |= 1 << 18;
        }

        _sz[0] = _sz[1];
        _sz[1] = _sz[2];
        _sz[2] = _sz[3];
        _sz[3] = (ushort)Math.Clamp(value, 0, 0xFFFF);
    }

    private void PushSxy(short x, short y)
    {
        _sx[0] = _sx[1];
        _sy[0] = _sy[1];
        _sx[1] = _sx[2];
        _sy[1] = _sy[2];
        _sx[2] = x;
        _sy[2] = y;
    }

    /// <summary>Pushes [MAC1..3] SAR 4, saturated to 0–255, with RGBC's code byte.</summary>
    private void PushColorFromMac()
    {
        var r = SaturateColor(_mac[1] >> 4, 21);
        var g = SaturateColor(_mac[2] >> 4, 20);
        var b = SaturateColor(_mac[3] >> 4, 19);
        _rgb[0] = _rgb[1];
        _rgb[1] = _rgb[2];
        _rgb[2] = r | g << 8 | b << 16 | (_rgbc & 0xFF000000);
    }

    private uint SaturateColor(int value, int flagBit)
    {
        if (value < 0 || value > 0xFF)
        {
            _flag |= 1u << flagBit;
        }

        return (uint)Math.Clamp(value, 0, 0xFF);
    }

    /// <summary>FLAG bit 31 is set when any of bits 30–23 or 18–13 is.</summary>
    private void UpdateErrorBit()
    {
        _flag &= 0x7FFFFFFF;
        if ((_flag & 0x7F87E000) != 0)
        {
            _flag |= 0x80000000;
        }
    }

    /// <summary>The UNR divider: H × 0x20000 / SZ3, rounded, as the hardware computes it (psx-spx: "GTE Division Inaccuracy").</summary>
    private uint Divide(ushort h, ushort sz3)
    {
        if (h >= sz3 * 2)
        {
            _flag |= 1 << 17;
            return 0x1FFFF;
        }

        var z = BitOperations.LeadingZeroCount((uint)sz3) - 16;
        var n = (uint)h << z;
        var d = (uint)sz3 << z;
        var u = UnrTable[((d & 0x7FFF) + 0x40) >> 7] + 0x101u;
        d = (0x2000080 - d * u) >> 8;
        d = (0x0000080 + d * u) >> 8;
        return (uint)Math.Min(0x1FFFF, ((ulong)n * d + 0x8000) >> 16);
    }

    private static byte[] BuildUnrTable()
    {
        var table = new byte[0x101];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = (byte)Math.Max(0, (0x40000 / (i + 0x100) + 1) / 2 - 0x101);
        }

        return table;
    }

    // ---- Save state ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        for (var i = 0; i < 32; i++)
        {
            writer.WriteUInt32(ReadData(i));
        }

        for (var i = 0; i < 32; i++)
        {
            writer.WriteUInt32(ReadControl(i));
        }

        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < 32; i++)
        {
            var value = reader.ReadUInt32();
            if (i == 15 || i >= 28 && i != 30)
            {
                continue; // Mirrors and computed registers: restored through the registers they show.
            }

            WriteData(i, value);
        }

        for (var i = 0; i < 32; i++)
        {
            var value = reader.ReadUInt32();
            if (i == 31)
            {
                _flag = value;
            }
            else
            {
                WriteControl(i, value);
            }
        }

        reader.EndChunk();
    }
}
