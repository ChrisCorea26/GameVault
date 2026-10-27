using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The VU block recompiler (outstanding-work plan §3). A straight run of micro-instruction pairs is translated into IL
/// once it has run a few times, and .NET's JIT turns that into machine code. Each pair in a block makes the same calls
/// the interpreter's <see cref="Step"/> makes, in the same order, but with its decoded fields as constants: the
/// interpreter's per-pair decode check, the operand-mask loops and the opcode switches fold away. A block therefore
/// does exactly what the interpreter would, cycle for cycle (stalls, the flag queue, Q and P, the integer branch
/// hazard, the E/D/T bits), so save states, replays and netplay don't depend on it.
/// </summary>
/// <remarks>
/// <para>
/// What a block is: pairs from its start up to and including a branch's or jump's delay pair, an E bit's delay pair,
/// a pair with a D or T bit, or the end of micro memory, at most <see cref="MaxBlockPairs"/>. A block only starts
/// between pairs the interpreter would also run from the top: no branch or end pending.
/// </para>
/// <para>
/// Slices: the interpreter checks the slice's end before every pair, and so does a block, leaving with the PC at the
/// next pair. Code changes: a block keeps the doublewords it was built from and compares them with micro memory each
/// time it starts, so microcode uploaded by the VIF or written by the EE never runs stale. Nothing a block calls can
/// write micro memory. Blocks are a cache, not state.
/// </para>
/// </remarks>
public sealed partial class Ps2Vu
{
    /// <summary>The most pairs in a block.</summary>
    internal const int MaxBlockPairs = 64;

    /// <summary>Runs of a block before it is compiled (cold code isn't worth the JIT's time). Tests lower it.</summary>
    internal int CompileThreshold { get; set; } = 8;

    private VuBlock?[]? _blocks;
    private bool _recompilerEnabled = RuntimeFeature.IsDynamicCodeCompiled;

    /// <summary>
    /// Whether micro programs are recompiled (on by default where the runtime can compile code). The results are the
    /// same either way; only the speed differs. Off forgets the compiled blocks.
    /// </summary>
    public bool RecompilerEnabled
    {
        get => _recompilerEnabled;
        set
        {
            _recompilerEnabled = value && RuntimeFeature.IsDynamicCodeCompiled;
            if (!_recompilerEnabled)
            {
                _blocks = null;
            }
        }
    }

    /// <summary>Blocks compiled so far (diagnostics and tests).</summary>
    public int CompiledBlocks { get; private set; }

    /// <summary>Cycles (stalls included) run in compiled blocks so far (diagnostics and tests; not part of the state).</summary>
    public long RecompiledCycles { get; private set; }

    private sealed class VuBlock(ulong[] pairs)
    {
        public readonly ulong[] Pairs = pairs;
        public Action<Ps2Vu, long>? Code;
        public int Runs;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(byte[] micro, uint pc) =>
            micro.AsSpan((int)pc, Pairs.Length * 8).SequenceEqual(MemoryMarshal.AsBytes(Pairs.AsSpan()));
    }

    /// <summary>
    /// Runs the compiled block at the PC if there is one; false when the interpreter should run the next pair instead.
    /// The caller has checked that the unit runs, the slice isn't over, and no branch or end is pending.
    /// </summary>
    private bool TryRunBlock(long end)
    {
        var pc = _pc;
        var blocks = _blocks ??= new VuBlock?[_micro.Length / 8];
        var block = blocks[pc >> 3];
        if (block is null || !block.Matches(_micro, pc))
        {
            block = blocks[pc >> 3] = new VuBlock(ReadBlock(pc));
        }

        if (block.Code is not { } code)
        {
            if (++block.Runs < CompileThreshold)
            {
                return false;
            }

            code = block.Code = VuRecompiler.Compile(this, pc, block.Pairs);
            CompiledBlocks++;
        }

        var start = _cycle;
        code(this, end);
        RecompiledCycles += _cycle - start;
        return true;
    }

    /// <summary>The doublewords of the block starting at <paramref name="pc"/>.</summary>
    private ulong[] ReadBlock(uint pc)
    {
        var pairs = new List<ulong>(16);
        var scratch = new DecodedPair();
        var endAfterNext = false;
        for (var at = pc; at < _micro.Length && pairs.Count < MaxBlockPairs; at += 8)
        {
            var raw = BinaryPrimitives.ReadUInt64LittleEndian(_micro.AsSpan((int)at, 8));
            pairs.Add(raw);
            if (endAfterNext)
            {
                break;
            }

            Decode(ref scratch, raw);
            if ((scratch.UpperBits & (DBit | TBit)) != 0)
            {
                break;
            }

            endAfterNext = (scratch.UpperBits & EBit) != 0 || (!scratch.Immediate && IsBranch(scratch.Lower));
        }

        return [.. pairs];
    }

    private static bool IsBranch(LowerOp op) => op is >= LowerOp.B and <= LowerOp.Ibgez;

    /// <summary>Translates a block into a <see cref="DynamicMethod"/> on <see cref="Ps2Vu"/>.</summary>
    private static class VuRecompiler
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly FieldInfo CycleField = Field(nameof(_cycle));
        private static readonly FieldInfo PcField = Field(nameof(_pc));
        private static readonly FieldInfo EndPendingField = Field(nameof(_endPending));
        private static readonly FieldInfo IField = Field(nameof(_i));
        private static readonly FieldInfo ViReadyField = Field(nameof(_viReady));

        private static readonly MethodInfo VfReadyMethod = Method(nameof(JitVfReady));
        private static readonly MethodInfo WaitFdivMethod = Method(nameof(WaitFdiv));
        private static readonly MethodInfo WaitEfuMethod = Method(nameof(WaitEfu));
        private static readonly MethodInfo BeginPairMethod = Method(nameof(BeginPair));
        private static readonly MethodInfo TakeBranchMethod = Method(nameof(TakeBranch));
        private static readonly MethodInfo EndPairMethod = Method(nameof(EndPair));
        private static readonly MethodInfo DebugOrTrapMethod = Method(nameof(DebugOrTrapStop));
        private static readonly MethodInfo StopMethod = Method(nameof(Stop));
        private static readonly MethodInfo ArithmeticMethod = Method(nameof(Arithmetic));
        private static readonly MethodInfo EfuMethod = Method(nameof(ExecuteEfu));
        private static readonly MethodInfo MaxMethod = typeof(Math).GetMethod(nameof(Math.Max), [typeof(long), typeof(long)])!;

        private static readonly Dictionary<UpperKind, MethodInfo> UpperMethods = new()
        {
            [UpperKind.OpMula] = Method(nameof(UpperOpMula)),
            [UpperKind.OpMsub] = Method(nameof(UpperOpMsub)),
            [UpperKind.Abs] = Method(nameof(UpperAbs)),
            [UpperKind.Ftoi] = Method(nameof(UpperFtoi)),
            [UpperKind.Itof] = Method(nameof(UpperItof)),
            [UpperKind.Clip] = Method(nameof(UpperClip)),
        };

        /// <summary><c>Lower</c> + each <see cref="LowerOp"/> name below the EFU's, which <see cref="ExecuteEfu"/> runs.</summary>
        private static readonly Dictionary<LowerOp, MethodInfo> LowerMethods = Enum.GetValues<LowerOp>()
            .Where(op => op is < LowerOp.Esadd or > LowerOp.Eexp)
            .ToDictionary(op => op, op => Method("Lower" + op));

        private static FieldInfo Field(string name) =>
            typeof(Ps2Vu).GetField(name, Instance) ?? throw new MissingFieldException(nameof(Ps2Vu), name);

        private static MethodInfo Method(string name) =>
            typeof(Ps2Vu).GetMethod(name, Instance) ?? throw new MissingMethodException(nameof(Ps2Vu), name);

        public static Action<Ps2Vu, long> Compile(Ps2Vu vu, uint start, ulong[] pairs)
        {
            var method = new DynamicMethod($"VU{vu.Index}_{start:X4}", null, [typeof(Ps2Vu), typeof(long)], typeof(Ps2Vu), skipVisibility: true);
            var il = method.GetILGenerator();
            var ready = il.DeclareLocal(typeof(long));
            var nextPc = il.DeclareLocal(typeof(uint));

            var afterBranch = false;
            var endPending = false; // a block never starts with an end pending
            var d = new DecodedPair();
            for (var i = 0; i < pairs.Length; i++)
            {
                var pc = start + (uint)(i * 8);
                vu.Decode(ref d, pairs[i]);

                // The interpreter's loop: no pair starts once the slice is over.
                if (i > 0)
                {
                    var go = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, CycleField);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Blt, go);
                    il.Emit(OpCodes.Ret);
                    il.MarkLabel(go);
                }

                EmitStall(il, ref d, ready);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, ready);
                il.Emit(OpCodes.Call, BeginPairMethod);

                if (afterBranch)
                {
                    // This pair is a delay slot: the next one is the branch's target if it was taken.
                    var notTaken = il.DefineLabel();
                    var done = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, Field(nameof(_branchPending)));
                    il.Emit(OpCodes.Brfalse, notTaken);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, (int)(pc + 8));
                    il.Emit(OpCodes.Call, TakeBranchMethod);
                    il.Emit(OpCodes.Stloc, nextPc);
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(notTaken);
                    il.Emit(OpCodes.Ldc_I4, (int)(pc + 8));
                    il.Emit(OpCodes.Stloc, nextPc);
                    il.MarkLabel(done);
                }
                else
                {
                    il.Emit(OpCodes.Ldc_I4, (int)(pc + 8));
                    il.Emit(OpCodes.Stloc, nextPc);
                }

                EmitUpper(il, ref d);
                if (d.Immediate)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, (int)(uint)pairs[i]);
                    il.Emit(OpCodes.Stfld, IField);
                }
                else
                {
                    EmitLower(il, ref d, pc);
                }

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, EndPairMethod);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, nextPc);
                il.Emit(OpCodes.Ldc_I4, (int)vu._microMask);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Stfld, PcField);

                var bits = d.UpperBits;
                var end = endPending;
                endPending = !end && (bits & EBit) != 0;
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(endPending ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stfld, EndPendingField);
                if ((bits & (DBit | TBit)) != 0)
                {
                    var running = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, (int)bits);
                    il.Emit(OpCodes.Call, DebugOrTrapMethod);
                    il.Emit(OpCodes.Brfalse, running);
                    il.Emit(OpCodes.Ret);
                    il.MarkLabel(running);
                }

                if (end)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Call, StopMethod);
                    il.Emit(OpCodes.Ret);
                    break;
                }

                afterBranch = !d.Immediate && IsBranch(d.Lower);
            }

            il.Emit(OpCodes.Ret);
            return method.CreateDelegate<Action<Ps2Vu, long>>();
        }

        /// <summary>The operands the pair waits for, each a constant register and lane mask.</summary>
        private static void EmitStall(ILGenerator il, ref DecodedPair d, LocalBuilder ready)
        {
            il.Emit(OpCodes.Ldc_I8, 0L);
            il.Emit(OpCodes.Stloc, ready);
            EmitVfRead(il, ready, d.UReadA, d.UMaskA);
            EmitVfRead(il, ready, d.UReadB, d.UMaskB);
            EmitVfRead(il, ready, d.LReadA, d.LMaskA);
            EmitVfRead(il, ready, d.LReadB, d.LMaskB);
            EmitViRead(il, ready, d.ViA);
            EmitViRead(il, ready, d.ViB);
            if (d.Waits != 0)
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, ready);
                il.Emit(OpCodes.Call, d.Waits == WaitsForFdiv ? WaitFdivMethod : WaitEfuMethod);
                il.Emit(OpCodes.Stloc, ready);
            }
        }

        private static void EmitVfRead(ILGenerator il, LocalBuilder ready, byte reg, byte mask)
        {
            if (mask == 0)
            {
                return;
            }

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, ready);
            il.Emit(OpCodes.Ldc_I4, (int)reg);
            il.Emit(OpCodes.Ldc_I4, (int)mask);
            il.Emit(OpCodes.Call, VfReadyMethod);
            il.Emit(OpCodes.Stloc, ready);
        }

        private static void EmitViRead(ILGenerator il, LocalBuilder ready, byte reg)
        {
            if (reg == 0)
            {
                return;
            }

            // ready = Math.Max(ready, _viReady[reg])
            il.Emit(OpCodes.Ldloc, ready);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, ViReadyField);
            il.Emit(OpCodes.Ldc_I4, (int)reg);
            il.Emit(OpCodes.Ldelem_I8);
            il.Emit(OpCodes.Call, MaxMethod);
            il.Emit(OpCodes.Stloc, ready);
        }

        private static void EmitUpper(ILGenerator il, ref DecodedPair d)
        {
            if (d.Upper == UpperKind.Nop)
            {
                return;
            }

            il.Emit(OpCodes.Ldarg_0);
            if (UpperMethods.TryGetValue(d.Upper, out var method))
            {
                EmitInts(il, d.Fs, d.Ft, d.Fd, d.UDest, d.Bc);
                il.Emit(OpCodes.Call, method);
                return;
            }

            // Arithmetic(kind, operand, toAcc, fs, ft, fd, dest, bc)
            EmitInts(il, (int)d.Upper, d.Operand, d.ToAcc ? 1 : 0, d.Fs, d.Ft, d.Fd, d.UDest, d.Bc);
            il.Emit(OpCodes.Call, ArithmeticMethod);
        }

        private static void EmitLower(ILGenerator il, ref DecodedPair d, uint pc)
        {
            if (d.Lower is LowerOp.Nop or LowerOp.Waitq or LowerOp.Waitp)
            {
                return; // nothing to do (WAITQ and WAITP are the stall)
            }

            il.Emit(OpCodes.Ldarg_0);
            if (LowerMethods.TryGetValue(d.Lower, out var method))
            {
                EmitInts(il, d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, (int)pc);
                il.Emit(OpCodes.Call, method);
                return;
            }

            // ExecuteEfu(op, fs, fsf)
            EmitInts(il, (int)d.Lower, d.Is, d.Fsf);
            il.Emit(OpCodes.Call, EfuMethod);
        }

        private static void EmitInts(ILGenerator il, params int[] values)
        {
            foreach (var value in values)
            {
                il.Emit(OpCodes.Ldc_I4, value);
            }
        }
    }

    /// <summary><c>Math.Max(ready, VfReady(reg, mask))</c>, the interpreter's operand wait, for recompiled blocks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long JitVfReady(long ready, int reg, int mask) => Math.Max(ready, VfReady(reg, mask));
}
