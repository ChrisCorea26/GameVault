using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace GameVault.Core.Nintendo64;

/// <summary>
/// The RDP's rasterizer: the edge walker that turns a triangle (or a rectangle, which the hardware draws as one) into
/// spans, the coverage of each pixel, and the span loops of the four cycle types.
/// </summary>
/// <remarks>
/// <para>Triangles are walked in quarter scanlines (YH, YM and YL are s11.2): the major edge H runs from YH to YL, the
/// middle edge M from YH to YM and the low edge L from YM to YL. XH and XM are the edges' X on the scanline holding YH,
/// XL the low edge's X at YM; X and the slopes are s15.16, and each quarter scanline adds a quarter of the slope with its
/// lowest bit cleared. With the "left major" flag the major edge is on the left.</para>
/// <para>Each quarter scanline's span is kept in 1/8 pixel with a sticky bit for anything finer, clipped to the scissor.
/// A pixel has eight coverage samples, two per quarter scanline (at 0 and 4/8 on even quarters, 2/8 and 6/8 on odd
/// ones). Without anti-aliasing a pixel is drawn when its top-left sample is covered.</para>
/// <para>Shade, texture and depth are given on the major edge at the first scanline, with their change per pixel
/// (DxDx), along the major edge per scanline (DxDe) and per scanline (DxDy). Each scanline's span starts on the major
/// edge and walks towards the minor edges (n64brew: "Reality Display Processor/Commands" — Triangle).</para>
/// </remarks>
public sealed partial class N64Rdp
{
    private const int LineCount = 1030;
    private const int AttrR = 0, AttrG = 1, AttrB = 2, AttrA = 3, AttrS = 4, AttrT = 5, AttrW = 6, AttrZ = 7;

    // The edge walker's output, per scanline.
    private readonly bool[] _lineValid = new bool[LineCount];
    private readonly int[] _lineLx = new int[LineCount];
    private readonly int[] _lineRx = new int[LineCount];
    private readonly int[] _lineMajorX = new int[LineCount];
    private readonly int[] _lineAttr = new int[LineCount * 8];
    private readonly int[] _lineMajor = new int[LineCount * 4];
    private readonly int[] _lineMinor = new int[LineCount * 4];
    private readonly bool[] _lineInvalid = new bool[LineCount * 4];
    private readonly byte[] _coverage = new byte[0x1008];

    // The primitive's steps.
    private readonly int[] _step = new int[8];
    private bool _flip;
    private int _primTile;
    private int _maxLevel;
    private int _dsdy, _dtdy, _dwdy;
    private int _dzPix;

    private static int Fixed(ulong integerWord, ulong fractionWord, int shift) =>
        (int)((((integerWord >> shift) & 0xFFFF) << 16) | ((fractionWord >> shift) & 0xFFFF));

    private void DrawTriangle(int id)
    {
        var c = _command;
        var w0 = c[0];
        Span<int> value = stackalloc int[8];
        Span<int> dx = stackalloc int[8];
        Span<int> de = stackalloc int[8];
        Span<int> dy = stackalloc int[8];
        var at = 4;
        if ((id & 4) != 0)
        {
            ReadCoefficients(at, 0, value, dx, de, dy);
            at += 8;
        }

        if ((id & 2) != 0)
        {
            ReadCoefficients(at, 4, value, dx, de, dy);
            at += 8;
        }

        if ((id & 1) != 0)
        {
            value[AttrZ] = (int)(c[at] >> 32);
            dx[AttrZ] = (int)c[at];
            de[AttrZ] = (int)(c[at + 1] >> 32);
            dy[AttrZ] = (int)c[at + 1];
        }

        WalkEdges(
            ((w0 >> 55) & 1) != 0,
            (int)(w0 >> 51) & 7,
            (int)(w0 >> 48) & 7,
            SignExtend((int)(w0 >> 32) & 0x3FFF, 14),
            SignExtend((int)(w0 >> 16) & 0x3FFF, 14),
            SignExtend((int)w0 & 0x3FFF, 14),
            SignExtend((int)(c[1] >> 32), 28),
            SignExtend((int)(c[2] >> 32), 28),
            SignExtend((int)(c[3] >> 32), 28),
            SignExtend((int)c[1], 30),
            SignExtend((int)c[2], 30),
            SignExtend((int)c[3], 30),
            value, dx, de, dy);
    }

    private void ReadCoefficients(int at, int slot, Span<int> value, Span<int> dx, Span<int> de, Span<int> dy)
    {
        var c = _command;
        for (var i = 0; i < 4; i++)
        {
            var shift = 48 - i * 16;
            value[slot + i] = Fixed(c[at], c[at + 2], shift);
            dx[slot + i] = Fixed(c[at + 1], c[at + 3], shift);
            de[slot + i] = Fixed(c[at + 4], c[at + 6], shift);
            dy[slot + i] = Fixed(c[at + 5], c[at + 7], shift);
        }
    }

    /// <summary>Fill Rectangle: a left-major primitive with no attributes. In fill and copy modes the bottom row is included.</summary>
    private void FillRectangle(ulong w)
    {
        var xl = (int)(w >> 44) & 0xFFF;
        var yl = (int)(w >> 32) & 0xFFF;
        var xh = (int)(w >> 12) & 0xFFF;
        var yh = (int)w & 0xFFF;
        Span<int> zero = stackalloc int[8];
        RectangleEdges(xl, yl, xh, yh, 0, zero, zero, zero, zero);
    }

    /// <summary>
    /// Texture Rectangle (and Flip): S and T in s10.5 at the top-left corner, DsDx and DtDy in s5.10. The rectangle is a
    /// left-major primitive whose S changes along X and T along Y (the other way round when flipped).
    /// </summary>
    private void TextureRectangle(ulong w0, ulong w1, bool flip)
    {
        var xl = (int)(w0 >> 44) & 0xFFF;
        var yl = (int)(w0 >> 32) & 0xFFF;
        var tile = (int)(w0 >> 24) & 7;
        var xh = (int)(w0 >> 12) & 0xFFF;
        var yh = (int)w0 & 0xFFF;
        var s = (int)(w1 >> 48) & 0xFFFF;
        var t = (int)(w1 >> 32) & 0xFFFF;
        var dsdx = (int)(short)(w1 >> 16);
        var dtdy = (int)(short)w1;
        Span<int> value = stackalloc int[8];
        Span<int> dx = stackalloc int[8];
        Span<int> de = stackalloc int[8];
        Span<int> dy = stackalloc int[8];
        value[AttrS] = s << 16;
        value[AttrT] = t << 16;

        // s5.10 steps become s10.5 integer parts with 16 fraction bits: shift left by 11.
        if (!flip)
        {
            dx[AttrS] = dsdx << 11;
            de[AttrT] = dy[AttrT] = dtdy << 11;
        }
        else
        {
            dx[AttrT] = dtdy << 11;
            de[AttrS] = dy[AttrS] = dsdx << 11;
        }

        RectangleEdges(xl, yl, xh, yh, tile, value, dx, de, dy);
    }

    private void RectangleEdges(int xl, int yl, int xh, int yh, int tile, Span<int> value, Span<int> dx, Span<int> de, Span<int> dy)
    {
        if (_cycleType is CycleFill or CycleCopy)
        {
            yl |= 3;
        }

        var xlFixed = ((xl >> 2) << 16) | ((xl & 3) << 14);
        var xhFixed = ((xh >> 2) << 16) | ((xh & 3) << 14);
        WalkEdges(true, 0, tile, yl, yl, yh, xlFixed, xhFixed, xlFixed, 0, 0, 0, value, dx, de, dy);
    }

    /// <summary>Walks the edges of a primitive into spans, then draws them with the current cycle type.</summary>
    private void WalkEdges(bool flip, int maxLevel, int tile, int yl, int ym, int yh, int xl, int xh, int xm,
        int dxldy, int dxhdy, int dxmdy, Span<int> value, Span<int> dx, Span<int> de, Span<int> dy)
    {
        _flip = flip;
        _maxLevel = maxLevel;
        _primTile = tile;

        for (var i = 0; i < 7; i++)
        {
            _step[i] = dx[i] & ~0x1F;
        }

        _step[AttrZ] = dx[AttrZ];
        _dsdy = dy[AttrS] & ~0x7FFF;
        _dtdy = dy[AttrT] & ~0x7FFF;
        _dwdy = dy[AttrW] & ~0x7FFF;
        _correctR = SignExtend(_step[AttrR] >> 14, 13);
        _correctG = SignExtend(_step[AttrG] >> 14, 13);
        _correctB = SignExtend(_step[AttrB] >> 14, 13);
        _correctA = SignExtend(_step[AttrA] >> 14, 13);
        _correctZ = SignExtend(_step[AttrZ] >> 10, 22);
        _dyR = SignExtend(dy[AttrR] >> 14, 13);
        _dyG = SignExtend(dy[AttrG] >> 14, 13);
        _dyB = SignExtend(dy[AttrB] >> 14, 13);
        _dyA = SignExtend(dy[AttrA] >> 14, 13);
        _dyZ = SignExtend(dy[AttrZ] >> 10, 22);

        // The depth slope: the sum of the X and Y slopes' integer parts (as one's complement magnitudes), to a power of two.
        var dzdyInt = (dy[AttrZ] >> 16) & 0xFFFF;
        var dzdxInt = (dx[AttrZ] >> 16) & 0xFFFF;
        var dzSum = ((dzdyInt & 0x8000) != 0 ? ~dzdyInt & 0x7FFF : dzdyInt) + ((dzdxInt & 0x8000) != 0 ? ~dzdxInt & 0x7FFF : dzdxInt);
        _dzPix = NormalizeDzPix(dzSum & 0xFFFF) & 0xFFFF;

        // Attributes are anchored on the major edge's X at one quarter scanline: the first (0) or, when the edge slants
        // away from the span, the last (3), moved back up by three quarters of (DxDe − DxDy).
        var majorSlantsAway = dxhdy < 0;
        var anchorQuarter = majorSlantsAway ^ flip ? 0 : 3;
        Span<int> offset = stackalloc int[8];
        Span<int> perPixel = stackalloc int[8];
        var copy = _cycleType == CycleCopy;
        for (var i = 0; i < 8; i++)
        {
            if (anchorQuarter == 3)
            {
                var e = de[i] & ~0x1FF;
                var d = dy[i] & ~0x1FF;
                offset[i] = e - (e >> 2) - d + (d >> 2);
            }

            perPixel[i] = copy ? 0 : (dx[i] >> 8) & ~1;
        }

        // The rows the scissor allows (YH and YL may also be negative or overflowed).
        int yLimitLow;
        if ((yl & 0x2000) != 0)
        {
            yLimitLow = yl;
        }
        else if ((yl & 0x1000) != 0)
        {
            yLimitLow = _scissorYl;
        }
        else
        {
            yLimitLow = (yl & 0xFFF) < _scissorYl ? yl : _scissorYl;
        }

        var yFar = yLimitLow | 3;
        if ((yl >> 2) > (yFar >> 2))
        {
            yFar += 4; // one more scanline for the LOD's look at the next line
        }
        else if ((yLimitLow >> 2) >= 0 && (yLimitLow >> 2) < LineCount - 1)
        {
            _lineValid[(yLimitLow >> 2) + 1] = false;
        }

        int yLimitHigh;
        if ((yh & 0x2000) != 0)
        {
            yLimitHigh = _scissorYh;
        }
        else if ((yh & 0x1000) != 0)
        {
            yLimitHigh = yh;
        }
        else
        {
            yLimitHigh = yh >= _scissorYh ? yh : _scissorYh;
        }

        var yFirst = yLimitHigh & ~3;
        var clipLeft = _scissorXh << 1;
        var clipRight = _scissorXl << 1;

        var major = xh & ~1;
        var minor = xm & ~1;
        var majorStep = (dxhdy >> 2) & ~1;
        var minorStep = (dxmdy >> 2) & ~1;
        Span<int> attr = stackalloc int[8];
        value.CopyTo(attr);

        int spanMin = 0, spanMax = 0;
        bool allOver = true, allUnder = true, allInvalid = true;
        for (var k = yh & ~3; k <= yFar; k++)
        {
            if (k == ym)
            {
                minor = xl & ~1;
                minorStep = (dxldy >> 2) & ~1;
            }

            var quarter = k & 3;
            if (k >= yFirst && (k >> 2) < LineCount - 1)
            {
                var line = k >> 2;
                if (quarter == 0)
                {
                    spanMin = flip ? 0 : 0xFFF; // flip: the largest minor X; otherwise the smallest
                    spanMax = flip ? 0xFFF : 0; // flip: the smallest major X; otherwise the largest
                    allOver = allUnder = allInvalid = true;
                }

                var invalid = k < yLimitHigh || k >= yLimitLow;
                var majorX = ClipX(major, clipLeft, clipRight, ref allOver, ref allUnder);
                var minorX = ClipX(minor, clipLeft, clipRight, ref allOver, ref allUnder);
                _lineMajor[line * 4 + quarter] = majorX;
                _lineMinor[line * 4 + quarter] = minorX;

                // The edges crossed: the minor edge is on the wrong side of the major one.
                var majorKey = (major ^ (1 << 27)) & (0x3FFF << 14);
                var minorKey = (minor ^ (1 << 27)) & (0x3FFF << 14);
                invalid |= flip ? minorKey < majorKey : majorKey < minorKey;
                _lineInvalid[line * 4 + quarter] = invalid;
                allInvalid &= invalid;
                if (!invalid)
                {
                    if (flip)
                    {
                        spanMin = Math.Max(spanMin, (minorX >> 3) & 0xFFF);
                        spanMax = Math.Min(spanMax, (majorX >> 3) & 0xFFF);
                    }
                    else
                    {
                        spanMin = Math.Min(spanMin, (minorX >> 3) & 0xFFF);
                        spanMax = Math.Max(spanMax, (majorX >> 3) & 0xFFF);
                    }
                }

                if (quarter == anchorQuarter)
                {
                    _lineMajorX[line] = SignExtend(major >> 16, 12);
                    var xFraction = (major >> 8) & 0xFF;
                    for (var i = 0; i < 8; i++)
                    {
                        _lineAttr[line * 8 + i] = ((attr[i] & ~0x1FF) + offset[i] - xFraction * perPixel[i]) & ~0x3FF;
                    }
                }

                if (quarter == 3)
                {
                    _lineLx[line] = spanMin;
                    _lineRx[line] = spanMax;
                    _lineValid[line] = !allInvalid && !allOver && !allUnder &&
                                       (!_scissorField || (_scissorOdd ? 1 : 0) == (line & 1));
                }
            }

            if (quarter == 3)
            {
                for (var i = 0; i < 8; i++)
                {
                    attr[i] += de[i];
                }
            }

            major += majorStep;
            minor += minorStep;
        }

        var start = yLimitHigh >> 2;
        var end = Math.Min(yLimitLow >> 2, LineCount - 2);
        switch (_cycleType)
        {
            case CycleFill:
                DrawFillSpans(start, end);
                break;
            case CycleCopy:
                DrawCopySpans(start, end);
                break;
            default:
                DrawSpans(start, end);
                break;
        }
    }

    /// <summary>
    /// An edge's X on one quarter scanline in 1/8 pixel, with a sticky bit for any finer fraction, clipped to the
    /// scissor (also in 1/8 pixel). Notes whether it fell off the left (under) or right (over).
    /// </summary>
    private static int ClipX(int x, int clipLeft, int clipRight, ref bool allOver, ref bool allUnder)
    {
        var sticky = ((x >> 1) & 0x1FFF) != 0 ? 1 : 0;
        var clipped = ((x >> 13) & 0x1FFE) | sticky;
        var under = (x & 0x8000000) != 0 || (clipped < clipLeft && (x & 0x4000000) == 0);
        clipped = under ? clipLeft : ((x >> 13) & 0x3FFE) | sticky;
        var over = (clipped & 0x2000) != 0 || (clipped & 0x1FFF) >= clipRight;
        clipped = over ? clipRight : clipped;
        allOver &= over;
        allUnder &= under;
        return clipped & 0x1FFF;
    }

    /// <summary>Works out the coverage masks of one scanline's pixels from its quarter-scanline spans.</summary>
    private void ComputeCoverage(int line, int first, int last)
    {
        if (last < first)
        {
            return;
        }

        _coverage.AsSpan(first, last - first + 1).Fill(0xFF);
        for (var quarter = 0; quarter < 4; quarter++)
        {
            var samples = (quarter & 1) == 0 ? 0xA : 0x5;
            var shift = quarter < 2 ? 4 : 0;
            var shifted = (byte)(samples << shift);
            if (_lineInvalid[line * 4 + quarter])
            {
                for (var x = first; x <= last; x++)
                {
                    _coverage[x] &= (byte)~shifted;
                }

                continue;
            }

            var majorX = _lineMajor[line * 4 + quarter];
            var minorX = _lineMinor[line * 4 + quarter];
            var left = _flip ? majorX : minorX;
            var right = _flip ? minorX : majorX;
            int leftPixel = left >> 3, rightPixel = right >> 3;
            for (var x = first; x <= leftPixel && x <= last; x++)
            {
                _coverage[x] &= (byte)~shifted;
            }

            for (var x = Math.Max(rightPixel, first); x <= last; x++)
            {
                _coverage[x] &= (byte)~shifted;
            }

            // Partly covered end pixels: samples at 0, 2, 4 and 6 eighths; the left edge includes, the right excludes.
            var covered = ((left & 7) + 1) >> 1;
            var leftSamples = (0xF >> covered) & samples;
            var rightSamples = (0xF0 >> (((right & 7) + 1) >> 1)) & samples;
            if (rightPixel > leftPixel)
            {
                if (leftPixel >= first && leftPixel <= last)
                {
                    _coverage[leftPixel] |= (byte)(leftSamples << shift);
                }

                if (rightPixel >= first && rightPixel <= last)
                {
                    _coverage[rightPixel] |= (byte)(rightSamples << shift);
                }
            }
            else if (rightPixel == leftPixel && leftPixel >= first && leftPixel <= last)
            {
                _coverage[leftPixel] |= (byte)((leftSamples & rightSamples) << shift);
            }
        }
    }

    /// <summary>Fill-mode spans: every pixel from the major edge to the minor edges takes the fill colour.</summary>
    private void DrawFillSpans(int start, int end)
    {
        var xStep = _flip ? 1 : -1;
        for (var line = Math.Max(start, 0); line <= end; line++)
        {
            if (!_lineValid[line])
            {
                continue;
            }

            var length = _flip ? _lineLx[line] - _lineRx[line] : _lineRx[line] - _lineLx[line];
            _clock += (uint)Math.Max(0, (length + 4) >> 2); // fill mode writes four pixels per clock
            if (length >= 0 && FillRun(line, _flip ? _lineRx[line] : _lineLx[line], length + 1))
            {
                continue;
            }

            var x = _lineRx[line];
            for (var j = 0; j <= length; j++)
            {
                FillPixel(x, line);
                x += xStep;
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="count"/> pixels of a 16- or 32-bit image from <paramref name="first"/> in one go, when they
    /// lie in RDRAM without wrapping; the same values <see cref="FillPixel"/> writes one by one. Returns false otherwise.
    /// </summary>
    private bool FillRun(int line, int first, int count)
    {
        var image = _colorImage;
        if (image.Size is not (Size16 or Size32))
        {
            return false;
        }

        var shift = image.Size == Size16 ? 1 : 2;
        var start = image.Address + ((long)(uint)(line * image.Width + first) << shift);
        var end = start + ((long)count << shift);
        var rdram = _host.Rdram;
        if ((start & ((1 << shift) - 1)) != 0 || end > 0x1000000 || end > rdram.Length)
        {
            return false;
        }

        if (image.Size == Size32)
        {
            MemoryMarshal.Cast<byte, uint>(rdram.Slice((int)start, count * 4))
                .Fill(BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(_fillColor) : _fillColor);
            return true;
        }

        // Even pixels take the fill colour's high half, odd ones its low half.
        var pixels = MemoryMarshal.Cast<byte, ushort>(rdram.Slice((int)start, count * 2));
        var high = (ushort)(_fillColor >> 16);
        var low = (ushort)_fillColor;
        if (BitConverter.IsLittleEndian)
        {
            high = BinaryPrimitives.ReverseEndianness(high);
            low = BinaryPrimitives.ReverseEndianness(low);
        }

        var i = 0;
        if ((first & 1) != 0)
        {
            pixels[i++] = low;
        }

        // From an even pixel on, each pair of pixels is the fill colour's two halves in memory order.
        var pairs = (pixels.Length - i) >> 1;
        MemoryMarshal.Cast<ushort, uint>(pixels.Slice(i, pairs * 2))
            .Fill(BitConverter.IsLittleEndian ? (uint)low << 16 | high : (uint)high << 16 | low);
        i += pairs * 2;
        if (i < pixels.Length)
        {
            pixels[i] = high;
        }

        return true;
    }

    /// <summary>
    /// Copy-mode spans: texels go to the framebuffer unchanged, four per clock. S steps by DsDx once per group of four
    /// pixels, and the pixels of a group take consecutive texels (so a DsDx of 4.0 copies one texel per pixel).
    /// </summary>
    private void DrawCopySpans(int start, int end)
    {
        var flip = _flip;
        var xStep = flip ? 1 : -1;
        var image = _colorImage;
        if (image.Size == Size32)
        {
            return; // copy mode can't write 32-bit images
        }

        var group = image.Size == Size8 ? 8 : 4;
        var dsInc = flip ? _step[AttrS] : -_step[AttrS];
        var dtInc = flip ? _step[AttrT] : -_step[AttrT];
        for (var line = Math.Max(start, 0); line <= end; line++)
        {
            if (!_lineValid[line])
            {
                continue;
            }

            var s = _lineAttr[line * 8 + AttrS];
            var t = _lineAttr[line * 8 + AttrT];
            var length = flip ? _lineLx[line] - _lineRx[line] : _lineRx[line] - _lineLx[line];
            var x = _lineRx[line];
            _clock += (uint)Math.Max(0, (length + group) / group);
            for (var j = 0; j <= length; j += group)
            {
                Divide(s >> 16, t >> 16, 0, out var ss, out var st);
                ss = ClampCoordinate(ss);
                st = ClampCoordinate(st);
                for (var k = 0; k < group && j + k <= length; k++)
                {
                    CopyPixel(x, line, ss, st, k);
                    x += xStep;
                }

                s += dsInc;
                t += dtInc;
            }
        }
    }

    /// <summary>
    /// A pixel in copy mode: texel <paramref name="offset"/> of the group starting at S, written unchanged (through the
    /// TLUT when it is on), unless alpha compare drops it.
    /// </summary>
    private void CopyPixel(int x, int y, int ss, int st, int offset)
    {
        ref var tile = ref _tiles[_primTile];
        var s = (Shift((short)ss, tile.ShiftS) - (tile.Sl << 3)) >> 5;
        var t = (Shift((short)st, tile.ShiftT) - (tile.Tl << 3)) >> 5;
        s = Mask(s + offset, tile.MaskS, tile.MirrorS);
        t = Mask(t, tile.MaskT, tile.MirrorT);
        var image = _colorImage;
        var index = (uint)(y * image.Width + x);
        var rowBase = ((tile.Line * (t & 0xFF) + tile.Tmem) << 3) & 0xFFF;
        var swap = (t & 1) != 0 ? 4 : 0;

        int raw;
        switch (tile.Size)
        {
            case Size4:
            {
                var b = _tmem[((rowBase + (s >> 1)) ^ swap) & 0xFFF];
                raw = (s & 1) != 0 ? b & 0xF : b >> 4;
                raw = tile.Format == FormatCi || _tlut ? (tile.Palette << 4) | raw : (raw << 4) | raw;
                break;
            }

            case Size8:
                raw = _tmem[((rowBase + s) ^ swap) & 0xFFF];
                break;
            default:
            {
                var at = ((rowBase + s * 2) ^ swap) & 0xFFE;
                raw = (_tmem[at] << 8) | _tmem[at + 1];
                break;
            }
        }

        if (image.Size == Size8)
        {
            if (_alphaCompare && raw < ((int)_blendColor & 0xFF))
            {
                return;
            }

            WriteRdram8(image.Address + index, (byte)raw);
            return;
        }

        int color;
        if (_tlut)
        {
            var at = 0x800 + ((raw & 0xFF) << 3);
            color = (_tmem[at] << 8) | _tmem[at + 1];
        }
        else if (tile.Size == Size16)
        {
            color = raw;
        }
        else
        {
            color = (raw << 8) | raw;
        }

        if (_alphaCompare && (color & 1) == 0)
        {
            return;
        }

        WriteRdram16(image.Address + index * 2, (ushort)color);
    }
}
