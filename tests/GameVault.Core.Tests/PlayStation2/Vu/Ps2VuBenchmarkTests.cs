using System.Buffers.Binary;
using System.Diagnostics;
using FluentAssertions;
using Xunit.Abstractions;
using static GameVault.Core.Tests.PlayStation2.Vu.VuOps;

namespace GameVault.Core.Tests.PlayStation2.Vu;

/// <summary>The VU's speed on a game-like transform loop, interpreted and recompiled.</summary>
public sealed class Ps2VuBenchmarkTests(ITestOutputHelper output)
{
    /// <summary>
    /// Reports VU1 cycles a second on a vertex transform (a matrix multiply through ACC, the perspective divide, FTOI4
    /// and a store per vertex), as games' VU1 programs do. Interpreted, then recompiled. Only with <c>GAMEVAULT_VU_BENCHMARK=1</c>, best in Release;
    /// it makes no timing assertion. The real VU1 runs 294.912 M cycles a second.
    /// </summary>
    [Fact]
    public void Benchmark_reports_vu1_cycles_per_second()
    {
        if (Environment.GetEnvironmentVariable("GAMEVAULT_VU_BENCHMARK") != "1")
        {
            return;
        }

        foreach (var recompiled in new[] { false, true })
        {
            var (_, vu1, _) = FakeVuHost.CreatePair();
            vu1.RecompilerEnabled = recompiled;
            var program = TransformLoop();
            for (var i = 0; i < program.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(vu1.MicroMemory.AsSpan(i * 8), program[i].Lower);
                BinaryPrimitives.WriteUInt32LittleEndian(vu1.MicroMemory.AsSpan(i * 8 + 4), program[i].Upper);
            }

            var one = BitConverter.SingleToUInt32Bits(1.0f);
            for (var i = 0; i < 4; i++)
            {
                for (var lane = 0; lane < 4; lane++)
                {
                    vu1.SetVf(10 + i, lane, BitConverter.SingleToUInt32Bits(i == lane ? 2.0f : 0.25f));
                }
            }

            var random = new Random(1);
            for (var q = 0; q < 1000; q++)
            {
                for (var lane = 0; lane < 4; lane++)
                {
                    var value = lane == 3 ? one : BitConverter.SingleToUInt32Bits((float)(random.NextDouble() * 200 - 100));
                    BinaryPrimitives.WriteUInt32LittleEndian(vu1.DataMemory.AsSpan(q * 16 + lane * 4), value);
                }
            }

            var watch = Stopwatch.StartNew();
            long cycles = 0;
            while (watch.ElapsedMilliseconds < 3000)
            {
                vu1.SetVi(1, 1000);
                vu1.SetVi(2, 0);
                vu1.StartMicro(0);
                while (vu1.Running)
                {
                    cycles += vu1.Run(1_000_000);
                }
            }

            cycles.Should().BePositive();
            output.WriteLine($"VU1, transform loop, {(recompiled ? "recompiled" : "interpreted")}: {cycles / watch.Elapsed.TotalSeconds / 1e6:F1} M cycles/s");
        }
    }

    /// <summary>The loop: VI1 vertices from VI2 (quadwords), transformed by the matrix in VF10–VF13, written back.</summary>
    internal static (uint Lower, uint Upper)[] TransformLoop() =>
    [
        (LQ(DestXYZW, VF(1), VI(2), 0), MULAbc(DestXYZW, FieldX, VF(10), VF(1))),
        (LowerNop, MADDAbc(DestXYZW, FieldY, VF(11), VF(1))),
        (LowerNop, MADDAbc(DestXYZW, FieldZ, VF(12), VF(1))),
        (LowerNop, MADDbc(DestXYZW, FieldW, VF(2), VF(13), VF(1))),
        (DIV(FieldW, VF(0), FieldW, VF(2)), UpperNop),
        (WAITQ(), UpperNop),
        (LowerNop, MULq(DestXYZ, VF(3), VF(2))),
        (LowerNop, FTOI4(DestXYZW, VF(4), VF(3))),
        (SQ(DestXYZW, VF(4), VI(2), 0), UpperNop),
        (IADDIU(VI(2), VI(2), 1), UpperNop),
        (ISUBIU(VI(1), VI(1), 1), UpperNop),
        (IBNE(VI(1), VI(0), -12), UpperNop),
        (LowerNop, UpperNop),
        (LowerNop, UpperNop | UpperE),
        (LowerNop, UpperNop),
    ];
}
