using System.Runtime.CompilerServices;

namespace GameVault.Core.PlayStation2;

public sealed partial class Ps2Gs
{
    /// <summary>
    /// The rasterizer and the pixel pipeline (ps2tek: "GS Primitives", "GS Textures", "GS Fog", "GS Alpha Blending",
    /// "GS Tests and Pixel Control"), drawing queued primitives into local memory with the draw state each was queued
    /// with. Several renderers can draw one batch at once: each owns the rows of its band (16-row strips taken in
    /// turn), and does every per-row step for the other rows too, so a pixel's value never depends on how the rows were
    /// shared out. The GS only shares out work whose pixels touch separate memory (see Ps2Gs.Batch.cs).
    /// </summary>
    private sealed class Renderer(byte[] memory)
    {
        /// <summary>A band is every <see cref="Bands"/>th strip of 2^<see cref="BandShift"/> rows.</summary>
        public const int BandShift = 4;

        private readonly byte[] _memory = memory;
        private DrawState _s;
        private int _state = -1;
        private int _band;
        private int _bands = 1;

        /// <summary>Draws commands [<paramref name="start"/>, <paramref name="end"/>) of the batch in this band's rows.</summary>
        public void Execute(Command[] commands, DrawState[] states, int start, int end, int band, int bands)
        {
            _band = band;
            _bands = bands;
            _state = -1;
            for (var i = start; i < end; i++)
            {
                ref var c = ref commands[i];
                if (c.State != _state)
                {
                    _state = c.State;
                    _s = states[c.State];
                }

                switch (c.Kind)
                {
                    case PrimPoint:
                        DrawPoint(in c.A);
                        break;
                    case PrimLine:
                        DrawLine(in c.A, in c.B);
                        break;
                    case PrimSprite:
                        DrawSprite(in c.A, in c.B);
                        break;
                    default:
                        DrawTriangle(in c.A, in c.B, in c.C);
                        break;
                }
            }
        }

        /// <summary>Whether row <paramref name="y"/> is this renderer's to draw.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool MyRow(int y) => _bands == 1 || (int)((uint)(y >> BandShift) % (uint)_bands) == _band;

        private Attr Attributes(in Vertex v)
        {
            var a = default(Attr);
            a.R = v.R;
            a.G = v.G;
            a.B = v.B;
            a.A = v.A;
            a.Z = v.Z;
            a.F = v.F;
            if (_s.Fst)
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
            if (x < _s.Scax0 || x > _s.Scax1 || y < _s.Scay0 || y > _s.Scay1 || !MyRow(y))
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
                if (x < _s.Scax0 || x > _s.Scax1 || y < _s.Scay0 || y > _s.Scay1 || !MyRow(y))
                {
                    continue;
                }

                var a = a0;
                a.AddScaled(delta, t);
                if (!_s.Iip)
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

            var xs = Math.Max((x0 + 15) >> 4, _s.Scax0);
            var xe = Math.Min(((x1 + 15) >> 4) - 1, _s.Scax1);
            var ys = Math.Max((y0 + 15) >> 4, _s.Scay0);
            var ye = Math.Min(((y1 + 15) >> 4) - 1, _s.Scay1);
            if (xs > xe || ys > ye)
            {
                return;
            }

            if (!_s.Tme && !_s.Fge && CanFillFast())
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
                if (!MyRow(y))
                {
                    continue;
                }

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
            var xs = Math.Max((minX + 15) >> 4, _s.Scax0);
            var xe = Math.Min(maxX >> 4, _s.Scax1);
            var ys = Math.Max((minY + 15) >> 4, _s.Scay0);
            var ye = Math.Min(maxY >> 4, _s.Scay1);
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
            if (!_s.Iip)
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
                if (kMin <= kMax && MyRow(y))
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

        // ---- Texels -----------------------------------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint Expand16(uint c) => Ps2Gs.Expand16(c, in _s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint Expand24(uint c) => Ps2Gs.Expand24(c, in _s);

        private uint FetchTexel(int u, int v)
        {
            var raw = ReadPixel(_memory, _s.Tpsm, _s.Tbp, _s.Tbw, u, v);
            return _s.Tpsm switch
            {
                PsmCt32 or PsmZ32 => raw,
                PsmCt24 or PsmZ24 => Expand24(raw),
                PsmCt16 or PsmCt16S or PsmZ16 or PsmZ16S => Expand16(raw),
                _ => _s.Palette![raw & 0xFF],
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Wrap(int t, int mode, int size, int min, int max) => mode switch
        {
            0 => t & (size - 1),
            1 => t < 0 ? 0 : t >= size ? size - 1 : t,
            2 => t < min ? min : t > max ? max : t,
            _ => (t & min) | max,
        };

        /// <summary>A texel at 1/16-texel coordinates, nearest or bilinear (the four texels around the sample point).</summary>
        private uint SampleTexture(int u16, int v16, bool linear)
        {
            if (!linear)
            {
                return FetchTexel(Wrap(u16 >> 4, _s.Wms, _s.TexW, _s.Minu, _s.Maxu), Wrap(v16 >> 4, _s.Wmt, _s.TexH, _s.Minv, _s.Maxv));
            }

            u16 -= 8;
            v16 -= 8;
            var fu = u16 & 15;
            var fv = v16 & 15;
            var u0 = Wrap(u16 >> 4, _s.Wms, _s.TexW, _s.Minu, _s.Maxu);
            var u1 = Wrap((u16 >> 4) + 1, _s.Wms, _s.TexW, _s.Minu, _s.Maxu);
            var v0 = Wrap(v16 >> 4, _s.Wmt, _s.TexH, _s.Minv, _s.Maxv);
            var v1 = Wrap((v16 >> 4) + 1, _s.Wmt, _s.TexH, _s.Minv, _s.Maxv);
            var c00 = FetchTexel(u0, v0);
            var c10 = FetchTexel(u1, v0);
            var c01 = FetchTexel(u0, v1);
            var c11 = FetchTexel(u1, v1);
            uint result = 0;
            for (var shift = 0; shift < 32; shift += 8)
            {
                var a = (int)((c00 >> shift) & 0xFF);
                var b = (int)((c10 >> shift) & 0xFF);
                var c = (int)((c01 >> shift) & 0xFF);
                var d = (int)((c11 >> shift) & 0xFF);
                var top = (a * (16 - fu)) + (b * fu);
                var bottom = (c * (16 - fu)) + (d * fu);
                result |= (uint)(((top * (16 - fv)) + (bottom * fv)) >> 8) << shift;
            }

            return result;
        }

        // ---- Fragments --------------------------------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ToByte(double value) => value <= 0 ? 0 : value >= 255 ? 255 : (int)value;

        private static int ToTexCoord(double value) =>
            value is >= -(1 << 24) and <= 1 << 24 ? (int)Math.Floor(value) : value > 0 ? 1 << 24 : -(1 << 24);

        /// <summary>Shades one sampled pixel from its interpolated attributes and sends it through the tests.</summary>
        private void ShadeFragment(int x, int y, ref Attr attr)
        {
            var r = ToByte(attr.R);
            var g = ToByte(attr.G);
            var b = ToByte(attr.B);
            var a = ToByte(attr.A);
            var zf = attr.Z;
            var z = zf <= 0 ? 0u : zf >= uint.MaxValue ? uint.MaxValue : (uint)zf;

            if (_s.Tme)
            {
                int u16, v16;
                var q = attr.Q;
                if (_s.Fst)
                {
                    u16 = ToTexCoord(attr.S);
                    v16 = ToTexCoord(attr.T);
                }
                else
                {
                    var scale = 16 / q;
                    u16 = ToTexCoord(attr.S * scale * _s.TexW);
                    v16 = ToTexCoord(attr.T * scale * _s.TexH);
                }

                bool linear;
                if (_s.MagLinear == _s.MinLinear)
                {
                    linear = _s.MagLinear;
                }
                else
                {
                    // LOD = K when LCM is set, otherwise (log2(1/|Q|) << L) + K; above 0 minifies (level 0 only).
                    var lod = _s.Lcm ? _s.LodK : (Math.Log2(1 / Math.Abs(q)) * (1 << _s.LodL)) + _s.LodK;
                    linear = lod > 0 ? _s.MinLinear : _s.MagLinear;
                }

                var tex = SampleTexture(u16, v16, linear);
                var tr = (int)(tex & 0xFF);
                var tg = (int)((tex >> 8) & 0xFF);
                var tb = (int)((tex >> 16) & 0xFF);
                var ta = (int)(tex >> 24);
                switch (_s.Tfx)
                {
                    case 0: // MODULATE
                        r = Math.Min((r * tr) >> 7, 255);
                        g = Math.Min((g * tg) >> 7, 255);
                        b = Math.Min((b * tb) >> 7, 255);
                        if (_s.Tcc)
                        {
                            a = Math.Min((a * ta) >> 7, 255);
                        }

                        break;
                    case 1: // DECAL
                        r = tr;
                        g = tg;
                        b = tb;
                        if (_s.Tcc)
                        {
                            a = ta;
                        }

                        break;
                    default: // HIGHLIGHT, HIGHLIGHT2
                        r = Math.Min(((r * tr) >> 7) + a, 255);
                        g = Math.Min(((g * tg) >> 7) + a, 255);
                        b = Math.Min(((b * tb) >> 7) + a, 255);
                        if (_s.Tcc)
                        {
                            a = _s.Tfx == 2 ? Math.Min(ta + a, 255) : ta;
                        }

                        break;
                }
            }

            if (_s.Fge)
            {
                var f = ToByte(attr.F);
                r = ((f * r) + ((255 - f) * _s.FogR)) >> 8;
                g = ((f * g) + ((255 - f) * _s.FogG)) >> 8;
                b = ((f * b) + ((255 - f) * _s.FogB)) >> 8;
            }

            WriteFragment(x, y, z, r, g, b, a);
        }

        private bool AlphaPasses(int a) => _s.Atst switch
        {
            0 => false,
            1 => true,
            2 => a < _s.Aref,
            3 => a <= _s.Aref,
            4 => a == _s.Aref,
            5 => a >= _s.Aref,
            6 => a > _s.Aref,
            _ => a != _s.Aref,
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int FrameAddress(int x, int y) =>
            _s.Fb16 ? Address16(_s.FbTable, _s.Fbp, _s.Fbw, x, y) : Address32(_s.FbTable, _s.Fbp, _s.Fbw, x, y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ZAddress(int x, int y) =>
            _s.Z16 ? Address16(_s.ZTable, _s.Zbp, _s.Fbw, x, y) : Address32(_s.ZTable, _s.Zbp, _s.Fbw, x, y);

        private uint ReadZ(int address) =>
            _s.Z16 ? Read16(_memory, address)
            : Read32(_memory, address) & _s.ZMax;

        private void WriteZ(int address, uint z)
        {
            if (_s.Z16)
            {
                Write16(_memory, address, (ushort)z);
            }
            else if (_s.Z24)
            {
                Write24(_memory, address, z);
            }
            else
            {
                Write32(_memory, address, z);
            }
        }

        /// <summary>The frame pixel as RGBA8888; a 24-bit frame reads alpha 0x80, a 16-bit one 0x80 or 0.</summary>
        private uint ReadFrameColor(int address)
        {
            if (_s.Fb16)
            {
                uint c = Read16(_memory, address);
                return ((c & 0x1F) << 3) | ((c & 0x3E0) << 6) | ((c & 0x7C00) << 9) | ((c & 0x8000) != 0 ? 0x8000_0000u : 0);
            }

            var v = Read32(_memory, address);
            return _s.Fb24 ? (v & 0xFFFFFF) | 0x8000_0000u : v;
        }

        /// <summary>Writes RGBA8888 under FBMSK (a set bit keeps the old bit), converting for 16- and 24-bit frames.</summary>
        private void WriteFrameColor(int address, uint color, uint mask)
        {
            if (_s.Fb16)
            {
                var c16 = ((color >> 3) & 0x1F) | ((color >> 6) & 0x3E0) | ((color >> 9) & 0x7C00) | ((color >> 16) & 0x8000);
                var m16 = ((mask >> 3) & 0x1F) | ((mask >> 6) & 0x3E0) | ((mask >> 9) & 0x7C00) | ((mask >> 16) & 0x8000);
                if (m16 != 0)
                {
                    c16 = (c16 & ~m16) | (Read16(_memory, address) & m16);
                }

                Write16(_memory, address, (ushort)c16);
                return;
            }

            if (_s.Fb24)
            {
                mask |= 0xFF00_0000;
            }

            if (mask != 0)
            {
                color = (color & ~mask) | (Read32(_memory, address) & mask);
            }

            Write32(_memory, address, color);
        }

        /// <summary>The tests, the blend and the writes for one coloured fragment.</summary>
        private void WriteFragment(int x, int y, uint z, int r, int g, int b, int a)
        {
            if ((y & 1) == _s.ScanmskSkip)
            {
                return;
            }

            var fbWrite = true;
            var zWrite = _s.Zte && !_s.Zmsk;
            var mask = _s.Fbmsk;
            if (_s.Ate && !AlphaPasses(a))
            {
                switch (_s.Afail)
                {
                    case 0: // KEEP
                        return;
                    case 1: // FB_ONLY
                        zWrite = false;
                        break;
                    case 2: // ZB_ONLY
                        fbWrite = false;
                        break;
                    default: // RGB_ONLY
                        zWrite = false;
                        mask |= 0xFF00_0000;
                        break;
                }
            }

            // ZTE = 0 is documented as prohibited; it is taken as "no depth test and no depth write".
            var zAddress = 0;
            if (_s.Zte)
            {
                if (z > _s.ZMax)
                {
                    z = _s.ZMax;
                }

                switch (_s.Ztst)
                {
                    case 0:
                        return;
                    case 1:
                        if (zWrite)
                        {
                            zAddress = ZAddress(x, y);
                        }

                        break;
                    default:
                    {
                        zAddress = ZAddress(x, y);
                        var stored = ReadZ(zAddress);
                        if (_s.Ztst == 2 ? z < stored : z <= stored)
                        {
                            return;
                        }

                        break;
                    }
                }
            }

            var fbAddress = FrameAddress(x, y);
            var needDest = _s.Date || _s.Abe;
            var dest = needDest ? ReadFrameColor(fbAddress) : 0;
            if (_s.Date && !_s.Fb24 && (int)(dest >> 31) != _s.Datm)
            {
                return;
            }

            if (zWrite)
            {
                WriteZ(zAddress, z);
            }

            if (!fbWrite)
            {
                return;
            }

            if (_s.Abe && !(_s.Pabe && (a & 0x80) == 0))
            {
                var dr = (int)(dest & 0xFF);
                var dg = (int)((dest >> 8) & 0xFF);
                var db = (int)((dest >> 16) & 0xFF);
                var da = (int)(dest >> 24);
                var c = _s.BlendC switch { 0 => a, 1 => da, _ => _s.BlendFix };
                r = Blend(r, dr, c);
                g = Blend(g, dg, c);
                b = Blend(b, db, c);
            }

            if (_s.Dthe && _s.Fb16)
            {
                var d = _s.Dimx[((y & 3) << 2) | (x & 3)];
                r += d;
                g += d;
                b += d;
            }

            if (_s.Colclamp)
            {
                r = r < 0 ? 0 : r > 255 ? 255 : r;
                g = g < 0 ? 0 : g > 255 ? 255 : g;
                b = b < 0 ? 0 : b > 255 ? 255 : b;
            }
            else
            {
                r &= 0xFF;
                g &= 0xFF;
                b &= 0xFF;
            }

            if (_s.Fba)
            {
                a |= 0x80;
            }

            WriteFrameColor(fbAddress, (uint)r | ((uint)g << 8) | ((uint)b << 16) | ((uint)(a & 0xFF) << 24), mask);
        }

        /// <summary>((A − B) × C &gt;&gt; 7) + D on one channel (ps2tek: "GS Alpha Blending").</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Blend(int source, int dest, int c)
        {
            var av = _s.BlendA switch { 0 => source, 1 => dest, _ => 0 };
            var bv = _s.BlendB switch { 0 => source, 1 => dest, _ => 0 };
            var dv = _s.BlendD switch { 0 => source, 1 => dest, _ => 0 };
            return (((av - bv) * c) >> 7) + dv;
        }

        // ---- Fast paths -------------------------------------------------------------------------------------------

        /// <summary>An untextured sprite whose pixels don't depend on what is underneath can be stored directly.</summary>
        private bool CanFillFast() =>
            !_s.Abe && (!_s.Ate || _s.Atst == 1) && !_s.Date && (!_s.Zte || _s.Ztst == 1) && !(_s.Dthe && _s.Fb16);

        private void FillSpriteFast(int xs, int xe, int ys, int ye, in Vertex v)
        {
            var a = _s.Fba ? v.A | 0x80 : v.A;
            var color = (uint)v.R | ((uint)v.G << 8) | ((uint)v.B << 16) | ((uint)(a & 0xFF) << 24);
            var writeZ = _s.Zte && !_s.Zmsk;
            var z = Math.Min(v.Z, _s.ZMax);
            for (var y = ys; y <= ye; y++)
            {
                if ((y & 1) == _s.ScanmskSkip || !MyRow(y))
                {
                    continue;
                }

                for (var x = xs; x <= xe; x++)
                {
                    WriteFrameColor(FrameAddress(x, y), color, _s.Fbmsk);
                    if (writeZ)
                    {
                        WriteZ(ZAddress(x, y), z);
                    }
                }
            }
        }
    }
}
