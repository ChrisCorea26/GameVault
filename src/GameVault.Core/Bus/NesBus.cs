using GameVault.Core.Cartridge;
using GameVault.Core.Cpu;
using GameVault.Core.Ppu;
using GameVault.Core.State;

namespace GameVault.Core.Bus;

/// <summary>
/// The CPU address space (NESdev Wiki: "CPU memory map"). Before each access it advances the PPU and
/// the mapper by one CPU cycle on the master clock, then routes the access.
/// </summary>
public sealed class NesBus : ICpuBus, IStateful
{
    private static readonly StateTag Tag = StateTag.From("BUS ");

    private readonly byte[] _ram = new byte[0x800];
    private readonly PpuTimingStub _ppu;
    private readonly IMapper _mapper;
    private readonly ClockRatio _clock;
    private Cpu6502? _cpu;
    private byte _openBus;
    private long _masterClock;
    private long _ppuMasterClock;

    public NesBus(PpuTimingStub ppu, IMapper mapper, ClockRatio clock)
    {
        _ppu = ppu;
        _mapper = mapper;
        _clock = clock;
    }

    public void AttachCpu(Cpu6502 cpu) => _cpu = cpu;

    /// <summary>Clears RAM and the clocks. Real RAM powers up with unpredictable contents; zero keeps runs deterministic.</summary>
    public void Power()
    {
        Array.Clear(_ram);
        _openBus = 0;
        _masterClock = _ppuMasterClock = 0;
    }

    public byte Read(ushort address)
    {
        Tick();
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
            // APU and controller ports arrive in Phase 3. Unconnected bits read as open bus.
            value = address switch
            {
                0x4015 => (byte)(_openBus & 0x20),
                0x4016 or 0x4017 => (byte)(_openBus & 0xE0),
                _ => _openBus,
            };
        }
        else
        {
            value = _mapper.CpuRead(address, _openBus);
        }

        _openBus = value;
        return value;
    }

    public void Write(ushort address, byte value)
    {
        Tick();
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
        else if (address >= 0x4020)
        {
            _mapper.CpuWrite(address, value);
        }
    }

    /// <summary>Side-effect-free read for the tracer. I/O registers read as $FF, matching nestest.log.</summary>
    public byte Peek(ushort address) => address switch
    {
        < 0x2000 => _ram[address & 0x7FF],
        < 0x4020 => 0xFF,
        _ => _mapper.CpuPeek(address),
    };

    /// <summary>One CPU cycle of time for everything except the CPU.</summary>
    private void Tick()
    {
        _masterClock += _clock.CpuDivider;
        while (_ppuMasterClock + _clock.PpuDivider <= _masterClock)
        {
            _ppu.Tick();
            _ppuMasterClock += _clock.PpuDivider;
        }

        _mapper.OnCpuCycle();

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
