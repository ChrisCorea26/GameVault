namespace GameVault.Core.PlayStation;

/// <summary>A vertex as the GPU decoded it: its position (with the drawing offset), colour and texture coordinate.</summary>
public struct PsGpuVertex
{
    public int X;
    public int Y;
    public int R;
    public int G;
    public int B;
    public int U;
    public int V;
}

/// <summary>Everything a primitive draws with besides its vertices: the shading, the texture and the draw environment.</summary>
public struct PsGpuDraw
{
    public bool Textured;
    public bool Raw;
    public bool SemiTransparent;
    public bool Gouraud;
    public int SemiMode;
    public int PageX;
    public int PageY;

    /// <summary>The texture page's depth: 0 for 4-bit, 1 for 8-bit, 2 for 15-bit.</summary>
    public int Depth;
    public int ClutX;
    public int ClutY;

    /// <summary>GP0 $E2's texture window, as written.</summary>
    public uint TextureWindow;
    public int DrawLeft;
    public int DrawTop;
    public int DrawRight;
    public int DrawBottom;
    public bool SetMask;
    public bool CheckMask;
}

/// <summary>
/// Something that draws the GPU's primitives again, such as a GPU renderer at a higher resolution (gpu-renderers.md).
/// <see cref="PsGpu"/> calls it for each primitive before drawing it into its own VRAM, which stays authoritative: the
/// backend only makes a picture, and can read <see cref="PsGpu.Vram"/> for textures and uploads at any time.
/// </summary>
public interface IPsGpuBackend
{
    void Triangle(in PsGpuVertex a, in PsGpuVertex b, in PsGpuVertex c, in PsGpuDraw draw);

    /// <summary>A rectangle; <paramref name="corner"/> holds its top-left position, its colour and texture coordinate.</summary>
    void Rectangle(in PsGpuVertex corner, int width, int height, bool flipX, bool flipY, in PsGpuDraw draw);

    void Line(in PsGpuVertex from, in PsGpuVertex to, in PsGpuDraw draw);

    /// <summary>GP0 $02's fill (it ignores the drawing area and the mask), already in VRAM.</summary>
    void Fill(int x, int y, int width, int height);

    /// <summary>A CPU-to-VRAM transfer finished; its pixels are in VRAM.</summary>
    void Upload(int x, int y, int width, int height);

    /// <summary>A VRAM-to-VRAM copy, called before the GPU makes it.</summary>
    void Copy(int sourceX, int sourceY, int destinationX, int destinationY, int width, int height, bool setMask, bool checkMask);

    /// <summary>All of VRAM may have changed (a state was loaded): draw it again from <see cref="PsGpu.Vram"/>.</summary>
    void Resync();
}
