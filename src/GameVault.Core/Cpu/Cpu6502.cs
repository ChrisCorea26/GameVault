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
/// <summary>Which 6502 a console has.</summary>
public enum CpuVariant
{
    /// <summary>The NES's Ricoh 2A03: an NMOS 6502 whose decimal mode is disconnected.</summary>
    Ricoh2A03,

    /// <summary>An NMOS 6502 with decimal mode (Apple II, II+, unenhanced IIe).</summary>
    Nmos6502,

    /// <summary>A WDC 65C02 (enhanced Apple IIe): new instructions, fixed JMP (abs), valid decimal flags.</summary>
    Cmos65C02,
}

public sealed class Cpu6502 : IStateful
{
    private const ushort NmiVector = 0xFFFA;
    private const ushort ResetVector = 0xFFFC;
    private const ushort IrqVector = 0xFFFE;
    private const byte FlagC = 0x01, FlagZ = 0x02, FlagI = 0x04, FlagD = 0x08, FlagB = 0x10, FlagU = 0x20, FlagV = 0x40, FlagN = 0x80;

    private static readonly StateTag Tag = StateTag.From("CPU ");

    private readonly ICpuBus _bus;

    // The bit RMB/SMB act on, from the opcode.
    private int _bitIndex;

    // Interrupt sampling (see remarks).
    private bool _prevNmiLine;
    private bool _needNmi;
    private bool _prevNeedNmi;
    private bool _runIrq;
    private bool _prevRunIrq;
    private IrqSource _irqLines;

    // DMA (NESdev Wiki: "DMA"): OAM DMA ($4014) and DMC sample fetches halt the CPU on its next read cycle.
    private bool _needHalt;
    private bool _needDummyRead;
    private bool _oamDmaPending;
    private byte _oamDmaPage;
    private bool _dmcDmaPending;

    public Cpu6502(ICpuBus bus, CpuVariant variant = CpuVariant.Ricoh2A03)
    {
        _bus = bus;
        Variant = variant;
    }

    public CpuVariant Variant { get; }

    private bool IsCmos => Variant == CpuVariant.Cmos65C02;

    /// <summary>True while a 65C02 waits in WAI for an interrupt.</summary>
    public bool IsWaiting { get; private set; }

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

    /// <summary>
    /// An NMI requested directly, as an edge on <see cref="NmiLine"/> would (the Atari 5200's ANTIC, whose requests can
    /// land while the CPU is halted for DMA and so between the cycles that sample the line).
    /// </summary>
    public void TriggerNmi() => _needNmi = true;

    public void ClearIrq(IrqSource source) => _irqLines &= ~source;

    /// <summary>Starts a 256-byte OAM DMA from <c>$XX00</c>. Called by the bus on a write to $4014.</summary>
    public void RequestOamDma(byte page)
    {
        _oamDmaPage = page;
        _oamDmaPending = true;
        _needHalt = true;
    }

    /// <summary>The DMC memory reader served by <see cref="RequestDmcDma"/> (the APU).</summary>
    public IDmcDmaSource? DmcSource { get; set; }

    /// <summary>Fetches one DMC sample byte by DMA. Called by the bus when the DMC buffer empties.</summary>
    public void RequestDmcDma()
    {
        if (DmcSource is null)
        {
            return;
        }

        _dmcDmaPending = true;
        _needHalt = true;
        _needDummyRead = true;
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
        IsWaiting = false;
        _oamDmaPending = _dmcDmaPending = _needHalt = _needDummyRead = false;
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

        if (IsWaiting)
        {
            // WAI: idle until an interrupt is requested (taken only if I is clear, otherwise execution resumes).
            Read(PC);
            if (_irqLines == IrqSource.None && !_needNmi)
            {
                return;
            }

            IsWaiting = false;
            if (_prevRunIrq || _prevNeedNmi)
            {
                Interrupt();
            }

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
        if (_needHalt)
        {
            RunDma(address);
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
    /// Runs pending DMA before a read cycle (NESdev Wiki: "DMA"). The CPU halts for one cycle, then DMA
    /// alternates "get" (read) and "put" (write) cycles. OAM DMA reads on get cycles and writes $2004 on put
    /// cycles (513 or 514 cycles in total). A DMC fetch needs a halt and a dummy cycle, then reads on a get
    /// cycle; when both are active, OAM cycles stand in for the DMC's halt and dummy cycles.
    /// </summary>
    private void RunDma(ushort haltAddress)
    {
        // Halt cycle: the CPU's pending read happens but is ignored.
        DmaRead(haltAddress);
        _needHalt = false;

        // Reading the controller ports twice would shift out extra bits, so DMA skips those dummy reads.
        var skipDummyReads = haltAddress is 0x4016 or 0x4017;
        var oamCount = 0;
        byte oamIndex = 0;
        byte value = 0;

        while (_dmcDmaPending || _oamDmaPending)
        {
            var getCycle = (CycleCount & 1) == 0;
            if (getCycle)
            {
                if (_dmcDmaPending && !_needHalt && !_needDummyRead)
                {
                    StandInForDmcCycle();
                    var source = DmcSource!;
                    source.OnDmcDmaRead(DmaRead(source.DmcDmaAddress));
                    _dmcDmaPending = false;
                }
                else if (_oamDmaPending)
                {
                    StandInForDmcCycle();
                    value = DmaRead((ushort)((_oamDmaPage << 8) | oamIndex++));
                    oamCount++;
                }
                else
                {
                    StandInForDmcCycle();
                    DummyDmaCycle(haltAddress, skipDummyReads);
                }
            }
            else if (_oamDmaPending && (oamCount & 1) == 1)
            {
                StandInForDmcCycle();
                DmaWrite(0x2004, value);
                if (++oamCount == 512)
                {
                    _oamDmaPending = false;
                }
            }
            else
            {
                // Alignment cycle (or a DMC halt/dummy cycle while waiting for a get cycle).
                StandInForDmcCycle();
                DummyDmaCycle(haltAddress, skipDummyReads);
            }
        }
    }

    /// <summary>Any DMA cycle counts toward a pending DMC fetch's halt, then dummy, cycle.</summary>
    private void StandInForDmcCycle()
    {
        if (_needHalt)
        {
            _needHalt = false;
        }
        else if (_needDummyRead)
        {
            _needDummyRead = false;
        }
    }

    private byte DmaRead(ushort address)
    {
        CycleCount++;
        var value = _bus.Read(address);
        EndCycle();
        return value;
    }

    private void DmaWrite(ushort address, byte value)
    {
        CycleCount++;
        _bus.Write(address, value);
        EndCycle();
    }

    private void DummyDmaCycle(ushort address, bool skipRead)
    {
        if (skipRead)
        {
            // The cycle still passes; only the read side effect is skipped.
            CycleCount++;
            _bus.Idle();
            EndCycle();
        }
        else
        {
            DmaRead(address);
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

            case AddressingMode.ZeroPageIndirect:
            {
                var zp = Fetch();
                var lo = Read(zp);
                var hi = Read((byte)(zp + 1));
                return (ushort)(lo | (hi << 8));
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
        var instruction = IsCmos ? OpcodeTable65C02.GetInstruction(opcode) : OpcodeTable.GetInstruction(opcode);
        var mode = IsCmos ? OpcodeTable65C02.GetMode(opcode) : OpcodeTable.GetMode(opcode);

        switch (instruction)
        {
            // Read instructions
            case Instruction.ADC or Instruction.AND or Instruction.BIT or Instruction.CMP or Instruction.CPX
                or Instruction.CPY or Instruction.EOR or Instruction.LDA or Instruction.LDX or Instruction.LDY
                or Instruction.ORA or Instruction.SBC or Instruction.LAX or Instruction.ANC or Instruction.ALR
                or Instruction.ARR or Instruction.AXS or Instruction.LXA or Instruction.ANE or Instruction.LAS:
                if (instruction == Instruction.BIT && mode == AddressingMode.Immediate)
                {
                    Zero = (A & Fetch()) == 0; // the 65C02's BIT #imm only sets Z
                    break;
                }

                ExecuteRead(instruction, mode == AddressingMode.Immediate ? Fetch() : Read(Address(mode, false)));
                if (IsCmos && Decimal && instruction is Instruction.ADC or Instruction.SBC)
                {
                    Read(PC); // the 65C02 takes a cycle to correct decimal results
                }

                break;

            case Instruction.NOP:
                if (mode == AddressingMode.SingleCycle)
                {
                    // Unused 65C02 opcode: one byte, one cycle.
                }
                else if (mode == AddressingMode.AbsoluteIndexedIndirect)
                {
                    // 65C02 $5C: three bytes, eight cycles.
                    FetchWord();
                    for (var i = 0; i < 5; i++)
                    {
                        Read(PC);
                    }
                }
                else if (mode == AddressingMode.Implied)
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
            case Instruction.STZ:
                Write(Address(mode, true), 0);
                break;

            // Read-modify-write
            case Instruction.ASL or Instruction.LSR or Instruction.ROL or Instruction.ROR or Instruction.INC
                or Instruction.DEC or Instruction.SLO or Instruction.RLA or Instruction.SRE or Instruction.RRA
                or Instruction.DCP or Instruction.ISB or Instruction.TRB or Instruction.TSB or Instruction.RMB or Instruction.SMB:
                if (mode == AddressingMode.Accumulator)
                {
                    Read(PC);
                    A = Modify(instruction, A);
                }
                else
                {
                    // The 65C02 only spends the abs,X fix-up cycle for shifts when the page changes.
                    var fixup = !IsCmos || mode != AddressingMode.AbsoluteX || instruction is Instruction.INC or Instruction.DEC;
                    var address = Address(mode, fixup);
                    var value = Read(address);
                    if (IsCmos)
                    {
                        Read(address); // the 65C02 reads again instead of writing the old value
                    }
                    else
                    {
                        Write(address, value); // the 6502 writes the unmodified value back first
                    }

                    _bitIndex = (opcode >> 4) & 7;
                    Write(address, Modify(instruction, value));
                }

                break;

            case Instruction.BBR or Instruction.BBS:
            {
                var value = Read(Fetch());
                Read(PC);
                var set = (value & (1 << ((opcode >> 4) & 7))) != 0;
                Branch(instruction == Instruction.BBS ? set : !set);
                break;
            }

            // Branches
            case Instruction.BPL: Branch(!Negative); break;
            case Instruction.BMI: Branch(Negative); break;
            case Instruction.BVC: Branch(!Overflow); break;
            case Instruction.BVS: Branch(Overflow); break;
            case Instruction.BCC: Branch(!Carry); break;
            case Instruction.BCS: Branch(Carry); break;
            case Instruction.BNE: Branch(!Zero); break;
            case Instruction.BEQ: Branch(Zero); break;
            case Instruction.BRA: Branch(true); break;

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
            case Instruction.PHX:
                Read(PC);
                Push(X);
                break;
            case Instruction.PHY:
                Read(PC);
                Push(Y);
                break;
            case Instruction.PLX:
                Read(PC);
                Read((ushort)(0x100 | SP));
                X = Pull();
                SetZn(X);
                break;
            case Instruction.PLY:
                Read(PC);
                Read((ushort)(0x100 | SP));
                Y = Pull();
                SetZn(Y);
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
                else if (mode == AddressingMode.AbsoluteIndexedIndirect)
                {
                    var pointer = (ushort)(FetchWord() + X);
                    Read(PC);
                    PC = ReadWord(pointer);
                }
                else if (IsCmos)
                {
                    // The 65C02 fixed the page-wrap bug, at the cost of a cycle.
                    var pointer = FetchWord();
                    Read(PC);
                    PC = ReadWord(pointer);
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
                if (IsCmos)
                {
                    Decimal = false;
                }

                PC = ReadWord(vector);
                _prevNeedNmi = false; // no interrupt immediately after BRK's vector fetch
                break;

            case Instruction.STP:
                IsJammed = true;
                Read(PC);
                break;

            case Instruction.WAI:
                Read(PC);
                Read(PC);
                IsWaiting = true;
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
            case Instruction.ADC:
                if (Decimal && Variant != CpuVariant.Ricoh2A03)
                {
                    AddDecimal(value);
                }
                else
                {
                    AddWithCarry(value);
                }

                break;
            case Instruction.SBC:
                if (Decimal && Variant != CpuVariant.Ricoh2A03)
                {
                    SubtractDecimal(value);
                }
                else
                {
                    AddWithCarry((byte)~value);
                }

                break;
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
            case Instruction.TSB:
                Zero = (A & value) == 0;
                return (byte)(value | A);
            case Instruction.TRB:
                Zero = (A & value) == 0;
                return (byte)(value & ~A);
            case Instruction.RMB: return (byte)(value & ~(1 << _bitIndex));
            case Instruction.SMB: return (byte)(value | (1 << _bitIndex));
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

    /// <summary>
    /// Decimal ADC (Bruce Clark, "Decimal Mode" on 6502.org). On the NMOS 6502, Z comes from the binary sum and N
    /// and V from the result before the high nibble is corrected; the 65C02 sets N and Z from the result.
    /// </summary>
    private void AddDecimal(byte value)
    {
        var carry = Carry ? 1 : 0;
        var lo = (A & 0x0F) + (value & 0x0F) + carry;
        if (lo >= 0x0A)
        {
            lo = ((lo + 0x06) & 0x0F) + 0x10;
        }

        var sum = (A & 0xF0) + (value & 0xF0) + lo;
        var binaryZero = ((A + value + carry) & 0xFF) == 0;
        Overflow = (~(A ^ value) & (A ^ sum) & 0x80) != 0;
        Negative = (sum & 0x80) != 0;
        if (sum >= 0xA0)
        {
            sum += 0x60;
        }

        Carry = sum >= 0x100;
        A = (byte)sum;
        if (IsCmos)
        {
            SetZn(A);
        }
        else
        {
            Zero = binaryZero;
        }
    }

    /// <summary>Decimal SBC: C and V as in binary; N and Z from the binary result on the NMOS 6502, from the result on the 65C02.</summary>
    private void SubtractDecimal(byte value)
    {
        var carry = Carry ? 1 : 0;
        var binary = A - value - (1 - carry);
        Overflow = ((A ^ value) & (A ^ binary) & 0x80) != 0;
        var newCarry = binary >= 0;
        int result;
        if (IsCmos)
        {
            var lo = (A & 0x0F) - (value & 0x0F) + carry - 1;
            result = binary;
            if (result < 0)
            {
                result -= 0x60;
            }

            if (lo < 0)
            {
                result -= 0x06;
            }

            A = (byte)result;
            SetZn(A);
        }
        else
        {
            var lo = (A & 0x0F) - (value & 0x0F) + carry - 1;
            if (lo < 0)
            {
                lo = ((lo - 0x06) & 0x0F) - 0x10;
            }

            result = (A & 0xF0) - (value & 0xF0) + lo;
            if (result < 0)
            {
                result -= 0x60;
            }

            SetZn((byte)binary);
            A = (byte)result;
        }

        Carry = newCarry;
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
        if (IsCmos)
        {
            Decimal = false;
        }

        PC = ReadWord(vector);
    }

    // ---------------------------------------------------------------- state

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 3);
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
        writer.WriteBool(_dmcDmaPending);
        writer.WriteBool(_needHalt);
        writer.WriteBool(_needDummyRead);
        writer.WriteBool(IsWaiting); // version 3: the 65C02's WAI
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        var version = reader.BeginChunk(Tag);
        if (version is not (2 or 3))
        {
            throw new StateFormatException($"Unsupported CPU state version {version}.");
        }

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
        _dmcDmaPending = reader.ReadBool();
        _needHalt = reader.ReadBool();
        _needDummyRead = reader.ReadBool();
        IsWaiting = version >= 3 && reader.ReadBool();
        reader.EndChunk();
    }
}
