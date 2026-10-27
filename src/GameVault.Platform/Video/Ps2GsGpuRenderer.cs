using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using System.Reflection;
using GameVault.Core.PlayStation2;

namespace GameVault.Platform.Video;

/// <summary>
/// The PlayStation 2's GS on the GPU at an internal resolution of <see cref="Scale"/> × (docs/plans/gpu-renderers.md,
/// milestone 3). The software GS stays authoritative for local memory; this draws each batch the GS hands over
/// (<see cref="IPs2GsBackend"/>) again, with the compute shader in Shaders/Ps2Gs.hlsl, into a copy of local memory that
/// keeps Scale × Scale samples a pixel, and shows the picture the PCRTC reads from it.
/// </summary>
/// <remarks>
/// Each 8 KB page of the GPU's copy is in step with local memory or seeded from it: before a batch uses a page, a hash
/// of the page in local memory is compared with the one taken after the GPU last drew it, and a page that changed some
/// other way (an upload, a copy, the CPU, a state load) is seeded again, each word repeated in every sample. Pages the
/// GPU drew are "HD": textures in them are sampled on their sample grid, so render targets keep their detail.
/// </remarks>
public sealed class Ps2GsGpuRenderer : IPs2GsBackend, IDisposable
{
    public const int MaxScale = 4;
    private const int CommandWords = 64;
    private const int StateWords = 96;
    private const int Pages = Ps2Gs.LocalMemorySize / PageBytes;
    private const int PageBytes = 8192;
    private const int PageWords = PageBytes / 4;
    private const int TableWords = 45056;

    private readonly IGpuCompute _gpu;
    private readonly Ps2Gs _gs;
    private readonly bool _ownsGpu;
    private readonly IGpuKernel _draw;
    private readonly IGpuKernel _display;
    private readonly IGpuBuffer _params;
    private readonly IGpuBuffer _tables;
    private readonly IGpuBuffer _memory;
    private readonly IGpuBuffer _hd;
    private IGpuBuffer _commands;
    private IGpuBuffer _states;
    private IGpuBuffer _palettes;
    private IGpuBuffer? _picture;

    private uint[] _commandData = new uint[256 * CommandWords];
    private uint[] _stateData = new uint[16 * StateWords];
    private uint[] _paletteData = new uint[4 * 256];
    private (int X0, int Y0, int X1, int Y1)[] _commandBoxes = new (int, int, int, int)[256];
    private readonly ulong[] _pageHash = new ulong[Pages];
    private readonly bool[] _pageKnown = new bool[Pages];
    private readonly uint[] _pageHd = new uint[Pages];
    private bool _hdDirty = true;
    private readonly uint[] _seed;
    private uint[] _pictureData = [];

    /// <param name="ownsGpu">Dispose <paramref name="gpu"/> with the renderer.</param>
    public Ps2GsGpuRenderer(IGpuCompute gpu, Ps2Gs gs, int scale, bool ownsGpu = false)
    {
        _gpu = gpu;
        _gs = gs;
        _ownsGpu = ownsGpu;
        Scale = Math.Clamp(scale, 1, MaxScale);
        var source = LoadShaderSource();
        _draw = gpu.CreateKernel(source, "Draw");
        _display = gpu.CreateKernel(source, "Display");
        _params = gpu.CreateBuffer(64);
        _tables = gpu.CreateBuffer(TableWords);
        _memory = gpu.CreateBuffer(Ps2Gs.LocalMemorySize / 4 * Scale * Scale);
        _hd = gpu.CreateBuffer(Pages);
        _commands = gpu.CreateBuffer(_commandData.Length);
        _states = gpu.CreateBuffer(_stateData.Length);
        _palettes = gpu.CreateBuffer(_paletteData.Length);
        _seed = new uint[PageWords * Scale * Scale];
        gpu.Write(_tables, BuildTables());
    }

    public int Scale { get; }

    /// <summary>Dispatches of the drawing kernel so far (diagnostics and tests).</summary>
    public int Dispatches { get; private set; }

    /// <summary>Pages seeded from local memory so far (diagnostics and tests).</summary>
    public int Seeds { get; private set; }

    private static string LoadShaderSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GameVault.Ps2Gs.hlsl")
            ?? throw new InvalidOperationException("The GS shader is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static uint[] BuildTables()
    {
        var tables = new uint[TableWords];
        void Put(int at, int[] table)
        {
            for (var i = 0; i < table.Length; i++)
            {
                tables[at + i] = (uint)table[i];
            }
        }

        Put(0, Ps2Gs.PageCt32);
        Put(2048, Ps2Gs.PageZ32);
        Put(4096, Ps2Gs.PageCt16);
        Put(8192, Ps2Gs.PageCt16S);
        Put(12288, Ps2Gs.PageZ16);
        Put(16384, Ps2Gs.PageZ16S);
        Put(20480, Ps2Gs.PageT8);
        Put(28672, Ps2Gs.PageT4);
        return tables;
    }

    // ---- IPs2GsBackend ----

    void IPs2GsBackend.Draw(ReadOnlySpan<Ps2Gs.Command> commands, ReadOnlySpan<Ps2Gs.DrawState> states, ReadOnlySpan<ulong> writes,
        ReadOnlySpan<ulong> reads)
    {
        // The pages the batch uses, as local memory has them now (before the GS draws the batch).
        var memory = _gs.Memory;
        for (var word = 0; word < writes.Length; word++)
        {
            var bits = writes[word] | reads[word];
            while (bits != 0)
            {
                var page = (word * 64) + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                Sync(memory, page);
            }
        }

        PackStates(states);
        if (_commandData.Length < commands.Length * CommandWords)
        {
            _commandData = new uint[commands.Length * CommandWords];
            _commandBoxes = new (int, int, int, int)[commands.Length];
        }

        for (var i = 0; i < commands.Length; i++)
        {
            _commandBoxes[i] = Setup(in commands[i], in states[commands[i].State], _commandData.AsSpan(i * CommandWords, CommandWords));
        }

        if (_commands.Words < commands.Length * CommandWords)
        {
            _commands.Dispose();
            _commands = _gpu.CreateBuffer(commands.Length * CommandWords);
        }

        _gpu.Write(_commands, _commandData.AsSpan(0, commands.Length * CommandWords));
        if (_hdDirty)
        {
            _gpu.Write(_hd, _pageHd);
            _hdDirty = false;
        }

        // A run of commands the GS lets share its rows goes in one dispatch; a serial one (its own rows share memory)
        // goes alone, in order.
        var start = 0;
        while (start < commands.Length)
        {
            var end = start + 1;
            if (!commands[start].Serial)
            {
                while (end < commands.Length && !commands[end].Serial)
                {
                    end++;
                }
            }

            DispatchRun(start, end);
            start = end;
        }
    }

    void IPs2GsBackend.Drawn(ReadOnlySpan<ulong> writes)
    {
        var memory = _gs.Memory;
        for (var word = 0; word < writes.Length; word++)
        {
            var bits = writes[word];
            while (bits != 0)
            {
                var page = (word * 64) + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                _pageHash[page] = XxHash3.HashToUInt64(memory.AsSpan(page * PageBytes, PageBytes));
                _pageKnown[page] = true;
                if (_pageHd[page] == 0)
                {
                    _pageHd[page] = 1;
                    _hdDirty = true;
                }
            }
        }
    }

    /// <summary>
    /// Nothing to forget: each page's hash is checked before it is next used, so a page a state load changed is seeded
    /// again and one it left as the GPU drew it keeps its detail (run-ahead loads a state every frame).
    /// </summary>
    void IPs2GsBackend.Resync()
    {
    }

    /// <summary>Seeds a page from local memory when it no longer holds what the GPU last left there.</summary>
    private void Sync(byte[] memory, int page)
    {
        var native = memory.AsSpan(page * PageBytes, PageBytes);
        var hash = XxHash3.HashToUInt64(native);
        if (_pageKnown[page] && _pageHash[page] == hash)
        {
            return;
        }

        Seeds++;
        var samples = Scale * Scale;
        for (var w = 0; w < PageWords; w++)
        {
            _seed.AsSpan(w * samples, samples).Fill(BinaryPrimitives.ReadUInt32LittleEndian(native[(w * 4)..]));
        }

        _gpu.Write(_memory, _seed, page * PageWords * samples);
        _pageHash[page] = hash;
        _pageKnown[page] = true;
        if (_pageHd[page] != 0)
        {
            _pageHd[page] = 0;
            _hdDirty = true;
        }
    }

    private void DispatchRun(int start, int end)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0;
        for (var i = start; i < end; i++)
        {
            var b = _commandBoxes[i];
            if (b.X1 <= b.X0 || b.Y1 <= b.Y0)
            {
                continue;
            }

            x0 = Math.Min(x0, b.X0);
            y0 = Math.Min(y0, b.Y0);
            x1 = Math.Max(x1, b.X1);
            y1 = Math.Max(y1, b.Y1);
        }

        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        Span<uint> p = stackalloc uint[8];
        p[0] = (uint)(end - start);
        p[1] = (uint)start;
        p[2] = (uint)Scale;
        p[3] = (uint)x0;
        p[4] = (uint)y0;
        _gpu.Write(_params, p);
        _gpu.Dispatch(_draw, [_params, _commands, _states, _tables, _palettes, _memory, _hd], (x1 - x0 + 7) / 8, (y1 - y0 + 7) / 8);
        Dispatches++;
    }

    // ---- Setup ----

    private void PackStates(ReadOnlySpan<Ps2Gs.DrawState> states)
    {
        if (_stateData.Length < states.Length * StateWords)
        {
            _stateData = new uint[states.Length * StateWords];
        }

        var palettes = 0;
        for (var i = 0; i < states.Length; i++)
        {
            if (states[i].Palette is not null)
            {
                palettes++;
            }
        }

        if (_paletteData.Length < Math.Max(palettes, 1) * 256)
        {
            _paletteData = new uint[palettes * 256];
        }

        var nextPalette = 0;
        for (var i = 0; i < states.Length; i++)
        {
            ref readonly var s = ref states[i];
            var w = _stateData.AsSpan(i * StateWords, StateWords);
            w.Clear();
            w[0] = B(s.Tme);
            w[1] = B(s.Fge);
            w[2] = B(s.Abe);
            w[3] = B(s.Fst);
            w[4] = (uint)s.Scax0;
            w[5] = (uint)s.Scax1;
            w[6] = (uint)s.Scay0;
            w[7] = (uint)s.Scay1;
            w[8] = (uint)s.Fbp;
            w[9] = (uint)s.Fbw;
            w[10] = (uint)s.Fpsm;
            w[11] = s.Fbmsk;
            w[12] = B(s.Fb16);
            w[13] = B(s.Fb24);
            w[14] = (uint)s.Zbp;
            w[15] = (uint)ZPsm(in s);
            w[16] = B(s.Z16);
            w[17] = B(s.Z24);
            w[18] = s.ZMax;
            w[19] = B(s.Zmsk);
            w[20] = B(s.Ate);
            w[21] = (uint)s.Atst;
            w[22] = (uint)s.Aref;
            w[23] = (uint)s.Afail;
            w[24] = B(s.Date);
            w[25] = (uint)s.Datm;
            w[26] = B(s.Zte);
            w[27] = (uint)s.Ztst;
            w[28] = (uint)s.BlendA;
            w[29] = (uint)s.BlendB;
            w[30] = (uint)s.BlendC;
            w[31] = (uint)s.BlendD;
            w[32] = (uint)s.BlendFix;
            w[33] = B(s.Pabe);
            w[34] = B(s.Fba);
            w[35] = B(s.Colclamp);
            w[36] = B(s.Dthe);
            w[37] = (uint)s.ScanmskSkip;
            w[38] = (uint)s.Tpsm;
            w[39] = (uint)s.TexW;
            w[40] = (uint)s.TexH;
            w[41] = B(s.Tcc);
            w[42] = (uint)s.Tfx;
            w[43] = (uint)s.Wms;
            w[44] = (uint)s.Wmt;
            w[45] = (uint)s.Minu;
            w[46] = (uint)s.Maxu;
            w[47] = (uint)s.Minv;
            w[48] = (uint)s.Maxv;
            w[49] = B(s.MagLinear);
            w[50] = B(s.MinLinear);
            w[51] = B(s.Lcm);
            w[52] = (uint)s.LodL;
            w[53] = BitConverter.SingleToUInt32Bits((float)s.LodK);
            w[54] = (uint)s.Mxl;
            w[55] = (uint)s.MipMode;
            for (var level = 0; level < 7; level++)
            {
                w[56 + level] = (uint)s.MipTbp[level];
                w[63 + level] = (uint)s.MipTbw[level];
            }

            w[70] = (uint)s.Ta0;
            w[71] = (uint)s.Ta1;
            w[72] = B(s.Aem);
            w[73] = (uint)s.FogR;
            w[74] = (uint)s.FogG;
            w[75] = (uint)s.FogB;
            for (var d = 0; d < 16; d++)
            {
                w[76 + d] = (uint)s.Dimx[d];
            }

            if (s.Palette is { } palette)
            {
                w[92] = (uint)(nextPalette * 256);
                palette.AsSpan(0, Math.Min(palette.Length, 256)).CopyTo(_paletteData.AsSpan(nextPalette * 256, 256));
                nextPalette++;
            }
        }

        if (_states.Words < states.Length * StateWords)
        {
            _states.Dispose();
            _states = _gpu.CreateBuffer(states.Length * StateWords);
        }

        if (_palettes.Words < _paletteData.Length)
        {
            _palettes.Dispose();
            _palettes = _gpu.CreateBuffer(_paletteData.Length);
        }

        _gpu.Write(_states, _stateData.AsSpan(0, states.Length * StateWords));
        if (nextPalette > 0)
        {
            _gpu.Write(_palettes, _paletteData.AsSpan(0, nextPalette * 256));
        }
    }

    private static uint B(bool value) => value ? 1u : 0u;

    private static int ZPsm(in Ps2Gs.DrawState s) =>
        ReferenceEquals(s.ZTable, Ps2Gs.PageZ16) ? Ps2Gs.PsmZ16
        : ReferenceEquals(s.ZTable, Ps2Gs.PageZ16S) ? Ps2Gs.PsmZ16S
        : s.Z24 ? Ps2Gs.PsmZ24 : Ps2Gs.PsmZ32;

    /// <summary>The attributes a vertex gives (Ps2Gs.Renderer.cs's Attributes): R, G, B, A, F, S, T, Q.</summary>
    private static void Attributes(in Ps2Gs.Vertex v, in Ps2Gs.DrawState s, Span<double> a)
    {
        a[0] = v.R;
        a[1] = v.G;
        a[2] = v.B;
        a[3] = v.A;
        a[4] = v.F;
        if (s.Fst)
        {
            a[5] = v.U;
            a[6] = v.V;
            a[7] = 1;
        }
        else
        {
            a[5] = v.S;
            a[6] = v.T;
            a[7] = v.Q;
        }
    }

    /// <summary>
    /// Sets a command up for the kernel (its words in <paramref name="w"/>) and returns the box of scaled pixels it can
    /// touch. Attribute planes are in scaled pixels: value = c + gx × X + gy × Y at scaled pixel (X, Y), whose sample is
    /// at native pixel position (X + ½) / S − ½.
    /// </summary>
    private (int X0, int Y0, int X1, int Y1) Setup(in Ps2Gs.Command c, in Ps2Gs.DrawState s, Span<uint> w)
    {
        w.Clear();
        w[1] = (uint)c.State;
        var scale = Scale;
        Span<double> a0 = stackalloc double[8];
        Span<double> gx = stackalloc double[8];
        Span<double> gy = stackalloc double[8];
        gx.Clear();
        gy.Clear();
        int bx0, by0, bx1, by1; // native pixels, inclusive
        double z0, zx = 0, zy = 0;
        switch (c.Kind)
        {
            case Ps2Gs.PrimPoint:
            {
                w[0] = 0;
                var x = (c.A.X + 8) >> 4;
                var y = (c.A.Y + 8) >> 4;
                w[8] = (uint)x;
                w[9] = (uint)y;
                Attributes(c.A, s, a0);
                z0 = c.A.Z;
                bx0 = bx1 = x;
                by0 = by1 = y;
                Planes(w, a0, gx, gy, scale, line: false);
                ZPlane(w, z0, 0, 0, scale);
                break;
            }

            case Ps2Gs.PrimLine:
            {
                w[0] = 1;
                var v0 = c.A;
                var v1 = c.B;
                var dx = v1.X - v0.X;
                var dy = v1.Y - v0.Y;
                if (dx == 0 && dy == 0)
                {
                    return default;
                }

                Attributes(v0, s, a0);
                Attributes(v1, s, gx);
                for (var i = 0; i < 8; i++)
                {
                    gx[i] -= a0[i];
                }

                if (!s.Iip)
                {
                    a0[0] = v1.R;
                    a0[1] = v1.G;
                    a0[2] = v1.B;
                    a0[3] = v1.A;
                    gx[0] = gx[1] = gx[2] = gx[3] = 0;
                }

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

                w[8] = xMajor ? 1u : 0u;
                w[9] = (uint)p0;
                w[10] = (uint)q0;
                w[11] = (uint)dq;
                w[12] = (uint)dp;
                w[13] = (uint)first;
                w[14] = (uint)last;
                w[15] = (uint)step;
                Planes(w, a0, gx, gy, scale, line: true);
                w[50] = BitConverter.SingleToUInt32Bits((float)(double)v0.Z);
                w[51] = BitConverter.SingleToUInt32Bits((float)((double)v1.Z - v0.Z));
                var pMin = Math.Min(first, last);
                var pMax = Math.Max(first, last);
                var minorMin = (Math.Min(xMajor ? v0.Y : v0.X, xMajor ? v1.Y : v1.X) >> 4) - 1;
                var minorMax = (Math.Max(xMajor ? v0.Y : v0.X, xMajor ? v1.Y : v1.X) >> 4) + 1;
                (bx0, bx1, by0, by1) = xMajor ? (pMin, pMax, minorMin, minorMax) : (minorMin, minorMax, pMin, pMax);
                break;
            }

            case Ps2Gs.PrimSprite:
            {
                w[0] = 2;
                var x0 = c.A.X;
                var x1 = c.B.X;
                var y0 = c.A.Y;
                var y1 = c.B.Y;
                Span<double> first = stackalloc double[8];
                Attributes(c.A, s, first);
                Attributes(c.B, s, a0);
                double s0 = first[5], s1 = a0[5], t0 = first[6], t1 = a0[6];
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

                w[8] = (uint)x0;
                w[9] = (uint)x1;
                w[10] = (uint)y0;
                w[11] = (uint)y1;
                var sPerUnit = x1 != x0 ? (s1 - s0) / (x1 - x0) : 0;
                var tPerUnit = y1 != y0 ? (t1 - t0) / (y1 - y0) : 0;

                // s at native pixel x is s0 + sPerUnit × (16x − x0).
                a0[5] = s0 - (sPerUnit * x0);
                gx[5] = sPerUnit * 16;
                a0[6] = t0 - (tPerUnit * y0);
                gy[6] = tPerUnit * 16;
                Planes(w, a0, gx, gy, scale, line: false);
                ZPlane(w, c.B.Z, 0, 0, scale);
                bx0 = (x0 >> 4) - 1;
                bx1 = (x1 >> 4) + 1;
                by0 = (y0 >> 4) - 1;
                by1 = (y1 >> 4) + 1;
                break;
            }

            default:
            {
                w[0] = 3;
                var v0 = c.A;
                var v1 = c.B;
                var v2 = c.C;
                var area = ((long)(v1.X - v0.X) * (v2.Y - v0.Y)) - ((long)(v1.Y - v0.Y) * (v2.X - v0.X));
                if (area == 0)
                {
                    return default;
                }

                if (area < 0)
                {
                    (v1, v2) = (v2, v1);
                    area = -area;
                }

                Edge(w, 8, v1, v2, scale);
                Edge(w, 12, v2, v0, scale);
                Edge(w, 16, v0, v1, scale);

                Attributes(v0, s, a0);
                Span<double> d1 = stackalloc double[8];
                Span<double> d2 = stackalloc double[8];
                Attributes(v1, s, d1);
                Attributes(v2, s, d2);
                var inv = 16.0 / area;
                for (var i = 0; i < 8; i++)
                {
                    d1[i] -= a0[i];
                    d2[i] -= a0[i];
                    gx[i] = (d1[i] * (v2.Y - v0.Y) * inv) - (d2[i] * (v1.Y - v0.Y) * inv);
                    gy[i] = (d2[i] * (v1.X - v0.X) * inv) - (d1[i] * (v2.X - v0.X) * inv);
                }

                double dz1 = (double)v1.Z - v0.Z, dz2 = (double)v2.Z - v0.Z;
                zx = (dz1 * (v2.Y - v0.Y) * inv) - (dz2 * (v1.Y - v0.Y) * inv);
                zy = (dz2 * (v1.X - v0.X) * inv) - (dz1 * (v2.X - v0.X) * inv);
                z0 = v0.Z - (zx * v0.X / 16) - (zy * v0.Y / 16);

                // As planes through the origin: value at native pixel (x, y) = a + gx·x + gy·y.
                for (var i = 0; i < 8; i++)
                {
                    a0[i] -= (gx[i] * v0.X / 16) + (gy[i] * v0.Y / 16);
                }

                if (!s.Iip)
                {
                    // Flat shading takes the colour of the vertex that completed the triangle.
                    a0[0] = c.C.R;
                    a0[1] = c.C.G;
                    a0[2] = c.C.B;
                    a0[3] = c.C.A;
                    gx[0] = gx[1] = gx[2] = gx[3] = 0;
                    gy[0] = gy[1] = gy[2] = gy[3] = 0;
                }

                Planes(w, a0, gx, gy, scale, line: false);
                ZPlane(w, z0, zx, zy, scale);
                var minX = Math.Min(v0.X, Math.Min(v1.X, v2.X));
                var maxX = Math.Max(v0.X, Math.Max(v1.X, v2.X));
                var minY = Math.Min(v0.Y, Math.Min(v1.Y, v2.Y));
                var maxY = Math.Max(v0.Y, Math.Max(v1.Y, v2.Y));
                bx0 = (minX >> 4) - 1;
                bx1 = (maxX >> 4) + 1;
                by0 = (minY >> 4) - 1;
                by1 = (maxY >> 4) + 1;
                break;
            }
        }

        // Within the scissor (the kernel tests it per pixel too).
        bx0 = Math.Max(bx0, s.Scax0);
        by0 = Math.Max(by0, s.Scay0);
        bx1 = Math.Min(bx1, s.Scax1);
        by1 = Math.Min(by1, s.Scay1);
        if (bx1 < bx0 || by1 < by0)
        {
            return default;
        }

        var box = (bx0 * scale, by0 * scale, (bx1 + 1) * scale, (by1 + 1) * scale);
        w[2] = (uint)box.Item1;
        w[3] = (uint)box.Item2;
        w[4] = (uint)box.Item3;
        w[5] = (uint)box.Item4;
        return box;
    }

    /// <summary>
    /// The edge function of a→b (Ps2Gs.Renderer.cs's Edge) at the scaled pixel grid: E = c + A·X + B·Y, sampled at
    /// 16X + 8 − 8S in 1/(16S) pixels, with the top-left rule's +1.
    /// </summary>
    private static void Edge(Span<uint> w, int at, in Ps2Gs.Vertex a, in Ps2Gs.Vertex b, int scale)
    {
        long dx = b.X - a.X;
        long dy = b.Y - a.Y;
        var topLeft = dy < 0 || (dy == 0 && dx > 0);
        long offset = 8 - (8 * scale);
        var c = (dx * (offset - ((long)a.Y * scale))) - (dy * (offset - ((long)a.X * scale))) + (topLeft ? 1 : 0);
        w[at] = (uint)c;
        w[at + 1] = (uint)(c >> 32);
        w[at + 2] = (uint)(int)(-dy * 16);
        w[at + 3] = (uint)(int)(dx * 16);
    }

    /// <summary>
    /// Attribute planes from native-pixel planes (value = a + gx·x + gy·y) to scaled pixels; a line's are in its step
    /// parameter t (value = a + gx·t) and stay as they are.
    /// </summary>
    private static void Planes(Span<uint> w, ReadOnlySpan<double> a, ReadOnlySpan<double> gx, ReadOnlySpan<double> gy, int scale, bool line)
    {
        var offset = (0.5 / scale) - 0.5;
        for (var i = 0; i < 8; i++)
        {
            double c = a[i], x = gx[i], y = gy[i];
            if (!line)
            {
                c += (x + y) * offset;
                x /= scale;
                y /= scale;
            }

            w[20 + (i * 3)] = BitConverter.SingleToUInt32Bits((float)c);
            w[21 + (i * 3)] = BitConverter.SingleToUInt32Bits((float)x);
            w[22 + (i * 3)] = BitConverter.SingleToUInt32Bits((float)y);
        }
    }

    /// <summary>Z's plane in fixed point (16 fraction bits) at the scaled grid.</summary>
    private static void ZPlane(Span<uint> w, double z0, double zx, double zy, int scale)
    {
        var offset = (0.5 / scale) - 0.5;
        var c = Fixed(z0 + ((zx + zy) * offset));
        var x = Fixed(zx / scale);
        var y = Fixed(zy / scale);
        w[44] = (uint)c;
        w[45] = (uint)(c >> 32);
        w[46] = (uint)x;
        w[47] = (uint)(x >> 32);
        w[48] = (uint)y;
        w[49] = (uint)(y >> 32);
    }

    private static long Fixed(double v) => (long)Math.Floor((Math.Clamp(v, -1e14, 1e14) * 65536.0) + 0.5);

    // ---- The picture ----

    /// <summary>
    /// The picture the PCRTC last showed (<see cref="Ps2Gs.RenderFrame"/>'s layout) at the internal resolution, as ARGB.
    /// </summary>
    public ReadOnlySpan<uint> ReadDisplay(out int width, out int height)
    {
        var layout = _gs.LastDisplay;
        width = layout.Width * Scale;
        height = layout.Height * Scale;
        if (layout.Width <= 0 || layout.Height <= 0)
        {
            width = height = 0;
            return default;
        }

        // The pages the circuits show, as local memory has them.
        var memory = _gs.Memory;
        SyncCircuit(memory, layout.C1, layout);
        SyncCircuit(memory, layout.C2, layout);
        if (_hdDirty)
        {
            _gpu.Write(_hd, _pageHd);
            _hdDirty = false;
        }

        var words = width * height;
        if (_picture is null || _picture.Words < words)
        {
            _picture?.Dispose();
            _picture = _gpu.CreateBuffer(words);
        }

        Span<uint> p = stackalloc uint[48];
        p[0] = (uint)layout.Width;
        p[1] = (uint)layout.Height;
        p[2] = (uint)Scale;
        p[3] = (uint)layout.SourceWidth;
        p[4] = (uint)layout.MinDx;
        p[5] = (uint)layout.MinDy;
        p[6] = layout.Background;
        p[7] = B(layout.FieldMode);
        p[8] = B(layout.FixedAlpha);
        p[9] = B(layout.BackgroundUnder);
        p[10] = (uint)layout.Alpha;
        Circuit(p[16..], layout.C1);
        Circuit(p[32..], layout.C2);
        _gpu.Write(_params, p);
        _gpu.Dispatch(_display, [_params, _picture, _states, _tables, _palettes, _memory, _hd], (width + 7) / 8, (height + 7) / 8);
        if (_pictureData.Length < words)
        {
            _pictureData = new uint[words];
        }

        _gpu.Read(_picture, _pictureData.AsSpan(0, words));
        return _pictureData.AsSpan(0, words);
    }

    private static void Circuit(Span<uint> p, in Ps2Gs.Circuit c)
    {
        p[0] = B(c.Enabled);
        p[1] = (uint)c.Fbp;
        p[2] = (uint)c.Fbw;
        p[3] = (uint)c.Psm;
        p[4] = (uint)c.Dbx;
        p[5] = (uint)c.Dby;
        p[6] = (uint)c.Dx;
        p[7] = (uint)c.Dy;
        p[8] = (uint)c.Width;
        p[9] = (uint)c.Height;
    }

    private void SyncCircuit(byte[] memory, in Ps2Gs.Circuit c, in Ps2Gs.DisplayLayout layout)
    {
        if (!c.Enabled)
        {
            return;
        }

        // Pages are 64 pixels wide and 32 rows (32-bit) or 64 rows (16-bit) tall; the circuit covers whole page rows.
        var rows = layout.FieldMode ? (c.Height + 1) / 2 : c.Height;
        var pageHeight = Ps2Gs.BitsPerPixel(c.Psm) == 16 ? 64 : 32;
        var firstRow = c.Dby / pageHeight;
        var lastRow = (c.Dby + rows - 1) / pageHeight;
        var perRow = Math.Max(c.Fbw, 1);
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = 0; column < perRow; column++)
            {
                Sync(memory, ((c.Fbp / 32) + (row * perRow) + column) & (Pages - 1));
            }
        }
    }

    /// <summary>Whether the GPU's copy of a page is in step with local memory (tests).</summary>
    internal bool InStep(int page) =>
        _pageKnown[page] && _pageHash[page] == XxHash3.HashToUInt64(_gs.Memory.AsSpan(page * PageBytes, PageBytes));

    /// <summary>Local memory's words as the GPU has them, sample 0 of each (tests).</summary>
    internal uint[] ReadMemory(int sample = 0)
    {
        var samples = Scale * Scale;
        var all = new uint[Ps2Gs.LocalMemorySize / 4 * samples];
        _gpu.Read(_memory, all);
        var words = new uint[Ps2Gs.LocalMemorySize / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = all[(i * samples) + sample];
        }

        return words;
    }

    public void Dispose()
    {
        _params.Dispose();
        _tables.Dispose();
        _memory.Dispose();
        _hd.Dispose();
        _commands.Dispose();
        _states.Dispose();
        _palettes.Dispose();
        _picture?.Dispose();
        _draw.Dispose();
        _display.Dispose();
        if (_ownsGpu)
        {
            _gpu.Dispose();
        }
    }
}
