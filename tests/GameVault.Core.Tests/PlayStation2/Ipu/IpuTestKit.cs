using System.Buffers.Binary;

namespace GameVault.Core.Tests.PlayStation2.Ipu;

/// <summary>Writes a bitstream MSB first, the order the IPU reads its input FIFO in.</summary>
internal sealed class IpuBitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bitCount;

    public int BitLength => _bitCount;

    public IpuBitWriter Put(uint value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--)
        {
            PutBit((int)(value >> i) & 1);
        }

        return this;
    }

    /// <summary>Appends a code written as a string of '0' and '1' (spaces ignored).</summary>
    public IpuBitWriter Put(string bits)
    {
        foreach (char c in bits)
        {
            if (c is '0' or '1')
            {
                PutBit(c - '0');
            }
        }

        return this;
    }

    public IpuBitWriter AlignWithZeros()
    {
        while ((_bitCount & 7) != 0)
        {
            PutBit(0);
        }

        return this;
    }

    public IpuBitWriter PutBytes(params byte[] bytes)
    {
        foreach (byte b in bytes)
        {
            Put(b, 8);
        }

        return this;
    }

    public byte[] ToArray() => [.. _bytes];

    /// <summary>The stream as quadwords (little-endian, byte 0 first in the stream), zero-padded to a whole quadword.</summary>
    public UInt128[] ToQuadwords(int extraZeroQuadwords = 0)
    {
        var bytes = new byte[((_bytes.Count + 15) & ~15) + extraZeroQuadwords * 16];
        _bytes.CopyTo(bytes);
        return Quadwords(bytes);
    }

    public static UInt128[] Quadwords(byte[] bytes)
    {
        var q = new UInt128[bytes.Length / 16];
        for (int i = 0; i < q.Length; i++)
        {
            q[i] = BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(i * 16));
        }

        return q;
    }

    public static byte[] Bytes(IEnumerable<UInt128> quadwords)
    {
        var list = new List<byte>();
        var buffer = new byte[16];
        foreach (UInt128 q in quadwords)
        {
            BinaryPrimitives.WriteUInt128LittleEndian(buffer, q);
            list.AddRange(buffer);
        }

        return [.. list];
    }

    private void PutBit(int bit)
    {
        if ((_bitCount & 7) == 0)
        {
            _bytes.Add(0);
        }

        if (bit != 0)
        {
            _bytes[^1] |= (byte)(0x80 >> (_bitCount & 7));
        }

        _bitCount++;
    }
}

/// <summary>
/// An MPEG-1/2 macroblock encoder for the codes the tests use, typed from ISO/IEC 13818-2 Annex B (Tables B.12–B.15)
/// independently of the IPU's tables; levels without a short code go out as escapes.
/// </summary>
internal static class IpuMpegEncoder
{
    private static readonly string[] DcLumCodes =
        ["100", "00", "01", "101", "110", "1110", "11110", "111110", "1111110", "11111110", "111111110", "111111111"];

    private static readonly string[] DcChromCodes =
    [
        "00", "01", "10", "110", "1110", "11110", "111110", "1111110", "11111110", "111111110", "1111111110",
        "1111111111",
    ];

    // (run, |level|) → code without the sign bit. B.14 entries for run 0 level 1 are "11" (not first).
    private static readonly Dictionary<(int, int), string> B14 = new()
    {
        [(0, 1)] = "11", [(1, 1)] = "011", [(0, 2)] = "0100", [(2, 1)] = "0101", [(0, 3)] = "00101",
        [(3, 1)] = "00111", [(4, 1)] = "00110", [(1, 2)] = "000110", [(0, 4)] = "0000110", [(0, 5)] = "00100110",
        [(0, 8)] = "000000011101", [(0, 16)] = "00000000011111", [(1, 18)] = "0000000000010000",
    };

    private static readonly Dictionary<(int, int), string> B15 = new()
    {
        [(0, 1)] = "10", [(1, 1)] = "010", [(0, 2)] = "110", [(2, 1)] = "00101", [(0, 3)] = "0111",
        [(1, 2)] = "00110", [(0, 4)] = "11100", [(0, 5)] = "11101", [(0, 8)] = "1111011",
    };

    /// <summary>
    /// Emits one block. <paramref name="levels"/> are quantised levels in scan order; for an intra block levels[0] is
    /// the DC value (QF[0]) itself, coded as a differential against <paramref name="predictor"/>.
    /// </summary>
    public static void Block(IpuBitWriter w, short[] levels, bool intra, int component, ref int predictor,
        bool tableB15 = false, bool mpeg1 = false)
    {
        int start = 0;
        if (intra)
        {
            int diff = levels[0] - predictor;
            predictor = levels[0];
            int size = 0;
            while ((Math.Abs(diff) >> size) != 0)
            {
                size++;
            }

            w.Put(component == 0 ? DcLumCodes[size] : DcChromCodes[size]);
            if (size > 0)
            {
                w.Put((uint)(diff > 0 ? diff : diff + (1 << size) - 1), size);
            }

            start = 1;
        }

        int run = 0;
        bool first = true;
        for (int i = start; i < 64; i++)
        {
            int level = levels[i];
            if (level == 0)
            {
                run++;
                continue;
            }

            int sign = level < 0 ? 1 : 0;
            if (!intra && first && run == 0 && Math.Abs(level) == 1)
            {
                w.Put("1").Put((uint)sign, 1);
            }
            else if ((tableB15 ? B15 : B14).TryGetValue((run, Math.Abs(level)), out string? code))
            {
                w.Put(code).Put((uint)sign, 1);
            }
            else
            {
                w.Put("000001").Put((uint)run, 6);
                if (!mpeg1)
                {
                    w.Put((uint)level & 0xFFF, 12);
                }
                else if (level is > -128 and < 128)
                {
                    w.Put((uint)level & 0xFF, 8);
                }
                else if (level > 0)
                {
                    w.Put(0, 8).Put((uint)level, 8);
                }
                else
                {
                    w.Put(0x80, 8).Put((uint)(level + 256), 8);
                }
            }

            first = false;
            run = 0;
        }

        w.Put(tableB15 ? "0110" : "10");
    }
}

/// <summary>
/// A straightforward MPEG decoder written from ISO/IEC 13818-2 §7.3–7.5 for checking the IPU: dequantisation in raster
/// space, a double-precision IDCT from the definition, and the IPU's colour conversion in floating point.
/// </summary>
internal static class IpuReference
{
    /// <summary>The zigzag scan generated by walking the anti-diagonals: scan position → raster position.</summary>
    public static readonly int[] ZigZag = GenerateZigZag();

    /// <summary>The alternate scan (Figure 7-3).</summary>
    public static readonly int[] Alternate =
    [
        0, 8, 16, 24, 1, 9, 2, 10, 17, 25, 32, 40, 48, 56, 57, 49, 41, 33, 26, 18, 3, 11, 4, 12, 19, 27, 34, 42, 50,
        58, 35, 43, 51, 59, 20, 28, 5, 13, 6, 14, 21, 29, 36, 44, 52, 60, 37, 45, 53, 61, 22, 30, 7, 15, 23, 31, 38,
        46, 54, 62, 39, 47, 55, 63,
    ];

    /// <summary>The default intra matrix in raster order (§6.3.11).</summary>
    public static readonly int[] DefaultIntraRaster =
    [
        8, 16, 19, 22, 26, 27, 29, 34, 16, 16, 22, 24, 27, 29, 34, 37, 19, 22, 26, 27, 29, 34, 34, 38, 22, 22, 26, 27,
        29, 34, 37, 40, 22, 26, 27, 29, 32, 35, 40, 48, 26, 27, 29, 32, 35, 40, 48, 58, 26, 27, 29, 34, 38, 46, 56, 69,
        27, 29, 35, 38, 46, 56, 69, 83,
    ];

    /// <summary>The default intra matrix in the order the IPU holds it (bitstream/zigzag order).</summary>
    public static int[] DefaultIntraScanOrder => [.. ZigZag.Select(p => DefaultIntraRaster[p])];

    /// <summary>F(u,v) → f(x,y) by the IDCT's definition, in double precision.</summary>
    public static double[] Idct(int[] f)
    {
        var result = new double[64];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                double s = 0;
                for (int v = 0; v < 8; v++)
                {
                    for (int u = 0; u < 8; u++)
                    {
                        double cu = u == 0 ? 1 / Math.Sqrt(2) : 1;
                        double cv = v == 0 ? 1 / Math.Sqrt(2) : 1;
                        s += cu * cv * f[v * 8 + u] * Math.Cos((2 * x + 1) * u * Math.PI / 16)
                            * Math.Cos((2 * y + 1) * v * Math.PI / 16);
                    }
                }

                result[y * 8 + x] = s / 4;
            }
        }

        return result;
    }

    /// <summary>Decodes one block's levels (scan order) to samples (raster order).</summary>
    public static int[] DecodeBlock(short[] levels, bool intra, int qsc, int[] matrixScanOrder, bool mpeg1 = false,
        bool alternate = false, int dcPrecision = 0, bool nonLinear = false)
    {
        int[] scan = alternate ? Alternate : ZigZag;
        int[] nonLinearTable =
            [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 18, 20, 22, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64, 72, 80, 88, 96, 104, 112];
        int quantiserScale = nonLinear ? nonLinearTable[qsc] : 2 * qsc;

        var f = new int[64];
        long sum = 0;
        for (int i = 0; i < 64; i++)
        {
            int qf = levels[i];
            int value;
            if (intra && i == 0)
            {
                value = qf * (8 >> dcPrecision);
            }
            else
            {
                if (qf == 0)
                {
                    continue;
                }

                int k = intra ? 0 : Math.Sign(qf);
                value = (2 * qf + k) * matrixScanOrder[i] * quantiserScale / 32;
                if (mpeg1 && (value & 1) == 0)
                {
                    value -= Math.Sign(value);
                }
            }

            value = Math.Clamp(value, -2048, 2047);
            f[scan[i]] = value;
            sum += value;
        }

        if (!mpeg1 && (sum & 1) == 0)
        {
            f[63] += (f[63] & 1) != 0 ? -1 : 1;
        }

        double[] samples = Idct(f);
        var result = new int[64];
        for (int i = 0; i < 64; i++)
        {
            int s = Math.Clamp((int)Math.Round(samples[i], MidpointRounding.AwayFromZero), -256, 255);
            result[i] = intra ? Math.Clamp(s, 0, 255) : s;
        }

        return result;
    }

    /// <summary>Puts six decoded blocks into a macroblock: Y 16×16 (frame or field DCT), then Cb, Cr.</summary>
    public static int[] Assemble(int[][] blocks, bool field = false)
    {
        var mb = new int[384];
        for (int b = 0; b < 4; b++)
        {
            for (int y = 0; y < 8; y++)
            {
                int row = field ? (b >> 1) + 2 * y : (b >> 1) * 8 + y;
                for (int x = 0; x < 8; x++)
                {
                    mb[row * 16 + (b & 1) * 8 + x] = blocks[b][y * 8 + x];
                }
            }
        }

        Array.Copy(blocks[4], 0, mb, 256, 64);
        Array.Copy(blocks[5], 0, mb, 320, 64);
        return mb;
    }

    /// <summary>The IPU's YCbCr → RGB conversion in floating point (no thresholds).</summary>
    public static (double R, double G, double B) Rgb(int y, int cb, int cr)
    {
        double lum = 1.1640625 * Math.Max(0, y - 16);
        return (lum + 1.59375 * (cr - 128),
            lum - 0.8125 * (cr - 128) - 0.390625 * (cb - 128),
            lum + 2.015625 * (cb - 128));
    }

    private static int[] GenerateZigZag()
    {
        var order = new int[64];
        int n = 0;
        for (int d = 0; d < 15; d++)
        {
            for (int k = 0; k <= d; k++)
            {
                // Even diagonals run up and right, odd ones down and left.
                int row = d % 2 == 0 ? d - k : k;
                int col = d - row;
                if (row < 8 && col < 8)
                {
                    order[n++] = row * 8 + col;
                }
            }
        }

        return order;
    }
}
