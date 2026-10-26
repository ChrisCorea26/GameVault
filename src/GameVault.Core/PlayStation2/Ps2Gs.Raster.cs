using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The vertex queue and the rasterizer (ps2tek: "GS Primitives", "GS Vertex Attributes"). Coordinates are 12.4 fixed
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
        v.X = x - _ofx;
        v.Y = y - _ofy;
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
                    DrawPoint(in _queue[0]);
                    break;
                case PrimLine:
                case PrimLineStrip:
                    DrawLine(in _queue[0], in _queue[1]);
                    break;
                case PrimTriangle:
                case PrimTriangleStrip:
                case PrimTriangleFan:
                    DrawTriangle(in _queue[0], in _queue[1], in _queue[2]);
                    break;
                case PrimSprite:
                    DrawSprite(in _queue[0], in _queue[1]);
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

    private Attr Attributes(in Vertex v)
    {
        var a = default(Attr);
        a.R = v.R;
        a.G = v.G;
        a.B = v.B;
        a.A = v.A;
        a.Z = v.Z;
        a.F = v.F;
        if (_fst)
        {
            a.S = v.U;
            a.T = v.V;
            a.Q = 1;
        }
        else
        {
            a.S = v.S;
            a.T = v.T;
            a.Q = v.Q;
        }

        return a;
    }

    /// <summary>Flat shading: every pixel takes the colour of <paramref name="v"/>.</summary>
    private static void Flatten(ref Attr a, in Vertex v)
    {
        a.R = v.R;
        a.G = v.G;
        a.B = v.B;
        a.A = v.A;
    }

    // ---- Points -----------------------------------------------------------------------------------------------

    private void DrawPoint(in Vertex v)
    {
        var x = (v.X + 8) >> 4;
        var y = (v.Y + 8) >> 4;
        if (x < _scax0 || x > _scax1 || y < _scay0 || y > _scay1)
        {
            return;
        }

        var a = Attributes(v);
        ShadeFragment(x, y, ref a);
    }

    // ---- Lines ------------------------------------------------------------------------------------------------

    private void DrawLine(in Vertex v0, in Vertex v1)
    {
        var dx = v1.X - v0.X;
        var dy = v1.Y - v0.Y;
        if (dx == 0 && dy == 0)
        {
            return;
        }

        var a0 = Attributes(v0);
        var delta = Attributes(v1);
        delta.AddScaled(a0, -1);
        var xMajor = Math.Abs(dx) >= Math.Abs(dy);
        var p0 = xMajor ? v0.X : v0.Y;
        var p1 = xMajor ? v1.X : v1.Y;
        var q0 = xMajor ? v0.Y : v0.X;
        var dq = xMajor ? dy : dx;
        var dp = p1 - p0;
        int first, last, step;
        if (dp > 0)
        {
            first = (p0 + 15) >> 4;
            last = ((p1 + 15) >> 4) - 1;
            step = 1;
        }
        else
        {
            first = p0 >> 4;
            last = (p1 >> 4) + 1;
            step = -1;
        }

        for (var p = first; step > 0 ? p <= last : p >= last; p += step)
        {
            var t = ((p * 16.0) - p0) / dp;
            var q = (int)Math.Floor((q0 + (t * dq) + 8) / 16.0);
            var x = xMajor ? p : q;
            var y = xMajor ? q : p;
            if (x < _scax0 || x > _scax1 || y < _scay0 || y > _scay1)
            {
                continue;
            }

            var a = a0;
            a.AddScaled(delta, t);
            if (!_iip)
            {
                Flatten(ref a, v1);
            }

            ShadeFragment(x, y, ref a);
        }
    }

    // ---- Sprites ----------------------------------------------------------------------------------------------

    /// <summary>A sprite: an axis-aligned rectangle; colour, Z and fog come from its second vertex.</summary>
    private void DrawSprite(in Vertex v0, in Vertex v1)
    {
        var x0 = v0.X;
        var x1 = v1.X;
        var y0 = v0.Y;
        var y1 = v1.Y;
        var a0 = Attributes(v0);
        var a = Attributes(v1);
        double s0 = a0.S, s1 = a.S, t0 = a0.T, t1 = a.T;
        if (x0 > x1)
        {
            (x0, x1) = (x1, x0);
            (s0, s1) = (s1, s0);
        }

        if (y0 > y1)
        {
            (y0, y1) = (y1, y0);
            (t0, t1) = (t1, t0);
        }

        var xs = Math.Max((x0 + 15) >> 4, _scax0);
        var xe = Math.Min(((x1 + 15) >> 4) - 1, _scax1);
        var ys = Math.Max((y0 + 15) >> 4, _scay0);
        var ye = Math.Min(((y1 + 15) >> 4) - 1, _scay1);
        if (xs > xe || ys > ye)
        {
            return;
        }

        if (!_tme && !_fge && CanFillFast())
        {
            FillSpriteFast(xs, xe, ys, ye, v1);
            return;
        }

        var sPerUnit = x1 != x0 ? (s1 - s0) / (x1 - x0) : 0;
        var tPerUnit = y1 != y0 ? (t1 - t0) / (y1 - y0) : 0;
        var sStart = s0 + (sPerUnit * ((xs * 16) - x0));
        var ds = sPerUnit * 16;
        var dt = tPerUnit * 16;
        a.T = t0 + (tPerUnit * ((ys * 16) - y0));
        for (var y = ys; y <= ye; y++, a.T += dt)
        {
            a.S = sStart;
            for (var x = xs; x <= xe; x++, a.S += ds)
            {
                ShadeFragment(x, y, ref a);
            }
        }
    }

    // ---- Triangles --------------------------------------------------------------------------------------------

    private void DrawTriangle(in Vertex a, in Vertex b, in Vertex c)
    {
        var v0 = a;
        var v1 = b;
        var v2 = c;
        var area = ((long)(v1.X - v0.X) * (v2.Y - v0.Y)) - ((long)(v1.Y - v0.Y) * (v2.X - v0.X));
        if (area == 0)
        {
            return;
        }

        if (area < 0)
        {
            (v1, v2) = (v2, v1);
            area = -area;
        }

        var minX = Math.Min(v0.X, Math.Min(v1.X, v2.X));
        var maxX = Math.Max(v0.X, Math.Max(v1.X, v2.X));
        var minY = Math.Min(v0.Y, Math.Min(v1.Y, v2.Y));
        var maxY = Math.Max(v0.Y, Math.Max(v1.Y, v2.Y));
        var xs = Math.Max((minX + 15) >> 4, _scax0);
        var xe = Math.Min(maxX >> 4, _scax1);
        var ys = Math.Max((minY + 15) >> 4, _scay0);
        var ye = Math.Min(maxY >> 4, _scay1);
        if (xs > xe || ys > ye)
        {
            return;
        }

        // Edge functions E(p) = (b-a) × (p-a), positive inside; top and left edges include their own pixels.
        Edge(v1, v2, xs, ys, out var w0Row, out var w0Dx, out var w0Dy);
        Edge(v2, v0, xs, ys, out var w1Row, out var w1Dx, out var w1Dy);
        Edge(v0, v1, xs, ys, out var w2Row, out var w2Dx, out var w2Dy);

        // Attribute planes: value = v0 + ddx·(x − x0) + ddy·(y − y0), stepped per pixel and per row.
        var a0 = Attributes(v0);
        var d1 = Attributes(v1);
        var d2 = Attributes(v2);
        d1.AddScaled(a0, -1);
        d2.AddScaled(a0, -1);
        var inv = 16.0 / area;
        var stepX = default(Attr);
        stepX.AddScaled(d1, (v2.Y - v0.Y) * inv);
        stepX.AddScaled(d2, -(v1.Y - v0.Y) * inv);
        var stepY = default(Attr);
        stepY.AddScaled(d2, (v1.X - v0.X) * inv);
        stepY.AddScaled(d1, -(v2.X - v0.X) * inv);
        var row = a0;
        row.AddScaled(stepX, ((xs * 16.0) - v0.X) / 16);
        row.AddScaled(stepY, ((ys * 16.0) - v0.Y) / 16);
        if (!_iip)
        {
            // Flat shading takes the colour of the vertex that completed the triangle.
            Flatten(ref row, c);
            stepX.R = stepX.G = stepX.B = stepX.A = 0;
            stepY.R = stepY.G = stepY.B = stepY.A = 0;
        }

        var width = xe - xs;
        for (var y = ys; y <= ye; y++)
        {
            // The span of this row where all three edge functions are positive.
            var kMin = 0;
            var kMax = width;
            SpanLimit(w0Row, w0Dx, ref kMin, ref kMax);
            SpanLimit(w1Row, w1Dx, ref kMin, ref kMax);
            SpanLimit(w2Row, w2Dx, ref kMin, ref kMax);
            if (kMin <= kMax)
            {
                var cur = row;
                cur.AddScaled(stepX, kMin);
                for (var x = xs + kMin; x <= xs + kMax; x++)
                {
                    ShadeFragment(x, y, ref cur);
                    cur.Add(stepX);
                }
            }

            w0Row += w0Dy;
            w1Row += w1Dy;
            w2Row += w2Dy;
            row.Add(stepY);
        }
    }

    /// <summary>Narrows [kMin, kMax] to the steps k where w + k·step &gt; 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SpanLimit(long w, long step, ref int kMin, ref int kMax)
    {
        if (step > 0)
        {
            if (w <= 0)
            {
                var k = ((-w) / step) + 1;
                if (k > kMin)
                {
                    kMin = k > int.MaxValue ? int.MaxValue : (int)k;
                }
            }
        }
        else if (step < 0)
        {
            if (w <= 0)
            {
                kMax = -1;
                return;
            }

            var k = (w - 1) / -step;
            if (k < kMax)
            {
                kMax = (int)k;
            }
        }
        else if (w <= 0)
        {
            kMax = -1;
        }
    }

    /// <summary>
    /// The edge function of a→b at pixel (<paramref name="xs"/>, <paramref name="ys"/>) and its steps per pixel. A
    /// top-left edge gets +1 so that points exactly on it pass the "&gt; 0" test.
    /// </summary>
    private static void Edge(in Vertex a, in Vertex b, int xs, int ys, out long start, out long stepX, out long stepY)
    {
        long dx = b.X - a.X;
        long dy = b.Y - a.Y;
        var topLeft = dy < 0 || (dy == 0 && dx > 0);
        start = (dx * ((ys * 16L) - a.Y)) - (dy * ((xs * 16L) - a.X)) + (topLeft ? 1 : 0);
        stepX = -dy * 16;
        stepY = dx * 16;
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
