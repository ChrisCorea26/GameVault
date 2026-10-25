using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.Cpu;

/// <summary>
/// Ricoh 2A03 CPU core: a 6502 without decimal mode.
/// </summary>
/// <remarks>
/// Every bus access is exactly one CPU cycle, including the dummy reads and writes the real chip makes,
/// so instruction-at-a-time execution stays cycle-exact (NESdev Wiki: "CPU", "6502 cycle times").
/// Interrupt lines are sampled at the end of every cycle. The CPU acts on the state sampled at the end
/// of an instruction's second-to-last cycle (NESdev Wiki: "CPU interrupts").
/// </remarks>
public sealed class Cpu6502 : IStateful
{
    private const ushort NmiVector = 0xFFFA;
    private const ushort ResetVector = 0xFFFC;
    private const ushort IrqVector = 0xFFFE;
    private const byte FlagC = 0x01, FlagZ = 0x02, FlagI = 0x04, FlagD = 0x08, FlagB = 0x10, FlagU = 0x20, FlagV = 0x40, FlagN = 0x80;

    private static readonly StateTag Tag = StateTag.From("CPU ");

    private readonly ICpuBus _bus;

    // Interrupt sampling (see remarks).
    private bool _prevNmiLine;
    private bool _needNmi;
    private bool _prevNeedNmi;
    private bool _runIrq;
    private bool _prevRunIrq;
    private IrqSource _irqLines;

    // OAM DMA ($4014): the CPU halts on its next read cycle.
    private bool _oamDmaPending;
    private byte _oamDmaPage;

    public Cpu6502(ICpuBus bus)
    {
        _bus = bus;
    }

    public byte A { get; set; }

    public byte X { get; set; }

    public byte Y { get; set; }

    public byte SP { get; set; }

    public ushort PC { get; set; }

    public bool Carry { get; set; }

    public bool Zero { get; set; }

    public bool InterruptDisable { get; set; }

    public bool Decimal { get; set; }

    public bool Overflow { get; set; }

    public bool Negative { get; set; }

    /// <summary>Total CPU cycles since power-on.</summary>
    public long CycleCount { get; private set; }

    /// <summary>True after a JAM opcode. The CPU stops executing until reset.</summary>
    public bool IsJammed { get; private set; }

    /// <summary>Level of the /NMI input (true = asserted). The CPU reacts to its rising edge.</summary>
    public bool NmiLine { get; set; }

    public IrqSource IrqLines => _irqLines;

    /// <summary>The status register as pushed by PHP minus the B flag: bit 5 always reads 1.</summary>
    public byte Status
    {
        get => (byte)((Carry ? FlagC : 0) | (Zero ? FlagZ : 0) | (InterruptDisable ? FlagI : 0) | (Decimal ? FlagD : 0)
            | FlagU | (Overflow ? FlagV : 0) | (Negative ? FlagN : 0));
        set
        {
            Carry = (value & FlagC) != 0;
            Zero = (value & FlagZ) != 0;
            InterruptDisable = (value & FlagI) != 0;
            Decimal = (value & FlagD) != 0;
            Overflow = (value & FlagV) != 0;
            Negative = (value & FlagN) != 0;
        }
    }

    public void SetIrq(IrqSource source) => _irqLines |= source;

    public void ClearIrq(IrqSource source) => _irqLines &= ~source;

    /// <summary>Starts a 256-byte OAM DMA from <c>$XX00</c>. Called by the bus on a write to $4014.</summary>
    public void RequestOamDma(byte page)
    {
        _oamDmaPage = page;
        _oamDmaPending = true;
    }

    /// <summary>Cold boot: clears registers, then runs the reset sequence (SP ends at $FD).</summary>
    public void Power()
    {
        A = X = Y = 0;
        SP = 0;
        Status = 0;
        CycleCount = 0;
        _irqLines = IrqSource.None;
        NmiLine = false;
        Reset();
    }

    /// <summary>
    /// The 7-cycle reset sequence: like an interrupt, but the three stack pushes become reads
    /// (NESdev Wiki: "CPU power up state").
    /// </summary>
    public void Reset()
    {
        IsJammed = false;
        _oamDmaPending = false;
        _needNmi = _prevNeedNmi = _runIrq = _prevRunIrq = false;
        _prevNmiLine = NmiLine;

        Read(PC);
        Read(PC);
        Read((ushort)(0x100 | SP--));
        Read((ushort)(0x100 | SP--));
        Read((ushort)(0x100 | SP--));
        InterruptDisable = true;
        PC = ReadWord(ResetVector);
    }

    /// <summary>Executes one instruction, then services a pending interrupt if one was sampled.</summary>
    public void Step()
    {
        if (IsJammed)
        {
            Read(0xFFFF);
            return;
        }

        var opcode = Fetch();
        Execute(opcode);

        if (_prevRunIrq || _prevNeedNmi)
        {
            Interrupt();
        }
    }

    // ---------------------------------------------------------------- cycles

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Read(ushort address)
    {
        if (_oamDmaPending)
        {
            RunOamDma(address);
        }

        CycleCount++;
        var value = _bus.Read(address);
        EndCycle();
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Write(ushort address, byte value)
    {
        CycleCount++;
        _bus.Write(address, value);
        EndCycle();
    }

    /// <summary>Samples the interrupt inputs at the end of a cycle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EndCycle()
    {
        _prevNeedNmi = _needNmi;
        if (!_prevNmiLine && NmiLine)
        {
            _needNmi = true;
        }

        _prevNmiLine = NmiLine;
        _prevRunIrq = _runIrq;
        _runIrq = _irqLines != IrqSource.None && !InterruptDisable;
    }

    /// <summary>
    /// OAM DMA: one halt cycle, one alignment cycle if needed so reads land on "get" cycles, then
    /// 256 read/write pairs to $2004 (513 or 514 cycles; NESdev Wiki: "DMA").
    /// </summary>
    private void RunOamDma(ushort haltAddress)
    {
        _oamDmaPending = false;

        CycleCount++;
        _bus.Read(haltAddress);
        EndCycle();

        if ((CycleCount & 1) == 1)
        {
            CycleCount++;
            _bus.Read(haltAddress);
            EndCycle();
        }

        var baseAddress = _oamDmaPage << 8;
        for (var i = 0; i < 256; i++)
        {
            CycleCount++;
            var value = _bus.Read((ushort)(baseAddress | i));
            EndCycle();

            CycleCount++;
            _bus.Write(0x2004, value);
            EndCycle();
        }
    }

    // ---------------------------------------------------------------- helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Fetch() => Read(PC++);

    private ushort FetchWord()
    {
        var lo = Fetch();
        var hi = Fetch();
        return (ushort)(lo | (hi << 8));
    }

    private ushort ReadWord(ushort address)
    {
        var lo = Read(address);
        var hi = Read((ushort)(address + 1));
        return (ushort)(lo | (hi << 8));
    }

    private void Push(byte value) => Write((ushort)(0x100 | SP--), value);

    private byte Pull() => Read((ushort)(0x100 | ++SP));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetZn(byte value)
    {
        Zero = value == 0;
        Negative = (value & 0x80) != 0;
    }

    private static bool PageCrossed(ushort a, ushort b) => ((a ^ b) & 0xFF00) != 0;

    // ---------------------------------------------------------------- addressing

    /// <summary>
    /// Resolves an operand address with the mode's exact cycle pattern. Read instructions only take the
    /// indexed page-cross penalty when the page changes; stores and read-modify-write instructions always
    /// spend the fix-up cycle (<paramref name="alwaysFixup"/>).
    /// </summary>
    private ushort Address(AddressingMode mode, bool alwaysFixup)
    {
        switch (mode)
        {
            case AddressingMode.ZeroPage:
                return Fetch();

            case AddressingMode.ZeroPageX:
            {
                var zp = Fetch();
                Read(zp);
                return (byte)(zp + X);
            }

            case AddressingMode.ZeroPageY:
            {
                var zp = Fetch();
                Read(zp);
                return (byte)(zp + Y);
            }

            case AddressingMode.Absolute:
                return FetchWord();

            case AddressingMode.AbsoluteX:
                return Indexed(FetchWord(), X, alwaysFixup);

            case AddressingMode.AbsoluteY:
                return Indexed(FetchWord(), Y, alwaysFixup);

            case AddressingMode.IndirectX:
            {
                var zp = Fetch();
                Read(zp);
                zp += X;
                var lo = Read(zp);
                var hi = Read((byte)(zp + 1));
                return (ushort)(lo | (hi << 8));
            }

            case AddressingMode.IndirectY:
            {
                var zp = Fetch();
                var lo = Read(zp);
                var hi = Read((byte)(zp + 1));
                return Indexed((ushort)(lo | (hi << 8)), Y, alwaysFixup);
            }

            default:
                throw new InvalidOperationException($"Addressing mode {mode} has no operand address.");
        }
    }

    private ushort Indexed(ushort baseAddress, byte index, bool alwaysFixup)
    {
        var address = (ushort)(baseAddress + index);
        if (alwaysFixup || PageCrossed(baseAddress, address))
        {
            // Dummy read from the address before the high byte is fixed.
            Read((ushort)((baseAddress & 0xFF00) | (address & 0x00FF)));
        }

        return address;
    }

    // ---------------------------------------------------------------- execution

    private void Execute(byte opcode)
    {
        var instruction = OpcodeTable.GetInstruction(opcode);
        var mode = OpcodeTable.GetMode(opcode);

        switch (instruction)
        {
            // Read instructions
            case Instruction.ADC or Instruction.AND or Instruction.BIT or Instruction.CMP or Instruction.CPX
                or Instruction.CPY or Instruction.EOR or Instruction.LDA or Instruction.LDX or Instruction.LDY
                or Instruction.ORA or Instruction.SBC or Instruction.LAX or Instruction.ANC or Instruction.ALR
                or Instruction.ARR or Instruction.AXS or Instruction.LXA or Instruction.ANE or Instruction.LAS:
                ExecuteRead(instruction, mode == AddressingMode.Immediate ? Fetch() : Read(Address(mode, false)));
                break;

            case Instruction.NOP:
                if (mode == AddressingMode.Implied)
                {
                    Read(PC);
                }
                else if (mode == AddressingMode.Immediate)
                {
                    Fetch();
                }
                else
                {
                    Read(Address(mode, false));
                }

                break;

            // Stores
            case Instruction.STA:
                Write(Address(mode, true), A);
                break;
            case Instruction.STX:
                Write(Address(mode, true), X);
                break;
            case Instruction.STY:
                Write(Address(mode, true), Y);
                break;
            case Instruction.SAX:
                Write(Address(mode, true), (byte)(A & X));
                break;

            // Read-modify-write
            case Instruction.ASL or Instruction.LSR or Instruction.ROL or Instruction.ROR or Instruction.INC
                or Instruction.DEC or Instruction.SLO or Instruction.RLA or Instruction.SRE or Instruction.RRA
                or Instruction.DCP or Instruction.ISB:
                if (mode == AddressingMode.Accumulator)
                {
                    Read(PC);
                    A = Modify(instruction, A);
                }
                else
                {
                    var address = Address(mode, true);
                    var value = Read(address);
                    Write(address, value); // the 6502 writes the unmodified value back first
                    Write(address, Modify(instruction, value));
                }

                break;

            // Branches
            case Instruction.BPL: Branch(!Negative); break;
            case Instruction.BMI: Branch(Negative); break;
            case Instruction.BVC: Branch(!Overflow); break;
            case Instruction.BVS: Branch(Overflow); break;
            case Instruction.BCC: Branch(!Carry); break;
            case Instruction.BCS: Branch(Carry); break;
            case Instruction.BNE: Branch(!Zero); break;
            case Instruction.BEQ: Branch(Zero); break;

            // Flags and transfers (2 cycles: the second is a dummy read of the next byte)
            case Instruction.CLC: Read(PC); Carry = false; break;
            case Instruction.SEC: Read(PC); Carry = true; break;
            case Instruction.CLI: Read(PC); InterruptDisable = false; break;
            case Instruction.SEI: Read(PC); InterruptDisable = true; break;
            case Instruction.CLV: Read(PC); Overflow = false; break;
            case Instruction.CLD: Read(PC); Decimal = false; break;
            case Instruction.SED: Read(PC); Decimal = true; break;
            case Instruction.TAX: Read(PC); X = A; SetZn(X); break;
            case Instruction.TAY: Read(PC); Y = A; SetZn(Y); break;
            case Instruction.TXA: Read(PC); A = X; SetZn(A); break;
            case Instruction.TYA: Read(PC); A = Y; SetZn(A); break;
            case Instruction.TSX: Read(PC); X = SP; SetZn(X); break;
            case Instruction.TXS: Read(PC); SP = X; break;
            case Instruction.INX: Read(PC); X++; SetZn(X); break;
            case Instruction.INY: Read(PC); Y++; SetZn(Y); break;
            case Instruction.DEX: Read(PC); X--; SetZn(X); break;
            case Instruction.DEY: Read(PC); Y--; SetZn(Y); break;

            // Stack
            case Instruction.PHA:
                Read(PC);
                Push(A);
                break;
            case Instruction.PHP:
                Read(PC);
                Push((byte)(Status | FlagB));
                break;
            case Instruction.PLA:
                Read(PC);
                Read((ushort)(0x100 | SP));
                A = Pull();
                SetZn(A);
                break;
            case Instruction.PLP:
                Read(PC);
                Read((ushort)(0x100 | SP));
                Status = Pull();
                break;

            // Jumps and subroutines
            case Instruction.JMP:
                if (mode == AddressingMode.Absolute)
                {
                    PC = FetchWord();
                }
                else
                {
                    // The pointer's high byte is read without carrying into the next page.
                    var pointer = FetchWord();
                    var lo = Read(pointer);
                    var hi = Read((ushort)((pointer & 0xFF00) | ((pointer + 1) & 0x00FF)));
                    PC = (ushort)(lo | (hi << 8));
                }

                break;

            case Instruction.JSR:
            {
                var lo = Fetch();
                Read((ushort)(0x100 | SP));
                Push((byte)(PC >> 8));
                Push((byte)PC);
                var hi = Read(PC);
                PC = (ushort)(lo | (hi << 8));
                break;
            }

            case Instruction.RTS:
            {
                Read(PC);
                Read((ushort)(0x100 | SP));
                var lo = Pull();
                var hi = Pull();
                PC = (ushort)(lo | (hi << 8));
                Read(PC);
                PC++;
                break;
            }

            case Instruction.RTI:
            {
                Read(PC);
                Read((ushort)(0x100 | SP));
                Status = Pull();
                var lo = Pull();
                var hi = Pull();
                PC = (ushort)(lo | (hi << 8));
                break;
            }

            case Instruction.BRK:
                Fetch(); // padding byte
                Push((byte)(PC >> 8));
                Push((byte)PC);
                // An NMI that arrives during BRK hijacks its vector but keeps the B flag.
                var vector = _needNmi ? NmiVector : IrqVector;
                _needNmi = false;
                Push((byte)(Status | FlagB));
                InterruptDisable = true;
                PC = ReadWord(vector);
                _prevNeedNmi = false; // no interrupt immediately after BRK's vector fetch
                break;

            // Unstable "high byte" stores (NESdev Wiki: "CPU unofficial opcodes")
            case Instruction.SHY:
                UnstableStore(FetchWord(), X, Y);
                break;
            case Instruction.SHX:
                UnstableStore(FetchWord(), Y, X);
                break;
            case Instruction.SHA:
                if (mode == AddressingMode.AbsoluteY)
                {
                    UnstableStore(FetchWord(), Y, (byte)(A & X));
                }
                else
                {
                    var zp = Fetch();
                    var lo = Read(zp);
                    var hi = Read((byte)(zp + 1));
                    UnstableStore((ushort)(lo | (hi << 8)), Y, (byte)(A & X));
                }

                break;
            case Instruction.TAS:
                SP = (byte)(A & X);
                UnstableStore(FetchWord(), Y, SP);
                break;

            case Instruction.JAM:
                IsJammed = true;
                Read(PC);
                break;

            default:
                throw new InvalidOperationException($"Unhandled instruction {instruction}.");
        }
    }

    private void ExecuteRead(Instruction instruction, byte value)
    {
        switch (instruction)
        {
            case Instruction.LDA: A = value; SetZn(A); break;
            case Instruction.LDX: X = value; SetZn(X); break;
            case Instruction.LDY: Y = value; SetZn(Y); break;
            case Instruction.AND: A &= value; SetZn(A); break;
            case Instruction.ORA: A |= value; SetZn(A); break;
            case Instruction.EOR: A ^= value; SetZn(A); break;
            case Instruction.ADC: AddWithCarry(value); break;
            case Instruction.SBC: AddWithCarry((byte)~value); break;
            case Instruction.CMP: Compare(A, value); break;
            case Instruction.CPX: Compare(X, value); break;
            case Instruction.CPY: Compare(Y, value); break;
            case Instruction.BIT:
                Zero = (A & value) == 0;
                Overflow = (value & 0x40) != 0;
                Negative = (value & 0x80) != 0;
                break;
            case Instruction.LAX:
                A = X = value;
                SetZn(A);
                break;
            case Instruction.ANC:
                A &= value;
                SetZn(A);
                Carry = Negative;
                break;
            case Instruction.ALR:
                A &= value;
                Carry = (A & 1) != 0;
                A >>= 1;
                SetZn(A);
                break;
            case Instruction.ARR:
                A = (byte)(((A & value) >> 1) | (Carry ? 0x80 : 0));
                SetZn(A);
                Carry = (A & 0x40) != 0;
                Overflow = (((A >> 6) ^ (A >> 5)) & 1) != 0;
                break;
            case Instruction.AXS:
            {
                var ax = A & X;
                Carry = ax >= value;
                X = (byte)(ax - value);
                SetZn(X);
                break;
            }

            // Unstable on real hardware. These use the common "magic constant = $FF" behaviour.
            case Instruction.LXA:
                A = X = value;
                SetZn(A);
                break;
            case Instruction.ANE:
                A = (byte)(X & value);
                SetZn(A);
                break;
            case Instruction.LAS:
                A = X = SP = (byte)(value & SP);
                SetZn(A);
                break;
        }
    }

    private byte Modify(Instruction instruction, byte value)
    {
        switch (instruction)
        {
            case Instruction.ASL: return ShiftLeft(value);
            case Instruction.LSR: return ShiftRight(value);
            case Instruction.ROL: return RotateLeft(value);
            case Instruction.ROR: return RotateRight(value);
            case Instruction.INC: value++; SetZn(value); return value;
            case Instruction.DEC: value--; SetZn(value); return value;
            case Instruction.SLO: value = ShiftLeft(value); A |= value; SetZn(A); return value;
            case Instruction.RLA: value = RotateLeft(value); A &= value; SetZn(A); return value;
            case Instruction.SRE: value = ShiftRight(value); A ^= value; SetZn(A); return value;
            case Instruction.RRA: value = RotateRight(value); AddWithCarry(value); return value;
            case Instruction.DCP: value--; Compare(A, value); return value;
            case Instruction.ISB: value++; AddWithCarry((byte)~value); return value;
            default: throw new InvalidOperationException($"{instruction} is not a read-modify-write instruction.");
        }
    }

    private void AddWithCarry(byte value)
    {
        var sum = A + value + (Carry ? 1 : 0);
        Overflow = (~(A ^ value) & (A ^ sum) & 0x80) != 0;
        Carry = sum > 0xFF;
        A = (byte)sum;
        SetZn(A);
    }

    private void Compare(byte register, byte value)
    {
        Carry = register >= value;
        SetZn((byte)(register - value));
    }

    private byte ShiftLeft(byte value)
    {
        Carry = (value & 0x80) != 0;
        value <<= 1;
        SetZn(value);
        return value;
    }

    private byte ShiftRight(byte value)
    {
        Carry = (value & 0x01) != 0;
        value >>= 1;
        SetZn(value);
        return value;
    }

    private byte RotateLeft(byte value)
    {
        var carryIn = Carry ? 1 : 0;
        Carry = (value & 0x80) != 0;
        value = (byte)((value << 1) | carryIn);
        SetZn(value);
        return value;
    }

    private byte RotateRight(byte value)
    {
        var carryIn = Carry ? 0x80 : 0;
        Carry = (value & 0x01) != 0;
        value = (byte)((value >> 1) | carryIn);
        SetZn(value);
        return value;
    }

    private void Branch(bool taken)
    {
        var offset = (sbyte)Fetch();
        if (!taken)
        {
            return;
        }

        // A taken branch that doesn't cross a page doesn't poll interrupts on its last cycle, so an
        // IRQ that arrives during the operand fetch waits one more instruction (NESdev Wiki: "CPU interrupts").
        if (_runIrq && !_prevRunIrq)
        {
            _runIrq = false;
        }

        Read(PC);
        var target = (ushort)(PC + offset);
        if (PageCrossed(PC, target))
        {
            Read((ushort)((PC & 0xFF00) | (target & 0x00FF)));
        }

        PC = target;
    }

    /// <summary>
    /// SHA/SHX/SHY/TAS: store <paramref name="value"/> ANDed with (high byte of base + 1). On a page
    /// cross, the stored value also replaces the high byte of the target address.
    /// </summary>
    private void UnstableStore(ushort baseAddress, byte index, byte value)
    {
        var address = (ushort)(baseAddress + index);
        Read((ushort)((baseAddress & 0xFF00) | (address & 0x00FF)));
        var result = (byte)(value & ((baseAddress >> 8) + 1));
        if (PageCrossed(baseAddress, address))
        {
            address = (ushort)((result << 8) | (address & 0x00FF));
        }

        Write(address, result);
    }

    /// <summary>NMI or IRQ entry: 7 cycles. An NMI that arrives during an IRQ's pushes hijacks it.</summary>
    private void Interrupt()
    {
        Read(PC);
        Read(PC);
        Push((byte)(PC >> 8));
        Push((byte)PC);

        var vector = _needNmi ? NmiVector : IrqVector;
        _needNmi = false;
        Push(Status);
        InterruptDisable = true;
        PC = ReadWord(vector);
    }

    // ---------------------------------------------------------------- state

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteByte(A);
        writer.WriteByte(X);
        writer.WriteByte(Y);
        writer.WriteByte(SP);
        writer.WriteUInt16(PC);
        writer.WriteByte(Status);
        writer.WriteInt64(CycleCount);
        writer.WriteBool(IsJammed);
        writer.WriteBool(NmiLine);
        writer.WriteBool(_prevNmiLine);
        writer.WriteBool(_needNmi);
        writer.WriteBool(_prevNeedNmi);
        writer.WriteBool(_runIrq);
        writer.WriteBool(_prevRunIrq);
        writer.WriteByte((byte)_irqLines);
        writer.WriteBool(_oamDmaPending);
        writer.WriteByte(_oamDmaPage);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        A = reader.ReadByte();
        X = reader.ReadByte();
        Y = reader.ReadByte();
        SP = reader.ReadByte();
        PC = reader.ReadUInt16();
        Status = reader.ReadByte();
        CycleCount = reader.ReadInt64();
        IsJammed = reader.ReadBool();
        NmiLine = reader.ReadBool();
        _prevNmiLine = reader.ReadBool();
        _needNmi = reader.ReadBool();
        _prevNeedNmi = reader.ReadBool();
        _runIrq = reader.ReadBool();
        _prevRunIrq = reader.ReadBool();
        _irqLines = (IrqSource)reader.ReadByte();
        _oamDmaPending = reader.ReadBool();
        _oamDmaPage = reader.ReadByte();
        reader.EndChunk();
    }
}
