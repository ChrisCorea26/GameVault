using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.Cpu.Z80;

/// <summary>Memory and I/O as the Z80 sees them.</summary>
public interface IZ80Bus
{
    byte Read(ushort address);

    void Write(ushort address, byte value);

    byte In(ushort port);

    void Out(ushort port, byte value);
}

/// <summary>
/// Zilog Z80 (SPEC §7.11, Phase 11): every documented and undocumented instruction, including the CB, ED, DD,
/// FD, DDCB and FDCB prefixes, the undocumented flags (bits 3 and 5) and MEMPTR (WZ), interrupt modes 0–2, NMI,
/// HALT, the R register and the EI delay.
/// </summary>
/// <remarks>
/// One <see cref="Step"/> runs a whole instruction (or accepts an interrupt) and returns its T-states; the
/// console clocks the rest of the system by that many cycles. References: Zilog Z80 CPU User Manual (UM0080);
/// Sean Young, "The Undocumented Z80 Documented"; the MEMPTR notes by Boo-boo and Vladimir Kladov; and David
/// Banks' 2018 findings for the flags of repeating block instructions.
/// </remarks>
public sealed class Z80Cpu : IStateful
{
    public const byte FlagC = 0x01, FlagN = 0x02, FlagPV = 0x04, FlagX = 0x08, FlagH = 0x10, FlagY = 0x20, FlagZ = 0x40, FlagS = 0x80;

    private static readonly StateTag Tag = StateTag.From("Z80 ");

    /// <summary>S, Z, X and Y for a result byte.</summary>
    private static readonly byte[] Szxy = BuildSzxy(parity: false);

    /// <summary>S, Z, X, Y and parity for a result byte.</summary>
    private static readonly byte[] Szxyp = BuildSzxy(parity: true);

    private readonly IZ80Bus _bus;

    // Index mode for the instruction being decoded: 0 = HL, 1 = IX, 2 = IY.
    private int _index;

    // The instruction just executed was EI: interrupts wait one more instruction.
    private bool _afterEi;
    private bool _nmiPending;

    // Q: the flags the last instruction wrote (0 when it wrote none); SCF and CCF read it for X and Y.
    private byte _q;

    public Z80Cpu(IZ80Bus bus) => _bus = bus;

    public byte A { get; set; }

    public byte F { get; set; }

    public byte B { get; set; }

    public byte C { get; set; }

    public byte D { get; set; }

    public byte E { get; set; }

    public byte H { get; set; }

    public byte L { get; set; }

    public ushort IX { get; set; }

    public ushort IY { get; set; }

    public ushort SP { get; set; }

    public ushort PC { get; set; }

    /// <summary>MEMPTR: an internal register that shows in the X and Y flags of some instructions.</summary>
    public ushort WZ { get; set; }

    public byte I { get; set; }

    public byte R { get; set; }

    public ushort AltAF { get; set; }

    public ushort AltBC { get; set; }

    public ushort AltDE { get; set; }

    public ushort AltHL { get; set; }

    public bool Iff1 { get; set; }

    public bool Iff2 { get; set; }

    /// <summary>Interrupt mode 0, 1 or 2.</summary>
    public int InterruptMode { get; set; }

    public bool Halted { get; set; }

    /// <summary>The /INT line (level-triggered; held by the device until acknowledged in its own registers).</summary>
    public bool IrqLine { get; set; }

    /// <summary>What a device puts on the data bus when an interrupt is acknowledged (IM 0 and IM 2). Open bus is $FF.</summary>
    public byte InterruptData { get; set; } = 0xFF;

    /// <summary>T-states since power-on.</summary>
    public long Cycles { get; private set; }

    public ushort AF
    {
        get => (ushort)(A << 8 | F);
        set
        {
            A = (byte)(value >> 8);
            F = (byte)value;
        }
    }

    public ushort BC
    {
        get => (ushort)(B << 8 | C);
        set
        {
            B = (byte)(value >> 8);
            C = (byte)value;
        }
    }

    public ushort DE
    {
        get => (ushort)(D << 8 | E);
        set
        {
            D = (byte)(value >> 8);
            E = (byte)value;
        }
    }

    public ushort HL
    {
        get => (ushort)(H << 8 | L);
        set
        {
            H = (byte)(value >> 8);
            L = (byte)value;
        }
    }

    /// <summary>Signals an NMI (edge-triggered); it is taken before the next instruction.</summary>
    public void RaiseNmi() => _nmiPending = true;

    /// <summary>Power-on: the documented reset state, with AF and SP at $FFFF as on real chips.</summary>
    public void Power()
    {
        AF = 0xFFFF;
        BC = DE = HL = 0;
        AltAF = AltBC = AltDE = AltHL = 0;
        IX = IY = 0;
        SP = 0xFFFF;
        Reset();
        Cycles = 0;
    }

    public void Reset()
    {
        PC = 0;
        I = R = 0;
        Iff1 = Iff2 = false;
        InterruptMode = 0;
        Halted = false;
        WZ = 0;
        _afterEi = false;
        _nmiPending = false;
        _q = 0;
    }

    /// <summary>Runs one instruction, or accepts a pending interrupt. Returns the T-states taken.</summary>
    public int Step()
    {
        int cycles;
        if (_nmiPending)
        {
            _nmiPending = false;
            Halted = false;
            IncrementR();
            Iff1 = false;
            Push(PC);
            PC = WZ = 0x0066;
            cycles = 11;
        }
        else if (IrqLine && Iff1 && !_afterEi)
        {
            cycles = AcceptInterrupt();
        }
        else
        {
            _afterEi = false;
            if (Halted)
            {
                IncrementR();
                cycles = 4;
            }
            else
            {
                var flagsBefore = F;
                _index = 0;
                cycles = Execute(FetchOpcode());
                // Approximates Q: an instruction that rewrote F with the same value counts as not writing it.
                _q = F != flagsBefore ? F : (byte)0;
            }
        }

        Cycles += cycles;
        return cycles;
    }

    private int AcceptInterrupt()
    {
        Halted = false;
        Iff1 = Iff2 = false;
        IncrementR();
        switch (InterruptMode)
        {
            case 2:
                Push(PC);
                var vector = (ushort)(I << 8 | InterruptData);
                PC = WZ = ReadWord(vector);
                return 19;
            case 1:
                Push(PC);
                PC = WZ = 0x0038;
                return 13;
            default:
                // IM 0 executes the byte on the bus; SMS and SG-1000 hardware leaves $FF there (RST 38h).
                if ((InterruptData & 0xC7) == 0xC7)
                {
                    Push(PC);
                    PC = WZ = (ushort)(InterruptData & 0x38);
                    return 13;
                }

                return Execute(InterruptData) + 2;
        }
    }

    // ---------------------------------------------------------------- fetch helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void IncrementR() => R = (byte)((R & 0x80) | ((R + 1) & 0x7F));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte FetchOpcode()
    {
        IncrementR();
        return _bus.Read(PC++);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte FetchByte() => _bus.Read(PC++);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort FetchWord()
    {
        var low = _bus.Read(PC++);
        return (ushort)(low | _bus.Read(PC++) << 8);
    }

    private ushort ReadWord(ushort address) => (ushort)(_bus.Read(address) | _bus.Read((ushort)(address + 1)) << 8);

    private void WriteWord(ushort address, ushort value)
    {
        _bus.Write(address, (byte)value);
        _bus.Write((ushort)(address + 1), (byte)(value >> 8));
    }

    private void Push(ushort value)
    {
        _bus.Write(--SP, (byte)(value >> 8));
        _bus.Write(--SP, (byte)value);
    }

    private ushort Pop()
    {
        var low = _bus.Read(SP++);
        return (ushort)(low | _bus.Read(SP++) << 8);
    }

    // ---------------------------------------------------------------- registers by decode index

    /// <summary>HL, IX or IY, by the current prefix.</summary>
    private ushort Hx
    {
        get => _index switch { 0 => HL, 1 => IX, _ => IY };
        set
        {
            switch (_index)
            {
                case 0: HL = value; break;
                case 1: IX = value; break;
                default: IY = value; break;
            }
        }
    }

    /// <summary>Register r (0 B, 1 C, 2 D, 3 E, 4 H, 5 L, 7 A); with a prefix, 4 and 5 are the index register's halves.</summary>
    private byte GetReg(int r, bool indexHalves = true)
    {
        switch (r)
        {
            case 0: return B;
            case 1: return C;
            case 2: return D;
            case 3: return E;
            case 4: return indexHalves && _index != 0 ? (byte)(Hx >> 8) : H;
            case 5: return indexHalves && _index != 0 ? (byte)Hx : L;
            default: return A;
        }
    }

    private void SetReg(int r, byte value, bool indexHalves = true)
    {
        switch (r)
        {
            case 0: B = value; break;
            case 1: C = value; break;
            case 2: D = value; break;
            case 3: E = value; break;
            case 4:
                if (indexHalves && _index != 0)
                {
                    Hx = (ushort)((Hx & 0x00FF) | value << 8);
                }
                else
                {
                    H = value;
                }

                break;
            case 5:
                if (indexHalves && _index != 0)
                {
                    Hx = (ushort)((Hx & 0xFF00) | value);
                }
                else
                {
                    L = value;
                }

                break;
            default: A = value; break;
        }
    }

    /// <summary>The address (HL) stands for: HL, or IX+d / IY+d (the displacement is fetched here).</summary>
    private ushort MemoryOperandAddress()
    {
        if (_index == 0)
        {
            return HL;
        }

        var address = (ushort)(Hx + (sbyte)FetchByte());
        WZ = address;
        return address;
    }

    private ushort GetPair(int p) => p switch { 0 => BC, 1 => DE, 2 => Hx, _ => SP };

    private void SetPair(int p, ushort value)
    {
        switch (p)
        {
            case 0: BC = value; break;
            case 1: DE = value; break;
            case 2: Hx = value; break;
            default: SP = value; break;
        }
    }

    private bool Condition(int cc) => cc switch
    {
        0 => (F & FlagZ) == 0,
        1 => (F & FlagZ) != 0,
        2 => (F & FlagC) == 0,
        3 => (F & FlagC) != 0,
        4 => (F & FlagPV) == 0,
        5 => (F & FlagPV) != 0,
        6 => (F & FlagS) == 0,
        _ => (F & FlagS) != 0,
    };

    // ---------------------------------------------------------------- the main opcode table

    private int Execute(byte op)
    {
        // Prefixes: DD and FD pick the index register; a second DD/FD (or ED) makes the first a 4-cycle no-op.
        var extra = 0;
        while (op is 0xDD or 0xFD)
        {
            _index = op == 0xDD ? 1 : 2;
            extra += 4;
            op = FetchOpcode();
        }

        if (op == 0xED)
        {
            _index = 0;
            return extra + ExecuteEd(FetchOpcode());
        }

        if (op == 0xCB)
        {
            return extra + (_index == 0 ? ExecuteCb(FetchOpcode()) : ExecuteIndexedCb());
        }

        var x = op >> 6;
        var y = (op >> 3) & 7;
        var z = op & 7;
        var p = y >> 1;
        var q = y & 1;
        var indexed = _index != 0;

        switch (x)
        {
            case 0:
                switch (z)
                {
                    case 0:
                        switch (y)
                        {
                            case 0: return extra + 4; // NOP
                            case 1:
                            {
                                var af = AF;
                                AF = AltAF;
                                AltAF = af;
                                return extra + 4;
                            }

                            case 2:
                            {
                                var d = (sbyte)FetchByte();
                                B--;
                                if (B != 0)
                                {
                                    PC = WZ = (ushort)(PC + d);
                                    return extra + 13;
                                }

                                return extra + 8;
                            }

                            case 3:
                            {
                                var d = (sbyte)FetchByte();
                                PC = WZ = (ushort)(PC + d);
                                return extra + 12;
                            }

                            default:
                            {
                                var d = (sbyte)FetchByte();
                                if (Condition(y - 4))
                                {
                                    PC = WZ = (ushort)(PC + d);
                                    return extra + 12;
                                }

                                return extra + 7;
                            }
                        }

                    case 1:
                        if (q == 0)
                        {
                            SetPair(p, FetchWord());
                            return extra + 10;
                        }

                        Hx = Add16(Hx, GetPair(p));
                        return extra + 11;

                    case 2:
                        switch (y)
                        {
                            case 0:
                                _bus.Write(BC, A);
                                WZ = (ushort)(A << 8 | ((BC + 1) & 0xFF));
                                return extra + 7;
                            case 1:
                                A = _bus.Read(BC);
                                WZ = (ushort)(BC + 1);
                                return extra + 7;
                            case 2:
                                _bus.Write(DE, A);
                                WZ = (ushort)(A << 8 | ((DE + 1) & 0xFF));
                                return extra + 7;
                            case 3:
                                A = _bus.Read(DE);
                                WZ = (ushort)(DE + 1);
                                return extra + 7;
                            case 4:
                            {
                                var address = FetchWord();
                                WriteWord(address, Hx);
                                WZ = (ushort)(address + 1);
                                return extra + 16;
                            }

                            case 5:
                            {
                                var address = FetchWord();
                                Hx = ReadWord(address);
                                WZ = (ushort)(address + 1);
                                return extra + 16;
                            }

                            case 6:
                            {
                                var address = FetchWord();
                                _bus.Write(address, A);
                                WZ = (ushort)(A << 8 | ((address + 1) & 0xFF));
                                return extra + 13;
                            }

                            default:
                            {
                                var address = FetchWord();
                                A = _bus.Read(address);
                                WZ = (ushort)(address + 1);
                                return extra + 13;
                            }
                        }

                    case 3:
                        SetPair(p, (ushort)(GetPair(p) + (q == 0 ? 1 : -1)));
                        return extra + 6;

                    case 4:
                    case 5:
                        if (y == 6)
                        {
                            var address = MemoryOperandAddress();
                            var value = _bus.Read(address);
                            _bus.Write(address, z == 4 ? Inc8(value) : Dec8(value));
                            return extra + (indexed ? 19 : 11);
                        }

                        SetReg(y, z == 4 ? Inc8(GetReg(y)) : Dec8(GetReg(y)));
                        return extra + 4;

                    case 6:
                        if (y == 6)
                        {
                            var address = MemoryOperandAddress();
                            _bus.Write(address, FetchByte());
                            return extra + (indexed ? 15 : 10);
                        }

                        SetReg(y, FetchByte());
                        return extra + 7;

                    default:
                        AccumulatorOp(y);
                        return extra + 4;
                }

            case 1:
                if (y == 6 && z == 6)
                {
                    Halted = true;
                    return extra + 4;
                }

                if (z == 6)
                {
                    // LD r,(HL) / LD r,(IX+d): with an index, r is the plain H or L.
                    SetReg(y, _bus.Read(MemoryOperandAddress()), indexHalves: false);
                    return extra + (indexed ? 15 : 7);
                }

                if (y == 6)
                {
                    var address = MemoryOperandAddress();
                    _bus.Write(address, GetReg(z, indexHalves: false));
                    return extra + (indexed ? 15 : 7);
                }

                SetReg(y, GetReg(z));
                return extra + 4;

            case 2:
                if (z == 6)
                {
                    Alu(y, _bus.Read(MemoryOperandAddress()));
                    return extra + (indexed ? 15 : 7);
                }

                Alu(y, GetReg(z));
                return extra + 4;

            default:
                return extra + ExecuteX3(y, z, p, q);
        }
    }

    private int ExecuteX3(int y, int z, int p, int q)
    {
        switch (z)
        {
            case 0:
                if (Condition(y))
                {
                    PC = WZ = Pop();
                    return 11;
                }

                return 5;

            case 1:
                if (q == 0)
                {
                    var value = Pop();
                    if (p == 3)
                    {
                        AF = value;
                    }
                    else
                    {
                        SetPair(p, value);
                    }

                    return 10;
                }

                switch (p)
                {
                    case 0:
                        PC = WZ = Pop();
                        return 10;
                    case 1:
                    {
                        var bc = BC;
                        var de = DE;
                        var hl = HL;
                        BC = AltBC;
                        DE = AltDE;
                        HL = AltHL;
                        AltBC = bc;
                        AltDE = de;
                        AltHL = hl;
                        return 4;
                    }

                    case 2:
                        PC = Hx;
                        return 4;
                    default:
                        SP = Hx;
                        return 6;
                }

            case 2:
            {
                var address = FetchWord();
                WZ = address;
                if (Condition(y))
                {
                    PC = address;
                }

                return 10;
            }

            case 3:
                switch (y)
                {
                    case 0:
                        PC = WZ = FetchWord();
                        return 10;
                    case 2:
                    {
                        var n = FetchByte();
                        _bus.Out((ushort)(A << 8 | n), A);
                        WZ = (ushort)(A << 8 | ((n + 1) & 0xFF));
                        return 11;
                    }

                    case 3:
                    {
                        var port = (ushort)(A << 8 | FetchByte());
                        A = _bus.In(port);
                        WZ = (ushort)(port + 1);
                        return 11;
                    }

                    case 4:
                    {
                        var value = ReadWord(SP);
                        WriteWord(SP, Hx);
                        Hx = WZ = value;
                        return 19;
                    }

                    case 5:
                    {
                        // EX DE,HL ignores DD/FD.
                        var de = DE;
                        DE = HL;
                        HL = de;
                        return 4;
                    }

                    case 6:
                        Iff1 = Iff2 = false;
                        return 4;
                    default:
                        Iff1 = Iff2 = true;
                        _afterEi = true;
                        return 4;
                }

            case 4:
            {
                var address = FetchWord();
                WZ = address;
                if (Condition(y))
                {
                    Push(PC);
                    PC = address;
                    return 17;
                }

                return 10;
            }

            case 5:
                if (q == 0)
                {
                    Push(p == 3 ? AF : GetPair(p));
                    return 11;
                }

                // p == 0 is CALL nn; the prefixes were handled in Execute.
                {
                    var address = FetchWord();
                    WZ = address;
                    Push(PC);
                    PC = address;
                    return 17;
                }

            case 6:
                Alu(y, FetchByte());
                return 7;

            default:
                Push(PC);
                PC = WZ = (ushort)(y * 8);
                return 11;
        }
    }

    // ---------------------------------------------------------------- CB prefix

    private int ExecuteCb(byte op)
    {
        var x = op >> 6;
        var y = (op >> 3) & 7;
        var z = op & 7;
        if (z == 6)
        {
            var value = _bus.Read(HL);
            if (x == 1)
            {
                Bit(y, value, (byte)(WZ >> 8));
                return 12;
            }

            _bus.Write(HL, CbOp(x, y, value));
            return 15;
        }

        var r = GetReg(z, indexHalves: false);
        if (x == 1)
        {
            Bit(y, r, r);
            return 8;
        }

        SetReg(z, CbOp(x, y, r), indexHalves: false);
        return 8;
    }

    /// <summary>DD CB d op / FD CB d op: the operand is (IX+d); results also go to a register when z != 6.</summary>
    private int ExecuteIndexedCb()
    {
        var address = (ushort)(Hx + (sbyte)FetchByte());
        WZ = address;
        var op = FetchByte(); // not an M1 cycle: R doesn't count it
        var x = op >> 6;
        var y = (op >> 3) & 7;
        var z = op & 7;
        var value = _bus.Read(address);
        if (x == 1)
        {
            Bit(y, value, (byte)(address >> 8));
            return 16;
        }

        var result = CbOp(x, y, value);
        _bus.Write(address, result);
        if (z != 6)
        {
            SetReg(z, result, indexHalves: false);
        }

        return 19;
    }

    private byte CbOp(int x, int y, byte value) => x switch
    {
        0 => Rotate(y, value),
        2 => (byte)(value & ~(1 << y)),
        _ => (byte)(value | 1 << y),
    };

    private byte Rotate(int kind, byte value)
    {
        int result;
        int carry;
        switch (kind)
        {
            case 0: // RLC
                carry = value >> 7;
                result = (value << 1) | carry;
                break;
            case 1: // RRC
                carry = value & 1;
                result = (value >> 1) | (carry << 7);
                break;
            case 2: // RL
                carry = value >> 7;
                result = (value << 1) | (F & FlagC);
                break;
            case 3: // RR
                carry = value & 1;
                result = (value >> 1) | ((F & FlagC) << 7);
                break;
            case 4: // SLA
                carry = value >> 7;
                result = value << 1;
                break;
            case 5: // SRA
                carry = value & 1;
                result = (value >> 1) | (value & 0x80);
                break;
            case 6: // SLL (undocumented): shifts in a 1
                carry = value >> 7;
                result = (value << 1) | 1;
                break;
            default: // SRL
                carry = value & 1;
                result = value >> 1;
                break;
        }

        var b = (byte)result;
        F = (byte)(Szxyp[b] | carry);
        return b;
    }

    /// <summary>BIT n: X and Y come from <paramref name="xySource"/> (the value, MEMPTR's high byte, or the address's).</summary>
    private void Bit(int bit, byte value, byte xySource)
    {
        var set = value & (1 << bit);
        var flags = (F & FlagC) | FlagH | (xySource & (FlagX | FlagY));
        if (set == 0)
        {
            flags |= FlagZ | FlagPV;
        }
        else if (bit == 7)
        {
            flags |= FlagS;
        }

        F = (byte)flags;
    }

    // ---------------------------------------------------------------- ED prefix

    private int ExecuteEd(byte op)
    {
        var x = op >> 6;
        var y = (op >> 3) & 7;
        var z = op & 7;
        var p = y >> 1;
        var q = y & 1;

        if (x == 1)
        {
            switch (z)
            {
                case 0:
                {
                    var value = _bus.In(BC);
                    WZ = (ushort)(BC + 1);
                    F = (byte)((F & FlagC) | Szxyp[value]);
                    if (y != 6)
                    {
                        SetReg(y, value);
                    }

                    return 12;
                }

                case 1:
                    _bus.Out(BC, y == 6 ? (byte)0 : GetReg(y));
                    WZ = (ushort)(BC + 1);
                    return 12;

                case 2:
                    HL = q == 0 ? Sbc16(HL, GetPair(p)) : Adc16(HL, GetPair(p));
                    return 15;

                case 3:
                {
                    var address = FetchWord();
                    if (q == 0)
                    {
                        WriteWord(address, GetPair(p));
                    }
                    else
                    {
                        SetPair(p, ReadWord(address));
                    }

                    WZ = (ushort)(address + 1);
                    return 20;
                }

                case 4:
                {
                    var value = A;
                    A = 0;
                    Sub8(value, 0, store: true);
                    return 8;
                }

                case 5:
                    // RETN and RETI both copy IFF2 to IFF1.
                    Iff1 = Iff2;
                    PC = WZ = Pop();
                    return 14;

                case 6:
                    InterruptMode = (y & 3) switch { 0 or 1 => 0, 2 => 1, _ => 2 };
                    return 8;

                default:
                    switch (y)
                    {
                        case 0:
                            I = A;
                            return 9;
                        case 1:
                            R = A;
                            return 9;
                        case 2:
                            A = I;
                            F = (byte)((F & FlagC) | Szxy[A] | (Iff2 ? FlagPV : 0));
                            return 9;
                        case 3:
                            A = R;
                            F = (byte)((F & FlagC) | Szxy[A] | (Iff2 ? FlagPV : 0));
                            return 9;
                        case 4:
                        {
                            var value = _bus.Read(HL);
                            _bus.Write(HL, (byte)((A << 4) | (value >> 4)));
                            A = (byte)((A & 0xF0) | (value & 0x0F));
                            F = (byte)((F & FlagC) | Szxyp[A]);
                            WZ = (ushort)(HL + 1);
                            return 18;
                        }

                        case 5:
                        {
                            var value = _bus.Read(HL);
                            _bus.Write(HL, (byte)((value << 4) | (A & 0x0F)));
                            A = (byte)((A & 0xF0) | (value >> 4));
                            F = (byte)((F & FlagC) | Szxyp[A]);
                            WZ = (ushort)(HL + 1);
                            return 18;
                        }

                        default:
                            return 8;
                    }
            }
        }

        if (x == 2 && z <= 3 && y >= 4)
        {
            return BlockOp(y, z);
        }

        return 8; // undefined ED opcodes are 8-cycle no-ops
    }

    private int BlockOp(int y, int z)
    {
        var decrement = (y & 1) != 0;
        var repeat = y >= 6;
        var step = decrement ? -1 : 1;
        switch (z)
        {
            case 0: // LDI, LDD, LDIR, LDDR
            {
                var value = _bus.Read(HL);
                _bus.Write(DE, value);
                HL = (ushort)(HL + step);
                DE = (ushort)(DE + step);
                BC--;
                var n = value + A;
                var flags = (F & (FlagS | FlagZ | FlagC)) | (BC != 0 ? FlagPV : 0) | (n & FlagX) | ((n << 4) & FlagY);
                if (repeat && BC != 0)
                {
                    PC -= 2;
                    WZ = (ushort)(PC + 1);
                    F = (byte)((flags & ~(FlagX | FlagY)) | ((PC >> 8) & (FlagX | FlagY)));
                    return 21;
                }

                F = (byte)flags;
                return 16;
            }

            case 1: // CPI, CPD, CPIR, CPDR
            {
                var value = _bus.Read(HL);
                var result = (A - value) & 0xFF;
                var half = (A ^ value ^ result) & FlagH;
                HL = (ushort)(HL + step);
                BC--;
                WZ = (ushort)(WZ + step);
                var n = result - (half != 0 ? 1 : 0);
                var flags = (F & FlagC) | FlagN | half | (result & FlagS) | (result == 0 ? FlagZ : 0)
                    | (BC != 0 ? FlagPV : 0) | (n & FlagX) | ((n << 4) & FlagY);
                if (repeat && BC != 0 && result != 0)
                {
                    PC -= 2;
                    WZ = (ushort)(PC + 1);
                    F = (byte)((flags & ~(FlagX | FlagY)) | ((PC >> 8) & (FlagX | FlagY)));
                    return 21;
                }

                F = (byte)flags;
                return 16;
            }

            case 2: // INI, IND, INIR, INDR
            {
                var value = _bus.In(BC);
                WZ = (ushort)(BC + step);
                _bus.Write(HL, value);
                B--;
                HL = (ushort)(HL + step);
                var k = value + ((C + step) & 0xFF);
                return BlockIoFlags(value, k, repeat);
            }

            default: // OUTI, OUTD, OTIR, OTDR
            {
                var value = _bus.Read(HL);
                B--;
                WZ = (ushort)(BC + step);
                _bus.Out(BC, value);
                HL = (ushort)(HL + step);
                var k = value + L;
                return BlockIoFlags(value, k, repeat);
            }
        }
    }

    private int BlockIoFlags(byte value, int k, bool repeat)
    {
        var flags = Szxy[B] | ((value & 0x80) != 0 ? FlagN : 0) | (k > 0xFF ? FlagH | FlagC : 0)
            | (Szxyp[(byte)((k & 7) ^ B)] & FlagPV);
        if (repeat && B != 0)
        {
            PC -= 2;
            flags = (flags & ~(FlagX | FlagY | FlagH | FlagPV)) | ((PC >> 8) & (FlagX | FlagY));
            var parity = Szxyp[(byte)((k & 7) ^ B)] & FlagPV;
            if ((flags & FlagC) != 0)
            {
                var adjusted = (value & 0x80) != 0 ? (B - 1) & 7 : (B + 1) & 7;
                parity ^= (Szxyp[(byte)adjusted] & FlagPV) ^ FlagPV;
                if ((value & 0x80) != 0 ? (B & 0x0F) == 0x00 : (B & 0x0F) == 0x0F)
                {
                    flags |= FlagH;
                }
            }
            else
            {
                parity ^= (Szxyp[(byte)(B & 7)] & FlagPV) ^ FlagPV;
            }

            F = (byte)(flags | parity);
            return 21;
        }

        F = (byte)flags;
        return 16;
    }

    // ---------------------------------------------------------------- arithmetic

    private void Alu(int op, byte value)
    {
        switch (op)
        {
            case 0: Add8(value, 0); break;
            case 1: Add8(value, F & FlagC); break;
            case 2: Sub8(value, 0, store: true); break;
            case 3: Sub8(value, F & FlagC, store: true); break;
            case 4:
                A &= value;
                F = (byte)(Szxyp[A] | FlagH);
                break;
            case 5:
                A ^= value;
                F = Szxyp[A];
                break;
            case 6:
                A |= value;
                F = Szxyp[A];
                break;
            default:
                Sub8(value, 0, store: false);
                // CP takes X and Y from the operand, not the result.
                F = (byte)((F & ~(FlagX | FlagY)) | (value & (FlagX | FlagY)));
                break;
        }
    }

    private void Add8(byte value, int carry)
    {
        var result = A + value + carry;
        var b = (byte)result;
        F = (byte)(Szxy[b] | ((A ^ value ^ result) & FlagH) | (((A ^ ~value) & (A ^ result) & 0x80) >> 5) | (result >> 8 & FlagC));
        A = b;
    }

    private void Sub8(byte value, int carry, bool store)
    {
        var result = A - value - carry;
        var b = (byte)result;
        F = (byte)(Szxy[b] | FlagN | ((A ^ value ^ result) & FlagH) | (((A ^ value) & (A ^ result) & 0x80) >> 5) | ((result >> 8) & FlagC));
        if (store)
        {
            A = b;
        }
    }

    private byte Inc8(byte value)
    {
        var result = (byte)(value + 1);
        F = (byte)((F & FlagC) | Szxy[result] | (result == 0x80 ? FlagPV : 0) | ((result & 0x0F) == 0 ? FlagH : 0));
        return result;
    }

    private byte Dec8(byte value)
    {
        var result = (byte)(value - 1);
        F = (byte)((F & FlagC) | FlagN | Szxy[result] | (value == 0x80 ? FlagPV : 0) | ((value & 0x0F) == 0 ? FlagH : 0));
        return result;
    }

    private ushort Add16(ushort a, ushort b)
    {
        var result = a + b;
        WZ = (ushort)(a + 1);
        F = (byte)((F & (FlagS | FlagZ | FlagPV)) | ((result >> 8) & (FlagX | FlagY)) | (((a ^ b ^ result) >> 8) & FlagH) | (result >> 16));
        return (ushort)result;
    }

    private ushort Adc16(ushort a, ushort b)
    {
        var result = a + b + (F & FlagC);
        WZ = (ushort)(a + 1);
        F = (byte)(((result >> 8) & (FlagS | FlagX | FlagY)) | ((result & 0xFFFF) == 0 ? FlagZ : 0) | (((a ^ b ^ result) >> 8) & FlagH)
            | (((a ^ ~b) & (a ^ result) & 0x8000) >> 13) | (result >> 16 & FlagC));
        return (ushort)result;
    }

    private ushort Sbc16(ushort a, ushort b)
    {
        var result = a - b - (F & FlagC);
        WZ = (ushort)(a + 1);
        F = (byte)(((result >> 8) & (FlagS | FlagX | FlagY)) | ((result & 0xFFFF) == 0 ? FlagZ : 0) | FlagN | (((a ^ b ^ result) >> 8) & FlagH)
            | (((a ^ b) & (a ^ result) & 0x8000) >> 13) | ((result >> 16) & FlagC));
        return (ushort)result;
    }

    /// <summary>RLCA, RRCA, RLA, RRA, DAA, CPL, SCF, CCF.</summary>
    private void AccumulatorOp(int y)
    {
        var keep = F & (FlagS | FlagZ | FlagPV);
        switch (y)
        {
            case 0:
            {
                var carry = A >> 7;
                A = (byte)((A << 1) | carry);
                F = (byte)(keep | (A & (FlagX | FlagY)) | carry);
                break;
            }

            case 1:
            {
                var carry = A & 1;
                A = (byte)((A >> 1) | (carry << 7));
                F = (byte)(keep | (A & (FlagX | FlagY)) | carry);
                break;
            }

            case 2:
            {
                var carry = A >> 7;
                A = (byte)((A << 1) | (F & FlagC));
                F = (byte)(keep | (A & (FlagX | FlagY)) | carry);
                break;
            }

            case 3:
            {
                var carry = A & 1;
                A = (byte)((A >> 1) | ((F & FlagC) << 7));
                F = (byte)(keep | (A & (FlagX | FlagY)) | carry);
                break;
            }

            case 4:
                Daa();
                break;
            case 5:
                A = (byte)~A;
                F = (byte)((F & (FlagS | FlagZ | FlagPV | FlagC)) | FlagH | FlagN | (A & (FlagX | FlagY)));
                break;
            case 6:
                // SCF and CCF: X and Y are (Q ^ F) | A (Banks; Patrik Rak's z80test).
                F = (byte)(keep | FlagC | (((_q ^ F) | A) & (FlagX | FlagY)));
                break;
            default:
                F = (byte)((keep | ((F & FlagC) << 4) | (((_q ^ F) | A) & (FlagX | FlagY)) | (F & FlagC)) ^ FlagC);
                break;
        }
    }

    private void Daa()
    {
        var a = A;
        var correction = 0;
        var carry = F & FlagC;
        if ((F & FlagH) != 0 || (a & 0x0F) > 9)
        {
            correction = 0x06;
        }

        if (carry != 0 || a > 0x99)
        {
            correction |= 0x60;
            carry = FlagC;
        }

        var result = (F & FlagN) != 0 ? (byte)(a - correction) : (byte)(a + correction);
        F = (byte)(Szxyp[result] | (F & FlagN) | ((a ^ result) & FlagH) | carry);
        A = result;
    }

    private static byte[] BuildSzxy(bool parity)
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var flags = (i & (FlagS | FlagX | FlagY)) | (i == 0 ? FlagZ : 0);
            if (parity && System.Numerics.BitOperations.PopCount((uint)i) % 2 == 0)
            {
                flags |= FlagPV;
            }

            table[i] = (byte)flags;
        }

        return table;
    }

    // ---------------------------------------------------------------- state

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteUInt16(AF);
        writer.WriteUInt16(BC);
        writer.WriteUInt16(DE);
        writer.WriteUInt16(HL);
        writer.WriteUInt16(IX);
        writer.WriteUInt16(IY);
        writer.WriteUInt16(SP);
        writer.WriteUInt16(PC);
        writer.WriteUInt16(WZ);
        writer.WriteUInt16(AltAF);
        writer.WriteUInt16(AltBC);
        writer.WriteUInt16(AltDE);
        writer.WriteUInt16(AltHL);
        writer.WriteByte(I);
        writer.WriteByte(R);
        writer.WriteBool(Iff1);
        writer.WriteBool(Iff2);
        writer.WriteByte((byte)InterruptMode);
        writer.WriteBool(Halted);
        writer.WriteBool(IrqLine);
        writer.WriteBool(_afterEi);
        writer.WriteBool(_nmiPending);
        writer.WriteByte(_q);
        writer.WriteInt64(Cycles);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        AF = reader.ReadUInt16();
        BC = reader.ReadUInt16();
        DE = reader.ReadUInt16();
        HL = reader.ReadUInt16();
        IX = reader.ReadUInt16();
        IY = reader.ReadUInt16();
        SP = reader.ReadUInt16();
        PC = reader.ReadUInt16();
        WZ = reader.ReadUInt16();
        AltAF = reader.ReadUInt16();
        AltBC = reader.ReadUInt16();
        AltDE = reader.ReadUInt16();
        AltHL = reader.ReadUInt16();
        I = reader.ReadByte();
        R = reader.ReadByte();
        Iff1 = reader.ReadBool();
        Iff2 = reader.ReadBool();
        InterruptMode = reader.ReadByte();
        Halted = reader.ReadBool();
        IrqLine = reader.ReadBool();
        _afterEi = reader.ReadBool();
        _nmiPending = reader.ReadBool();
        _q = reader.ReadByte();
        Cycles = reader.ReadInt64();
        reader.EndChunk();
    }
}
