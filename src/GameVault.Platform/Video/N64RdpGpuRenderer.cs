using System.Buffers.Binary;
using System.IO.Hashing;
using System.Reflection;
using GameVault.Core.Nintendo64;

namespace GameVault.Platform.Video;

/// <summary>
/// The N64's RDP on the GPU at an internal resolution of <see cref="Scale"/> × (docs/plans/gpu-renderers.md, milestone
/// 2). The software RDP stays authoritative for RDRAM; this draws the picture again from the primitives the RDP hands
/// over (<see cref="IN64RdpBackend"/>), with the compute shader in Shaders/N64Rdp.hlsl, into scaled copies of the colour
/// and Z images (surfaces). The host shows a surface's picture instead of the VI's frame when the VI shows it.
/// </summary>
/// <remarks>
/// Primitives are gathered into batches, one per colour and Z image; a batch is drawn when the images change, when the
/// RDP finishes its commands, or when the picture is read. A surface is taken when a primitive for it arrives, before
/// the software RDP draws that primitive: it is seeded from RDRAM (each pixel repeated Scale × Scale times) when it is
/// new, and again when RDRAM under it changed since the RDP last finished (a hash of its pixels no longer matches), so
/// whatever the CPU drew there, or a state load brought back, isn't lost. A Z buffer cleared as a colour image is the
/// same memory under two keys; the one drawn more recently is copied into the other.
/// </remarks>
public sealed class N64RdpGpuRenderer : IN64RdpBackend, IDisposable
{
    public const int MaxScale = 4;
    private const int PrimWords = 104;
    private const int MaxBatch = 2048;
    private const int MaxRows = 1024;
    private const int TmemWords = 1024;

    private readonly IGpuCompute _gpu;
    private readonly byte[] _rdram;
    private readonly IGpuKernel _kernel;
    private readonly bool _ownsGpu;
    private readonly IGpuBuffer _params;
    private IGpuBuffer _prims;
    private IGpuBuffer _tmem;
    private IGpuBuffer? _scratchDepth;

    private readonly uint[] _batch = new uint[MaxBatch * PrimWords];
    private readonly List<uint[]> _snapshots = [];
    private readonly uint[] _tmemNow = new uint[TmemWords];
    private bool _tmemChanged = true;
    private int _count;
    private Target _target;
    private int _rows;
    private int _boxX0, _boxY0, _boxX1, _boxY1;
    private uint _seed = 1;
    private readonly Dictionary<SurfaceKey, Surface> _surfaces = [];
    private uint[] _picture = [];
    private uint[] _download = [];

    /// <param name="ownsGpu">Dispose <paramref name="gpu"/> with the renderer.</param>
    public N64RdpGpuRenderer(IGpuCompute gpu, byte[] rdram, int scale, bool ownsGpu = false)
    {
        _gpu = gpu;
        _ownsGpu = ownsGpu;
        _rdram = rdram;
        Scale = Math.Clamp(scale, 1, MaxScale);
        _kernel = gpu.CreateKernel(LoadShaderSource(), "Draw");
        _params = gpu.CreateBuffer(16);
        _prims = gpu.CreateBuffer(64 * PrimWords);
        _tmem = gpu.CreateBuffer(8 * TmemWords);
        _boxX0 = _boxY0 = int.MaxValue;
    }

    public int Scale { get; }

    /// <summary>Batches drawn so far (diagnostics and tests).</summary>
    public int BatchesDrawn { get; private set; }

    /// <summary>Times a surface was seeded from RDRAM (diagnostics and tests).</summary>
    public int Seeds { get; private set; }

    internal static string LoadShaderSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GameVault.N64Rdp.hlsl")
            ?? throw new InvalidOperationException("The N64 RDP shader is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ---- IN64RdpBackend ----

    public void Primitive(in N64RdpPrimitive p)
    {
        var cycle = (int)(p.OtherModes >> 52) & 3;
        var zUsed = cycle < 2 && (p.OtherModes & (3UL << 4)) != 0; // Z compare or Z update, in one- or two-cycle mode
        var target = new Target(p.ColorAddress & 0xFFFFFF, p.ColorWidth, p.ColorSize, p.ColorFormat, p.ZAddress & 0xFFFFFF);
        if (_count > 0 && (target != _target || _count == MaxBatch))
        {
            Flush();
        }

        _target = target;
        _batchUsesZ |= zUsed;
        if (_tmemChanged || _snapshots.Count == 0)
        {
            if (_snapshots.Count == 64)
            {
                Flush();
            }

            _snapshots.Add((uint[])_tmemNow.Clone());
            _tmemChanged = false;
        }

        Pack(p, _batch.AsSpan(_count * PrimWords, PrimWords), _snapshots.Count - 1);
        _count++;

        // Take the surfaces now, before the software RDP draws this primitive into RDRAM: a new surface is seeded (and
        // a stale one reseeded) from RDRAM as it stands before the primitive.
        var rows = Math.Clamp(_rows, 1, MaxRows);
        GetSurface(new SurfaceKey(target.ColorAddress, target.Width, target.Size), rows);
        if (zUsed)
        {
            GetSurface(new SurfaceKey(target.ZAddress, target.Width, ZSize), rows);
        }
    }

    private bool _batchUsesZ;
    private long _writes;

    public void TmemLoaded(ReadOnlySpan<byte> tmem)
    {
        for (var i = 0; i < TmemWords; i++)
        {
            _tmemNow[i] = BinaryPrimitives.ReadUInt32BigEndian(tmem[(i * 4)..]);
        }

        _tmemChanged = true;
    }

    public void CommandsDone()
    {
        Flush();
        foreach (var surface in _surfaces.Values)
        {
            if (surface.Touched)
            {
                surface.Hash = HashRdram(surface);
                surface.HashValid = true;
                surface.Touched = false;
            }

            surface.CheckedThisRun = false;
        }
    }

    /// <summary>
    /// After a state load: the queued primitives are dropped and every surface is reseeded from RDRAM when next drawn
    /// to (the buffers are kept, as run-ahead loads a state every frame).
    /// </summary>
    public void Resync()
    {
        _count = 0;
        _rows = 0;
        _boxX0 = _boxY0 = int.MaxValue;
        _boxX1 = _boxY1 = 0;
        _snapshots.Clear();
        _tmemChanged = true;
        _batchUsesZ = false;
        foreach (var surface in _surfaces.Values)
        {
            surface.HashValid = false;
            surface.CheckedThisRun = false;
            surface.Touched = false;
            surface.LastWrite = 0;
        }
    }

    // ---- The picture ----

    /// <summary>
    /// The picture the VI shows (<paramref name="video"/>) at the internal resolution as ARGB, or empty when this
    /// renderer doesn't have it (no surface there, or RDRAM under it changed since the RDP drew it).
    /// </summary>
    public ReadOnlySpan<uint> ReadDisplay(N64VideoOutput video, out int width, out int height)
    {
        width = height = 0;
        Flush();
        if (video.Blanked)
        {
            return default;
        }

        var size = video.BytesPerPixel == 4 ? N64Rdp.Size32 : N64Rdp.Size16;
        Surface? found = null;
        var origin = video.Origin & 0xFFFFFF;
        foreach (var surface in _surfaces.Values)
        {
            var key = surface.Key;
            if (key.Size == size && key.Width == video.Stride && origin >= key.Address &&
                origin < key.Address + (uint)(surface.Rows * key.Width * video.BytesPerPixel))
            {
                found = surface;
                break;
            }
        }

        if (found is null || !found.HashValid || HashRdram(found) != found.Hash)
        {
            return default;
        }

        var offset = (int)(origin - found.Key.Address) / video.BytesPerPixel;
        var row = offset / video.Stride;
        var column = offset % video.Stride;
        var rows = video.Height;
        var columns = video.Width;
        if (found.Rows - row < rows || video.Stride - column < columns)
        {
            return default; // the RDP drew only part of what the VI shows: the software picture has it all
        }

        var s = Scale;
        var stride = found.Key.Width * s;
        var words = rows * s * stride;
        if (_download.Length < words)
        {
            _download = new uint[words];
        }

        _gpu.Read(found.Buffer, _download.AsSpan(0, words), row * s * stride);
        width = columns * s;
        height = rows * s;
        if (_picture.Length < width * height)
        {
            _picture = new uint[width * height];
        }

        for (var y = 0; y < height; y++)
        {
            var source = _download.AsSpan(y * stride + column * s, width);
            var destination = _picture.AsSpan(y * width, width);
            for (var x = 0; x < width; x++)
            {
                var v = source[x];
                destination[x] = size == N64Rdp.Size32
                    ? 0xFF000000u | (v >> 8)
                    : 0xFF000000u | (uint)(Expand5((int)(v >> 11) & 31) << 16 | Expand5((int)(v >> 6) & 31) << 8 | Expand5((int)(v >> 1) & 31));
            }
        }

        return _picture.AsSpan(0, width * height);
    }

    /// <summary>A surface's pixels at the internal resolution, as the RDP stores them (tests).</summary>
    internal uint[]? ReadSurface(uint address, int width, int size, out int rows)
    {
        Flush();
        rows = 0;
        if (!_surfaces.TryGetValue(new SurfaceKey(address & 0xFFFFFF, width, size), out var surface))
        {
            return null;
        }

        rows = surface.Rows;
        var data = new uint[surface.Buffer.Words];
        _gpu.Read(surface.Buffer, data);
        return data;
    }

    private static int Expand5(int v) => (v << 3) | (v >> 2);

    // ---- Batches ----

    private void Flush()
    {
        if (_count == 0)
        {
            _snapshots.Clear();
            _tmemChanged = true;
            return;
        }

        var target = _target;
        var rows = Math.Clamp(_rows, 1, MaxRows);
        var color = GetSurface(new SurfaceKey(target.ColorAddress, target.Width, target.Size), rows);
        IGpuBuffer depth;
        if (_batchUsesZ)
        {
            depth = GetSurface(new SurfaceKey(target.ZAddress, target.Width, ZSize), rows).Buffer;
        }
        else
        {
            if (_scratchDepth is null || _scratchDepth.Words < color.Buffer.Words)
            {
                _scratchDepth?.Dispose();
                _scratchDepth = _gpu.CreateBuffer(color.Buffer.Words);
            }

            depth = _scratchDepth;
        }

        if (_prims.Words < _count * PrimWords)
        {
            _prims.Dispose();
            _prims = _gpu.CreateBuffer(Math.Max(_count, MaxBatch / 4) * PrimWords);
        }

        if (_tmem.Words < _snapshots.Count * TmemWords)
        {
            _tmem.Dispose();
            _tmem = _gpu.CreateBuffer(64 * TmemWords);
        }

        _gpu.Write(_prims, _batch.AsSpan(0, _count * PrimWords));
        for (var i = 0; i < _snapshots.Count; i++)
        {
            _gpu.Write(_tmem, _snapshots[i], i * TmemWords);
        }

        var s = Scale;
        var x0 = Math.Clamp(_boxX0, 0, target.Width * s);
        var y0 = Math.Clamp(_boxY0, 0, color.Rows * s);
        var x1 = Math.Clamp(_boxX1, 0, target.Width * s);
        var y1 = Math.Clamp(_boxY1, 0, color.Rows * s);
        Span<uint> parameters = stackalloc uint[16];
        parameters[0] = (uint)_count;
        parameters[1] = (uint)s;
        parameters[2] = (uint)target.Width;
        parameters[3] = (uint)color.Rows;
        parameters[4] = (uint)x0;
        parameters[5] = (uint)y0;
        parameters[6] = _seed = _seed * 1664525u + 1013904223u;
        _gpu.Write(_params, parameters);
        if (x1 > x0 && y1 > y0)
        {
            _gpu.Dispatch(_kernel, [_params, _prims, _tmem, color.Buffer, depth], (x1 - x0 + 7) / 8, (y1 - y0 + 7) / 8);
        }

        color.Touched = true;
        color.LastWrite = ++_writes;
        if (_batchUsesZ)
        {
            var z = _surfaces[new SurfaceKey(target.ZAddress, target.Width, ZSize)];
            z.Touched = true;
            z.LastWrite = ++_writes;
        }

        BatchesDrawn++;
        _count = 0;
        _rows = 0;
        _batchUsesZ = false;
        _boxX0 = _boxY0 = int.MaxValue;
        _boxX1 = _boxY1 = 0;
        _snapshots.Clear();
        _tmemChanged = true;
    }

    /// <summary>The Z surfaces' "size" in their key: 16-bit depth, apart from the colour images.</summary>
    private const int ZSize = -1;

    private Surface GetSurface(SurfaceKey key, int rows)
    {
        var s = Scale;
        var created = false;
        if (!_surfaces.TryGetValue(key, out var surface))
        {
            surface = new Surface(key, Math.Max(rows, 1), _gpu.CreateBuffer(key.Width * s * Math.Max(rows, 1) * s));
            _surfaces[key] = surface;
            created = true;
        }

        // The same memory as the other kind of 16-bit image (a Z buffer cleared as a colour image, or read back as
        // one), drawn since this one: take the GPU's pixels from there, as RDRAM may not hold them yet (the software
        // RDP draws a batched run only at its end) and doesn't hold them above 1×.
        var aliasKey = key.Size switch
        {
            ZSize => new SurfaceKey(key.Address, key.Width, N64Rdp.Size16),
            N64Rdp.Size16 => new SurfaceKey(key.Address, key.Width, ZSize),
            _ => (SurfaceKey?)null,
        };
        if (aliasKey is { } ak && _surfaces.TryGetValue(ak, out var alias) && alias.LastWrite > surface.LastWrite)
        {
            if (created)
            {
                Seed(surface, 0); // for the rows the other doesn't have
            }

            _gpu.Copy(alias.Buffer, surface.Buffer, Math.Min(alias.Buffer.Words, surface.Buffer.Words));
            surface.LastWrite = alias.LastWrite;
            surface.Touched = true;
            surface.CheckedThisRun = true;
        }
        else if (created)
        {
            Seed(surface, 0);
            surface.CheckedThisRun = true;
        }

        if (!surface.CheckedThisRun)
        {
            // RDRAM under it changed since the RDP last finished: the CPU (or a state load) drew there.
            if (!surface.HashValid || HashRdram(surface) != surface.Hash)
            {
                Seed(surface, 0);
            }

            surface.CheckedThisRun = true;
        }

        if (surface.Rows < rows)
        {
            // Taller: the rows it has keep what the GPU drew (rows are whole strides, so they're the new buffer's
            // first words), and only the new rows are seeded.
            var old = new uint[surface.Buffer.Words];
            _gpu.Read(surface.Buffer, old);
            var grown = new Surface(key, rows, _gpu.CreateBuffer(key.Width * s * rows * s))
            {
                CheckedThisRun = true,
                Touched = surface.Touched,
                LastWrite = surface.LastWrite,
            };
            _gpu.Write(grown.Buffer, old);
            Seed(grown, surface.Rows);
            surface.Buffer.Dispose();
            _surfaces[key] = surface = grown;
        }

        return surface;
    }

    /// <summary>Fills the surface's rows from <paramref name="fromRow"/> down with RDRAM's pixels, each S×S.</summary>
    private void Seed(Surface surface, int fromRow)
    {
        Seeds++;
        var key = surface.Key;
        var s = Scale;
        var stride = key.Width * s;
        var data = new uint[stride * (surface.Rows - fromRow) * s];
        var bytes = key.Size switch { N64Rdp.Size32 => 4, N64Rdp.Size8 => 1, _ => 2 };
        for (var y = fromRow; y < surface.Rows; y++)
        {
            for (var x = 0; x < key.Width; x++)
            {
                var address = (long)key.Address + ((long)y * key.Width + x) * bytes;
                uint v = 0;
                if (address + bytes <= _rdram.Length)
                {
                    v = bytes switch
                    {
                        4 => BinaryPrimitives.ReadUInt32BigEndian(_rdram.AsSpan((int)address)),
                        1 => _rdram[address],
                        _ => BinaryPrimitives.ReadUInt16BigEndian(_rdram.AsSpan((int)address)),
                    };
                }

                for (var dy = 0; dy < s; dy++)
                {
                    data.AsSpan(((y - fromRow) * s + dy) * stride + x * s, s).Fill(v);
                }
            }
        }

        _gpu.Write(surface.Buffer, data, fromRow * s * stride);
        surface.Hash = HashRdram(surface);
        surface.HashValid = true;
    }

    private ulong HashRdram(Surface surface)
    {
        var key = surface.Key;
        var bytes = key.Size switch { N64Rdp.Size32 => 4, N64Rdp.Size8 => 1, _ => 2 };
        var start = (int)Math.Min(key.Address, (uint)_rdram.Length);
        var length = (int)Math.Min((long)surface.Rows * key.Width * bytes, _rdram.Length - start);
        return XxHash3.HashToUInt64(_rdram.AsSpan(start, length));
    }

    /// <summary>The primitive's words in the shader's layout (Shaders/N64Rdp.hlsl, F_* fields).</summary>
    private void Pack(in N64RdpPrimitive p, Span<uint> w, int tmem)
    {
        w.Clear();
        var e = p.Edges;
        w[0] = (e.Flip ? 1u : 0) | (uint)(e.MaxLevel & 7) << 1 | (uint)(e.Tile & 7) << 4;
        w[1] = (uint)e.Yl;
        w[2] = (uint)e.Ym;
        w[3] = (uint)e.Yh;
        w[4] = (uint)e.Xl;
        w[5] = (uint)e.Xh;
        w[6] = (uint)e.Xm;
        w[7] = (uint)e.DxlDy;
        w[8] = (uint)e.DxhDy;
        w[9] = (uint)e.DxmDy;
        for (var i = 0; i < 8; i++)
        {
            w[10 + i] = (uint)e.Value[i];
            w[18 + i] = (uint)e.Dx[i];
            w[26 + i] = (uint)e.De[i];
            w[34 + i] = (uint)e.Dy[i];
        }

        w[42] = (uint)p.OtherModes;
        w[43] = (uint)(p.OtherModes >> 32);
        w[44] = (uint)p.Combine;
        w[45] = (uint)(p.Combine >> 32);
        w[46] = p.FillColor;
        w[47] = p.FogColor;
        w[48] = p.BlendColor;
        w[49] = p.PrimColor;
        w[50] = p.EnvColor;
        w[51] = (uint)p.PrimLodFraction;
        w[52] = (uint)p.PrimMinLevel;
        w[53] = (uint)p.PrimZ;
        w[54] = (uint)p.PrimDeltaZ;
        w[55] = (uint)p.KeyGb;
        w[56] = (uint)(p.KeyGb >> 32);
        w[57] = (uint)p.KeyR;
        w[58] = (uint)p.Convert;
        w[59] = (uint)(p.Convert >> 32);
        w[60] = (uint)p.Scissor;
        w[61] = (uint)(p.Scissor >> 32);
        w[62] = (uint)p.ColorFormat;
        w[63] = (uint)p.ColorSize;
        w[64] = (uint)p.ColorWidth;
        w[65] = p.ColorAddress & 0xFFFFFF;
        w[66] = p.ZAddress & 0xFFFFFF;
        w[67] = (uint)tmem;

        // The rows and columns it can touch (the shader decides each pixel exactly): within the scissor, from YH to YL.
        var scissorXh = (int)(p.Scissor >> 44) & 0xFFF;
        var scissorYh = (int)(p.Scissor >> 32) & 0xFFF;
        var scissorXl = (int)(p.Scissor >> 12) & 0xFFF;
        var scissorYl = (int)p.Scissor & 0xFFF;
        var top = Math.Max(Math.Max(e.Yh >> 2, scissorYh >> 2), 0);
        var bottom = Math.Min(Math.Min(e.Yl >> 2, scissorYl >> 2) + 1, MaxRows);
        var left = Math.Max(scissorXh >> 2, 0);
        var right = Math.Min((scissorXl >> 2) + 1, p.ColorWidth);
        var s = Scale;
        w[68] = (uint)(left * s);
        w[69] = (uint)(top * s);
        w[70] = (uint)(Math.Max(right, left) * s);
        w[71] = (uint)(Math.Max(bottom, top) * s);
        _boxX0 = Math.Min(_boxX0, left * s);
        _boxY0 = Math.Min(_boxY0, top * s);
        _boxX1 = Math.Max(_boxX1, right * s);
        _boxY1 = Math.Max(_boxY1, bottom * s);
        // The surfaces cover the scissor box, not only the primitive: a surface that grows mid-run seeds its new rows
        // from RDRAM, which a batched software RDP hasn't drawn yet.
        _rows = Math.Max(_rows, Math.Min((scissorYl + 3) >> 2, MaxRows));

        for (var i = 0; i < 8; i++)
        {
            var tile = p.Tiles[i];
            w[72 + i * 4] = (uint)tile.SetTile;
            w[73 + i * 4] = (uint)(tile.SetTile >> 32);
            w[74 + i * 4] = (uint)(tile.Sl & 0xFFFF) | (uint)(tile.Tl & 0xFFFF) << 16;
            w[75 + i * 4] = (uint)(tile.Sh & 0xFFFF) | (uint)(tile.Th & 0xFFFF) << 16;
        }
    }

    public void Dispose()
    {
        foreach (var surface in _surfaces.Values)
        {
            surface.Buffer.Dispose();
        }

        _surfaces.Clear();
        _scratchDepth?.Dispose();
        _prims.Dispose();
        _tmem.Dispose();
        _params.Dispose();
        _kernel.Dispose();
        if (_ownsGpu)
        {
            _gpu.Dispose();
        }
    }

    private readonly record struct Target(uint ColorAddress, int Width, int Size, int Format, uint ZAddress);

    private readonly record struct SurfaceKey(uint Address, int Width, int Size);

    private sealed class Surface(SurfaceKey key, int rows, IGpuBuffer buffer)
    {
        public SurfaceKey Key { get; } = key;

        public int Rows { get; } = rows;

        public IGpuBuffer Buffer { get; } = buffer;

        public ulong Hash { get; set; }

        public bool HashValid { get; set; }

        public bool Touched { get; set; }

        public bool CheckedThisRun { get; set; }

        /// <summary>When a batch last drew into it (a count of the batches' writes).</summary>
        public long LastWrite { get; set; }
    }
}
