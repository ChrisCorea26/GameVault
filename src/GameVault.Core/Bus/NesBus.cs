using System.Runtime.CompilerServices;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu;
using GameVault.Core.Input;
using GameVault.Core.Ppu;
using GameVault.Core.State;

namespace GameVault.Core.Bus;

/// <summary>
/// The CPU address space (NESdev Wiki: "CPU memory map"). Each access is one CPU cycle on the master
/// clock. The PPU runs part of the cycle before the access and the rest after, because reads and writes
/// land at different points within the cycle (NESdev Wiki: "Cycle reference chart"). That split decides
/// races such as reading $2002 just as VBlank starts.
/// </summary>
public sealed class NesBus : ICpuBus, IStateful
{
    private static readonly StateTag Tag = StateTag.From("BUS ");

    private readonly byte[] _ram = new byte[0x800];
    private readonly Ppu2C02 _ppu;
    private readonly IMapper _mapper;
    private readonly ClockRatio _clock;
    private readonly int _readBefore;
    private readonly int _readAfter;
    private readonly int _writeBefore;
    private readonly int _writeAfter;
    private Cpu6502? _cpu;
    private byte _openBus;
    private long _masterClock;
    private long _ppuMasterClock;
    private int _ppuLag = DefaultPpuLag;

    private readonly StandardController _port1;
    private readonly StandardController _port2;

    public NesBus(Ppu2C02 ppu, IMapper mapper, ClockRatio clock, StandardController port1, StandardController port2)
    {
        _ppu = ppu;
        _port1 = port1;
        _port2 = port2;
        _mapper = mapper;
        _clock = clock;

        // Reads sample the bus a master clock earlier than mid-cycle; writes a master clock later.
        var half = clock.CpuDivider / 2;
        _readBefore = half - 1;
        _readAfter = clock.CpuDivider - _readBefore;
        _writeBefore = half + 1;
        _writeAfter = clock.CpuDivider - _writeBefore;
    }

    public void AttachCpu(Cpu6502 cpu) => _cpu = cpu;

    /// <summary>
    /// Master clocks the PPU trails the CPU. The real CPU/PPU alignment varies per power-on; 1 is the
    /// alignment that passes blargg's ppu_vbl_nmi suite, which times NMI and $2002 races to the dot.
    /// </summary>
    internal const int DefaultPpuLag = 1;

    internal int PpuLag
    {
        get => _ppuLag;
        set => _ppuLag = value;
    }

    /// <summary>PPU dots the CPU's clock is ahead by (0 or 1), so tracers can report the CPU's view of time.</summary>
    public int PendingPpuDots => (int)((_masterClock - _ppuMasterClock) / _clock.PpuDivider);

    /// <summary>Clears RAM and the clocks. Real RAM powers up with unpredictable contents; zero keeps runs deterministic.</summary>
    public void Power()
    {
        Array.Clear(_ram);
        _openBus = 0;
        _masterClock = _ppuMasterClock = 0;
    }

    public byte Read(ushort address)
    {
        BeginCycle(_readBefore);
        byte value;
        if (address < 0x2000)
        {
            value = _ram[address & 0x7FF];
        }
        else if (address < 0x4000)
        {
            value = _ppu.ReadRegister(address);
        }
        else if (address < 0x4020)
        {
            // Controller data is bit 0; the undriven upper bits read as open bus. APU arrives in Phase 3.
            value = address switch
            {
                0x4015 => (byte)(_openBus & 0x20),
                0x4016 => (byte)((_openBus & 0xE0) | _port1.Read()),
                0x4017 => (byte)((_openBus & 0xE0) | _port2.Read()),
                _ => _openBus,
            };
        }
        else
        {
            value = _mapper.CpuRead(address, _openBus);
        }

        _openBus = value;
        EndCycle(_readAfter);
        return value;
    }

    public void Write(ushort address, byte value)
    {
        BeginCycle(_writeBefore);
        _openBus = value;
        if (address < 0x2000)
        {
            _ram[address & 0x7FF] = value;
        }
        else if (address < 0x4000)
        {
            _ppu.WriteRegister(address, value);
        }
        else if (address == 0x4014)
        {
            _cpu?.RequestOamDma(value);
        }
        else if (address == 0x4016)
        {
            _port1.Write(value);
            _port2.Write(value);
        }
        else if (address >= 0x4020)
        {
            _mapper.CpuWrite(address, value);
        }

        EndCycle(_writeAfter);
    }

    /// <summary>Side-effect-free read for the tracer. I/O registers read as $FF, matching nestest.log.</summary>
    public byte Peek(ushort address) => address switch
    {
        < 0x2000 => _ram[address & 0x7FF],
        < 0x4020 => 0xFF,
        _ => _mapper.CpuPeek(address),
    };

    private void BeginCycle(int masterClocks)
    {
        _mapper.OnCpuCycle();
        RunPpu(masterClocks);
    }

    /// <summary>Finishes the cycle and updates the CPU's interrupt inputs, which it samples at the end of each cycle.</summary>
    private void EndCycle(int masterClocks)
    {
        RunPpu(masterClocks);

        if (_cpu is { } cpu)
        {
            cpu.NmiLine = _ppu.NmiOutput;
            if (_mapper.IrqPending)
            {
                cpu.SetIrq(IrqSource.Mapper);
            }
            else
            {
                cpu.ClearIrq(IrqSource.Mapper);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RunPpu(int masterClocks)
    {
        _masterClock += masterClocks;
        var divider = _clock.PpuDivider;
        var target = _masterClock - _ppuLag;
        while (_ppuMasterClock + divider <= target)
        {
            _ppu.Tick();
            _ppuMasterClock += divider;
        }
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_ram);
        writer.WriteByte(_openBus);
        writer.WriteInt64(_masterClock);
        writer.WriteInt64(_ppuMasterClock);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_ram);
        _openBus = reader.ReadByte();
        _masterClock = reader.ReadInt64();
        _ppuMasterClock = reader.ReadInt64();
        reader.EndChunk();
    }
}
