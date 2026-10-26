using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.Nintendo64;

/// <summary>The RDP's command registers as the RSP sees them (COP0 registers 8–15), at their physical addresses $0410_00xx.</summary>
public interface IN64DpRegisters
{
    uint ReadRegister(uint address);

    void WriteRegister(uint address, uint value);
}

/// <summary>
/// The Reality Signal Processor (SPEC §7.11, Phase 16): a MIPS core without multiply, divide or 64-bit instructions,
/// running from its own 4 KB IMEM over its 4 KB DMEM, with the vector unit as coprocessor 2. The CPU starts and stops
/// it through the SP registers, and it moves data with its DMA engine. One instruction runs per RCP cycle.
/// </summary>
/// <remarks>
/// References: n64brew "Reality Signal Processor", "Reality Signal Processor/CPU Core" and
/// "Reality Signal Processor/Interface" (the SP registers, SP_STATUS's write bits, DMA length/count/skip).
/// Every memory here is big-endian: byte <c>n</c> of <see cref="Dmem"/> is DMEM address <c>n</c>.
/// </remarks>
public sealed partial class N64Rsp : IStateful
{
    public const int MemorySize = 4096;

    // Physical addresses of the SP registers.
    public const uint SpDmaSpAddr = 0x04040000;
    public const uint SpDmaRamAddr = 0x04040004;
    public const uint SpDmaRdLen = 0x04040008;
    public const uint SpDmaWrLen = 0x0404000C;
    public const uint SpStatus = 0x04040010;
    public const uint SpDmaFull = 0x04040014;
    public const uint SpDmaBusy = 0x04040018;
    public const uint SpSemaphore = 0x0404001C;
    public const uint SpPc = 0x04080000;
    public const uint SpIbist = 0x04080004;

    // SP_STATUS read bits.
    public const uint StatusHalt = 1 << 0;
    public const uint StatusBroke = 1 << 1;
    public const uint StatusDmaBusy = 1 << 2;
    public const uint StatusDmaFull = 1 << 3;
    public const uint StatusIoFull = 1 << 4;
    public const uint StatusSingleStep = 1 << 5;
    public const uint StatusInterruptOnBreak = 1 << 6;
    private const int StatusSignalShift = 7;

    private const uint DpRegisterBase = 0x04100000;

    private static readonly StateTag Tag = StateTag.From("RSP ");

    private readonly IN64RcpHost _host;
    private readonly IN64DpRegisters _dp;
    private readonly uint[] _gpr = new uint[32];

    private uint _pc;
    private uint _nextPc = 4;
    private uint _status = StatusHalt;
    private bool _semaphore;

    // The DMA registers as last written (and as left by the last transfer).
    private uint _dmaSpAddr;
    private uint _dmaRamAddr;
    private uint _dmaRdLen;
    private uint _dmaWrLen;

    public N64Rsp(IN64RcpHost host, IN64DpRegisters dp)
    {
        _host = host;
        _dp = dp;
    }

    /// <summary>Data memory, at $0400_0000.</summary>
    public byte[] Dmem { get; } = new byte[MemorySize];

    /// <summary>Instruction memory, at $0400_1000.</summary>
    public byte[] Imem { get; } = new byte[MemorySize];

    /// <summary>Instructions executed since power-on.</summary>
    public long Cycles { get; private set; }

    public bool Halted => (_status & StatusHalt) != 0;

    /// <summary>SP_STATUS as the CPU reads it.</summary>
    public uint Status => _status;

    /// <summary>The program counter (12 bits, in IMEM).</summary>
    public uint Pc => _pc;

    /// <summary>A scalar register, for tests and debugging.</summary>
    public uint Gpr(int index) => _gpr[index];

    /// <summary>Power-on state: halted at 0, registers and memories cleared.</summary>
    public void Reset()
    {
        Array.Clear(_gpr);
        Array.Clear(Dmem);
        Array.Clear(Imem);
        ResetVector();
        _pc = 0;
        _nextPc = 4;
        _status = StatusHalt;
        _semaphore = false;
        _dmaSpAddr = _dmaRamAddr = _dmaRdLen = _dmaWrLen = 0;
        Cycles = 0;
    }

    /// <summary>Runs up to <paramref name="cycles"/> instructions, stopping when the RSP halts (BREAK, or a halt written to SP_STATUS).</summary>
    public void Run(int cycles)
    {
        while (cycles-- > 0 && (_status & StatusHalt) == 0)
        {
            Step();
            if ((_status & StatusSingleStep) != 0)
            {
                _status |= StatusHalt;
            }
        }
    }

    public uint ReadRegister(uint address)
    {
        switch (address)
        {
            case SpDmaSpAddr: return _dmaSpAddr;
            case SpDmaRamAddr: return _dmaRamAddr;
            case SpDmaRdLen: return _dmaRdLen;
            case SpDmaWrLen: return _dmaWrLen;
            case SpStatus: return _status;
            case SpDmaFull: return 0; // transfers finish at once, so the queue is never full
            case SpDmaBusy: return 0;
            case SpSemaphore:
                {
                    var value = _semaphore ? 1u : 0u;
                    _semaphore = true;
                    return value;
                }

            case SpPc: return _pc;
            case SpIbist: return 0;
            default: return 0;
        }
    }

    public void WriteRegister(uint address, uint value)
    {
        switch (address)
        {
            case SpDmaSpAddr:
                _dmaSpAddr = value & 0x1FF8;
                break;
            case SpDmaRamAddr:
                _dmaRamAddr = value & 0xFFFFF8;
                break;
            case SpDmaRdLen:
                _dmaRdLen = value;
                Dma(toRdram: false, value);
                break;
            case SpDmaWrLen:
                _dmaWrLen = value;
                Dma(toRdram: true, value);
                break;
            case SpStatus:
                WriteStatus(value);
                break;
            case SpSemaphore:
                _semaphore = false;
                break;
            case SpPc:
                _pc = value & 0xFFC;
                _nextPc = (_pc + 4) & 0xFFC;
                break;
        }
    }

    /// <summary>SP_STATUS's write bits: pairs of clear/set bits (n64brew: "Reality Signal Processor/Interface").</summary>
    private void WriteStatus(uint value)
    {
        if ((value & (1 << 0)) != 0 && (value & (1 << 1)) == 0)
        {
            _status &= ~StatusHalt;
        }
        else if ((value & (1 << 1)) != 0 && (value & (1 << 0)) == 0)
        {
            _status |= StatusHalt;
        }

        if ((value & (1 << 2)) != 0)
        {
            _status &= ~StatusBroke;
        }

        if ((value & (1 << 3)) != 0 && (value & (1 << 4)) == 0)
        {
            _host.ClearInterrupt(N64Interrupt.Sp);
        }
        else if ((value & (1 << 4)) != 0 && (value & (1 << 3)) == 0)
        {
            _host.RaiseInterrupt(N64Interrupt.Sp);
        }

        ApplyPair(value, 5, StatusSingleStep);
        ApplyPair(value, 7, StatusInterruptOnBreak);
        for (var signal = 0; signal < 8; signal++)
        {
            ApplyPair(value, 9 + 2 * signal, 1u << (StatusSignalShift + signal));
        }
    }

    private void ApplyPair(uint value, int clearBit, uint flag)
    {
        var clear = (value & (1u << clearBit)) != 0;
        var set = (value & (1u << (clearBit + 1))) != 0;
        if (clear && !set)
        {
            _status &= ~flag;
        }
        else if (set && !clear)
        {
            _status |= flag;
        }
    }

    /// <summary>
    /// A DMA between RDRAM and IMEM/DMEM, finished at once. The length register holds the row length minus one (rounded
    /// up to 8 bytes), the row count minus one and the skip between rows in RDRAM. The SP address wraps within its
    /// memory. Afterwards the addresses point past the transfer and the length reads $FF8 with a count of 0, as on hardware.
    /// </summary>
    private void Dma(bool toRdram, uint lengthRegister)
    {
        var length = (int)((lengthRegister & 0xFFF) | 7) + 1;
        var count = (int)((lengthRegister >> 12) & 0xFF) + 1;
        var skip = (int)((lengthRegister >> 20) & 0xFF8);
        var memory = (_dmaSpAddr & 0x1000) != 0 ? Imem : Dmem;
        var spAddress = (int)(_dmaSpAddr & 0xFF8);
        var ramAddress = (int)(_dmaRamAddr & 0xFFFFF8);
        var rdram = _host.Rdram;

        for (var row = 0; row < count; row++)
        {
            for (var i = 0; i < length; i++)
            {
                var sp = (spAddress + i) & 0xFFF;
                var ram = (ramAddress + i) & 0xFFFFFF;
                if (toRdram)
                {
                    if (ram < rdram.Length)
                    {
                        rdram[ram] = memory[sp];
                    }
                }
                else
                {
                    memory[sp] = ram < rdram.Length ? rdram[ram] : (byte)0;
                }
            }

            spAddress = (spAddress + length) & 0xFFF;
            ramAddress = (ramAddress + length + skip) & 0xFFFFF8;
        }

        _dmaSpAddr = (_dmaSpAddr & 0x1000) | (uint)spAddress;
        _dmaRamAddr = (uint)ramAddress;
        var final = (lengthRegister & 0xFF800000) | 0xFF8;
        if (toRdram)
        {
            _dmaWrLen = final;
            _dmaRdLen = final;
        }
        else
        {
            _dmaRdLen = final;
            _dmaWrLen = final;
        }
    }

    private void Step()
    {
        var pc = _pc;
        var instruction = (uint)(Imem[pc] << 24 | Imem[pc + 1] << 16 | Imem[pc + 2] << 8 | Imem[pc + 3]);
        _pc = _nextPc;
        _nextPc = (_nextPc + 4) & 0xFFC;
        Cycles++;
        Execute(instruction, pc);
        _gpr[0] = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Branch(uint pc, uint instruction, bool taken)
    {
        if (taken)
        {
            _nextPc = (uint)(pc + 4 + ((short)instruction << 2)) & 0xFFC;
        }
    }

    private void Execute(uint instruction, uint pc)
    {
        var rs = (int)((instruction >> 21) & 31);
        var rt = (int)((instruction >> 16) & 31);
        var immediate = (uint)(short)instruction;
        switch (instruction >> 26)
        {
            case 0x00:
                ExecuteSpecial(instruction, pc, rs, rt);
                break;
            case 0x01: // REGIMM
                {
                    var value = (int)_gpr[rs];
                    var op = rt & 0x1F;
                    var taken = (op & 1) == 0 ? value < 0 : value >= 0;
                    if ((op & 0x10) != 0)
                    {
                        _gpr[31] = (pc + 8) & 0xFFF;
                    }

                    Branch(pc, instruction, taken);
                    break;
                }

            case 0x02: // J
                _nextPc = (instruction << 2) & 0xFFC;
                break;
            case 0x03: // JAL
                _gpr[31] = (pc + 8) & 0xFFF;
                _nextPc = (instruction << 2) & 0xFFC;
                break;
            case 0x04: Branch(pc, instruction, _gpr[rs] == _gpr[rt]); break; // BEQ
            case 0x05: Branch(pc, instruction, _gpr[rs] != _gpr[rt]); break; // BNE
            case 0x06: Branch(pc, instruction, (int)_gpr[rs] <= 0); break; // BLEZ
            case 0x07: Branch(pc, instruction, (int)_gpr[rs] > 0); break; // BGTZ
            case 0x08: // ADDI (the RSP has no overflow exception)
            case 0x09: _gpr[rt] = _gpr[rs] + immediate; break; // ADDIU
            case 0x0A: _gpr[rt] = (int)_gpr[rs] < (int)immediate ? 1u : 0u; break; // SLTI
            case 0x0B: _gpr[rt] = _gpr[rs] < immediate ? 1u : 0u; break; // SLTIU
            case 0x0C: _gpr[rt] = _gpr[rs] & (immediate & 0xFFFF); break; // ANDI
            case 0x0D: _gpr[rt] = _gpr[rs] | (immediate & 0xFFFF); break; // ORI
            case 0x0E: _gpr[rt] = _gpr[rs] ^ (immediate & 0xFFFF); break; // XORI
            case 0x0F: _gpr[rt] = instruction << 16; break; // LUI
            case 0x10: ExecuteCop0(instruction, rs, rt); break;
            case 0x12: ExecuteCop2(instruction, rs, rt); break;
            case 0x20: _gpr[rt] = (uint)(sbyte)Dmem[(_gpr[rs] + immediate) & 0xFFF]; break; // LB
            case 0x21: _gpr[rt] = (uint)(short)ReadHalf(_gpr[rs] + immediate); break; // LH
            case 0x23: // LW
            case 0x27: _gpr[rt] = ReadWord(_gpr[rs] + immediate); break; // LWU (behaves as LW)
            case 0x24: _gpr[rt] = Dmem[(_gpr[rs] + immediate) & 0xFFF]; break; // LBU
            case 0x25: _gpr[rt] = ReadHalf(_gpr[rs] + immediate); break; // LHU
            case 0x28: Dmem[(_gpr[rs] + immediate) & 0xFFF] = (byte)_gpr[rt]; break; // SB
            case 0x29: WriteHalf(_gpr[rs] + immediate, _gpr[rt]); break; // SH
            case 0x2B: WriteWord(_gpr[rs] + immediate, _gpr[rt]); break; // SW
            case 0x32: LoadVector(instruction, rs, rt); break; // LWC2
            case 0x3A: StoreVector(instruction, rs, rt); break; // SWC2
            default: break; // everything else does nothing on the RSP
        }
    }

    private void ExecuteSpecial(uint instruction, uint pc, int rs, int rt)
    {
        var rd = (int)((instruction >> 11) & 31);
        var sa = (int)((instruction >> 6) & 31);
        switch (instruction & 0x3F)
        {
            case 0x00: _gpr[rd] = _gpr[rt] << sa; break; // SLL
            case 0x02: _gpr[rd] = _gpr[rt] >> sa; break; // SRL
            case 0x03: _gpr[rd] = (uint)((int)_gpr[rt] >> sa); break; // SRA
            case 0x04: _gpr[rd] = _gpr[rt] << (int)(_gpr[rs] & 31); break; // SLLV
            case 0x06: _gpr[rd] = _gpr[rt] >> (int)(_gpr[rs] & 31); break; // SRLV
            case 0x07: _gpr[rd] = (uint)((int)_gpr[rt] >> (int)(_gpr[rs] & 31)); break; // SRAV
            case 0x08: _nextPc = _gpr[rs] & 0xFFC; break; // JR
            case 0x09: // JALR
                {
                    var target = _gpr[rs] & 0xFFC;
                    _gpr[rd] = (pc + 8) & 0xFFF;
                    _nextPc = target;
                    break;
                }

            case 0x0D: // BREAK
                _status |= StatusBroke | StatusHalt;
                if ((_status & StatusInterruptOnBreak) != 0)
                {
                    _host.RaiseInterrupt(N64Interrupt.Sp);
                }

                break;
            case 0x20: // ADD
            case 0x21: _gpr[rd] = _gpr[rs] + _gpr[rt]; break; // ADDU
            case 0x22: // SUB
            case 0x23: _gpr[rd] = _gpr[rs] - _gpr[rt]; break; // SUBU
            case 0x24: _gpr[rd] = _gpr[rs] & _gpr[rt]; break; // AND
            case 0x25: _gpr[rd] = _gpr[rs] | _gpr[rt]; break; // OR
            case 0x26: _gpr[rd] = _gpr[rs] ^ _gpr[rt]; break; // XOR
            case 0x27: _gpr[rd] = ~(_gpr[rs] | _gpr[rt]); break; // NOR
            case 0x2A: _gpr[rd] = (int)_gpr[rs] < (int)_gpr[rt] ? 1u : 0u; break; // SLT
            case 0x2B: _gpr[rd] = _gpr[rs] < _gpr[rt] ? 1u : 0u; break; // SLTU
            default: break;
        }
    }

    /// <summary>MFC0/MTC0: registers 0–7 are the SP registers, 8–15 the RDP's command registers.</summary>
    private void ExecuteCop0(uint instruction, int rs, int rt)
    {
        var rd = (int)((instruction >> 11) & 15);
        var address = rd < 8 ? SpDmaSpAddr + (uint)(rd * 4) : DpRegisterBase + (uint)((rd - 8) * 4);
        if (rs == 0x00)
        {
            _gpr[rt] = rd < 8 ? ReadRegister(address) : _dp.ReadRegister(address);
        }
        else if (rs == 0x04)
        {
            if (rd < 8)
            {
                WriteRegister(address, _gpr[rt]);
            }
            else
            {
                _dp.WriteRegister(address, _gpr[rt]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadHalf(uint address) =>
        (uint)(Dmem[address & 0xFFF] << 8 | Dmem[(address + 1) & 0xFFF]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadWord(uint address) =>
        (uint)(Dmem[address & 0xFFF] << 24 | Dmem[(address + 1) & 0xFFF] << 16 | Dmem[(address + 2) & 0xFFF] << 8 | Dmem[(address + 3) & 0xFFF]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteHalf(uint address, uint value)
    {
        Dmem[address & 0xFFF] = (byte)(value >> 8);
        Dmem[(address + 1) & 0xFFF] = (byte)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteWord(uint address, uint value)
    {
        Dmem[address & 0xFFF] = (byte)(value >> 24);
        Dmem[(address + 1) & 0xFFF] = (byte)(value >> 16);
        Dmem[(address + 2) & 0xFFF] = (byte)(value >> 8);
        Dmem[(address + 3) & 0xFFF] = (byte)value;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var value in _gpr)
        {
            writer.WriteUInt32(value);
        }

        writer.WriteUInt32(_pc);
        writer.WriteUInt32(_nextPc);
        writer.WriteUInt32(_status);
        writer.WriteBool(_semaphore);
        writer.WriteUInt32(_dmaSpAddr);
        writer.WriteUInt32(_dmaRamAddr);
        writer.WriteUInt32(_dmaRdLen);
        writer.WriteUInt32(_dmaWrLen);
        writer.WriteInt64(Cycles);
        writer.WriteBytes(Dmem);
        writer.WriteBytes(Imem);
        SaveVectorState(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < _gpr.Length; i++)
        {
            _gpr[i] = reader.ReadUInt32();
        }

        _pc = reader.ReadUInt32() & 0xFFC;
        _nextPc = reader.ReadUInt32() & 0xFFC;
        _status = reader.ReadUInt32();
        _semaphore = reader.ReadBool();
        _dmaSpAddr = reader.ReadUInt32();
        _dmaRamAddr = reader.ReadUInt32();
        _dmaRdLen = reader.ReadUInt32();
        _dmaWrLen = reader.ReadUInt32();
        Cycles = reader.ReadInt64();
        reader.ReadBytes(Dmem);
        reader.ReadBytes(Imem);
        LoadVectorState(reader);
        reader.EndChunk();
    }
}
