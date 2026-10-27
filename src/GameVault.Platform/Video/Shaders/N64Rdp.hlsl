// The N64's RDP as a compute shader (docs/plans/gpu-renderers.md, milestone 2): the same integer arithmetic as
// GameVault.Core's N64Rdp, per pixel, at an internal resolution of Scale × the RDP's. Each thread owns one pixel of
// the scaled colour and Z surfaces and walks a batch of primitives in order, so draw order and every read of the
// framebuffer and Z buffer are exactly the RDP's. Compiled by fxc (cs_5_0, Direct3D 11) and by glslang's HLSL front
// end (SPIR-V, the tests' Vulkan device), so it keeps to what both accept: 32-bit integers, uint buffers, no recursion.
//
// Surfaces hold the RDP's own pixel values (16-bit RGBA5551 or IA88, 32-bit RGBA8888, 8-bit), one uint per pixel, and
// Z the RDP's 16-bit compressed depth with its 2-bit slope. Textures come from TMEM snapshots the RDP loaded.

RWStructuredBuffer<uint> Params : register(u0);
RWStructuredBuffer<uint> Prims : register(u1);
RWStructuredBuffer<uint> Tmem : register(u2);
RWStructuredBuffer<uint> Color : register(u3);
RWStructuredBuffer<uint> Depth : register(u4);

// Params
#define P_COUNT 0
#define P_SCALE 1
#define P_WIDTH 2      // the image's width in RDP pixels (its stride)
#define P_HEIGHT 3     // rows of the surface, in RDP pixels
#define P_ORIGIN_X 4   // the dispatch's first scaled pixel
#define P_ORIGIN_Y 5
#define P_SEED 6       // the batch's noise seed

// One primitive: PRIM_WORDS uints.
#define PRIM_WORDS 104
#define F_FLAGS 0      // bit 0 flip, 1-3 max level, 4-6 tile
#define F_YL 1
#define F_YM 2
#define F_YH 3
#define F_XL 4
#define F_XH 5
#define F_XM 6
#define F_DXLDY 7
#define F_DXHDY 8
#define F_DXMDY 9
#define F_VALUE 10
#define F_DX 18
#define F_DE 26
#define F_DY 34
#define F_MODES_LO 42
#define F_MODES_HI 43
#define F_COMBINE_LO 44
#define F_COMBINE_HI 45
#define F_FILL 46
#define F_FOG 47
#define F_BLEND 48
#define F_PRIM 49
#define F_ENV 50
#define F_PRIM_LOD 51
#define F_PRIM_MIN_LEVEL 52
#define F_PRIM_Z 53
#define F_PRIM_DZ 54
#define F_KEYGB_LO 55
#define F_KEYGB_HI 56
#define F_KEYR_LO 57
#define F_CONVERT_LO 58
#define F_CONVERT_HI 59
#define F_SCISSOR_LO 60
#define F_SCISSOR_HI 61
#define F_COLOR_FORMAT 62
#define F_COLOR_SIZE 63
#define F_COLOR_WIDTH 64
#define F_COLOR_ADDRESS 65
#define F_Z_ADDRESS 66
#define F_TMEM 67      // TMEM snapshot index
#define F_BOX 68       // scaled x0, y0, x1, y1 (end exclusive)
#define F_TILES 72     // 8 × (Set Tile low, high, Sl | Tl << 16, Sh | Th << 16)

#define SIZE4 0
#define SIZE8 1
#define SIZE16 2
#define SIZE32 3
#define FMT_RGBA 0
#define FMT_YUV 1
#define FMT_CI 2
#define FMT_IA 3
#define FMT_I 4

#define CYCLE_ONE 0
#define CYCLE_TWO 1
#define CYCLE_COPY 2
#define CYCLE_FILL 3

// Combiner slots.
#define S_COMBINED 0
#define S_TEX0 1
#define S_TEX1 2
#define S_PRIM 3
#define S_SHADE 4
#define S_ENV 5
#define S_ONE 6
#define S_NOISE 7
#define S_ZERO 8
#define S_KEY_CENTER 9
#define S_KEY_SCALE 10
#define S_K4 11
#define S_K5 12
#define S_COMBINED_ALPHA 13
#define S_TEX0_ALPHA 14
#define S_TEX1_ALPHA 15
#define S_PRIM_ALPHA 16
#define S_SHADE_ALPHA 17
#define S_ENV_ALPHA 18
#define S_LOD_FRACTION 19
#define S_PRIM_LOD 20
#define SLOT_COUNT 21

static const int ColorSubA[16] = { S_COMBINED, S_TEX0, S_TEX1, S_PRIM, S_SHADE, S_ENV, S_ONE, S_NOISE,
    S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO };
static const int ColorSubB[16] = { S_COMBINED, S_TEX0, S_TEX1, S_PRIM, S_SHADE, S_ENV, S_KEY_CENTER, S_K4,
    S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO };
static const int ColorMulTable[32] = { S_COMBINED, S_TEX0, S_TEX1, S_PRIM, S_SHADE, S_ENV, S_KEY_SCALE, S_COMBINED_ALPHA,
    S_TEX0_ALPHA, S_TEX1_ALPHA, S_PRIM_ALPHA, S_SHADE_ALPHA, S_ENV_ALPHA, S_LOD_FRACTION, S_PRIM_LOD, S_K5,
    S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO, S_ZERO };
static const int ColorAdd[8] = { S_COMBINED, S_TEX0, S_TEX1, S_PRIM, S_SHADE, S_ENV, S_ONE, S_ZERO };
static const int AlphaSubAddB[8] = { S_COMBINED, S_TEX0, S_TEX1, S_PRIM, S_SHADE, S_ENV, S_ONE, S_ZERO };
static const int AlphaMul[8] = { S_LOD_FRACTION, S_TEX0, S_TEX1, S_PRIM, S_SHADE, S_ENV, S_PRIM_LOD, S_ZERO };
static const int MagicSquare[16] = { 0, 6, 1, 7, 4, 2, 5, 3, 3, 5, 2, 4, 7, 1, 6, 0 };
static const int Bayer[16] = { 0, 4, 1, 5, 4, 0, 5, 1, 3, 7, 2, 6, 7, 3, 6, 2 };

static uint g_prim;     // the primitive's first word
static uint g_tmem;     // the primitive's TMEM snapshot, in words
static int4 g_slot[SLOT_COUNT]; // the combiner's inputs as the RDP sets them (before the A/B/D and C readings)
static uint g_noise;
static int g_tile0;     // the tile the LOD picked for the first texel

uint W(int field) { return Prims[g_prim + field]; }
int I(int field) { return asint(Prims[g_prim + field]); }

int SignExtend(int v, int bits) { return (v << (32 - bits)) >> (32 - bits); }
int Bit(uint word, int bit) { return (int)((word >> bit) & 1); }

uint NextRandom()
{
    g_noise = g_noise * 0x343FDu + 0x269EC3u;
    return (g_noise >> 16) & 0x7FFFu;
}

// ---- TMEM ----

int TmemByte(int index)
{
    index &= 0xFFF;
    uint word = Tmem[g_tmem + (uint)(index >> 2)];
    return (int)((word >> (24 - ((index & 3) << 3))) & 0xFF);
}

int TmemWord(int index)
{
    index &= 0xFFE;
    return (TmemByte(index) << 8) | TmemByte(index + 1);
}

// ---- Combiner inputs ----

int Ext9(int v) { return (v & 0x180) == 0x180 ? (v | ~0x1FF) : (v & 0x1FF); }
int Sign9(int v) { return (v & 0x1FF) | -(v & 0x100); }
int4 Ext4(int4 v) { return int4(Ext9(v.x), Ext9(v.y), Ext9(v.z), Ext9(v.w)); }
int4 Mul4(int4 v) { return int4(Sign9(v.x), Sign9(v.y), Sign9(v.z), Sign9(v.w)); }

int Clamp9(int v)
{
    v &= 0x1FF;
    return v < 0x100 ? v : (v < 0x180 ? 0xFF : 0);
}

int4 Rgba(uint c) { return int4((int)(c >> 24), (int)((c >> 16) & 0xFF), (int)((c >> 8) & 0xFF), (int)(c & 0xFF)); }
int4 Gray(int v) { return int4(v, v, v, v); }

int4 Combine(int a, int b, int m, int d)
{
    int4 r = (Ext4(g_slot[a]) - Ext4(g_slot[b])) * Mul4(g_slot[m]) + (Ext4(g_slot[d]) << 8) + int4(0x80, 0x80, 0x80, 0x80);
    return r & int4(0x1FFFF, 0x1FFFF, 0x1FFFF, 0x1FFFF);
}

// ---- Texture unit (N64Rdp.Texture.cs) ----

struct TileState
{
    int format, size, rowWords, tmem, palette;
    bool clampT, mirrorT, clampS, mirrorS;
    int maskT, shiftT, maskS, shiftS;
    int sl, tl, sh, th;
    bool clampEnabledS, clampEnabledT;
    int clampDiffS, clampDiffT;
};

TileState LoadTile(int index)
{
    TileState t;
    int at = F_TILES + (index & 7) * 4;
    uint lo = W(at), hi = W(at + 1), a = W(at + 2), b = W(at + 3);
    t.format = (int)((hi >> 21) & 7);
    t.size = (int)((hi >> 19) & 3);
    t.rowWords = (int)((hi >> 9) & 0x1FF);
    t.tmem = (int)(hi & 0x1FF);
    t.palette = (int)((lo >> 20) & 0xF);
    t.clampT = Bit(lo, 19) != 0;
    t.mirrorT = Bit(lo, 18) != 0;
    t.maskT = min((int)((lo >> 14) & 0xF), 10);
    t.shiftT = (int)((lo >> 10) & 0xF);
    t.clampS = Bit(lo, 9) != 0;
    t.mirrorS = Bit(lo, 8) != 0;
    t.maskS = min((int)((lo >> 4) & 0xF), 10);
    t.shiftS = (int)(lo & 0xF);
    t.sl = (int)(a & 0xFFFF);
    t.tl = (int)(a >> 16);
    t.sh = (int)(b & 0xFFFF);
    t.th = (int)(b >> 16);
    t.clampEnabledS = t.clampS || t.maskS == 0;
    t.clampEnabledT = t.clampT || t.maskT == 0;
    t.clampDiffS = ((t.sh >> 2) - (t.sl >> 2)) & 0x3FF;
    t.clampDiffT = ((t.th >> 2) - (t.tl >> 2)) & 0x3FF;
    return t;
}

// Set Other Modes and Set Convert, decoded once per primitive.
static int m_cycle;
static bool m_perspective, m_detail, m_sharpen, m_lod, m_tlut, m_tlutIa, m_bilinear, m_midTexel, m_biLerp0, m_biLerp1;
static bool m_convertOne, m_keyEnable, m_forceBlend, m_alphaCvgSelect, m_cvgTimesAlpha, m_colorOnCvg, m_imageRead;
static bool m_zUpdate, m_zCompare, m_antialias, m_zSourcePrim, m_ditherAlpha, m_alphaCompare;
static int m_rgbDither, m_alphaDither, m_zMode, m_cvgDest;
static int m_blendP[2], m_blendA[2], m_blendM[2], m_blendB[2];
static int m_k0, m_k1, m_k2, m_k3;
static int m_primTile, m_maxLevel, m_primMinLevel;

int SignExt16(int v) { return (v << 16) >> 16; }

void DivideOne(int coordinate, int w, bool carry, out int result)
{
    // (coordinate << 15) / w, floored; coordinate is 16-bit, so the product fits 32 bits.
    int n = coordinate << 15;
    int q = n / w;
    int remainder = n - q * w;
    if (remainder != 0 && ((remainder < 0) != (w < 0)))
    {
        q--;
    }

    int flags = 0;
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

    result = (q & 0x1FFFF) | flags;
}

void Divide(int s, int t, int w, out int ss, out int st)
{
    if (!m_perspective)
    {
        ss = SignExt16(s) & 0x1FFFF;
        st = SignExt16(t) & 0x1FFFF;
        return;
    }

    int w16 = SignExt16(w);
    bool carry = w16 <= 0;
    int divisor = carry ? 1 : w16;
    DivideOne(SignExt16(s), divisor, carry, ss);
    DivideOne(SignExt16(t), divisor, carry, st);
}

int ClampCoordinate(int c)
{
    if ((c & 0x40000) != 0) return 0x7FFF;
    if ((c & 0x20000) != 0) return 0x8000;
    int top = c & 0x18000;
    if (top == 0x8000) return 0x7FFF;
    if (top == 0x10000) return 0x8000;
    return c & 0xFFFF;
}

int LodDistance(int s0, int s1, int t0, int t1, int previous)
{
    int ds = SignExtend(s1, 17) - SignExtend(s0, 17);
    if ((ds & 0x20000) != 0) ds = ~ds & 0x1FFFF;
    int dt = SignExtend(t1, 17) - SignExtend(t0, 17);
    if ((dt & 0x20000) != 0) dt = ~dt & 0x1FFFF;
    int d = max(max(ds, dt), previous);
    int lod = d & 0x7FFF;
    if ((d & 0x1C000) != 0) lod |= 0x4000;
    return lod;
}

void LodSignals(bool clamped, int lod, out int level, out bool magnify, out bool distant, out int fraction)
{
    bool plain = !m_sharpen && !m_detail;
    if ((lod & 0x4000) != 0 || clamped)
    {
        magnify = false;
        level = 0;
        distant = true;
        fraction = 0xFF;
    }
    else if (lod < m_primMinLevel || lod < 32)
    {
        magnify = true;
        level = 0;
        distant = m_maxLevel == 0;
        if (plain)
        {
            fraction = distant ? 0xFF : 0;
        }
        else
        {
            fraction = (lod < m_primMinLevel ? m_primMinLevel : lod) << 3;
            if (m_sharpen) fraction |= 0x100;
        }
    }
    else
    {
        magnify = false;
        level = firstbithigh((uint)max(1, (lod >> 5) & 0xFF));
        distant = m_maxLevel == 0 || (lod & 0x6000) != 0 || level >= m_maxLevel;
        fraction = (plain && distant) ? 0xFF : (((lod << 3) >> level) & 0xFF);
    }
}

int LodTile(int level, bool magnify, bool distant)
{
    if (distant) level = m_maxLevel;
    return ((!m_detail || magnify) ? m_primTile + level : m_primTile + level + 1) & 7;
}

int Expand5(int v) { return (v << 3) | (v >> 2); }
int4 Rgba5551(int c) { return int4(Expand5((c >> 11) & 31), Expand5((c >> 6) & 31), Expand5((c >> 1) & 31), (c & 1) != 0 ? 0xFF : 0); }

int4 Palette(int index)
{
    int c = TmemWord(0x800 + ((index & 0xFF) << 3));
    return m_tlutIa ? int4(c >> 8, c >> 8, c >> 8, c & 0xFF) : Rgba5551(c);
}

int RowBase(TileState tile, int t) { return ((tile.rowWords * (t & 0xFF) + tile.tmem) << 3) & 0xFFF; }

int4 FetchTexelRow(TileState tile, int rowBase, int swap, int s)
{
    if (tile.size == SIZE4)
    {
        int b = TmemByte((rowBase + (s >> 1)) ^ swap);
        int n = (s & 1) != 0 ? (b & 0xF) : (b >> 4);
        if (m_tlut) return Palette((tile.palette << 4) | n);
        if (tile.format == FMT_IA)
        {
            int i = n & 0xE;
            i = (i << 4) | (i << 1) | (i >> 2);
            return int4(i, i, i, (n & 1) != 0 ? 0xFF : 0);
        }

        if (tile.format == FMT_CI) return Gray((tile.palette << 4) | n);
        if (tile.format == FMT_YUV)
        {
            int y = (n << 4) | n;
            return int4(y - 0x80, y - 0x80, y, y);
        }

        return Gray(n * 17);
    }

    if (tile.size == SIZE8)
    {
        int b = TmemByte(((rowBase + s) ^ swap) & (tile.format == FMT_YUV ? 0x7FF : 0xFFF));
        if (m_tlut) return Palette(b);
        if (tile.format == FMT_IA)
        {
            int i = (b & 0xF0) | (b >> 4);
            return int4(i, i, i, ((b & 0xF) << 4) | (b & 0xF));
        }

        if (tile.format == FMT_YUV) return int4(b - 0x80, b - 0x80, b, b);
        return Gray(b);
    }

    if (tile.size == SIZE16)
    {
        if (tile.format == FMT_YUV)
        {
            int y = TmemByte((((rowBase + s) ^ swap) & 0x7FF) | 0x800);
            int uv = ((rowBase + (s & ~1)) ^ swap) & 0x7FE;
            return int4(TmemByte(uv) - 0x80, TmemByte(uv + 1) - 0x80, y, y);
        }

        int c = TmemWord(((rowBase + s * 2) ^ swap) & 0xFFE);
        if (m_tlut) return Palette(c >> 8);
        if (tile.format == FMT_RGBA) return Rgba5551(c);
        if (tile.format == FMT_IA) return int4(c >> 8, c >> 8, c >> 8, c & 0xFF);
        return int4(c >> 8, c & 0xFF, c >> 8, c & 0xFF);
    }

    int at = ((rowBase + s * 2) ^ swap) & 0x7FE;
    if (m_tlut) return Palette(TmemByte(at));
    if (tile.format != FMT_RGBA)
    {
        int c = TmemWord(at);
        return int4(c >> 8, c & 0xFF, c >> 8, c & 0xFF);
    }

    return int4(TmemByte(at), TmemByte(at + 1), TmemByte(at | 0x800), TmemByte((at | 0x800) + 1));
}

int4 FetchTexel(TileState tile, int s, int t) { return FetchTexelRow(tile, RowBase(tile, t), (t & 1) << 2, s); }

int4 Signed9(int4 c) { return int4((c.x << 23) >> 23, (c.y << 23) >> 23, (c.z << 23) >> 23, c.w); }

int4 Lerp(int4 o, int4 sN, int4 tN, int fs, int ft) { return o + (((sN - o) * fs + (tN - o) * ft + int4(0x10, 0x10, 0x10, 0x10)) >> 5); }

int4 Center(int4 c0, int4 c1, int4 c2, int4 c3)
{
    return c3 + ((((c1 + c2) << 6) - (c3 << 7) + ((~c3 + c0) << 6) + int4(0xC0, 0xC0, 0xC0, 0xC0)) >> 8);
}

int4 Convert(int4 c)
{
    int u = c.x, v = c.y, y = c.z;
    return int4(y + ((m_k0 * v + 0x80) >> 8), y + ((m_k1 * u + m_k2 * v + 0x80) >> 8), y + ((m_k3 * u + 0x80) >> 8), y);
}

int ClampFrac(int c, bool clamp, bool isMax, int clampDiff, inout int fraction)
{
    if (!clamp) return c >> 5;
    if (isMax)
    {
        fraction = 0;
        return clampDiff;
    }

    if ((c & 0x10000) == 0) return c >> 5;
    fraction = 0;
    return 0;
}

int ClampLight(int c, bool clamp, bool isMax, int clampDiff)
{
    if (!clamp) return c >> 5;
    if (isMax) return clampDiff;
    return (c & 0x10000) == 0 ? (c >> 5) : 0;
}

int ShiftCoordinate(int c, int shift) { return shift < 11 ? (SignExt16(c) >> shift) : SignExt16(c << (16 - shift)); }
int MaskBits(int mask) { return mask == 0 ? 0x3FF : ((0xFFFF >> (16 - mask)) & 0x3FF); }

int MaskCoordinate(int c, int mask, bool mirror)
{
    if (mask == 0) return c;
    if (mirror && ((c >> min(mask, 10)) & 1) != 0) c = ~c;
    return c & MaskBits(mask);
}

int MaskCoupled(inout int c, int mask, bool mirror)
{
    if (mask == 0) return 1;
    int bits = MaskBits(mask);
    if (mirror)
    {
        int wrap = (c >> min(mask, 10)) & 1;
        c ^= -wrap;
        c &= bits;
        return ((c - wrap) & bits) == bits ? 0 : 1 - (wrap << 1);
    }

    c &= bits;
    return c == bits ? -c : 1;
}

int4 TextureCycle(int ss, int st, int tileIndex, int cycle, int4 previous)
{
    TileState tile = LoadTile(tileIndex);
    bool bilerp = cycle == 0 ? m_biLerp0 : m_biLerp1;
    bool convert = m_convertOne && cycle != 0;
    int s = ShiftCoordinate(ss, tile.shiftS);
    int t = ShiftCoordinate(st, tile.shiftT);
    bool maxS = (s >> 3) >= tile.sh;
    bool maxT = (t >> 3) >= tile.th;
    s -= tile.sl << 3;
    t -= tile.tl << 3;

    if (m_bilinear || m_tlut)
    {
        int sFrac = s & 0x1F, tFrac = t & 0x1F;
        int si = ClampFrac(s, tile.clampEnabledS, maxS, tile.clampDiffS, sFrac);
        int ti = ClampFrac(t, tile.clampEnabledT, maxT, tile.clampDiffT, tFrac);
        int s1 = MaskCoupled(si, tile.maskS, tile.mirrorS);
        int t1 = MaskCoupled(ti, tile.maskT, tile.mirrorT);
        bool upper = ((sFrac + tFrac) & 0x20) != 0;
        int4 result;
        if (convert)
        {
            result = Convert(Signed9(previous));
        }
        else if (!m_bilinear)
        {
            int4 c0 = FetchTexel(tile, si, ti);
            result = !bilerp ? Convert(c0) : ((m_midTexel && sFrac == 0x10 && tFrac == 0x10) ? Center(c0, c0, c0, c0) : c0);
        }
        else if (!bilerp)
        {
            result = Convert(upper ? FetchTexel(tile, si + s1, ti + t1) : FetchTexel(tile, si, ti));
        }
        else
        {
            int row0 = RowBase(tile, ti), swap0 = (ti & 1) << 2;
            int row1 = RowBase(tile, ti + t1), swap1 = ((ti + t1) & 1) << 2;
            if (m_midTexel && sFrac == 0x10 && tFrac == 0x10)
            {
                result = Center(FetchTexelRow(tile, row0, swap0, si), FetchTexelRow(tile, row0, swap0, si + s1),
                    FetchTexelRow(tile, row1, swap1, si), FetchTexelRow(tile, row1, swap1, si + s1));
            }
            else if (upper)
            {
                result = Lerp(FetchTexelRow(tile, row1, swap1, si + s1), FetchTexelRow(tile, row1, swap1, si),
                    FetchTexelRow(tile, row0, swap0, si + s1), 0x20 - sFrac, 0x20 - tFrac);
            }
            else
            {
                result = Lerp(FetchTexelRow(tile, row0, swap0, si), FetchTexelRow(tile, row0, swap0, si + s1),
                    FetchTexelRow(tile, row1, swap1, si), sFrac, tFrac);
            }
        }

        return result & int4(0x1FF, 0x1FF, 0x1FF, 0x1FF);
    }

    s = ClampLight(s, tile.clampEnabledS, maxS, tile.clampDiffS);
    t = ClampLight(t, tile.clampEnabledT, maxT, tile.clampDiffT);
    s = MaskCoordinate(s, tile.maskS, tile.mirrorS);
    t = MaskCoordinate(t, tile.maskT, tile.mirrorT);
    if (bilerp)
    {
        if (convert) return Gray(previous.z);
        return FetchTexel(tile, s, t) & int4(0x1FF, 0x1FF, -1, -1);
    }

    return Convert(convert ? Signed9(previous) : FetchTexel(tile, s, t)) & int4(0x1FF, 0x1FF, 0x1FF, 0x1FF);
}

// ---- The edge walker's scanline (N64Rdp.Raster.cs, WalkEdges) ----

struct Line
{
    bool valid;
    int lx, rx, majorX;
    int attr[8];
    int major[4], minor[4];
    bool invalid[4];
};

int ClipX(int x, int clipLeft, int clipRight, inout bool allOver, inout bool allUnder)
{
    int sticky = ((x >> 1) & 0x1FFF) != 0 ? 1 : 0;
    int clipped = ((x >> 13) & 0x1FFE) | sticky;
    bool under = (x & 0x8000000) != 0 || (clipped < clipLeft && (x & 0x4000000) == 0);
    clipped = under ? clipLeft : (((x >> 13) & 0x3FFE) | sticky);
    bool over = (clipped & 0x2000) != 0 || (clipped & 0x1FFF) >= clipRight;
    clipped = over ? clipRight : clipped;
    allOver = allOver && over;
    allUnder = allUnder && under;
    return clipped & 0x1FFF;
}

// The scissor and the rows the primitive draws (the walker's yLimitHigh, yLimitLow).
static int g_scissorXh, g_scissorYh, g_scissorXl, g_scissorYl;
static bool g_scissorField, g_scissorOdd;
static int g_yLimitHigh, g_yLimitLow, g_yFar, g_yFirst;

void SetupRows()
{
    uint lo = W(F_SCISSOR_LO), hi = W(F_SCISSOR_HI);
    g_scissorXh = (int)((hi >> 12) & 0xFFF);
    g_scissorYh = (int)(hi & 0xFFF);
    g_scissorField = Bit(lo, 25) != 0;
    g_scissorOdd = Bit(lo, 24) != 0;
    g_scissorXl = (int)((lo >> 12) & 0xFFF);
    g_scissorYl = (int)(lo & 0xFFF);
    int yl = I(F_YL), yh = I(F_YH);
    if ((yl & 0x2000) != 0) g_yLimitLow = yl;
    else if ((yl & 0x1000) != 0) g_yLimitLow = g_scissorYl;
    else g_yLimitLow = (yl & 0xFFF) < g_scissorYl ? yl : g_scissorYl;
    g_yFar = g_yLimitLow | 3;
    if ((yl >> 2) > (g_yFar >> 2)) g_yFar += 4;
    if ((yh & 0x2000) != 0) g_yLimitHigh = g_scissorYh;
    else if ((yh & 0x1000) != 0) g_yLimitHigh = yh;
    else g_yLimitHigh = yh >= g_scissorYh ? yh : g_scissorYh;
    g_yFirst = g_yLimitHigh & ~3;
}

void ComputeLine(int lineIndex, out Line L)
{
    L.valid = false;
    L.lx = 0;
    L.rx = 0;
    L.majorX = 0;
    [unroll] for (int i0 = 0; i0 < 8; i0++) L.attr[i0] = 0;
    [unroll] for (int q0 = 0; q0 < 4; q0++) { L.major[q0] = 0; L.minor[q0] = 0; L.invalid[q0] = true; }

    uint flags = W(F_FLAGS);
    bool flip = (flags & 1) != 0;
    int yh = I(F_YH), ym = I(F_YM), xl = I(F_XL), xh = I(F_XH), xm = I(F_XM);
    int dxldy = I(F_DXLDY), dxhdy = I(F_DXHDY), dxmdy = I(F_DXMDY);
    int k0 = yh & ~3;
    int kLine = lineIndex * 4;
    if (kLine < k0 || kLine + 3 > g_yFar || kLine < g_yFirst || lineIndex >= 1029)
    {
        return;
    }

    int clipLeft = g_scissorXh << 1, clipRight = g_scissorXl << 1;
    int majorStep = (dxhdy >> 2) & ~1, minorStep = (dxmdy >> 2) & ~1, lowStep = (dxldy >> 2) & ~1;
    int anchorQuarter = ((dxhdy < 0) != flip) ? 0 : 3;
    bool copy = m_cycle == CYCLE_COPY;
    int spanMin = flip ? 0 : 0xFFF, spanMax = flip ? 0xFFF : 0;
    bool allOver = true, allUnder = true, allInvalid = true;
    [unroll] for (int q = 0; q < 4; q++)
    {
        int k = kLine + q;
        int major = (xh & ~1) + majorStep * (k - k0);
        int minor = (ym >= k0 && k >= ym) ? (xl & ~1) + lowStep * (k - ym) : (xm & ~1) + minorStep * (k - k0);
        bool invalid = k < g_yLimitHigh || k >= g_yLimitLow;
        int majorX = ClipX(major, clipLeft, clipRight, allOver, allUnder);
        int minorX = ClipX(minor, clipLeft, clipRight, allOver, allUnder);
        L.major[q] = majorX;
        L.minor[q] = minorX;
        int majorKey = (major ^ (1 << 27)) & (0x3FFF << 14);
        int minorKey = (minor ^ (1 << 27)) & (0x3FFF << 14);
        invalid = invalid || (flip ? minorKey < majorKey : majorKey < minorKey);
        L.invalid[q] = invalid;
        allInvalid = allInvalid && invalid;
        if (!invalid)
        {
            if (flip)
            {
                spanMin = max(spanMin, (minorX >> 3) & 0xFFF);
                spanMax = min(spanMax, (majorX >> 3) & 0xFFF);
            }
            else
            {
                spanMin = min(spanMin, (minorX >> 3) & 0xFFF);
                spanMax = max(spanMax, (majorX >> 3) & 0xFFF);
            }
        }

        if (q == anchorQuarter)
        {
            L.majorX = SignExtend(major >> 16, 12);
            int xFraction = (major >> 8) & 0xFF;
            int lines = lineIndex - (k0 >> 2);
            [unroll] for (int i = 0; i < 8; i++)
            {
                int de = I(F_DE + i), dy = I(F_DY + i), dx = I(F_DX + i);
                int offset = 0;
                if (anchorQuarter == 3)
                {
                    int e = de & ~0x1FF;
                    int d = dy & ~0x1FF;
                    offset = e - (e >> 2) - d + (d >> 2);
                }

                int perPixel = copy ? 0 : ((dx >> 8) & ~1);
                int attr = I(F_VALUE + i) + de * lines;
                L.attr[i] = ((attr & ~0x1FF) + offset - xFraction * perPixel) & ~0x3FF;
            }
        }
    }

    L.lx = spanMin;
    L.rx = spanMax;
    L.valid = !allInvalid && !allOver && !allUnder && (!g_scissorField || (g_scissorOdd ? 1 : 0) == (lineIndex & 1));
}

int CoverageMask(Line L, bool flip, int x)
{
    int m = 0xFF;
    [unroll] for (int q = 0; q < 4; q++)
    {
        int samples = (q & 1) == 0 ? 0xA : 0x5;
        int shift = q < 2 ? 4 : 0;
        int shifted = samples << shift;
        if (L.invalid[q])
        {
            m &= ~shifted;
            continue;
        }

        int left = flip ? L.major[q] : L.minor[q];
        int right = flip ? L.minor[q] : L.major[q];
        int leftPixel = left >> 3, rightPixel = right >> 3;
        if (x <= leftPixel) m &= ~shifted;
        if (x >= rightPixel) m &= ~shifted;
        int covered = ((left & 7) + 1) >> 1;
        int leftSamples = (0xF >> covered) & samples;
        int rightSamples = (0xF0 >> (((right & 7) + 1) >> 1)) & samples;
        if (rightPixel > leftPixel)
        {
            if (x == leftPixel) m |= leftSamples << shift;
            if (x == rightPixel) m |= rightSamples << shift;
        }
        else if (rightPixel == leftPixel && x == leftPixel)
        {
            m |= (leftSamples & rightSamples) << shift;
        }
    }

    return m & 0xFF;
}

// At an internal resolution above 1×: whether a scaled pixel's own top-left point lies inside the primitive (its
// quarter scanline's span, in eighths of an RDP pixel).
bool SubPixelCovered(Line L, bool flip, int scale, int fx, int fy, int x)
{
    int q = (fy * 4) / scale;
    if (L.invalid[q]) return false;
    int p = x * 8 + (fx * 8) / scale;
    int left = flip ? L.major[q] : L.minor[q];
    int right = flip ? L.minor[q] : L.major[q];
    return p >= left && p < right;
}

void CoverageOffset(int mask, out int offX, out int offY)
{
    int rows[4];
    rows[0] = (mask >> 4) & 0xA;
    rows[1] = (mask >> 4) & 0x5;
    rows[2] = mask & 0xA;
    rows[3] = mask & 0x5;
    offY = 0;
    while (offY < 3 && rows[offY] == 0) offY++;
    if (rows[offY] == 0) offY = 0;
    int row = rows[offY];
    offX = (row & 8) != 0 ? 0 : ((row & 4) != 0 ? 1 : ((row & 2) != 0 ? 2 : ((row & 1) != 0 ? 3 : 0)));
}

// ---- Z (N64Rdp.Pixel.cs) ----

int CompressDz(int value)
{
    int j = 0;
    if ((value & 0xFF00) != 0) j |= 8;
    if ((value & 0xF0F0) != 0) j |= 4;
    if ((value & 0xCCCC) != 0) j |= 2;
    if ((value & 0xAAAA) != 0) j |= 1;
    return j;
}

int NormalizeDzPix(int sum)
{
    if ((sum & 0xC000) != 0) return 0x8000;
    if ((sum & 0xFFFF) == 0) return 1;
    if (sum == 1) return 3;
    return 1 << (firstbithigh((uint)(sum & 0x3FFF)) + 1);
}

int CompressZ(int z)
{
    z &= 0x3FFFF;
    int exponent = 0;
    while (exponent < 7 && (z & (0x20000 >> exponent)) != 0) exponent++;
    return (exponent << 11) | ((z >> max(0, 6 - exponent)) & 0x7FF);
}

int DecompressZ(int compressed)
{
    int exponent = (compressed >> 11) & 7;
    int mantissa = compressed & 0x7FF;
    int ones = 0x3FFFF & ~(0x3FFFF >> exponent);
    return (ones | (mantissa << max(0, 6 - exponent))) & 0x3FFFF;
}

static int g_blendShiftA, g_blendShiftB;

bool ZCompare(int stored, int sz, int dzPix, int dzPixEncoded, inout int coverage, int memCvg, out bool blendEnable, out bool prewrap)
{
    bool overflow = ((memCvg + coverage) & 8) != 0;
    prewrap = overflow;
    int oldZ = DecompressZ((stored >> 2) & 0x3FFF);
    int rawDzMem = (stored & 3) << 2;
    int dzMem = 1 << rawDzMem;
    g_blendShiftA = clamp(dzPixEncoded - rawDzMem, 0, 4);
    g_blendShiftB = clamp(rawDzMem - dzPixEncoded, 0, 4);
    bool forceCoplanar = false;
    int precision = (stored >> 13) & 0xF;
    if (precision < 3)
    {
        if (dzMem != 0x8000)
        {
            int floorValue = 16 >> precision;
            dzMem <<= 1;
            if (dzMem < floorValue) dzMem = floorValue;
        }
        else
        {
            forceCoplanar = true;
            dzMem = 0xFFFF;
        }
    }

    int combined = (dzPix | dzMem) & 0xFFFF;
    int dzNew = combined == 0 ? 0 : (1 << firstbithigh((uint)combined));
    int dzNotShifted = dzNew;
    dzNew <<= 3;
    bool farther = forceCoplanar || sz + dzNew >= oldZ;
    blendEnable = m_forceBlend || (!overflow && m_antialias && farther);
    bool isMax = oldZ == 0x3FFFF;
    bool nearer = forceCoplanar || sz - dzNew <= oldZ;
    bool inFront = sz < oldZ;
    if (m_zMode == 0) return isMax || (overflow ? inFront : nearer);
    if (m_zMode == 1)
    {
        if (!inFront || !farther || !overflow) return isMax || (overflow ? inFront : nearer);
        int dzEncoded = CompressDz(dzNotShifted & 0xFFFF);
        int weight = ((oldZ >> dzEncoded) - (sz >> dzEncoded)) & 0xF;
        coverage = ((weight * coverage) >> 3) & 0xF;
        return true;
    }

    if (m_zMode == 2) return inFront || isMax;
    return farther && nearer && !isMax;
}

// ---- Blender ----

int3 BlendColor(int select, int cycle, int3 pixel, int3 mem, int3 first)
{
    if (select == 0) return cycle == 0 ? pixel : first;
    if (select == 1) return mem;
    if (select == 2) return Rgba(W(F_BLEND)).xyz;
    return Rgba(W(F_FOG)).xyz;
}

int3 BlendCycle(int cycle, int3 pixel, int alpha, int shadeA, int3 mem, int memA, int3 first, bool firstOfTwo)
{
    int3 p = BlendColor(m_blendP[cycle], cycle, pixel, mem, first);
    int3 m = BlendColor(m_blendM[cycle], cycle, pixel, mem, first);
    int aInput = m_blendA[cycle] == 0 ? alpha : (m_blendA[cycle] == 1 ? (int)(W(F_FOG) & 0xFF) : (m_blendA[cycle] == 2 ? shadeA : 0));
    int factorA = aInput >> 3;
    int factorB = m_blendB[cycle] == 0 ? ((~aInput & 0xFF) >> 3) : (m_blendB[cycle] == 1 ? (memA >> 3) : (m_blendB[cycle] == 2 ? (0xFF >> 3) : 0));
    if (m_blendB[cycle] == 1)
    {
        factorA = (factorA >> g_blendShiftA) & 0x3C;
        factorB = (factorB >> g_blendShiftB) | 3;
    }

    int mulB = factorB + 1;
    int3 blend = p * factorA + m * mulB;
    if (m_forceBlend || firstOfTwo) return (blend >> 5) & int3(0xFF, 0xFF, 0xFF);
    int sum = (factorA & ~3) + (factorB & ~3) + 4;
    return min(int3(0xFF, 0xFF, 0xFF), blend / sum);
}

int3 BlendOutput(int cycle, int3 pixel, int alpha, int shadeA, int3 mem, int memA, int3 first, bool blendEnable, bool prewrap)
{
    if (m_colorOnCvg && !prewrap) return BlendColor(m_blendM[cycle], cycle, pixel, mem, first);
    bool partialReject = m_blendA[cycle] == 0 && m_blendB[cycle] == 0 && alpha >= 0xFF;
    if (!blendEnable || partialReject) return BlendColor(m_blendP[cycle], cycle, pixel, mem, first);
    return BlendCycle(cycle, pixel, alpha, shadeA, mem, memA, first, false);
}

int FinalAlpha(int combinedAlpha, int alphaDither, int coverage)
{
    int alpha = Clamp9(combinedAlpha);
    if (alpha == 0xFF) alpha = 0x100;
    if (!m_alphaCvgSelect)
    {
        alpha += alphaDither;
        return (alpha & 0x100) != 0 ? 0xFF : alpha;
    }

    return min(0xFF, m_cvgTimesAlpha ? ((alpha * coverage + 4) >> 3) : (coverage << 5));
}

int KeyChannel(int full, int width)
{
    int v = SignExtend(full, 17);
    if (v > 0) v = (v & 0xF) == 8 ? -v + 0x10 : -v;
    return (width << 4) + v;
}

int KeyAlpha(int3 full)
{
    uint gb = W(F_KEYGB_HI), gbLo = W(F_KEYGB_LO), r = W(F_KEYR_LO);
    int widthG = (int)((gb >> 12) & 0xFFF), widthB = (int)(gb & 0xFFF), widthR = (int)((r >> 16) & 0xFFF);
    int key = min(KeyChannel(full.x, widthR), min(KeyChannel(full.y, widthG), KeyChannel(full.z, widthB)));
    return clamp(key, 0, 0xFF);
}

bool AlphaCompare(int alpha)
{
    int threshold = m_ditherAlpha ? (int)(NextRandom() & 0xFF) : (int)(W(F_BLEND) & 0xFF);
    return alpha >= threshold;
}

int Dither(int c, int compare) { return compare >= (c & 7) ? c : (c > 247 ? 255 : (c & 0xF8) + 8); }

// ---- Framebuffer values ----

void ReadFramebuffer(uint old, int size, int format, out int3 mem, out int coverage)
{
    mem = int3(0, 0, 0);
    coverage = 7;
    if (!m_imageRead) return;
    if (size == SIZE16)
    {
        int v = (int)old;
        if (format == FMT_RGBA)
        {
            mem = int3(Expand5((v >> 11) & 31) & 0xF8, Expand5((v >> 6) & 31) & 0xF8, Expand5((v >> 1) & 31) & 0xF8);
            coverage = (v & 1) != 0 ? 7 : 0;
        }
        else
        {
            mem = int3(v >> 8, v >> 8, v >> 8);
            coverage = (v >> 5) & 7;
        }
    }
    else if (size == SIZE32)
    {
        mem = int3((int)(old >> 24), (int)((old >> 16) & 0xFF), (int)((old >> 8) & 0xFF));
        coverage = (int)((old >> 5) & 7);
    }
    else if (size == SIZE8)
    {
        mem = int3((int)old, (int)old, (int)old);
    }
}

uint FramebufferValue(int size, int format, uint address, int3 c, bool blendEnable, int coverage, int memCvg, uint old)
{
    int clamped = blendEnable ? coverage + memCvg : coverage - 1;
    int finalCvg = m_cvgDest == 0 ? ((clamped & 8) != 0 ? 7 : clamped & 7) : (m_cvgDest == 1 ? ((coverage + memCvg) & 7) : (m_cvgDest == 2 ? 7 : memCvg));
    if (size == SIZE16)
    {
        if (format == FMT_RGBA) return (uint)((((c.x & ~7) << 8) | ((c.y & ~7) << 3) | ((c.z & ~7) >> 2) | (finalCvg >> 2)) & 0xFFFF);
        return (uint)(((c.x << 8) | (finalCvg << 5)) & 0xFFFF);
    }

    if (size == SIZE32) return (uint)(c.x << 24 | c.y << 16 | c.z << 8 | finalCvg << 5);
    if (size == SIZE8) return (uint)(((address & 1) != 0 ? c.y : c.x) & 0xFF);
    return old;
}

bool Uses(int cycle, int slot, int alphaSlot, int ccA, int ccB, int ccM, int ccD, int acA, int acB, int acM, int acD)
{
    return ccA == slot || ccB == slot || ccM == slot || ccD == slot || ccM == alphaSlot || acA == slot || acB == slot || acM == slot || acD == slot;
}

// ---- One pixel of one primitive ----

void Pixel(int px, int scan, int fx, int fy, int scale, inout uint color, inout uint depth, uint pixelIndex)
{
    uint modesLo = W(F_MODES_LO), modesHi = W(F_MODES_HI);
    uint flags = W(F_FLAGS);
    bool flip = (flags & 1) != 0;
    m_maxLevel = (int)((flags >> 1) & 7);
    m_primTile = (int)((flags >> 4) & 7);
    m_cycle = (int)((modesHi >> 20) & 3);
    m_perspective = Bit(modesHi, 19) != 0;
    m_detail = Bit(modesHi, 18) != 0;
    m_sharpen = Bit(modesHi, 17) != 0;
    m_lod = Bit(modesHi, 16) != 0;
    m_tlut = Bit(modesHi, 15) != 0;
    m_tlutIa = Bit(modesHi, 14) != 0;
    m_bilinear = Bit(modesHi, 13) != 0;
    m_midTexel = Bit(modesHi, 12) != 0;
    m_biLerp0 = Bit(modesHi, 11) != 0;
    m_biLerp1 = Bit(modesHi, 10) != 0;
    m_convertOne = Bit(modesHi, 9) != 0;
    m_keyEnable = Bit(modesHi, 8) != 0;
    m_rgbDither = (int)((modesHi >> 6) & 3);
    m_alphaDither = (int)((modesHi >> 4) & 3);
    m_blendP[0] = (int)((modesLo >> 30) & 3);
    m_blendP[1] = (int)((modesLo >> 28) & 3);
    m_blendA[0] = (int)((modesLo >> 26) & 3);
    m_blendA[1] = (int)((modesLo >> 24) & 3);
    m_blendM[0] = (int)((modesLo >> 22) & 3);
    m_blendM[1] = (int)((modesLo >> 20) & 3);
    m_blendB[0] = (int)((modesLo >> 18) & 3);
    m_blendB[1] = (int)((modesLo >> 16) & 3);
    m_forceBlend = Bit(modesLo, 14) != 0;
    m_alphaCvgSelect = Bit(modesLo, 13) != 0;
    m_cvgTimesAlpha = Bit(modesLo, 12) != 0;
    m_zMode = (int)((modesLo >> 10) & 3);
    m_cvgDest = (int)((modesLo >> 8) & 3);
    m_colorOnCvg = Bit(modesLo, 7) != 0;
    m_imageRead = Bit(modesLo, 6) != 0;
    m_zUpdate = Bit(modesLo, 5) != 0;
    m_zCompare = Bit(modesLo, 4) != 0;
    m_antialias = Bit(modesLo, 3) != 0;
    m_zSourcePrim = Bit(modesLo, 2) != 0;
    m_ditherAlpha = Bit(modesLo, 1) != 0;
    m_alphaCompare = Bit(modesLo, 0) != 0;
    m_primMinLevel = I(F_PRIM_MIN_LEVEL);

    int size = I(F_COLOR_SIZE), format = I(F_COLOR_FORMAT);
    uint colorAddress = W(F_COLOR_ADDRESS);
    int width = I(F_COLOR_WIDTH);

    Line L;
    ComputeLine(scan, L);
    if (!L.valid) return;

    int spanStart = L.lx, spanEnd = L.rx;
    int length = flip ? spanStart - spanEnd : spanEnd - spanStart;
    int j = flip ? px - spanEnd : spanEnd - px;
    if (j < 0 || j > length) return;

    uint nativeIndex = (uint)(scan * width + px);

    // Fill mode: every pixel of the span takes the fill colour.
    if (m_cycle == CYCLE_FILL)
    {
        uint fill = W(F_FILL);
        if (size == SIZE16) color = (px & 1) != 0 ? (fill & 0xFFFF) : (fill >> 16);
        else if (size == SIZE32) color = fill;
        else if (size == SIZE8) color = (fill >> (((int)((colorAddress + nativeIndex) & 3) ^ 3) << 3)) & 0xFF;
        return;
    }

    int mask = CoverageMask(L, flip, px);
    if (scale > 1 && m_cycle != CYCLE_COPY && !SubPixelCovered(L, flip, scale, fx, fy, px)) return;

    int sign = flip ? 1 : -1;
    int step[8];
    [unroll] for (int i = 0; i < 7; i++) step[i] = I(F_DX + i) & ~0x1F;
    step[7] = I(F_DX + 7);
    int clipped = flip ? spanEnd - L.majorX : L.majorX - spanEnd;
    if (clipped != 0) clipped &= 0xFFF;
    int pos = clipped + j;
    int a[8];
    [unroll] for (int k = 0; k < 8; k++)
    {
        a[k] = L.attr[k] + step[k] * sign * pos;
        if (scale > 1)
        {
            // Sub-pixel position: along X at the primitive's per-pixel rate, down at its per-scanline rate.
            int dy = I(F_DY + k);
            a[k] += (step[k] / scale) * fx + ((step[k] % scale) * fx) / scale;
            a[k] += (dy / scale) * fy + ((dy % scale) * fy) / scale;
        }
    }

    // Copy mode: texels go to the framebuffer unchanged, a group of four (eight for 8-bit images) per S step.
    if (m_cycle == CYCLE_COPY)
    {
        if (size == SIZE32) return;
        int group = size == SIZE8 ? 8 : 4;
        int groupStart = (j / group) * group;
        // S and T start on the scanline's anchor (copy mode has no per-pixel attributes) and step once per group.
        int s0 = L.attr[4] + (flip ? step[4] : -step[4]) * (j / group);
        int t0 = L.attr[5] + (flip ? step[5] : -step[5]) * (j / group);
        int css, cst;
        m_perspective = false;
        Divide(s0 >> 16, t0 >> 16, 0, css, cst);
        css = ClampCoordinate(css);
        cst = ClampCoordinate(cst);
        TileState tile = LoadTile(m_primTile);
        int cs = (ShiftCoordinate(css, tile.shiftS) - (tile.sl << 3)) >> 5;
        int ct = (ShiftCoordinate(cst, tile.shiftT) - (tile.tl << 3)) >> 5;
        cs = MaskCoordinate(cs + (j - groupStart), tile.maskS, tile.mirrorS);
        ct = MaskCoordinate(ct, tile.maskT, tile.mirrorT);
        int rowBase = ((tile.rowWords * (ct & 0xFF) + tile.tmem) << 3) & 0xFFF;
        int swap = (ct & 1) != 0 ? 4 : 0;
        int raw;
        if (tile.size == SIZE4)
        {
            int b = TmemByte(((rowBase + (cs >> 1)) ^ swap) & 0xFFF);
            raw = (cs & 1) != 0 ? (b & 0xF) : (b >> 4);
            raw = (tile.format == FMT_CI || m_tlut) ? ((tile.palette << 4) | raw) : ((raw << 4) | raw);
        }
        else if (tile.size == SIZE8)
        {
            raw = TmemByte(((rowBase + cs) ^ swap) & 0xFFF);
        }
        else
        {
            int at = ((rowBase + cs * 2) ^ swap) & 0xFFE;
            raw = (TmemByte(at) << 8) | TmemByte(at + 1);
        }

        if (size == SIZE8)
        {
            if (m_alphaCompare && raw < (int)(W(F_BLEND) & 0xFF)) return;
            color = (uint)(raw & 0xFF);
            return;
        }

        int c;
        if (m_tlut)
        {
            int at = 0x800 + ((raw & 0xFF) << 3);
            c = (TmemByte(at) << 8) | TmemByte(at + 1);
        }
        else if (tile.size == SIZE16) c = raw;
        else c = (raw << 8) | raw;
        if (m_alphaCompare && (c & 1) == 0) return;
        color = (uint)(c & 0xFFFF);
        return;
    }

    // One- and two-cycle modes (N64Rdp.Span.cs).
    bool twoCycle = m_cycle == CYCLE_TWO;
    uint combLo = W(F_COMBINE_LO), combHi = W(F_COMBINE_HI);
    int ccA0 = ColorSubA[(combHi >> 20) & 0xF], ccM0 = ColorMulTable[(combHi >> 15) & 0x1F], acA0 = AlphaSubAddB[(combHi >> 12) & 7], acM0 = AlphaMul[(combHi >> 9) & 7];
    int ccA1 = ColorSubA[(combHi >> 5) & 0xF], ccM1 = ColorMulTable[combHi & 0x1F];
    int ccB0 = ColorSubB[(combLo >> 28) & 0xF], ccB1 = ColorSubB[(combLo >> 24) & 0xF];
    int acA1 = AlphaSubAddB[(combLo >> 21) & 7], acM1 = AlphaMul[(combLo >> 18) & 7];
    int ccD0 = ColorAdd[(combLo >> 15) & 7], acB0 = AlphaSubAddB[(combLo >> 12) & 7], acD0 = AlphaSubAddB[(combLo >> 9) & 7];
    int ccD1 = ColorAdd[(combLo >> 6) & 7], acB1 = AlphaSubAddB[(combLo >> 3) & 7], acD1 = AlphaSubAddB[combLo & 7];

    // Constant inputs.
    uint convLo = W(F_CONVERT_LO), convHi = W(F_CONVERT_HI);
    m_k0 = (SignExtend((int)((convHi >> 13) & 0x1FF), 9) << 1) + 1;
    m_k1 = (SignExtend((int)((convHi >> 4) & 0x1FF), 9) << 1) + 1;
    m_k2 = (SignExtend((int)(((convHi & 0xF) << 5) | (convLo >> 27)), 9) << 1) + 1;
    m_k3 = (SignExtend((int)((convLo >> 18) & 0x1FF), 9) << 1) + 1;
    int k4 = (int)((convLo >> 9) & 0x1FF), k5 = (int)(convLo & 0x1FF);
    uint gbLo = W(F_KEYGB_LO), rLo = W(F_KEYR_LO);
    uint prim = W(F_PRIM), env = W(F_ENV);
    g_slot[S_COMBINED] = int4(0, 0, 0, 0);
    g_slot[S_TEX0] = int4(0, 0, 0, 0);
    g_slot[S_TEX1] = int4(0, 0, 0, 0);
    g_slot[S_PRIM] = Rgba(prim);
    g_slot[S_ENV] = Rgba(env);
    g_slot[S_ONE] = Gray(0x100);
    g_slot[S_ZERO] = Gray(0);
    g_slot[S_KEY_CENTER] = int4((int)((rLo >> 8) & 0xFF), (int)((gbLo >> 24) & 0xFF), (int)((gbLo >> 8) & 0xFF), 0);
    g_slot[S_KEY_SCALE] = int4((int)(rLo & 0xFF), (int)((gbLo >> 16) & 0xFF), (int)(gbLo & 0xFF), 0);
    g_slot[S_K4] = Gray(k4);
    g_slot[S_K5] = Gray(k5);
    g_slot[S_COMBINED_ALPHA] = Gray(0);
    g_slot[S_TEX0_ALPHA] = Gray(0);
    g_slot[S_TEX1_ALPHA] = Gray(0);
    g_slot[S_PRIM_ALPHA] = Gray((int)(prim & 0xFF));
    g_slot[S_ENV_ALPHA] = Gray((int)(env & 0xFF));
    g_slot[S_LOD_FRACTION] = Gray(0);
    g_slot[S_PRIM_LOD] = Gray(I(F_PRIM_LOD));
    g_slot[S_SHADE] = int4(0, 0, 0, 0);
    g_slot[S_SHADE_ALPHA] = Gray(0);
    g_slot[S_NOISE] = Gray(0);

    int coverage = countbits((uint)mask);
    int centerBit = (mask >> 7) & 1;
    int offX = 0, offY = 0;
    int pr = a[0], pg = a[1], pb = a[2], pa = a[3];
    if (coverage == 8)
    {
        pr >>= 16;
        pg >>= 16;
        pb >>= 16;
        pa >>= 16;
    }
    else
    {
        CoverageOffset(mask, offX, offY);
        int correctR = SignExtend(step[0] >> 14, 13), correctG = SignExtend(step[1] >> 14, 13);
        int correctB = SignExtend(step[2] >> 14, 13), correctA = SignExtend(step[3] >> 14, 13);
        int dyR = SignExtend(I(F_DY + 0) >> 14, 13), dyG = SignExtend(I(F_DY + 1) >> 14, 13);
        int dyB = SignExtend(I(F_DY + 2) >> 14, 13), dyA = SignExtend(I(F_DY + 3) >> 14, 13);
        pr = (((pr >> 14) << 2) + offX * correctR + offY * dyR) >> 4;
        pg = (((pg >> 14) << 2) + offX * correctG + offY * dyG) >> 4;
        pb = (((pb >> 14) << 2) + offX * correctB + offY * dyB) >> 4;
        pa = (((pa >> 14) << 2) + offX * correctA + offY * dyA) >> 4;
    }

    int shadeA = Clamp9(pa);
    g_slot[S_SHADE] = int4(Clamp9(pr), Clamp9(pg), Clamp9(pb), shadeA);
    g_slot[S_SHADE_ALPHA] = Gray(shadeA);

    // Depth.
    int dzPix;
    {
        int dzdyInt = (I(F_DY + 7) >> 16) & 0xFFFF;
        int dzdxInt = (I(F_DX + 7) >> 16) & 0xFFFF;
        int dzSum = ((dzdyInt & 0x8000) != 0 ? (~dzdyInt & 0x7FFF) : dzdyInt) + ((dzdxInt & 0x8000) != 0 ? (~dzdxInt & 0x7FFF) : dzdxInt);
        dzPix = NormalizeDzPix(dzSum & 0xFFFF) & 0xFFFF;
    }

    if (m_zSourcePrim) dzPix = I(F_PRIM_DZ);
    int dzPixEncoded = CompressDz(dzPix & 0xFFFF);
    int pz = m_zSourcePrim ? (I(F_PRIM_Z) << 16) : a[7];
    int sz = (pz >> 10) & 0x3FFFFF;
    if (coverage == 8)
    {
        sz = sz >> 3;
    }
    else
    {
        int correctZ = SignExtend(step[7] >> 10, 22), dyZ = SignExtend(I(F_DY + 7) >> 10, 22);
        sz = ((sz << 2) + offX * correctZ + offY * dyZ) >> 5;
    }

    int zTop = (sz & 0x60000) >> 17;
    sz = zTop == 2 ? 0x3FFFF : (zTop == 3 ? 0 : (sz & 0x3FFFF));

    // Texturing (N64Rdp.Span.cs, TexturePixel).
    {
        int s = a[4], t = a[5], w = a[6];
        int dsInc = step[4] * sign, dtInc = step[5] * sign, dwInc = step[6] * sign;
        int ss, st;
        Divide(s >> 16, t >> 16, w >> 16, ss, st);
        int tile0 = m_primTile, tile1 = (m_primTile + 1) & 7, lodFraction = 0;
        {
            int nextS, nextT, farS, farT;
            Divide((s + dsInc) >> 16, (t + dtInc) >> 16, (w + dwInc) >> 16, nextS, nextT);
            if (twoCycle)
            {
                Divide((s + (I(F_DY + 4) & ~0x7FFF)) >> 16, (t + (I(F_DY + 5) & ~0x7FFF)) >> 16, (w + (I(F_DY + 6) & ~0x7FFF)) >> 16, farS, farT);
            }
            else
            {
                Divide((s + (dsInc << 1)) >> 16, (t + (dtInc << 1)) >> 16, (w + (dwInc << 1)) >> 16, farS, farT);
            }

            bool clampedLod = ((ss | st | nextS | nextT | farS | farT) & 0x60000) != 0;
            int lod = 0;
            if (!clampedLod)
            {
                lod = twoCycle ? LodDistance(ss, farS, st, farT, LodDistance(ss, nextS, st, nextT, 0)) : LodDistance(nextS, farS, nextT, farT, 0);
            }

            int level;
            bool magnify, distant;
            LodSignals(clampedLod, lod, level, magnify, distant, lodFraction);
            if (m_lod)
            {
                tile0 = LodTile(level, magnify, distant);
                if (!m_detail) tile1 = (distant || (!m_sharpen && magnify)) ? tile0 : ((tile0 + 1) & 7);
                else tile1 = (m_primTile + (distant ? m_maxLevel : level) + ((!distant && !magnify) ? 2 : 1)) & 7;
            }
        }

        if (m_perspective)
        {
            ss = ClampCoordinate(ss);
            st = ClampCoordinate(st);
        }
        else
        {
            ss &= 0xFFFF;
            st &= 0xFFFF;
        }

        g_tile0 = tile0;
        g_slot[S_LOD_FRACTION] = Gray(lodFraction);
        int4 texel0 = TextureCycle(ss, st, tile0, 0, int4(0, 0, 0, 0));
        g_slot[S_TEX0] = texel0;
        g_slot[S_TEX0_ALPHA] = Gray(texel0.w);
        int4 texel1 = int4(0, 0, 0, 0);
        if (twoCycle)
        {
            texel1 = TextureCycle(ss, st, tile1, 1, texel0);
            g_slot[S_TEX1] = texel1;
            g_slot[S_TEX1_ALPHA] = Gray(texel1.w);
        }

        if (!twoCycle)
        {
            // One-cycle mode's TEXEL1 is the next pixel's texel.
            int ns, nt;
            Divide((s + dsInc) >> 16, (t + dtInc) >> 16, (w + dwInc) >> 16, ns, nt);
            int4 next = TextureCycle(ClampCoordinate(ns), ClampCoordinate(nt), tile0, 0, texel0);
            g_slot[S_TEX1] = next;
            g_slot[S_TEX1_ALPHA] = Gray(next.w);
        }
    }

    // Noise and dither.
    int noise = (int)((NextRandom() & 7) << 6) | 0x20;
    g_slot[S_NOISE] = Gray(noise);
    int colorDither = 7, alphaDither = 0;
    bool scissorField = g_scissorField;
    int ditherRow = (((scan >> (scissorField ? 1 : 0)) & 3) << 2);
    int pattern = (m_rgbDither == 0 || m_rgbDither == 2) ? MagicSquare[ditherRow | (px & 3)] : Bayer[ditherRow | (px & 3)];
    if (m_rgbDither != 3 || m_alphaDither != 3)
    {
        colorDither = m_rgbDither == 2 ? (int)NextRandom() : (m_rgbDither == 3 ? 7 : pattern);
        alphaDither = m_alphaDither == 0 ? pattern : (m_alphaDither == 1 ? (~pattern & 7) : (m_alphaDither == 2 ? ((noise >> 6) & 7) : 0));
    }

    // The combiner.
    int3 pixel;
    int pixelA, alphaCompareValue;
    if (twoCycle)
    {
        int4 first = Combine(ccA0, ccB0, ccM0, ccD0);
        int firstAlpha = (Combine(acA0, acB0, acM0, acD0).w >> 8) & 0x1FF;
        alphaCompareValue = FinalAlpha(firstAlpha, alphaDither, coverage);
        g_slot[S_COMBINED] = int4((int)((uint)first.x >> 8), (int)((uint)first.y >> 8), (int)((uint)first.z >> 8), firstAlpha);
        g_slot[S_COMBINED_ALPHA] = Gray(firstAlpha);

        // The second cycle sees the second texel as TEXEL0 and the next pixel's first texel as TEXEL1, when the
        // combiner reads them (otherwise TEXEL0 stays the first texel).
        bool needTex1 = Uses(0, S_TEX1, S_TEX1_ALPHA, ccA0, ccB0, ccM0, ccD0, acA0, acB0, acM0, acD0)
            || Uses(1, S_TEX0, S_TEX0_ALPHA, ccA1, ccB1, ccM1, ccD1, acA1, acB1, acM1, acD1);
        bool needNextTex1 = Uses(1, S_TEX1, S_TEX1_ALPHA, ccA1, ccB1, ccM1, ccD1, acA1, acB1, acM1, acD1);
        if (needTex1 || needNextTex1)
        {
            int4 tex1 = g_slot[S_TEX1];
            g_slot[S_TEX0] = tex1;
            g_slot[S_TEX0_ALPHA] = Gray(tex1.w);
            if (needNextTex1)
            {
                int s = a[4], t = a[5], w = a[6];
                int ns, nt;
                Divide((s + step[4] * sign) >> 16, (t + step[5] * sign) >> 16, (w + step[6] * sign) >> 16, ns, nt);
                int4 next = TextureCycle(ClampCoordinate(ns), ClampCoordinate(nt), g_tile0, 0, int4(0, 0, 0, 0));
                g_slot[S_TEX1] = next;
                g_slot[S_TEX1_ALPHA] = Gray(next.w);
            }
        }
    }
    else
    {
        alphaCompareValue = 0;
    }

    int4 full = Combine(ccA1, ccB1, ccM1, ccD1);
    int alpha = (Combine(acA1, acB1, acM1, acD1).w >> 8) & 0x1FF;
    if (m_keyEnable)
    {
        int4 keyInput = Ext4(g_slot[ccA1]);
        pixel = int3(Clamp9(keyInput.x), Clamp9(keyInput.y), Clamp9(keyInput.z));
        pixelA = KeyAlpha(full.xyz);
    }
    else
    {
        pixel = int3(Clamp9(full.x >> 8), Clamp9(full.y >> 8), Clamp9(full.z >> 8));
        pixelA = FinalAlpha(alpha, alphaDither, coverage);
        if (m_cvgTimesAlpha)
        {
            int ca = Clamp9(alpha);
            coverage = (((ca == 0xFF ? 0x100 : ca) * coverage + 4) >> 8) & 0xF;
        }
    }

    if (!twoCycle) alphaCompareValue = pixelA;
    int blenderShadeA = shadeA + alphaDither;
    if ((blenderShadeA & 0x100) != 0) blenderShadeA = 0xFF;

    // Memory.
    int3 mem;
    int memCvg;
    ReadFramebuffer(color, size, format, mem, memCvg);

    // The Z test.
    bool blendEnable, prewrap;
    bool zUsed = m_zCompare;
    if (zUsed)
    {
        if (!ZCompare((int)(depth & 0xFFFF), sz, dzPix, dzPixEncoded, coverage, memCvg, blendEnable, prewrap)) return;
    }
    else
    {
        prewrap = ((memCvg + coverage) & 8) != 0;
        blendEnable = m_forceBlend || (!prewrap && m_antialias);
        if (m_blendB[0] == 1 || m_blendB[1] == 1)
        {
            g_blendShiftA = 0;
            g_blendShiftB = dzPixEncoded < 0xB ? 4 : 0xF - dzPixEncoded;
        }
    }

    int memA = memCvg << 5;
    int3 outColor;
    if (twoCycle)
    {
        if ((m_antialias ? coverage : centerBit) == 0) return;
        int3 first3;
        if (m_blendP[0] == 0 && m_blendA[0] == 3 && m_blendM[0] == 0 && m_blendB[0] == 2) first3 = pixel;
        else first3 = BlendCycle(0, pixel, pixelA, blenderShadeA, mem, memA, int3(0, 0, 0), true);
        if (m_alphaCompare && !AlphaCompare(alphaCompareValue)) return;
        if (!blendEnable && !m_colorOnCvg && m_blendP[1] == 0) outColor = first3;
        else outColor = BlendOutput(1, pixel, pixelA, blenderShadeA, mem, memA, first3, blendEnable, prewrap);
    }
    else
    {
        if (m_alphaCompare && !AlphaCompare(alphaCompareValue)) return;
        if ((m_antialias ? coverage : centerBit) == 0) return;
        if (!blendEnable && !m_colorOnCvg && m_blendP[0] == 0) outColor = pixel;
        else outColor = BlendOutput(0, pixel, pixelA, blenderShadeA, mem, memA, int3(0, 0, 0), blendEnable, prewrap);
    }

    if (m_rgbDither != 3)
    {
        if (m_rgbDither == 2)
        {
            outColor = int3(Dither(outColor.x, colorDither & 7), Dither(outColor.y, (colorDither >> 3) & 7), Dither(outColor.z, (colorDither >> 6) & 7));
        }
        else
        {
            outColor = int3(Dither(outColor.x, colorDither), Dither(outColor.y, colorDither), Dither(outColor.z, colorDither));
        }
    }

    color = FramebufferValue(size, format, colorAddress + nativeIndex * (size == SIZE16 ? 2u : (size == SIZE32 ? 4u : 1u)), outColor, blendEnable, coverage, memCvg, color);
    if (m_zUpdate) depth = (uint)(((CompressZ(sz) << 2) | (dzPixEncoded >> 2)) & 0xFFFF);
}

[numthreads(8, 8, 1)]
void Draw(uint3 id : SV_DispatchThreadID)
{
    int scale = (int)Params[P_SCALE];
    int width = (int)Params[P_WIDTH];
    int height = (int)Params[P_HEIGHT];
    int X = (int)Params[P_ORIGIN_X] + (int)id.x;
    int Y = (int)Params[P_ORIGIN_Y] + (int)id.y;
    int scaledWidth = width * scale;
    if (X >= scaledWidth || Y >= height * scale) return;

    uint index = (uint)(Y * scaledWidth + X);
    uint color = Color[index];
    uint depth = Depth[index];
    int px = X / scale, scan = Y / scale, fx = X % scale, fy = Y % scale;
    uint count = Params[P_COUNT];
    for (uint i = 0; i < count; i++)
    {
        g_prim = i * PRIM_WORDS;
        if (X < I(F_BOX) || Y < I(F_BOX + 1) || X >= I(F_BOX + 2) || Y >= I(F_BOX + 3)) continue;
        g_tmem = W(F_TMEM) * 1024u;
        g_noise = Params[P_SEED] ^ (uint)(px * 0x9E3779B1u) ^ (uint)(scan * 0x85EBCA77u) ^ (i * 0xC2B2AE3Du);
        SetupRows();
        Pixel(px, scan, fx, fy, scale, color, depth, index);
    }

    Color[index] = color;
    Depth[index] = depth;
}
