using System.Buffers.Binary;
using GameVault.Core.Cpu.R3000;
using GameVault.Core.Input;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>The IOP's interrupt lines: the bits of I_STAT and I_MASK (ps2tek: "IOP Interrupts").</summary>
public enum Ps2IopInterrupt
{
    VBlankStart = 0,
    Gpu = 1,
    Cdvd = 2,
    Dma = 3,
    Timer0 = 4,
    Timer1 = 5,
    Timer2 = 6,
    Sio0 = 7,
    Sio1 = 8,
    Spu2 = 9,
    Pio = 10,
    VBlankEnd = 11,
    Dvd = 12,
    Pcmcia = 13,
    Timer3 = 14,
    Timer4 = 15,
    Timer5 = 16,
    Sio2 = 17,
    Usb = 22,
    FireWire = 24,
    FireWireDma = 25,
}

/// <summary>
/// The PlayStation 2's I/O processor (SPEC §7.11, Phase 17): the PlayStation's R3000A (PRId 1Fh, no GTE) at
/// 36.864 MHz with 2 MB of RAM (which the EE sees at $1C000000), the 1 KB scratchpad, the shared 4 MB BIOS, and its
/// devices: the interrupt controller, the DMA controller's 14 channels, the six timers, the SIF registers, the CDVD
/// drive, SIO2 with the pads and memory card, and the SPU2's registers and DMA.
/// </summary>
/// <remarks>
/// Physical map (ps2tek: "IOP Physical Memory Map", "IOP Map"): RAM at $00000000 (mirrored to 8 MB), the SIF at
/// $1D000000, the CDVD at $1F402000, the scratchpad at $1F800000, I/O at $1F801000 (INTC $1070, DMA $1080 and $1500,
/// timers $1100 and $1480), the IOP's console output byte at $1F80380C, SIO2 at $1F808200, the SPU2 at $1F900000 and
/// the BIOS at $1FC00000. Every instruction takes one cycle (memory wait states aren't modelled).
/// Devices catch up lazily, as on the PlayStation: the CPU runs to the next event any device has scheduled, and a
/// device catches up before its registers are touched. Without a BIOS the CPU doesn't run (<see cref="Halted"/>):
/// GameVault's own IOP services answer the EE, and time still passes for the devices.
/// The SIF (shared with the EE) is saved by the EE's side, not here.
/// </remarks>
public sealed partial class Ps2Iop : IR3000Bus, IStateful
{
    public const int RamSize = 2 * 1024 * 1024;
    public const int BiosSize = 4 * 1024 * 1024;
    public const double Clock = 36_864_000.0;

    private const int MaxSyncInterval = 1024;
    private const uint ConsoleOutput = 0x1F80380C;

    private static readonly StateTag Tag = StateTag.From("PIOP");

    private readonly byte[]? _bios;
    private readonly Ps2Sif _sif;
    private readonly byte[] _scratchpad = new byte[1024];

    // I/O registers without a device behind them (memory and bus control, DEV9's window) keep what was written.
    private readonly byte[] _io = new byte[0x1000];

    private uint _iStat;
    private uint _iMask;
    private uint _iCtrl;

    private long _lastSync;
    private long _nextSync;
    private int _spuCycles;

    /// <param name="bios">The player's BIOS (4 MB, shared with the EE), or null to boot without one: the CPU then
    /// doesn't run.</param>
    /// <param name="sif">The SIF between the IOP and the EE.</param>
    /// <param name="spu2">The SPU2, or null for none (its registers read as zero).</param>
    public Ps2Iop(byte[]? bios, Ps2Sif sif, IPs2Spu2? spu2 = null)
    {
        _bios = bios;
        _sif = sif;
        Spu2 = spu2;
        Cpu = new R3000Cpu(this, R3000Cpu.IopProcessorId, hasGte: false);
        Timers = new Ps2IopTimers { Interrupt = t => RaiseInterrupt(t < 3 ? Ps2IopInterrupt.Timer0 + t : Ps2IopInterrupt.Timer3 + (t - 3)) };
        Cdvd = new Ps2Cdvd
        {
            Interrupt = () => RaiseInterrupt(Ps2IopInterrupt.Cdvd),
            DirectTransfer = CdvdDirectTransfer,
        };
        Sio2 = new Ps2Sio2 { Interrupt = () => RaiseInterrupt(Ps2IopInterrupt.Sio2) };
        if (spu2 is not null)
        {
            spu2.Irq = () => RaiseInterrupt(Ps2IopInterrupt.Spu2);
        }

        Reset();
    }

    public R3000Cpu Cpu { get; }

    /// <summary>The IOP's 2 MB of RAM (the EE reaches it at $1C000000).</summary>
    public byte[] Ram { get; } = new byte[RamSize];

    public IPs2Spu2? Spu2 { get; }

    public Ps2IopTimers Timers { get; }

    public Ps2Cdvd Cdvd { get; }

    public Ps2Sio2 Sio2 { get; }

    public Ps2Sif Sif => _sif;

    /// <summary>IOP cycles since power-on (36.864 MHz).</summary>
    public long Cycles => Cpu.Cycles;

    /// <summary>
    /// The CPU doesn't run (no BIOS: the IOP's work is done by GameVault's services). Tests that put a program in RAM
    /// clear it after <see cref="R3000Cpu.Jump"/>.
    /// </summary>
    public bool Halted { get; set; }

    /// <summary>Characters the IOP writes to its console output byte at $1F80380C (the BIOS's TTY).</summary>
    public Action<char>? DebugOutput { get; set; }

    /// <summary>Each device register the IOP reads (the host's boot log; null while unused).</summary>
    public Action<uint>? DeviceReadProbe { get; set; }

    /// <summary>The disc in the CDVD drive.</summary>
    public IPs2Disc? Disc
    {
        get => Cdvd.Disc;
        set => Cdvd.Disc = value;
    }

    /// <summary>The memory card in slot 1 (slot 2 is always empty).</summary>
    public Ps2MemoryCard? MemoryCard
    {
        get => Sio2.MemoryCard;
        set => Sio2.MemoryCard = value;
    }

    public Ps2MemoryCard? MemoryCard2
    {
        get => Sio2.MemoryCard2;
        set => Sio2.MemoryCard2 = value;
    }

    /// <summary>I_STAT, I_MASK and I_CTRL as the CPU sees them (tests and the debugger).</summary>
    public uint InterruptStatus => _iStat;

    public uint InterruptMask => _iMask;

    public void Reset()
    {
        Array.Clear(Ram);
        Array.Clear(_scratchpad);
        Array.Clear(_io);
        Cpu.Reset();
        Cpu.Cycles = 0;
        _iStat = _iMask = _iCtrl = 0;
        _lastSync = 0;
        _nextSync = 0;
        _spuCycles = 0;
        ResetDma();
        Timers.Reset();
        Cdvd.Reset();
        Sio2.Reset();
        Spu2?.Reset();
        Halted = _bios is null;
        UpdateInterruptLine();
    }

    // ---- Running ----

    /// <summary>Runs the IOP (CPU and devices) until <see cref="Cycles"/> reaches <paramref name="untilCycle"/>.</summary>
    public void Run(long untilCycle)
    {
        while (Cpu.Cycles < untilCycle)
        {
            var stop = Math.Min(untilCycle, _nextSync);
            if (Halted)
            {
                Cpu.Cycles = Math.Max(Cpu.Cycles, stop);
            }
            else
            {
                // A register write can bring the next sync forward, so the bound is re-read each step.
                while (Cpu.Cycles < stop)
                {
                    Cpu.Step();
                    stop = Math.Min(untilCycle, _nextSync);
                }
            }

            if (Cpu.Cycles >= _nextSync)
            {
                Sync();
            }
        }
    }

    /// <summary>The EE moved data through the SIF FIFOs: the IOP's SIF channels continue at once.</summary>
    public void ServiceSif()
    {
        ServiceSif0();
        ServiceSif1();
        Sync();
    }

    /// <summary>The pad's buttons, both sticks and L3/R3 from <paramref name="state"/>.</summary>
    public void SetControllerState(int port, ControllerState state) => SetPadInput(port, Ps2PadInput.From(state));

    public void SetPadInput(int port, Ps2PadInput input)
    {
        if (port is 0 or 1)
        {
            Sio2.Pads[port].Input = input;
        }
    }

    /// <summary>The H-blank signal from the EE's video timing (timers 0, 1 and 3), at the IOP's current time.</summary>
    public void SetHBlank(bool active)
    {
        Sync();
        Timers.SetHBlank(active);
        Sync();
    }

    /// <summary>The V-blank signal: its start raises IRQ 0 and its end IRQ 11; it gates timers 1 and 3.</summary>
    public void SetVBlank(bool active)
    {
        Sync();
        Timers.SetVBlank(active);
        RaiseInterrupt(active ? Ps2IopInterrupt.VBlankStart : Ps2IopInterrupt.VBlankEnd);
        Sync();
    }

    public void RaiseInterrupt(Ps2IopInterrupt line)
    {
        _iStat |= 1u << (int)line;
        UpdateInterruptLine();
    }

    private void UpdateInterruptLine() => Cpu.InterruptLine = (_iCtrl & 1) != 0 && (_iStat & _iMask) != 0;

    // ---- Scheduling ----

    /// <summary>Brings every device up to the CPU's time, then schedules the next sync.</summary>
    private void Sync()
    {
        var now = Cpu.Cycles;
        var delta = now - _lastSync;
        if (delta > 0)
        {
            _lastSync = now;
            Timers.Advance(delta);
            if (Spu2 is { } spu)
            {
                _spuCycles += (int)Math.Min(delta, int.MaxValue / 2);
                while (_spuCycles >= IPs2Spu2.CyclesPerSample)
                {
                    _spuCycles -= IPs2Spu2.CyclesPerSample;
                    spu.Tick();
                }
            }
        }

        Cdvd.Advance(now);
        ServiceDma(now);

        var next = Math.Min(MaxSyncInterval, Timers.CyclesUntilEvent());
        if (Spu2 is not null)
        {
            next = Math.Min(next, IPs2Spu2.CyclesPerSample - _spuCycles);
        }

        next = Math.Min(next, Cdvd.NextEvent - now);
        next = Math.Min(next, NextDmaEvent() - now);
        _nextSync = now + Math.Max(1, next);
    }

    // ---- Bus ----

    public uint Fetch(uint address) => Read32(address);

    public uint Read32(uint address)
    {
        if (address < 0x00800000)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(Ram.AsSpan((int)(address & (RamSize - 4))));
        }

        if (address - 0x1F800000 < 0x400)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(_scratchpad.AsSpan((int)(address & 0x3FC)));
        }

        if (address - 0x1FC00000 < BiosSize)
        {
            var offset = (int)(address & (BiosSize - 4));
            return _bios is not null && offset + 4 <= _bios.Length ? BinaryPrimitives.ReadUInt32LittleEndian(_bios.AsSpan(offset)) : 0;
        }

        if (address == 0xFFFE0130)
        {
            return Cpu.CacheControl;
        }

        return ReadDevice(address & ~3u, 4);
    }

    public ushort Read16(uint address)
    {
        if (address < 0x00800000)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(Ram.AsSpan((int)(address & (RamSize - 2))));
        }

        if (address - 0x1F800000 < 0x400)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(_scratchpad.AsSpan((int)(address & 0x3FE)));
        }

        if (address - 0x1F900000 < 0x800 || address - Ps2Sio2.Base < 0x100 || address - Ps2Cdvd.Base < 0x40)
        {
            return (ushort)ReadDevice(address & ~1u, 2);
        }

        return (ushort)(Read32(address & ~3u) >> (int)((address & 2) * 8));
    }

    public byte Read8(uint address)
    {
        if (address < 0x00800000)
        {
            return Ram[address & (RamSize - 1)];
        }

        if (address - 0x1F800000 < 0x400)
        {
            return _scratchpad[address & 0x3FF];
        }

        if (address - 0x1F900000 < 0x800 || address - Ps2Sio2.Base < 0x100 || address - Ps2Cdvd.Base < 0x40)
        {
            return (byte)ReadDevice(address, 1);
        }

        return (byte)(Read32(address & ~3u) >> (int)((address & 3) * 8));
    }

    public void Write32(uint address, uint value)
    {
        if (address < 0x00800000)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(Ram.AsSpan((int)(address & (RamSize - 4))), value);
        }
        else if (address - 0x1F800000 < 0x400)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_scratchpad.AsSpan((int)(address & 0x3FC)), value);
        }
        else
        {
            WriteDevice(address & ~3u, value, 4);
        }
    }

    public void Write16(uint address, uint value)
    {
        if (address < 0x00800000)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(Ram.AsSpan((int)(address & (RamSize - 2))), (ushort)value);
        }
        else if (address - 0x1F800000 < 0x400)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_scratchpad.AsSpan((int)(address & 0x3FE)), (ushort)value);
        }
        else
        {
            WriteDevice(address & ~1u, value, 2);
        }
    }

    public void Write8(uint address, uint value)
    {
        if (address < 0x00800000)
        {
            Ram[address & (RamSize - 1)] = (byte)value;
        }
        else if (address - 0x1F800000 < 0x400)
        {
            _scratchpad[address & 0x3FF] = (byte)value;
        }
        else
        {
            WriteDevice(address, value, 1);
        }
    }

    // ---- Devices ----

    /// <summary>A device read of <paramref name="size"/> bytes (aligned to it).</summary>
    private uint ReadDevice(uint address, int size)
    {
        DeviceReadProbe?.Invoke(address);
        switch (address)
        {
            case >= 0x1D000000 and < 0x1D000070:
                return ReadSif(address & ~3u) >> (int)((address & 3) * 8);
            case >= Ps2Cdvd.Base and < Ps2Cdvd.Base + 0x40:
            {
                Sync();
                uint value = 0;
                for (var i = 0; i < size; i++)
                {
                    value |= (uint)Cdvd.Read(address + (uint)i - Ps2Cdvd.Base, Cpu.Cycles) << (8 * i);
                }

                Sync();
                return value;
            }

            case >= 0x1F801000 and < 0x1F802000:
                return ReadIo(address & ~3u, size) >> (int)((address & 3) * 8);
            case >= Ps2Sio2.Base and < Ps2Sio2.Base + 0x100:
            {
                var offset = address - Ps2Sio2.Base;
                if ((offset & ~3u) == 0x64)
                {
                    // The out FIFO pops a byte per read, whatever the width.
                    return Sio2.ReadFifo();
                }

                return Sio2.Read(offset & ~3u) >> (int)((offset & 3) * 8);
            }

            case >= 0x1F900000 and < 0x1F900800:
            {
                if (Spu2 is not { } spu)
                {
                    return 0;
                }

                Sync();
                var offset = address - 0x1F900000;
                var low = spu.Read16(offset & ~1u);
                if (size == 4)
                {
                    return low | (uint)spu.Read16((offset + 2) & 0x7FE) << 16;
                }

                return (offset & 1) != 0 ? (uint)(low >> 8) : low;
            }

            default:
                return 0;
        }
    }

    private void WriteDevice(uint address, uint value, int size)
    {
        switch (address)
        {
            case >= 0x1D000000 and < 0x1D000070:
                WriteSif(address & ~3u, value);
                return;
            case >= Ps2Cdvd.Base and < Ps2Cdvd.Base + 0x40:
                Sync();
                for (var i = 0; i < size; i++)
                {
                    Cdvd.Write(address + (uint)i - Ps2Cdvd.Base, (byte)(value >> (8 * i)), Cpu.Cycles);
                }

                Sync();
                return;
            case >= 0x1F801000 and < 0x1F802000:
                WriteIo(address, value, size);
                return;
            case ConsoleOutput:
                DebugOutput?.Invoke((char)(byte)value);
                return;
            case >= Ps2Sio2.Base and < Ps2Sio2.Base + 0x100:
                Sio2.Write((address - Ps2Sio2.Base) & ~3u, value);
                return;
            case >= 0x1F900000 and < 0x1F900800:
            {
                if (Spu2 is not { } spu)
                {
                    return;
                }

                Sync();
                var offset = (address - 0x1F900000) & ~1u;
                spu.Write16(offset, (ushort)value);
                if (size == 4)
                {
                    spu.Write16((offset + 2) & 0x7FE, (ushort)(value >> 16));
                }

                Sync();
                return;
            }

            case 0xFFFE0130:
                Cpu.CacheControl = value;
                return;
        }
    }

    // ---- SIF registers (ps2tek: "SIF Registers", the IOP's side at $1D000000) ----

    private uint ReadSif(uint address) => (address & 0xFF) switch
    {
        0x00 => _sif.Mscom,
        0x10 => _sif.Smcom,
        0x20 => _sif.Msflg,
        0x30 => _sif.Smflg,
        0x40 => _sif.IopReadCtrl(),
        0x60 => _sif.Bd6,
        _ => 0,
    };

    private void WriteSif(uint address, uint value)
    {
        switch (address & 0xFF)
        {
            case 0x10:
                _sif.Smcom = value;
                break;
            case 0x20:
                _sif.IopWriteMsflg(value);
                break;
            case 0x30:
                _sif.IopWriteSmflg(value);
                break;
            case 0x40:
                _sif.IopWriteCtrl(value);
                break;
            case 0x60:
                _sif.Bd6 = value;
                break;
        }
    }

    // ---- $1F801000: interrupts, DMA, timers ----

    private uint ReadIo(uint address, int size)
    {
        switch (address)
        {
            case 0x1F801070:
                return _iStat;
            case 0x1F801074:
                return _iMask;
            case 0x1F801078:
            {
                // Reading I_CTRL returns it and disables interrupts (ps2tek: "IOP Interrupts").
                var value = _iCtrl;
                _iCtrl = 0;
                UpdateInterruptLine();
                return value;
            }

            case >= 0x1F801080 and < 0x1F801100:
                return ReadDma(address);
            case >= 0x1F801100 and < 0x1F801130:
                Sync();
                return Timers.Read((int)((address >> 4) & 3), (int)((address >> 2) & 3));
            case >= 0x1F801480 and < 0x1F8014B0:
                Sync();
                return Timers.Read(3 + (int)((address - 0x1F801480) >> 4), (int)((address >> 2) & 3));
            case >= 0x1F801500 and < 0x1F801580:
                return ReadDma(address);
            default:
                return BinaryPrimitives.ReadUInt32LittleEndian(_io.AsSpan((int)(address - 0x1F801000)));
        }
    }

    /// <summary>
    /// An I/O write with the CPU register's whole value: 32-bit registers take all of it whatever the store's width,
    /// as on the PlayStation (ps1-tests cpu/io-access-bitwidth).
    /// </summary>
    private void WriteIo(uint address, uint value, int size)
    {
        var aligned = address & ~3u;
        switch (aligned)
        {
            case 0x1F801070:
                _iStat &= value; // writing 0 acknowledges
                UpdateInterruptLine();
                return;
            case 0x1F801074:
                _iMask = value & 0x03FFFFFF;
                UpdateInterruptLine();
                return;
            case 0x1F801078:
                _iCtrl = value & 1;
                UpdateInterruptLine();
                return;
            case >= 0x1F801080 and < 0x1F801100:
            case >= 0x1F801500 and < 0x1F801580:
                WriteDma(aligned, value);
                return;
            case >= 0x1F801100 and < 0x1F801130:
                Sync();
                Timers.Write((int)((aligned >> 4) & 3), (int)((aligned >> 2) & 3), value);
                Sync();
                return;
            case >= 0x1F801480 and < 0x1F8014B0:
                Sync();
                Timers.Write(3 + (int)((aligned - 0x1F801480) >> 4), (int)((aligned >> 2) & 3), value);
                Sync();
                return;
        }

        var offset = (int)(address - 0x1F801000);
        switch (size)
        {
            case 4:
                BinaryPrimitives.WriteUInt32LittleEndian(_io.AsSpan(offset), value);
                break;
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(_io.AsSpan(offset), (ushort)value);
                break;
            default:
                _io[offset] = (byte)value;
                break;
        }
    }

    // ---- Save state ----

    public void SaveState(StateWriter writer)
    {
        Sync();
        writer.BeginChunk(Tag, 1);
        Cpu.SaveState(writer);
        writer.WriteBytes(Ram);
        writer.WriteBytes(_scratchpad);
        writer.WriteBytes(_io);
        writer.WriteUInt32(_iStat);
        writer.WriteUInt32(_iMask);
        writer.WriteUInt32(_iCtrl);
        writer.WriteInt64(_lastSync);
        writer.WriteInt64(_nextSync);
        writer.WriteInt32(_spuCycles);
        writer.WriteBool(Halted);
        SaveDma(writer);
        Timers.SaveState(writer);
        Cdvd.SaveState(writer);
        Sio2.SaveState(writer);
        writer.WriteBool(Spu2 is not null);
        Spu2?.SaveState(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        if (reader.BeginChunk(Tag) != 1)
        {
            throw new StateFormatException("Unsupported IOP state version.");
        }

        Cpu.LoadState(reader);
        reader.ReadBytes(Ram);
        reader.ReadBytes(_scratchpad);
        reader.ReadBytes(_io);
        _iStat = reader.ReadUInt32();
        _iMask = reader.ReadUInt32();
        _iCtrl = reader.ReadUInt32();
        _lastSync = reader.ReadInt64();
        _nextSync = reader.ReadInt64();
        _spuCycles = reader.ReadInt32();
        Halted = reader.ReadBool();
        LoadDma(reader);
        Timers.LoadState(reader);
        Cdvd.LoadState(reader);
        Sio2.LoadState(reader);
        if (reader.ReadBool())
        {
            if (Spu2 is null)
            {
                throw new StateFormatException("The state has an SPU2 but this IOP has none.");
            }

            Spu2.LoadState(reader);
        }

        reader.EndChunk();
        UpdateInterruptLine();
    }
}
