using FluentAssertions;
using GameVault.Core.PlayStation2;
using static GameVault.Core.Tests.PlayStation2.Gs.GsRegs;

namespace GameVault.Core.Tests.PlayStation2.Gs;

/// <summary>
/// Drawing batches on several renderers gives the same local memory, byte for byte, as drawing each primitive at once:
/// random scenes (buffers that overlap, render to texture, uploads, CLUT loads, copies and read-backs) run on a lone
/// renderer, on bands run in reverse order (the order a scheduler could pick that is most unlike the lone renderer's)
/// and on bands run by real threads.
/// </summary>
public sealed class Ps2GsParallelTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_scenes_draw_the_same_on_several_renderers(int seed)
    {
        var serial = new Ps2Gs();
        var reversed = new Ps2Gs { ParallelWork = new ReversedBands(3) };
        var threaded = new Ps2Gs { ParallelWork = new ThreadedBands(5) };
        Ps2Gs[] all = [serial, reversed, threaded];
        foreach (var gs in all)
        {
            gs.WriteRegister(Ps2Gs.RegPrModeCont, 1);
        }

        var random = new Random(seed);
        var downloads = new UInt128[64];
        for (var step = 0; step < 2500; step++)
        {
            foreach (var (register, value) in RandomStep(random))
            {
                foreach (var gs in all)
                {
                    gs.WriteRegister(register, value);
                }
            }

            // A read-back now and then (it reads local memory, so the batch is drawn first).
            if (random.Next(400) == 0)
            {
                ReadBack(all, random, downloads);
            }

            if (random.Next(250) == 0 || step == 2499)
            {
                var expected = serial.LocalMemory;
                reversed.LocalMemory.AsSpan().SequenceEqual(expected).Should().BeTrue($"seed {seed}, step {step}: reversed bands");
                threaded.LocalMemory.AsSpan().SequenceEqual(expected).Should().BeTrue($"seed {seed}, step {step}: threads");
            }
        }

        reversed.ParallelPrimitives.Should().BePositive("the scenes have batches big enough to share out");
    }

    public static TheoryData<int> Seeds()
    {
        var seeds = new TheoryData<int>();
        for (var seed = 1; seed <= 24; seed++)
        {
            seeds.Add(seed);
        }

        return seeds;
    }

    /// <summary>
    /// A batch samples a texture, then draws over that texture: every band must sample it before any band draws over
    /// it, as one renderer would.
    /// </summary>
    [Fact]
    public void Drawing_over_a_texture_the_batch_samples_waits_for_the_sampling()
    {
        var serial = new Ps2Gs();
        var reversed = new Ps2Gs { ParallelWork = new ReversedBands(4) };
        foreach (var gs in new[] { serial, reversed })
        {
            gs.WriteRegister(Ps2Gs.RegPrModeCont, 1);
            gs.WriteRegister(Ps2Gs.RegTest1, Test(zte: false));
            gs.WriteRegister(Ps2Gs.RegScissor1, Scissor(0, 255, 0, 255));

            // A gradient at page 100 (the texture), then a sprite at page 0 that samples it upside down.
            gs.WriteRegister(Ps2Gs.RegFrame1, Frame(100, 4, Ps2Gs.PsmCt32));
            gs.WriteRegister(Ps2Gs.RegPrim, Prim(Ps2Gs.PrimTriangle, iip: true));
            gs.WriteRegister(Ps2Gs.RegRgbaq, Rgbaq(0, 0, 0));
            gs.WriteRegister(Ps2Gs.RegXyz2, Xyz(0, 0));
            gs.WriteRegister(Ps2Gs.RegRgbaq, Rgbaq(255, 0, 0));
            gs.WriteRegister(Ps2Gs.RegXyz2, Xyz(256 * 16, 0));
            gs.WriteRegister(Ps2Gs.RegRgbaq, Rgbaq(0, 255, 0));
            gs.WriteRegister(Ps2Gs.RegXyz2, Xyz(0, 256 * 16));
            _ = gs.LocalMemory; // the texture is drawn

            gs.WriteRegister(Ps2Gs.RegFrame1, Frame(0, 4, Ps2Gs.PsmCt32));
            gs.WriteRegister(Ps2Gs.RegTex0_1, Tex0(100 * 32, 4, Ps2Gs.PsmCt32, 8, 8));
            gs.WriteRegister(Ps2Gs.RegPrim, Prim(Ps2Gs.PrimSprite, tme: true, fst: true));
            gs.WriteRegister(Ps2Gs.RegUv, Uv(0, 256 * 16));
            gs.WriteRegister(Ps2Gs.RegXyz2, Xyz(0, 0));
            gs.WriteRegister(Ps2Gs.RegUv, Uv(256 * 16, 0));
            gs.WriteRegister(Ps2Gs.RegXyz2, Xyz(256 * 16, 256 * 16));

            // Then the texture's own pages are drawn over, untextured.
            gs.WriteRegister(Ps2Gs.RegFrame1, Frame(100, 4, Ps2Gs.PsmCt32));
            gs.WriteRegister(Ps2Gs.RegPrim, Prim(Ps2Gs.PrimSprite));
            gs.WriteRegister(Ps2Gs.RegRgbaq, Rgbaq(9, 9, 9));
            gs.WriteRegister(Ps2Gs.RegXyz2, Xyz(0, 0));
            gs.WriteRegister(Ps2Gs.RegXyz2, Xyz(256 * 16, 256 * 16));
        }

        reversed.LocalMemory.AsSpan().SequenceEqual(serial.LocalMemory).Should().BeTrue();
        reversed.ReadPixel(Ps2Gs.PsmCt32, 0, 4, 10, 250).Should().NotBe(0x0009_0909u, "the sprite sampled the gradient");
    }

    [Fact]
    public void The_homebrew_draws_the_same_on_several_renderers()
    {
        foreach (var name in new[] { "vu1-path1", "gs-basic", "gs-texture" })
        {
            var elf = File.ReadAllBytes(Homebrew.Ps2HomebrewRunner.ElfPath(name));
            var serial = new Ps2Console(Ps2Boot.FromElf(elf));
            var parallel = new Ps2Console(Ps2Boot.FromElf(elf));
            parallel.Gs.ParallelWork = new ThreadedBands(4);
            for (var frame = 0; frame < 30; frame++)
            {
                serial.RunFrame();
                parallel.RunFrame();
                parallel.FrameBuffer.SequenceEqual(serial.FrameBuffer).Should().BeTrue($"{name}, frame {frame}");
            }

            parallel.SaveSnapshot().AsSpan().SequenceEqual(serial.SaveSnapshot()).Should().BeTrue(name);
        }
    }

    private static readonly int[] FramePsms = [Ps2Gs.PsmCt32, Ps2Gs.PsmCt24, Ps2Gs.PsmCt16, Ps2Gs.PsmCt16S];
    private static readonly int[] ZPsms = [Ps2Gs.PsmZ32, Ps2Gs.PsmZ24, Ps2Gs.PsmZ16, Ps2Gs.PsmZ16S];
    private static readonly int[] TexPsms =
        [Ps2Gs.PsmCt32, Ps2Gs.PsmCt24, Ps2Gs.PsmCt16, Ps2Gs.PsmT8, Ps2Gs.PsmT4, Ps2Gs.PsmT8H, Ps2Gs.PsmT4HL, Ps2Gs.PsmZ32, Ps2Gs.PsmZ16];
    private static readonly int[] Pages = [0, 0, 20, 40, 64, 100, 140, 300];

    /// <summary>A few register writes: a new state, a primitive, an upload or a copy.</summary>
    private static List<(int Register, ulong Value)> RandomStep(Random random)
    {
        var writes = new List<(int, ulong)>();
        switch (random.Next(20))
        {
            case 0:
            {
                var fbp = Pages[random.Next(Pages.Length)];
                var fbw = new[] { 1, 2, 4, 5, 10 }[random.Next(5)];
                writes.Add((Ps2Gs.RegFrame1, Frame(fbp, fbw, FramePsms[random.Next(4)], random.Next(6) == 0 ? (uint)random.Next() : 0)));
                var zbp = random.Next(4) == 0 ? fbp + random.Next(0, 3) : Pages[random.Next(Pages.Length)] + 10;
                writes.Add((Ps2Gs.RegZbuf1, Zbuf(zbp, ZPsms[random.Next(4)], random.Next(4) == 0)));
                var x1 = random.Next(4) == 0 ? random.Next(0, 700) : (fbw * 64) - 1;
                writes.Add((Ps2Gs.RegScissor1, Scissor(random.Next(0, 32), x1, random.Next(0, 32), random.Next(32, 520))));
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
                // A texture: often a buffer that is also drawn to (render to texture, or feedback).
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
                // An upload into a random buffer.
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
                // A local copy.
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
                var cx = random.Next(0, 600) * 16;
                var cy = random.Next(0, 480) * 16;
                var spread = (big ? 300 : 40) * 16;
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

    private static void ReadBack(Ps2Gs[] all, Random random, UInt128[] buffer)
    {
        var bitblt = BitBltBuf(Pages[random.Next(Pages.Length)] * 32, random.Next(1, 6), FramePsms[random.Next(4)], 0, 1, 0);
        var pos = TrxPos(random.Next(100), random.Next(100), 0, 0);
        var reg = TrxReg(random.Next(1, 32), random.Next(1, 32));
        UInt128[]? expected = null;
        foreach (var gs in all)
        {
            gs.WritePrivileged(Ps2Gs.PrivBusDir, 1);
            gs.WriteRegister(Ps2Gs.RegBitBltBuf, bitblt);
            gs.WriteRegister(Ps2Gs.RegTrxPos, pos);
            gs.WriteRegister(Ps2Gs.RegTrxReg, reg);
            gs.WriteRegister(Ps2Gs.RegTrxDir, 1);
            var data = new List<UInt128>();
            int n;
            while ((n = gs.ReadDownload(buffer)) > 0)
            {
                data.AddRange(buffer.AsSpan(0, n).ToArray());
            }

            gs.WritePrivileged(Ps2Gs.PrivBusDir, 0);
            if (expected is null)
            {
                expected = [.. data];
            }
            else
            {
                data.Should().Equal(expected, "every GS reads back the same pixels");
            }
        }
    }

    /// <summary>
    /// Reports Mpixels a second for a game-like scene (2000 textured, bilinear, blended and Z-tested triangles a frame
    /// on a 640×448 frame) drawn by one renderer and by several. Only with <c>GAMEVAULT_GS_BENCHMARK=1</c>, in Release.
    /// </summary>
    [Fact]
    public void Benchmark_reports_pixels_per_second()
    {
        if (Environment.GetEnvironmentVariable("GAMEVAULT_GS_BENCHMARK") != "1")
        {
            return;
        }

        foreach (var threads in new[] { 1, 2, 4, Environment.ProcessorCount })
        {
            var gs = new Ps2Gs { ParallelWork = threads == 1 ? null : new ThreadedBands(threads) };
            gs.WriteRegister(Ps2Gs.RegPrModeCont, 1);
            gs.WriteRegister(Ps2Gs.RegFrame1, Frame(0, 10, Ps2Gs.PsmCt32));
            gs.WriteRegister(Ps2Gs.RegZbuf1, Zbuf(150, Ps2Gs.PsmZ24));
            gs.WriteRegister(Ps2Gs.RegScissor1, Scissor(0, 639, 0, 447));
            gs.WriteRegister(Ps2Gs.RegTest1, Test(zte: true, ztst: 2));
            gs.WriteRegister(Ps2Gs.RegAlpha1, Alpha(0, 1, 0, 1));
            gs.WriteRegister(Ps2Gs.RegTex0_1, Tex0(300 * 32, 4, Ps2Gs.PsmCt32, 8, 8, tfx: 0));
            gs.WriteRegister(Ps2Gs.RegTex1_1, Tex1(true, true));
            var random = new Random(1);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long pixels = 0;
            var frames = 0;
            while (watch.ElapsedMilliseconds < 3000)
            {
                for (var i = 0; i < 2000; i++)
                {
                    gs.WriteRegister(Ps2Gs.RegPrim, Prim(Ps2Gs.PrimTriangle, iip: true, tme: true, abe: true));
                    var cx = random.Next(16, 624);
                    var cy = random.Next(16, 432);
                    for (var v = 0; v < 3; v++)
                    {
                        gs.WriteRegister(Ps2Gs.RegRgbaq, Rgbaq(random.Next(256), random.Next(256), random.Next(256), 0x60, 1));
                        gs.WriteRegister(Ps2Gs.RegSt, St((float)random.NextDouble(), (float)random.NextDouble()));
                        gs.WriteRegister(Ps2Gs.RegXyz2, Xyz((cx + random.Next(-30, 30)) * 16, (cy + random.Next(-30, 30)) * 16, (uint)random.Next(1 << 24)));
                    }
                }

                _ = gs.LocalMemory; // the frame is drawn
                pixels += 2000 * 450;
                frames++;
            }

            output.WriteLine($"GS, {threads} thread(s): {pixels / watch.Elapsed.TotalSeconds / 1e6:F1} Mpixels/s (about), {frames / watch.Elapsed.TotalSeconds:F1} frames/s");
        }
    }

    /// <summary>Runs the bands one after another, last first.</summary>
    internal sealed class ReversedBands(int bands) : IParallelWork
    {
        public int Concurrency => bands;

        public void For(int count, Action<int> work)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                work(i);
            }
        }
    }

    /// <summary>Runs the bands on the thread pool, all at once.</summary>
    internal sealed class ThreadedBands(int bands) : IParallelWork
    {
        public int Concurrency => bands;

        public void For(int count, Action<int> work) =>
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = count }, work);
    }
}
