using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The vertex queue and what the rasterizer works with (ps2tek: "GS Primitives", "GS Vertex Attributes"); completed
/// primitives are queued (Ps2Gs.Batch.cs) and drawn by Ps2Gs.Renderer.cs. Coordinates are 12.4 fixed
/// point after XYOFFSET is subtracted; pixel (x, y) is sampled at the integer point (x, y) — the GS's pixel centres
/// are at integer coordinates — and triangles follow the top-left fill rule, so shared edges draw each pixel once.
/// Sprites cover [x0, x1) × [y0, y1) at those sample points. Lines step along their major axis over the half-open
/// span from the first vertex (so strips don't draw shared pixels twice), rounding the minor axis; points draw the
/// pixel nearest their position. Attributes are interpolated with plane equations in double precision.
/// </summary>
public sealed partial class Ps2Gs
{
    // Primitive types (PRIM bits 0–2).
    public const int PrimPoint = 0;
    public const int PrimLine = 1;
    public const int PrimLineStrip = 2;
    public const int PrimTriangle = 3;
    public const int PrimTriangleStrip = 4;
    public const int PrimTriangleFan = 5;
    public const int PrimSprite = 6;

    private readonly Vertex[] _queue = new Vertex[3];
    private int _queueCount;

    private struct Vertex
    {
        public int X;
        public int Y;
        public uint Z;
        public int R;
        public int G;
        public int B;
        public int A;
        public int F;
        public float S;
        public float T;
        public float Q;
        public int U;
        public int V;
    }

    /// <summary>A pixel's interpolated attributes; S, T, Q hold U, V (in 1/16 texels) and 1 for UV texturing.</summary>
    private struct Attr
    {
        public double R;
        public double G;
        public double B;
        public double A;
        public double Z;
        public double F;
        public double S;
        public double T;
        public double Q;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(in Attr d)
        {
            R += d.R;
            G += d.G;
            B += d.B;
            A += d.A;
            Z += d.Z;
            F += d.F;
            S += d.S;
            T += d.T;
            Q += d.Q;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddScaled(in Attr d, double k)
        {
            R += d.R * k;
            G += d.G * k;
            B += d.B * k;
            A += d.A * k;
            Z += d.Z * k;
            F += d.F * k;
            S += d.S * k;
            T += d.T * k;
            Q += d.Q * k;
        }
    }

    private void ResetDrawing()
    {
        _queueCount = 0;
        _drawStateDirty = true;
        DiscardBatch();
    }

    private static int VerticesNeeded(int prim) => prim switch
    {
        PrimPoint => 1,
        PrimLine or PrimLineStrip or PrimSprite => 2,
        PrimTriangle or PrimTriangleStrip or PrimTriangleFan => 3,
        _ => 0,
    };

    /// <summary>
    /// A vertex kick from XYZ(F)2 or XYZ(F)3. Only the "2" registers start drawing; the "3" ones advance the queue
    /// the same way without drawing (ps2tek: "GS Vertex Attributes").
    /// </summary>
    private void VertexKick(int x, int y, uint z, bool draw)
    {
        if (_drawStateDirty)
        {
            UpdateDrawState();
        }

        var prim = (int)(_regs[RegPrim] & 7);
        var needed = VerticesNeeded(prim);
        if (needed == 0)
        {
            _queueCount = 0;
            return;
        }

        ref var v = ref _queue[_queueCount];
        v.X = x - _state.Ofx;
        v.Y = y - _state.Ofy;
        v.Z = z;
        var rgbaq = _regs[RegRgbaq];
        v.R = (int)(rgbaq & 0xFF);
        v.G = (int)((rgbaq >> 8) & 0xFF);
        v.B = (int)((rgbaq >> 16) & 0xFF);
        v.A = (int)((rgbaq >> 24) & 0xFF);
        v.Q = BitConverter.UInt32BitsToSingle((uint)(rgbaq >> 32));

        // ST's low 8 mantissa bits are dropped (ps2tek: "GS Textures", ST).
        var st = _regs[RegSt];
        v.S = BitConverter.UInt32BitsToSingle((uint)st & 0xFFFF_FF00);
        v.T = BitConverter.UInt32BitsToSingle((uint)(st >> 32) & 0xFFFF_FF00);
        var uv = _regs[RegUv];
        v.U = (int)(uv & 0x3FFF);
        v.V = (int)((uv >> 16) & 0x3FFF);
        v.F = (int)(_regs[RegFog] >> 56);
        _queueCount++;
        if (_queueCount < needed)
        {
            return;
        }

        if (draw)
        {
            switch (prim)
            {
                case PrimPoint:
                    Enqueue(PrimPoint, in _queue[0], in _queue[0], in _queue[0]);
                    break;
                case PrimLine:
                case PrimLineStrip:
                    Enqueue(PrimLine, in _queue[0], in _queue[1], in _queue[1]);
                    break;
                case PrimTriangle:
                case PrimTriangleStrip:
                case PrimTriangleFan:
                    Enqueue(PrimTriangle, in _queue[0], in _queue[1], in _queue[2]);
                    break;
                case PrimSprite:
                    Enqueue(PrimSprite, in _queue[0], in _queue[1], in _queue[1]);
                    break;
            }
        }

        switch (prim)
        {
            case PrimLineStrip:
                _queue[0] = _queue[1];
                _queueCount = 1;
                break;
            case PrimTriangleStrip:
                _queue[0] = _queue[1];
                _queue[1] = _queue[2];
                _queueCount = 2;
                break;
            case PrimTriangleFan:
                _queue[1] = _queue[2];
                _queueCount = 2;
                break;
            default:
                _queueCount = 0;
                break;
        }
    }

    // ---- Save states ------------------------------------------------------------------------------------------

    private void SaveDrawing(StateWriter writer)
    {
        writer.WriteInt32(_queueCount);
        for (var i = 0; i < _queue.Length; i++)
        {
            ref var v = ref _queue[i];
            writer.WriteInt32(v.X);
            writer.WriteInt32(v.Y);
            writer.WriteUInt32(v.Z);
            writer.WriteInt32(v.R);
            writer.WriteInt32(v.G);
            writer.WriteInt32(v.B);
            writer.WriteInt32(v.A);
            writer.WriteInt32(v.F);
            writer.WriteUInt32(BitConverter.SingleToUInt32Bits(v.S));
            writer.WriteUInt32(BitConverter.SingleToUInt32Bits(v.T));
            writer.WriteUInt32(BitConverter.SingleToUInt32Bits(v.Q));
            writer.WriteInt32(v.U);
            writer.WriteInt32(v.V);
        }
    }

    private void LoadDrawing(StateReader reader)
    {
        _queueCount = reader.ReadInt32();
        if ((uint)_queueCount > (uint)_queue.Length)
        {
            throw new StateFormatException("The GS's vertex queue is out of range.");
        }

        for (var i = 0; i < _queue.Length; i++)
        {
            ref var v = ref _queue[i];
            v.X = reader.ReadInt32();
            v.Y = reader.ReadInt32();
            v.Z = reader.ReadUInt32();
            v.R = reader.ReadInt32();
            v.G = reader.ReadInt32();
            v.B = reader.ReadInt32();
            v.A = reader.ReadInt32();
            v.F = reader.ReadInt32();
            v.S = BitConverter.UInt32BitsToSingle(reader.ReadUInt32());
            v.T = BitConverter.UInt32BitsToSingle(reader.ReadUInt32());
            v.Q = BitConverter.UInt32BitsToSingle(reader.ReadUInt32());
            v.U = reader.ReadInt32();
            v.V = reader.ReadInt32();
        }
    }
}
