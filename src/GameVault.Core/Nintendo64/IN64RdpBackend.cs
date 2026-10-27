using System.Runtime.CompilerServices;

namespace GameVault.Core.Nintendo64;

/// <summary>
/// An optional second renderer for the RDP (docs/plans/gpu-renderers.md, milestone 2): the RDP hands it every primitive
/// with the state that draws it, and TMEM after every load, in command order. The software RDP stays authoritative
/// for RDRAM; a backend draws the picture only, so it changes nothing the emulated machine can see.
/// </summary>
public interface IN64RdpBackend
{
    /// <summary>A triangle, fill rectangle or texture rectangle, as the edge walker receives it, with its state.</summary>
    void Primitive(in N64RdpPrimitive primitive);

    /// <summary>TMEM after a Load Tile, Load Block or Load TLUT.</summary>
    void TmemLoaded(ReadOnlySpan<byte> tmem);

    /// <summary>
    /// The RDP finished the commands it was given and RDRAM holds everything it drew: the CPU may read or change RDRAM
    /// from here until the next command.
    /// </summary>
    void CommandsDone();

    /// <summary>The RDP's state was replaced (a save state was loaded): anything the backend kept may be stale.</summary>
    void Resync();
}

/// <summary>Eight attributes (R, G, B, A, S, T, W, Z) in s15.16.</summary>
[InlineArray(8)]
public struct N64RdpAttributes
{
    private int _element;
}

/// <summary>
/// A primitive as the edge walker takes it (n64brew: "Reality Display Processor/Commands" — Triangle): YL, YM and YH
/// in s11.2, the edges' X and slopes in s15.16, the attributes' start values on the major edge with their change per
/// pixel (Dx), along the major edge (De) and per scanline (Dy). Rectangles arrive as left-major primitives.
/// </summary>
public struct N64RdpEdges
{
    public bool Flip;
    public int MaxLevel;
    public int Tile;
    public int Yl;
    public int Ym;
    public int Yh;
    public int Xl;
    public int Xh;
    public int Xm;
    public int DxlDy;
    public int DxhDy;
    public int DxmDy;
    public N64RdpAttributes Value;
    public N64RdpAttributes Dx;
    public N64RdpAttributes De;
    public N64RdpAttributes Dy;
}

/// <summary>A tile: its Set Tile command word and its Set Tile Size coordinates (10.2 fixed point).</summary>
public struct N64RdpTile
{
    public ulong SetTile;
    public int Sl;
    public int Tl;
    public int Sh;
    public int Th;
}

[InlineArray(8)]
public struct N64RdpTiles
{
    private N64RdpTile _element;
}

/// <summary>A primitive with everything that decides how it draws: the commands' raw words, as the RDP received them.</summary>
public struct N64RdpPrimitive
{
    public N64RdpEdges Edges;
    public ulong OtherModes;
    public ulong Combine;
    public uint FillColor;
    public uint FogColor;
    public uint BlendColor;
    public uint PrimColor;
    public uint EnvColor;
    public int PrimLodFraction;
    public int PrimMinLevel;
    public int PrimZ;
    public int PrimDeltaZ;
    public ulong KeyGb;
    public ulong KeyR;
    public ulong Convert;
    public ulong Scissor;
    public int ColorFormat;
    public int ColorSize;
    public int ColorWidth;
    public uint ColorAddress;
    public uint ZAddress;
    public N64RdpTiles Tiles;
}
