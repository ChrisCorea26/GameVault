using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.Nintendo64;
using GameVault.Core.State;
using GameVault.Core.Tests.PlayStation2.Gs;

namespace GameVault.Core.Tests.Nintendo64;

/// <summary>
/// The RDP drawing a command run on several replicas leaves RDRAM and its own state (save state included) byte for
/// byte as one RDP would: random command lists (random modes and combiners, buffers that overlap, loads from what was
/// just drawn, noise and dither, the COMBINED carry, command lists that get drawn over) run on a lone RDP, on bands run
/// in reverse order and on real threads.
/// </summary>
public sealed class N64RdpParallelTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const uint Commands = 0x010000;
    private static readonly uint[] Buffers = [0x100000, 0x140000, 0x180000, 0x1C0000, 0x200000];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_command_lists_draw_the_same_on_several_replicas(int seed)
    {
        var serial = new Machine(seed, null);
        var reversed = new Machine(seed, new Ps2GsParallelTests.ReversedBands(3));
        var threaded = new Machine(seed, new Ps2GsParallelTests.ThreadedBands(4));
        var random = new Random(seed * 31);
        for (var list = 0; list < 40; list++)
        {
            var words = RandomList(random, list);
            var inBuffer = random.Next(30) == 0;
            foreach (var machine in new[] { serial, reversed, threaded })
            {
                machine.Run(words, inBuffer);
            }

            var expected = serial.State();
            reversed.Host.Rdram.AsSpan().SequenceEqual(serial.Host.Rdram).Should().BeTrue($"seed {seed}, list {list}: RDRAM (reversed)");
            threaded.Host.Rdram.AsSpan().SequenceEqual(serial.Host.Rdram).Should().BeTrue($"seed {seed}, list {list}: RDRAM (threads)");
            Compare(expected, reversed.State(), $"seed {seed}, list {list}: state (reversed)");
            Compare(expected, threaded.State(), $"seed {seed}, list {list}: state (threads)");
            reversed.Host.Raised.Count.Should().Be(serial.Host.Raised.Count);
        }

    }

    /// <summary>Big rectangles and triangles, shared out for sure, still draw what one RDP draws.</summary>
    [Fact]
    public void Big_primitives_are_shared_out_and_draw_the_same()
    {
        var serial = new Machine(1, null);
        var threaded = new Machine(1, new Ps2GsParallelTests.ThreadedBands(4));
        var words = new List<ulong>
        {
            SetImage(0x3F, 0, 2, 320, Buffers[0]),
            0x3EUL << 56 | Buffers[2],
            Scissor(0, 0, 320, 240),
            0x2FUL << 56 | (0UL << 52) | (3UL << 38) | (3UL << 36) | (1UL << 5) | (1UL << 4), // one cycle, Z, no dither
            0x3CUL << 56 | 0x00FF_FFFF_FFFF_FFFFUL & 0x0012_7E24_F5FF_EFFF, // shade
        };
        var random = new Random(5);
        for (var i = 0; i < 40; i++)
        {
            AddTriangle(words, random, 320);
            words.Add(0x2FUL << 56 | (3UL << 52)); // fill
            words.Add(0x37UL << 56 | (uint)random.Next());
            words.Add(0x36UL << 56 | (ulong)(random.Next(160, 320) * 4) << 44 | (ulong)(random.Next(120, 240) * 4) << 32 | (ulong)(random.Next(0, 100) * 4) << 12 | (ulong)(random.Next(0, 100) * 4));
            words.Add(0x2FUL << 56 | (0UL << 52) | (3UL << 38) | (3UL << 36) | (1UL << 5) | (1UL << 4));
        }

        serial.Run(words, false);
        threaded.Run(words, false);
        threaded.Host.Rdram.AsSpan().SequenceEqual(serial.Host.Rdram).Should().BeTrue();
        Compare(serial.State(), threaded.State(), "the state");
        threaded.Rdp.ParallelPrimitives.Should().BePositive();
    }

    /// <summary>
    /// PeterLemon's RDP programs (115; see PeterLemonN64RdpTests) through the whole console, drawn by threads and by one
    /// RDP: the same RDRAM and save state every 10 frames.
    /// </summary>
    [N64TestRomTheory]
    [MemberData(nameof(RdpPrograms))]
    public void PeterLemons_rdp_programs_draw_the_same_on_several_replicas(string program)
    {
        var serial = PeterLemonN64Tests.Boot(program);
        var threaded = PeterLemonN64Tests.Boot(program);
        threaded.Rdp.ParallelWork = new Ps2GsParallelTests.ThreadedBands(4);
        for (var frame = 1; frame <= 40; frame++)
        {
            serial.RunFrame();
            threaded.RunFrame();
            if (frame % 10 == 0)
            {
                threaded.Rdram.AsSpan().SequenceEqual(serial.Rdram).Should().BeTrue($"{program}, frame {frame}: RDRAM");
                Compare(serial.SaveSnapshot(), threaded.SaveSnapshot(), $"{program}, frame {frame}: the save state");
            }
        }
    }

    /// <summary>
    /// Reports Mpixels a second for a heavy scene (shaded, textured, Z-buffered one-cycle triangles over a 320×240
    /// frame) drawn by one RDP and by several replicas. Only with <c>GAMEVAULT_RDP_BENCHMARK=1</c>, in Release.
    /// </summary>
    [Fact]
    public void Benchmark_reports_pixels_per_second()
    {
        if (Environment.GetEnvironmentVariable("GAMEVAULT_RDP_BENCHMARK") != "1")
        {
            return;
        }

        var random = new Random(3);
        var words = new List<ulong>
        {
            SetImage(0x3F, 0, 2, 320, Buffers[0]),
            0x3EUL << 56 | Buffers[2],
            Scissor(0, 0, 320, 240),
            0x2FUL << 56 | (0UL << 52) | (1UL << 45) | (3UL << 38) | (3UL << 36) | (1UL << 5) | (1UL << 4), // one cycle, bilinear, Z
            0x3CUL << 56 | 0x0012_7E24_F5FF_EFFF & 0x00FF_FFFF_FFFF_FFFFUL,
            SetImage(0x3D, 0, 2, 32, 0x300000),
            0x35UL << 56 | (2UL << 51) | (8UL << 41), // tile 0: RGBA16, line 8
            0x34UL << 56 | (31UL * 4 << 12) | (31UL * 4), // load 32×32
        };
        for (var i = 0; i < 300; i++)
        {
            var id = 0x0F; // shade, texture, Z
            var yh = random.Next(0, 160);
            var ym = yh + random.Next(10, 40);
            var yl = ym + random.Next(10, 40);
            var x = random.Next(40, 280);
            words.Add((ulong)(uint)id << 56 | (ulong)(uint)(yl * 4) << 32 | (ulong)(uint)(ym * 4) << 16 | (ulong)(uint)(yh * 4));
            words.Add((ulong)(uint)((x - 30) << 16) << 32 | (uint)(1 << 16));
            words.Add((ulong)(uint)((x + 30) << 16) << 32 | unchecked((uint)(-1 << 16)));
            words.Add((ulong)(uint)((x - 30) << 16) << 32 | unchecked((uint)(-1 << 16)));
            for (var c = 0; c < 18; c++)
            {
                words.Add((ulong)random.NextInt64() & 0x003F_003F_003F_003FUL);
            }
        }

        foreach (var threads in new[] { 1, 2, 4 })
        {
            var machine = new Machine(1, threads == 1 ? null : new Ps2GsParallelTests.ThreadedBands(threads));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var runs = 0;
            while (watch.ElapsedMilliseconds < 3000)
            {
                machine.Run(words, false);
                runs++;
            }

            output.WriteLine($"RDP, {threads} thread(s): {runs / watch.Elapsed.TotalSeconds:F1} scenes/s (300 triangles of about 1500 pixels)");
        }
    }

    public static TheoryData<string> RdpPrograms()
    {
        var programs = new TheoryData<string>();
        var manifest = Path.Combine(Harness.TestRomLocator.RepoRoot, "tests", "TestRoms", "manifest-n64.tsv");
        foreach (var line in File.ReadLines(manifest))
        {
            var path = line.Split('\t')[0];
            if (path.StartsWith("n64-peterlemon/RDP", StringComparison.Ordinal) && path.EndsWith(".N64", StringComparison.Ordinal))
            {
                programs.Add(path["n64-peterlemon/".Length..]);
            }
        }

        return programs;
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

    private static void Compare(byte[] expected, byte[] actual, string because)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new Xunit.Sdk.XunitException($"{because}: the states differ at byte {expected.AsSpan().CommonPrefixLength(actual)} of {expected.Length}");
        }
    }

    internal static List<ulong> RandomList(Random random, int index)
    {
        var words = new List<ulong>();
        var width = new[] { 64, 128, 160, 320 }[random.Next(4)];
        words.Add(SetImage(0x3F, random.Next(5), random.Next(4) == 0 ? 3 : 2, width, Buffers[random.Next(Buffers.Length)]));
        words.Add(0x3EUL << 56 | Buffers[random.Next(Buffers.Length)] + (uint)random.Next(4) * 0x8000); // Z image
        words.Add(Scissor(0, 0, random.Next(4) == 0 ? random.Next(32, 400) : width, random.Next(32, 240)));
        for (var i = 0; i < 160; i++)
        {
            switch (random.Next(24))
            {
                case 0:
                case 1:
                {
                    // Other modes: the cycle type, Z, dither, alpha compare, blending, texture options.
                    var bits = (ulong)random.NextInt64() & 0x00FF_FFFF_FFFF_FFFF & ~(3UL << 52);
                    bits |= (ulong)new[] { 0, 0, 1, 1, 2, 3 }[random.Next(6)] << 52;
                    words.Add(0x2FUL << 56 | bits);
                    break;
                }

                case 2:
                case 3:
                    words.Add(0x3CUL << 56 | ((ulong)random.NextInt64() & 0x00FF_FFFF_FFFF_FFFF)); // combine
                    break;
                case 4:
                    words.Add((ulong)(0x37 + random.Next(5)) << 56 | (uint)random.Next() | ((ulong)random.Next(256) << 32)); // fill/fog/blend/prim/env
                    break;
                case 5:
                    words.Add(0x35UL << 56 | ((ulong)random.NextInt64() & 0x00FF_FFFF_FFFF_FFFF) & ~(7UL << 24) | ((ulong)random.Next(8) << 24)); // set tile
                    words.Add(0x32UL << 56 | (ulong)random.Next(64) << 44 | (ulong)random.Next(64) << 32 | (ulong)random.Next(8) << 24 | (ulong)random.Next(64, 256) << 12 | (ulong)random.Next(64, 256));
                    break;
                case 6:
                    // A texture image (sometimes a buffer being drawn), then a load from it.
                    words.Add(SetImage(0x3D, random.Next(5), random.Next(1, 4), new[] { 16, 32, 64 }[random.Next(3)],
                        random.Next(3) == 0 ? Buffers[random.Next(Buffers.Length)] : 0x300000 + (uint)random.Next(64) * 0x100));
                    words.Add((ulong)new[] { 0x34, 0x33, 0x30 }[random.Next(3)] << 56 | (ulong)random.Next(16) << 44 | (ulong)random.Next(16) << 32
                        | (ulong)random.Next(8) << 24 | (ulong)random.Next(16, 128) << 12 | (ulong)random.Next(16, 128));
                    break;
                case 7:
                    words.Add(0x2EUL << 56 | (uint)random.Next()); // prim depth
                    words.Add(0x2CUL << 56 | ((ulong)random.NextInt64() & 0x00FF_FFFF_FFFF_FFFF)); // convert
                    break;
                case 8:
                    words.Add(0x29UL << 56); // sync full
                    break;
                case 9:
                case 10:
                case 11:
                    words.Add(0x36UL << 56 | (ulong)random.Next(width * 4) << 44 | (ulong)random.Next(960) << 32 | (ulong)random.Next(width * 4) << 12 | (ulong)random.Next(960));
                    break;
                case 12:
                case 13:
                    words.Add((ulong)(0x24 + random.Next(2)) << 56 | (ulong)random.Next(width * 4) << 44 | (ulong)random.Next(960) << 32 | (ulong)random.Next(8) << 24
                        | (ulong)random.Next(width * 4) << 12 | (ulong)random.Next(960));
                    words.Add((ulong)random.NextInt64());
                    break;
                default:
                    AddTriangle(words, random, width);
                    break;
            }
        }

        return words;
    }

    /// <summary>A triangle of a random kind (shade, texture, Z), its edges inside the image, its coefficients random.</summary>
    private static void AddTriangle(List<ulong> words, Random random, int width)
    {
        var id = 0x08 + random.Next(8);
        var yh = random.Next(0, 200);
        var ym = yh + random.Next(0, 40);
        var yl = ym + random.Next(1, 40);
        var x = random.Next(0, width);
        words.Add((ulong)(uint)id << 56 | (random.Next(2) == 0 ? 1UL << 55 : 0) | (ulong)(uint)random.Next(4) << 48
            | (ulong)(uint)(yl * 4 & 0x3FFF) << 32 | (ulong)(uint)(ym * 4 & 0x3FFF) << 16 | (ulong)(uint)(yh * 4 & 0x3FFF));
        for (var e = 0; e < 3; e++)
        {
            words.Add((ulong)(uint)((x + random.Next(-20, 20)) << 16) << 32 | (uint)(random.Next(-3, 4) << 16 | random.Next(65536)));
        }

        var coefficients = ((id & 4) != 0 ? 8 : 0) + ((id & 2) != 0 ? 8 : 0) + ((id & 1) != 0 ? 2 : 0);
        for (var c = 0; c < coefficients; c++)
        {
            words.Add((ulong)random.NextInt64() & 0x00FF_00FF_00FF_00FFUL);
        }
    }

    private static ulong SetImage(int command, int format, int size, int width, uint address) =>
        (ulong)(uint)command << 56 | (ulong)(uint)format << 53 | (ulong)(uint)size << 51 | (ulong)(uint)(width - 1) << 32 | address;

    private static ulong Scissor(int xh, int yh, int xl, int yl) =>
        0x2DUL << 56 | (ulong)(uint)(xh * 4) << 44 | (ulong)(uint)(yh * 4) << 32 | (ulong)(uint)(xl * 4) << 12 | (ulong)(uint)(yl * 4);

    /// <summary>An RDP with its RDRAM (random textures, the same for each seed) and a writer for its state.</summary>
    internal sealed class Machine
    {
        public readonly Host Host = new();
        public readonly N64Rdp Rdp;
        private readonly StateWriter _writer = new();

        public Machine(int seed, IParallelWork? work)
        {
            new Random(seed).NextBytes(Host.Rdram.AsSpan(0x100000, 0x300000));
            Rdp = new N64Rdp(Host) { ParallelWork = work };
        }

        /// <summary>Writes the list to RDRAM and runs it; <paramref name="inBuffer"/> puts it where a buffer may draw over it.</summary>
        public void Run(List<ulong> words, bool inBuffer)
        {
            var at = inBuffer ? Buffers[0] + 0x20000 : Commands;
            for (var i = 0; i < words.Count; i++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(Host.Rdram.AsSpan((int)at + i * 8), words[i]);
            }

            Rdp.WriteRegister(N64Rdp.RegisterBase, at);
            Rdp.WriteRegister(N64Rdp.RegisterBase + 4, at + (uint)(words.Count * 8));
        }

        public byte[] State()
        {
            _writer.Reset();
            Rdp.SaveState(_writer);
            return _writer.ToArray();
        }
    }

    internal sealed class Host : IN64RcpHost
    {
        private readonly byte[] _dmem = new byte[4096];

        public byte[] Rdram { get; } = new byte[8 * 1024 * 1024];

        public List<N64Interrupt> Raised { get; } = [];

        Span<byte> IN64RcpHost.Rdram => Rdram;

        public Span<byte> Dmem => _dmem;

        public void RaiseInterrupt(N64Interrupt source) => Raised.Add(source);

        public void ClearInterrupt(N64Interrupt source) => Raised.Remove(source);
    }
}
