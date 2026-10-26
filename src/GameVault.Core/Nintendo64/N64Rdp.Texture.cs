using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace GameVault.Core.Nintendo64;

/// <summary>
/// The texture unit: perspective divide, coordinate shift, clamp, mirror and mask per tile; texel fetch from TMEM in
/// every format; the TLUT; point and three-point bilinear filtering; the YUV conversion; and the level of detail
/// (n64brew: "Reality Display Processor/Pipeline", "TMEM").
/// </summary>
/// <remarks>
/// Texels are <see cref="Vector128{T}"/> lanes R, G, B and A, each a 9-bit value: the YUV formats deliver U and V as
/// signed numbers in red and green, and the conversion with Set Convert's K0–K3 can leave results outside 0–255, which
/// the combiner then sees.
/// </remarks>
public sealed partial class N64Rdp
{
    private static readonly Vector128<int> Low9 = Vector128.Create(0x1FF);
    private static readonly Vector128<int> Low9RedGreen = Vector128.Create(0x1FF, 0x1FF, -1, -1);
    private static readonly Vector128<int> ColorLanes = Vector128.Create(-1, -1, -1, 0);


    /// <summary>
    /// The perspective divide: S and T (integer parts, s10.5) over W (0x7FFF ≈ 1.0), giving coordinates with a 17-bit
    /// range plus overflow flags in bits 17 (below) and 18 (above). Without perspective the coordinates pass through.
    /// The hardware uses a reciprocal table; this divides exactly, so the lowest bits can differ.
    /// </summary>
    private void Divide(int s, int t, int w, out int ss, out int st)
    {
        if (!_perspective)
        {
            ss = (short)s & 0x1FFFF;
            st = (short)t & 0x1FFFF;
            return;
        }

        var w16 = (short)w;
        var carry = w16 <= 0;
        var divisor = carry ? 1 : w16;
        ss = DivideOne((short)s, divisor, carry);
        st = DivideOne((short)t, divisor, carry);
    }

    private static int DivideOne(int coordinate, int w, bool carry)
    {
        var q = Math.DivRem((long)coordinate << 15, w, out var remainder);
        if (remainder < 0)
        {
            q--; // floor, as the hardware's arithmetic shift
        }

        var flags = 0;
        if (q > 0xFFFF)
        {
            flags = 2 << 17;
        }
        else if (q < -0x10000)
        {
            flags = 1 << 17;
        }

        if (carry)
        {
            flags |= 2 << 17;
        }

        return ((int)q & 0x1FFFF) | flags;
    }

    /// <summary>Saturates a divided coordinate to 16 bits, using its overflow flags.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClampCoordinate(int c)
    {
        if ((c & 0x40000) != 0)
        {
            return 0x7FFF;
        }

        if ((c & 0x20000) != 0)
        {
            return 0x8000;
        }

        return (c & 0x18000) switch
        {
            0x8000 => 0x7FFF,
            0x10000 => 0x8000,
            _ => c & 0xFFFF,
        };
    }

    /// <summary>The LOD distance between two pixels' coordinates (17-bit), as the larger of the S and T steps.</summary>
    private static int LodDistance(int s0, int s1, int t0, int t1, int previous)
    {
        var ds = SignExtend(s1, 17) - SignExtend(s0, 17);
        if ((ds & 0x20000) != 0)
        {
            ds = ~ds & 0x1FFFF;
        }

        var dt = SignExtend(t1, 17) - SignExtend(t0, 17);
        if ((dt & 0x20000) != 0)
        {
            dt = ~dt & 0x1FFFF;
        }

        var d = Math.Max(Math.Max(ds, dt), previous);
        var lod = d & 0x7FFF;
        if ((d & 0x1C000) != 0)
        {
            lod |= 0x4000;
        }

        return lod;
    }

    /// <summary>
    /// The LOD unit's outputs: the mip-map level, whether the texture is magnified or past the last level, and the LOD
    /// fraction the combiner sees. A primitive with no mip-maps counts as distant, so its fraction is 0xFF.
    /// </summary>
    private void LodSignals(bool clamped, int lod, out int level, out bool magnify, out bool distant, out int fraction)
    {
        var plain = !_sharpenTexture && !_detailTexture;
        if ((lod & 0x4000) != 0 || clamped)
        {
            magnify = false;
            level = 0;
            distant = true;
            fraction = 0xFF;
        }
        else if (lod < _primMinLevel || lod < 32)
        {
            magnify = true;
            level = 0;
            distant = _maxLevel == 0;
            if (plain)
            {
                fraction = distant ? 0xFF : 0;
            }
            else
            {
                fraction = (lod < _primMinLevel ? _primMinLevel : lod) << 3;
                if (_sharpenTexture)
                {
                    fraction |= 0x100;
                }
            }
        }
        else
        {
            magnify = false;
            level = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Max(1, (lod >> 5) & 0xFF));
            distant = _maxLevel == 0 || (lod & 0x6000) != 0 || level >= _maxLevel;
            fraction = plain && distant ? 0xFF : ((lod << 3) >> level) & 0xFF;
        }
    }

    /// <summary>The tile a texel comes from once the LOD has picked a level (with detail textures, one tile further).</summary>
    private int LodTile(int level, bool magnify, bool distant)
    {
        if (distant)
        {
            level = _maxLevel;
        }

        return (!_detailTexture || magnify ? _primTile + level : _primTile + level + 1) & 7;
    }

    /// <summary>
    /// One texture cycle: the tile's shift, clamp, mask and mirror, then a point sample, a three-point bilinear sample
    /// or a YUV conversion. <paramref name="previous"/> is the first cycle's texel, which the second cycle can convert
    /// instead of fetching (Set Other Modes' convert_one).
    /// </summary>
    private Vector128<int> TextureCycle(int ss, int st, int tileIndex, int cycle, Vector128<int> previous)
    {
        ref var tile = ref _tiles[tileIndex & 7];
        var bilerp = cycle == 0 ? _biLerp0 : _biLerp1;
        var convert = _convertOne && cycle != 0;

        // Shift, then make the coordinate relative to the tile; the clamp test uses the shifted coordinate.
        var s = Shift((short)ss, tile.ShiftS);
        var t = Shift((short)st, tile.ShiftT);
        var maxS = (s >> 3) >= tile.Sh;
        var maxT = (t >> 3) >= tile.Th;
        s -= tile.Sl << 3;
        t -= tile.Tl << 3;

        if (_bilinear || _tlut)
        {
            int sFrac = s & 0x1F, tFrac = t & 0x1F;
            var si = Clamp(s, tile.ClampEnabledS, maxS, tile.ClampDiffS, ref sFrac);
            var ti = Clamp(t, tile.ClampEnabledT, maxT, tile.ClampDiffT, ref tFrac);
            var s1 = MaskCoupled(ref si, tile.MaskS, tile.MirrorS);
            var t1 = MaskCoupled(ref ti, tile.MaskT, tile.MirrorT);
            var upper = ((sFrac + tFrac) & 0x20) != 0;

            // Only the texels the filter reads are fetched (fetching has no side effects).
            Vector128<int> result;
            if (convert)
            {
                result = Convert(Signed9(previous));
            }
            else if (!_bilinear)
            {
                // Point sampling through the TLUT: every corner is the same texel, so the filter returns it.
                var c0 = FetchTexel(ref tile, si, ti);
                result = !bilerp ? Convert(c0) : _midTexel && sFrac == 0x10 && tFrac == 0x10 ? Center(c0, c0, c0, c0) : c0;
            }
            else if (!bilerp)
            {
                result = Convert(upper ? FetchTexel(ref tile, si + s1, ti + t1) : FetchTexel(ref tile, si, ti));
            }
            else if (_midTexel && sFrac == 0x10 && tFrac == 0x10)
            {
                result = Center(FetchTexel(ref tile, si, ti), FetchTexel(ref tile, si + s1, ti), FetchTexel(ref tile, si, ti + t1),
                    FetchTexel(ref tile, si + s1, ti + t1));
            }
            else if (upper)
            {
                result = Lerp(FetchTexel(ref tile, si + s1, ti + t1), FetchTexel(ref tile, si, ti + t1), FetchTexel(ref tile, si + s1, ti),
                    0x20 - sFrac, 0x20 - tFrac);
            }
            else
            {
                result = Lerp(FetchTexel(ref tile, si, ti), FetchTexel(ref tile, si + s1, ti), FetchTexel(ref tile, si, ti + t1), sFrac, tFrac);
            }

            return result & Low9;
        }

        s = ClampLight(s, tile.ClampEnabledS, maxS, tile.ClampDiffS);
        t = ClampLight(t, tile.ClampEnabledT, maxT, tile.ClampDiffT);
        s = Mask(s, tile.MaskS, tile.MirrorS);
        t = Mask(t, tile.MaskT, tile.MirrorT);
        if (bilerp)
        {
            if (convert)
            {
                return Vector128.Create(previous.GetElement(2));
            }

            return FetchTexel(ref tile, s, t) & Low9RedGreen;
        }

        return Convert(convert ? Signed9(previous) : FetchTexel(ref tile, s, t)) & Low9;
    }

    /// <summary>Sign-extends red, green and blue from 9 bits; alpha passes through.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Signed9(Vector128<int> c) =>
        Vector128.ConditionalSelect(ColorLanes, Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(c, 23), 23), c);

    /// <summary>Three-point filtering from corner <paramref name="o"/> towards <paramref name="sNeighbour"/> and <paramref name="tNeighbour"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Lerp(Vector128<int> o, Vector128<int> sNeighbour, Vector128<int> tNeighbour, int fs, int ft) =>
        o + Vector128.ShiftRightArithmetic((sNeighbour - o) * fs + (tNeighbour - o) * ft + Vector128.Create(0x10), 5);

    /// <summary>The mid-texel case: exactly between four texels, the filter averages all of them.</summary>
    private static Vector128<int> Center(Vector128<int> c0, Vector128<int> c1, Vector128<int> c2, Vector128<int> c3) =>
        c3 + Vector128.ShiftRightArithmetic(
            Vector128.ShiftLeft(c1 + c2, 6) - Vector128.ShiftLeft(c3, 7) + Vector128.ShiftLeft(~c3 + c0, 6) + Vector128.Create(0xC0), 8);

    /// <summary>YUV to RGB with K0–K3 (stored as 2K + 1, divided by 256): Y is in blue, U in red and V in green.</summary>
    private Vector128<int> Convert(Vector128<int> c)
    {
        int u = c.GetElement(0), v = c.GetElement(1), y = c.GetElement(2);
        return Vector128.Create(
            y + ((_k0Filter * v + 0x80) >> 8),
            y + ((_k1Filter * u + _k2Filter * v + 0x80) >> 8),
            y + ((_k3Filter * u + 0x80) >> 8),
            y);
    }

    /// <summary>
    /// The clamp with fractions: past the tile's right edge (SH) the coordinate stops at the tile's width and the
    /// fraction is dropped; a negative coordinate becomes 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp(int c, bool clamp, bool max, int clampDiff, ref int fraction)
    {
        if (!clamp)
        {
            return c >> 5;
        }

        if (max)
        {
            fraction = 0;
            return clampDiff;
        }

        if ((c & 0x10000) == 0)
        {
            return c >> 5;
        }

        fraction = 0;
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClampLight(int c, bool clamp, bool max, int clampDiff)
    {
        if (!clamp)
        {
            return c >> 5;
        }

        if (max)
        {
            return clampDiff;
        }

        return (c & 0x10000) == 0 ? c >> 5 : 0;
    }

    /// <summary>A tile's coordinate shift: right by 1–10, or left by 1–5 for the values 15 down to 11, kept to 16 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Shift(int c, int shift) => shift < 11 ? (short)c >> shift : (short)(c << (16 - shift));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MaskBits(int mask) => mask == 0 ? 0x3FF : (0xFFFF >> (16 - mask)) & 0x3FF;

    /// <summary>Mirror (when the bit above the mask is set, the coordinate is inverted) and mask.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mask(int c, int mask, bool mirror)
    {
        if (mask == 0)
        {
            return c;
        }

        if (mirror && ((c >> Math.Min(mask, 10)) & 1) != 0)
        {
            c = ~c;
        }

        return c & MaskBits(mask);
    }

    /// <summary>Masks <paramref name="c"/> and returns the step to the next texel, which wraps or mirrors with it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MaskCoupled(ref int c, int mask, bool mirror)
    {
        if (mask == 0)
        {
            return 1;
        }

        var bits = MaskBits(mask);
        if (mirror)
        {
            var wrap = (c >> Math.Min(mask, 10)) & 1;
            c ^= -wrap;
            c &= bits;
            return ((c - wrap) & bits) == bits ? 0 : 1 - (wrap << 1);
        }

        c &= bits;
        return c == bits ? -c : 1;
    }

    /// <summary>A byte of TMEM; the index wraps at 4 KB.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte TmemByte(int index) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_tmem), index & 0xFFF);

    /// <summary>A big-endian 16-bit word of TMEM at an even index below 4 KB.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int TmemWord(int index)
    {
        ref var at = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_tmem), index & 0xFFE);
        return (at << 8) | Unsafe.Add(ref at, 1);
    }

    /// <summary>
    /// Reads one texel of a tile from TMEM. Rows are <c>line</c> 64-bit words apart (row index taken to 8 bits), and the
    /// 32-bit words of odd rows are swapped. With the TLUT on, 4- and 8-bit texels (and the high byte of 16-bit ones)
    /// index the palette in the high half of TMEM.
    /// </summary>
    private Vector128<int> FetchTexel(ref Tile tile, int s, int t)
    {
        var rowBase = ((tile.Line * (t & 0xFF) + tile.Tmem) << 3) & 0xFFF;
        var swap = (t & 1) << 2;
        switch (tile.Size)
        {
            case Size4:
            {
                var b = TmemByte((rowBase + (s >> 1)) ^ swap);
                var n = (s & 1) != 0 ? b & 0xF : b >> 4;
                if (_tlut)
                {
                    return Palette((tile.Palette << 4) | n);
                }

                switch (tile.Format)
                {
                    case FormatIa:
                    {
                        var i = n & 0xE;
                        i = (i << 4) | (i << 1) | (i >> 2);
                        return Vector128.Create(i, i, i, (n & 1) != 0 ? 0xFF : 0);
                    }

                    case FormatCi:
                        return Vector128.Create((tile.Palette << 4) | n);
                    case FormatYuv:
                    {
                        var y = (n << 4) | n;
                        return Vector128.Create(y - 0x80, y - 0x80, y, y);
                    }

                    default:
                        return Vector128.Create(n * 17);
                }
            }

            case Size8:
            {
                int b = TmemByte(((rowBase + s) ^ swap) & (tile.Format == FormatYuv ? 0x7FF : 0xFFF));
                if (_tlut)
                {
                    return Palette(b);
                }

                switch (tile.Format)
                {
                    case FormatIa:
                    {
                        var i = (b & 0xF0) | (b >> 4);
                        return Vector128.Create(i, i, i, ((b & 0xF) << 4) | (b & 0xF));
                    }

                    case FormatYuv:
                        return Vector128.Create(b - 0x80, b - 0x80, b, b);
                    default:
                        return Vector128.Create(b);
                }
            }

            case Size16:
            {
                if (tile.Format == FormatYuv)
                {
                    return ReadYuv16(rowBase, s, swap);
                }

                var c = TmemWord(((rowBase + s * 2) ^ swap) & 0xFFE);
                if (_tlut)
                {
                    return Palette(c >> 8);
                }

                return tile.Format switch
                {
                    FormatRgba => Rgba5551(c),
                    FormatIa => Vector128.Create(c >> 8, c >> 8, c >> 8, c & 0xFF),
                    _ => Vector128.Create(c >> 8, c & 0xFF, c >> 8, c & 0xFF),
                };
            }

            default:
            {
                var at = ((rowBase + s * 2) ^ swap) & 0x7FE;
                if (_tlut)
                {
                    return Palette(TmemByte(at));
                }

                if (tile.Format != FormatRgba)
                {
                    var c = TmemWord(at);
                    return Vector128.Create(c >> 8, c & 0xFF, c >> 8, c & 0xFF);
                }

                return Vector128.Create(TmemByte(at), TmemByte(at + 1), TmemByte(at | 0x800), TmemByte((at | 0x800) + 1));
            }
        }
    }

    /// <summary>A TLUT entry from the high half of TMEM (entry <c>n</c> at 0x800 + n × 8), as RGBA5551 or IA88.</summary>
    private Vector128<int> Palette(int index)
    {
        var c = TmemWord(0x800 + ((index & 0xFF) << 3));
        return _tlutIa ? Vector128.Create(c >> 8, c >> 8, c >> 8, c & 0xFF) : Rgba5551(c);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Expand5(int v) => (v << 3) | (v >> 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Rgba5551(int c) =>
        Vector128.Create(Expand5((c >> 11) & 31), Expand5((c >> 6) & 31), Expand5((c >> 1) & 31), (c & 1) != 0 ? 0xFF : 0);

    /// <summary>
    /// A YUV16 texel. Loading splits the pairs: each pair's U and V sit in the low half of TMEM (one 16-bit word per
    /// pair), the Y bytes in the high half. U and V come back signed in red and green, Y in blue and alpha.
    /// </summary>
    private Vector128<int> ReadYuv16(int rowBase, int s, int swap)
    {
        int y = TmemByte((((rowBase + s) ^ swap) & 0x7FF) | 0x800);
        var uv = ((rowBase + (s & ~1)) ^ swap) & 0x7FE;
        return Vector128.Create(TmemByte(uv) - 0x80, TmemByte(uv + 1) - 0x80, y, y);
    }
}
