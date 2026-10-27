using System.Buffers.Binary;
using GameVault.Core.Nintendo64;
using GameVault.Core.Tests.Harness;
using GameVault.Platform.Emulation;
using GameVault.Platform.Video;
using Xunit.Abstractions;

namespace GameVault.Platform.Tests;

/// <summary>
/// The N64 RDP on the GPU (docs/plans/gpu-renderers.md, milestone 2) against the software RDP: command lists drawn by
/// both at 1×, compared pixel by pixel (colour and Z images), and the 2× picture against the 1× one. The kernel runs on
/// Direct3D 11's WARP on Windows and on a Vulkan software device (SwiftShader or lavapipe) elsewhere; without either
/// the tests return early.
/// </summary>
public sealed class N64RdpGpuRendererTests(ITestOutputHelper output)
{
    private const uint Frame = 0x100000;
    private const uint ZBuffer = 0x200000;
    private const uint Texture = 0x300000;
    private const uint Commands = 0x010000;
    private const int Width = 64;
    private const int Height = 48;

    private const ulong CycleOne = 0UL << 52;
    private const ulong CycleTwo = 1UL << 52;
    private const ulong CycleCopy = 2UL << 52;
    private const ulong CycleFill = 3UL << 52;
    private const ulong NoDither = (3UL << 38) | (3UL << 36);
    private const ulong ForceBlend = 1UL << 14;
    private const ulong ImageRead = 1UL << 6;
    private const ulong ZUpdate = 1UL << 5;
    private const ulong ZCompare = 1UL << 4;
    private const ulong AntiAlias = 1UL << 3;
    private const ulong BiLerp0 = 1UL << 43;
    private const ulong Bilinear = 1UL << 45;

    internal static IGpuCompute? CreateGpu(out string why)
    {
        why = "";
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new D3D11Compute(forceWarp: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or SharpGen.Runtime.SharpGenException)
            {
                why = ex.Message;
                return null;
            }
        }

        return VulkanCompute.TryCreate(out why);
    }

    private sealed class Rig : IDisposable
    {
        private readonly RenderThreads? _threads;

        public Rig(IGpuCompute gpu, int scale, bool batching)
        {
            Gpu = gpu;
            Rdp = new N64Rdp(Host);
            if (batching)
            {
                // The software RDP then draws each run at its end, on several threads, after the GPU has seen it all.
                _threads = new RenderThreads(4);
                Rdp.ParallelWork = _threads;
            }

            Renderer = new N64RdpGpuRenderer(gpu, Host.Rdram, scale);
            Rdp.Backend = Renderer;
        }

        public Host Host { get; } = new();

        public N64Rdp Rdp { get; }

        public IGpuCompute Gpu { get; }

        public N64RdpGpuRenderer Renderer { get; }

        public List<ulong> List { get; } = [];

        public void Run()
        {
            for (var i = 0; i < List.Count; i++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(Host.Rdram.AsSpan((int)Commands + i * 8), List[i]);
            }

            Rdp.WriteRegister(N64Rdp.RegisterBase, Commands);
            Rdp.WriteRegister(N64Rdp.RegisterBase + 4, Commands + (uint)(List.Count * 8));
            List.Clear();
        }

        public void Dispose()
        {
            Renderer.Dispose();
            Gpu.Dispose();
            _threads?.Dispose();
        }
    }

    private Rig? NewRig(int scale = 1, bool batching = false)
    {
        var gpu = CreateGpu(out var why);
        if (gpu is null)
        {
            output.WriteLine("Skipped: " + why);
            return null;
        }

        return new Rig(gpu, scale, batching);
    }

    /// <summary>Compares the GPU surface (at 1×) with RDRAM's image; returns how many pixels differ and the first.</summary>
    private static (int Count, string First) Compare(Rig rig, uint address, int width, int height, int size)
    {
        var surface = rig.Renderer.ReadSurface(address, width, size, out var rows);
        if (surface is null)
        {
            return (-1, "no surface");
        }

        var bytes = size == N64Rdp.Size32 ? 4 : size == N64Rdp.Size8 ? 1 : 2;
        var count = 0;
        var first = "";
        for (var y = 0; y < Math.Min(height, rows); y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (int)address + (y * width + x) * bytes;
                uint expected = bytes switch
                {
                    4 => BinaryPrimitives.ReadUInt32BigEndian(rig.Host.Rdram.AsSpan(at)),
                    1 => rig.Host.Rdram[at],
                    _ => BinaryPrimitives.ReadUInt16BigEndian(rig.Host.Rdram.AsSpan(at)),
                };
                var actual = surface[y * width + x];
                if (actual != expected)
                {
                    if (count == 0)
                    {
                        first = $"({x}, {y}): RDP {expected:X}, GPU {actual:X}";
                    }

                    count++;
                }
            }
        }

        return (count, first);
    }

    private static void Setup16(Rig rig, bool z = false)
    {
        rig.List.Add(SetColorImage(N64Rdp.FormatRgba, N64Rdp.Size16, Width, Frame));
        rig.List.Add(Scissor(0, 0, Width, Height));
        if (z)
        {
            rig.List.Add(0x3EUL << 56 | ZBuffer);
            rig.List.Add(OtherModes(CycleFill));
            rig.List.Add(SetColorImage(N64Rdp.FormatRgba, N64Rdp.Size16, Width, ZBuffer));
            rig.List.Add(0x37UL << 56 | 0xFFFCFFFC);
            rig.List.Add(FillRect(0, 0, Width - 1, Height - 1));
            rig.List.Add(SetColorImage(N64Rdp.FormatRgba, N64Rdp.Size16, Width, Frame));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fill_rectangles_match_the_software_rdp(bool batching)
    {
        using var rig = NewRig(batching: batching);
        if (rig is null)
        {
            return;
        }

        Setup16(rig);
        rig.List.Add(OtherModes(CycleFill));
        rig.List.Add(0x37UL << 56 | 0x12345678);
        rig.List.Add(FillRect(10, 10, 19, 19));
        rig.List.Add(0x37UL << 56 | 0xF801F801);
        rig.List.Add(FillRect(3, 30, 40, 44));
        rig.Run();

        rig.Renderer.BatchesDrawn.Should().BePositive();
        var (count, first) = Compare(rig, Frame, Width, Height, N64Rdp.Size16);
        count.Should().Be(0, first);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shaded_z_buffered_triangles_match_the_software_rdp(bool batching)
    {
        using var rig = NewRig(batching: batching);
        if (rig is null)
        {
            return;
        }

        Setup16(rig, z: true);
        rig.List.Add(OtherModes(CycleOne | ZCompare | ZUpdate | (1UL << 30 >> 30) * 0));
        rig.List.Add(CombineShade());
        AddShadedTriangle(rig, leftMajor: false, 4, 40, 30, 4, 5, 60, 0x00FF0000, 0x0000FF00, 0x000000FF, z: 0x3000000, dzdx: 0x10000);
        AddShadedTriangle(rig, leftMajor: true, 2, 44, 20, 8, 50, 50, 0xFFFF0000, 0x00FFFF00, 0xFF00FF00, z: 0x1000000, dzdx: 0x8000);
        rig.Run();

        rig.Renderer.BatchesDrawn.Should().BePositive();
        var (count, first) = Compare(rig, Frame, Width, Height, N64Rdp.Size16);
        count.Should().Be(0, "colour: " + first);
        var (zCount, zFirst) = Compare(rig, ZBuffer, Width, Height, -1);
        zCount.Should().Be(0, "Z: " + zFirst);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Textured_and_blended_rectangles_match_the_software_rdp(bool batching)
    {
        using var rig = NewRig(batching: batching);
        if (rig is null)
        {
            return;
        }

        // A 16×16 RGBA16 texture of gradients.
        for (var i = 0; i < 256; i++)
        {
            var r = (i & 15) * 2;
            var g = (i >> 4) * 2;
            BinaryPrimitives.WriteUInt16BigEndian(rig.Host.Rdram.AsSpan((int)Texture + i * 2), (ushort)(r << 11 | g << 6 | 20 << 1 | (i & 1)));
        }

        Setup16(rig);
        rig.List.Add(OtherModes(CycleFill));
        rig.List.Add(0x37UL << 56 | 0x7BDE7BDE);
        rig.List.Add(FillRect(0, 0, Width - 1, Height - 1));
        rig.List.Add(SetTextureImage(N64Rdp.FormatRgba, N64Rdp.Size16, 16, Texture));
        rig.List.Add(SetTile(N64Rdp.FormatRgba, N64Rdp.Size16, line: 4, tmem: 0, tile: 0));
        rig.List.Add(LoadTile(0, 0, 0, 15, 15));
        rig.List.Add(SetTileSize(0, 0, 0, 15, 15));

        // Point sampled, one cycle.
        rig.List.Add(OtherModes(CycleOne | NoDither | BiLerp0));
        rig.List.Add(CombineTexel0());
        AddTextureRectangle(rig, 0, 2, 2, 34, 34, s: 0, t: 0, dsdx: 1 << 9, dtdy: 1 << 9);

        // Bilinear, blended at half alpha with what's there.
        rig.List.Add(OtherModes(CycleOne | NoDither | Bilinear | BiLerp0 | ForceBlend | ImageRead | (1UL << 22) | (0UL << 26) | (0UL << 18)));
        rig.List.Add(CombineTexel0PrimAlpha());
        rig.List.Add(0x3AUL << 56 | 0x80FF8080);
        AddTextureRectangle(rig, 0, 20, 10, 60, 46, s: 3 << 4, t: 1 << 4, dsdx: 3 << 8, dtdy: 3 << 8);
        rig.Run();

        rig.Renderer.BatchesDrawn.Should().BePositive();
        var (count, first) = Compare(rig, Frame, Width, Height, N64Rdp.Size16);
        count.Should().Be(0, first);
    }

    [Fact]
    public void At_2x_each_pixel_the_primitive_covers_wholly_holds_the_1x_pixel()
    {
        using var one = NewRig(1);
        using var two = NewRig(2);
        if (one is null || two is null)
        {
            return;
        }

        foreach (var rig in new[] { one, two })
        {
            Setup16(rig);
            rig.List.Add(OtherModes(CycleFill));
            rig.List.Add(0x37UL << 56 | 0x00010001);
            rig.List.Add(FillRect(0, 0, Width - 1, Height - 1));
            rig.List.Add(OtherModes(CycleOne | NoDither));
            rig.List.Add(CombineShade());
            AddShadedTriangle(rig, leftMajor: false, 4, 40, 30, 4, 5, 60, 0x00FF0000, 0x0000FF00, 0x000000FF, z: 0, dzdx: 0);
            rig.Run();
        }

        var native = one.Renderer.ReadSurface(Frame, Width, N64Rdp.Size16, out _)!;
        var scaled = two.Renderer.ReadSurface(Frame, Width, N64Rdp.Size16, out _)!;
        var mismatched = 0;
        for (var y = 1; y < Height - 1; y++)
        {
            for (var x = 1; x < Width - 1; x++)
            {
                // Inside the triangle (and its neighbours too), all four 2× pixels draw; compare their colour.
                var inside = native[y * Width + x] != 0x0001 && native[(y - 1) * Width + x] != 0x0001 && native[(y + 1) * Width + x] != 0x0001 &&
                             native[y * Width + x - 1] != 0x0001 && native[y * Width + x + 1] != 0x0001;
                if (!inside)
                {
                    continue;
                }

                var c = native[y * Width + x] >> 1;
                var c2 = scaled[(y * 2) * Width * 2 + x * 2] >> 1;
                if (Math.Abs((int)((c >> 10) & 31) - (int)((c2 >> 10) & 31)) > 1 || Math.Abs((int)((c >> 5) & 31) - (int)((c2 >> 5) & 31)) > 1 ||
                    Math.Abs((int)(c & 31) - (int)(c2 & 31)) > 1)
                {
                    mismatched++;
                }
            }
        }

        mismatched.Should().Be(0);
    }

    /// <summary>
    /// PeterLemon's RDP test programs through the whole console with the GPU renderer attached: after they draw, the
    /// GPU's picture of the framebuffer the VI shows matches the software RDP's. Opt-in, with
    /// <c>GAMEVAULT_N64_GPU=all</c> (or a part of the programs' paths): software Vulkan devices run the kernel's every
    /// branch for every pixel, so a whole-screen batch takes them most of a minute.
    /// </summary>
    [Fact]
    public void PeterLemons_rdp_programs_draw_the_same_on_the_gpu()
    {
        var select = Environment.GetEnvironmentVariable("GAMEVAULT_N64_GPU");
        var root = Path.Combine(TestRomLocator.CacheDirectory, "n64-peterlemon", "RDP");
        if (string.IsNullOrEmpty(select))
        {
            output.WriteLine("Skipped: set GAMEVAULT_N64_GPU=all (or part of a program's path) to run it");
            return;
        }

        if (!Directory.Exists(root))
        {
            output.WriteLine("Skipped: PeterLemon's N64 programs aren't fetched (tests/TestRoms/fetch-test-roms.sh manifest-n64.tsv)");
            return;
        }

        var gpu = CreateGpu(out var why);
        if (gpu is null)
        {
            output.WriteLine("Skipped: " + why);
            return;
        }

        using var _ = gpu;
        var failures = new List<string>();
        var programs = Directory.GetFiles(root, "*.N64", SearchOption.AllDirectories).Order().ToList();
        if (select != "all")
        {
            programs = programs.Where(p => p.Contains(select, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        foreach (var program in programs)
        {
            var console = new N64Console(new N64Cartridge(File.ReadAllBytes(program)));
            console.Power();
            using var renderer = new N64RdpGpuRenderer(gpu, console.Rdram, 1);
            console.Rdp.Backend = renderer;
            var settled = int.MaxValue;

            // Until a few frames after the RDP first draws (most programs draw once, then wait).
            for (var frame = 0; frame < 60 && (renderer.BatchesDrawn == 0 || frame < settled); frame++)
            {
                console.RunFrame();
                if (renderer.BatchesDrawn > 0 && settled == int.MaxValue)
                {
                    settled = frame + 3;
                }
            }

            output.WriteLine($"{Path.GetRelativePath(root, program)}: {renderer.BatchesDrawn} batches");
            var video = console.VideoOutput;
            var picture = renderer.ReadDisplay(video, out var w, out var h);
            var name = Path.GetRelativePath(root, program);
            if (video.Blanked)
            {
                continue;
            }

            if (picture.IsEmpty)
            {
                failures.Add($"{name}: no GPU picture");
                continue;
            }

            var differing = 0;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var address = (int)video.Origin + (y * video.Stride + x) * video.BytesPerPixel;
                    uint expected;
                    if (video.BytesPerPixel == 4)
                    {
                        expected = 0xFF000000u | (BinaryPrimitives.ReadUInt32BigEndian(console.Rdram.AsSpan(address)) >> 8);
                    }
                    else
                    {
                        int c = BinaryPrimitives.ReadUInt16BigEndian(console.Rdram.AsSpan(address));
                        expected = 0xFF000000u | (uint)(Expand5((c >> 11) & 31) << 16 | Expand5((c >> 6) & 31) << 8 | Expand5((c >> 1) & 31));
                    }

                    if (picture[y * w + x] != expected)
                    {
                        differing++;
                    }
                }
            }

            if (differing > 0)
            {
                failures.Add($"{name}: {differing} of {w * h} pixels differ");
            }
        }

        output.WriteLine($"{programs.Count} programs, {failures.Count} differ");
        foreach (var failure in failures)
        {
            output.WriteLine(failure);
        }

        failures.Should().BeEmpty();
    }

    private static int Expand5(int v) => (v << 3) | (v >> 2);

    // ---- Commands (as in GameVault.Core.Tests' N64RdpTests) ----

    private static ulong SetColorImage(int format, int size, int width, uint address) =>
        0x3FUL << 56 | (ulong)(uint)format << 53 | (ulong)(uint)size << 51 | (ulong)(uint)(width - 1) << 32 | address;

    private static ulong SetTextureImage(int format, int size, int width, uint address) =>
        0x3DUL << 56 | (ulong)(uint)format << 53 | (ulong)(uint)size << 51 | (ulong)(uint)(width - 1) << 32 | address;

    private static ulong Scissor(int xh, int yh, int xl, int yl) =>
        0x2DUL << 56 | (ulong)(uint)(xh * 4) << 44 | (ulong)(uint)(yh * 4) << 32 | (ulong)(uint)(xl * 4) << 12 | (ulong)(uint)(yl * 4);

    private static ulong OtherModes(ulong bits) => 0x2FUL << 56 | bits;

    private static ulong FillRect(int xh, int yh, int xl, int yl) =>
        0x36UL << 56 | (ulong)(uint)(xl * 4) << 44 | (ulong)(uint)(yl * 4) << 32 | (ulong)(uint)(xh * 4) << 12 | (ulong)(uint)(yh * 4);

    private static ulong SetTile(int format, int size, int line, int tmem, int tile, int palette = 0) =>
        0x35UL << 56 | (ulong)(uint)format << 53 | (ulong)(uint)size << 51 | (ulong)(uint)line << 41 | (ulong)(uint)tmem << 32 | (ulong)(uint)tile << 24 |
        (ulong)(uint)palette << 20;

    private static ulong SetTileSize(int tile, int sl, int tl, int sh, int th) =>
        0x32UL << 56 | (ulong)(uint)(sl * 4) << 44 | (ulong)(uint)(tl * 4) << 32 | (ulong)(uint)tile << 24 | (ulong)(uint)(sh * 4) << 12 | (ulong)(uint)(th * 4);

    private static ulong LoadTile(int tile, int sl, int tl, int sh, int th) =>
        0x34UL << 56 | (ulong)(uint)(sl * 4) << 44 | (ulong)(uint)(tl * 4) << 32 | (ulong)(uint)tile << 24 | (ulong)(uint)(sh * 4) << 12 | (ulong)(uint)(th * 4);

    private static void AddTextureRectangle(Rig rig, int tile, int xh, int yh, int xl, int yl, int s, int t, int dsdx, int dtdy)
    {
        rig.List.Add(0x24UL << 56 | (ulong)(uint)(xl * 4) << 44 | (ulong)(uint)(yl * 4) << 32 | (ulong)(uint)tile << 24 | (ulong)(uint)(xh * 4) << 12 | (ulong)(uint)(yh * 4));
        rig.List.Add((ulong)(ushort)s << 48 | (ulong)(ushort)t << 32 | (ulong)(ushort)dsdx << 16 | (ushort)dtdy);
    }

    /// <summary>
    /// A shaded, Z-buffered triangle (command $0D) with integer edges, red/green/blue changing along X by the given
    /// per-pixel steps (s15.16 words packed as RGBA steps), and depth with a per-pixel slope.
    /// </summary>
    private static void AddShadedTriangle(Rig rig, bool leftMajor, int yh, int yl, int ym, int xh, int xm, int xl,
        uint startRgba, uint drdx, uint dgdx, int z, int dzdx)
    {
        // Edges: H from (xh, yh) to (xl, yl), M from (xh, yh) to (xm, ym), L from (xm, ym) to (xl, yl), in 16.16 slopes.
        var dxhdy = ((xl - xh) << 16) / Math.Max(1, yl - yh);
        var dxmdy = ((xm - xh) << 16) / Math.Max(1, ym - yh);
        var dxldy = ((xl - xm) << 16) / Math.Max(1, yl - ym);
        rig.List.Add(0x0DUL << 56 | (leftMajor ? 1UL << 55 : 0) | (ulong)(uint)(yl * 4 & 0x3FFF) << 32 | (ulong)(uint)(ym * 4 & 0x3FFF) << 16 |
                     (ulong)(uint)(yh * 4 & 0x3FFF));
        rig.List.Add((ulong)(uint)(xm << 16) << 32 | (uint)dxldy);
        rig.List.Add((ulong)(uint)(xh << 16) << 32 | (uint)dxhdy);
        rig.List.Add((ulong)(uint)(xh << 16) << 32 | (uint)dxmdy);

        // Shade: start values (integer parts), per-pixel steps, fractions, then DxDe and DxDy (here: following the
        // major edge's slope, so the colour is constant down the edge).
        ulong Pack(uint a, uint b, uint c, uint d) => (ulong)(a & 0xFFFF) << 48 | (ulong)(b & 0xFFFF) << 32 | (ulong)(c & 0xFFFF) << 16 | (d & 0xFFFF);
        var r = startRgba >> 24;
        var g = (startRgba >> 16) & 0xFF;
        var b = (startRgba >> 8) & 0xFF;
        var a = startRgba & 0xFF;
        rig.List.Add(Pack(r, g, b, a));
        rig.List.Add(Pack(drdx >> 16, dgdx >> 16, 0, 0));
        rig.List.Add(Pack(0, 0, 0, 0));
        rig.List.Add(Pack(drdx & 0xFFFF, dgdx & 0xFFFF, 0, 0));
        rig.List.Add(Pack(0, 0, 0, 0));
        rig.List.Add(Pack(0, 0x0002, 0, 0));
        rig.List.Add(Pack(0, 0, 0, 0));
        rig.List.Add(Pack(0, 0x8000, 0, 0));

        // Z: value and DzDx, DzDe and DzDy.
        rig.List.Add((ulong)(uint)z << 32 | (uint)dzdx);
        rig.List.Add((ulong)(uint)(dzdx / 2) << 32 | (uint)(dzdx / 3));
    }

    private static ulong Combine(int subA0, int mul0, int subAa0, int mula0, int subA1, int mul1, int subB0, int subB1, int subAa1, int mula1,
        int add0, int subBa0, int adda0, int add1, int subBa1, int adda1) =>
        0x3CUL << 56 | (ulong)(uint)subA0 << 52 | (ulong)(uint)mul0 << 47 | (ulong)(uint)subAa0 << 44 | (ulong)(uint)mula0 << 41 | (ulong)(uint)subA1 << 37 |
        (ulong)(uint)mul1 << 32 | (ulong)(uint)subB0 << 28 | (ulong)(uint)subB1 << 24 | (ulong)(uint)subAa1 << 21 | (ulong)(uint)mula1 << 18 | (ulong)(uint)add0 << 15 |
        (ulong)(uint)subBa0 << 12 | (ulong)(uint)adda0 << 9 | (ulong)(uint)add1 << 6 | (ulong)(uint)subBa1 << 3 | (ulong)(uint)adda1;

    private static ulong CombineShade() => Combine(15, 31, 7, 7, 15, 31, 15, 15, 7, 7, 4, 7, 4, 4, 7, 4);

    private static ulong CombineTexel0() => Combine(15, 31, 7, 7, 15, 31, 15, 15, 7, 7, 1, 7, 1, 1, 7, 1);

    /// <summary>Colour = texel 0, alpha = primitive alpha.</summary>
    private static ulong CombineTexel0PrimAlpha() => Combine(15, 31, 7, 7, 15, 31, 15, 15, 7, 7, 1, 7, 3, 1, 7, 3);

    internal sealed class Host : IN64RcpHost
    {
        private readonly byte[] _dmem = new byte[4096];

        public byte[] Rdram { get; } = new byte[8 * 1024 * 1024];

        Span<byte> IN64RcpHost.Rdram => Rdram;

        public Span<byte> Dmem => _dmem;

        public void RaiseInterrupt(N64Interrupt source)
        {
        }

        public void ClearInterrupt(N64Interrupt source)
        {
        }
    }
}
