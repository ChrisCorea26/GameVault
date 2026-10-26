namespace GameVault.Core.PlayStation;

public sealed partial class PsGpu
{
    /// <summary>The 4×4 dither offsets added to 8-bit colours before they drop to 5 bits (psx-spx: "GPU Dithering").</summary>
    private static readonly int[] DitherTable =
    [
        -4, +0, -3, +1,
        +2, -2, +3, -1,
        -3, +1, -4, +0,
        +3, -1, +2, -2,
    ];

    private struct Vertex
    {
        public int X;
        public int Y;
        public int R;
        public int G;
        public int B;
        public int U;
        public int V;
    }

    /// <summary>What a primitive draws with, decoded once per command.</summary>
    private struct Shading
    {
        public bool Textured;
        public bool Raw;
        public bool SemiTransparent;
        public bool Gouraud;
        public bool Dither;
        public int SemiMode;
        public int PageX;
        public int PageY;
        public int Depth; // 0: 4-bit, 1: 8-bit, 2: 15-bit
        public int ClutX;
        public int ClutY;
    }

    // ---- Polygons ----

    private void DrawPolygon()
    {
        var op = _command[0] >> 24;
        var gouraud = (op & 0x10) != 0;
        var quad = (op & 0x08) != 0;
        var textured = (op & 0x04) != 0;

        var shading = new Shading
        {
            Textured = textured,
            Raw = textured && (op & 0x01) != 0,
            SemiTransparent = (op & 0x02) != 0,
            Gouraud = gouraud,
        };

        Span<Vertex> v = stackalloc Vertex[4];
        var count = quad ? 4 : 3;
        var index = 0;
        var color = _command[index++];
        for (var i = 0; i < count; i++)
        {
            if (gouraud && i > 0)
            {
                color = _command[index++];
            }

            var position = _command[index++];
            v[i].X = SignExtend11(position) + _offsetX;
            v[i].Y = SignExtend11(position >> 16) + _offsetY;
            v[i].R = (int)(color & 0xFF);
            v[i].G = (int)((color >> 8) & 0xFF);
            v[i].B = (int)((color >> 16) & 0xFF);
            if (textured)
            {
                var texture = _command[index++];
                v[i].U = (int)(texture & 0xFF);
                v[i].V = (int)((texture >> 8) & 0xFF);
                if (i == 0)
                {
                    shading.ClutX = (int)((texture >> 16) & 0x3F) * 16;
                    shading.ClutY = (int)((texture >> 22) & 0x1FF);
                }
                else if (i == 1)
                {
                    // A textured polygon's page also becomes the draw mode's page (psx-spx: "GPU Render Polygon").
                    var page = (texture >> 16) & 0x9FF;
                    _drawMode = (_drawMode & ~0x9FFu) | page;
                }
            }
        }

        ApplyDrawMode(ref shading);

        // Dithering applies to Gouraud shading and to modulated textures, when the draw mode enables it.
        shading.Dither = (_drawMode & 0x200) != 0 && (gouraud || (textured && !shading.Raw));

        DrawTriangle(v[0], v[1], v[2], shading);
        if (quad)
        {
            DrawTriangle(v[1], v[2], v[3], shading);
        }
    }

    private void ApplyDrawMode(ref Shading shading)
    {
        shading.PageX = (int)(_drawMode & 0xF) * 64;
        shading.PageY = (int)((_drawMode >> 4) & 1) * 256;
        shading.SemiMode = (int)((_drawMode >> 5) & 3);
        shading.Depth = (int)((_drawMode >> 7) & 3);
    }

    private void DrawTriangle(Vertex a, Vertex b, Vertex c, in Shading shading)
    {
        var area = Edge(a.X, a.Y, b.X, b.Y, c.X, c.Y);
        if (area == 0)
        {
            return;
        }

        if (area < 0)
        {
            (b, c) = (c, b);
            area = -area;
        }

        var minX = Math.Min(a.X, Math.Min(b.X, c.X));
        var maxX = Math.Max(a.X, Math.Max(b.X, c.X));
        var minY = Math.Min(a.Y, Math.Min(b.Y, c.Y));
        var maxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));

        // The GPU skips polygons wider than 1023 or taller than 511 (psx-spx).
        if (maxX - minX >= 1024 || maxY - minY >= 512)
        {
            return;
        }

        minX = Math.Max(minX, _drawLeft);
        minY = Math.Max(minY, _drawTop);
        maxX = Math.Min(maxX, _drawRight);
        maxY = Math.Min(maxY, _drawBottom);

        // Top-left rule: pixels on a right or bottom edge aren't drawn.
        var biasA = TopLeft(b, c) ? 0 : -1;
        var biasB = TopLeft(c, a) ? 0 : -1;
        var biasC = TopLeft(a, b) ? 0 : -1;

        // Attributes step by per-polygon gradients from a "core" vertex, in 12-bit fixed point with the gradients
        // truncated, as the hardware's interpolators do; Mednafen's model of them matches VRAM dumps from a real
        // console (ps1-tests gpu/triangle).
        var core = CoreVertex(a, b, c);
        var denominator = (long)(b.X - a.X) * (c.Y - b.Y) - (long)(c.X - b.X) * (b.Y - a.Y);
        var gradients = new Gradients(a, b, c, denominator);

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var wa = Edge(b.X, b.Y, c.X, c.Y, x, y);
                var wb = Edge(c.X, c.Y, a.X, a.Y, x, y);
                var wc = Edge(a.X, a.Y, b.X, b.Y, x, y);
                if (wa + biasA < 0 || wb + biasB < 0 || wc + biasC < 0)
                {
                    continue;
                }

                var dx = x - core.X;
                var dy = y - core.Y;
                int r, g, bl, u = 0, v = 0;
                if (shading.Gouraud)
                {
                    r = Step(core.R, gradients.DrDx, gradients.DrDy, dx, dy);
                    g = Step(core.G, gradients.DgDx, gradients.DgDy, dx, dy);
                    bl = Step(core.B, gradients.DbDx, gradients.DbDy, dx, dy);
                }
                else
                {
                    r = a.R;
                    g = a.G;
                    bl = a.B;
                }

                if (shading.Textured)
                {
                    u = Step(core.U, gradients.DuDx, gradients.DuDy, dx, dy);
                    v = Step(core.V, gradients.DvDx, gradients.DvDy, dx, dy);
                }

                Plot(x, y, r, g, bl, u, v, shading);
            }
        }
    }

    /// <summary>Per-polygon attribute gradients in 12-bit fixed point, truncated towards zero.</summary>
    private readonly struct Gradients
    {
        public readonly int DrDx, DrDy, DgDx, DgDy, DbDx, DbDy, DuDx, DuDy, DvDx, DvDy;

        public Gradients(in Vertex a, in Vertex b, in Vertex c, long denominator)
        {
            DrDx = GradientX(a.R, b.R, c.R, a, b, c, denominator);
            DrDy = GradientY(a.R, b.R, c.R, a, b, c, denominator);
            DgDx = GradientX(a.G, b.G, c.G, a, b, c, denominator);
            DgDy = GradientY(a.G, b.G, c.G, a, b, c, denominator);
            DbDx = GradientX(a.B, b.B, c.B, a, b, c, denominator);
            DbDy = GradientY(a.B, b.B, c.B, a, b, c, denominator);
            DuDx = GradientX(a.U, b.U, c.U, a, b, c, denominator);
            DuDy = GradientY(a.U, b.U, c.U, a, b, c, denominator);
            DvDx = GradientX(a.V, b.V, c.V, a, b, c, denominator);
            DvDy = GradientY(a.V, b.V, c.V, a, b, c, denominator);
        }

        private static int GradientX(int va, int vb, int vc, in Vertex a, in Vertex b, in Vertex c, long denominator) =>
            (int)(((long)(vb - va) * (c.Y - b.Y) - (long)(vc - vb) * (b.Y - a.Y)) * 4096 / denominator);

        private static int GradientY(int va, int vb, int vc, in Vertex a, in Vertex b, in Vertex c, long denominator) =>
            (int)(((long)(b.X - a.X) * (vc - vb) - (long)(c.X - b.X) * (vb - va)) * 4096 / denominator);
    }

    /// <summary>An attribute at an offset from the core vertex: its value plus half a step, plus the gradients, wrapped to 8 bits.</summary>
    private static int Step(int start, int perX, int perY, int dx, int dy) =>
        (int)((((long)start << 12) + 0x800 + (long)perX * dx + (long)perY * dy) >> 12) & 0xFF;

    /// <summary>The vertex the interpolation starts from: the leftmost (the first of equals), as in Mednafen's model.</summary>
    private static Vertex CoreVertex(in Vertex a, in Vertex b, in Vertex c) =>
        b.X < a.X ? (c.X < b.X ? c : b) : (c.X < a.X ? c : a);

    private static long Edge(int ax, int ay, int bx, int by, int px, int py) =>
        (long)(bx - ax) * (py - ay) - (long)(by - ay) * (px - ax);

    private static bool TopLeft(in Vertex from, in Vertex to)
    {
        var dy = to.Y - from.Y;
        return dy < 0 || (dy == 0 && to.X - from.X > 0);
    }

    // ---- Lines ----

    private void DrawLine()
    {
        var op = _command[0] >> 24;
        var gouraud = (op & 0x10) != 0;
        var color0 = _command[0];
        var p0 = _command[1];
        var color1 = gouraud ? _command[2] : color0;
        var p1 = gouraud ? _command[3] : _command[2];
        DrawLineSegment(p0, color0, p1, color1);
    }

    private void DrawLineSegment(uint from, uint fromColor, uint to, uint toColor)
    {
        var op = _command[0] >> 24;
        var shading = new Shading { SemiTransparent = (op & 0x02) != 0, Gouraud = (op & 0x10) != 0 };
        ApplyDrawMode(ref shading);
        // Lines dither whenever the draw mode enables it, flat ones too (ps1-tests gpu/lines, hardware VRAM dump).
        shading.Dither = (_drawMode & 0x200) != 0;

        var x0 = SignExtend11(from) + _offsetX;
        var y0 = SignExtend11(from >> 16) + _offsetY;
        var x1 = SignExtend11(to) + _offsetX;
        var y1 = SignExtend11(to >> 16) + _offsetY;
        var dx = Math.Abs(x1 - x0);
        var dy = Math.Abs(y1 - y0);
        if (dx >= 1024 || dy >= 512)
        {
            return;
        }

        if (!shading.Gouraud)
        {
            toColor = fromColor;
        }

        // Lines step along the longer axis in 32.32 fixed point, always from left to right, with colours in 12-bit
        // fixed point; Mednafen's model of the hardware's line unit (psx-spx: "GPU Render Line Commands").
        var k = Math.Max(dx, dy);
        if (x0 >= x1 && k != 0)
        {
            (x0, x1) = (x1, x0);
            (y0, y1) = (y1, y0);
            (fromColor, toColor) = (toColor, fromColor);
        }

        var stepX = LineDivide(x1 - x0, k);
        var stepY = LineDivide(y1 - y0, k);
        var curX = ((long)x0 << 32) + (1L << 31) - 1024;
        var curY = ((long)y0 << 32) + (1L << 31) - (stepY < 0 ? 1024 : 0);

        int r0 = (int)(fromColor & 0xFF), g0 = (int)((fromColor >> 8) & 0xFF), b0 = (int)((fromColor >> 16) & 0xFF);
        int r1 = (int)(toColor & 0xFF), g1 = (int)((toColor >> 8) & 0xFF), b1 = (int)((toColor >> 16) & 0xFF);
        var stepR = k == 0 ? 0 : ((r1 - r0) << 12) / k;
        var stepG = k == 0 ? 0 : ((g1 - g0) << 12) / k;
        var stepB = k == 0 ? 0 : ((b1 - b0) << 12) / k;
        var curR = (r0 << 12) | 0x800;
        var curG = (g0 << 12) | 0x800;
        var curB = (b0 << 12) | 0x800;

        for (var i = 0; i <= k; i++)
        {
            var x = (int)(curX >> 32);
            var y = (int)(curY >> 32);
            if (x >= _drawLeft && x <= _drawRight && y >= _drawTop && y <= _drawBottom)
            {
                Plot(x, y, curR >> 12, curG >> 12, curB >> 12, 0, 0, shading);
            }

            curX += stepX;
            curY += stepY;
            curR += stepR;
            curG += stepG;
            curB += stepB;
        }
    }

    /// <summary>A line's step per pixel in 32.32 fixed point, rounded away from zero.</summary>
    private static long LineDivide(int delta, int k)
    {
        if (k == 0)
        {
            return 0;
        }

        var value = (long)delta << 32;
        if (value < 0)
        {
            value -= k - 1;
        }
        else if (value > 0)
        {
            value += k - 1;
        }

        return value / k;
    }

    // ---- Rectangles ----

    private void DrawRectangle()
    {
        var op = _command[0] >> 24;
        var textured = (op & 0x04) != 0;
        var shading = new Shading
        {
            Textured = textured,
            Raw = textured && (op & 0x01) != 0,
            SemiTransparent = (op & 0x02) != 0,
        };
        ApplyDrawMode(ref shading);

        var index = 1;
        var position = _command[index++];
        var x0 = SignExtend11(position) + _offsetX;
        var y0 = SignExtend11(position >> 16) + _offsetY;
        int u0 = 0, v0 = 0;
        if (textured)
        {
            var texture = _command[index++];
            u0 = (int)(texture & 0xFF);
            v0 = (int)((texture >> 8) & 0xFF);
            shading.ClutX = (int)((texture >> 16) & 0x3F) * 16;
            shading.ClutY = (int)((texture >> 22) & 0x1FF);
        }

        int width, height;
        switch ((op >> 3) & 3)
        {
            case 0:
                var size = _command[index];
                width = (int)(size & 0x3FF);
                height = (int)((size >> 16) & 0x1FF);
                break;
            case 1:
                width = height = 1;
                break;
            case 2:
                width = height = 8;
                break;
            default:
                width = height = 16;
                break;
        }

        var color = _command[0];
        int r = (int)(color & 0xFF), g = (int)((color >> 8) & 0xFF), b = (int)((color >> 16) & 0xFF);
        var flipX = (_drawMode & 0x1000) != 0;
        var flipY = (_drawMode & 0x2000) != 0;

        for (var dy = 0; dy < height; dy++)
        {
            var y = y0 + dy;
            if (y < _drawTop || y > _drawBottom)
            {
                continue;
            }

            for (var dx = 0; dx < width; dx++)
            {
                var x = x0 + dx;
                if (x < _drawLeft || x > _drawRight)
                {
                    continue;
                }

                var u = (flipX ? u0 - dx : u0 + dx) & 0xFF;
                var v = (flipY ? v0 - dy : v0 + dy) & 0xFF;
                Plot(x, y, r, g, b, u, v, shading);
            }
        }
    }

    // ---- Pixels ----

    /// <summary>Shades, textures, blends and writes one pixel (already inside the draw area).</summary>
    private void Plot(int x, int y, int r, int g, int b, int u, int v, in Shading shading)
    {
        var index = (y & (VramHeight - 1)) * VramWidth + (x & (VramWidth - 1));
        var background = _vram[index];
        if (_checkMask && (background & 0x8000) != 0)
        {
            return;
        }

        var semi = shading.SemiTransparent;
        ushort maskBit = 0;
        if (shading.Textured)
        {
            var texel = FetchTexel(u, v, shading);
            if (texel == 0)
            {
                return; // fully transparent
            }

            maskBit = (ushort)(texel & 0x8000);
            semi &= maskBit != 0; // only texels with bit 15 set are blended
            var tr = (texel & 0x1F) << 3;
            var tg = ((texel >> 5) & 0x1F) << 3;
            var tb = ((texel >> 10) & 0x1F) << 3;
            if (shading.Raw)
            {
                (r, g, b) = (tr, tg, tb);
            }
            else
            {
                // Modulation: 128 is full brightness (psx-spx: "GPU Texture Blending").
                r = Math.Min(255, tr * r >> 7);
                g = Math.Min(255, tg * g >> 7);
                b = Math.Min(255, tb * b >> 7);
            }
        }

        if (shading.Dither)
        {
            var offset = DitherTable[(y & 3) * 4 + (x & 3)];
            r = Math.Clamp(r + offset, 0, 255);
            g = Math.Clamp(g + offset, 0, 255);
            b = Math.Clamp(b + offset, 0, 255);
        }

        int r5 = r >> 3, g5 = g >> 3, b5 = b >> 3;
        if (semi)
        {
            var br = background & 0x1F;
            var bg = (background >> 5) & 0x1F;
            var bb = (background >> 10) & 0x1F;
            switch (shading.SemiMode)
            {
                case 0:
                    (r5, g5, b5) = ((br + r5) >> 1, (bg + g5) >> 1, (bb + b5) >> 1);
                    break;
                case 1:
                    (r5, g5, b5) = (Math.Min(31, br + r5), Math.Min(31, bg + g5), Math.Min(31, bb + b5));
                    break;
                case 2:
                    (r5, g5, b5) = (Math.Max(0, br - r5), Math.Max(0, bg - g5), Math.Max(0, bb - b5));
                    break;
                default:
                    (r5, g5, b5) = (Math.Min(31, br + (r5 >> 2)), Math.Min(31, bg + (g5 >> 2)), Math.Min(31, bb + (b5 >> 2)));
                    break;
            }
        }

        _vram[index] = (ushort)(r5 | g5 << 5 | b5 << 10 | maskBit | (_setMask ? 0x8000 : 0));
    }

    /// <summary>A texel through the texture window, from a 4- or 8-bit page via its CLUT, or a 15-bit page.</summary>
    private ushort FetchTexel(int u, int v, in Shading shading)
    {
        // Texture window (GP0 $E2): masked bits are replaced by the offset, in steps of 8 texels.
        var maskX = (int)(_textureWindow & 0x1F) * 8;
        var maskY = (int)((_textureWindow >> 5) & 0x1F) * 8;
        var offsetX = (int)((_textureWindow >> 10) & 0x1F) * 8;
        var offsetY = (int)((_textureWindow >> 15) & 0x1F) * 8;
        u = (u & ~maskX) | (offsetX & maskX);
        v = (v & ~maskY) | (offsetY & maskY);

        var y = shading.PageY + v;
        switch (shading.Depth)
        {
            case 0:
            {
                var word = Pixel(shading.PageX + (u >> 2), y);
                var entry = (word >> ((u & 3) * 4)) & 0xF;
                return Pixel(shading.ClutX + entry, shading.ClutY);
            }

            case 1:
            {
                var word = Pixel(shading.PageX + (u >> 1), y);
                var entry = (word >> ((u & 1) * 8)) & 0xFF;
                return Pixel(shading.ClutX + entry, shading.ClutY);
            }

            default:
                return Pixel(shading.PageX + u, y);
        }
    }

    private static int SignExtend11(uint value) => (int)(value << 21) >> 21;

    // ---- Display ----

    /// <summary>The displayed width in pixels, from GP1 $08: 256, 320, 512 or 640, or 368.</summary>
    public int DisplayWidth => (_displayMode & 0x40) != 0
        ? 368
        : (_displayMode & 3) switch { 0 => 256, 1 => 320, 2 => 512, _ => 640 };

    /// <summary>The displayed height: the vertical display range (GP1 $07), doubled in interlaced 480-line mode, at most 480.</summary>
    public int DisplayHeight
    {
        get
        {
            var lines = Math.Clamp(_vEnd - _vStart, 1, PalMode ? 288 : 240);
            return Math.Min(Interlaced ? lines * 2 : lines, 480);
        }
    }

    /// <summary>GPU clocks per dot for the display mode: 10, 8, 5, 4, or 7 for 368 wide (psx-spx: "GPU Timings").</summary>
    public int DotClockDivider => (_displayMode & 0x40) != 0
        ? 7
        : (_displayMode & 3) switch { 0 => 10, 1 => 8, 2 => 5, _ => 4 };

    /// <summary>The first and last display lines (GP1 $07), for V-blank timing.</summary>
    public int DisplayLineStart => _vStart;

    public int DisplayLineEnd => _vEnd;

    /// <summary>
    /// Copies the displayed part of VRAM into <paramref name="frame"/> (<see cref="DisplayWidth"/> × <see cref="DisplayHeight"/>
    /// BGR555 indices). 24-bit mode is rounded to 15 bits; a disabled display is black.
    /// </summary>
    public void CopyDisplay(Span<ushort> frame)
    {
        var width = DisplayWidth;
        var height = DisplayHeight;
        if (_displayDisabled)
        {
            frame[..(width * height)].Clear();
            return;
        }

        var color24 = (_displayMode & 0x10) != 0;
        for (var y = 0; y < height; y++)
        {
            var row = ((_displayY + y) & (VramHeight - 1)) * VramWidth;
            var line = frame.Slice(y * width, width);
            if (!color24)
            {
                for (var x = 0; x < width; x++)
                {
                    line[x] = (ushort)(_vram[row + ((_displayX + x) & (VramWidth - 1))] & 0x7FFF);
                }

                continue;
            }

            // 24-bit: three bytes a pixel, packed through the 16-bit words.
            var byteStart = _displayX * 2;
            for (var x = 0; x < width; x++)
            {
                var offset = byteStart + x * 3;
                int r = VramByte(row, offset), g = VramByte(row, offset + 1), b = VramByte(row, offset + 2);
                line[x] = (ushort)(r >> 3 | (g >> 3) << 5 | (b >> 3) << 10);
            }
        }
    }

    private int VramByte(int row, int byteOffset)
    {
        var word = _vram[row + ((byteOffset >> 1) & (VramWidth - 1))];
        return (byteOffset & 1) == 0 ? word & 0xFF : word >> 8;
    }
}
