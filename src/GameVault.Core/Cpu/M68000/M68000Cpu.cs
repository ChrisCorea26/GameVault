using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.Cpu.M68000;

/// <summary>Memory as the 68000 sees it: a 24-bit address bus with byte and word accesses.</summary>
public interface IM68000Bus
{
    byte ReadByte(uint address);

    ushort ReadWord(uint address);

    void WriteByte(uint address, byte value);

    void WriteWord(uint address, ushort value);

    /// <summary>An interrupt at <paramref name="level"/> is taken. Returns its vector number, or -1 for the autovector.</summary>
    int AcknowledgeInterrupt(int level);
}

/// <summary>
/// Motorola 68000 (SPEC §7.11, Phase 13): every instruction and addressing mode, the exceptions (address error with
/// its 14-byte frame, illegal and line A/F, privilege, trace, divide by zero, CHK, TRAPV, TRAP) and autovectored or
/// vectored interrupts at levels 1–7.
/// </summary>
/// <remarks>
/// One <see cref="Step"/> runs a whole instruction (or takes an exception) and returns its clock cycles, counted
/// as the hardware spends them: four per bus word, plus the internal cycles from the 68000 User's Manual tables.
/// References: M68000 Family Programmer's Reference Manual; MC68000 User's Manual, section 8 (timing); Jorge Cwik's
/// DIVU/DIVS timing notes; and the SingleStepTests 680x0 suite.
/// </remarks>
public sealed partial class M68000Cpu : IStateful
{
    private static readonly StateTag Tag = StateTag.From("M68K");

    private readonly IM68000Bus _bus;
    private readonly uint[] _d = new uint[8];
    private readonly uint[] _a = new uint[8];

    // The stack pointer that isn't A7 right now: the USP in supervisor mode, the SSP in user mode.
    private uint _otherSp;
    private bool _t, _s, _x, _n, _z, _v, _c;
    private int _mask;
    private uint _pc;
    private ushort _ir;

    // Where this instruction started, and how far into it the program counter had got when a fault hit.
    private uint _instructionPc;
    private int _cycles;
    private bool _stopped;
    private bool _halted;
    private bool _nmiLatch;

    // The address a memory <ea> resolved to, for read-modify-write instructions.
    private uint _ea;

    // The address error being raised.
    private uint _faultAddress;
    private bool _faultRead;
    private bool _faultProgram;
    private bool _faultNotInstruction;
    private bool _inException;

    public M68000Cpu(IM68000Bus bus) => _bus = bus;

    /// <summary>The interrupt priority level on the IPL pins (0–7), held by the devices until they are acknowledged.</summary>
    public int InterruptLevel { get; set; }

    /// <summary>Clock cycles since power-on.</summary>
    public long Cycles { get; private set; }

    /// <summary>True after STOP, until an interrupt (or trace) wakes the CPU.</summary>
    public bool Stopped => _stopped;

    /// <summary>True after a double fault (an address error while processing an exception); only reset recovers.</summary>
    public bool Halted => _halted;

    /// <summary>Called when the RESET instruction asserts the reset line for the other devices.</summary>
    public Action? ResetDevices { get; set; }

    public uint Pc
    {
        get => _pc;
        set => _pc = value;
    }

    public ushort Sr
    {
        get => (ushort)((_t ? 0x8000 : 0) | (_s ? 0x2000 : 0) | (_mask << 8) | Ccr);
        set => SetSr(value);
    }

    /// <summary>The condition codes: X, N, Z, V and C.</summary>
    public byte Ccr
    {
        get => (byte)((_x ? 0x10 : 0) | (_n ? 8 : 0) | (_z ? 4 : 0) | (_v ? 2 : 0) | (_c ? 1 : 0));
        set
        {
            _x = (value & 0x10) != 0;
            _n = (value & 8) != 0;
            _z = (value & 4) != 0;
            _v = (value & 2) != 0;
            _c = (value & 1) != 0;
        }
    }

    public uint Usp
    {
        get => _s ? _otherSp : _a[7];
        set
        {
            if (_s)
            {
                _otherSp = value;
            }
            else
            {
                _a[7] = value;
            }
        }
    }

    public uint Ssp
    {
        get => _s ? _a[7] : _otherSp;
        set
        {
            if (_s)
            {
                _a[7] = value;
            }
            else
            {
                _otherSp = value;
            }
        }
    }

    public uint GetD(int index) => _d[index];

    public void SetD(int index, uint value) => _d[index] = value;

    public uint GetA(int index) => _a[index];

    public void SetA(int index, uint value) => _a[index] = value;

    /// <summary>The reset exception: supervisor mode, interrupts masked, SSP and PC from vectors 0 and 1.</summary>
    public void Reset()
    {
        _stopped = false;
        _halted = false;
        _t = false;
        _s = true;
        _mask = 7;
        _a[7] = ReadLongRaw(0);
        _pc = ReadLongRaw(4);
        Cycles += 40;
    }

    /// <summary>Runs one instruction, or takes a pending interrupt. Returns the clock cycles used.</summary>
    public int Step()
    {
        _cycles = 0;
        if (_halted)
        {
            Cycles += 4;
            return 4;
        }

        var level = InterruptLevel;
        if (level < 7)
        {
            _nmiLatch = false;
        }

        if (level > _mask || (level == 7 && !_nmiLatch))
        {
            _nmiLatch = level == 7;
            try
            {
                Interrupt(level);
            }
            catch (AddressFault)
            {
                AddressError();
            }

            return Finish();
        }

        if (_stopped)
        {
            Cycles += 4;
            return 4;
        }

        var trace = _t;
        _instructionPc = _pc;
        try
        {
            _ir = Fetch();
            Execute();
            if (trace)
            {
                GroupOneException(9, _pc);
            }
        }
        catch (AddressFault)
        {
            AddressError();
        }

        return Finish();
    }

    private int Finish()
    {
        Cycles += _cycles;
        return _cycles;
    }

    private void Execute()
    {
        switch (_ir >> 12)
        {
            case 0x0: Group0(); break;
            case 0x1: Move(1); break;
            case 0x2: Move(4); break;
            case 0x3: Move(2); break;
            case 0x4: Group4(); break;
            case 0x5: Group5(); break;
            case 0x6: Branch(); break;
            case 0x7: Moveq(); break;
            case 0x8: Group8(); break;
            case 0x9: AddSub(subtract: true); break;
            case 0xA: Exception(10, _instructionPc, 34); break; // line A
            case 0xB: GroupB(); break;
            case 0xC: GroupC(); break;
            case 0xD: AddSub(subtract: false); break;
            case 0xE: Shift(); break;
            default: Exception(11, _instructionPc, 34); break; // line F
        }
    }

    private void SetSr(ushort value)
    {
        SetSupervisor((value & 0x2000) != 0);
        _t = (value & 0x8000) != 0;
        _mask = (value >> 8) & 7;
        Ccr = (byte)value;
    }

    private void SetSupervisor(bool supervisor)
    {
        if (supervisor != _s)
        {
            (_a[7], _otherSp) = (_otherSp, _a[7]);
            _s = supervisor;
        }
    }

    // ---- Exceptions ----

    /// <summary>The CPU can't make an odd word or long access: it raises an address error instead.</summary>
    private sealed class AddressFault : Exception
    {
        public static readonly AddressFault Instance = new();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Fault(uint address, bool read, bool program)
    {
        _faultAddress = address;
        _faultRead = read;
        _faultProgram = program;
        _faultNotInstruction = false;
        throw AddressFault.Instance;
    }

    /// <summary>
    /// A jump to an odd address faults on the prefetch at the target: a program read with I/N set, and the pushed
    /// PC is the target minus 4.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void JumpFault(uint target)
    {
        _faultAddress = target;
        _faultRead = true;
        _faultProgram = true;
        _faultNotInstruction = true;
        _faultPc = target - 4;
        throw AddressFault.Instance;
    }

    /// <summary>
    /// Group 0: the 14-byte frame of the access that faulted (status word, address, instruction register), then SR
    /// and PC. A fault while already processing an exception is a double fault, which halts the CPU.
    /// </summary>
    private void AddressError()
    {
        if (_inException)
        {
            _inException = false;
            _halted = true;
            return;
        }

        var sr = Sr;
        var functionCode = (_s ? 4 : 0) | (_faultProgram ? 2 : 1);
        var status = (ushort)((_ir & 0xFFE0) | (_faultRead ? 0x10 : 0) | (_faultNotInstruction ? 8 : 0) | functionCode);
        var pc = _faultPc ?? _pc - 2;
        _faultPc = null;
        _t = false;
        SetSupervisor(true);
        _inException = true;
        try
        {
            Push32(pc);
            Push16(sr);
            Push16(_ir);
            Push32(_faultAddress);
            Push16(status);
            _pc = ReadLong(3 * 4);
        }
        catch (AddressFault)
        {
            _halted = true;
        }

        _inException = false;
        _stopped = false;
        _cycles += 50 - 4 * 10; // 7 words pushed and 2 read: the rest is internal
    }

    // An instruction that knows the pushed PC differs from where it got to sets this before the faulting access.
    private uint? _faultPc;

    /// <summary>A group 1 or 2 exception taking <paramref name="totalCycles"/> for the whole instruction.</summary>
    private void Exception(int vector, uint pushedPc, int totalCycles)
    {
        GroupOneException(vector, pushedPc);
        _cycles = totalCycles;
    }

    private void GroupOneException(int vector, uint pushedPc)
    {
        var sr = Sr;
        _t = false;
        SetSupervisor(true);
        _inException = true;
        Push32(pushedPc);
        Push16(sr);
        _inException = false;
        _pc = ReadLong((uint)vector * 4);
        _stopped = false;
    }

    private void Interrupt(int level)
    {
        _instructionPc = _pc;
        var vector = _bus.AcknowledgeInterrupt(level);
        if (vector < 0)
        {
            vector = 24 + level;
        }

        var sr = Sr;
        _t = false;
        SetSupervisor(true);
        _mask = level;
        _inException = true;
        Push32(_pc);
        Push16(sr);
        _inException = false;
        _pc = ReadLong((uint)vector * 4);
        _stopped = false;
        _cycles = 44;
    }

    /// <summary>True in supervisor mode; otherwise takes the privilege violation and returns false.</summary>
    private bool Privileged()
    {
        if (_s)
        {
            return true;
        }

        Exception(8, _instructionPc, 34);
        return false;
    }

    private void Illegal() => Exception(4, _instructionPc, 34);

    // ---- Bus access ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort Fetch()
    {
        if ((_pc & 1) != 0)
        {
            Fault(_pc, read: true, program: true);
        }

        var value = _bus.ReadWord(_pc & 0xFFFFFF);
        _pc += 2;
        _cycles += 4;
        return value;
    }

    private uint FetchLong()
    {
        var high = (uint)Fetch() << 16;
        return high | Fetch();
    }

    private byte ReadByte(uint address)
    {
        _cycles += 4;
        return _bus.ReadByte(address & 0xFFFFFF);
    }

    private ushort ReadWord(uint address, bool program = false)
    {
        if ((address & 1) != 0)
        {
            Fault(address, read: true, program);
        }

        _cycles += 4;
        return _bus.ReadWord(address & 0xFFFFFF);
    }

    private uint ReadLong(uint address, bool program = false)
    {
        if ((address & 1) != 0)
        {
            Fault(address, read: true, program);
        }

        var high = (uint)ReadWord(address, program) << 16;
        return high | ReadWord(address + 2, program);
    }

    private uint ReadLongRaw(uint address) => ((uint)_bus.ReadWord(address & 0xFFFFFF) << 16) | _bus.ReadWord((address + 2) & 0xFFFFFF);

    private void WriteByte(uint address, uint value)
    {
        _cycles += 4;
        _bus.WriteByte(address & 0xFFFFFF, (byte)value);
    }

    private void WriteWord(uint address, uint value)
    {
        if ((address & 1) != 0)
        {
            Fault(address, read: false, program: false);
        }

        _cycles += 4;
        _bus.WriteWord(address & 0xFFFFFF, (ushort)value);
    }

    private void WriteLong(uint address, uint value)
    {
        if ((address & 1) != 0)
        {
            Fault(address, read: false, program: false);
        }

        WriteWord(address, value >> 16);
        WriteWord(address + 2, value);
    }

    private uint Read(uint address, int size, bool program = false) => size switch
    {
        1 => ReadByte(address),
        2 => ReadWord(address, program),
        _ => ReadLong(address, program),
    };

    private void Write(uint address, int size, uint value)
    {
        switch (size)
        {
            case 1: WriteByte(address, value); break;
            case 2: WriteWord(address, value); break;
            default: WriteLong(address, value); break;
        }
    }

    private void Push16(uint value)
    {
        _a[7] -= 2;
        WriteWord(_a[7], value);
    }

    private void Push32(uint value)
    {
        _a[7] -= 4;
        if ((_a[7] & 1) != 0)
        {
            Fault(_a[7], read: false, program: false);
        }

        WriteWord(_a[7] + 2, value);
        WriteWord(_a[7], value >> 16);
    }

    private uint Pop16()
    {
        var value = ReadWord(_a[7]);
        _a[7] += 2;
        return value;
    }

    private uint Pop32()
    {
        var value = ReadLong(_a[7]);
        _a[7] += 4;
        return value;
    }

    // ---- Effective addresses ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Mask(int size) => size == 1 ? 0xFFu : size == 2 ? 0xFFFFu : 0xFFFFFFFFu;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Msb(int size) => size == 1 ? 0x80u : size == 2 ? 0x8000u : 0x80000000u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SignExtend(uint value, int size) => size == 1 ? (uint)(sbyte)value : size == 2 ? (uint)(short)value : value;

    /// <summary>Addressing-mode classes, as bits over <see cref="ModeIndex"/>.</summary>
    private const int EaAll = 0xFFF, EaData = 0xFFD, EaMemory = 0xFFC, EaControl = 0x7E4, EaAlterable = 0x1FF,
        EaDataAlterable = 0x1FD, EaMemoryAlterable = 0x1FC, EaControlAlterable = 0x1E4;

    /// <summary>0 Dn, 1 An, 2 (An), 3 (An)+, 4 -(An), 5 d16(An), 6 d8(An,Xn), 7 abs.W, 8 abs.L, 9 d16(PC), 10 d8(PC,Xn), 11 #imm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ModeIndex(int mode, int reg) => mode < 7 ? mode : reg < 5 ? 7 + reg : 12;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Valid(int mode, int reg, int classes) => ((classes >> ModeIndex(mode, reg)) & 1) != 0;

    private uint Indexed(uint baseAddress)
    {
        var extension = Fetch();
        var index = (extension & 0x8000) != 0 ? _a[(extension >> 12) & 7] : _d[(extension >> 12) & 7];
        if ((extension & 0x0800) == 0)
        {
            index = (uint)(short)index;
        }

        _cycles += 2;
        return baseAddress + index + (uint)(sbyte)extension;
    }

    /// <summary>
    /// The address of a memory operand, with the (An)+ and -(An) updates (A7 moves by 2 for bytes). Predecrement
    /// costs 2 internal cycles except as MOVE's destination.
    /// </summary>
    private uint Address(int mode, int reg, int size, bool predecrementPenalty = true)
    {
        switch (mode)
        {
            case 2:
                return _a[reg];
            case 3:
            {
                var address = _a[reg];
                _a[reg] += (uint)(reg == 7 && size == 1 ? 2 : size);
                return address;
            }

            case 4:
                if (predecrementPenalty)
                {
                    _cycles += 2;
                }

                _a[reg] -= (uint)(reg == 7 && size == 1 ? 2 : size);
                return _a[reg];
            case 5:
            {
                var baseAddress = _a[reg];
                return baseAddress + (uint)(short)Fetch();
            }

            case 6:
                return Indexed(_a[reg]);
            default:
                switch (reg)
                {
                    case 0: return (uint)(short)Fetch();
                    case 1: return FetchLong();
                    case 2:
                    {
                        var baseAddress = _pc;
                        return baseAddress + (uint)(short)Fetch();
                    }

                    case 3: return Indexed(_pc);
                    default: throw new InvalidOperationException("Not a memory addressing mode.");
                }
        }
    }

    private uint Immediate(int size) => size == 1 ? Fetch() & 0xFFu : size == 2 ? Fetch() : FetchLong();

    /// <summary>Reads an operand; a memory operand's address is kept in <see cref="_ea"/> for a write back.</summary>
    private uint ReadEa(int mode, int reg, int size)
    {
        switch (mode)
        {
            case 0: return _d[reg] & Mask(size);
            case 1: return _a[reg] & Mask(size);
            case 7 when reg == 4: return Immediate(size);
            default:
                _ea = Address(mode, reg, size);
                return Read(_ea, size);
        }
    }

    /// <summary>Writes back to the operand <see cref="ReadEa"/> just read.</summary>
    private void WriteBack(int mode, int reg, int size, uint value)
    {
        switch (mode)
        {
            case 0: SetDataRegister(reg, value, size); break;
            case 1: _a[reg] = value; break;
            default: Write(_ea, size, value); break;
        }
    }

    /// <summary>Writes a destination operand that wasn't read.</summary>
    private void WriteEa(int mode, int reg, int size, uint value, bool predecrementPenalty = true)
    {
        switch (mode)
        {
            case 0: SetDataRegister(reg, value, size); break;
            case 1: _a[reg] = value; break;
            default:
                _ea = Address(mode, reg, size, predecrementPenalty);
                Write(_ea, size, value);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetDataRegister(int reg, uint value, int size) =>
        _d[reg] = size == 4 ? value : (_d[reg] & ~Mask(size)) | (value & Mask(size));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetNz(uint value, int size)
    {
        _n = (value & Msb(size)) != 0;
        _z = (value & Mask(size)) == 0;
    }

    private bool Condition(int condition) => condition switch
    {
        0 => true,
        1 => false,
        2 => !_c && !_z,
        3 => _c || _z,
        4 => !_c,
        5 => _c,
        6 => !_z,
        7 => _z,
        8 => !_v,
        9 => _v,
        10 => !_n,
        11 => _n,
        12 => _n == _v,
        13 => _n != _v,
        14 => !_z && _n == _v,
        _ => _z || _n != _v,
    };

    // ---- Save states ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var value in _d)
        {
            writer.WriteUInt32(value);
        }

        foreach (var value in _a)
        {
            writer.WriteUInt32(value);
        }

        writer.WriteUInt32(_otherSp);
        writer.WriteUInt16(Sr);
        writer.WriteUInt32(_pc);
        writer.WriteUInt16(_ir);
        writer.WriteBool(_stopped);
        writer.WriteBool(_halted);
        writer.WriteBool(_nmiLatch);
        writer.WriteInt32(InterruptLevel);
        writer.WriteInt64(Cycles);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < 8; i++)
        {
            _d[i] = reader.ReadUInt32();
        }

        for (var i = 0; i < 8; i++)
        {
            _a[i] = reader.ReadUInt32();
        }

        _otherSp = reader.ReadUInt32();
        var sr = reader.ReadUInt16();
        _s = (sr & 0x2000) != 0; // the registers were saved as they were; don't swap stacks
        SetSr(sr);
        _pc = reader.ReadUInt32();
        _ir = reader.ReadUInt16();
        _stopped = reader.ReadBool();
        _halted = reader.ReadBool();
        _nmiLatch = reader.ReadBool();
        InterruptLevel = reader.ReadInt32();
        Cycles = reader.ReadInt64();
        reader.EndChunk();
    }
}
