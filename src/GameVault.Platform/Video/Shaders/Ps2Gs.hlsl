// The PlayStation 2's GS on the GPU (docs/plans/gpu-renderers.md, milestone 3): a port of Ps2Gs.Renderer.cs's
// rasterizer and pixel pipeline (ps2tek: "GS Primitives", "GS Textures", "GS Fog", "GS Alpha Blending", "GS Tests and
// Pixel Control") drawing into local memory kept at S×S samples a pixel. Native 32-bit word w of local memory holds
// its samples at Mem[w × S² + k], k = fy × S + fx; 16-, 8- and 4-bit pixels share their sample words as they share
// native words. Every buffer and texture goes through the GS's own address functions (the page tables in Tables),
// so every format, and reading one through another, works at the internal resolution.
//
// One thread per scaled pixel (X, Y) of the dispatch walks the dispatch's commands in order. Its native pixel is
// (X / S, Y / S) and it samples at X × 16 + 8 − 8S (and the same for Y) in 1/(16S) pixel units, which is the GS's
// integer sample point at 1×. The CPU sets each command up (Ps2GsGpuRenderer.cs): edge functions, attribute planes
// and Z in fixed point, so the kernel's arithmetic stays in 32-bit integers and floats. It compiles with fxc
// (cs_5_0) and, for the tests, with glslang to SPIR-V.

#define CMD_WORDS 64
#define STATE_WORDS 96
#define MEMORY_MASK 0x3FFFFF

// Params (dispatches of Draw)
#define P_COUNT 0
#define P_FIRST 1
#define P_SCALE 2
#define P_ORIGIN_X 3
#define P_ORIGIN_Y 4

// Command words
#define C_KIND 0
#define C_STATE 1
#define C_BOX 2
#define C_GEOMETRY 8
#define C_PLANES 20
#define C_Z 44
#define C_LINE_Z 50

#define KIND_POINT 0
#define KIND_LINE 1
#define KIND_SPRITE 2
#define KIND_TRIANGLE 3

#define A_R 0
#define A_G 1
#define A_B 2
#define A_A 3
#define A_F 4
#define A_S 5
#define A_T 6
#define A_Q 7

// State words
#define S_TME 0
#define S_FGE 1
#define S_ABE 2
#define S_FST 3
#define S_SCAX0 4
#define S_SCAX1 5
#define S_SCAY0 6
#define S_SCAY1 7
#define S_FBP 8
#define S_FBW 9
#define S_FPSM 10
#define S_FBMSK 11
#define S_FB16 12
#define S_FB24 13
#define S_ZBP 14
#define S_ZPSM 15
#define S_Z16 16
#define S_Z24 17
#define S_ZMAX 18
#define S_ZMSK 19
#define S_ATE 20
#define S_ATST 21
#define S_AREF 22
#define S_AFAIL 23
#define S_DATE 24
#define S_DATM 25
#define S_ZTE 26
#define S_ZTST 27
#define S_BLEND_A 28
#define S_BLEND_B 29
#define S_BLEND_C 30
#define S_BLEND_D 31
#define S_BLEND_FIX 32
#define S_PABE 33
#define S_FBA 34
#define S_COLCLAMP 35
#define S_DTHE 36
#define S_SCANMSK_SKIP 37
#define S_TPSM 38
#define S_TEXW 39
#define S_TEXH 40
#define S_TCC 41
#define S_TFX 42
#define S_WMS 43
#define S_WMT 44
#define S_MINU 45
#define S_MAXU 46
#define S_MINV 47
#define S_MAXV 48
#define S_MAG_LINEAR 49
#define S_MIN_LINEAR 50
#define S_LCM 51
#define S_LODL 52
#define S_LODK 53
#define S_MXL 54
#define S_MIPMODE 55
#define S_MIP_TBP 56
#define S_MIP_TBW 63
#define S_TA0 70
#define S_TA1 71
#define S_AEM 72
#define S_FOG_R 73
#define S_FOG_G 74
#define S_FOG_B 75
#define S_DIMX 76
#define S_PALETTE 92

// Pixel storage modes (Ps2Gs.Memory.cs)
#define PSMCT32 0x00
#define PSMCT24 0x01
#define PSMCT16 0x02
#define PSMCT16S 0x0A
#define PSMT8 0x13
#define PSMT4 0x14
#define PSMT8H 0x1B
#define PSMT4HL 0x24
#define PSMT4HH 0x2C
#define PSMZ32 0x30
#define PSMZ24 0x31
#define PSMZ16 0x32
#define PSMZ16S 0x3A

// The page tables in Tables
#define T_CT32 0
#define T_Z32 2048
#define T_CT16 4096
#define T_CT16S 8192
#define T_Z16 12288
#define T_Z16S 16384
#define T_T8 20480
#define T_T4 28672

RWStructuredBuffer<uint> Params : register(u0);
RWStructuredBuffer<uint> Commands : register(u1);
RWStructuredBuffer<uint> States : register(u2);
RWStructuredBuffer<uint> Tables : register(u3);
RWStructuredBuffer<uint> Palettes : register(u4);
RWStructuredBuffer<uint> Mem : register(u5);
RWStructuredBuffer<uint> HdPages : register(u6);

static uint g_cmd;    // the command's first word
static uint g_state;  // its state's first word
static int g_scale;
static uint g_samples; // S²

int CI(uint i) { return (int)Commands[g_cmd + i]; }
float CF(uint i) { return asfloat(Commands[g_cmd + i]); }
int SI(uint i) { return (int)States[g_state + i]; }
uint SU(uint i) { return States[g_state + i]; }
bool SB(uint i) { return States[g_state + i] != 0; }

// ---- 64-bit integers (x: the low word, y: the high word, two's complement) -------------------------------------

uint2 MulU32(uint a, uint b)
{
    uint a0 = a & 0xFFFF, a1 = a >> 16, b0 = b & 0xFFFF, b1 = b >> 16;
    uint p00 = a0 * b0, p01 = a0 * b1, p10 = a1 * b0, p11 = a1 * b1;
    uint mid = (p00 >> 16) + (p01 & 0xFFFF) + (p10 & 0xFFFF);
    return uint2((p00 & 0xFFFF) | (mid << 16), p11 + (p01 >> 16) + (p10 >> 16) + (mid >> 16));
}

uint2 Add64(uint2 a, uint2 b)
{
    uint lo = a.x + b.x;
    return uint2(lo, a.y + b.y + (lo < a.x ? 1u : 0u));
}

// a × b, b ≥ 0 (the low 64 bits, which is the signed product for a two's complement a).
uint2 Mul64(uint2 a, uint b)
{
    uint2 p = MulU32(a.x, b);
    return uint2(p.x, p.y + a.y * b);
}

uint2 FromInt(int v) { return uint2((uint)v, v < 0 ? 0xFFFFFFFFu : 0u); }

bool Positive64(uint2 v) { return (int)v.y > 0 || (v.y == 0 && v.x != 0); }

// ---- Local memory ---------------------------------------------------------------------------------------------

uint Index(int address, uint k) { return ((uint)address >> 2) * g_samples + k; }

uint Read32(int address, uint k) { return Mem[Index(address, k)]; }

uint Read16(int address, uint k) { return (Mem[Index(address, k)] >> ((address & 2) * 8)) & 0xFFFF; }

void Write32(int address, uint k, uint value) { Mem[Index(address, k)] = value; }

// Other threads' pixels can share the word: change only this pixel's bits, atomically.
void WriteBits(int address, uint k, uint value, uint mask)
{
    uint i = Index(address, k);
    InterlockedAnd(Mem[i], ~mask);
    InterlockedOr(Mem[i], value & mask);
}

void Write16(int address, uint k, uint value)
{
    uint shift = (uint)(address & 2) * 8;
    WriteBits(address, k, (value & 0xFFFF) << shift, 0xFFFFu << shift);
}

void Write24(int address, uint k, uint value) { WriteBits(address, k, value, 0xFFFFFFu); }

int Address32(uint table, int bp, int bw, int x, int y)
{
    x &= 2047;
    y &= 2047;
    int page = (x >> 6) + ((y >> 5) * bw);
    return ((bp << 8) + (page << 13) + (int)Tables[table + (uint)(((y & 31) << 6) | (x & 63))]) & MEMORY_MASK;
}

int Address16(uint table, int bp, int bw, int x, int y)
{
    x &= 2047;
    y &= 2047;
    int page = (x >> 6) + ((y >> 6) * bw);
    return ((bp << 8) + (page << 13) + (int)Tables[table + (uint)(((y & 63) << 6) | (x & 63))]) & MEMORY_MASK;
}

int Address8(int bp, int bw, int x, int y)
{
    x &= 2047;
    y &= 2047;
    int page = (x >> 7) + ((y >> 6) * (bw >> 1));
    return ((bp << 8) + (page << 13) + (int)Tables[T_T8 + (uint)(((y & 63) << 7) | (x & 127))]) & MEMORY_MASK;
}

// A nibble address (byte address × 2, plus 1 for the high nibble).
int Address4(int bp, int bw, int x, int y)
{
    x &= 2047;
    y &= 2047;
    int page = (x >> 7) + ((y >> 7) * (bw >> 1));
    return ((bp << 9) + (page << 14) + (int)Tables[T_T4 + (uint)(((y & 127) << 7) | (x & 127))]) & (MEMORY_MASK * 2 + 1);
}

uint TableFor(int psm)
{
    if (psm == PSMCT16) return T_CT16;
    if (psm == PSMCT16S) return T_CT16S;
    if (psm == PSMZ16) return T_Z16;
    if (psm == PSMZ16S) return T_Z16S;
    if (psm == PSMZ32 || psm == PSMZ24) return T_Z32;
    return T_CT32;
}

bool Is16(int psm) { return psm == PSMCT16 || psm == PSMCT16S || psm == PSMZ16 || psm == PSMZ16S; }

// A pixel's stored bits (Ps2Gs.Memory.cs's ReadPixel), sample k.
uint ReadPixel(int psm, int bp, int bw, int x, int y, uint k)
{
    if (psm == PSMT8)
    {
        int a8 = Address8(bp, bw, x, y);
        return (Read32(a8 & ~3, k) >> ((a8 & 3) * 8)) & 0xFF;
    }

    if (psm == PSMT4)
    {
        int n = Address4(bp, bw, x, y);
        int a4 = n >> 1;
        return (Read32(a4 & ~3, k) >> ((a4 & 3) * 8 + (n & 1) * 4)) & 0xF;
    }

    if (Is16(psm)) return Read16(Address16(TableFor(psm), bp, bw, x, y), k);
    uint v = Read32(Address32(TableFor(psm), bp, bw, x, y), k);
    if (psm == PSMCT24 || psm == PSMZ24) return v & 0xFFFFFF;
    if (psm == PSMT8H) return v >> 24;
    if (psm == PSMT4HL) return (v >> 24) & 0xF;
    if (psm == PSMT4HH) return v >> 28;
    return v;
}

// ---- Texels ---------------------------------------------------------------------------------------------------

uint Expand16(uint c)
{
    uint rgb = ((c & 0x1F) << 3) | ((c & 0x3E0) << 6) | ((c & 0x7C00) << 9);
    uint a = (c & 0x8000) != 0 ? SU(S_TA1) : (!SB(S_AEM) || (c & 0x7FFF) != 0) ? SU(S_TA0) : 0u;
    return rgb | (a << 24);
}

uint Expand24(uint c)
{
    c &= 0xFFFFFF;
    uint a = (!SB(S_AEM) || c != 0) ? SU(S_TA0) : 0u;
    return c | (a << 24);
}

uint FetchTexel(int u, int v, int level, uint k)
{
    int psm = SI(S_TPSM);
    uint raw = ReadPixel(psm, SI(S_MIP_TBP + (uint)level), SI(S_MIP_TBW + (uint)level), u, v, k);
    if (psm == PSMCT32 || psm == PSMZ32) return raw;
    if (psm == PSMCT24 || psm == PSMZ24) return Expand24(raw);
    if (Is16(psm)) return Expand16(raw);
    return Palettes[SU(S_PALETTE) + (raw & 0xFF)];
}

int Wrap(int t, int mode, int size, int mn, int mx)
{
    if (mode == 0) return t & (size - 1);
    if (mode == 1) return t < 0 ? 0 : t >= size ? size - 1 : t;
    if (mode == 2) return t < mn ? mn : t > mx ? mx : t;
    return (t & mn) | mx;
}

int FloorDiv(int a, int b) { return a >= 0 ? a / b : -((-a + b - 1) / b); }

// A coordinate in sample units (1/S texel) to its texel (wrapped) and sample.
int2 SplitWrap(int t, int scale, int mode, int size, int mn, int mx)
{
    int texel = FloorDiv(t, scale);
    return int2(Wrap(texel, mode, size, mn, mx), t - texel * scale);
}

uint Bilerp(uint c00, uint c10, uint c01, uint c11, int fu, int fv)
{
    uint result = 0;
    [unroll] for (int shift = 0; shift < 32; shift += 8)
    {
        int a = (int)((c00 >> shift) & 0xFF);
        int b = (int)((c10 >> shift) & 0xFF);
        int c = (int)((c01 >> shift) & 0xFF);
        int d = (int)((c11 >> shift) & 0xFF);
        int top = (a * (16 - fu)) + (b * fu);
        int bottom = (c * (16 - fu)) + (d * fu);
        result |= (uint)(((top * (16 - fv)) + (bottom * fv)) >> 8) << shift;
    }

    return result;
}

// Ps2Gs.Renderer.cs's SampleTexture. A texture the GPU drew (an HD page) is sampled on its sample grid, S texels to
// the native texel; one uploaded from the EE (every sample the same) is sampled as at 1×, with sample 0.
uint SampleTexture(int u16, int v16, bool bilinear, int level, bool hd)
{
    int w = max(SI(S_TEXW) >> level, 1);
    int h = max(SI(S_TEXH) >> level, 1);
    int minu = SI(S_MINU) >> level, maxu = SI(S_MAXU) >> level;
    int minv = SI(S_MINV) >> level, maxv = SI(S_MAXV) >> level;
    int wms = SI(S_WMS), wmt = SI(S_WMT);
    if (wms == 3) { minu = SI(S_MINU); maxu = SI(S_MAXU); }
    if (wmt == 3) { minv = SI(S_MINV); maxv = SI(S_MAXV); }
    u16 >>= level;
    v16 >>= level;
    int s = hd ? g_scale : 1;
    if (!bilinear)
    {
        int2 su = SplitWrap((u16 * s) >> 4, s, wms, w, minu, maxu);
        int2 sv = SplitWrap((v16 * s) >> 4, s, wmt, h, minv, maxv);
        return FetchTexel(su.x, sv.x, level, (uint)(sv.y * g_scale + su.y));
    }

    int us = u16 * s - 8, vs = v16 * s - 8;
    int fu = us & 15, fv = vs & 15;
    int2 u0 = SplitWrap(us >> 4, s, wms, w, minu, maxu);
    int2 u1 = SplitWrap((us >> 4) + 1, s, wms, w, minu, maxu);
    int2 v0 = SplitWrap(vs >> 4, s, wmt, h, minv, maxv);
    int2 v1 = SplitWrap((vs >> 4) + 1, s, wmt, h, minv, maxv);
    uint c00 = FetchTexel(u0.x, v0.x, level, (uint)(v0.y * g_scale + u0.y));
    uint c10 = FetchTexel(u1.x, v0.x, level, (uint)(v0.y * g_scale + u1.y));
    uint c01 = FetchTexel(u0.x, v1.x, level, (uint)(v1.y * g_scale + u0.y));
    uint c11 = FetchTexel(u1.x, v1.x, level, (uint)(v1.y * g_scale + u1.y));
    return Bilerp(c00, c10, c01, c11, fu, fv);
}

bool HdLevel(int level) { return HdPages[(uint)(SI(S_MIP_TBP + (uint)level) >> 5) & 511] != 0; }

int ToByte(float v) { return v <= 0 ? 0 : v >= 255 ? 255 : (int)v; }

int ToTexCoord(float v) { return v >= -16777216.0 && v <= 16777216.0 ? (int)floor(v) : v > 0 ? 16777216 : -16777216; }

uint Texture(float sAttr, float tAttr, float q)
{
    int u16, v16;
    if (SB(S_FST))
    {
        u16 = ToTexCoord(sAttr);
        v16 = ToTexCoord(tAttr);
    }
    else
    {
        float scale = 16.0 / q;
        u16 = ToTexCoord(sAttr * scale * (float)SI(S_TEXW));
        v16 = ToTexCoord(tAttr * scale * (float)SI(S_TEXH));
    }

    bool magLinear = SB(S_MAG_LINEAR), minLinear = SB(S_MIN_LINEAR);
    int mipMode = SI(S_MIPMODE), mxl = SI(S_MXL);
    if (magLinear == minLinear && mipMode == 0) return SampleTexture(u16, v16, magLinear, 0, HdLevel(0));

    // LOD = K when LCM is set, otherwise (log2(1/|Q|) << L) + K (ps2tek: "GS Textures").
    float lod = SB(S_LCM) ? asfloat(SU(S_LODK)) : log2(1.0 / abs(q)) * (float)(1 << SI(S_LODL)) + asfloat(SU(S_LODK));
    if (lod <= 0 || mipMode == 0) return SampleTexture(u16, v16, lod > 0 ? minLinear : magLinear, 0, HdLevel(0));
    if (mipMode == 1)
    {
        int nearest = min((int)(lod + 0.5), mxl);
        return SampleTexture(u16, v16, minLinear, nearest, HdLevel(nearest));
    }

    int level = min((int)lod, mxl);
    int next = min(level + 1, mxl);
    int t = level == next ? 0 : (int)((lod - (float)(int)lod) * 16);
    uint c0 = SampleTexture(u16, v16, minLinear, level, HdLevel(level));
    uint c1 = t == 0 ? c0 : SampleTexture(u16, v16, minLinear, next, HdLevel(next));
    uint tex = 0;
    [unroll] for (int shift = 0; shift < 32; shift += 8)
    {
        int x0 = (int)((c0 >> shift) & 0xFF);
        int x1 = (int)((c1 >> shift) & 0xFF);
        tex |= (uint)(((x0 * (16 - t)) + (x1 * t)) >> 4) << shift;
    }

    return tex;
}

// ---- Fragments ------------------------------------------------------------------------------------------------

bool AlphaPasses(int a)
{
    int atst = SI(S_ATST), aref = SI(S_AREF);
    if (atst == 0) return false;
    if (atst == 1) return true;
    if (atst == 2) return a < aref;
    if (atst == 3) return a <= aref;
    if (atst == 4) return a == aref;
    if (atst == 5) return a >= aref;
    if (atst == 6) return a > aref;
    return a != aref;
}

int FrameAddress(int x, int y)
{
    int psm = SI(S_FPSM);
    return SB(S_FB16) ? Address16(TableFor(psm), SI(S_FBP), SI(S_FBW), x, y) : Address32(TableFor(psm), SI(S_FBP), SI(S_FBW), x, y);
}

int ZAddress(int x, int y)
{
    int psm = SI(S_ZPSM);
    return SB(S_Z16) ? Address16(TableFor(psm), SI(S_ZBP), SI(S_FBW), x, y) : Address32(TableFor(psm), SI(S_ZBP), SI(S_FBW), x, y);
}

uint ReadZ(int address, uint k) { return SB(S_Z16) ? Read16(address, k) : Read32(address, k) & SU(S_ZMAX); }

void WriteZ(int address, uint k, uint z)
{
    if (SB(S_Z16)) Write16(address, k, z);
    else if (SB(S_Z24)) Write24(address, k, z);
    else Write32(address, k, z);
}

uint ReadFrameColor(int address, uint k)
{
    if (SB(S_FB16))
    {
        uint c = Read16(address, k);
        return ((c & 0x1F) << 3) | ((c & 0x3E0) << 6) | ((c & 0x7C00) << 9) | ((c & 0x8000) != 0 ? 0x80000000u : 0u);
    }

    uint v = Read32(address, k);
    return SB(S_FB24) ? (v & 0xFFFFFF) | 0x80000000u : v;
}

void WriteFrameColor(int address, uint k, uint color, uint mask)
{
    if (SB(S_FB16))
    {
        uint c16 = ((color >> 3) & 0x1F) | ((color >> 6) & 0x3E0) | ((color >> 9) & 0x7C00) | ((color >> 16) & 0x8000);
        uint m16 = ((mask >> 3) & 0x1F) | ((mask >> 6) & 0x3E0) | ((mask >> 9) & 0x7C00) | ((mask >> 16) & 0x8000);
        uint shift = (uint)(address & 2) * 8;
        WriteBits(address, k, c16 << shift, (~m16 & 0xFFFF) << shift);
        return;
    }

    if (SB(S_FB24)) mask |= 0xFF000000;
    if (mask == 0) Write32(address, k, color);
    else WriteBits(address, k, color, ~mask);
}

int BlendInput(int which, int source, int dest) { return which == 0 ? source : which == 1 ? dest : 0; }

int Blend(int source, int dest, int c)
{
    int av = BlendInput(SI(S_BLEND_A), source, dest);
    int bv = BlendInput(SI(S_BLEND_B), source, dest);
    int dv = BlendInput(SI(S_BLEND_D), source, dest);
    return (((av - bv) * c) >> 7) + dv;
}

// Ps2Gs.Renderer.cs's WriteFragment: the tests, the blend and the writes, for sample k of pixel (x, y).
void WriteFragment(int x, int y, uint k, uint z, int r, int g, int b, int a)
{
    if ((y & 1) == SI(S_SCANMSK_SKIP)) return;
    bool fbWrite = true;
    bool zWrite = SB(S_ZTE) && !SB(S_ZMSK);
    uint mask = SU(S_FBMSK);
    if (SB(S_ATE) && !AlphaPasses(a))
    {
        int afail = SI(S_AFAIL);
        if (afail == 0) return;
        if (afail == 1) zWrite = false;
        else if (afail == 2) fbWrite = false;
        else { zWrite = false; mask |= 0xFF000000; }
    }

    int zAddress = 0;
    if (SB(S_ZTE))
    {
        z = min(z, SU(S_ZMAX));
        int ztst = SI(S_ZTST);
        if (ztst == 0) return;
        if (ztst == 1)
        {
            if (zWrite) zAddress = ZAddress(x, y);
        }
        else
        {
            zAddress = ZAddress(x, y);
            uint stored = ReadZ(zAddress, k);
            if (ztst == 2 ? z < stored : z <= stored) return;
        }
    }

    int fbAddress = FrameAddress(x, y);
    bool needDest = SB(S_DATE) || SB(S_ABE);
    uint dest = needDest ? ReadFrameColor(fbAddress, k) : 0u;
    if (SB(S_DATE) && !SB(S_FB24) && (int)(dest >> 31) != SI(S_DATM)) return;
    if (zWrite) WriteZ(zAddress, k, z);
    if (!fbWrite) return;

    if (SB(S_ABE) && !(SB(S_PABE) && (a & 0x80) == 0))
    {
        int dr = (int)(dest & 0xFF), dg = (int)((dest >> 8) & 0xFF), db = (int)((dest >> 16) & 0xFF), da = (int)(dest >> 24);
        int bc = SI(S_BLEND_C);
        int c = bc == 0 ? a : bc == 1 ? da : SI(S_BLEND_FIX);
        r = Blend(r, dr, c);
        g = Blend(g, dg, c);
        b = Blend(b, db, c);
    }

    if (SB(S_DTHE) && SB(S_FB16))
    {
        int d = SI(S_DIMX + (uint)(((y & 3) << 2) | (x & 3)));
        r += d;
        g += d;
        b += d;
    }

    if (SB(S_COLCLAMP))
    {
        r = clamp(r, 0, 255);
        g = clamp(g, 0, 255);
        b = clamp(b, 0, 255);
    }
    else
    {
        r &= 0xFF;
        g &= 0xFF;
        b &= 0xFF;
    }

    if (SB(S_FBA)) a |= 0x80;
    WriteFrameColor(fbAddress, k, (uint)r | ((uint)g << 8) | ((uint)b << 16) | ((uint)(a & 0xFF) << 24), mask);
}

// Ps2Gs.Renderer.cs's ShadeFragment: texture, fog, then the tests and writes.
void ShadeFragment(int x, int y, uint k, uint z, float4 color, float fog, float sAttr, float tAttr, float q)
{
    int r = ToByte(color.x), g = ToByte(color.y), b = ToByte(color.z), a = ToByte(color.w);
    if (SB(S_TME))
    {
        uint tex = Texture(sAttr, tAttr, q);
        int tr = (int)(tex & 0xFF), tg = (int)((tex >> 8) & 0xFF), tb = (int)((tex >> 16) & 0xFF), ta = (int)(tex >> 24);
        int tfx = SI(S_TFX);
        bool tcc = SB(S_TCC);
        if (tfx == 0) // MODULATE
        {
            r = min((r * tr) >> 7, 255);
            g = min((g * tg) >> 7, 255);
            b = min((b * tb) >> 7, 255);
            if (tcc) a = min((a * ta) >> 7, 255);
        }
        else if (tfx == 1) // DECAL
        {
            r = tr;
            g = tg;
            b = tb;
            if (tcc) a = ta;
        }
        else // HIGHLIGHT, HIGHLIGHT2
        {
            r = min(((r * tr) >> 7) + a, 255);
            g = min(((g * tg) >> 7) + a, 255);
            b = min(((b * tb) >> 7) + a, 255);
            if (tcc) a = tfx == 2 ? min(ta + a, 255) : ta;
        }
    }

    if (SB(S_FGE))
    {
        int f = ToByte(fog);
        r = ((f * r) + ((255 - f) * SI(S_FOG_R))) >> 8;
        g = ((f * g) + ((255 - f) * SI(S_FOG_G))) >> 8;
        b = ((f * b) + ((255 - f) * SI(S_FOG_B))) >> 8;
    }

    WriteFragment(x, y, k, z, r, g, b, a);
}

// ---- Primitives -----------------------------------------------------------------------------------------------

float Plane(uint attribute, float px, float py)
{
    uint at = C_PLANES + attribute * 3;
    return CF(at) + CF(at + 1) * px + CF(at + 2) * py;
}

bool EdgeInside(uint e, int X, int Y)
{
    uint at = C_GEOMETRY + e * 4;
    uint2 v = uint2(Commands[g_cmd + at], Commands[g_cmd + at + 1]);
    v = Add64(v, Mul64(FromInt(CI(at + 2)), (uint)X));
    v = Add64(v, Mul64(FromInt(CI(at + 3)), (uint)Y));
    return Positive64(v);
}

uint PlaneZ(int X, int Y)
{
    uint2 z = uint2(Commands[g_cmd + C_Z], Commands[g_cmd + C_Z + 1]);
    z = Add64(z, Mul64(uint2(Commands[g_cmd + C_Z + 2], Commands[g_cmd + C_Z + 3]), (uint)X));
    z = Add64(z, Mul64(uint2(Commands[g_cmd + C_Z + 4], Commands[g_cmd + C_Z + 5]), (uint)Y));
    // >> 16, clamped to 0 … 2³² − 1.
    int hi = (int)z.y >> 16;
    if ((int)z.y < 0) return 0;
    if (hi > 0) return 0xFFFFFFFFu;
    return (z.x >> 16) | (z.y << 16);
}

void DrawCommand(int X, int Y, int x, int y, uint k)
{
    if (x < SI(S_SCAX0) || x > SI(S_SCAX1) || y < SI(S_SCAY0) || y > SI(S_SCAY1)) return;
    int kind = CI(C_KIND);
    int s = g_scale;
    // The sample's position in pixels (for the attribute planes, set up in these units).
    float px = (float)X, py = (float)Y;
    if (kind == KIND_TRIANGLE)
    {
        if (!EdgeInside(0, X, Y) || !EdgeInside(1, X, Y) || !EdgeInside(2, X, Y)) return;
    }
    else if (kind == KIND_SPRITE)
    {
        int Px = X * 16 + 8 - 8 * s, Py = Y * 16 + 8 - 8 * s;
        if (Px < CI(C_GEOMETRY) * s || Px >= CI(C_GEOMETRY + 1) * s || Py < CI(C_GEOMETRY + 2) * s || Py >= CI(C_GEOMETRY + 3) * s) return;
    }
    else if (kind == KIND_POINT)
    {
        if (x != CI(C_GEOMETRY) || y != CI(C_GEOMETRY + 1)) return;
    }
    else
    {
        // A line: the pixel on the major axis's step p, and the minor coordinate it rounds to.
        bool xMajor = CI(C_GEOMETRY) != 0;
        int p = xMajor ? x : y;
        int first = CI(C_GEOMETRY + 5), last = CI(C_GEOMETRY + 6), step = CI(C_GEOMETRY + 7);
        if (step > 0 ? (p < first || p > last) : (p > first || p < last)) return;
        float t = ((float)p * 16.0 - (float)CI(C_GEOMETRY + 1)) / (float)CI(C_GEOMETRY + 4);
        int q = (int)floor(((float)CI(C_GEOMETRY + 2) + t * (float)CI(C_GEOMETRY + 3) + 8.0) / 16.0);
        if ((xMajor ? y : x) != q) return;
        px = t;
        py = 0;
    }

    uint z;
    if (kind == KIND_LINE)
    {
        float zf = CF(C_LINE_Z) + CF(C_LINE_Z + 1) * px;
        z = zf <= 0 ? 0u : zf >= 4294967040.0 ? 0xFFFFFFFFu : (uint)zf;
    }
    else
    {
        z = PlaneZ(X, Y);
    }

    float4 color = float4(Plane(A_R, px, py), Plane(A_G, px, py), Plane(A_B, px, py), Plane(A_A, px, py));
    ShadeFragment(x, y, k, z, color, Plane(A_F, px, py), Plane(A_S, px, py), Plane(A_T, px, py), Plane(A_Q, px, py));
}

[numthreads(8, 8, 1)]
void Draw(uint3 id : SV_DispatchThreadID)
{
    g_scale = (int)Params[P_SCALE];
    g_samples = (uint)(g_scale * g_scale);
    int X = (int)Params[P_ORIGIN_X] + (int)id.x;
    int Y = (int)Params[P_ORIGIN_Y] + (int)id.y;
    int x = X / g_scale, y = Y / g_scale;
    uint k = (uint)((Y - y * g_scale) * g_scale + (X - x * g_scale));
    uint first = Params[P_FIRST];
    uint count = Params[P_COUNT];
    for (uint i = first; i < first + count; i++)
    {
        g_cmd = i * CMD_WORDS;
        if (X < CI(C_BOX) || Y < CI(C_BOX + 1) || X >= CI(C_BOX + 2) || Y >= CI(C_BOX + 3)) continue;
        g_state = (uint)CI(C_STATE) * STATE_WORDS;
        DrawCommand(X, Y, x, y, k);
    }
}

// ---- The picture ----------------------------------------------------------------------------------------------
// Ps2Gs.Output.cs's RenderFrame at the internal resolution, as ARGB into the buffer bound at u1 (Commands, for
// Draw). Params: the layout, and each circuit's words from D_C1 and D_C2.

#define D_WIDTH 0
#define D_HEIGHT 1
#define D_SCALE 2
#define D_SOURCE_WIDTH 3
#define D_MIN_DX 4
#define D_MIN_DY 5
#define D_BACKGROUND 6
#define D_FIELD 7
#define D_FIXED_ALPHA 8
#define D_BACKGROUND_UNDER 9
#define D_ALPHA 10
#define D_C1 16
#define D_C2 32
// A circuit's words
#define DC_ENABLED 0
#define DC_FBP 1
#define DC_FBW 2
#define DC_PSM 3
#define DC_DBX 4
#define DC_DBY 5
#define DC_DX 6
#define DC_DY 7
#define DC_WIDTH 8
#define DC_HEIGHT 9

int DI(uint i) { return (int)Params[i]; }

bool ReadCircuit(uint c, int sx, int sy, out uint color)
{
    color = 0;
    if (DI(c + DC_ENABLED) == 0) return false;
    int s = g_scale;
    int x = sx - DI(c + DC_DX) * s, y = sy - DI(c + DC_DY) * s;
    if (x < 0 || y < 0 || x >= DI(c + DC_WIDTH) * s || y >= DI(c + DC_HEIGHT) * s) return false;
    if (DI(D_FIELD) != 0) y >>= 1;
    int nx = x / s, ny = y / s;
    uint k = (uint)((y - ny * s) * s + (x - nx * s));
    int psm = DI(c + DC_PSM);
    uint raw = ReadPixel(psm, DI(c + DC_FBP), DI(c + DC_FBW), DI(c + DC_DBX) + nx, DI(c + DC_DBY) + ny, k);
    if (Is16(psm)) color = ((raw & 0x1F) << 3) | ((raw & 0x3E0) << 6) | ((raw & 0x7C00) << 9) | ((raw & 0x8000) != 0 ? 0x80000000u : 0u);
    else if (psm == PSMCT24 || psm == PSMZ24) color = raw | 0x80000000u;
    else color = raw;
    return true;
}

uint Mix(uint top, uint under, int alpha)
{
    if (alpha >= 255) return top;
    uint result = 0;
    [unroll] for (int shift = 0; shift < 24; shift += 8)
    {
        int t = (int)((top >> shift) & 0xFF), u = (int)((under >> shift) & 0xFF);
        result |= (uint)(((t * alpha) + (u * (255 - alpha))) / 255) << shift;
    }

    return result;
}

[numthreads(8, 8, 1)]
void Display(uint3 id : SV_DispatchThreadID)
{
    g_scale = DI(D_SCALE);
    g_samples = (uint)(g_scale * g_scale);
    int width = DI(D_WIDTH) * g_scale, height = DI(D_HEIGHT) * g_scale;
    int X = (int)id.x, Y = (int)id.y;
    if (X >= width || Y >= height) return;
    int sourceWidth = DI(D_SOURCE_WIDTH) * g_scale;
    int sx = width == sourceWidth ? X : (int)(((uint)X * (uint)sourceWidth) / (uint)width);
    sx += DI(D_MIN_DX) * g_scale;
    int sy = Y + DI(D_MIN_DY) * g_scale;
    uint color = Params[D_BACKGROUND], under = color;
    uint p2, p1;
    if (DI(D_BACKGROUND_UNDER) == 0 && ReadCircuit(D_C2, sx, sy, p2))
    {
        under = p2;
        color = p2;
    }

    if (ReadCircuit(D_C1, sx, sy, p1))
    {
        int alpha = DI(D_FIXED_ALPHA) != 0 ? DI(D_ALPHA) : min((int)(p1 >> 24) * 2, 255);
        color = Mix(p1, under, alpha);
    }

    Commands[(uint)(Y * width + X)] = 0xFF000000u | ((color & 0xFF) << 16) | (color & 0xFF00) | ((color >> 16) & 0xFF);
}
