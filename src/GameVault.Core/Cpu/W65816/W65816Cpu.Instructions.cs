namespace GameVault.Core.Cpu.W65816;

/// <summary>The 65C816's instructions and addressing modes.</summary>
public sealed partial class W65816Cpu
{
    private enum Mode
    {
        Immediate,
        DpXIndirect,
        StackRelative,
        Dp,
        DpIndirectLong,
        Absolute,
        Long,
        DpIndirectY,
        DpIndirect,
        StackRelativeIndirectY,
        DpX,
        DpY,
        DpIndirectLongY,
        AbsoluteY,
        AbsoluteX,
        LongX,
    }

    // The resolved operand address; bank-0 operands (direct page, stack) wrap their second byte within bank 0.
    private uint _ea;
    private bool _eaBank0;

    private void DirectPagePenalty()
    {
        if ((D & 0xFF) != 0)
        {
            Idle();
        }
    }

    /// <summary>A direct page address plus an index: in emulation mode with DL = 0 it stays within the page.</summary>
    private uint DpIndexed(byte offset, ushort index) =>
        E && (D & 0xFF) == 0 ? (uint)((D & 0xFF00) | ((offset + index) & 0xFF)) : Bank0(D + offset + index);

    /// <summary>A pointer in the direct page: in emulation mode with DL = 0 its high byte wraps within the page.</summary>
    private ushort ReadDpPointer(uint address)
    {
        var low = Read(address);
        var highAddress = E && (D & 0xFF) == 0 ? (address & 0xFF00) | ((address + 1) & 0xFF) : Bank0((int)address + 1);
        return (ushort)(low | Read(highAddress) << 8);
    }

    private void Resolve(Mode mode, bool write)
    {
        _eaBank0 = false;
        switch (mode)
        {
            case Mode.DpXIndirect:
            {
                var offset = FetchByte();
                DirectPagePenalty();
                Idle();
                var pointer = ReadDpPointer(DpIndexed(offset, X));
                _ea = (uint)(Dbr << 16 | pointer);
                break;
            }

            case Mode.StackRelative:
            {
                var offset = FetchByte();
                Idle();
                _ea = Bank0(S + offset);
                _eaBank0 = true;
                break;
            }

            case Mode.Dp:
            {
                var offset = FetchByte();
                DirectPagePenalty();
                _ea = Bank0(D + offset);
                _eaBank0 = true;
                break;
            }

            case Mode.DpIndirectLong:
            case Mode.DpIndirectLongY:
            {
                var offset = FetchByte();
                DirectPagePenalty();
                var at = Bank0(D + offset);
                var low = Read(at);
                var high = Read(Bank0((int)at + 1));
                var bank = Read(Bank0((int)at + 2));
                _ea = (uint)(bank << 16 | high << 8 | low);
                if (mode == Mode.DpIndirectLongY)
                {
                    _ea = (_ea + Y) & 0xFFFFFF;
                }

                break;
            }

            case Mode.Absolute:
                _ea = (uint)(Dbr << 16 | FetchWord());
                break;
            case Mode.Long:
            case Mode.LongX:
            {
                var address = FetchWord();
                var bank = FetchByte();
                _ea = (uint)(bank << 16 | address);
                if (mode == Mode.LongX)
                {
                    _ea = (_ea + X) & 0xFFFFFF;
                }

                break;
            }

            case Mode.DpIndirectY:
            {
                var offset = FetchByte();
                DirectPagePenalty();
                var pointer = ReadDpPointer(Bank0(D + offset));
                var baseAddress = (uint)(Dbr << 16 | pointer);
                _ea = (baseAddress + Y) & 0xFFFFFF;
                if (write || !X8 || ((baseAddress ^ _ea) & 0xFF00) != 0)
                {
                    Idle();
                }

                break;
            }

            case Mode.DpIndirect:
            {
                var offset = FetchByte();
                DirectPagePenalty();
                var pointer = ReadDpPointer(Bank0(D + offset));
                _ea = (uint)(Dbr << 16 | pointer);
                break;
            }

            case Mode.StackRelativeIndirectY:
            {
                var offset = FetchByte();
                Idle();
                var at = Bank0(S + offset);
                var pointer = Read(at) | Read(Bank0((int)at + 1)) << 8;
                Idle();
                _ea = (uint)((Dbr << 16 | pointer) + Y) & 0xFFFFFF;
                break;
            }

            case Mode.DpX:
            case Mode.DpY:
            {
                var offset = FetchByte();
                DirectPagePenalty();
                Idle();
                _ea = DpIndexed(offset, mode == Mode.DpX ? X : Y);
                _eaBank0 = true;
                break;
            }

            case Mode.AbsoluteX:
            case Mode.AbsoluteY:
            {
                var address = FetchWord();
                var baseAddress = (uint)(Dbr << 16 | address);
                _ea = (baseAddress + (mode == Mode.AbsoluteX ? X : Y)) & 0xFFFFFF;
                if (write || !X8 || ((baseAddress ^ _ea) & 0xFF00) != 0)
                {
                    Idle();
                }

                break;
            }
        }
    }

    private uint NextAddress(uint address) => _eaBank0 ? Bank0((int)address + 1) : (address + 1) & 0xFFFFFF;

    private ushort ReadOperand(bool wide)
    {
        var low = Read(_ea);
        return wide ? (ushort)(low | Read(NextAddress(_ea)) << 8) : low;
    }

    private void WriteOperand(bool wide, ushort value)
    {
        Write(_ea, (byte)value);
        if (wide)
        {
            Write(NextAddress(_ea), (byte)(value >> 8));
        }
    }

    /// <summary>A memory operand of the accumulator's width (immediate included).</summary>
    private ushort Load(Mode mode, bool wide)
    {
        if (mode == Mode.Immediate)
        {
            return wide ? FetchWord() : FetchByte();
        }

        Resolve(mode, write: false);
        return ReadOperand(wide);
    }

    private static Mode GroupOneMode(int low) => low switch
    {
        0x01 => Mode.DpXIndirect,
        0x03 => Mode.StackRelative,
        0x05 => Mode.Dp,
        0x07 => Mode.DpIndirectLong,
        0x09 => Mode.Immediate,
        0x0D => Mode.Absolute,
        0x0F => Mode.Long,
        0x11 => Mode.DpIndirectY,
        0x12 => Mode.DpIndirect,
        0x13 => Mode.StackRelativeIndirectY,
        0x15 => Mode.DpX,
        0x17 => Mode.DpIndirectLongY,
        0x19 => Mode.AbsoluteY,
        0x1D => Mode.AbsoluteX,
        _ => Mode.LongX,
    };

    private static bool IsGroupOne(int opcode) =>
        (opcode & 0x1F) is 0x01 or 0x03 or 0x05 or 0x07 or 0x09 or 0x0D or 0x0F or 0x11 or 0x12 or 0x13 or 0x15 or 0x17 or 0x19 or 0x1D or 0x1F
        && opcode != 0x89;

    private void Execute(byte opcode)
    {
        if (IsGroupOne(opcode))
        {
            GroupOne(opcode >> 5, GroupOneMode(opcode & 0x1F));
            return;
        }

        switch (opcode)
        {
            // Interrupts and control
            case 0x00: Software(E ? 0xFFFE : 0xFFE6); break;
            case 0x02: Software(E ? 0xFFF4 : 0xFFE4); break;
            case 0x40: Rti(); break;
            case 0x42: FetchByte(); break; // WDM
            case 0xCB: Idle(); Idle(); Idle(); Waiting = true; break; // WAI: 4 cycles, as the single-step suite counts
            case 0xDB: Idle(); Idle(); Idle(); Stopped = true; break; // STP
            case 0xEA: Idle(); break;

            // Flags
            case 0x18: Idle(); P &= unchecked((byte)~FlagC); break;
            case 0x38: Idle(); P |= FlagC; break;
            case 0x58: Idle(); P &= unchecked((byte)~FlagI); break;
            case 0x78: Idle(); P |= FlagI; break;
            case 0xB8: Idle(); P &= unchecked((byte)~FlagV); break;
            case 0xD8: Idle(); P &= unchecked((byte)~FlagD); break;
            case 0xF8: Idle(); P |= FlagD; break;
            case 0xC2: P &= (byte)~FetchByte(); Idle(); FixFlags(); break; // REP
            case 0xE2: P |= FetchByte(); Idle(); FixFlags(); break;          // SEP
            case 0xFB: // XCE
            {
                Idle();
                var carry = (P & FlagC) != 0;
                SetFlag(FlagC, E);
                E = carry;
                if (E)
                {
                    P |= FlagM | FlagX;
                    S = (ushort)(0x0100 | (S & 0xFF));
                }

                FixFlags();
                break;
            }

            // Transfers
            case 0xAA: Idle(); X = Transfer(A, X8); break;                 // TAX
            case 0xA8: Idle(); Y = Transfer(A, X8); break;                 // TAY
            case 0x8A: Idle(); A = TransferToA(X); break;                  // TXA
            case 0x98: Idle(); A = TransferToA(Y); break;                  // TYA
            case 0x9B: Idle(); Y = Transfer(X, X8); break;                 // TXY
            case 0xBB: Idle(); X = Transfer(Y, X8); break;                 // TYX
            case 0xBA: Idle(); X = Transfer(S, X8); break;                 // TSX
            case 0x9A: Idle(); S = E ? (ushort)(0x0100 | (X & 0xFF)) : X; break; // TXS
            case 0x5B: Idle(); D = A; SetNz16(D); break;                    // TCD
            case 0x7B: Idle(); A = D; SetNz16(A); break;                    // TDC
            case 0x1B: Idle(); S = E ? (ushort)(0x0100 | (A & 0xFF)) : A; break; // TCS
            case 0x3B: Idle(); A = S; SetNz16(A); break;                    // TSC
            case 0xEB: // XBA
                Idle();
                Idle();
                A = (ushort)(A >> 8 | A << 8);
                SetNz8(A);
                break;

            // Register increments
            case 0x1A: Idle(); A = M8 ? (ushort)(A & 0xFF00 | ((A + 1) & 0xFF)) : (ushort)(A + 1); SetNzA(); break;
            case 0x3A: Idle(); A = M8 ? (ushort)(A & 0xFF00 | ((A - 1) & 0xFF)) : (ushort)(A - 1); SetNzA(); break;
            case 0xE8: Idle(); X = IncIndex(X, 1); break;
            case 0xCA: Idle(); X = IncIndex(X, -1); break;
            case 0xC8: Idle(); Y = IncIndex(Y, 1); break;
            case 0x88: Idle(); Y = IncIndex(Y, -1); break;

            // Stack
            case 0x48: Idle(); PushWidth(A, !M8); break;  // PHA
            case 0xDA: Idle(); PushWidth(X, !X8); break;  // PHX
            case 0x5A: Idle(); PushWidth(Y, !X8); break;  // PHY
            case 0x08: Idle(); Push(P); break;            // PHP
            case 0x8B: Idle(); Push(Dbr); break;          // PHB
            case 0x4B: Idle(); Push(Pbr); break;          // PHK
            case 0x0B: Idle(); PushNew((byte)(D >> 8)); PushNew((byte)D); EndNew(); break; // PHD
            case 0x68: Idle(); Idle(); A = M8 ? (ushort)(A & 0xFF00 | Pull()) : PullWord(); SetNzA(); break; // PLA
            case 0xFA: Idle(); Idle(); X = X8 ? Pull() : PullWord(); SetNzIndex(X); break; // PLX
            case 0x7A: Idle(); Idle(); Y = X8 ? Pull() : PullWord(); SetNzIndex(Y); break; // PLY
            case 0x28: Idle(); Idle(); P = Pull(); FixFlags(); break;                       // PLP
            case 0xAB: Idle(); Idle(); Dbr = PullNew(); EndNew(); SetNz8(Dbr); break;       // PLB
            case 0x2B: // PLD
            {
                Idle();
                Idle();
                var low = PullNew();
                D = (ushort)(low | PullNew() << 8);
                EndNew();
                SetNz16(D);
                break;
            }

            case 0xF4: // PEA
            {
                var value = FetchWord();
                PushNew((byte)(value >> 8));
                PushNew((byte)value);
                EndNew();
                break;
            }

            case 0xD4: // PEI
            {
                var offset = FetchByte();
                DirectPagePenalty();
                var at = Bank0(D + offset);
                var value = Read(at) | Read(Bank0((int)at + 1)) << 8;
                PushNew((byte)(value >> 8));
                PushNew((byte)value);
                EndNew();
                break;
            }

            case 0x62: // PER
            {
                var offset = FetchWord();
                Idle();
                var value = (ushort)(Pc + offset);
                PushNew((byte)(value >> 8));
                PushNew((byte)value);
                EndNew();
                break;
            }

            // Jumps and calls
            case 0x4C: Pc = FetchWord(); break;                          // JMP abs
            case 0x5C: { var address = FetchWord(); Pbr = FetchByte(); Pc = address; break; } // JML long
            case 0x6C: // JMP (abs)
            {
                var pointer = FetchWord();
                Pc = (ushort)(Read(pointer) | Read((ushort)(pointer + 1)) << 8);
                break;
            }

            case 0x7C: // JMP (abs,X)
            {
                var pointer = (ushort)(FetchWord() + X);
                Idle();
                Pc = (ushort)(Read((uint)(Pbr << 16 | pointer)) | Read((uint)(Pbr << 16 | (ushort)(pointer + 1))) << 8);
                break;
            }

            case 0xDC: // JML [abs]
            {
                var pointer = FetchWord();
                var low = Read(pointer);
                var high = Read((ushort)(pointer + 1));
                Pbr = Read((ushort)(pointer + 2));
                Pc = (ushort)(low | high << 8);
                break;
            }

            case 0x20: // JSR abs
            {
                var target = FetchWord();
                Idle();
                var ret = (ushort)(Pc - 1);
                Push((byte)(ret >> 8));
                Push((byte)ret);
                Pc = target;
                break;
            }

            case 0x22: // JSL long
            {
                var target = FetchWord();
                PushNew(Pbr);
                Idle();
                var bank = FetchByte();
                var ret = (ushort)(Pc - 1);
                PushNew((byte)(ret >> 8));
                PushNew((byte)ret);
                EndNew();
                Pbr = bank;
                Pc = target;
                break;
            }

            case 0xFC: // JSR (abs,X)
            {
                var low = FetchByte();
                var ret = Pc;
                PushNew((byte)(ret >> 8));
                PushNew((byte)ret);
                var high = FetchByte();
                Idle();
                var pointer = (ushort)((low | high << 8) + X);
                Pc = (ushort)(Read((uint)(Pbr << 16 | pointer)) | Read((uint)(Pbr << 16 | (ushort)(pointer + 1))) << 8);
                EndNew();
                break;
            }

            case 0x60: // RTS
            {
                Idle();
                Idle();
                var low = Pull();
                Pc = (ushort)((low | Pull() << 8) + 1);
                Idle();
                break;
            }

            case 0x6B: // RTL
            {
                Idle();
                Idle();
                var low = PullNew();
                var high = PullNew();
                Pbr = PullNew();
                Pc = (ushort)((low | high << 8) + 1);
                EndNew();
                break;
            }

            // Branches
            case 0x10: Branch((P & FlagN) == 0); break;
            case 0x30: Branch((P & FlagN) != 0); break;
            case 0x50: Branch((P & FlagV) == 0); break;
            case 0x70: Branch((P & FlagV) != 0); break;
            case 0x90: Branch((P & FlagC) == 0); break;
            case 0xB0: Branch((P & FlagC) != 0); break;
            case 0xD0: Branch((P & FlagZ) == 0); break;
            case 0xF0: Branch((P & FlagZ) != 0); break;
            case 0x80: Branch(true); break;
            case 0x82: // BRL
            {
                var offset = FetchWord();
                Idle();
                Pc = (ushort)(Pc + offset);
                break;
            }

            // Block moves
            case 0x54: BlockMove(1); break;  // MVN
            case 0x44: BlockMove(-1); break; // MVP

            // BIT, TSB, TRB, STZ
            case 0x89: // BIT #: only Z
            {
                var value = M8 ? FetchByte() : FetchWord();
                SetFlag(FlagZ, (value & A & (M8 ? 0xFF : 0xFFFF)) == 0);
                break;
            }

            case 0x24: Bit(Mode.Dp); break;
            case 0x2C: Bit(Mode.Absolute); break;
            case 0x34: Bit(Mode.DpX); break;
            case 0x3C: Bit(Mode.AbsoluteX); break;
            case 0x04: TestBits(Mode.Dp, set: true); break;
            case 0x0C: TestBits(Mode.Absolute, set: true); break;
            case 0x14: TestBits(Mode.Dp, set: false); break;
            case 0x1C: TestBits(Mode.Absolute, set: false); break;
            case 0x64: Store(Mode.Dp, 0, !M8); break;
            case 0x74: Store(Mode.DpX, 0, !M8); break;
            case 0x9C: Store(Mode.Absolute, 0, !M8); break;
            case 0x9E: Store(Mode.AbsoluteX, 0, !M8); break;

            // Index loads, stores and compares
            case 0xA2: X = LoadIndex(Mode.Immediate); break;
            case 0xA6: X = LoadIndex(Mode.Dp); break;
            case 0xB6: X = LoadIndex(Mode.DpY); break;
            case 0xAE: X = LoadIndex(Mode.Absolute); break;
            case 0xBE: X = LoadIndex(Mode.AbsoluteY); break;
            case 0xA0: Y = LoadIndex(Mode.Immediate); break;
            case 0xA4: Y = LoadIndex(Mode.Dp); break;
            case 0xB4: Y = LoadIndex(Mode.DpX); break;
            case 0xAC: Y = LoadIndex(Mode.Absolute); break;
            case 0xBC: Y = LoadIndex(Mode.AbsoluteX); break;
            case 0x86: Store(Mode.Dp, X, !X8); break;
            case 0x96: Store(Mode.DpY, X, !X8); break;
            case 0x8E: Store(Mode.Absolute, X, !X8); break;
            case 0x84: Store(Mode.Dp, Y, !X8); break;
            case 0x94: Store(Mode.DpX, Y, !X8); break;
            case 0x8C: Store(Mode.Absolute, Y, !X8); break;
            case 0xE0: CompareIndex(X, Mode.Immediate); break;
            case 0xE4: CompareIndex(X, Mode.Dp); break;
            case 0xEC: CompareIndex(X, Mode.Absolute); break;
            case 0xC0: CompareIndex(Y, Mode.Immediate); break;
            case 0xC4: CompareIndex(Y, Mode.Dp); break;
            case 0xCC: CompareIndex(Y, Mode.Absolute); break;

            // Shifts, rotates, increments: accumulator
            case 0x0A: Idle(); A = ShiftA(0); break;
            case 0x2A: Idle(); A = ShiftA(1); break;
            case 0x4A: Idle(); A = ShiftA(2); break;
            case 0x6A: Idle(); A = ShiftA(3); break;

            // ... and memory (read-modify-write)
            case 0x06: Modify(Mode.Dp, 0); break;
            case 0x16: Modify(Mode.DpX, 0); break;
            case 0x0E: Modify(Mode.Absolute, 0); break;
            case 0x1E: Modify(Mode.AbsoluteX, 0); break;
            case 0x26: Modify(Mode.Dp, 1); break;
            case 0x36: Modify(Mode.DpX, 1); break;
            case 0x2E: Modify(Mode.Absolute, 1); break;
            case 0x3E: Modify(Mode.AbsoluteX, 1); break;
            case 0x46: Modify(Mode.Dp, 2); break;
            case 0x56: Modify(Mode.DpX, 2); break;
            case 0x4E: Modify(Mode.Absolute, 2); break;
            case 0x5E: Modify(Mode.AbsoluteX, 2); break;
            case 0x66: Modify(Mode.Dp, 3); break;
            case 0x76: Modify(Mode.DpX, 3); break;
            case 0x6E: Modify(Mode.Absolute, 3); break;
            case 0x7E: Modify(Mode.AbsoluteX, 3); break;
            case 0xE6: Modify(Mode.Dp, 4); break;
            case 0xF6: Modify(Mode.DpX, 4); break;
            case 0xEE: Modify(Mode.Absolute, 4); break;
            case 0xFE: Modify(Mode.AbsoluteX, 4); break;
            case 0xC6: Modify(Mode.Dp, 5); break;
            case 0xD6: Modify(Mode.DpX, 5); break;
            case 0xCE: Modify(Mode.Absolute, 5); break;
            case 0xDE: Modify(Mode.AbsoluteX, 5); break;
        }
    }

    // ---- Group one: ORA AND EOR ADC STA LDA CMP SBC ----

    private void GroupOne(int operation, Mode mode)
    {
        var wide = !M8;
        if (operation == 4)
        {
            Store(mode, A, wide);
            return;
        }

        var value = Load(mode, wide);
        switch (operation)
        {
            case 0: SetA((ushort)(A | value)); break;
            case 1: SetA((ushort)(A & (value | (wide ? 0 : 0xFF00)))); break;
            case 2: SetA((ushort)(A ^ value)); break;
            case 3: Add(value); break;
            case 5: SetA(value); break;
            case 6: Compare(A, value, wide); break;
            default: Subtract(value); break;
        }
    }

    /// <summary>Sets the accumulator's active width (B keeps its value in 8-bit mode) and N, Z.</summary>
    private void SetA(ushort value)
    {
        A = M8 ? (ushort)(A & 0xFF00 | (value & 0xFF)) : value;
        SetNzA();
    }

    private void SetNzA()
    {
        if (M8)
        {
            SetNz8(A);
        }
        else
        {
            SetNz16(A);
        }
    }

    private void SetNzIndex(ushort value)
    {
        if (X8)
        {
            SetNz8(value);
        }
        else
        {
            SetNz16(value);
        }
    }

    private void Store(Mode mode, ushort value, bool wide)
    {
        Resolve(mode, write: true);
        WriteOperand(wide, value);
    }

    private ushort Transfer(ushort value, bool narrow)
    {
        var result = narrow ? (ushort)(value & 0xFF) : value;
        if (narrow)
        {
            SetNz8(result);
        }
        else
        {
            SetNz16(result);
        }

        return result;
    }

    private ushort TransferToA(ushort value)
    {
        var result = M8 ? (ushort)(A & 0xFF00 | (value & 0xFF)) : value;
        if (M8)
        {
            SetNz8(result);
        }
        else
        {
            SetNz16(result);
        }

        return result;
    }

    private ushort IncIndex(ushort value, int delta)
    {
        var result = X8 ? (ushort)((value + delta) & 0xFF) : (ushort)(value + delta);
        SetNzIndex(result);
        return result;
    }

    private void PushWidth(ushort value, bool wide)
    {
        if (wide)
        {
            Push((byte)(value >> 8));
        }

        Push((byte)value);
    }

    private ushort PullWord()
    {
        var low = Pull();
        return (ushort)(low | Pull() << 8);
    }

    private ushort LoadIndex(Mode mode)
    {
        var value = Load(mode, !X8);
        SetNzIndex(value);
        return value;
    }

    private void CompareIndex(ushort register, Mode mode) => Compare(register, Load(mode, !X8), !X8);

    private void Compare(ushort register, ushort value, bool wide)
    {
        var mask = wide ? 0xFFFF : 0xFF;
        var result = (register & mask) - (value & mask);
        SetFlag(FlagC, result >= 0);
        if (wide)
        {
            SetNz16(result);
        }
        else
        {
            SetNz8(result);
        }
    }

    private void Bit(Mode mode)
    {
        var wide = !M8;
        var value = Load(mode, wide);
        var top = wide ? 0x8000 : 0x80;
        SetFlag(FlagN, (value & top) != 0);
        SetFlag(FlagV, (value & (top >> 1)) != 0);
        SetFlag(FlagZ, (value & A & (wide ? 0xFFFF : 0xFF)) == 0);
    }

    private void TestBits(Mode mode, bool set)
    {
        var wide = !M8;
        Resolve(mode, write: true);
        var value = ReadOperand(wide);
        Idle();
        SetFlag(FlagZ, (value & A & (wide ? 0xFFFF : 0xFF)) == 0);
        value = set ? (ushort)(value | A) : (ushort)(value & ~A);
        WriteBack(wide, value);
    }

    /// <summary>A read-modify-write's write: the high byte first.</summary>
    private void WriteBack(bool wide, ushort value)
    {
        if (wide)
        {
            Write(NextAddress(_ea), (byte)(value >> 8));
        }

        Write(_ea, (byte)value);
    }

    /// <summary>ASL (0), ROL (1), LSR (2), ROR (3), INC (4) and DEC (5) on memory.</summary>
    private void Modify(Mode mode, int operation)
    {
        var wide = !M8;
        Resolve(mode, write: true);
        var value = ReadOperand(wide);
        Idle();
        WriteBack(wide, ModifyValue(operation, value, wide));
    }

    private ushort ShiftA(int operation)
    {
        var wide = !M8;
        var result = ModifyValue(operation, wide ? A : (ushort)(A & 0xFF), wide);
        return wide ? result : (ushort)(A & 0xFF00 | result);
    }

    private ushort ModifyValue(int operation, ushort value, bool wide)
    {
        var top = wide ? 0x8000 : 0x80;
        var mask = wide ? 0xFFFF : 0xFF;
        int result;
        switch (operation)
        {
            case 0:
                SetFlag(FlagC, (value & top) != 0);
                result = (value << 1) & mask;
                break;
            case 1:
                result = ((value << 1) | (P & FlagC)) & mask;
                SetFlag(FlagC, (value & top) != 0);
                break;
            case 2:
                SetFlag(FlagC, (value & 1) != 0);
                result = value >> 1;
                break;
            case 3:
                result = (value >> 1) | ((P & FlagC) != 0 ? top : 0);
                SetFlag(FlagC, (value & 1) != 0);
                break;
            case 4:
                result = (value + 1) & mask;
                break;
            default:
                result = (value - 1) & mask;
                break;
        }

        if (wide)
        {
            SetNz16(result);
        }
        else
        {
            SetNz8(result);
        }

        return (ushort)result;
    }

    private void Branch(bool taken)
    {
        var offset = (sbyte)FetchByte();
        if (!taken)
        {
            return;
        }

        Idle();
        var target = (ushort)(Pc + offset);
        if (E && ((target ^ Pc) & 0xFF00) != 0)
        {
            Idle();
        }

        Pc = target;
    }

    /// <summary>MVN/MVP: one byte per step; the instruction repeats (PC stays) until A runs out.</summary>
    private void BlockMove(int direction)
    {
        var destinationBank = FetchByte();
        var sourceBank = FetchByte();
        Dbr = destinationBank;
        var value = Read((uint)(sourceBank << 16 | X));
        Write((uint)(destinationBank << 16 | Y), value);
        Idle();
        Idle();
        X = X8 ? (ushort)((X + direction) & 0xFF) : (ushort)(X + direction);
        Y = X8 ? (ushort)((Y + direction) & 0xFF) : (ushort)(Y + direction);
        A--;
        if (A != 0xFFFF)
        {
            Pc -= 3;
        }
    }

    private void Software(int vector)
    {
        FetchByte(); // the signature byte
        Interrupt(vector, software: true);
    }

    private void Rti()
    {
        Idle();
        Idle();
        P = Pull();
        FixFlags();
        var low = Pull();
        Pc = (ushort)(low | Pull() << 8);
        if (!E)
        {
            Pbr = Pull();
        }
    }

    // ---- Arithmetic (decimal mode as in bsnes, which matches the hardware's flags) ----

    private void Add(ushort data)
    {
        var carry = P & FlagC;
        int result;
        if (M8)
        {
            var a = A & 0xFF;
            if ((P & FlagD) == 0)
            {
                result = a + data + carry;
            }
            else
            {
                result = (a & 0x0F) + (data & 0x0F) + carry;
                if (result > 0x09)
                {
                    result += 0x06;
                }

                carry = result > 0x0F ? 1 : 0;
                result = (a & 0xF0) + (data & 0xF0) + (carry << 4) + (result & 0x0F);
            }

            SetFlag(FlagV, (~(a ^ data) & (a ^ result) & 0x80) != 0);
            if ((P & FlagD) != 0 && result > 0x9F)
            {
                result += 0x60;
            }

            SetFlag(FlagC, result > 0xFF);
            A = (ushort)(A & 0xFF00 | (result & 0xFF));
            SetNz8(result);
            return;
        }

        if ((P & FlagD) == 0)
        {
            result = A + data + carry;
        }
        else
        {
            result = (A & 0x000F) + (data & 0x000F) + carry;
            if (result > 0x0009)
            {
                result += 0x0006;
            }

            carry = result > 0x000F ? 1 : 0;
            result = (A & 0x00F0) + (data & 0x00F0) + (carry << 4) + (result & 0x000F);
            if (result > 0x009F)
            {
                result += 0x0060;
            }

            carry = result > 0x00FF ? 1 : 0;
            result = (A & 0x0F00) + (data & 0x0F00) + (carry << 8) + (result & 0x00FF);
            if (result > 0x09FF)
            {
                result += 0x0600;
            }

            carry = result > 0x0FFF ? 1 : 0;
            result = (A & 0xF000) + (data & 0xF000) + (carry << 12) + (result & 0x0FFF);
        }

        SetFlag(FlagV, (~(A ^ data) & (A ^ result) & 0x8000) != 0);
        if ((P & FlagD) != 0 && result > 0x9FFF)
        {
            result += 0x6000;
        }

        SetFlag(FlagC, result > 0xFFFF);
        A = (ushort)result;
        SetNz16(result);
    }

    private void Subtract(ushort data)
    {
        var carry = P & FlagC;
        int result;
        if (M8)
        {
            var a = A & 0xFF;
            var d = data ^ 0xFF;
            if ((P & FlagD) == 0)
            {
                result = a + d + carry;
            }
            else
            {
                result = (a & 0x0F) + (d & 0x0F) + carry;
                if (result <= 0x0F)
                {
                    result -= 0x06;
                }

                carry = result > 0x0F ? 1 : 0;
                result = (a & 0xF0) + (d & 0xF0) + (carry << 4) + (result & 0x0F);
            }

            SetFlag(FlagV, (~(a ^ d) & (a ^ result) & 0x80) != 0);
            if ((P & FlagD) != 0 && result <= 0xFF)
            {
                result -= 0x60;
            }

            SetFlag(FlagC, result > 0xFF);
            A = (ushort)(A & 0xFF00 | (result & 0xFF));
            SetNz8(result);
            return;
        }

        var w = data ^ 0xFFFF;
        if ((P & FlagD) == 0)
        {
            result = A + w + carry;
        }
        else
        {
            result = (A & 0x000F) + (w & 0x000F) + carry;
            if (result <= 0x000F)
            {
                result -= 0x0006;
            }

            carry = result > 0x000F ? 1 : 0;
            result = (A & 0x00F0) + (w & 0x00F0) + (carry << 4) + (result & 0x000F);
            if (result <= 0x00FF)
            {
                result -= 0x0060;
            }

            carry = result > 0x00FF ? 1 : 0;
            result = (A & 0x0F00) + (w & 0x0F00) + (carry << 8) + (result & 0x00FF);
            if (result <= 0x0FFF)
            {
                result -= 0x0600;
            }

            carry = result > 0x0FFF ? 1 : 0;
            result = (A & 0xF000) + (w & 0xF000) + (carry << 12) + (result & 0x0FFF);
        }

        SetFlag(FlagV, (~(A ^ w) & (A ^ result) & 0x8000) != 0);
        if ((P & FlagD) != 0 && result <= 0xFFFF)
        {
            result -= 0x6000;
        }

        SetFlag(FlagC, result > 0xFFFF);
        A = (ushort)result;
        SetNz16(result);
    }
}
