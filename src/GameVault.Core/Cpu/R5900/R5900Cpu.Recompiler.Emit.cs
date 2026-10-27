using System.Reflection;
using System.Reflection.Emit;

namespace GameVault.Core.Cpu.R5900;

public sealed partial class R5900Cpu
{
    /// <summary>
    /// Translates a block into a <see cref="DynamicMethod"/> on <see cref="R5900Cpu"/> (see R5900Cpu.Recompiler.cs for
    /// the rules a block keeps). The GPRs, HI/LO and the PC stay in the CPU's fields; the block only keeps the cycles
    /// it hasn't added to <see cref="Cycles"/> yet, and adds them before anything that reads the time.
    /// </summary>
    private static class R5900Recompiler
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo GprField = Field(nameof(_gpr));
        private static readonly FieldInfo CyclesField = Field(nameof(Cycles));
        private static readonly FieldInfo SliceEndField = Field(nameof(_sliceEnd));
        private static readonly FieldInfo PcField = Field(nameof(_pc));
        private static readonly FieldInfo NextPcField = Field(nameof(_nextPc));
        private static readonly FieldInfo NextIsDelaySlotField = Field(nameof(_nextIsDelaySlot));
        private static readonly FieldInfo CurrentPcField = Field(nameof(_currentPc));
        private static readonly FieldInfo InDelaySlotField = Field(nameof(_inDelaySlot));
        private static readonly FieldInfo HiField = Field(nameof(_hi));
        private static readonly FieldInfo LoField = Field(nameof(_lo));
        private static readonly FieldInfo SaField = Field(nameof(_sa));

        private static readonly FieldInfo StopRequestedField = Field(nameof(StopRequested));
        private static readonly FieldInfo InterruptPendingField = Field(nameof(_interruptPending));

        private static readonly MethodInfo Cop1Method = Method(nameof(ExecuteCop1));
        private static readonly MethodInfo Cop2Method = Method(nameof(ExecuteCop2));
        private static readonly MethodInfo MmiMethod = Method(nameof(ExecuteMmi));
        private static readonly MethodInfo StepMethod = Method(nameof(JitStep));
        private static readonly MethodInfo BranchMethod = Method(nameof(JitBranch));
        private static readonly MethodInfo StepDelayMethod = Method(nameof(JitStepDelay));
        private static readonly MethodInfo IdleMethod = Method(nameof(JitIdle));
        private static readonly MethodInfo Load8Method = Method(nameof(JitLoad8));
        private static readonly MethodInfo Load16Method = Method(nameof(JitLoad16));
        private static readonly MethodInfo Load32Method = Method(nameof(JitLoad32));
        private static readonly MethodInfo Load64Method = Method(nameof(JitLoad64));
        private static readonly MethodInfo LqMethod = Method(nameof(JitLq));
        private static readonly MethodInfo Store8Method = Method(nameof(JitStore8));
        private static readonly MethodInfo Store16Method = Method(nameof(JitStore16));
        private static readonly MethodInfo Store32Method = Method(nameof(JitStore32));
        private static readonly MethodInfo Store64Method = Method(nameof(JitStore64));
        private static readonly MethodInfo SqMethod = Method(nameof(JitSq));
        private static readonly MethodInfo Lwc1Method = Method(nameof(JitLwc1));
        private static readonly MethodInfo Swc1Method = Method(nameof(JitSwc1));
        private static readonly MethodInfo MultMethod = Method(nameof(JitMult));
        private static readonly MethodInfo MultuMethod = Method(nameof(JitMultu));
        private static readonly MethodInfo MfHiLoMethod = Method(nameof(JitMfHiLo));

        private static FieldInfo Field(string name) =>
            typeof(R5900Cpu).GetField(name, Instance) ?? throw new MissingFieldException(nameof(R5900Cpu), name);

        private static MethodInfo Method(string name) =>
            typeof(R5900Cpu).GetMethod(name, Instance) ?? throw new MissingMethodException(nameof(R5900Cpu), name);

        // ---- What a block is ----

        /// <summary>Branches and jumps (all have a delay slot).</summary>
        public static bool IsBranch(uint op)
        {
            var primary = op >> 26;
            return primary switch
            {
                0x00 => (op & 0x3F) is 0x08 or 0x09, // JR, JALR
                0x01 => ((op >> 16) & 31) is <= 0x03 or (>= 0x10 and <= 0x13),
                >= 0x02 and <= 0x07 => true,
                >= 0x10 and <= 0x12 => ((op >> 21) & 31) == 0x08, // BC0x, BC1x, BC2x
                >= 0x14 and <= 0x17 => true,
                _ => false,
            };
        }

        /// <summary>Instructions a block ends after: SYSCALL, BREAK and COP0 (ERET, EI/DI, MTC0, the TLB…).</summary>
        private static bool EndsBlock(uint op) =>
            op >> 26 == 0x10 || (op >> 26 == 0 && (op & 0x3F) is 0x0C or 0x0D);

        /// <summary>
        /// How many of <paramref name="words"/> (the rest of the page) make the block: up to and including a branch's
        /// delay slot, or an instruction that ends blocks, or the page's end. 0 when the first one is a branch whose
        /// delay slot is in the next page or is itself a branch (the interpreter runs those).
        /// </summary>
        public static int BlockLength(ReadOnlySpan<uint> words)
        {
            var limit = Math.Min(words.Length, MaxBlockInstructions - 1);
            for (var i = 0; i < limit; i++)
            {
                var op = words[i];
                if (IsBranch(op))
                {
                    return i + 1 < words.Length && !IsBranch(words[i + 1]) ? i + 2 : i;
                }

                if (EndsBlock(op))
                {
                    return i + 1;
                }
            }

            return limit;
        }

        public static Action<R5900Cpu> Compile(uint pc, uint[] words)
        {
            var method = new DynamicMethod($"EE_{pc:X8}", null, [typeof(R5900Cpu)], typeof(R5900Cpu), skipVisibility: true);
            new Emitter(method.GetILGenerator(), pc, words).Emit();
            return method.CreateDelegate<Action<R5900Cpu>>();
        }

        private sealed class Emitter
        {
            private readonly ILGenerator _il;
            private readonly uint _start;
            private readonly uint[] _words;
            private readonly LocalBuilder _gpr;
            private readonly LocalBuilder _address;
            private readonly LocalBuilder _next;
            private readonly LocalBuilder _a32;
            private readonly LocalBuilder _b32;
            private readonly LocalBuilder _r32;
            private readonly LocalBuilder _a64;
            private readonly LocalBuilder _b64;
            private readonly LocalBuilder _r64;
            private readonly LocalBuilder _u8;
            private readonly LocalBuilder _u16;
            private readonly LocalBuilder _u32;
            private readonly LocalBuilder _u64;

            /// <summary>Cycles run but not yet added to <see cref="Cycles"/>.</summary>
            private int _pending;

            // The instruction being emitted.
            private uint _op;
            private uint _pc;
            private int _remaining;
            private bool _delay;

            public Emitter(ILGenerator il, uint start, uint[] words)
            {
                _il = il;
                _start = start;
                _words = words;
                _gpr = il.DeclareLocal(typeof(ulong).MakeByRefType());
                _address = il.DeclareLocal(typeof(uint));
                _next = il.DeclareLocal(typeof(uint));
                _a32 = il.DeclareLocal(typeof(int));
                _b32 = il.DeclareLocal(typeof(int));
                _r32 = il.DeclareLocal(typeof(int));
                _a64 = il.DeclareLocal(typeof(long));
                _b64 = il.DeclareLocal(typeof(long));
                _r64 = il.DeclareLocal(typeof(long));
                _u8 = il.DeclareLocal(typeof(byte));
                _u16 = il.DeclareLocal(typeof(ushort));
                _u32 = il.DeclareLocal(typeof(uint));
                _u64 = il.DeclareLocal(typeof(ulong));
            }

            public void Emit()
            {
                // ref ulong gpr = ref _gpr[0];
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, GprField);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ldelema, typeof(ulong));
                _il.Emit(OpCodes.Stloc, _gpr);

                for (var i = 0; i < _words.Length; i++)
                {
                    _op = _words[i];
                    _pc = _start + (uint)i * 4;
                    _remaining = _words.Length - i - 1;
                    _delay = false;
                    _pending++;
                    if (IsBranch(_op))
                    {
                        EmitBranch();
                        _op = _words[i + 1];
                        _pc += 4;
                        _remaining = 0;
                        _delay = true;
                        _pending++;
                        EmitInstruction();
                        EmitExitToNext();
                        return;
                    }

                    EmitInstruction();
                }

                EmitExit(_pc + 4);
            }

            // ---- Plumbing ----

            private void Ldc(int value) => _il.Emit(OpCodes.Ldc_I4, value);

            private void Ldc(uint value) => _il.Emit(OpCodes.Ldc_I4, unchecked((int)value));

            private void LdcLong(long value) => _il.Emit(OpCodes.Ldc_I8, value);

            private void StoreField(FieldInfo field, Action value)
            {
                _il.Emit(OpCodes.Ldarg_0);
                value();
                _il.Emit(OpCodes.Stfld, field);
            }

            private void LoadField(FieldInfo field)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, field);
            }

            /// <summary>Adds <paramref name="cycles"/> to <see cref="Cycles"/> (and forgets the pending ones).</summary>
            private void Flush(int cycles)
            {
                if (cycles != 0)
                {
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Ldfld, CyclesField);
                    LdcLong(cycles);
                    _il.Emit(OpCodes.Add);
                    _il.Emit(OpCodes.Stfld, CyclesField);
                }

                _pending = 0;
            }

            /// <summary>The cycles up to and including the current instruction.</summary>
            private void FlushAll() => Flush(_pending);

            /// <summary>Pushes the address of GPR <paramref name="register"/>'s low (or high) 64 bits.</summary>
            private void GprAddress(int register, bool high = false)
            {
                _il.Emit(OpCodes.Ldloc, _gpr);
                Ldc((register * 2 + (high ? 1 : 0)) * 8);
                _il.Emit(OpCodes.Conv_I);
                _il.Emit(OpCodes.Add);
            }

            /// <summary>Pushes a GPR's low 64 bits.</summary>
            private void Gpr64(int register)
            {
                if (register == 0)
                {
                    LdcLong(0);
                    return;
                }

                GprAddress(register);
                _il.Emit(OpCodes.Ldind_I8);
            }

            /// <summary>Pushes a GPR's low 32 bits.</summary>
            private void Gpr32(int register)
            {
                if (register == 0)
                {
                    Ldc(0);
                    return;
                }

                GprAddress(register);
                _il.Emit(OpCodes.Ldind_I4);
            }

            /// <summary>Sets a GPR's low 64 bits to the value <paramref name="value"/> pushes (nothing for r0).</summary>
            private void SetGpr(int register, Action value)
            {
                if (register == 0)
                {
                    return;
                }

                GprAddress(register);
                value();
                _il.Emit(OpCodes.Stind_I8);
            }

            // ---- Leaving the block ----

            /// <summary>Leaves before <paramref name="nextPc"/>, after the current instruction (outside a delay slot).</summary>
            private void EmitExit(uint nextPc)
            {
                var pending = _pending;
                FlushAll();
                _pending = pending; // a dead end: the code after it still has these cycles to add
                StoreField(PcField, () => Ldc(nextPc));
                StoreField(NextPcField, () => Ldc(nextPc + 4));
                StoreField(NextIsDelaySlotField, () => Ldc(0));
                StoreField(CurrentPcField, () => Ldc(nextPc - 4));
                StoreField(InDelaySlotField, () => Ldc(0));
                _il.Emit(OpCodes.Ret);
            }

            /// <summary>The block's end after a delay slot: on to where the branch went.</summary>
            private void EmitExitToNext()
            {
                FlushAll();
                StoreField(PcField, () => _il.Emit(OpCodes.Ldloc, _next));
                StoreField(NextPcField, () =>
                {
                    _il.Emit(OpCodes.Ldloc, _next);
                    Ldc(4);
                    _il.Emit(OpCodes.Add);
                });
                StoreField(NextIsDelaySlotField, () => Ldc(0));
                StoreField(CurrentPcField, () => Ldc(_pc));
                StoreField(InDelaySlotField, () => Ldc(1));
                _il.Emit(OpCodes.Ret);
            }

            /// <summary>
            /// After something that may have stalled (moved <see cref="Cycles"/> on): leaves if the rest of the block no
            /// longer fits in the slice. Pending cycles must have been flushed.
            /// </summary>
            private void EmitFitCheck()
            {
                if (_delay)
                {
                    return; // the block ends anyway
                }

                var fits = _il.DefineLabel();
                LoadField(CyclesField);
                LdcLong(_remaining);
                _il.Emit(OpCodes.Add);
                LoadField(SliceEndField);
                _il.Emit(OpCodes.Ble, fits);
                EmitExit(_pc + 4);
                _il.MarkLabel(fits);
            }

            /// <summary>
            /// Hands the current instruction to the interpreter. <paramref name="leave"/>: the block ends after it
            /// anyway (a store's slow path: the store may have changed code). <paramref name="currentFlushed"/>: the
            /// instruction's own cycle is already in <see cref="Cycles"/>.
            /// </summary>
            private void EmitInterpret(bool leave = false, bool currentFlushed = false)
            {
                var pending = _pending;
                if (currentFlushed)
                {
                    Flush(-1);
                }
                else
                {
                    Flush(_pending - 1);
                }

                _il.Emit(OpCodes.Ldarg_0);
                Ldc(_op);
                Ldc(_pc);
                if (_delay)
                {
                    _il.Emit(OpCodes.Ldloc, _next);
                    _il.Emit(OpCodes.Call, StepDelayMethod);
                    _il.Emit(OpCodes.Ret);
                    _pending = pending; // a dead end
                    return;
                }

                Ldc(_remaining);
                _il.Emit(OpCodes.Call, StepMethod);
                if (leave)
                {
                    _il.Emit(OpCodes.Pop);
                    _il.Emit(OpCodes.Ret);
                    _pending = pending; // a dead end
                    return;
                }

                var next = _il.DefineLabel();
                _il.Emit(OpCodes.Brtrue, next);
                _il.Emit(OpCodes.Ret);
                _il.MarkLabel(next);
            }

            // ---- Instructions ----

            private int Rs => (int)(_op >> 21) & 31;

            private int Rt => (int)(_op >> 16) & 31;

            private int Rd => (int)(_op >> 11) & 31;

            private int Sa => (int)(_op >> 6) & 31;

            private short Immediate => (short)_op;

            private void EmitInstruction()
            {
                switch (_op >> 26)
                {
                    case 0x00:
                        EmitSpecial();
                        break;
                    case 0x08: // ADDI
                        EmitAdd32(() => Gpr32(Rs), () => Ldc(Immediate), Rt);
                        break;
                    case 0x09: // ADDIU
                        SetGpr(Rt, () =>
                        {
                            Gpr32(Rs);
                            Ldc(Immediate);
                            _il.Emit(OpCodes.Add);
                            _il.Emit(OpCodes.Conv_I8);
                        });
                        break;
                    case 0x0A: // SLTI
                        SetGpr(Rt, () =>
                        {
                            Gpr64(Rs);
                            LdcLong(Immediate);
                            _il.Emit(OpCodes.Clt);
                            _il.Emit(OpCodes.Conv_U8);
                        });
                        break;
                    case 0x0B: // SLTIU
                        SetGpr(Rt, () =>
                        {
                            Gpr64(Rs);
                            LdcLong(Immediate);
                            _il.Emit(OpCodes.Clt_Un);
                            _il.Emit(OpCodes.Conv_U8);
                        });
                        break;
                    case 0x0C: // ANDI
                        EmitLogicImmediate(OpCodes.And);
                        break;
                    case 0x0D: // ORI
                        EmitLogicImmediate(OpCodes.Or);
                        break;
                    case 0x0E: // XORI
                        EmitLogicImmediate(OpCodes.Xor);
                        break;
                    case 0x0F: // LUI
                        SetGpr(Rt, () => LdcLong((int)(_op << 16)));
                        break;
                    case 0x18: // DADDI
                        EmitAdd64(() => Gpr64(Rs), () => LdcLong(Immediate), Rt, subtract: false);
                        break;
                    case 0x19: // DADDIU
                        SetGpr(Rt, () =>
                        {
                            Gpr64(Rs);
                            LdcLong(Immediate);
                            _il.Emit(OpCodes.Add);
                        });
                        break;
                    case 0x1E: // LQ
                        EmitAddress();
                        _il.Emit(OpCodes.Ldarg_0);
                        _il.Emit(OpCodes.Ldloc, _address);
                        Ldc(Rt);
                        _il.Emit(OpCodes.Call, LqMethod);
                        EmitSlowPathIfFalse();
                        break;
                    case 0x1F: // SQ
                        EmitAddress();
                        _il.Emit(OpCodes.Ldarg_0);
                        _il.Emit(OpCodes.Ldloc, _address);
                        Ldc(Rt);
                        _il.Emit(OpCodes.Call, SqMethod);
                        EmitSlowPathIfFalse();
                        break;
                    case 0x20: // LB
                        EmitLoad(Load8Method, _u8, OpCodes.Conv_I1, OpCodes.Conv_I8);
                        break;
                    case 0x21: // LH
                        EmitLoad(Load16Method, _u16, OpCodes.Conv_I2, OpCodes.Conv_I8);
                        break;
                    case 0x23: // LW
                        EmitLoad(Load32Method, _u32, OpCodes.Conv_I8, null);
                        break;
                    case 0x24: // LBU
                        EmitLoad(Load8Method, _u8, OpCodes.Conv_U8, null);
                        break;
                    case 0x25: // LHU
                        EmitLoad(Load16Method, _u16, OpCodes.Conv_U8, null);
                        break;
                    case 0x27: // LWU
                        EmitLoad(Load32Method, _u32, OpCodes.Conv_U8, null);
                        break;
                    case 0x37: // LD
                        EmitLoad(Load64Method, _u64, null, null);
                        break;
                    case 0x28: // SB
                        EmitStore(Store8Method, OpCodes.Conv_U1);
                        break;
                    case 0x29: // SH
                        EmitStore(Store16Method, OpCodes.Conv_U2);
                        break;
                    case 0x2B: // SW
                        EmitStore(Store32Method, OpCodes.Conv_U4);
                        break;
                    case 0x3F: // SD
                        EmitStore(Store64Method, null);
                        break;
                    case 0x2F: // CACHE
                    case 0x33: // PREF
                        break;
                    case 0x31: // LWC1: reads the time (the FPU's stall)
                    {
                        EmitAddress();
                        FlushAll();
                        _il.Emit(OpCodes.Ldarg_0);
                        _il.Emit(OpCodes.Ldloc, _address);
                        Ldc(Rt);
                        _il.Emit(OpCodes.Call, Lwc1Method);
                        var done = _il.DefineLabel();
                        _il.Emit(OpCodes.Brtrue, done);
                        EmitInterpret(leave: true, currentFlushed: true);
                        _il.MarkLabel(done);
                        EmitFitCheck();
                        break;
                    }

                    case 0x39: // SWC1
                        EmitAddress();
                        _il.Emit(OpCodes.Ldarg_0);
                        _il.Emit(OpCodes.Ldloc, _address);
                        Ldc(Rt);
                        _il.Emit(OpCodes.Call, Swc1Method);
                        EmitSlowPathIfFalse();
                        break;
                    case 0x2A: // SWL
                    case 0x2C: // SDL
                    case 0x2D: // SDR
                    case 0x2E: // SWR
                    case 0x3E: // SQC2
                        EmitInterpret(leave: true);
                        break;
                    case 0x11: // the FPU (its branches are branches)
                        EmitDirect(Cop1Method, stops: false);
                        break;
                    case 0x12: // COP2: VU0's registers and macro instructions
                        EmitDirect(Cop2Method, stops: true);
                        break;
                    case 0x1C: // MMI
                        EmitDirect(MmiMethod, stops: false);
                        break;
                    default: // the unaligned loads, LQC2, traps, SYSCALL, COP0…
                        EmitInterpret();
                        break;
                }
            }

            /// <summary>
            /// Calls the interpreter's handler for a coprocessor or MMI instruction directly, with the PC state set as
            /// the interpreter's loop would have it (for the exceptions it may raise); leaves when it raised one, when
            /// the rest of the block no longer fits (stalls) and, with <paramref name="stops"/>, when it stopped the
            /// CPU or raised an interrupt.
            /// </summary>
            private void EmitDirect(MethodInfo handler, bool stops)
            {
                StoreField(CurrentPcField, () => Ldc(_pc));
                StoreField(InDelaySlotField, () => Ldc(_delay ? 1 : 0));
                StoreField(NextIsDelaySlotField, () => Ldc(0));
                if (_delay)
                {
                    StoreField(PcField, () => _il.Emit(OpCodes.Ldloc, _next));
                    StoreField(NextPcField, () =>
                    {
                        _il.Emit(OpCodes.Ldloc, _next);
                        Ldc(4);
                        _il.Emit(OpCodes.Add);
                    });
                }
                else
                {
                    StoreField(PcField, () => Ldc(_pc + 4));
                    StoreField(NextPcField, () => Ldc(_pc + 8));
                }

                FlushAll();
                _il.Emit(OpCodes.Ldarg_0);
                Ldc(_op);
                Ldc(Rs);
                Ldc(Rt);
                _il.Emit(OpCodes.Call, handler);

                // r0 stays zero (Execute's job).
                _il.Emit(OpCodes.Ldloc, _gpr);
                LdcLong(0);
                _il.Emit(OpCodes.Stind_I8);
                GprAddress(0, high: true);
                LdcLong(0);
                _il.Emit(OpCodes.Stind_I8);

                var leave = _il.DefineLabel();
                var go = _il.DefineLabel();
                LoadField(PcField);
                if (_delay)
                {
                    _il.Emit(OpCodes.Ldloc, _next);
                }
                else
                {
                    Ldc(_pc + 4);
                }

                _il.Emit(OpCodes.Bne_Un, leave);
                LoadField(NextIsDelaySlotField);
                _il.Emit(OpCodes.Brtrue, leave);
                if (stops)
                {
                    LoadField(StopRequestedField);
                    _il.Emit(OpCodes.Brtrue, leave);
                    LoadField(InterruptPendingField);
                    _il.Emit(OpCodes.Brtrue, leave);
                }

                if (!_delay)
                {
                    LoadField(CyclesField);
                    LdcLong(_remaining);
                    _il.Emit(OpCodes.Add);
                    LoadField(SliceEndField);
                    _il.Emit(OpCodes.Ble, go);
                }
                else
                {
                    _il.Emit(OpCodes.Br, go);
                }

                // The fields already say where the CPU is: the handler set them, or they were set above.
                _il.MarkLabel(leave);
                _il.Emit(OpCodes.Ret);
                _il.MarkLabel(go);
            }

            private void EmitSpecial()
            {
                var rs = Rs;
                var rt = Rt;
                var rd = Rd;
                var sa = Sa;
                switch (_op & 0x3F)
                {
                    case 0x00: // SLL
                        EmitShift32(rd, rt, () => Ldc(sa), OpCodes.Shl);
                        break;
                    case 0x02: // SRL
                        EmitShift32(rd, rt, () => Ldc(sa), OpCodes.Shr_Un);
                        break;
                    case 0x03: // SRA
                        EmitShift32(rd, rt, () => Ldc(sa), OpCodes.Shr);
                        break;
                    case 0x04: // SLLV
                        EmitShift32(rd, rt, () => ShiftAmount(rs, 31), OpCodes.Shl);
                        break;
                    case 0x06: // SRLV
                        EmitShift32(rd, rt, () => ShiftAmount(rs, 31), OpCodes.Shr_Un);
                        break;
                    case 0x07: // SRAV
                        EmitShift32(rd, rt, () => ShiftAmount(rs, 31), OpCodes.Shr);
                        break;
                    case 0x0A: // MOVZ
                    case 0x0B: // MOVN
                    {
                        if (rd == 0)
                        {
                            break;
                        }

                        var skip = _il.DefineLabel();
                        Gpr64(rt);
                        _il.Emit((_op & 0x3F) == 0x0A ? OpCodes.Brtrue : OpCodes.Brfalse, skip);
                        SetGpr(rd, () => Gpr64(rs));
                        _il.MarkLabel(skip);
                        break;
                    }

                    case 0x0F: // SYNC
                        break;
                    case 0x10: // MFHI
                    case 0x12: // MFLO
                        FlushAll();
                        _il.Emit(OpCodes.Ldarg_0);
                        Ldc(rd);
                        Ldc((_op & 0x3F) == 0x10 ? 1 : 0);
                        _il.Emit(OpCodes.Call, MfHiLoMethod);
                        EmitFitCheck();
                        break;
                    case 0x11: // MTHI
                        StoreField(HiField, () => Gpr64(rs));
                        break;
                    case 0x13: // MTLO
                        StoreField(LoField, () => Gpr64(rs));
                        break;
                    case 0x14: // DSLLV
                        EmitShift64(rd, rt, () => ShiftAmount(rs, 63), OpCodes.Shl);
                        break;
                    case 0x16: // DSRLV
                        EmitShift64(rd, rt, () => ShiftAmount(rs, 63), OpCodes.Shr_Un);
                        break;
                    case 0x17: // DSRAV
                        EmitShift64(rd, rt, () => ShiftAmount(rs, 63), OpCodes.Shr);
                        break;
                    case 0x18: // MULT
                    case 0x19: // MULTU
                        FlushAll();
                        _il.Emit(OpCodes.Ldarg_0);
                        Ldc(rs);
                        Ldc(rt);
                        Ldc(rd);
                        _il.Emit(OpCodes.Call, (_op & 0x3F) == 0x18 ? MultMethod : MultuMethod);
                        break;
                    case 0x20: // ADD
                        EmitAdd32(() => Gpr32(rs), () => Gpr32(rt), rd);
                        break;
                    case 0x21: // ADDU
                        EmitBinary32(rd, rs, rt, OpCodes.Add);
                        break;
                    case 0x22: // SUB
                        EmitSub32(rs, rt, rd);
                        break;
                    case 0x23: // SUBU
                        EmitBinary32(rd, rs, rt, OpCodes.Sub);
                        break;
                    case 0x24: // AND
                        EmitBinary64(rd, rs, rt, OpCodes.And);
                        break;
                    case 0x25: // OR
                        EmitBinary64(rd, rs, rt, OpCodes.Or);
                        break;
                    case 0x26: // XOR
                        EmitBinary64(rd, rs, rt, OpCodes.Xor);
                        break;
                    case 0x27: // NOR
                        SetGpr(rd, () =>
                        {
                            Gpr64(rs);
                            Gpr64(rt);
                            _il.Emit(OpCodes.Or);
                            _il.Emit(OpCodes.Not);
                        });
                        break;
                    case 0x28: // MFSA
                        SetGpr(rd, () =>
                        {
                            LoadField(SaField);
                            _il.Emit(OpCodes.Conv_U8);
                        });
                        break;
                    case 0x29: // MTSA
                        StoreField(SaField, () =>
                        {
                            Gpr32(rs);
                            Ldc(0xF);
                            _il.Emit(OpCodes.And);
                        });
                        break;
                    case 0x2A: // SLT
                        EmitCompare(rd, rs, rt, OpCodes.Clt);
                        break;
                    case 0x2B: // SLTU
                        EmitCompare(rd, rs, rt, OpCodes.Clt_Un);
                        break;
                    case 0x2C: // DADD
                        EmitAdd64(() => Gpr64(rs), () => Gpr64(rt), rd, subtract: false);
                        break;
                    case 0x2D: // DADDU
                        EmitBinary64(rd, rs, rt, OpCodes.Add);
                        break;
                    case 0x2E: // DSUB
                        EmitAdd64(() => Gpr64(rs), () => Gpr64(rt), rd, subtract: true);
                        break;
                    case 0x2F: // DSUBU
                        EmitBinary64(rd, rs, rt, OpCodes.Sub);
                        break;
                    case 0x38: // DSLL
                        EmitShift64(rd, rt, () => Ldc(sa), OpCodes.Shl);
                        break;
                    case 0x3A: // DSRL
                        EmitShift64(rd, rt, () => Ldc(sa), OpCodes.Shr_Un);
                        break;
                    case 0x3B: // DSRA
                        EmitShift64(rd, rt, () => Ldc(sa), OpCodes.Shr);
                        break;
                    case 0x3C: // DSLL32
                        EmitShift64(rd, rt, () => Ldc(sa + 32), OpCodes.Shl);
                        break;
                    case 0x3E: // DSRL32
                        EmitShift64(rd, rt, () => Ldc(sa + 32), OpCodes.Shr_Un);
                        break;
                    case 0x3F: // DSRA32
                        EmitShift64(rd, rt, () => Ldc(sa + 32), OpCodes.Shr);
                        break;
                    default: // DIV, DIVU, SYSCALL, BREAK, traps, reserved…
                        EmitInterpret();
                        break;
                }
            }

            private void ShiftAmount(int register, int mask)
            {
                Gpr32(register);
                Ldc(mask);
                _il.Emit(OpCodes.And);
            }

            /// <summary>A 32-bit shift of rt, sign-extended into rd.</summary>
            private void EmitShift32(int rd, int rt, Action amount, OpCode shift) => SetGpr(rd, () =>
            {
                Gpr32(rt);
                amount();
                _il.Emit(shift);
                _il.Emit(OpCodes.Conv_I8);
            });

            private void EmitShift64(int rd, int rt, Action amount, OpCode shift) => SetGpr(rd, () =>
            {
                Gpr64(rt);
                amount();
                _il.Emit(shift);
            });

            /// <summary>A 32-bit operation, sign-extended into rd (ADDU, SUBU).</summary>
            private void EmitBinary32(int rd, int rs, int rt, OpCode operation) => SetGpr(rd, () =>
            {
                Gpr32(rs);
                Gpr32(rt);
                _il.Emit(operation);
                _il.Emit(OpCodes.Conv_I8);
            });

            private void EmitBinary64(int rd, int rs, int rt, OpCode operation) => SetGpr(rd, () =>
            {
                Gpr64(rs);
                Gpr64(rt);
                _il.Emit(operation);
            });

            private void EmitCompare(int rd, int rs, int rt, OpCode compare) => SetGpr(rd, () =>
            {
                Gpr64(rs);
                Gpr64(rt);
                _il.Emit(compare);
                _il.Emit(OpCodes.Conv_U8);
            });

            private void EmitLogicImmediate(OpCode operation) => SetGpr(Rt, () =>
            {
                Gpr64(Rs);
                LdcLong(_op & 0xFFFF);
                _il.Emit(operation);
            });

            /// <summary>ADD/ADDI: on overflow, the interpreter raises the exception.</summary>
            private void EmitAdd32(Action a, Action b, int rd)
            {
                a();
                _il.Emit(OpCodes.Stloc, _a32);
                b();
                _il.Emit(OpCodes.Stloc, _b32);
                _il.Emit(OpCodes.Ldloc, _a32);
                _il.Emit(OpCodes.Ldloc, _b32);
                _il.Emit(OpCodes.Add);
                _il.Emit(OpCodes.Stloc, _r32);
                OverflowCheck32(_b32);
                SetGpr(rd, () =>
                {
                    _il.Emit(OpCodes.Ldloc, _r32);
                    _il.Emit(OpCodes.Conv_I8);
                });
            }

            private void EmitSub32(int rs, int rt, int rd)
            {
                Gpr32(rs);
                _il.Emit(OpCodes.Stloc, _a32);
                Gpr32(rt);
                _il.Emit(OpCodes.Stloc, _b32);
                _il.Emit(OpCodes.Ldloc, _a32);
                _il.Emit(OpCodes.Ldloc, _b32);
                _il.Emit(OpCodes.Sub);
                _il.Emit(OpCodes.Stloc, _r32);

                // ((a ^ b) & (a ^ difference)) < 0 overflows
                var fine = _il.DefineLabel();
                _il.Emit(OpCodes.Ldloc, _a32);
                _il.Emit(OpCodes.Ldloc, _b32);
                _il.Emit(OpCodes.Xor);
                _il.Emit(OpCodes.Ldloc, _a32);
                _il.Emit(OpCodes.Ldloc, _r32);
                _il.Emit(OpCodes.Xor);
                _il.Emit(OpCodes.And);
                Ldc(0);
                _il.Emit(OpCodes.Bge, fine);
                EmitInterpret(leave: true);
                _il.MarkLabel(fine);
                SetGpr(rd, () =>
                {
                    _il.Emit(OpCodes.Ldloc, _r32);
                    _il.Emit(OpCodes.Conv_I8);
                });
            }

            /// <summary>((a ^ sum) &amp; (b ^ sum)) &lt; 0 overflows: the interpreter takes it from there.</summary>
            private void OverflowCheck32(LocalBuilder b)
            {
                var fine = _il.DefineLabel();
                _il.Emit(OpCodes.Ldloc, _a32);
                _il.Emit(OpCodes.Ldloc, _r32);
                _il.Emit(OpCodes.Xor);
                _il.Emit(OpCodes.Ldloc, b);
                _il.Emit(OpCodes.Ldloc, _r32);
                _il.Emit(OpCodes.Xor);
                _il.Emit(OpCodes.And);
                Ldc(0);
                _il.Emit(OpCodes.Bge, fine);
                EmitInterpret(leave: true);
                _il.MarkLabel(fine);
            }

            private void EmitAdd64(Action a, Action b, int rd, bool subtract)
            {
                a();
                _il.Emit(OpCodes.Stloc, _a64);
                b();
                _il.Emit(OpCodes.Stloc, _b64);
                _il.Emit(OpCodes.Ldloc, _a64);
                _il.Emit(OpCodes.Ldloc, _b64);
                _il.Emit(subtract ? OpCodes.Sub : OpCodes.Add);
                _il.Emit(OpCodes.Stloc, _r64);

                // Sum: ((a ^ r) & (b ^ r)) < 0. Difference: ((a ^ b) & (a ^ r)) < 0.
                var fine = _il.DefineLabel();
                _il.Emit(OpCodes.Ldloc, _a64);
                _il.Emit(OpCodes.Ldloc, subtract ? _b64 : _r64);
                _il.Emit(OpCodes.Xor);
                _il.Emit(OpCodes.Ldloc, subtract ? _a64 : _b64);
                _il.Emit(OpCodes.Ldloc, _r64);
                _il.Emit(OpCodes.Xor);
                _il.Emit(OpCodes.And);
                LdcLong(0);
                _il.Emit(OpCodes.Bge, fine);
                EmitInterpret(leave: true);
                _il.MarkLabel(fine);
                SetGpr(rd, () => _il.Emit(OpCodes.Ldloc, _r64));
            }

            // ---- Loads and stores ----

            /// <summary>Puts rs + the offset (32 bits) in the address local.</summary>
            private void EmitAddress()
            {
                Gpr32(Rs);
                Ldc(Immediate);
                _il.Emit(OpCodes.Add);
                _il.Emit(OpCodes.Stloc, _address);
            }

            /// <summary>After a fast path's bool: false runs the instruction in the interpreter, and the block ends.</summary>
            private void EmitSlowPathIfFalse()
            {
                var done = _il.DefineLabel();
                _il.Emit(OpCodes.Brtrue, done);
                EmitInterpret(leave: true);
                _il.MarkLabel(done);
            }

            private void EmitLoad(MethodInfo load, LocalBuilder value, OpCode? first, OpCode? second)
            {
                EmitAddress();
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldloc, _address);
                _il.Emit(OpCodes.Ldloca, value);
                _il.Emit(OpCodes.Call, load);
                EmitSlowPathIfFalse();
                SetGpr(Rt, () =>
                {
                    _il.Emit(OpCodes.Ldloc, value);
                    if (first is { } a)
                    {
                        _il.Emit(a);
                    }

                    if (second is { } b)
                    {
                        _il.Emit(b);
                    }
                });
            }

            private void EmitStore(MethodInfo store, OpCode? narrow)
            {
                EmitAddress();
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldloc, _address);
                Gpr64(Rt);
                if (narrow is { } conversion)
                {
                    _il.Emit(conversion);
                }

                _il.Emit(OpCodes.Call, store);
                EmitSlowPathIfFalse();
            }

            // ---- Branches ----

            /// <summary>
            /// The branch at <see cref="_pc"/>: sets the next local to where it goes after the delay slot, or leaves (a
            /// branch-likely not taken skips its delay slot).
            /// </summary>
            private void EmitBranch()
            {
                var primary = _op >> 26;
                var target = _pc + 4 + ((uint)(int)Immediate << 2);
                var link = (long)(int)(_pc + 8);
                switch (primary)
                {
                    case 0x00: // JR, JALR: the target is read before the link is written (JALR rd, rd)
                    {
                        Gpr32(Rs);
                        _il.Emit(OpCodes.Stloc, _next);
                        if ((_op & 0x3F) == 0x09)
                        {
                            SetGpr(Rd, () => LdcLong(link));
                        }

                        // A jump to itself is an idle loop: the interpreter's skip, which reads the time.
                        FlushAll();
                        var notIdle = _il.DefineLabel();
                        _il.Emit(OpCodes.Ldloc, _next);
                        Ldc(_pc);
                        _il.Emit(OpCodes.Bne_Un, notIdle);
                        _il.Emit(OpCodes.Ldarg_0);
                        Ldc(_pc);
                        _il.Emit(OpCodes.Call, IdleMethod);
                        _il.MarkLabel(notIdle);
                        return;
                    }

                    case 0x02: // J
                    case 0x03: // JAL
                    {
                        var jump = ((_pc + 4) & 0xF000_0000) | ((_op & 0x03FF_FFFF) << 2);
                        if (jump == _pc)
                        {
                            EmitBranchByInterpreter();
                            return;
                        }

                        if (primary == 0x03)
                        {
                            SetGpr(31, () => LdcLong(link));
                        }

                        Ldc(jump);
                        _il.Emit(OpCodes.Stloc, _next);
                        return;
                    }
                }

                if (target == _pc || primary is >= 0x10 and <= 0x12)
                {
                    EmitBranchByInterpreter(); // idle loops and the coprocessor branches
                    return;
                }

                // The condition, from the registers before any link is written.
                bool likely;
                bool linked = false;
                switch (primary)
                {
                    case 0x01:
                    {
                        var rt = Rt;
                        Gpr64(Rs);
                        LdcLong(0);
                        if ((rt & 1) == 0)
                        {
                            _il.Emit(OpCodes.Clt); // BLTZ…: rs < 0
                        }
                        else
                        {
                            _il.Emit(OpCodes.Clt); // BGEZ…: !(rs < 0)
                            Ldc(0);
                            _il.Emit(OpCodes.Ceq);
                        }

                        likely = (rt & 2) != 0;
                        linked = rt >= 0x10;
                        break;
                    }

                    case 0x04 or 0x14: // BEQ
                        Gpr64(Rs);
                        Gpr64(Rt);
                        _il.Emit(OpCodes.Ceq);
                        likely = primary == 0x14;
                        break;
                    case 0x05 or 0x15: // BNE
                        Gpr64(Rs);
                        Gpr64(Rt);
                        _il.Emit(OpCodes.Ceq);
                        Ldc(0);
                        _il.Emit(OpCodes.Ceq);
                        likely = primary == 0x15;
                        break;
                    case 0x06 or 0x16: // BLEZ: !(rs > 0)
                        Gpr64(Rs);
                        LdcLong(0);
                        _il.Emit(OpCodes.Cgt);
                        Ldc(0);
                        _il.Emit(OpCodes.Ceq);
                        likely = primary == 0x16;
                        break;
                    default: // BGTZ
                        Gpr64(Rs);
                        LdcLong(0);
                        _il.Emit(OpCodes.Cgt);
                        likely = primary == 0x17;
                        break;
                }

                _il.Emit(OpCodes.Stloc, _a32);
                if (linked)
                {
                    SetGpr(31, () => LdcLong(link));
                }

                var taken = _il.DefineLabel();
                var done = _il.DefineLabel();
                _il.Emit(OpCodes.Ldloc, _a32);
                _il.Emit(OpCodes.Brtrue, taken);
                if (likely)
                {
                    // Not taken: the delay slot is skipped.
                    EmitExit(_pc + 8);
                    _il.MarkLabel(taken);
                    Ldc(target);
                    _il.Emit(OpCodes.Stloc, _next);
                    return;
                }

                Ldc(_pc + 8);
                _il.Emit(OpCodes.Stloc, _next);
                _il.Emit(OpCodes.Br, done);
                _il.MarkLabel(taken);
                Ldc(target);
                _il.Emit(OpCodes.Stloc, _next);
                _il.MarkLabel(done);
            }

            /// <summary>The interpreter runs the branch; the block goes on with its delay slot unless it shouldn't.</summary>
            private void EmitBranchByInterpreter()
            {
                Flush(_pending - 1);
                _il.Emit(OpCodes.Ldarg_0);
                Ldc(_op);
                Ldc(_pc);
                _il.Emit(OpCodes.Call, BranchMethod);
                var go = _il.DefineLabel();
                _il.Emit(OpCodes.Brtrue, go);
                _il.Emit(OpCodes.Ret);
                _il.MarkLabel(go);
                LoadField(NextPcField);
                _il.Emit(OpCodes.Stloc, _next);
            }
        }
    }
}
