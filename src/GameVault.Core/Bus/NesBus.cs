using System.Runtime.CompilerServices;
using GameVault.Core.Apu;
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

    private IInputDevice _port1;
    private IInputDevice _port2;
    private readonly Apu2A03 _apu;
    private readonly bool _mapperCycleHooks;
    private readonly bool _mapperSnoopsPpu;

    public NesBus(Ppu2C02 ppu, Apu2A03 apu, IMapper mapper, ClockRatio clock, StandardController port1, StandardController port2)
    {
        _ppu = ppu;
        _apu = apu;
        _mapperCycleHooks = mapper.HasCycleHooks;
        _mapperSnoopsPpu = mapper.SnoopsPpuRegisters;
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

    /// <summary>Plugs a device into port 0 ($4016) or 1 ($4017).</summary>
    public void SetPort(int port, IInputDevice device)
    {
        if (port == 0)
        {
            _port1 = device;
        }
        else
        {
            _port2 = device;
        }
    }

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
            // Controller data is bit 0; the undriven upper bits read as open bus.
            switch (address)
            {
                case 0x4015:
                    // $4015 is internal to the 2A03, so reading it doesn't change the external data bus.
                    var status = _apu.ReadStatus(_openBus);
                    EndCycle(_readAfter);
                    return status;
                case 0x4016:
                    value = (byte)((_openBus & 0xE0) | (_port1.Read() & 0x1F));
                    break;
                case 0x4017:
                    value = (byte)((_openBus & 0xE0) | (_port2.Read() & 0x1F));
                    break;
                default:
                    value = _openBus;
                    break;
            }
        }
        else
        {
            value = _mapper.CpuRead(address, _openBus);
            if (Cheats is { } cheats && address >= 0x8000)
            {
                value = cheats.ReadRom(address, value);
            }
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
            if (_mapperSnoopsPpu)
            {
                _mapper.OnPpuRegisterWrite((ushort)(0x2000 | (address & 7)), value);
            }
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
        else if (address < 0x4018)
        {
            _apu.WriteRegister(address, value);
        }
        else if (address >= 0x4020)
        {
            _mapper.CpuWrite(address, value);
            _ppu.RefreshMirroring(); // mappers can switch mirroring on any register write
        }

        EndCycle(_writeAfter);
    }

    /// <summary>A CPU cycle without a bus access (DMA skipping a dummy read).</summary>
    public void Idle()
    {
        BeginCycle(_readBefore);
        EndCycle(_readAfter);
    }

    /// <summary>Side-effect-free read for the tracer. I/O registers read as $FF, matching nestest.log.</summary>
    /// <summary>Active Game Genie and RAM codes (SPEC §7.8), or null. Set between frames.</summary>
    public Cheats.CheatEngine? Cheats { get; set; }

    /// <summary>
    /// Writes RAM without a bus cycle, for RAM codes: internal RAM, or cartridge RAM at $6000-$7FFF.
    /// </summary>
    public void PokeRam(ushort address, byte value)
    {
        if (address < 0x2000)
        {
            _ram[address & 0x7FF] = value;
        }
        else if (address is >= 0x6000 and < 0x8000)
        {
            _mapper.CpuWrite(address, value);
        }
    }

    public byte Peek(ushort address) => address switch
    {
        < 0x2000 => _ram[address & 0x7FF],
        < 0x4020 => 0xFF,
        _ => _mapper.CpuPeek(address),
    };

    private void BeginCycle(int masterClocks)
    {
        if (_mapperCycleHooks)
        {
            _mapper.OnCpuCycle();
        }

        RunPpu(masterClocks);
    }

    /// <summary>Finishes the cycle and updates the CPU's interrupt inputs, which it samples at the end of each cycle.</summary>
    private void EndCycle(int masterClocks)
    {
        RunPpu(masterClocks);

        if (_cpu is { } cpu)
        {
            cpu.NmiLine = _ppu.NmiOutput;

            // APU IRQs are sampled from before this cycle's APU tick: an IRQ the frame counter raises now
            // reaches the CPU one cycle later (blargg cpu_interrupts_v2).
            var irq = (_apu.FrameIrq ? IrqSource.FrameCounter : IrqSource.None)
                | (_apu.DmcIrq ? IrqSource.Dmc : IrqSource.None)
                | (_mapperCycleHooks && _mapper.IrqPending ? IrqSource.Mapper : IrqSource.None);
            var external = cpu.IrqLines & IrqSource.External;
            if ((irq | external) != cpu.IrqLines)
            {
                cpu.ClearIrq(IrqSource.FrameCounter | IrqSource.Dmc | IrqSource.Mapper);
                cpu.SetIrq(irq);
            }
        }

        // The APU ticks after the access, so a register write lands before that cycle's frame-counter clock.
        _apu.Tick();
        if (_cpu is not null && _apu.TakeDmcDmaRequest())
        {
            _cpu.RequestDmcDma();
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
