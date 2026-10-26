using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>The SPC700's 64 KB address space (the sound module's RAM, I/O registers and boot ROM).</summary>
public interface ISpc700Bus
{
    byte Read(ushort address);

    void Write(ushort address, byte value);
}

/// <summary>
/// Sony SPC700, the SNES's sound CPU (SPEC §7.11, Phase 14): every instruction, including the 16-bit word operations,
/// MUL and DIV, the decimal adjusts and the memory bit operations. <see cref="Step"/> runs an instruction and returns
/// its cycles (1.024 MHz), from the timing table the SingleStepTests spc700 suite confirms, plus 2 for a taken branch.
/// </summary>
/// <remarks>References: Sony SPC700 manual; anomie's "SPC700 doc"; the SingleStepTests spc700 suite (MIT).</remarks>
public sealed class Spc700 : IStateful
{
    public const byte FlagC = 0x01, FlagZ = 0x02, FlagI = 0x04, FlagH = 0x08, FlagB = 0x10, FlagP = 0x20, FlagV = 0x40, FlagN = 0x80;

    private static readonly StateTag Tag = StateTag.From("S700");

    private static readonly byte[] Cycles =
    [
        2, 8, 4, 5, 3, 4, 3, 6, 2, 6, 5, 4, 5, 4, 6, 8,
        2, 8, 4, 5, 4, 5, 5, 6, 5, 5, 6, 5, 2, 2, 4, 6,
        2, 8, 4, 5, 3, 4, 3, 6, 2, 6, 5, 4, 5, 4, 5, 4,
        2, 8, 4, 5, 4, 5, 5, 6, 5, 5, 6, 5, 2, 2, 3, 8,
        2, 8, 4, 5, 3, 4, 3, 6, 2, 6, 4, 4, 5, 4, 6, 6,
        2, 8, 4, 5, 4, 5, 5, 6, 5, 5, 4, 5, 2, 2, 4, 3,
        2, 8, 4, 5, 3, 4, 3, 6, 2, 6, 4, 4, 5, 4, 5, 5,
        2, 8, 4, 5, 4, 5, 5, 6, 5, 5, 5, 5, 2, 2, 3, 6,
        2, 8, 4, 5, 3, 4, 3, 6, 2, 6, 5, 4, 5, 2, 4, 5,
        2, 8, 4, 5, 4, 5, 5, 6, 5, 5, 5, 5, 2, 2, 12, 5,
        3, 8, 4, 5, 3, 4, 3, 6, 2, 6, 4, 4, 5, 2, 4, 4,
        2, 8, 4, 5, 4, 5, 5, 6, 5, 5, 5, 5, 2, 2, 3, 4,
        3, 8, 4, 5, 4, 5, 4, 7, 2, 5, 6, 4, 5, 2, 4, 9,
        2, 8, 4, 5, 5, 6, 6, 7, 4, 5, 5, 5, 2, 2, 6, 3,
        2, 8, 4, 5, 3, 4, 3, 6, 2, 4, 5, 3, 4, 3, 4, 7,
        2, 8, 4, 5, 4, 5, 5, 6, 3, 4, 5, 4, 2, 2, 4, 7,
    ];

    private readonly ISpc700Bus _bus;
    private int _extra;

    public Spc700(ISpc700Bus bus) => _bus = bus;

    public byte A { get; set; }

    public byte X { get; set; }

    public byte Y { get; set; }

    public byte Sp { get; set; }

    public byte Psw { get; set; }

    public ushort Pc { get; set; }

    /// <summary>After SLEEP or STOP: the SPC700 does nothing more until reset.</summary>
    public bool Halted { get; set; }

    public void Reset()
    {
        A = X = Y = 0;
        Sp = 0xEF;
        Psw = 0x02;
        Halted = false;
        Pc = (ushort)(_bus.Read(0xFFFE) | _bus.Read(0xFFFF) << 8);
    }

    /// <summary>Runs one instruction; returns its cycles.</summary>
    public int Step()
    {
        if (Halted)
        {
            return 2;
        }

        _extra = 0;
        var opcode = Fetch();
        Execute(opcode);
        return Cycles[opcode] + _extra;
    }

    private byte Read(int address) => _bus.Read((ushort)address);

    private void Write(int address, byte value) => _bus.Write((ushort)address, value);

    private byte Fetch() => Read(Pc++);

    private ushort FetchWord()
    {
        var low = Fetch();
        return (ushort)(low | Fetch() << 8);
    }

    private int Page => (Psw & FlagP) != 0 ? 0x100 : 0;

    private int Dp(int offset) => Page | (offset & 0xFF);

    private ushort ReadWordDp(int offset) => (ushort)(Read(Dp(offset)) | Read(Dp(offset + 1)) << 8);

    private void SetFlag(byte flag, bool on) => Psw = (byte)(on ? Psw | flag : Psw & ~flag);

    private byte Nz(int value)
    {
        Psw = (byte)(Psw & ~(FlagN | FlagZ) | (value & 0x80) | ((value & 0xFF) == 0 ? FlagZ : 0));
        return (byte)value;
    }

    private void Nz16(int value) =>
        Psw = (byte)(Psw & ~(FlagN | FlagZ) | ((value >> 8) & 0x80) | ((value & 0xFFFF) == 0 ? FlagZ : 0));

    private void Push(byte value)
    {
        Write(0x100 | Sp, value);
        Sp--;
    }

    private byte Pop()
    {
        Sp++;
        return Read(0x100 | Sp);
    }

    private void PushPc()
    {
        Push((byte)(Pc >> 8));
        Push((byte)Pc);
    }

    private void Branch(bool taken)
    {
        var offset = (sbyte)Fetch();
        if (taken)
        {
            Pc = (ushort)(Pc + offset);
            _extra = 2;
        }
    }

    // ---- ALU ----

    private byte Adc(byte a, byte b)
    {
        var result = a + b + (Psw & FlagC);
        SetFlag(FlagV, (~(a ^ b) & (a ^ result) & 0x80) != 0);
        SetFlag(FlagH, ((a ^ b ^ result) & 0x10) != 0);
        SetFlag(FlagC, result > 0xFF);
        return Nz(result);
    }

    private byte Sbc(byte a, byte b) => Adc(a, (byte)~b);

    private void Cmp(byte a, byte b)
    {
        var result = a - b;
        SetFlag(FlagC, result >= 0);
        Nz(result);
    }

    private byte Alu(int operation, byte a, byte b) => operation switch
    {
        0 => Nz(a | b),
        1 => Nz(a & b),
        2 => Nz(a ^ b),
        4 => Adc(a, b),
        5 => Sbc(a, b),
        _ => a,
    };

    private byte Shift(int operation, byte value)
    {
        int result;
        switch (operation)
        {
            case 0: // ASL
                SetFlag(FlagC, (value & 0x80) != 0);
                result = value << 1;
                break;
            case 1: // ROL
                result = (value << 1) | (Psw & FlagC);
                SetFlag(FlagC, (value & 0x80) != 0);
                break;
            case 2: // LSR
                SetFlag(FlagC, (value & 1) != 0);
                result = value >> 1;
                break;
            case 3: // ROR
                result = (value >> 1) | ((Psw & FlagC) << 7);
                SetFlag(FlagC, (value & 1) != 0);
                break;
            case 4: // DEC
                result = value - 1;
                break;
            default: // INC
                result = value + 1;
                break;
        }

        return Nz(result);
    }

    /// <summary>The address of a 13-bit memory bit operand, and the bit number.</summary>
    private (int Address, int Bit) MemoryBit()
    {
        var operand = FetchWord();
        return (operand & 0x1FFF, operand >> 13);
    }

    private void Execute(byte opcode)
    {
        var column = opcode & 0x0F;
        var row = opcode >> 4;

        // The ALU block: OR AND EOR CMP ADC SBC on A (columns 4–8, even rows: A,src) and their partners.
        if (row < 0xC && column is >= 4 and <= 9)
        {
            AluBlock(row, column);
            return;
        }

        switch (opcode)
        {
            // Column 0: branches and flags
            case 0x00: break;
            case 0x10: Branch((Psw & FlagN) == 0); break;
            case 0x20: Psw &= unchecked((byte)~FlagP); break;
            case 0x30: Branch((Psw & FlagN) != 0); break;
            case 0x40: Psw |= FlagP; break;
            case 0x50: Branch((Psw & FlagV) == 0); break;
            case 0x60: Psw &= unchecked((byte)~FlagC); break;
            case 0x70: Branch((Psw & FlagV) != 0); break;
            case 0x80: Psw |= FlagC; break;
            case 0x90: Branch((Psw & FlagC) == 0); break;
            case 0xA0: Psw |= FlagI; break;
            case 0xB0: Branch((Psw & FlagC) != 0); break;
            case 0xC0: Psw &= unchecked((byte)~FlagI); break;
            case 0xD0: Branch((Psw & FlagZ) == 0); break;
            case 0xE0: Psw &= unchecked((byte)~(FlagV | FlagH)); break;
            case 0xF0: Branch((Psw & FlagZ) != 0); break;

            // Column C (rows C–F) and D, E, F: moves, stack, misc
            case 0xC4: Write(Dp(Fetch()), A); break;
            case 0xD4: Write(Dp(Fetch() + X), A); break;
            case 0xE4: A = Nz(Read(Dp(Fetch()))); break;
            case 0xF4: A = Nz(Read(Dp(Fetch() + X))); break;
            case 0xC5: Write(FetchWord(), A); break;
            case 0xD5: Write((ushort)(FetchWord() + X), A); break;
            case 0xE5: A = Nz(Read(FetchWord())); break;
            case 0xF5: A = Nz(Read((ushort)(FetchWord() + X))); break;
            case 0xC6: Write(Dp(X), A); break;
            case 0xD6: Write((ushort)(FetchWord() + Y), A); break;
            case 0xE6: A = Nz(Read(Dp(X))); break;
            case 0xF6: A = Nz(Read((ushort)(FetchWord() + Y))); break;
            case 0xC7: Write(ReadWordDp(Fetch() + X), A); break;
            case 0xD7: Write((ushort)(ReadWordDp(Fetch()) + Y), A); break;
            case 0xE7: A = Nz(Read(ReadWordDp(Fetch() + X))); break;
            case 0xF7: A = Nz(Read((ushort)(ReadWordDp(Fetch()) + Y))); break;
            case 0xC8: Cmp(X, Fetch()); break;
            case 0xD8: Write(Dp(Fetch()), X); break;
            case 0xE8: A = Nz(Fetch()); break;
            case 0xF8: X = Nz(Read(Dp(Fetch()))); break;
            case 0xC9: Write(FetchWord(), X); break;
            case 0xD9: Write(Dp(Fetch() + Y), X); break;
            case 0xE9: X = Nz(Read(FetchWord())); break;
            case 0xF9: X = Nz(Read(Dp(Fetch() + Y))); break;

            // Column 1: TCALL
            case 0x01: case 0x11: case 0x21: case 0x31: case 0x41: case 0x51: case 0x61: case 0x71:
            case 0x81: case 0x91: case 0xA1: case 0xB1: case 0xC1: case 0xD1: case 0xE1: case 0xF1:
            {
                PushPc();
                var vector = 0xFFDE - row * 2;
                Pc = (ushort)(Read(vector) | Read(vector + 1) << 8);
                break;
            }

            // Column 2: SET1/CLR1 dp.bit; column 3: BBS/BBC dp.bit,rel
            case 0x02: case 0x12: case 0x22: case 0x32: case 0x42: case 0x52: case 0x62: case 0x72:
            case 0x82: case 0x92: case 0xA2: case 0xB2: case 0xC2: case 0xD2: case 0xE2: case 0xF2:
            {
                var address = Dp(Fetch());
                var value = Read(address);
                var mask = 1 << (opcode >> 5);
                Write(address, (byte)((opcode & 0x10) == 0 ? value | mask : value & ~mask));
                break;
            }

            case 0x03: case 0x13: case 0x23: case 0x33: case 0x43: case 0x53: case 0x63: case 0x73:
            case 0x83: case 0x93: case 0xA3: case 0xB3: case 0xC3: case 0xD3: case 0xE3: case 0xF3:
            {
                var value = Read(Dp(Fetch()));
                var set = (value & (1 << (opcode >> 5))) != 0;
                Branch((opcode & 0x10) == 0 ? set : !set);
                break;
            }

            // Column A
            case 0x0A: { var (a, b) = MemoryBit(); SetFlag(FlagC, (Psw & FlagC) != 0 || ((Read(a) >> b) & 1) != 0); break; }
            case 0x2A: { var (a, b) = MemoryBit(); SetFlag(FlagC, (Psw & FlagC) != 0 || ((Read(a) >> b) & 1) == 0); break; }
            case 0x4A: { var (a, b) = MemoryBit(); SetFlag(FlagC, (Psw & FlagC) != 0 && ((Read(a) >> b) & 1) != 0); break; }
            case 0x6A: { var (a, b) = MemoryBit(); SetFlag(FlagC, (Psw & FlagC) != 0 && ((Read(a) >> b) & 1) == 0); break; }
            case 0x8A: { var (a, b) = MemoryBit(); SetFlag(FlagC, ((Psw & FlagC) != 0) ^ (((Read(a) >> b) & 1) != 0)); break; }
            case 0xAA: { var (a, b) = MemoryBit(); SetFlag(FlagC, ((Read(a) >> b) & 1) != 0); break; }
            case 0xCA:
            {
                var (a, b) = MemoryBit();
                var value = Read(a);
                Write(a, (byte)((Psw & FlagC) != 0 ? value | (1 << b) : value & ~(1 << b)));
                break;
            }

            case 0xEA: { var (a, b) = MemoryBit(); Write(a, (byte)(Read(a) ^ (1 << b))); break; }
            case 0x1A: { var d = Fetch(); var value = (ushort)(ReadWordDp(d) - 1); WriteWordDp(d, value); Nz16(value); break; }
            case 0x3A: { var d = Fetch(); var value = (ushort)(ReadWordDp(d) + 1); WriteWordDp(d, value); Nz16(value); break; }
            case 0x5A: // CMPW YA,dp
            {
                var value = ReadWordDp(Fetch());
                var result = YA - value;
                SetFlag(FlagC, result >= 0);
                Nz16(result);
                break;
            }

            case 0x7A: AddWord(ReadWordDp(Fetch())); break;
            case 0x9A: AddWord((ushort)~ReadWordDp(Fetch()), carryIn: 1); break; // SUBW
            case 0xBA: { var value = ReadWordDp(Fetch()); YA = value; Nz16(value); break; } // MOVW YA,dp
            case 0xDA: { var d = Fetch(); Read(Dp(d)); WriteWordDp(d, YA); break; }       // MOVW dp,YA
            case 0xFA: { var source = Read(Dp(Fetch())); Write(Dp(Fetch()), source); break; } // MOV dp,dp

            // Column B: shifts and inc/dec on dp, dp+X; and MOV with Y
            case 0x0B: case 0x2B: case 0x4B: case 0x6B: case 0x8B: case 0xAB:
            {
                var address = Dp(Fetch());
                Write(address, Shift(ShiftOf(row), Read(address)));
                break;
            }

            case 0x1B: case 0x3B: case 0x5B: case 0x7B: case 0x9B: case 0xBB:
            {
                var address = Dp(Fetch() + X);
                Write(address, Shift(ShiftOf(row), Read(address)));
                break;
            }

            case 0xCB: Write(Dp(Fetch()), Y); break;
            case 0xDB: Write(Dp(Fetch() + X), Y); break;
            case 0xEB: Y = Nz(Read(Dp(Fetch()))); break;
            case 0xFB: Y = Nz(Read(Dp(Fetch() + X))); break;

            // Column C: shifts on !abs and A
            case 0x0C: case 0x2C: case 0x4C: case 0x6C: case 0x8C: case 0xAC:
            {
                var address = FetchWord();
                Write(address, Shift(ShiftOf(row), Read(address)));
                break;
            }

            case 0x1C: case 0x3C: case 0x5C: case 0x7C: case 0x9C: case 0xBC:
                A = Shift(ShiftOf(row), A);
                break;
            case 0xCC: Write(FetchWord(), Y); break;
            case 0xDC: Y = Nz(Y - 1); break;
            case 0xEC: Y = Nz(Read(FetchWord())); break;
            case 0xFC: Y = Nz(Y + 1); break;

            // Column D
            case 0x0D: Push(Psw); break;
            case 0x1D: X = Nz(X - 1); break;
            case 0x2D: Push(A); break;
            case 0x3D: X = Nz(X + 1); break;
            case 0x4D: Push(X); break;
            case 0x5D: X = Nz(A); break;
            case 0x6D: Push(Y); break;
            case 0x7D: A = Nz(X); break;
            case 0x8D: Y = Nz(Fetch()); break;
            case 0x9D: X = Nz(Sp); break;
            case 0xAD: Cmp(Y, Fetch()); break;
            case 0xBD: Sp = X; break;
            case 0xCD: X = Nz(Fetch()); break;
            case 0xDD: A = Nz(Y); break;
            case 0xED: Psw ^= FlagC; break;
            case 0xFD: Y = Nz(A); break;

            // Column E
            case 0x0E: case 0x4E: // TSET1 / TCLR1 !abs
            {
                var address = FetchWord();
                var value = Read(address);
                Nz(A - value);
                Write(address, (byte)(opcode == 0x0E ? value | A : value & ~A));
                break;
            }

            case 0x1E: Cmp(X, Read(FetchWord())); break;
            case 0x3E: Cmp(X, Read(Dp(Fetch()))); break;
            case 0x5E: Cmp(Y, Read(FetchWord())); break;
            case 0x7E: Cmp(Y, Read(Dp(Fetch()))); break;
            case 0x2E: { var value = Read(Dp(Fetch())); Branch(A != value); break; }      // CBNE dp,rel
            case 0xDE: { var value = Read(Dp(Fetch() + X)); Branch(A != value); break; }  // CBNE dp+X,rel
            case 0x6E: // DBNZ dp,rel
            {
                var address = Dp(Fetch());
                var value = (byte)(Read(address) - 1);
                Write(address, value);
                Branch(value != 0);
                break;
            }

            case 0xFE: Y--; Branch(Y != 0); break;
            case 0x8E: Psw = Pop(); break;
            case 0xAE: A = Pop(); break;
            case 0xCE: X = Pop(); break;
            case 0xEE: Y = Pop(); break;
            case 0x9E: Divide(); break;
            case 0xBE: // DAS
                if ((Psw & FlagC) == 0 || A > 0x99)
                {
                    A -= 0x60;
                    Psw &= unchecked((byte)~FlagC);
                }

                if ((Psw & FlagH) == 0 || (A & 0x0F) > 9)
                {
                    A -= 6;
                }

                Nz(A);
                break;

            // Column F
            case 0x0F: // BRK
                PushPc();
                Push(Psw);
                Psw = (byte)((Psw | FlagB) & ~FlagI);
                Pc = (ushort)(Read(0xFFDE) | Read(0xFFDF) << 8);
                break;
            case 0x1F: // JMP [!abs+X]
            {
                var pointer = (ushort)(FetchWord() + X);
                Pc = (ushort)(Read(pointer) | Read((ushort)(pointer + 1)) << 8);
                break;
            }

            case 0x2F: Pc = (ushort)(Pc + 1 + (sbyte)Read(Pc)); break; // BRA: the table's 4 cycles include the jump
            case 0x3F: { var target = FetchWord(); PushPc(); Pc = target; break; }   // CALL
            case 0x4F: { var target = Fetch(); PushPc(); Pc = (ushort)(0xFF00 | target); break; } // PCALL
            case 0x5F: Pc = FetchWord(); break;
            case 0x6F: { var low = Pop(); Pc = (ushort)(low | Pop() << 8); break; }  // RET
            case 0x7F: { Psw = Pop(); var low = Pop(); Pc = (ushort)(low | Pop() << 8); break; } // RETI
            case 0x8F: { var value = Fetch(); var address = Dp(Fetch()); Read(address); Write(address, value); break; } // MOV dp,#imm
            case 0x9F: A = Nz((A >> 4) | (A << 4)); break; // XCN
            case 0xAF: Write(Dp(X), A); X++; break;        // MOV (X)+,A
            case 0xBF: A = Nz(Read(Dp(X))); X++; break;    // MOV A,(X)+
            case 0xCF: { var product = Y * A; YA = (ushort)product; Nz(Y); break; } // MUL YA
            case 0xDF: // DAA
                if ((Psw & FlagC) != 0 || A > 0x99)
                {
                    A += 0x60;
                    Psw |= FlagC;
                }

                if ((Psw & FlagH) != 0 || (A & 0x0F) > 9)
                {
                    A += 6;
                }

                Nz(A);
                break;
            case 0xEF: // SLEEP
            case 0xFF: // STOP
                Halted = true;
                break;
        }
    }

    private static int ShiftOf(int row) => (row >> 1) switch
    {
        0 => 0, // ASL
        1 => 1, // ROL
        2 => 2, // LSR
        3 => 3, // ROR
        4 => 4, // DEC
        _ => 5, // INC
    };

    private ushort YA
    {
        get => (ushort)(Y << 8 | A);
        set
        {
            A = (byte)value;
            Y = (byte)(value >> 8);
        }
    }

    private void WriteWordDp(int offset, ushort value)
    {
        Write(Dp(offset), (byte)value);
        Write(Dp(offset + 1), (byte)(value >> 8));
    }

    /// <summary>ADDW YA,dp (and SUBW as an add of the complement with carry in).</summary>
    private void AddWord(ushort value, int carryIn = 0)
    {
        var ya = YA;
        var result = ya + value + carryIn;
        SetFlag(FlagC, result > 0xFFFF);
        SetFlag(FlagH, ((ya ^ value ^ result) & 0x1000) != 0);
        SetFlag(FlagV, (~(ya ^ value) & (ya ^ result) & 0x8000) != 0);
        YA = (ushort)result;
        Nz16(result);
    }

    /// <summary>DIV YA,X, including what the SPC700 gives when the quotient doesn't fit in 9 bits (from bsnes).</summary>
    private void Divide()
    {
        var ya = YA;
        SetFlag(FlagV, Y >= X);
        SetFlag(FlagH, (Y & 15) >= (X & 15));
        if (Y < X << 1)
        {
            A = (byte)(ya / X);
            Y = (byte)(ya % X);
        }
        else
        {
            A = (byte)(255 - (ya - (X << 9)) / (256 - X));
            Y = (byte)(X + (ya - (X << 9)) % (256 - X));
        }

        Nz(A);
    }

    /// <summary>
    /// The ALU block: rows 0–B, columns 4–9. Even rows hold "A, src" forms and odd rows the indexed partners; the
    /// operation is row / 2 (OR, AND, EOR, CMP, ADC, SBC). Column 8 of odd rows and column 9 are memory-to-memory.
    /// </summary>
    private void AluBlock(int row, int column)
    {
        var operation = row >> 1;
        var odd = (row & 1) != 0;
        if (column == 8 && odd)
        {
            // op dp,#imm
            var immediate = Fetch();
            var address = Dp(Fetch());
            MemoryOperation(operation, address, immediate);
            return;
        }

        if (column == 9)
        {
            if (!odd)
            {
                // op dp(d),dp(s): encoded source first
                var source = Read(Dp(Fetch()));
                var address = Dp(Fetch());
                MemoryOperation(operation, address, source);
            }
            else
            {
                // op (X),(Y)
                var source = Read(Dp(Y));
                MemoryOperation(operation, Dp(X), source);
            }

            return;
        }

        byte operand = (column, odd) switch
        {
            (4, false) => Read(Dp(Fetch())),
            (4, true) => Read(Dp(Fetch() + X)),
            (5, false) => Read(FetchWord()),
            (5, true) => Read((ushort)(FetchWord() + X)),
            (6, false) => Read(Dp(X)),
            (6, true) => Read((ushort)(FetchWord() + Y)),
            (7, false) => Read(ReadWordDp(Fetch() + X)),
            (7, true) => Read((ushort)(ReadWordDp(Fetch()) + Y)),
            _ => Fetch(), // (8, false): #imm
        };

        if (operation == 3)
        {
            Cmp(A, operand);
        }
        else
        {
            A = Alu(operation == 4 ? 4 : operation == 5 ? 5 : operation, A, operand);
        }
    }

    private void MemoryOperation(int operation, int address, byte source)
    {
        var destination = Read(address);
        if (operation == 3)
        {
            Cmp(destination, source);
            return;
        }

        Write(address, Alu(operation, destination, source));
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteByte(A);
        writer.WriteByte(X);
        writer.WriteByte(Y);
        writer.WriteByte(Sp);
        writer.WriteByte(Psw);
        writer.WriteUInt16(Pc);
        writer.WriteBool(Halted);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        A = reader.ReadByte();
        X = reader.ReadByte();
        Y = reader.ReadByte();
        Sp = reader.ReadByte();
        Psw = reader.ReadByte();
        Pc = reader.ReadUInt16();
        Halted = reader.ReadBool();
        reader.EndChunk();
    }
}
