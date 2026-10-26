using System.Buffers.Binary;

namespace GameVault.Core.PlayStation2;

/// <summary>Colour space conversion (CSC, IDEC's output) and PACK.</summary>
public sealed partial class Ps2Ipu
{
    /// <summary>Converts one YCbCr macroblock into the output stage as RGB32 (1 KB) or RGB16 (512 bytes).</summary>
    private void ConvertYcc(ReadOnlySpan<byte> ycc, bool rgb16, bool dither, bool sgn)
    {
        Span<byte> rgba = stackalloc byte[1024];
        YccToRgba(ycc, rgba, _th0, _th1, sgn);
        if (rgb16)
        {
            RgbaToRgb16(rgba, _stage.AsSpan(0, 512), dither);
            _stageLength = 512;
        }
        else
        {
            rgba.CopyTo(_stage);
            _stageLength = 1024;
        }

        _stagePosition = 0;
    }

    /// <summary>PACK: one RGB32 macroblock to RGB16 (512 bytes) or, through the VQ CLUT, INDX4 (128 bytes).</summary>
    private void Pack(ReadOnlySpan<byte> rgba, bool rgb16, bool dither)
    {
        if (rgb16)
        {
            RgbaToRgb16(rgba, _stage.AsSpan(0, 512), dither);
            _stageLength = 512;
        }
        else
        {
            Span<byte> packed = stackalloc byte[512];
            RgbaToRgb16(rgba, packed, dither);
            Quantise(packed, _vqClut, _stage.AsSpan(0, 128));
            _stageLength = 128;
        }

        _stagePosition = 0;
    }

    /// <summary>
    /// YCbCr 4:2:0 (Y 16×16, Cb 8×8, Cr 8×8) to RGBA32 with the IPU's studio-range coefficients (Y' = 1.1640625(Y-16);
    /// R += 1.59375Cr', G -= 0.390625Cb' + 0.8125Cr', B += 2.015625Cb', in 1/64 steps then halved with rounding). Alpha
    /// is 0x80, 0x40 when R, G and B are all below TH1, and the whole pixel is 0 when they are all below TH0 (SETTH).
    /// <paramref name="sgn"/> (IDEC.SGN) then subtracts 128 from each colour, wrapping.
    /// </summary>
    internal static void YccToRgba(ReadOnlySpan<byte> ycc, Span<byte> rgba, int th0, int th1, bool sgn)
    {
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int c = 256 + (y >> 1) * 8 + (x >> 1);
                int cb = ycc[c] - 128;
                int cr = ycc[c + 64] - 128;
                int lum = (0x95 * Math.Max(0, ycc[y * 16 + x] - 16)) >> 6;
                int r = Math.Clamp((lum + ((0xCC * cr) >> 6) + 1) >> 1, 0, 255);
                int g = Math.Clamp((lum + ((-0x68 * cr) >> 6) + ((-0x32 * cb) >> 6) + 1) >> 1, 0, 255);
                int b = Math.Clamp((lum + ((0x102 * cb) >> 6) + 1) >> 1, 0, 255);
                int a = 0x80;
                if (r < th0 && g < th0 && b < th0)
                {
                    r = g = b = a = 0;
                }
                else if (r < th1 && g < th1 && b < th1)
                {
                    a = 0x40;
                }

                if (sgn)
                {
                    r ^= 0x80;
                    g ^= 0x80;
                    b ^= 0x80;
                }

                int o = (y * 16 + x) * 4;
                rgba[o] = (byte)r;
                rgba[o + 1] = (byte)g;
                rgba[o + 2] = (byte)b;
                rgba[o + 3] = (byte)a;
            }
        }
    }

    /// <summary>
    /// RGBA32 to RGB16 (5:5:5:1): the 4×4 dither added first when <paramref name="dither"/>, each colour truncated to
    /// five bits; the alpha bit is set for translucent (A = 0x40) pixels.
    /// </summary>
    internal static void RgbaToRgb16(ReadOnlySpan<byte> rgba, Span<byte> rgb16, bool dither)
    {
        for (int i = 0; i < 256; i++)
        {
            int d = dither ? Ps2IpuTables.Dither[((i >> 4) & 3) * 4 + (i & 3)] : 0;
            int r = Math.Clamp(rgba[i * 4] + d, 0, 255) >> 3;
            int g = Math.Clamp(rgba[i * 4 + 1] + d, 0, 255) >> 3;
            int b = Math.Clamp(rgba[i * 4 + 2] + d, 0, 255) >> 3;
            int a = rgba[i * 4 + 3] == 0x40 ? 1 : 0;
            BinaryPrimitives.WriteUInt16LittleEndian(rgb16.Slice(i * 2), (ushort)(r | (g << 5) | (b << 10) | (a << 15)));
        }
    }

    /// <summary>
    /// INDX4 by vector quantisation: each RGB16 pixel takes the CLUT entry nearest in squared 5-bit RGB distance (the
    /// lowest index on a tie); two pixels per byte, the left one in the low nibble.
    /// </summary>
    internal static void Quantise(ReadOnlySpan<byte> rgb16, ReadOnlySpan<ushort> clut, Span<byte> indx4)
    {
        for (int i = 0; i < 128; i++)
        {
            int lo = Nearest(BinaryPrimitives.ReadUInt16LittleEndian(rgb16.Slice(i * 4)), clut);
            int hi = Nearest(BinaryPrimitives.ReadUInt16LittleEndian(rgb16.Slice(i * 4 + 2)), clut);
            indx4[i] = (byte)(lo | (hi << 4));
        }
    }

    private static int Nearest(ushort pixel, ReadOnlySpan<ushort> clut)
    {
        int best = 0;
        int bestDistance = int.MaxValue;
        for (int k = 0; k < 16; k++)
        {
            int dr = (pixel & 31) - (clut[k] & 31);
            int dg = ((pixel >> 5) & 31) - ((clut[k] >> 5) & 31);
            int db = ((pixel >> 10) & 31) - ((clut[k] >> 10) & 31);
            int distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance)
            {
                best = k;
                bestDistance = distance;
            }
        }

        return best;
    }
}
