// GameVault display presets (SPEC §5.2). One full-screen triangle drawn into the picture's viewport.
// Mode 0 Sharp Pixels, 1 Smooth, 2 Living Room CRT, 3 Arcade Monitor.

cbuffer Params : register(b0)
{
    float2 OutputSize;   // viewport size in pixels
    float SourceTop;     // first visible NES line
    float SourceHeight;  // visible NES lines
    int Mode;
    float2 FrameSize;    // the frame texture's size (256x240 for the NES)
    float Padding;
};

Texture2D Frame : register(t0);
SamplerState PointSampler : register(s0);
SamplerState LinearSampler : register(s1);


struct VsOut
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
};

VsOut VS(uint id : SV_VertexID)
{
    VsOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.Position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    o.Uv = uv;
    return o;
}

// Picture UV (0..1 over the viewport) to frame texture coordinates.
float2 ToFrame(float2 uv)
{
    return float2(uv.x, (SourceTop + uv.y * SourceHeight) / FrameSize.y);
}

// Sharp bilinear: crisp pixel interiors with one output pixel of smoothing at the edges, so non-integer
// scales don't shimmer.
float3 SampleSharp(float2 uv)
{
    float2 texel = ToFrame(uv) * FrameSize;
    float2 scale = OutputSize / float2(FrameSize.x, SourceHeight);
    float2 fracPart = frac(texel);
    float2 region = 0.5 - 0.5 / scale;
    float2 offset = (clamp(fracPart - region, 0.0, 1.0 - 2.0 * region) / (1.0 - 2.0 * region)) - fracPart;
    return Frame.Sample(LinearSampler, (texel + offset) / FrameSize).rgb;
}

// Barrel distortion for the arcade preset.
float2 Curve(float2 uv, float amount)
{
    float2 c = uv * 2.0 - 1.0;
    c *= 1.0 + amount * float2(c.y * c.y, c.x * c.x);
    return c * 0.5 + 0.5;
}

float3 Crt(float2 uv, float2 pixel, float maskStrength, float scanStrength, float bloom)
{
    float3 color = SampleSharp(uv);

    // Scanlines: darken between NES lines, less on bright pixels (they "bloom" over the gaps).
    float scan = frac(ToFrame(uv).y * FrameSize.y);
    float brightness = dot(color, float3(0.299, 0.587, 0.114));
    float beam = lerp(scanStrength, 0.0, brightness * 0.6);
    color *= 1.0 - beam * pow(abs(scan - 0.5) * 2.0, 2.0);

    // Aperture grille: red, green and blue stripes across output pixels.
    uint stripe = (uint)pixel.x % 3;
    float3 mask = stripe == 0 ? float3(1.0, 1.0 - maskStrength, 1.0 - maskStrength)
                : stripe == 1 ? float3(1.0 - maskStrength, 1.0, 1.0 - maskStrength)
                              : float3(1.0 - maskStrength, 1.0 - maskStrength, 1.0);
    color *= mask;

    // A little glow from neighboring pixels.
    float2 texelStep = 1.0 / FrameSize;
    float3 glow = Frame.Sample(LinearSampler, ToFrame(uv) + float2(texelStep.x, 0)).rgb
                + Frame.Sample(LinearSampler, ToFrame(uv) - float2(texelStep.x, 0)).rgb
                + Frame.Sample(LinearSampler, ToFrame(uv) + float2(0, texelStep.y)).rgb
                + Frame.Sample(LinearSampler, ToFrame(uv) - float2(0, texelStep.y)).rgb;
    color += glow * 0.25 * bloom;

    // Make up for the light the mask and scanlines take away.
    return color * (1.0 + maskStrength * 0.6 + scanStrength * 0.3);
}

float4 PS(VsOut input) : SV_Target
{
    float2 uv = input.Uv;
    float2 pixel = input.Position.xy;

    if (Mode == 0)
    {
        return float4(Frame.Sample(PointSampler, ToFrame(uv)).rgb, 1.0);
    }

    if (Mode == 1)
    {
        return float4(Frame.Sample(LinearSampler, ToFrame(uv)).rgb, 1.0);
    }

    if (Mode == 2)
    {
        return float4(Crt(uv, pixel, 0.15, 0.35, 0.08), 1.0);
    }

    float2 curved = Curve(uv, 0.06);
    if (any(curved < 0.0) || any(curved > 1.0))
    {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    // Darken the corners like the edge of a tube.
    float2 edge = curved * (1.0 - curved);
    float vignette = saturate(pow(edge.x * edge.y * 30.0, 0.25));
    return float4(Crt(curved, pixel, 0.3, 0.5, 0.12) * vignette, 1.0);
}
