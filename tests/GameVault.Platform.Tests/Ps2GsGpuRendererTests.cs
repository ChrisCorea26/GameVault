using FluentAssertions;
using GameVault.Core.PlayStation2;
using GameVault.Core.Tests.PlayStation2.Gs;
using GameVault.Platform.Video;
using Xunit.Abstractions;
using static GameVault.Core.Tests.PlayStation2.Gs.GsRegs;

namespace GameVault.Platform.Tests;

/// <summary>
/// The PS2's GS on the GPU (docs/plans/gpu-renderers.md, milestone 3) against the software GS: scenes drawn by both at
/// 1×, comparing local memory word for word where the GPU's copy is in step, and the 2× picture against the 1× one.
/// The software GS interpolates in doubles and the kernel in floats, so a few words may differ by a step. Runs on
/// WARP on Windows and on a Vulkan software device elsewhere; without either the tests return early.
/// </summary>
[Collection(GpuKernelCollection.Name)]
public sealed class Ps2GsGpuRendererTests(ITestOutputHelper output)
{
    private sealed class Rig : IDisposable
    {
        public Rig(IGpuCompute gpu, int scale)
        {
            Gpu = gpu;
            Renderer = new Ps2GsGpuRenderer(gpu, Gs, scale);
            Gs.Backend = Renderer;
            W(Ps2Gs.RegPrModeCont, 1);
            W(Ps2Gs.RegFrame1, Frame(0, 4, Ps2Gs.PsmCt32));
            W(Ps2Gs.RegZbuf1, Zbuf(100, Ps2Gs.PsmZ32));
            W(Ps2Gs.RegScissor1, Scissor(0, 255, 0, 255));
            W(Ps2Gs.RegTest1, Test(zte: false));
        }

        public Ps2Gs Gs { get; } = new();

        public IGpuCompute Gpu { get; }

        public Ps2GsGpuRenderer Renderer { get; }

        public void W(int register, ulong value) => Gs.WriteRegister(register, value);

        public void Sprite(int x0, int y0, int x1, int y1, int r, int g, int b, int a = 0x80, uint z = 0)
        {
            W(Ps2Gs.RegPrim, Prim(Ps2Gs.PrimSprite));
            W(Ps2Gs.RegRgbaq, Rgbaq(r, g, b, a));
            W(Ps2Gs.RegXyz2, Xyz(x0 * 16, y0 * 16, z));
            W(Ps2Gs.RegXyz2, Xyz(x1 * 16, y1 * 16, z));
        }

        public void Upload(int dbp, int dbw, int psm, int x, int y, int w, int h, IReadOnlyList<uint> pixels)
        {
            W(Ps2Gs.RegBitBltBuf, BitBltBuf(0, 0, 0, dbp, dbw, psm));
            W(Ps2Gs.RegTrxPos, TrxPos(0, 0, x, y));
            W(Ps2Gs.RegTrxReg, TrxReg(w, h));
            W(Ps2Gs.RegTrxDir, 0);
            foreach (var word in GsFixture.Pack(Ps2Gs.BitsPerPixel(psm), pixels))
            {
                Gs.WriteHwReg(word);
            }
        }

        public void Dispose()
        {
            Renderer.Dispose();
            Gpu.Dispose();
        }
    }

    private Rig? NewRig(int scale = 1)
    {
        var gpu = N64RdpGpuRendererTests.CreateGpu(out var why);
        if (gpu is null)
        {
            output.WriteLine("Skipped: " + why);
            return null;
        }

        return new Rig(gpu, scale);
    }

    /// <summary>The words that differ between local memory and the GPU's copy (sample 0), over the pages in step.</summary>
    private (int Count, int Compared, string First) Compare(Rig rig)
    {
        var native = rig.Gs.LocalMemory;
        var gpu = rig.Renderer.ReadMemory();
        var count = 0;
        var compared = 0;
        var first = "";
        for (var page = 0; page < Ps2Gs.LocalMemorySize / 8192; page++)
        {
            if (!rig.Renderer.InStep(page))
            {
                continue;
            }

            for (var w = page * 2048; w < (page + 1) * 2048; w++)
            {
                compared++;
                var expected = BitConverter.ToUInt32(native, w * 4);
                if (gpu[w] != expected)
                {
                    if (count == 0)
                    {
                        first = $"word {w:X} (page {page}): GS {expected:X8}, GPU {gpu[w]:X8}";
                    }

                    count++;
                }
            }
        }

        output.WriteLine($"{count} of {compared} words differ{(count > 0 ? "; first " + first : "")}");
        return (count, compared, first);
    }

    [Fact]
    public void Untextured_sprites_and_triangles_match_the_software_gs()
    {
        using var rig = NewRig();
        if (rig is null)
        {
            return;
        }

        rig.Sprite(0, 0, 256, 256, 10, 20, 30);
        rig.Sprite(13, 7, 100, 90, 200, 100, 50);

        // A Gouraud triangle and a flat one, with Z.
        rig.W(Ps2Gs.RegTest1, Test(zte: true, ztst: 2));
        rig.W(Ps2Gs.RegPrim, Prim(Ps2Gs.PrimTriangle, iip: true));
        rig.W(Ps2Gs.RegRgbaq, Rgbaq(255, 0, 0));
        rig.W(Ps2Gs.RegXyz2, Xyz(20 * 16 + 5, 30 * 16 + 3, 1000));
        rig.W(Ps2Gs.RegRgbaq, Rgbaq(0, 255, 0));
        rig.W(Ps2Gs.RegXyz2, Xyz(220 * 16 + 9, 60 * 16, 50000));
        rig.W(Ps2Gs.RegRgbaq, Rgbaq(0, 0, 255));
        rig.W(Ps2Gs.RegXyz2, Xyz(90 * 16, 240 * 16 + 11, 300000));
        rig.W(Ps2Gs.RegPrim, Prim(Ps2Gs.PrimTriangle));
        rig.W(Ps2Gs.RegRgbaq, Rgbaq(9, 99, 199));
        rig.W(Ps2Gs.RegXyz2, Xyz(0, 0, 40000));
        rig.W(Ps2Gs.RegXyz2, Xyz(250 * 16, 10 * 16, 40000));
        rig.W(Ps2Gs.RegXyz2, Xyz(30 * 16, 200 * 16, 40000));

        var (count, compared, first) = Compare(rig);
        compared.Should().BePositive();
        count.Should().BeLessThan(compared / 1000 + 1, first);
        rig.Renderer.Dispatches.Should().BePositive();
    }

    [Fact]
    public void Textured_sprites_with_a_clut_and_bilinear_filtering_match_the_software_gs()
    {
        using var rig = NewRig();
        if (rig is null)
        {
            return;
        }

        // A 64×64 8-bit texture with a 256-colour CLUT, and a 32-bit one.
        var indices = new List<uint>();
        var colors = new List<uint>();
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                indices.Add((uint)((x * 4) ^ y) & 0xFF);
                colors.Add((uint)((x * 4) | ((y * 4) << 8) | (((x + y) * 2) << 16) | (0x80u << 24)));
            }
        }

        var clut = new List<uint>();
        for (var i = 0; i < 256; i++)
        {
            clut.Add((uint)(i | ((255 - i) << 8) | ((i * 7 & 0xFF) << 16) | (0x80u << 24)));
        }

        rig.Upload(0x2000, 1, Ps2Gs.PsmT8, 0, 0, 64, 64, indices);
        rig.Upload(0x3000, 1, Ps2Gs.PsmCt32, 0, 0, 16, 16, clut);
        rig.Upload(0x2800, 1, Ps2Gs.PsmCt32, 0, 0, 64, 64, colors);
        rig.Sprite(0, 0, 256, 256, 0, 0, 0);

        rig.W(Ps2Gs.RegTex0_1, Tex0(0x2000, 1, Ps2Gs.PsmT8, 6, 6, tfx: 1, cbp: 0x3000, cpsm: Ps2Gs.PsmCt32, cld: 1));
        rig.W(Ps2Gs.RegPrim, Prim(Ps2Gs.PrimSprite, tme: true, fst: true));
        rig.W(Ps2Gs.RegUv, Uv(0, 0));
        rig.W(Ps2Gs.RegXyz2, Xyz(0, 0));
        rig.W(Ps2Gs.RegUv, Uv(64 * 16, 64 * 16));
        rig.W(Ps2Gs.RegXyz2, Xyz(128 * 16, 128 * 16));

        rig.W(Ps2Gs.RegTex0_1, Tex0(0x2800, 1, Ps2Gs.PsmCt32, 6, 6, tfx: 0));
        rig.W(Ps2Gs.RegTex1_1, Tex1(true, true));
        rig.W(Ps2Gs.RegPrim, Prim(Ps2Gs.PrimSprite, tme: true, fst: true));
        rig.W(Ps2Gs.RegRgbaq, Rgbaq(128, 100, 255));
        rig.W(Ps2Gs.RegUv, Uv(3, 5));
        rig.W(Ps2Gs.RegXyz2, Xyz(128 * 16, 0));
        rig.W(Ps2Gs.RegUv, Uv(40 * 16, 60 * 16));
        rig.W(Ps2Gs.RegXyz2, Xyz(256 * 16, 200 * 16));

        // A perspective (STQ) triangle, blended over what's there.
        rig.W(Ps2Gs.RegAlpha1, Alpha(0, 1, 0, 1));
        rig.W(Ps2Gs.RegPrim, Prim(Ps2Gs.PrimTriangle, iip: true, tme: true, abe: true));
        rig.W(Ps2Gs.RegSt, St(0, 0));
        rig.W(Ps2Gs.RegRgbaq, Rgbaq(128, 128, 128, 0x40, 1));
        rig.W(Ps2Gs.RegXyz2, Xyz(10 * 16, 140 * 16));
        rig.W(Ps2Gs.RegSt, St(0.5f, 0));
        rig.W(Ps2Gs.RegRgbaq, Rgbaq(128, 128, 128, 0x40, 0.5f));
        rig.W(Ps2Gs.RegXyz2, Xyz(200 * 16, 150 * 16));
        rig.W(Ps2Gs.RegSt, St(0, 1.5f));
        rig.W(Ps2Gs.RegRgbaq, Rgbaq(128, 128, 128, 0x40, 2));
        rig.W(Ps2Gs.RegXyz2, Xyz(60 * 16, 250 * 16));

        var (count, compared, first) = Compare(rig);
        compared.Should().BePositive();
        count.Should().BeLessThan(compared / 200 + 1, first);
    }

    [Fact]
    public void A_render_target_read_back_as_a_texture_and_a_16_bit_dithered_frame_match_the_software_gs()
    {
        using var rig = NewRig();
        if (rig is null)
        {
            return;
        }

        // A gradient drawn at page 100, then sampled upside down into the frame (render to texture).
        rig.W(Ps2Gs.RegFrame1, Frame(100, 4, Ps2Gs.PsmCt32));
        rig.W(Ps2Gs.RegPrim, Prim(Ps2Gs.PrimTriangle, iip: true));
        rig.W(Ps2Gs.RegRgbaq, Rgbaq(0, 0, 0));
        rig.W(Ps2Gs.RegXyz2, Xyz(0, 0));
        rig.W(Ps2Gs.RegRgbaq, Rgbaq(255, 0, 0));
        rig.W(Ps2Gs.RegXyz2, Xyz(256 * 16, 0));
        rig.W(Ps2Gs.RegRgbaq, Rgbaq(0, 255, 0));
        rig.W(Ps2Gs.RegXyz2, Xyz(0, 256 * 16));
        rig.W(Ps2Gs.RegFrame1, Frame(0, 4, Ps2Gs.PsmCt32));
        rig.W(Ps2Gs.RegTex0_1, Tex0(100 * 32, 4, Ps2Gs.PsmCt32, 8, 8));
        rig.W(Ps2Gs.RegPrim, Prim(Ps2Gs.PrimSprite, tme: true, fst: true));
        rig.W(Ps2Gs.RegUv, Uv(0, 256 * 16));
        rig.W(Ps2Gs.RegXyz2, Xyz(0, 0));
        rig.W(Ps2Gs.RegUv, Uv(256 * 16, 0));
        rig.W(Ps2Gs.RegXyz2, Xyz(256 * 16, 256 * 16));

        // A 16-bit frame at page 200 with dithering, alpha test and FBMSK.
        rig.W(Ps2Gs.RegFrame1, Frame(200, 4, Ps2Gs.PsmCt16, 0x00070000));
        rig.W(Ps2Gs.RegDthe, 1);
        rig.W(Ps2Gs.RegDimx, 0x1234_5670_7654_3210);
        rig.W(Ps2Gs.RegTest1, Test(ate: true, atst: 5, aref: 0x30, afail: 1, zte: false));
        rig.W(Ps2Gs.RegPrim, Prim(Ps2Gs.PrimTriangle, iip: true));
        rig.W(Ps2Gs.RegRgbaq, Rgbaq(250, 3, 100, 0x10));
        rig.W(Ps2Gs.RegXyz2, Xyz(0, 0));
        rig.W(Ps2Gs.RegRgbaq, Rgbaq(7, 250, 30, 0x80));
        rig.W(Ps2Gs.RegXyz2, Xyz(255 * 16, 20 * 16));
        rig.W(Ps2Gs.RegRgbaq, Rgbaq(100, 100, 250, 0x60));
        rig.W(Ps2Gs.RegXyz2, Xyz(40 * 16, 255 * 16));

        var (count, compared, first) = Compare(rig);
        compared.Should().BePositive();
        count.Should().BeLessThan(compared / 200 + 1, first);
    }

    [Fact]
    public void The_picture_matches_the_software_gs_and_at_2x_each_block_holds_the_1x_pixel()
    {
        using var one = NewRig(1);
        using var two = NewRig(2);
        if (one is null || two is null)
        {
            return;
        }

        foreach (var rig in new[] { one, two })
        {
            rig.Sprite(0, 0, 256, 256, 40, 80, 120);
            rig.Sprite(16, 16, 128, 100, 250, 200, 10);
            rig.W(Ps2Gs.RegPrim, Prim(Ps2Gs.PrimTriangle, iip: true));
            rig.W(Ps2Gs.RegRgbaq, Rgbaq(255, 0, 0));
            rig.W(Ps2Gs.RegXyz2, Xyz(130 * 16, 20 * 16));
            rig.W(Ps2Gs.RegRgbaq, Rgbaq(0, 255, 0));
            rig.W(Ps2Gs.RegXyz2, Xyz(250 * 16, 120 * 16));
            rig.W(Ps2Gs.RegRgbaq, Rgbaq(0, 0, 255));
            rig.W(Ps2Gs.RegXyz2, Xyz(140 * 16, 220 * 16));

            // Circuit 1 shows the 256×224 frame.
            rig.Gs.WritePrivileged(Ps2Gs.PrivPmode, 0x0001 | (0xFFUL << 8) | (1UL << 5));
            rig.Gs.WritePrivileged(Ps2Gs.PrivDispFb1, (0UL) | (4UL << 9) | ((ulong)Ps2Gs.PsmCt32 << 15));
            rig.Gs.WritePrivileged(Ps2Gs.PrivDisplay1, (255UL << 32) | (223UL << 44));
        }

        var frame = new ushort[Ps2Gs.MaxFrameWidth * Ps2Gs.MaxFrameHeight];
        one.Gs.RenderFrame(frame, out var width, out var height);
        two.Gs.RenderFrame(frame, out _, out _);
        width.Should().Be(256);
        height.Should().Be(224);
        var picture = one.Renderer.ReadDisplay(out var w1, out var h1).ToArray();
        (w1, h1).Should().Be((256, 224));
        var differing = 0;
        for (var i = 0; i < width * height; i++)
        {
            var c = picture[i];
            var bgr555 = (ushort)(((c >> 19) & 0x1F) | (((c >> 11) & 0x1F) << 5) | (((c >> 3) & 0x1F) << 10));
            if (bgr555 != frame[i])
            {
                differing++;
            }
        }

        differing.Should().BeLessThan(width * height / 500 + 1, "the 1× picture is the software GS's");

        var scaled = two.Renderer.ReadDisplay(out var w2, out var h2).ToArray();
        (w2, h2).Should().Be((512, 448));
        var blocks = 0;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                // Away from edges (where the 1× pixel and its eight neighbours agree), the 2×2 block holds the 1× pixel.
                var c = picture[y * width + x];
                var uniform = true;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        uniform &= picture[(y + dy) * width + x + dx] == c;
                    }
                }

                if (!uniform)
                {
                    continue;
                }

                if (!Near(scaled[(y * 2) * w2 + (x * 2)], c))
                {
                    output.WriteLine($"({x}, {y}): 1× {c:X8}, 2× {scaled[(y * 2) * w2 + (x * 2)]:X8}");
                    blocks++;
                }
            }
        }

        blocks.Should().Be(0);
    }

    /// <summary>
    /// GameVault's PS2 homebrew through the whole console with the renderer attached at 1×: the GPU's picture matches
    /// the software GS's, frame after frame (but for pixels float interpolation rounds the other way).
    /// </summary>
    [Theory]
    [InlineData("gs-basic")]
    [InlineData("gs-texture")]
    [InlineData("vu1-path1")]
    public void The_homebrew_draws_the_same_picture_on_the_gpu(string name)
    {
        var gpu = N64RdpGpuRendererTests.CreateGpu(out var why);
        if (gpu is null)
        {
            output.WriteLine("Skipped: " + why);
            return;
        }

        using var _ = gpu;
        var console = new Ps2Console(Ps2Boot.FromElf(File.ReadAllBytes(ElfPath(name))));
        using var renderer = new Ps2GsGpuRenderer(gpu, console.Gs, 1);
        console.Gs.Backend = renderer;
        var worst = 0;
        for (var frame = 0; frame < 20; frame++)
        {
            console.RunFrame();
            var picture = renderer.ReadDisplay(out var width, out var height);
            (width * height).Should().Be(console.FrameBuffer.Length, $"frame {frame}");
            var differing = 0;
            for (var i = 0; i < picture.Length; i++)
            {
                var c = picture[i];
                var bgr555 = (ushort)(((c >> 19) & 0x1F) | (((c >> 11) & 0x1F) << 5) | (((c >> 3) & 0x1F) << 10));
                if (bgr555 != console.FrameBuffer[i])
                {
                    differing++;
                }
            }

            worst = Math.Max(worst, differing);
        }

        output.WriteLine($"{name}: at worst {worst} pixels differ in a frame; {renderer.Dispatches} dispatches");
        worst.Should().BeLessThan(console.FrameBuffer.Length / 100, "the GPU draws what the software GS draws");
    }

    private static string ElfPath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GameVault.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "tests", "Ps2Homebrew", "elf", name + ".elf");
    }

    /// <summary>Two ARGB colours within 2 a channel (float interpolation at another sample point).</summary>
    private static bool Near(uint a, uint b)
    {
        for (var shift = 0; shift < 24; shift += 8)
        {
            if (Math.Abs((int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF)) > 2)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Random scenes (as Ps2GsParallelTests draws them: buffers that overlap, render to texture, uploads, CLUT loads,
    /// copies and every primitive and format): after each, the GPU's copy matches local memory where it is in step,
    /// but for the few words float interpolation rounds the other way.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Random_scenes_match_the_software_gs(int seed)
    {
        using var rig = NewRig();
        if (rig is null)
        {
            return;
        }

        var random = new Random(seed);
        for (var step = 0; step < 300; step++)
        {
            foreach (var (register, value) in RandomStep(random))
            {
                rig.W(register, value);
            }
        }

        var (count, compared, first) = Compare(rig);
        output.WriteLine($"{rig.Renderer.Dispatches} dispatches, {rig.Renderer.Seeds} pages seeded");
        compared.Should().BePositive();
        count.Should().BeLessThan(compared / 100, first);
    }

    private static readonly int[] FramePsms = [Ps2Gs.PsmCt32, Ps2Gs.PsmCt24, Ps2Gs.PsmCt16, Ps2Gs.PsmCt16S];
    private static readonly int[] ZPsms = [Ps2Gs.PsmZ32, Ps2Gs.PsmZ24, Ps2Gs.PsmZ16, Ps2Gs.PsmZ16S];
    private static readonly int[] TexPsms =
        [Ps2Gs.PsmCt32, Ps2Gs.PsmCt24, Ps2Gs.PsmCt16, Ps2Gs.PsmT8, Ps2Gs.PsmT4, Ps2Gs.PsmT8H, Ps2Gs.PsmT4HL, Ps2Gs.PsmZ32, Ps2Gs.PsmZ16];
    private static readonly int[] Pages = [0, 0, 20, 40, 64, 100, 140, 300];

    /// <summary>A few register writes: a new state, a primitive, an upload or a copy (Ps2GsParallelTests's, on 256×256).</summary>
    private static List<(int Register, ulong Value)> RandomStep(Random random)
    {
        var writes = new List<(int, ulong)>();
        switch (random.Next(20))
        {
            case 0:
            {
                var fbp = Pages[random.Next(Pages.Length)];
                var fbw = new[] { 1, 2, 4 }[random.Next(3)];
                writes.Add((Ps2Gs.RegFrame1, Frame(fbp, fbw, FramePsms[random.Next(4)], random.Next(6) == 0 ? (uint)random.Next() : 0)));
                var zbp = Pages[random.Next(Pages.Length)] + 10;
                writes.Add((Ps2Gs.RegZbuf1, Zbuf(zbp, ZPsms[random.Next(4)], random.Next(4) == 0)));
                writes.Add((Ps2Gs.RegScissor1, Scissor(random.Next(0, 32), (fbw * 64) - 1, random.Next(0, 32), random.Next(32, 256))));
                break;
            }

            case 1:
                writes.Add((Ps2Gs.RegTest1, Test(random.Next(3) == 0, random.Next(8), random.Next(256), random.Next(4),
                    random.Next(5) == 0, random.Next(2), random.Next(4) != 0, random.Next(4))));
                writes.Add((Ps2Gs.RegAlpha1, Alpha(random.Next(3), random.Next(3), random.Next(3), random.Next(3), random.Next(256))));
                writes.Add((Ps2Gs.RegDthe, (ulong)random.Next(2)));
                writes.Add((Ps2Gs.RegColClamp, (ulong)random.Next(2)));
                writes.Add((Ps2Gs.RegScanMsk, (ulong)random.Next(4)));
                break;
            case 2:
            {
                var tbp = (Pages[random.Next(Pages.Length)] * 32) + (random.Next(3) == 0 ? random.Next(32) : 0);
                var psm = TexPsms[random.Next(TexPsms.Length)];
                var cbp = (Pages[random.Next(Pages.Length)] * 32) + random.Next(64);
                writes.Add((Ps2Gs.RegTex0_1, Tex0(tbp, new[] { 1, 2, 4, 8 }[random.Next(4)], psm, random.Next(3, 9), random.Next(3, 9),
                    random.Next(2) == 0, random.Next(4), cbp, random.Next(2) == 0 ? Ps2Gs.PsmCt32 : Ps2Gs.PsmCt16, random.Next(6) == 0,
                    random.Next(32), random.Next(6))));
                writes.Add((Ps2Gs.RegClamp1, Clamp(random.Next(4), random.Next(4), random.Next(64), random.Next(256), random.Next(64), random.Next(256))));
                writes.Add((Ps2Gs.RegTex1_1, Tex1(random.Next(2) == 0, random.Next(2) == 0)));
                writes.Add((Ps2Gs.RegTexClut, (ulong)random.Next(1, 4) | ((ulong)random.Next(4) << 6) | ((ulong)random.Next(64) << 12)));
                break;
            }

            case 3:
            {
                var psm = TexPsms[random.Next(TexPsms.Length)];
                var width = random.Next(1, 96);
                var height = random.Next(1, 64);
                writes.Add((Ps2Gs.RegBitBltBuf, BitBltBuf(0, 1, 0, (Pages[random.Next(Pages.Length)] * 32) + random.Next(32), random.Next(1, 6), psm)));
                writes.Add((Ps2Gs.RegTrxPos, TrxPos(0, 0, random.Next(200), random.Next(200))));
                writes.Add((Ps2Gs.RegTrxReg, TrxReg(width, height)));
                writes.Add((Ps2Gs.RegTrxDir, 0));
                var qwords = (((long)width * height * Ps2Gs.BitsPerPixel(psm)) + 63) / 64;
                for (var i = 0; i < qwords; i++)
                {
                    writes.Add((Ps2Gs.RegHwReg, (ulong)random.NextInt64()));
                }

                break;
            }

            case 4 when random.Next(3) == 0:
                writes.Add((Ps2Gs.RegBitBltBuf, BitBltBuf(Pages[random.Next(Pages.Length)] * 32, random.Next(1, 6), FramePsms[random.Next(4)],
                    Pages[random.Next(Pages.Length)] * 32, random.Next(1, 6), FramePsms[random.Next(4)])));
                writes.Add((Ps2Gs.RegTrxPos, TrxPos(random.Next(100), random.Next(100), random.Next(100), random.Next(100), random.Next(4))));
                writes.Add((Ps2Gs.RegTrxReg, TrxReg(random.Next(1, 64), random.Next(1, 64))));
                writes.Add((Ps2Gs.RegTrxDir, 2));
                break;
            default:
            {
                var type = random.Next(7);
                writes.Add((Ps2Gs.RegPrim, Prim(type, random.Next(2) == 0, random.Next(2) == 0, random.Next(5) == 0, random.Next(3) == 0, random.Next(2) == 0)));
                var big = random.Next(6) == 0;
                var cx = random.Next(0, 256) * 16;
                var cy = random.Next(0, 256) * 16;
                var spread = (big ? 200 : 40) * 16;
                for (var v = 0; v < 6; v++)
                {
                    writes.Add((Ps2Gs.RegRgbaq, Rgbaq(random.Next(256), random.Next(256), random.Next(256), random.Next(256), (float)(0.25 + random.NextDouble()))));
                    writes.Add((Ps2Gs.RegUv, Uv(random.Next(0, 4096), random.Next(0, 4096))));
                    writes.Add((Ps2Gs.RegSt, St((float)random.NextDouble() * 2, (float)random.NextDouble() * 2)));
                    writes.Add((Ps2Gs.RegFog, (ulong)random.Next(256) << 56));
                    var x = Math.Clamp(cx + random.Next(-spread, spread), 0, 0xFFFF);
                    var y = Math.Clamp(cy + random.Next(-spread, spread), 0, 0xFFFF);
                    writes.Add((Ps2Gs.RegXyz2, Xyz(x, y, (uint)random.Next())));
                }

                break;
            }
        }

        return writes;
    }
}
