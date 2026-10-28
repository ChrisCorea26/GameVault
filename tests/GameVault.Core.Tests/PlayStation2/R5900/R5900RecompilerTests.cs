using FluentAssertions;
using GameVault.Core.Cpu.R5900;
using GameVault.Core.State;

namespace GameVault.Core.Tests.PlayStation2.R5900;

/// <summary>
/// The EE's block recompiler must do exactly what the interpreter does: random programs run on two CPUs, one
/// interpreting and one recompiling, and after every slice their whole state (registers, COP0, the FPU, Cycles, the
/// scratchpad) and memory must be identical.
/// </summary>
public sealed class R5900RecompilerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const uint CodeBase = 0x8001_0000;
    private const int CodeWords = 2048; // two pages
    private const uint DataBase = 0x8010_0000;
    private const uint IoBase = 0xB000_F000; // KSEG1: the bus, which stops the CPU when written

    // r3: the program's end (for JR), r4: data, r27: I/O, r28: code (for self-modifying stores), r29: the instruction
    // they store, r31: links. The others are free.
    // Every S-format function (the arithmetic and accumulator forms, compares, MAX/MIN, conversions) and a reserved one.
    private static readonly uint[] s_fpuFunctions =
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x16, 0x18, 0x19, 0x1A, 0x1C, 0x1D, 0x1E, 0x1F, 0x24, 0x28, 0x29, 0x30, 0x32, 0x34, 0x36, 0x3F];

    private static readonly int[] Free = [2, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_programs_run_the_same_recompiled_as_interpreted(int seed, bool fast)
    {
        var interpreted = new Machine(seed, recompiler: false, fast);
        var recompiled = new Machine(seed, recompiler: true, fast);
        var random = new Random(seed * 7919);
        for (var slice = 0; slice < 400; slice++)
        {
            // Sometimes an interrupt line changes between slices; the handler masks it (IM2) and the harness unmasks it.
            if (random.Next(8) == 0)
            {
                var line = random.Next(2) == 0;
                interpreted.Cpu.Int0 = recompiled.Cpu.Int0 = line;
                interpreted.Cpu.Status |= R5900Cpu.StatusIm2;
                recompiled.Cpu.Status |= R5900Cpu.StatusIm2;
            }

            // Often the next slice starts somewhere else in the program, so most of it gets run (and compiled).
            if (random.Next(2) == 0)
            {
                var entry = CodeBase + (uint)random.Next(CodeWords - 16) * 4;
                interpreted.Cpu.Jump(entry);
                recompiled.Cpu.Jump(entry);
            }

            var length = random.Next(4) == 0 ? random.Next(1, 40) : random.Next(100, 4000);
            interpreted.Cpu.Run(interpreted.Cpu.Cycles + length);
            recompiled.Cpu.Run(recompiled.Cpu.Cycles + length);

            var a = interpreted.State();
            var b = recompiled.State();
            if (!a.AsSpan().SequenceEqual(b))
            {
                var at = a.AsSpan().CommonPrefixLength(b);
                throw new Xunit.Sdk.XunitException(
                    $"seed {seed}, slice {slice}: the states differ at byte {at} (PC {interpreted.Cpu.Pc:X8} vs {recompiled.Cpu.Pc:X8}, "
                    + $"cycles {interpreted.Cpu.Cycles} vs {recompiled.Cpu.Cycles})");
            }

            interpreted.Bus.Rdram.AsSpan().SequenceEqual(recompiled.Bus.Rdram).Should().BeTrue($"seed {seed}, slice {slice}: RDRAM");
            interpreted.Bus.Writes.Should().Be(recompiled.Bus.Writes, $"seed {seed}, slice {slice}: I/O writes");
        }

        output.WriteLine($"seed {seed}: {recompiled.Cpu.CompiledBlocks} blocks, {recompiled.Cpu.RecompiledInstructions * 100.0 / recompiled.Cpu.Cycles:F1}% of the cycles in them");
    }

    /// <summary>48 programs with the exact float maths, and 16 with the fast maths (ps2-full-speed.md §2) on both sides.</summary>
    public static TheoryData<int, bool> Seeds()
    {
        var seeds = new TheoryData<int, bool>();
        for (var seed = 1; seed <= 48; seed++)
        {
            seeds.Add(seed, false);
        }

        for (var seed = 1; seed <= 16; seed++)
        {
            seeds.Add(seed, true);
        }

        return seeds;
    }

    [Fact]
    public void A_block_that_rewrites_itself_runs_the_new_code_as_the_interpreter_does()
    {
        foreach (var recompiler in new[] { false, true })
        {
            var machine = new R5900TestMachine();
            machine.Cpu.RecompilerEnabled = recompiler;

            // A loop whose body stores "addiu r3, r3, 2" over its own "addiu r3, r3, 1" after 100 rounds.
            machine.Load(
                CodeBase,
                Ee.Lui(28, 0x8001),
                Ee.Lui(6, 0x2463), // r6 = addiu r3, r3, 2
                Ee.Ori(6, 6, 0x0002),
                Ee.Addiu(10, 0, 200),
                Ee.Addiu(3, 3, 1), // $10: rewritten
                Ee.Addiu(10, 10, -1),
                Ee.Addiu(5, 10, -100),
                Ee.Bne(5, 0, 2),
                Ee.Nop,
                Ee.Sw(6, 28, 0x10),
                Ee.Bne(10, 0, -7),
                Ee.Nop,
                Ee.Beq(0, 0, -1),
                Ee.Nop);
            machine.Cpu.Jump(CodeBase);
            machine.Cpu.Run(100_000);

            // 100 rounds of +1; the 100th stores the new instruction, so the last 100 rounds add 2 each.
            machine.Cpu.GetGpr64(3).Should().Be(300, recompiler ? "recompiled" : "interpreted");
        }
    }

    [Fact]
    public void New_code_loaded_over_a_compiled_block_runs()
    {
        var machine = new R5900TestMachine();
        machine.Load(CodeBase, Ee.Addiu(3, 3, 1), Ee.Jr(31), Ee.Nop);
        machine.Load(CodeBase + 0x100, Ee.Lui(31, 0x8001), Ee.Ori(31, 31, 0x100 + 7 * 4), Ee.Nop, Ee.Nop, Ee.Nop, Ee.J(CodeBase), Ee.Nop, Ee.Beq(0, 0, -1), Ee.Nop);
        for (var i = 0; i < 50; i++)
        {
            machine.Cpu.Jump(CodeBase + 0x100);
            machine.Cpu.Run(machine.Cpu.Cycles + 30);
        }

        machine.Cpu.CompiledBlocks.Should().BePositive();
        machine.Cpu.GetGpr64(3).Should().Be(50);

        // Something else (DMA, the IOP, a loader) puts new code there: the next run uses it.
        machine.Load(CodeBase, Ee.Addiu(3, 3, 100));
        machine.Cpu.Jump(CodeBase + 0x100);
        machine.Cpu.Run(machine.Cpu.Cycles + 30);
        machine.Cpu.GetGpr64(3).Should().Be(150);
    }

    /// <summary>One CPU with its memory, a random program and handlers that step over whatever faulted.</summary>
    private sealed class Machine
    {
        public readonly FuzzBus Bus = new();
        public readonly R5900Cpu Cpu;
        private readonly StateWriter _writer = new();

        public Machine(int seed, bool recompiler, bool fast = false)
        {
            Cpu = new R5900Cpu(Bus, new FakeCop2());
            Bus.Cpu = Cpu;
            Cpu.RecompilerEnabled = recompiler;
            Cpu.FastFloat = fast;
            Cpu.CompileThreshold = 2; // nearly everything the program reaches gets compiled
            Cpu.Status = R5900Cpu.StatusCu0 | R5900Cpu.StatusCu1 | R5900Cpu.StatusCu2
                | R5900Cpu.StatusIe | R5900Cpu.StatusEie | R5900Cpu.StatusIm2;

            // The exception vectors: EPC += 4 (past what faulted), then ERET. The interrupt vector masks INT0 and
            // returns. k0 and k1 are r26 and r27… r27 is the I/O base, so the interrupt handler uses r26 and r30.
            uint[] handler = [Ee.Mfc0(26, 14), Ee.Addiu(26, 26, 4), Ee.Mtc0(26, 14), Ee.Nop, Ee.Eret, Ee.Nop];
            uint[] interrupt = [Ee.Mfc0(26, 12), Ee.Addiu(30, 0, ~(int)R5900Cpu.StatusIm2), Ee.R(0x24, 26, 26, 30), Ee.Mtc0(26, 12), Ee.Nop, Ee.Eret, Ee.Nop];
            Load(0x8000_0000, handler);
            Load(0x8000_0180, handler);
            Load(0x8000_0200, interrupt);

            var random = new Random(seed);
            var program = new uint[CodeWords];
            for (var i = 0; i < program.Length - 16; i++)
            {
                program[i] = RandomInstruction(random, i);
            }

            for (var i = program.Length - 16; i < program.Length; i += 2)
            {
                program[i] = Ee.J(CodeBase); // several, as a branch-likely or a handler can step over one
            }

            Load(CodeBase, program);
            for (var r = 1; r < 32; r++)
            {
                var value = random.Next(3) switch
                {
                    0 => (ulong)random.Next(-8, 8),
                    1 => (ulong)random.NextInt64(),
                    _ => (ulong)(long)random.Next(),
                };
                Cpu.SetGpr(r, new UInt128((ulong)random.NextInt64(), value));
            }

            for (var i = 0; i < 0x10000; i += 4)
            {
                Bus.WriteWord((DataBase & 0x1FFF_FFFF) + (uint)i, (uint)random.Next());
            }

            for (var f = 0; f < 32; f++)
            {
                Cpu.SetFpr(f, (uint)random.Next(0x3F00_0000, 0x4100_0000));
            }

            Cpu.SetGpr64(3, CodeBase + (CodeWords - 16) * 4);
            Cpu.SetGpr64(4, DataBase);
            Cpu.SetGpr64(27, IoBase);
            Cpu.SetGpr64(28, CodeBase);
            Cpu.SetGpr64(29, Ee.Addiu(2, 2, 1));
            Cpu.SetGpr64(31, CodeBase);
            Cpu.Jump(CodeBase);
        }

        public byte[] State()
        {
            _writer.Reset();
            Cpu.SaveState(_writer);
            return _writer.ToArray();
        }

        private void Load(uint address, uint[] words)
        {
            for (var i = 0; i < words.Length; i++)
            {
                Bus.WriteWord((address & 0x1FFF_FFFF) + (uint)(i * 4), words[i]);
            }
        }

        private static int Pick(Random random) => Free[random.Next(Free.Length)];

        private static uint RandomInstruction(Random random, int index)
        {
            var d = Pick(random);
            var s = random.Next(4) == 0 ? random.Next(32) : Pick(random);
            var t = random.Next(4) == 0 ? random.Next(32) : Pick(random);
            var sa = random.Next(32);
            var imm = random.Next(3) == 0 ? random.Next(-16, 16) : (short)random.Next();

            // Branches and jumps only go forward (no endless loops), so every round runs much of the program; the
            // program's end jumps back to its start.
            var branch = Math.Min(random.Next(0, 24), CodeWords - index - 18);
            var forward = CodeBase + (uint)Math.Min(index + 2 + random.Next(64), CodeWords - 16) * 4;
            var dataOffset = random.Next(8) == 0 ? random.Next(-3, 0x8000) : random.Next(0, 0x1000) & ~15 | random.Next(4) * 4;
            var codeOffset = random.Next(0, CodeWords * 4) & ~3;
            return random.Next(64) switch
            {
                0 => Ee.Addiu(d, s, imm),
                1 => Ee.Addu(d, s, t),
                2 => Ee.Addi(d, s, imm),
                3 => Ee.Add(d, s, t),
                4 => Ee.R(0x22, d, s, t), // SUB
                5 => Ee.R(0x23, d, s, t), // SUBU
                6 => Ee.R(0x2C + (uint)random.Next(4), d, s, t), // DADD, DADDU, DSUB, DSUBU
                7 => Ee.I(0x18 + (uint)random.Next(2), d, s, imm), // DADDI, DADDIU
                8 => Ee.R(0x24 + (uint)random.Next(4), d, s, t), // AND, OR, XOR, NOR
                9 => Ee.I(0x0C + (uint)random.Next(3), d, s, imm), // ANDI, ORI, XORI
                10 => Ee.Lui(d, imm),
                11 => Ee.R(0x2A + (uint)random.Next(2), d, s, t), // SLT, SLTU
                12 => Ee.I(0x0A + (uint)random.Next(2), d, s, imm), // SLTI, SLTIU
                13 => Ee.R(new uint[] { 0x00, 0x02, 0x03 }[random.Next(3)], d, 0, t, sa), // SLL, SRL, SRA
                14 => Ee.R(new uint[] { 0x04, 0x06, 0x07 }[random.Next(3)], d, s, t), // SLLV, SRLV, SRAV
                15 => Ee.R(new uint[] { 0x38, 0x3A, 0x3B, 0x3C, 0x3E, 0x3F }[random.Next(6)], d, 0, t, sa), // DSLL…
                16 => Ee.R(new uint[] { 0x14, 0x16, 0x17 }[random.Next(3)], d, s, t), // DSLLV, DSRLV, DSRAV
                17 => Ee.R(0x0A + (uint)random.Next(2), d, s, t), // MOVZ, MOVN
                18 => Ee.R(0x18 + (uint)random.Next(2), random.Next(2) == 0 ? 0 : d, s, t), // MULT, MULTU
                19 => Ee.R(0x1A + (uint)random.Next(2), 0, s, t), // DIV, DIVU
                20 => Ee.R(0x10 + (uint)random.Next(4), d, s), // MFHI, MTHI, MFLO, MTLO
                21 => Ee.R(0x28 + (uint)random.Next(2), d, s), // MFSA, MTSA
                22 or 23 or 24 => Ee.I(new uint[] { 0x20, 0x21, 0x23, 0x24, 0x25, 0x27, 0x37, 0x1E }[random.Next(8)], d, 4, dataOffset),
                25 => Ee.I(new uint[] { 0x22, 0x26, 0x1A, 0x1B }[random.Next(4)], d, 4, dataOffset), // LWL, LWR, LDL, LDR
                26 or 27 => Ee.I(new uint[] { 0x28, 0x29, 0x2B, 0x3F, 0x1F }[random.Next(5)], t, 4, dataOffset), // SB, SH, SW, SD, SQ
                28 => Ee.I(new uint[] { 0x2A, 0x2E, 0x2C, 0x2D }[random.Next(4)], t, 4, dataOffset), // SWL, SWR, SDL, SDR
                29 => Ee.I(0x31 + (uint)random.Next(2) * 8, random.Next(32), 4, dataOffset), // LWC1, SWC1
                30 => random.Next(16) == 0
                    ? 0x4680_0020u | (uint)(random.Next(32) << 11 | random.Next(32) << 6) // CVT.S.W
                    : Ee.FpuS(s_fpuFunctions[random.Next(s_fpuFunctions.Length)], random.Next(32), random.Next(32), random.Next(32)),
                31 => random.Next(2) == 0 ? Ee.Mtc1(s, random.Next(32)) : Ee.Mfc1(d, random.Next(32)),
                32 => new[] { Ee.Por(d, s, t), Ee.Pcpyld(d, s, t), Ee.Mmi(0x08, d, s, t, 0x00), Ee.Qfsrv(d, s, t) }[random.Next(4)],
                33 or 34 or 35 => Ee.I(new uint[] { 0x04, 0x05, 0x06, 0x07 }[random.Next(4)], t, s, branch), // BEQ, BNE, BLEZ, BGTZ
                36 => Ee.I(new uint[] { 0x14, 0x15, 0x16, 0x17 }[random.Next(4)], t, s, branch), // the likelies
                37 => Ee.I(0x01, new[] { 0x00, 0x01, 0x02, 0x03, 0x10, 0x11, 0x12, 0x13 }[random.Next(8)], s, branch), // REGIMM
                38 => 0x0C00_0000 | ((forward >> 2) & 0x03FF_FFFF), // JAL
                39 => Ee.J(forward),
                40 => random.Next(4) == 0 ? Ee.Jr(3) : Ee.Nop,
                41 => random.Next(4) == 0 ? Ee.R(0x09, 31, 3) : Ee.Nop, // JALR r31, r3
                42 => Ee.Sw(29, 28, codeOffset), // rewrites the program
                43 => random.Next(3) switch
                {
                    0 => Ee.Syscall,
                    1 => Ee.Break,
                    _ => Ee.Teq(s, t),
                },
                44 => Ee.Sw(t, 27, 0), // I/O: stops the CPU
                45 => Ee.Lw(d, 27, 0), // I/O read
                46 => Ee.R(0x09, d, 3), // JALR rd, r3
                47 => Ee.Bc1(random.Next(4), branch),
                48 => Ee.Mfc0(d, new[] { 9, 12, 13, 14 }[random.Next(4)]), // Count, Status, Cause, EPC
                49 => Ee.I(0x31, random.Next(32), 4, dataOffset), // LWC1
                _ => Ee.Addiu(d, d, imm),
            };
        }
    }

    /// <summary>4 MB of RDRAM; the rest reads its address back, and a write stops the CPU (as a device can).</summary>
    private sealed class FuzzBus : IR5900Bus
    {
        public byte[] Rdram { get; } = new byte[4 * 1024 * 1024];

        public R5900Cpu? Cpu;
        public int Writes;

        public byte Read8(uint address) => (byte)address;

        public ushort Read16(uint address) => (ushort)address;

        public uint Read32(uint address) => address;

        public ulong Read64(uint address) => address;

        public UInt128 Read128(uint address) => address;

        public void Write8(uint address, byte value) => Write();

        public void Write16(uint address, ushort value) => Write();

        public void Write32(uint address, uint value) => Write();

        public void Write64(uint address, ulong value) => Write();

        public void Write128(uint address, UInt128 value) => Write();

        public void WriteWord(uint physical, uint value) =>
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(Rdram.AsSpan((int)physical), value);

        private void Write()
        {
            Writes++;
            Cpu!.StopRequested = true;
        }
    }
}
