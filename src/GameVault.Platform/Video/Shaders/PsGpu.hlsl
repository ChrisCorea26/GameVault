// The PlayStation GPU drawn on Direct3D 11 at a higher internal resolution (docs/plans/gpu-renderers.md).
// Positions arrive in VRAM pixels (1024 x 512) and are scaled by the viewport; colours in 0-255; texture coordinates
// in texels. The scaled VRAM holds each pixel's 15-bit colour as UNORM (exactly k/31) and its mask bit in alpha.

Texture2D<uint> Native : register(t0);   // the emulated VRAM at its own resolution, as 16-bit words
Texture2D<float4> Scaled : register(t1); // a copy of the scaled VRAM (for VRAM-to-VRAM copies)

cbuffer Draw : register(b0)
{
    int2 Page;          // the texture page's corner in VRAM
    int2 Clut;          // the CLUT's position
    int Depth;          // 0: 4-bit, 1: 8-bit, 2: 15-bit
    int Flags;
    uint TextureWindow; // GP0 $E2
    int Scale;
    int2 CopyOffset;    // a copy: destination minus source, in scaled pixels
    int2 Padding;
};

static const int Textured = 1, Raw = 2, SetMask = 4, SemiPass = 8, OpaquePass = 16, Semi = 32;

struct VertexIn
{
    float2 Position : POSITION;
    float3 Color : COLOR;
    float2 Uv : TEXCOORD;
};

struct PixelIn
{
    float4 Position : SV_Position;
    float3 Color : COLOR;
    float2 Uv : TEXCOORD;
};

PixelIn VS(VertexIn input)
{
    PixelIn output;
    output.Position = float4(input.Position.x / 512.0 - 1.0, 1.0 - input.Position.y / 256.0, 0.0, 1.0);
    output.Color = input.Color;
    output.Uv = input.Uv;
    return output;
}

uint Word(int x, int y)
{
    return Native.Load(int3(x & 1023, y & 511, 0));
}

float4 FromWord(uint word)
{
    return float4(float(word & 31u), float((word >> 5) & 31u), float((word >> 10) & 31u), 0.0) / 31.0
        + float4(0.0, 0.0, 0.0, (word & 0x8000u) != 0u ? 1.0 : 0.0);
}

uint Texel(int u, int v)
{
    // The texture window: masked bits are replaced by the offset, in steps of 8 texels.
    int maskX = int(TextureWindow & 31u) * 8;
    int maskY = int((TextureWindow >> 5) & 31u) * 8;
    int offsetX = int((TextureWindow >> 10) & 31u) * 8;
    int offsetY = int((TextureWindow >> 15) & 31u) * 8;
    u = (u & ~maskX) | (offsetX & maskX);
    v = (v & ~maskY) | (offsetY & maskY);
    int y = Page.y + v;
    if (Depth == 0)
    {
        uint word = Word(Page.x + (u >> 2), y);
        uint entry = (word >> ((u & 3) * 4)) & 15u;
        return Word(Clut.x + int(entry), Clut.y);
    }

    if (Depth == 1)
    {
        uint word = Word(Page.x + (u >> 1), y);
        uint entry = (word >> ((u & 1) * 8)) & 255u;
        return Word(Clut.x + int(entry), Clut.y);
    }

    return Word(Page.x + u, y);
}

float4 PS(PixelIn input) : SV_Target
{
    float3 color = input.Color;
    float mask = (Flags & SetMask) != 0 ? 1.0 : 0.0;
    if ((Flags & Textured) != 0)
    {
        int u = int(floor(input.Uv.x)) & 255;
        int v = int(floor(input.Uv.y)) & 255;
        uint texel = Texel(u, v);
        if (texel == 0u)
        {
            discard; // fully transparent
        }

        bool blended = (texel & 0x8000u) != 0u;
        if ((Flags & Semi) != 0 && (Flags & OpaquePass) != 0 && blended)
        {
            discard; // drawn in the semi-transparent pass
        }

        if ((Flags & SemiPass) != 0 && !blended)
        {
            discard;
        }

        float3 t = float3(float(texel & 31u), float((texel >> 5) & 31u), float((texel >> 10) & 31u)) * 8.0;
        color = (Flags & Raw) != 0 ? t : min(255.0, floor(t * color / 128.0));
        if (blended)
        {
            mask = 1.0;
        }
    }

    // The GPU stores 15-bit colour: each channel to 5 bits before it's written (or blended).
    return float4(floor(color / 8.0) / 31.0, mask);
}

// A region of the native VRAM drawn into the scaled one (uploads, fills, resynchronising).
float4 PS_Upload(PixelIn input) : SV_Target
{
    int2 native = int2(input.Position.xy) / Scale;
    return FromWord(Word(native.x, native.y));
}

// A VRAM-to-VRAM copy, from a copy of the scaled VRAM.
float4 PS_Copy(PixelIn input) : SV_Target
{
    int2 size = int2(1024, 512) * Scale;
    int2 source = ((int2(input.Position.xy) - CopyOffset) % size + size) % size; // VRAM wraps
    float4 value = Scaled.Load(int3(source, 0));
    if ((Flags & SetMask) != 0)
    {
        value.a = 1.0;
    }

    return value;
}
