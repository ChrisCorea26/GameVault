using System.Numerics;
using System.Runtime.Intrinsics;

namespace GameVault.Core.Nintendo64;

/// <summary>
/// The one- and two-cycle span loop: every pixel of a scanline through texturing, the combiner, the Z test, the blender
/// and the framebuffer write. The loop is a generic method instantiated per combination of the mode bits that change
/// the pixel's path the most (two-cycle mode, texturing, Z compare, image read), so the JIT drops the branches and the
/// work a mode doesn't use; everything else is decided per pixel exactly as before.
/// </summary>
public sealed partial class N64Rdp
{
    /// <summary>A mode bit fixed at compile time for one instantiation of the span loop.</summary>
    private interface IModeFlag;

    private readonly struct On : IModeFlag;

    private readonly struct Off : IModeFlag;

    /// <summary>Draws one- and two-cycle spans with the loop specialized for the current modes.</summary>
    private void DrawSpans(int start, int end)
    {
        var textured = _needTex0 || _needTex1 || _lodFractionUsed;
        if (_cycleType == CycleTwo)
        {
            if (textured)
            {
                DrawSpans<On, On>(start, end);
            }
            else
            {
                DrawSpans<On, Off>(start, end);
            }
        }
        else if (textured)
        {
            DrawSpans<Off, On>(start, end);
        }
        else
        {
            DrawSpans<Off, Off>(start, end);
        }
    }

    private void DrawSpans<TTwoCycle, TTextured>(int start, int end)
        where TTwoCycle : struct, IModeFlag
        where TTextured : struct, IModeFlag
    {
        if (_zCompare)
        {
            if (_imageRead)
            {
                DrawSpans<TTwoCycle, TTextured, On, On>(start, end);
            }
            else
            {
                DrawSpans<TTwoCycle, TTextured, On, Off>(start, end);
            }
        }
        else if (_imageRead)
        {
            DrawSpans<TTwoCycle, TTextured, Off, On>(start, end);
        }
        else
        {
            DrawSpans<TTwoCycle, TTextured, Off, Off>(start, end);
        }
    }

    /// <summary>
    /// The span loop: from the major edge towards the minor edges. Each pixel's work is the hardware pipeline described
    /// on <see cref="N64Rdp"/>'s pixel partial: shade (corrected for partial coverage), depth, texels and LOD, noise and
    /// dither, the combiner's cycles, the framebuffer and Z buffer, the blender, dithering and the writes.
    /// </summary>
    private void DrawSpans<TTwoCycle, TTextured, TZCompare, TImageRead>(int start, int end)
        where TTwoCycle : struct, IModeFlag
        where TTextured : struct, IModeFlag
        where TZCompare : struct, IModeFlag
        where TImageRead : struct, IModeFlag
    {
        var twoCycle = typeof(TTwoCycle) == typeof(On);
        var flip = _flip;
        var xStep = flip ? 1 : -1;
        var sign = flip ? 1 : -1;
        int incR = _step[AttrR] * sign, incG = _step[AttrG] * sign, incB = _step[AttrB] * sign, incA = _step[AttrA] * sign;
        int incS = _step[AttrS] * sign, incT = _step[AttrT] * sign, incW = _step[AttrW] * sign;
        var incZ = _zSourcePrim ? 0 : _step[AttrZ] * sign;
        var dzPix = _zSourcePrim ? _primDeltaZ : _dzPix;
        var dzPixEncoded = CompressDz(dzPix & 0xFFFF);
        var rdram = _host.Rdram;
        var image = _colorImage;
        var fast16 = image.Size == Size16 && image.Format == FormatRgba && _cvgDest == 0;

        // The combiner's selections, per cycle.
        int cA0 = _ccA[0], cB0 = _ccB[0], cM0 = _ccM[0], cD0 = _ccD[0], aA0 = _acA[0], aB0 = _acB[0], aM0 = _acM[0], aD0 = _acD[0];
        int cA1 = _ccA[1], cB1 = _ccB[1], cM1 = _ccM[1], cD1 = _ccD[1], aA1 = _acA[1], aB1 = _acB[1], aM1 = _acM[1], aD1 = _acD[1];
        var ditherNeeded = _rgbDither != 3 || _alphaDither != 3;
        var noiseNeeded = _noiseUsed || _alphaDither == 2;

        for (var line = Math.Max(start, 0); line <= end; line++)
        {
            if (!_lineValid[line])
            {
                continue;
            }

            var spanStart = _lineLx[line];
            var spanEnd = _lineRx[line];
            var majorX = _lineMajorX[line];
            int length, clipped;
            if (flip)
            {
                length = spanStart - spanEnd;
                clipped = spanEnd - majorX;
                ComputeCoverage(line, spanEnd, spanStart);
            }
            else
            {
                length = spanEnd - spanStart;
                clipped = majorX - spanEnd;
                ComputeCoverage(line, spanStart, spanEnd);
            }

            var at = line * 8;
            int r = _lineAttr[at + AttrR], g = _lineAttr[at + AttrG], b = _lineAttr[at + AttrB], a = _lineAttr[at + AttrA];
            int s = _lineAttr[at + AttrS], t = _lineAttr[at + AttrT], w = _lineAttr[at + AttrW];
            var z = _zSourcePrim ? _primZ << 16 : _lineAttr[at + AttrZ];
            if (clipped != 0)
            {
                clipped &= 0xFFF;
                r += incR * clipped;
                g += incG * clipped;
                b += incB * clipped;
                a += incA * clipped;
                s += incS * clipped;
                t += incT * clipped;
                w += incW * clipped;
                z += incZ * clipped;
            }

            _clock += (uint)Math.Max(0, length + 1);
            var rowIndex = line * image.Width;
            var x = spanEnd;
            for (var j = 0; j <= length; j++)
            {
                // This pixel's attributes; the loop's move on to the next pixel at once.
                int pr = r, pg = g, pb = b, pa = a, ps = s, pt = t, pw = w, pz = z, px = x;
                r += incR;
                g += incG;
                b += incB;
                a += incA;
                s += incS;
                t += incT;
                w += incW;
                z += incZ;
                x += xStep;

                var mask = _coverage[px & 0xFFF];
                var coverage = BitOperations.PopCount(mask);
                var centerBit = (mask >> 7) & 1;
                int offX = 0, offY = 0;

                // Shade, with the coverage centroid correction for partly covered pixels.
                if (coverage == 8)
                {
                    pr >>= 16;
                    pg >>= 16;
                    pb >>= 16;
                    pa >>= 16;
                }
                else
                {
                    CoverageOffset(mask, out offX, out offY);
                    pr = (((pr >> 14) << 2) + offX * _correctR + offY * _dyR) >> 4;
                    pg = (((pg >> 14) << 2) + offX * _correctG + offY * _dyG) >> 4;
                    pb = (((pb >> 14) << 2) + offX * _correctB + offY * _dyB) >> 4;
                    pa = (((pa >> 14) << 2) + offX * _correctA + offY * _dyA) >> 4;
                }

                var shadeA = Clamp9(pa);
                if (_shadeUsed)
                {
                    SetChannels(SlotShade, Clamp9(pr), Clamp9(pg), Clamp9(pb), shadeA);
                    if (_alphaAsColorUsed)
                    {
                        SetGraySlot(SlotShadeAlpha, shadeA);
                    }
                }

                // Depth, corrected the same way.
                var sz = (pz >> 10) & 0x3FFFFF;
                sz = coverage == 8 ? sz >> 3 : ((sz << 2) + offX * _correctZ + offY * _dyZ) >> 5;
                sz = ((sz & 0x60000) >> 17) switch
                {
                    2 => 0x3FFFF,
                    3 => 0,
                    _ => sz & 0x3FFFF,
                };

                if (typeof(TTextured) == typeof(On))
                {
                    TexturePixel(twoCycle, ps, pt, pw, incS, incT, incW);
                }

                // Noise and dither.
                var noise = 0;
                if (noiseNeeded)
                {
                    noise = (int)((NextRandom() & 7) << 6) | 0x20;
                    SetGraySlot(SlotNoise, noise);
                }

                int colorDither = 7, alphaDither = 0;
                if (ditherNeeded)
                {
                    DitherValues(px, line, noise, out colorDither, out alphaDither);
                }

                // The combiner.
                int pixelR, pixelG, pixelB, pixelA, alphaCompareValue;
                if (twoCycle)
                {
                    var first = Combine(cA0, cB0, cM0, cD0);
                    var firstAlpha = (Combine(aA0, aB0, aM0, aD0).GetElement(3) >> 8) & 0x1FF;
                    alphaCompareValue = FinalAlpha(firstAlpha, alphaDither, ref coverage, updateCoverage: false);
                    SetVector(SlotCombined, Vector128.ShiftRightLogical(first, 8).WithElement(3, firstAlpha));
                    SetGraySlot(SlotCombinedAlpha, firstAlpha);

                    // The second cycle sees the second texel as TEXEL0 and the next pixel's first texel as TEXEL1.
                    if (_needTex1 || _needNextTex1)
                    {
                        CopySlot(SlotTex1, SlotTex0);
                        CopySlot(SlotTex1Alpha, SlotTex0Alpha);
                        if (_needNextTex1)
                        {
                            SetSlot(SlotTex1, SlotTex1Alpha, _nextTexel);
                        }
                    }
                }
                else
                {
                    alphaCompareValue = 0;
                }

                var full = Combine(cA1, cB1, cM1, cD1);
                var alpha = (Combine(aA1, aB1, aM1, aD1).GetElement(3) >> 8) & 0x1FF;
                int fullR = full.GetElement(0), fullG = full.GetElement(1), fullB = full.GetElement(2);
                if (_keyEnable)
                {
                    pixelR = Clamp9(Input(cA1, 0));
                    pixelG = Clamp9(Input(cA1, 1));
                    pixelB = Clamp9(Input(cA1, 2));
                    pixelA = KeyAlpha(fullR, fullG, fullB);
                }
                else
                {
                    pixelR = Clamp9(fullR >> 8);
                    pixelG = Clamp9(fullG >> 8);
                    pixelB = Clamp9(fullB >> 8);
                    pixelA = FinalAlpha(alpha, alphaDither, ref coverage, updateCoverage: true);
                }

                if (!twoCycle)
                {
                    alphaCompareValue = pixelA;

                    // One-cycle mode's COMBINED input is the previous pixel's result.
                    if (_combinedUsed)
                    {
                        SetVector(SlotCombined, Vector128.ShiftRightLogical(full, 8).WithElement(3, alpha));
                        SetGraySlot(SlotCombinedAlpha, alpha);
                    }
                }

                var blenderShadeA = shadeA + alphaDither;
                if ((blenderShadeA & 0x100) != 0)
                {
                    blenderShadeA = 0xFF;
                }

                // Memory: the colour already in the framebuffer, and its coverage.
                var index = (uint)(rowIndex + px);
                int memR = 0, memG = 0, memB = 0, memCvg = 7;
                if (typeof(TImageRead) == typeof(On))
                {
                    ReadFramebuffer(rdram, image, index, out memR, out memG, out memB, out memCvg);
                }

                // The Z test, which also decides whether anti-aliased edges blend.
                var zAddress = _zImageAddress + index * 2;
                bool blendEnable, prewrap;
                if (typeof(TZCompare) == typeof(On))
                {
                    if (!ZCompare(rdram, zAddress, sz, dzPix, dzPixEncoded, ref coverage, memCvg, out blendEnable, out prewrap))
                    {
                        continue;
                    }
                }
                else
                {
                    prewrap = ((memCvg + coverage) & 8) != 0;
                    blendEnable = _forceBlend || (!prewrap && _antialias);
                    if (_blendUsesMemoryCoverage)
                    {
                        _blendShiftA = 0;
                        _blendShiftB = dzPixEncoded < 0xB ? 4 : 0xF - dzPixEncoded;
                    }
                }

                var memA = memCvg << 5;
                int outR, outG, outB;
                if (twoCycle)
                {
                    if ((_antialias ? coverage : centerBit) == 0)
                    {
                        continue;
                    }

                    BlendCycle(0, pixelR, pixelG, pixelB, pixelA, blenderShadeA, memR, memG, memB, memA, 0, 0, 0, true, out var r0, out var g0, out var b0);
                    if (_alphaCompare && !AlphaCompare(alphaCompareValue))
                    {
                        continue;
                    }

                    if (!blendEnable && !_colorOnCvg && _blendP[1] == 0)
                    {
                        // No blending: the first cycle's result passes through.
                        (outR, outG, outB) = (r0, g0, b0);
                    }
                    else
                    {
                        BlendOutput(1, pixelR, pixelG, pixelB, pixelA, blenderShadeA, memR, memG, memB, memA, r0, g0, b0, blendEnable, prewrap,
                            out outR, out outG, out outB);
                    }
                }
                else
                {
                    if (_alphaCompare && !AlphaCompare(alphaCompareValue))
                    {
                        continue;
                    }

                    if ((_antialias ? coverage : centerBit) == 0)
                    {
                        continue;
                    }

                    if (!blendEnable && !_colorOnCvg && _blendP[0] == 0)
                    {
                        // The common case: no blending, the pixel passes through.
                        (outR, outG, outB) = (pixelR, pixelG, pixelB);
                    }
                    else
                    {
                        BlendOutput(0, pixelR, pixelG, pixelB, pixelA, blenderShadeA, memR, memG, memB, memA, 0, 0, 0, blendEnable, prewrap,
                            out outR, out outG, out outB);
                    }
                }

                if (_rgbDither != 3)
                {
                    outR = Dither(outR, colorDither, 0);
                    outG = Dither(outG, colorDither, 3);
                    outB = Dither(outB, colorDither, 6);
                }

                if (fast16)
                {
                    var clampedCvg = blendEnable ? coverage + memCvg : coverage - 1;
                    var finalCvg = (clampedCvg & 8) != 0 ? 7 : clampedCvg & 7;
                    Write16(rdram, image.Address + index * 2,
                        (ushort)(((outR & ~7) << 8) | ((outG & ~7) << 3) | ((outB & ~7) >> 2) | (finalCvg >> 2)));
                }
                else
                {
                    WriteFramebuffer(rdram, image, index, outR, outG, outB, blendEnable, coverage, memCvg);
                }

                if (_zUpdate)
                {
                    Write16(rdram, zAddress, (ushort)(CompressZ(sz) << 2 | (dzPixEncoded >> 2)));
                }
            }
        }
    }

    /// <summary>
    /// A pixel's texturing: the perspective divide, the LOD and its tiles, and the texels the combiner reads, stored in
    /// its TEXEL0/TEXEL1 slots (and the next pixel's first texel where one-cycle TEXEL1 or two-cycle mode needs it).
    /// </summary>
    private void TexturePixel(bool twoCycle, int s, int t, int w, int dsInc, int dtInc, int dwInc)
    {
        Divide(s >> 16, t >> 16, w >> 16, out var ss, out var st);
        int tile0 = _primTile, tile1 = (_primTile + 1) & 7, lodFraction = 0;
        if (_textureLod || _lodFractionUsed)
        {
            Divide((s + dsInc) >> 16, (t + dtInc) >> 16, (w + dwInc) >> 16, out var nextS, out var nextT);
            int farS, farT;
            if (twoCycle)
            {
                Divide((s + _dsdy) >> 16, (t + _dtdy) >> 16, (w + _dwdy) >> 16, out farS, out farT);
            }
            else
            {
                Divide((s + (dsInc << 1)) >> 16, (t + (dtInc << 1)) >> 16, (w + (dwInc << 1)) >> 16, out farS, out farT);
            }

            var clamped = ((ss | st | nextS | nextT | farS | farT) & 0x60000) != 0;
            var lod = 0;
            if (!clamped)
            {
                lod = twoCycle
                    ? LodDistance(ss, farS, st, farT, LodDistance(ss, nextS, st, nextT, 0))
                    : LodDistance(nextS, farS, nextT, farT, 0);
            }

            LodSignals(clamped, lod, out var level, out var magnify, out var distant, out lodFraction);
            if (_textureLod)
            {
                tile0 = LodTile(level, magnify, distant);
                if (!_detailTexture)
                {
                    tile1 = distant || (!_sharpenTexture && magnify) ? tile0 : (tile0 + 1) & 7;
                }
                else
                {
                    tile1 = (_primTile + (distant ? _maxLevel : level) + (!distant && !magnify ? 2 : 1)) & 7;
                }
            }
        }

        ss = ClampCoordinate(ss);
        st = ClampCoordinate(st);
        SetGraySlot(SlotLodFraction, lodFraction);
        var texel0 = Vector128<int>.Zero;
        if (_needTex0)
        {
            texel0 = TextureCycle(ss, st, tile0, 0, texel0);
        }

        SetSlot(SlotTex0, SlotTex0Alpha, texel0);
        if (twoCycle && _needTex1)
        {
            SetSlot(SlotTex1, SlotTex1Alpha, TextureCycle(ss, st, tile1, 1, texel0));
        }

        if (_needNextTex1)
        {
            Divide((s + dsInc) >> 16, (t + dtInc) >> 16, (w + dwInc) >> 16, out var ns, out var nt);
            var next = TextureCycle(ClampCoordinate(ns), ClampCoordinate(nt), tile0, 0, texel0);
            if (!twoCycle)
            {
                SetSlot(SlotTex1, SlotTex1Alpha, next);
            }
            else
            {
                _nextTexel = next;
            }
        }
    }
}
