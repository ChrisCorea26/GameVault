using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.PlayStation2;
using GameVault.Core.State;
using static GameVault.Core.Tests.PlayStation2.Vu.VuOps;

namespace GameVault.Core.Tests.PlayStation2.Vu;

/// <summary>
/// The VU recompiler must do exactly what the interpreter does (outstanding-work plan §3). Random micro programs run on
/// two units, one interpreted and one recompiled, and the whole state (registers, flags, the pipeline, memory, cycles,
/// the PC) and everything they asked of the host must match after every slice.
/// </summary>
public sealed class Ps2VuRecompilerTests
{
    private sealed record Side(Ps2Vu Unit, FakeVuHost Host, Ps2Vu Vu1);

    private static Side Create(int index, bool recompiled, bool fast = false)
    {
        var (vu0, vu1, host) = FakeVuHost.CreatePair();
        foreach (var unit in new[] { vu0, vu1 })
        {
            unit.RecompilerEnabled = recompiled;
            unit.CompileThreshold = 1;
            unit.FastFloat = fast;
        }

        return new Side(index == 0 ? vu0 : vu1, host, vu1);
    }

    private static byte[] State(Side side)
    {
        var writer = new StateWriter();
        side.Unit.SaveState(writer);
        if (!ReferenceEquals(side.Unit, side.Vu1))
        {
            side.Vu1.SaveState(writer); // VU0 reaches VU1's registers through its data space
        }

        return writer.ToArray();
    }

    /// <summary>A random but plausible pair: mostly real instructions, some raw words, now and then an E, D, T or I bit.</summary>
    private static ulong RandomPair(Random random, int index)
    {
        int Vf() => random.Next(32);
        int Vi() => VI(random.Next(16));
        int Dest() => random.Next(1, 16);
        int Field() => random.Next(4);
        int Small() => random.Next(-8, 8);

        uint upper = random.Next(16) switch
        {
            0 => UpperNop,
            1 => ADD(Dest(), Vf(), Vf(), Vf()),
            2 => MULbc(Dest(), Field(), Vf(), Vf(), Vf()),
            3 => MADDAbc(Dest(), Field(), Vf(), Vf()),
            4 => MADD(Dest(), Vf(), Vf(), Vf()),
            5 => MSUBA(Dest(), Vf(), Vf()),
            6 => MULq(Dest(), Vf(), Vf()),
            7 => SUBi(Dest(), Vf(), Vf()),
            8 => FTOI4(Dest(), Vf(), Vf()),
            9 => ITOF12(Dest(), Vf(), Vf()),
            10 => MAXbc(Dest(), Field(), Vf(), Vf(), Vf()),
            11 => CLIP(Vf(), Vf()),
            12 => OPMULA(Vf(), Vf()),
            13 => OPMSUB(Vf(), Vf(), Vf()),
            14 => ABS(Dest(), Vf(), Vf()),
            _ => (uint)random.Next() & 0x07FF_FFFF, // anything, flag bits clear
        };

        uint lower = random.Next(28) switch
        {
            24 or 25 => ILW(Dest(), Vi(), Vi(), Small()), // an integer load is due 4 cycles later: VI stalls
            26 => IBEQ(Vi(), Vi(), Small()),
            27 => IADDI(Vi(), Vi(), Small()),
            0 => LowerNop,
            1 => LQ(Dest(), Vf(), Vi(), Small()),
            2 => SQ(Dest(), Vf(), Vi(), Small()),
            3 => IADDIU(Vi(), Vi(), random.Next(64)),
            4 => ISUBIU(Vi(), Vi(), random.Next(4)),
            5 => IADD(Vi(), Vi(), Vi()),
            6 => IBNE(Vi(), Vi(), Small()),
            7 => IBEQ(Vi(), Vi(), Small()),
            8 => IBLTZ(Vi(), Small()),
            9 => B(Small()),
            10 => DIV(Field(), Vf(), Field(), Vf()),
            11 => RSQRT(Field(), Vf(), Field(), Vf()),
            12 => WAITQ(),
            13 => MOVE(Dest(), Vf(), Vf()),
            14 => MR32(Dest(), Vf(), Vf()),
            15 => MFIR(Dest(), Vf(), Vi()),
            16 => MTIR(Vi(), Field(), Vf()),
            17 => FMAND(Vi(), Vi()),
            18 => FSAND(Vi(), random.Next(0x1000)),
            19 => ILW(Dest(), Vi(), Vi(), Small()),
            20 => ISW(Dest(), Vi(), Vi(), Small()),
            21 => LQI(Dest(), Vf(), Vi()),
            22 => index == 1 ? XGKICK(Vi()) : RNEXT(Dest(), Vf()),
            _ => (uint)random.Next() | (random.Next(2) == 0 ? 0x8000_0000u : 0), // anything
        };

        var bits = random.Next(100);
        if (bits < 3)
        {
            upper |= UpperE;
        }
        else if (bits < 4)
        {
            upper |= 1u << 28; // D
        }
        else if (bits < 5)
        {
            upper |= 1u << 27; // T
        }
        else if (bits < 8)
        {
            upper |= UpperI;
            lower = BitConverter.SingleToUInt32Bits((float)(random.NextDouble() * 10 - 5));
        }

        return ((ulong)upper << 32) | lower;
    }

    private static void Fill(Random random, Side a, Side b, int index)
    {
        var micro = a.Unit.MicroMemory;
        for (var at = 0; at < micro.Length; at += 8)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(micro.AsSpan(at), RandomPair(random, index));
        }

        random.NextBytes(a.Unit.DataMemory);
        for (var q = 0; q < a.Unit.DataMemory.Length / 4; q += 3)
        {
            // Plenty of ordinary floats among the random words.
            BinaryPrimitives.WriteUInt32LittleEndian(a.Unit.DataMemory.AsSpan(q * 4), BitConverter.SingleToUInt32Bits((float)(random.NextDouble() * 100 - 50)));
        }

        for (var r = 1; r < 32; r++)
        {
            for (var lane = 0; lane < 4; lane++)
            {
                a.Unit.SetVf(r, lane, BitConverter.SingleToUInt32Bits((float)(random.NextDouble() * 8 - 4)));
            }
        }

        for (var r = 1; r < 16; r++)
        {
            a.Unit.SetVi(r, (ushort)random.Next(0, 64));
        }

        a.Unit.DebugEnable = b.Unit.DebugEnable = random.Next(2) == 0;
        a.Unit.TrapEnable = b.Unit.TrapEnable = random.Next(2) == 0;
        a.Unit.MicroMemory.CopyTo(b.Unit.MicroMemory, 0);
        a.Unit.DataMemory.CopyTo(b.Unit.DataMemory, 0);
        var writer = new StateWriter();
        a.Unit.SaveState(writer);
        b.Unit.LoadState(new StateReader(writer.ToArray()));
    }

    /// <summary>48 programs with the exact float maths, and 16 more with the fast maths (ps2-full-speed.md §2) on both units.</summary>
    public static IEnumerable<object[]> Seeds() =>
        Enumerable.Range(0, 48).Select(seed => new object[] { seed, false }).Concat(Enumerable.Range(0, 16).Select(seed => new object[] { seed, true }));

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_programs_match_the_interpreter_slice_by_slice(int seed, bool fast)
    {
        var random = new Random(seed);
        var index = seed % 2;
        var interpreted = Create(index, recompiled: false, fast);
        var recompiled = Create(index, recompiled: true, fast);
        Fill(random, interpreted, recompiled, index);
        State(recompiled).Should().Equal(State(interpreted));

        var micro = interpreted.Unit.MicroMemory.Length;
        for (var slice = 0; slice < 400; slice++)
        {
            if (!interpreted.Unit.Running)
            {
                var pc = (uint)random.Next(micro / 8) * 8;
                if (random.Next(4) == 0)
                {
                    interpreted.Unit.ContinueMicro();
                    recompiled.Unit.ContinueMicro();
                }
                else
                {
                    interpreted.Unit.StartMicro(pc);
                    recompiled.Unit.StartMicro(pc);
                }
            }

            if (random.Next(40) == 0)
            {
                // New microcode, as an MPG upload brings: the next blocks there must be the new code.
                var at = random.Next(micro / 8) * 8;
                var pair = RandomPair(random, index);
                BinaryPrimitives.WriteUInt64LittleEndian(interpreted.Unit.MicroMemory.AsSpan(at), pair);
                BinaryPrimitives.WriteUInt64LittleEndian(recompiled.Unit.MicroMemory.AsSpan(at), pair);
            }

            var cycles = random.Next(1, 120);
            var ranA = interpreted.Unit.Run(cycles);
            var ranB = recompiled.Unit.Run(cycles);
            ranB.Should().Be(ranA, $"slice {slice}");
            State(recompiled).Should().Equal(State(interpreted), $"slice {slice}, seed {seed}");
            recompiled.Host.Kicks.Should().Equal(interpreted.Host.Kicks);
            recompiled.Host.Interrupts.Should().Equal(interpreted.Host.Interrupts);
        }

        recompiled.Unit.CompiledBlocks.Should().BePositive();
        recompiled.Unit.RecompiledCycles.Should().BePositive();
    }

    [Fact]
    public void The_transform_loop_runs_recompiled_and_matches()
    {
        var interpreted = Create(1, recompiled: false);
        var recompiled = Create(1, recompiled: true);
        var program = Ps2VuBenchmarkTests.TransformLoop();
        foreach (var side in new[] { interpreted, recompiled })
        {
            for (var i = 0; i < program.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(side.Unit.MicroMemory.AsSpan(i * 8), program[i].Lower);
                BinaryPrimitives.WriteUInt32LittleEndian(side.Unit.MicroMemory.AsSpan(i * 8 + 4), program[i].Upper);
            }

            for (var q = 0; q < 200; q++)
            {
                for (var lane = 0; lane < 4; lane++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(side.Unit.DataMemory.AsSpan(q * 16 + lane * 4), BitConverter.SingleToUInt32Bits(lane == 3 ? 1 : q * 0.5f - lane));
                }
            }

            for (var i = 0; i < 4; i++)
            {
                for (var lane = 0; lane < 4; lane++)
                {
                    side.Unit.SetVf(10 + i, lane, BitConverter.SingleToUInt32Bits(i == lane ? 2.0f : 0.25f));
                }
            }

            side.Unit.SetVi(1, 200);
            side.Unit.StartMicro(0);
            for (var n = 0; n < 100_000 && side.Unit.Running; n++)
            {
                side.Unit.Run(37); // slices that end mid-block
            }

            side.Unit.Running.Should().BeFalse("the program ends");
        }

        State(recompiled).Should().Equal(State(interpreted));
        recompiled.Unit.CompiledBlocks.Should().BePositive();
        recompiled.Unit.RecompiledCycles.Should().BeGreaterThan(recompiled.Unit.Cycles / 2, "the loop ran mostly in its block");
    }
}
