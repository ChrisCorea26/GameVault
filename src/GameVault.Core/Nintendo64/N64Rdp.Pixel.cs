using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GameVault.Core.Nintendo64;

/// <summary>
/// The per-pixel pipeline of the one- and two-cycle modes: texturing, the colour and alpha combiners, alpha compare,
/// the Z test, the blender, dithering and the framebuffer write; plus the fill and copy modes' pixels.
/// </summary>
/// <remarks>
/// <para>The combiner computes <c>(A − B) × C + D</c> on 9-bit inputs (A, B and D read 0x180–0x1FF as negative, C reads
/// 0x100–0x1FF as negative), rounds with +0x80 and clamps with the hardware's 9-bit rule: 0x100–0x17F saturate to 255,
/// 0x180–0x1FF become 0. In one-cycle mode it uses the second cycle's settings; the blender uses the first.</para>
/// <para>The blender works on 5-bit factors: <c>(P × A + M × (B + 1)) / 32</c> with force blend, divided by
/// <c>A + B + 1</c> otherwise. Without force blend it only blends anti-aliased edges; elsewhere the pixel passes
/// through as P (n64brew: "Reality Display Processor/Pipeline").</para>
/// </remarks>
public sealed partial class N64Rdp
{
    // Combiner input slots. Colour inputs read _inR/_inG/_inB, alpha inputs read _inA.
    private const int SlotCombined = 0;
    private const int SlotTex0 = 1;
    private const int SlotTex1 = 2;
    private const int SlotPrim = 3;
    private const int SlotShade = 4;
    private const int SlotEnv = 5;
    private const int SlotOne = 6;
    private const int SlotNoise = 7;
    private const int SlotZero = 8;
    private const int SlotKeyCenter = 9;
    private const int SlotKeyScale = 10;
    private const int SlotK4 = 11;
    private const int SlotK5 = 12;
    private const int SlotCombinedAlpha = 13;
    private const int SlotTex0Alpha = 14;
    private const int SlotTex1Alpha = 15;
    private const int SlotPrimAlpha = 16;
    private const int SlotShadeAlpha = 17;
    private const int SlotEnvAlpha = 18;
    private const int SlotLodFraction = 19;
    private const int SlotPrimLod = 20;
    private const int SlotCount = 21;

    private static readonly int[] ColorSubA =
    [
        SlotCombined, SlotTex0, SlotTex1, SlotPrim, SlotShade, SlotEnv, SlotOne, SlotNoise,
        SlotZero, SlotZero, SlotZero, SlotZero, SlotZero, SlotZero, SlotZero, SlotZero,
    ];

    private static readonly int[] ColorSubB =
    [
        SlotCombined, SlotTex0, SlotTex1, SlotPrim, SlotShade, SlotEnv, SlotKeyCenter, SlotK4,
        SlotZero, SlotZero, SlotZero, SlotZero, SlotZero, SlotZero, SlotZero, SlotZero,
    ];

    private static readonly int[] ColorMul = BuildColorMul();

    private static readonly int[] ColorAdd = [SlotCombined, SlotTex0, SlotTex1, SlotPrim, SlotShade, SlotEnv, SlotOne, SlotZero];

    private static readonly int[] AlphaSubAddB = [SlotCombined, SlotTex0, SlotTex1, SlotPrim, SlotShade, SlotEnv, SlotOne, SlotZero];

    private static readonly int[] AlphaMul = [SlotLodFraction, SlotTex0, SlotTex1, SlotPrim, SlotShade, SlotEnv, SlotPrimLod, SlotZero];

    /// <summary>The 4×4 magic-square and Bayer dither matrices, by <c>(y &amp; 3) × 4 + (x &amp; 3)</c>.</summary>
    private static readonly int[] MagicSquare = [0, 6, 1, 7, 4, 2, 5, 3, 3, 5, 2, 4, 7, 1, 6, 0];

    private static readonly int[] Bayer = [0, 4, 1, 5, 4, 0, 5, 1, 3, 7, 2, 6, 7, 3, 6, 2];

    // The combiner's inputs, four channels (R, G, B, A) per slot: as the A, B and D inputs read them (0x180–0x1FF
    // negative) and as the C input reads them (0x100–0x1FF negative).
    private readonly int[] _ext = new int[SlotCount * 4];
    private readonly int[] _mul = new int[SlotCount * 4];

    // Decoded Set Combine Mode: slot indices per cycle.
    private readonly int[] _ccA = new int[2];
    private readonly int[] _ccB = new int[2];
    private readonly int[] _ccM = new int[2];
    private readonly int[] _ccD = new int[2];
    private readonly int[] _acA = new int[2];
    private readonly int[] _acB = new int[2];
    private readonly int[] _acM = new int[2];
    private readonly int[] _acD = new int[2];

    // Which texels and signals the active combiner cycles read (refreshed by Set Combine and Set Other Modes).
    private bool _needTex0;
    private bool _needTex1;
    private bool _needNextTex1;
    private bool _lodFractionUsed;
    private bool _noiseUsed;
    private bool _combinedUsed;
    private bool _shadeUsed;
    private bool _blendUsesMemoryCoverage;
    private bool _alphaAsColorUsed;

    private uint _noise = 1;

    // The blender's shifters for coverage-weighted blending (memory coverage as the B input).
    private int _blendShiftA;
    private int _blendShiftB;

    private static int[] BuildColorMul()
    {
        var map = new int[32];
        Array.Fill(map, SlotZero);
        int[] first =
        [
            SlotCombined, SlotTex0, SlotTex1, SlotPrim, SlotShade, SlotEnv, SlotKeyScale, SlotCombinedAlpha,
            SlotTex0Alpha, SlotTex1Alpha, SlotPrimAlpha, SlotShadeAlpha, SlotEnvAlpha, SlotLodFraction, SlotPrimLod, SlotK5,
        ];
        first.CopyTo(map, 0);
        return map;
    }

    private void SetCombine(ulong w)
    {
        _combineMode = w;
        _ccA[0] = ColorSubA[(int)(w >> 52) & 0xF];
        _ccM[0] = ColorMul[(int)(w >> 47) & 0x1F];
        _acA[0] = AlphaSubAddB[(int)(w >> 44) & 7];
        _acM[0] = AlphaMul[(int)(w >> 41) & 7];
        _ccA[1] = ColorSubA[(int)(w >> 37) & 0xF];
        _ccM[1] = ColorMul[(int)(w >> 32) & 0x1F];
        _ccB[0] = ColorSubB[(int)(w >> 28) & 0xF];
        _ccB[1] = ColorSubB[(int)(w >> 24) & 0xF];
        _acA[1] = AlphaSubAddB[(int)(w >> 21) & 7];
        _acM[1] = AlphaMul[(int)(w >> 18) & 7];
        _ccD[0] = ColorAdd[(int)(w >> 15) & 7];
        _acB[0] = AlphaSubAddB[(int)(w >> 12) & 7];
        _acD[0] = AlphaSubAddB[(int)(w >> 9) & 7];
        _ccD[1] = ColorAdd[(int)(w >> 6) & 7];
        _acB[1] = AlphaSubAddB[(int)(w >> 3) & 7];
        _acD[1] = AlphaSubAddB[(int)w & 7];
        UpdateCombinerUsage();
    }

    /// <summary>Works out which texels and the LOD fraction the active combiner cycles need.</summary>
    private void UpdateCombinerUsage()
    {
        if (_cycleType == CycleTwo)
        {
            // In the second cycle, TEXEL0 is the second texel and TEXEL1 the next pixel's first.
            _needTex0 = Uses(0, SlotTex0, SlotTex0Alpha) || Uses(0, SlotTex1, SlotTex1Alpha) || Uses(1, SlotTex0, SlotTex0Alpha);
            _needTex1 = Uses(0, SlotTex1, SlotTex1Alpha) || Uses(1, SlotTex0, SlotTex0Alpha);
            _needNextTex1 = Uses(1, SlotTex1, SlotTex1Alpha);
            _needTex0 |= _needNextTex1;
            _lodFractionUsed = Uses(0, SlotLodFraction, SlotLodFraction) || Uses(1, SlotLodFraction, SlotLodFraction);
            _noiseUsed = Uses(0, SlotNoise, SlotNoise) || Uses(1, SlotNoise, SlotNoise);
        }
        else
        {
            // One cycle: the second cycle's settings; TEXEL1 is the next pixel's texel.
            _needTex0 = Uses(1, SlotTex0, SlotTex0Alpha);
            _needNextTex1 = Uses(1, SlotTex1, SlotTex1Alpha);
            _needTex1 = false;
            _lodFractionUsed = Uses(1, SlotLodFraction, SlotLodFraction);
            _noiseUsed = Uses(1, SlotNoise, SlotNoise);
        }

        _combinedUsed = Uses(1, SlotCombined, SlotCombinedAlpha);
        _shadeUsed = Uses(1, SlotShade, SlotShadeAlpha) || (_cycleType == CycleTwo && Uses(0, SlotShade, SlotShadeAlpha));
        _blendUsesMemoryCoverage = _blendB[0] == 1 || _blendB[1] == 1;
        _alphaAsColorUsed = _ccM[0] is SlotCombinedAlpha or SlotTex0Alpha or SlotTex1Alpha or SlotShadeAlpha ||
                            _ccM[1] is SlotCombinedAlpha or SlotTex0Alpha or SlotTex1Alpha or SlotShadeAlpha;
    }

    private bool Uses(int cycle, int slot, int alphaSlot) =>
        _ccA[cycle] == slot || _ccB[cycle] == slot || _ccM[cycle] == slot || _ccD[cycle] == slot ||
        _ccM[cycle] == alphaSlot || _acA[cycle] == slot || _acB[cycle] == slot || _acM[cycle] == slot || _acD[cycle] == slot;

    /// <summary>Refreshes the combiner inputs that only change with Set commands.</summary>
    private void UpdateConstantInputs()
    {
        SetSlot(SlotPrim, _primColor);
        SetSlot(SlotEnv, _envColor);
        SetGraySlot(SlotPrimAlpha, (int)_primColor & 0xFF);
        SetGraySlot(SlotEnvAlpha, (int)_envColor & 0xFF);
        SetGraySlot(SlotOne, 0x100);
        SetGraySlot(SlotZero, 0);
        SetGraySlot(SlotK4, _k4);
        SetGraySlot(SlotK5, _k5);
        SetGraySlot(SlotPrimLod, _primLodFraction);
        SetChannels(SlotKeyCenter, _keyCenterR, _keyCenterG, _keyCenterB, 0);
        SetChannels(SlotKeyScale, _keyScaleR, _keyScaleG, _keyScaleB, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetChannels(int slot, int r, int g, int b, int a)
    {
        ref var ext = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ext), slot << 2);
        ref var mul = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_mul), slot << 2);
        ext = Ext9(r);
        Unsafe.Add(ref ext, 1) = Ext9(g);
        Unsafe.Add(ref ext, 2) = Ext9(b);
        Unsafe.Add(ref ext, 3) = Ext9(a);
        mul = Sign9(r);
        Unsafe.Add(ref mul, 1) = Sign9(g);
        Unsafe.Add(ref mul, 2) = Sign9(b);
        Unsafe.Add(ref mul, 3) = Sign9(a);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetSlot(int slot, uint rgba) =>
        SetChannels(slot, (int)(rgba >> 24), (int)(rgba >> 16) & 0xFF, (int)(rgba >> 8) & 0xFF, (int)rgba & 0xFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetSlot(int slot, int alphaSlot, in Rgba c)
    {
        SetChannels(slot, c.R, c.G, c.B, c.A);
        if (_alphaAsColorUsed)
        {
            SetGraySlot(alphaSlot, c.A);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetGraySlot(int slot, int value) => SetChannels(slot, value, value, value, value);

    /// <summary>Copies one slot's channels to another.</summary>
    private void CopySlot(int from, int to)
    {
        Array.Copy(_ext, from << 2, _ext, to << 2, 4);
        Array.Copy(_mul, from << 2, _mul, to << 2, 4);
    }

    /// <summary>A slot's channel as the A/B/D inputs read it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Input(int slot, int channel) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ext), (slot << 2) + channel);

    /// <summary>A/B/D inputs: 9 bits, with 0x180–0x1FF negative.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Ext9(int v) => (v & 0x180) == 0x180 ? v | ~0x1FF : v & 0x1FF;

    /// <summary>The C (multiplier) input: 9 bits, with 0x100–0x1FF negative.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sign9(int v) => (v & 0x1FF) | -(v & 0x100);

    /// <summary>One combiner equation on one channel, before the shift: 17 bits whose top 9 are the result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Combine(int channel, int a, int b, int m, int d)
    {
        ref var ext = ref MemoryMarshal.GetArrayDataReference(_ext);
        ref var mul = ref MemoryMarshal.GetArrayDataReference(_mul);
        return ((Unsafe.Add(ref ext, (a << 2) + channel) - Unsafe.Add(ref ext, (b << 2) + channel)) * Unsafe.Add(ref mul, (m << 2) + channel) +
                (Unsafe.Add(ref ext, (d << 2) + channel) << 8) + 0x80) & 0x1FFFF;
    }

    /// <summary>The combiner's output clamp: 0x100–0x17F saturate, 0x180–0x1FF are negative and become 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp9(int v)
    {
        v &= 0x1FF;
        return v < 0x100 ? v : v < 0x180 ? 0xFF : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint NextRandom()
    {
        _noise = _noise * 0x343FD + 0x269EC3;
        return (_noise >> 16) & 0x7FFF;
    }

    /// <summary>The colour and alpha dither values of a pixel (Set Other Modes' RGB and alpha dither selects).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DitherValues(int x, int y, int noise, out int colorDither, out int alphaDither)
    {
        if (_scissorField)
        {
            y >>= 1;
        }

        var index = ((y & 3) << 2) | (x & 3);
        int pattern;
        switch (_rgbDither)
        {
            case 0:
                colorDither = pattern = MagicSquare[index];
                break;
            case 1:
                colorDither = pattern = Bayer[index];
                break;
            case 2:
                colorDither = (int)NextRandom();
                pattern = MagicSquare[index];
                break;
            default:
                colorDither = 7;
                pattern = Bayer[index];
                break;
        }

        alphaDither = _alphaDither switch
        {
            0 => pattern,
            1 => ~pattern & 7,
            2 => (noise >> 6) & 7,
            _ => 0,
        };
    }

    /// <summary>
    /// Draws one pixel in one- or two-cycle mode. <paramref name="mask"/> is its coverage: two samples per quarter
    /// scanline, top quarter in bits 7 and 5, then 6 and 4, 3 and 1, 2 and 0. The attributes are the edge walker's
    /// fixed-point values; <paramref name="dsInc"/>, <paramref name="dtInc"/> and <paramref name="dwInc"/> step to the next
    /// pixel, for the LOD and for one-cycle mode's TEXEL1.
    /// </summary>
    private void ShadePixel(Span<byte> rdram, int x, int y, int mask, int r, int g, int b, int a, int s, int t, int w, int z,
        int dsInc, int dtInc, int dwInc, int dzPix, int dzPixEncoded)
    {
        var twoCycle = _cycleType == CycleTwo;
        var coverage = BitOperations.PopCount((uint)mask);
        var centerBit = (mask >> 7) & 1;
        int offX = 0, offY = 0;

        // Shade, with the coverage centroid correction for partly covered pixels.
        if (coverage == 8)
        {
            r >>= 16;
            g >>= 16;
            b >>= 16;
            a >>= 16;
        }
        else
        {
            CoverageOffset(mask, out offX, out offY);
            r = (((r >> 14) << 2) + offX * _correctR + offY * _dyR) >> 4;
            g = (((g >> 14) << 2) + offX * _correctG + offY * _dyG) >> 4;
            b = (((b >> 14) << 2) + offX * _correctB + offY * _dyB) >> 4;
            a = (((a >> 14) << 2) + offX * _correctA + offY * _dyA) >> 4;
        }

        var shadeA = Clamp9(a);
        if (_shadeUsed)
        {
            SetChannels(SlotShade, Clamp9(r), Clamp9(g), Clamp9(b), shadeA);
            if (_alphaAsColorUsed)
            {
                SetGraySlot(SlotShadeAlpha, shadeA);
            }
        }

        // Depth, corrected the same way.
        var sz = (z >> 10) & 0x3FFFFF;
        sz = coverage == 8 ? sz >> 3 : ((sz << 2) + offX * _correctZ + offY * _dyZ) >> 5;
        sz = ((sz & 0x60000) >> 17) switch
        {
            2 => 0x3FFFF,
            3 => 0,
            _ => sz & 0x3FFFF,
        };

        // Texturing and the level of detail.
        if (_needTex0 || _needTex1 || _lodFractionUsed)
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
            var texel0 = default(Rgba);
            if (_needTex0)
            {
                texel0 = TextureCycle(ss, st, tile0, 0, texel0);
            }

            if (twoCycle)
            {
                SetSlot(SlotTex0, SlotTex0Alpha, texel0);
                if (_needTex1)
                {
                    SetSlot(SlotTex1, SlotTex1Alpha, TextureCycle(ss, st, tile1, 1, texel0));
                }
            }
            else
            {
                SetSlot(SlotTex0, SlotTex0Alpha, texel0);
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

        // Noise and dither.
        var noise = 0;
        if (_noiseUsed || _alphaDither == 2)
        {
            noise = (int)((NextRandom() & 7) << 6) | 0x20;
            SetGraySlot(SlotNoise, noise);
        }

        int colorDither = 7, alphaDither = 0;
        if (_rgbDither != 3 || _alphaDither != 3)
        {
            DitherValues(x, y, noise, out colorDither, out alphaDither);
        }

        // The combiner.
        int pixelR, pixelG, pixelB, pixelA, alphaCompareValue;
        if (twoCycle)
        {
            var c0r = Combine(0, _ccA[0], _ccB[0], _ccM[0], _ccD[0]);
            var c0g = Combine(1, _ccA[0], _ccB[0], _ccM[0], _ccD[0]);
            var c0b = Combine(2, _ccA[0], _ccB[0], _ccM[0], _ccD[0]);
            var c0a = (Combine(3, _acA[0], _acB[0], _acM[0], _acD[0]) >> 8) & 0x1FF;
            alphaCompareValue = FinalAlpha(c0a, alphaDither, ref coverage, updateCoverage: false);
            SetChannels(SlotCombined, c0r >> 8, c0g >> 8, c0b >> 8, c0a);
            SetGraySlot(SlotCombinedAlpha, c0a);

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

        var keySource = _keyEnable ? _ccA[1] : 0;
        var fullR = Combine(0, _ccA[1], _ccB[1], _ccM[1], _ccD[1]);
        var fullG = Combine(1, _ccA[1], _ccB[1], _ccM[1], _ccD[1]);
        var fullB = Combine(2, _ccA[1], _ccB[1], _ccM[1], _ccD[1]);
        var alpha = (Combine(3, _acA[1], _acB[1], _acM[1], _acD[1]) >> 8) & 0x1FF;
        if (_keyEnable)
        {
            pixelR = Clamp9(Input(keySource, 0));
            pixelG = Clamp9(Input(keySource, 1));
            pixelB = Clamp9(Input(keySource, 2));
        }
        else
        {
            pixelR = Clamp9(fullR >> 8);
            pixelG = Clamp9(fullG >> 8);
            pixelB = Clamp9(fullB >> 8);
        }

        pixelA = _keyEnable ? KeyAlpha(fullR, fullG, fullB) : FinalAlpha(alpha, alphaDither, ref coverage, updateCoverage: true);
        if (!twoCycle)
        {
            alphaCompareValue = pixelA;

            // One-cycle mode's COMBINED input is the previous pixel's result.
            if (_combinedUsed)
            {
                SetChannels(SlotCombined, fullR >> 8, fullG >> 8, fullB >> 8, alpha);
                SetGraySlot(SlotCombinedAlpha, alpha);
            }
        }

        var blenderShadeA = shadeA + alphaDither;
        if ((blenderShadeA & 0x100) != 0)
        {
            blenderShadeA = 0xFF;
        }

        // Memory: the colour already in the framebuffer, and its coverage.
        var image = _colorImage;
        var index = (uint)(y * image.Width + x);
        int memR = 0, memG = 0, memB = 0, memCvg = 7;
        if (_imageRead)
        {
            ReadFramebuffer(rdram, image, index, out memR, out memG, out memB, out memCvg);
        }

        // The Z test, which also decides whether anti-aliased edges blend.
        var zAddress = _zImageAddress + index * 2;
        bool blendEnable, prewrap;
        if (_zCompare)
        {
            if (!ZCompare(rdram, zAddress, sz, dzPix, dzPixEncoded, ref coverage, memCvg, out blendEnable, out prewrap))
            {
                return;
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
                return;
            }

            BlendCycle(0, pixelR, pixelG, pixelB, pixelA, blenderShadeA, memR, memG, memB, memA, 0, 0, 0, true, out var r0, out var g0, out var b0);
            if (_alphaCompare && !AlphaCompare(alphaCompareValue))
            {
                return;
            }

            BlendOutput(1, pixelR, pixelG, pixelB, pixelA, blenderShadeA, memR, memG, memB, memA, r0, g0, b0, blendEnable, prewrap, out outR, out outG, out outB);
        }
        else
        {
            if (_alphaCompare && !AlphaCompare(alphaCompareValue))
            {
                return;
            }

            if ((_antialias ? coverage : centerBit) == 0)
            {
                return;
            }

            if (!blendEnable && !_colorOnCvg && _blendP[0] == 0)
            {
                // The common case: no blending, the pixel passes through.
                (outR, outG, outB) = (pixelR, pixelG, pixelB);
            }
            else
            {
                BlendOutput(0, pixelR, pixelG, pixelB, pixelA, blenderShadeA, memR, memG, memB, memA, 0, 0, 0, blendEnable, prewrap, out outR, out outG, out outB);
            }
        }

        if (_rgbDither != 3)
        {
            outR = Dither(outR, colorDither, 0);
            outG = Dither(outG, colorDither, 3);
            outB = Dither(outB, colorDither, 6);
        }

        if (image.Size == Size16 && image.Format == FormatRgba && _cvgDest == 0)
        {
            var clamped = blendEnable ? coverage + memCvg : coverage - 1;
            var finalCvg = (clamped & 8) != 0 ? 7 : clamped & 7;
            Write16(rdram, image.Address + index * 2, (ushort)(((outR & ~7) << 8) | ((outG & ~7) << 3) | ((outB & ~7) >> 2) | (finalCvg >> 2)));
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

    private Rgba _nextTexel;

    // Coverage correction steps, set by the edge walker: per pixel and per scanline.
    private int _correctR, _correctG, _correctB, _correctA, _correctZ;
    private int _dyR, _dyG, _dyB, _dyA, _dyZ;

    /// <summary>
    /// The alpha that leaves the combiner: 255 becomes 256 (full), coverage can replace or scale it, and the alpha
    /// dither is added.
    /// </summary>
    private int FinalAlpha(int combinedAlpha, int alphaDither, ref int coverage, bool updateCoverage)
    {
        var alpha = Clamp9(combinedAlpha);
        if (alpha == 0xFF)
        {
            alpha = 0x100;
        }

        var scaled = 0;
        if (_cvgTimesAlpha)
        {
            scaled = (alpha * coverage + 4) >> 3;
            if (updateCoverage)
            {
                coverage = (scaled >> 5) & 0xF;
            }
        }

        if (!_alphaCvgSelect)
        {
            alpha += alphaDither;
            if ((alpha & 0x100) != 0)
            {
                alpha = 0xFF;
            }
        }
        else
        {
            alpha = Math.Min(0xFF, _cvgTimesAlpha ? scaled : coverage << 5);
        }

        return alpha;
    }

    /// <summary>Chroma key: the alpha is how far inside the key's widths the combined colour lies (0–255).</summary>
    private int KeyAlpha(int fullR, int fullG, int fullB)
    {
        static int Channel(int full, int width)
        {
            var v = SignExtend(full, 17);
            if (v > 0)
            {
                v = (v & 0xF) == 8 ? -v + 0x10 : -v;
            }

            return (width << 4) + v;
        }

        var key = Math.Min(Channel(fullR, _keyWidthR), Math.Min(Channel(fullG, _keyWidthG), Channel(fullB, _keyWidthB)));
        return Math.Clamp(key, 0, 0xFF);
    }

    private bool AlphaCompare(int alpha)
    {
        var threshold = _ditherAlpha ? (int)NextRandom() & 0xFF : (int)_blendColor & 0xFF;
        return alpha >= threshold;
    }

    /// <summary>The colour dither: a channel moves up to the next 5-bit step when its low bits exceed the dither value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Dither(int c, int dither, int shift)
    {
        var compare = _rgbDither == 2 ? (dither >> shift) & 7 : dither;
        if (compare >= (c & 7))
        {
            return c;
        }

        return c > 247 ? 255 : (c & 0xF8) + 8;
    }

    /// <summary>The first covered sample of a coverage mask, for the shade and depth centroid correction.</summary>
    private static void CoverageOffset(int mask, out int offX, out int offY)
    {
        // Rows of the coverage mask from the top, as 4-bit column masks (column 0 is the leftmost).
        Span<int> rows = [(mask >> 4) & 0xA, (mask >> 4) & 0x5, mask & 0xA, mask & 0x5];
        offY = 0;
        while (offY < 3 && rows[offY] == 0)
        {
            offY++;
        }

        if (rows[offY] == 0)
        {
            offY = 0;
        }

        var row = rows[offY];
        offX = (row & 8) != 0 ? 0 : (row & 4) != 0 ? 1 : (row & 2) != 0 ? 2 : (row & 1) != 0 ? 3 : 0;
    }

    private void ReadFramebuffer(Span<byte> rdram, Image image, uint index, out int r, out int g, out int b, out int coverage)
    {
        r = g = b = 0;
        coverage = 7;
        if (!_imageRead)
        {
            return;
        }

        switch (image.Size)
        {
            case Size16:
            {
                int old = Read16(rdram, image.Address + index * 2);
                if (image.Format == FormatRgba)
                {
                    r = Expand5((old >> 11) & 31) & 0xF8;
                    g = Expand5((old >> 6) & 31) & 0xF8;
                    b = Expand5((old >> 1) & 31) & 0xF8;

                    // The two low coverage bits live in RDRAM's hidden ninth bits, which aren't kept: they read as
                    // copies of the visible one.
                    coverage = (old & 1) != 0 ? 7 : 0;
                }
                else
                {
                    r = g = b = old >> 8;
                    coverage = (old >> 5) & 7;
                }

                break;
            }

            case Size32:
            {
                var old = Read32(rdram, image.Address + index * 4);
                r = (int)(old >> 24);
                g = (int)(old >> 16) & 0xFF;
                b = (int)(old >> 8) & 0xFF;
                coverage = (int)(old >> 5) & 7;
                break;
            }

            case Size8:
                r = g = b = Read8(rdram, image.Address + index);
                break;
        }
    }

    private void WriteFramebuffer(Span<byte> rdram, Image image, uint index, int r, int g, int b, bool blendEnable, int coverage, int memCvg)
    {
        var clamped = blendEnable ? coverage + memCvg : coverage - 1;
        var finalCvg = _cvgDest switch
        {
            0 => (clamped & 8) != 0 ? 7 : clamped & 7,
            1 => (coverage + memCvg) & 7,
            2 => 7,
            _ => memCvg,
        };

        switch (image.Size)
        {
            case Size16:
                if (image.Format == FormatRgba)
                {
                    Write16(rdram, image.Address + index * 2,
                        (ushort)(((r & ~7) << 8) | ((g & ~7) << 3) | ((b & ~7) >> 2) | (finalCvg >> 2)));
                }
                else
                {
                    Write16(rdram, image.Address + index * 2, (ushort)((r << 8) | (finalCvg << 5)));
                }

                break;
            case Size32:
                Write32(rdram, image.Address + index * 4, (uint)(r << 24 | g << 16 | b << 8 | finalCvg << 5));
                break;
            case Size8:
            {
                var address = image.Address + index;
                Write8(rdram, address, (byte)((address & 1) != 0 ? g : r));
                break;
            }
        }
    }

    /// <summary>
    /// The blender's output for the last cycle: P when not blending (or when a fully opaque pixel blends with its own
    /// inverse alpha), the blend when blending, and M (memory) when colour-on-coverage keeps the old colour.
    /// </summary>
    private void BlendOutput(int cycle, int r, int g, int b, int alpha, int shadeA, int memR, int memG, int memB, int memA,
        int firstR, int firstG, int firstB, bool blendEnable, bool prewrap, out int outR, out int outG, out int outB)
    {
        if (_colorOnCvg && !prewrap)
        {
            BlendColor(_blendM[cycle], cycle, r, g, b, memR, memG, memB, firstR, firstG, firstB, out outR, out outG, out outB);
            return;
        }

        var partialReject = _blendA[cycle] == 0 && _blendB[cycle] == 0 && alpha >= 0xFF;
        if (!blendEnable || partialReject)
        {
            BlendColor(_blendP[cycle], cycle, r, g, b, memR, memG, memB, firstR, firstG, firstB, out outR, out outG, out outB);
            return;
        }

        BlendCycle(cycle, r, g, b, alpha, shadeA, memR, memG, memB, memA, firstR, firstG, firstB, false, out outR, out outG, out outB);
    }

    /// <summary>The blender's P/M colour input: the pixel (or the first cycle's result), memory, blend or fog colour.</summary>
    private void BlendColor(int select, int cycle, int r, int g, int b, int memR, int memG, int memB, int firstR, int firstG, int firstB,
        out int outR, out int outG, out int outB)
    {
        switch (select)
        {
            case 0:
                (outR, outG, outB) = cycle == 0 ? (r, g, b) : (firstR, firstG, firstB);
                break;
            case 1:
                (outR, outG, outB) = (memR, memG, memB);
                break;
            case 2:
                (outR, outG, outB) = ((int)(_blendColor >> 24), (int)(_blendColor >> 16) & 0xFF, (int)(_blendColor >> 8) & 0xFF);
                break;
            default:
                (outR, outG, outB) = ((int)(_fogColor >> 24), (int)(_fogColor >> 16) & 0xFF, (int)(_fogColor >> 8) & 0xFF);
                break;
        }
    }

    /// <summary>
    /// One blender equation. <paramref name="firstOfTwo"/> is the first cycle of two-cycle mode, which always shifts by
    /// 5; otherwise force blend shifts and plain blending divides by the factors' sum.
    /// </summary>
    private void BlendCycle(int cycle, int r, int g, int b, int alpha, int shadeA, int memR, int memG, int memB, int memA,
        int firstR, int firstG, int firstB, bool firstOfTwo, out int outR, out int outG, out int outB)
    {
        BlendColor(_blendP[cycle], cycle, r, g, b, memR, memG, memB, firstR, firstG, firstB, out var pR, out var pG, out var pB);
        BlendColor(_blendM[cycle], cycle, r, g, b, memR, memG, memB, firstR, firstG, firstB, out var mR, out var mG, out var mB);
        var aInput = _blendA[cycle] switch
        {
            0 => alpha,
            1 => (int)_fogColor & 0xFF,
            2 => shadeA,
            _ => 0,
        };
        var factorA = aInput >> 3;
        var factorB = _blendB[cycle] switch
        {
            0 => (~aInput & 0xFF) >> 3,
            1 => memA >> 3,
            2 => 0xFF >> 3,
            _ => 0,
        };

        if (_blendB[cycle] == 1)
        {
            // Memory coverage as B: the factors are scaled by the depth slopes' difference.
            factorA = (factorA >> _blendShiftA) & 0x3C;
            factorB = (factorB >> _blendShiftB) | 3;
        }

        var mulB = factorB + 1;
        var blendR = pR * factorA + mR * mulB;
        var blendG = pG * factorA + mG * mulB;
        var blendB = pB * factorA + mB * mulB;
        if (_forceBlend || firstOfTwo)
        {
            outR = (blendR >> 5) & 0xFF;
            outG = (blendG >> 5) & 0xFF;
            outB = (blendB >> 5) & 0xFF;
        }
        else
        {
            var sum = (factorA & ~3) + (factorB & ~3) + 4;
            outR = Math.Min(0xFF, blendR / sum);
            outG = Math.Min(0xFF, blendG / sum);
            outB = Math.Min(0xFF, blendB / sum);
        }
    }

    /// <summary>
    /// The Z test by Z mode (n64brew: "Reality Display Processor/Z buffer"). It also works out whether an anti-aliased
    /// edge blends (<paramref name="blendEnable"/>) and whether the coverage wrapped (<paramref name="prewrap"/>), and in
    /// interpenetrating mode scales the coverage where two surfaces cross.
    /// </summary>
    private bool ZCompare(Span<byte> rdram, uint zAddress, int sz, int dzPix, int dzPixEncoded, ref int coverage, int memCvg, out bool blendEnable, out bool prewrap)
    {
        var overflow = ((memCvg + coverage) & 8) != 0;
        prewrap = overflow;
        int stored = Read16(rdram, zAddress);
        var oldZ = DecompressZ((stored >> 2) & 0x3FFF);
        var rawDzMem = (stored & 3) << 2;
        var dzMem = 1 << rawDzMem;
        _blendShiftA = Math.Clamp(dzPixEncoded - rawDzMem, 0, 4);
        _blendShiftB = Math.Clamp(rawDzMem - dzPixEncoded, 0, 4);

        var forceCoplanar = false;
        var precision = (stored >> 13) & 0xF;
        if (precision < 3)
        {
            if (dzMem != 0x8000)
            {
                var floor = 16 >> precision;
                dzMem <<= 1;
                if (dzMem < floor)
                {
                    dzMem = floor;
                }
            }
            else
            {
                forceCoplanar = true;
                dzMem = 0xFFFF;
            }
        }

        var combined = (dzPix | dzMem) & 0xFFFF;
        var dzNew = combined == 0 ? 0 : 1 << (31 - BitOperations.LeadingZeroCount((uint)combined));
        var dzNotShifted = dzNew;
        dzNew <<= 3;
        var farther = forceCoplanar || sz + dzNew >= oldZ;
        blendEnable = _forceBlend || (!overflow && _antialias && farther);

        var max = oldZ == 0x3FFFF;
        var nearer = forceCoplanar || sz - dzNew <= oldZ;
        var inFront = sz < oldZ;
        switch (_zMode)
        {
            case 0: // opaque
                return max || (overflow ? inFront : nearer);
            case 1: // interpenetrating
                if (!inFront || !farther || !overflow)
                {
                    return max || (overflow ? inFront : nearer);
                }

                var dzEncoded = CompressDz(dzNotShifted & 0xFFFF);
                var weight = ((oldZ >> dzEncoded) - (sz >> dzEncoded)) & 0xF;
                coverage = ((weight * coverage) >> 3) & 0xF;
                return true;
            case 2: // transparent
                return inFront || max;
            default: // decal
                return farther && nearer && !max;
        }
    }

    /// <summary>The depth slope's 4-bit code: the position of its highest set bit.</summary>
    private static int CompressDz(int value)
    {
        var j = 0;
        if ((value & 0xFF00) != 0)
        {
            j |= 8;
        }

        if ((value & 0xF0F0) != 0)
        {
            j |= 4;
        }

        if ((value & 0xCCCC) != 0)
        {
            j |= 2;
        }

        if ((value & 0xAAAA) != 0)
        {
            j |= 1;
        }

        return j;
    }

    /// <summary>The depth slope of a primitive: a power of two at least as large as the sum of its X and Y slopes.</summary>
    private static int NormalizeDzPix(int sum)
    {
        if ((sum & 0xC000) != 0)
        {
            return 0x8000;
        }

        if ((sum & 0xFFFF) == 0)
        {
            return 1;
        }

        if (sum == 1)
        {
            return 3;
        }

        return 1 << (32 - BitOperations.LeadingZeroCount((uint)(sum & 0x3FFF)));
    }

    /// <summary>
    /// Z buffer compression: the 18-bit depth becomes a 3-bit exponent (the leading ones) and an 11-bit mantissa
    /// (n64brew: "Reality Display Processor/Z buffer").
    /// </summary>
    internal static int CompressZ(int z)
    {
        z &= 0x3FFFF;
        var exponent = 0;
        while (exponent < 7 && (z & (0x20000 >> exponent)) != 0)
        {
            exponent++;
        }

        return (exponent << 11) | ((z >> Math.Max(0, 6 - exponent)) & 0x7FF);
    }

    internal static int DecompressZ(int compressed)
    {
        var exponent = (compressed >> 11) & 7;
        var mantissa = compressed & 0x7FF;
        var ones = 0x3FFFF & ~(0x3FFFF >> exponent);
        return (ones | (mantissa << Math.Max(0, 6 - exponent))) & 0x3FFFF;
    }

    /// <summary>A pixel in fill mode: the fill colour, which holds two 16-bit pixels (or one 32-bit, or four 8-bit).</summary>
    private void FillPixel(int x, int y)
    {
        var image = _colorImage;
        var index = (uint)(y * image.Width + x);
        switch (image.Size)
        {
            case Size16:
                WriteRdram16(image.Address + index * 2, (ushort)((x & 1) != 0 ? _fillColor : _fillColor >> 16));
                break;
            case Size32:
                WriteRdram32(image.Address + index * 4, _fillColor);
                break;
            case Size8:
            {
                var address = image.Address + index;
                WriteRdram8(address, (byte)(_fillColor >> (((int)(address & 3) ^ 3) << 3)));
                break;
            }
        }
    }
}
