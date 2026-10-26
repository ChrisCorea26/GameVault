using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>The macroblock decoder shared by BDEC and IDEC: VLC coefficients, dequantisation, inverse scan, IDCT.</summary>
public sealed partial class Ps2Ipu
{
    private const int StepContinue = 0;
    private const int StepStall = 1;
    private const int StepError = 2;
    private const int StepDone = 3;

    private const int MbCbp = 0;
    private const int MbBlock = 1;
    private const int MbDc = 2;
    private const int MbCoefficient = 3;

    private bool _mbIntra;
    private bool _mbField;
    private int _mbQsc;
    private int _cbp;
    private int _block;
    private int _mbPhase;
    private int _coefficient;
    private int _mba;

    // The block being read (levels in scan order) and the decoded macroblock: Y 16×16, then Cb 8×8, Cr 8×8.
    private readonly short[] _levels = new short[64];
    private readonly short[] _mb = new short[384];
    private readonly int[] _dcPredictor = new int[3];

    private void ResetDecoder()
    {
        _mbIntra = false;
        _mbField = false;
        _mbQsc = 0;
        _cbp = 0;
        _block = 0;
        _mbPhase = MbCbp;
        _coefficient = 0;
        _mba = 0;
        Array.Clear(_levels);
        Array.Clear(_mb);
    }

    /// <summary>The DC predictors' reset value, 128 &lt;&lt; intra_dc_precision (ISO/IEC 13818-2 Table 7-2).</summary>
    private void ResetDcPredictors()
    {
        int value = 128 << IntraDcPrecision;
        _dcPredictor[0] = value;
        _dcPredictor[1] = value;
        _dcPredictor[2] = value;
    }

    private void BeginMacroblock(bool intra, bool field, int qsc)
    {
        _mbIntra = intra;
        _mbField = field;
        _mbQsc = qsc;
        _mbPhase = MbCbp;
        _phase = PhMacroblock;
    }

    /// <summary>One step of the macroblock decoder: one VLC symbol (with its fixed-length tail), or a block's IDCT.</summary>
    private int MacroblockStep()
    {
        switch (_mbPhase)
        {
            case MbCbp:
            {
                if (_mbIntra)
                {
                    _cbp = 0x3F;
                }
                else
                {
                    int available = Available(Ps2IpuTables.CbpBits);
                    int entry = Ps2IpuTables.Cbp[(int)(Window() >> (64 - Ps2IpuTables.CbpBits))];
                    int length = entry & 31;
                    if (length == 0 || length > available)
                    {
                        return length == 0 && available >= Ps2IpuTables.CbpBits ? StepError : StepStall;
                    }

                    Advance(length);
                    _cbp = entry >> 5;
                }

                Array.Clear(_mb);
                _block = 0;
                _mbPhase = MbBlock;
                return StepContinue;
            }

            case MbBlock:
                if (_block == 6)
                {
                    return StepDone;
                }

                if ((_cbp & (0x20 >> _block)) == 0)
                {
                    _block++;
                    return StepContinue;
                }

                Array.Clear(_levels);
                _coefficient = 0;
                _mbPhase = _mbIntra ? MbDc : MbCoefficient;
                return StepContinue;

            case MbDc:
                return ReadDcDifference();

            default:
                return ReadCoefficient();
        }
    }

    /// <summary>An intra block's DC: dct_dc_size then dct_dc_differential, added to the component's predictor.</summary>
    private int ReadDcDifference()
    {
        int component = _block < 4 ? 0 : _block - 3;
        int available = Available(32);
        ulong w = Window();
        int maxBits = component == 0 ? Ps2IpuTables.DcLumBits : Ps2IpuTables.DcChromBits;
        int entry = component == 0
            ? Ps2IpuTables.DcLum[(int)(w >> (64 - Ps2IpuTables.DcLumBits))]
            : Ps2IpuTables.DcChrom[(int)(w >> (64 - Ps2IpuTables.DcChromBits))];
        int length = entry & 31;
        if (length == 0)
        {
            return available >= maxBits ? StepError : StepStall;
        }

        int size = entry >> 5;
        if (length + size > available)
        {
            return StepStall;
        }

        int difference = 0;
        if (size != 0)
        {
            difference = (int)((w << length) >> (64 - size));
            if ((difference & (1 << (size - 1))) == 0)
            {
                difference -= (1 << size) - 1;
            }
        }

        Advance(length + size);
        _dcPredictor[component] += difference;
        _levels[0] = (short)_dcPredictor[component];
        _coefficient = 1;
        _mbPhase = MbCoefficient;
        return StepContinue;
    }

    /// <summary>One run/level pair, or the end of block (which finishes the block).</summary>
    private int ReadCoefficient()
    {
        bool tableB15 = _mbIntra && IntraVlcFormat && !Mpeg1;
        int available = Available(32);
        ulong w = Window();
        int run;
        int level;
        int used;

        if (_coefficient == 0 && (w >> 63) != 0)
        {
            // A non-intra block's first coefficient: "1s" is run 0, level ±1 (Table B.14 note).
            if (available < 2)
            {
                return StepStall;
            }

            run = 0;
            level = ((w >> 62) & 1) != 0 ? -1 : 1;
            used = 2;
        }
        else
        {
            if (_coefficient != 0)
            {
                // End of block: "10" in B.14, "0110" in B.15.
                int eobLength = tableB15 ? 4 : 2;
                if (available < eobLength)
                {
                    return StepStall;
                }

                if ((int)(w >> (64 - eobLength)) == (tableB15 ? 0b0110 : 0b10))
                {
                    Advance(eobLength);
                    FinishBlock();
                    return StepContinue;
                }
            }

            int entry = (tableB15 ? Ps2IpuTables.DctB15 : Ps2IpuTables.DctB14)[(int)(w >> (64 - Ps2IpuTables.DctBits))];
            int length = entry & 31;
            if (length == 0)
            {
                return available >= Ps2IpuTables.DctBits ? StepError : StepStall;
            }

            run = (entry >> 5) & 63;
            if (run == Ps2IpuTables.DctEscape)
            {
                // Escape: 6-bit run, then a 12-bit signed level (MPEG-2) or an 8/16-bit one (MPEG-1).
                run = (int)((w << length) >> 58);
                used = length + 6;
                if (Mpeg1)
                {
                    level = (sbyte)(byte)((w << used) >> 56);
                    used += 8;
                    if (level == 0)
                    {
                        level = (int)((w << used) >> 56);
                        used += 8;
                    }
                    else if (level == -128)
                    {
                        level = (int)((w << used) >> 56) - 256;
                        used += 8;
                    }
                }
                else
                {
                    level = (int)((long)(w << used) >> 52);
                    used += 12;
                }
            }
            else
            {
                level = entry >> 11;
                if (((w << length) >> 63) != 0)
                {
                    level = -level;
                }

                used = length + 1;
            }
        }

        if (used > available)
        {
            return StepStall;
        }

        Advance(used);
        _coefficient += run;
        if (_coefficient > 63)
        {
            return StepError;
        }

        _levels[_coefficient] = (short)level;
        _coefficient++;
        return StepContinue;
    }

    /// <summary>Dequantises, inverse-scans and transforms the finished block into the macroblock.</summary>
    private void FinishBlock()
    {
        Span<int> block = stackalloc int[64];
        Dequantise(_levels, block, _mbIntra, _mbQsc, NonLinearQuantiser, IntraDcPrecision, Mpeg1,
            AlternateScan ? Ps2IpuTables.Alternate : Ps2IpuTables.ZigZag, _mbIntra ? _intraMatrix : _nonIntraMatrix);
        Idct(block);

        int min = _mbIntra ? 0 : -256;
        int max = 255;
        if (_block < 4)
        {
            // Frame DCT: the four 8×8 quadrants; field DCT: blocks 0/1 on even lines, 2/3 on odd lines.
            int row0 = _mbField ? _block >> 1 : (_block >> 1) * 8;
            int rowStep = _mbField ? 2 : 1;
            int col0 = (_block & 1) * 8;
            for (int y = 0; y < 8; y++)
            {
                int dst = (row0 + y * rowStep) * 16 + col0;
                for (int x = 0; x < 8; x++)
                {
                    _mb[dst + x] = (short)Math.Clamp(block[y * 8 + x], min, max);
                }
            }
        }
        else
        {
            int dst = _block == 4 ? 256 : 320;
            for (int i = 0; i < 64; i++)
            {
                _mb[dst + i] = (short)Math.Clamp(block[i], min, max);
            }
        }

        _block++;
        _mbPhase = MbBlock;
    }

    /// <summary>
    /// Inverse quantisation (ISO/IEC 13818-2 §7.4; ISO/IEC 11172-2 §2.4.4) of <paramref name="levels"/> (scan order)
    /// into <paramref name="block"/> (raster order). The weighting matrix is indexed in scan order, as SETIQ loads it.
    /// MPEG-1 makes each coefficient odd; MPEG-2 applies mismatch control to coefficient 63.
    /// </summary>
    internal static void Dequantise(ReadOnlySpan<short> levels, Span<int> block, bool intra, int qsc, bool nonLinear,
        int dcPrecision, bool mpeg1, ReadOnlySpan<byte> scan, ReadOnlySpan<byte> matrix)
    {
        block.Clear();
        int scale = nonLinear ? Ps2IpuTables.NonLinearScale[qsc & 31] : (qsc & 31) << 1;
        int sum = 0;
        int start = 0;
        if (intra)
        {
            int dc = Math.Clamp(levels[0] << (3 - dcPrecision), -2048, 2047);
            block[0] = dc;
            sum = dc;
            start = 1;
        }

        for (int i = start; i < 64; i++)
        {
            int level = levels[i];
            if (level == 0)
            {
                continue;
            }

            int magnitude = Math.Abs(level);
            int value = intra
                ? 2 * magnitude * matrix[i] * scale / 32
                : (2 * magnitude + 1) * matrix[i] * scale / 32;
            if (mpeg1 && value != 0 && (value & 1) == 0)
            {
                value--;
            }

            value = Math.Clamp(level < 0 ? -value : value, -2048, 2047);
            block[scan[i]] = value;
            sum += value;
        }

        if (!mpeg1 && (sum & 1) == 0)
        {
            block[63] ^= 1;
        }
    }

    /// <summary>
    /// The 8×8 inverse DCT in place: separable, 64-bit fixed point with <see cref="Ps2IpuTables.IdctMatrix"/>, rounded
    /// to nearest and saturated to [-256, 255] (ISO/IEC 13818-2 §7.5, IEEE 1180 accuracy).
    /// </summary>
    internal static void Idct(Span<int> block)
    {
        ReadOnlySpan<long> k = Ps2IpuTables.IdctMatrix;
        Span<long> rows = stackalloc long[64];
        for (int y = 0; y < 8; y++)
        {
            Span<int> f = block.Slice(y * 8, 8);
            if ((f[0] | f[1] | f[2] | f[3] | f[4] | f[5] | f[6] | f[7]) == 0)
            {
                rows.Slice(y * 8, 8).Clear();
                continue;
            }

            for (int x = 0; x < 8; x++)
            {
                long s = 0;
                for (int u = 0; u < 8; u++)
                {
                    s += k[x * 8 + u] * f[u];
                }

                rows[y * 8 + x] = s;
            }
        }

        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                long s = 0;
                for (int v = 0; v < 8; v++)
                {
                    s += k[y * 8 + v] * rows[v * 8 + x];
                }

                block[y * 8 + x] = (int)Math.Clamp((s + (1L << 39)) >> 40, -256, 255);
            }
        }
    }

    private void SaveDecoder(StateWriter writer)
    {
        writer.WriteBool(_mbIntra);
        writer.WriteBool(_mbField);
        writer.WriteInt32(_mbQsc);
        writer.WriteInt32(_cbp);
        writer.WriteInt32(_block);
        writer.WriteInt32(_mbPhase);
        writer.WriteInt32(_coefficient);
        writer.WriteInt32(_mba);
        foreach (short level in _levels)
        {
            writer.WriteUInt16((ushort)level);
        }

        foreach (short sample in _mb)
        {
            writer.WriteUInt16((ushort)sample);
        }

        foreach (int predictor in _dcPredictor)
        {
            writer.WriteInt32(predictor);
        }
    }

    private void LoadDecoder(StateReader reader)
    {
        _mbIntra = reader.ReadBool();
        _mbField = reader.ReadBool();
        _mbQsc = reader.ReadInt32() & 31;
        _cbp = reader.ReadInt32() & 0x3F;
        _block = Math.Clamp(reader.ReadInt32(), 0, 6);
        _mbPhase = Math.Clamp(reader.ReadInt32(), MbCbp, MbCoefficient);
        _coefficient = Math.Clamp(reader.ReadInt32(), 0, 64);
        _mba = reader.ReadInt32();
        for (int i = 0; i < _levels.Length; i++)
        {
            _levels[i] = (short)reader.ReadUInt16();
        }

        for (int i = 0; i < _mb.Length; i++)
        {
            _mb[i] = (short)reader.ReadUInt16();
        }

        for (int i = 0; i < _dcPredictor.Length; i++)
        {
            _dcPredictor[i] = reader.ReadInt32();
        }
    }
}
