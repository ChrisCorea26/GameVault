using GameVault.Core.Audio;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu.Sh2;
using GameVault.Core.State;

namespace GameVault.Core.Sega32X;

/// <summary>The player's three 32X BIOS files.</summary>
/// <param name="Genesis">The 68000's 256-byte vector BIOS.</param>
/// <param name="Master">The master SH-2's 2 KB boot ROM.</param>
/// <param name="Slave">The slave SH-2's 1 KB boot ROM.</param>
public sealed record Sega32XBios(byte[] Genesis, byte[] Master, byte[] Slave);

/// <summary>
/// The 32X (sega-cd-32x.md, milestone 2), plugged into the cartridge slot: two SH-2s at 23 MHz with 256 KB of SDRAM,
/// the 32X VDP (<see cref="Mars32XVdp"/>), PWM sound, the communication registers both sides share, the 68000-to-SH-2
/// FIFO, and the cartridge as both sides see it. <see cref="Genesis.GenesisConsole"/> routes the 68000's cartridge
/// space and $A15100–$A153FF here, runs the unit in step with its master clock and lays the 32X picture over its own.
/// </summary>
/// <remarks>
/// The 68000's side: with the adapter off it sees the cartridge as usual; with ADEN set the 32X's vector BIOS sits at
/// $000000–$0000FF, the cartridge at $880000 (its first 512 KB) and $900000 (a 1 MB bank), the frame buffer at $840000
/// and its overwrite image at $860000. The SH-2s' side: their boot ROM at 0, the system registers at $4000, the VDP at
/// $4100, the palette at $4200, the cartridge at $02000000, the frame buffer at $04000000 and SDRAM at $06000000.
/// Interrupts (each SH-2 masks its own): VRES 14, V 12, H 10, CMD 8, PWM 6. Behaviour follows Sega's 32X hardware
/// manual, read for behaviour only.
/// </remarks>
public sealed class Sega32XUnit : IStateful
{
    /// <summary>The SH-2s' clock: three-sevenths of the Genesis' master clock.</summary>
    public const int ClockNumerator = 3, ClockDenominator = 7;

    private static readonly StateTag Tag = StateTag.From("32X ");

    private readonly byte[] _rom;
    private readonly Sega32XBios _bios;
    private readonly byte[] _sdram = new byte[0x40000];
    private readonly ushort[] _comm = new ushort[8];
    private readonly ushort[] _fifo = new ushort[8];
    private readonly Port[] _ports;
    private readonly ushort[] _line = new ushort[Mars32XVdp.Width];

    private bool _adapterEnabled;
    private bool _sh2Reset = true;
    private bool _sh2Access; // FM: the SH-2s own the VDP and frame buffer
    private bool _romToVdp; // RV
    private int _bank;
    private ushort _dreqControl;
    private uint _dreqSource;
    private uint _dreqDestination;
    private ushort _dreqLength;
    private int _fifoCount;
    private int _fifoRead;
    private ushort _hCount;
    private int _hCounter;
    private ushort _pwmControl;
    private ushort _pwmCycle;
    private ushort _pwmLeft;
    private ushort _pwmRight;
    private int _pwmTimer;
    private int _pwmPeriods;
    private long _sh2Cycles;
    private long _frameBase;
    private float _lastLeft;
    private float _lastRight;

    /// <exception cref="RomFormatException">A BIOS file isn't the size the 32X's is.</exception>
    public Sega32XUnit(byte[] rom, Sega32XBios bios, bool pal, double masterClock)
    {
        if (bios.Genesis.Length != 256 || bios.Master.Length != 2048 || bios.Slave.Length != 1024)
        {
            throw new RomFormatException("The 32X BIOS files should be 256 bytes (the 68000's), 2 KB (the master SH-2's) and 1 KB (the slave SH-2's).");
        }

        _rom = rom;
        _bios = bios;
        MasterClock = masterClock;
        Vdp = new Mars32XVdp { Pal = pal };
        _ports = [new Port(this, master: true), new Port(this, master: false)];
        Master = new Sh2Cpu(_ports[0], "master SH-2");
        Slave = new Sh2Cpu(_ports[1], "slave SH-2");
        Master.ExternalVector = level => 64 + (level >> 1);
        Slave.ExternalVector = level => 64 + (level >> 1);
    }

    public double MasterClock { get; }

    public Sh2Cpu Master { get; }

    public Sh2Cpu Slave { get; }

    public Mars32XVdp Vdp { get; }

    public Span<byte> Sdram => _sdram;

    public ReadOnlySpan<ushort> Communication => _comm;

    public bool AdapterEnabled => _adapterEnabled;

    public BandLimitedResampler? Audio { get; set; }

    public void Power()
    {
        Array.Clear(_sdram);
        Vdp.Reset();
        Reset();
        _sh2Cycles = 0;
        _frameBase = 0;
    }

    public void Reset()
    {
        Array.Clear(_comm);
        _adapterEnabled = false;
        _sh2Reset = true;
        _sh2Access = false;
        _romToVdp = false;
        _bank = 0;
        _dreqControl = 0;
        _dreqSource = _dreqDestination = 0;
        _dreqLength = 0;
        _fifoCount = _fifoRead = 0;
        _hCount = 0;
        _hCounter = 0;
        _pwmControl = 0;
        _pwmCycle = 0;
        _pwmLeft = _pwmRight = 0;
        _pwmTimer = 0;
        _pwmPeriods = 0;
        foreach (var port in _ports)
        {
            port.Reset();
        }

        _lastLeft = _lastRight = 0;
    }

    // ---- Timing ----

    /// <summary>Runs both SH-2s up to <paramref name="master"/>, the 68000's master clock within the frame, in small turns.</summary>
    public void RunTo(long master)
    {
        var target = (long)((_frameBase + master) * ((double)ClockNumerator / ClockDenominator));
        while (_sh2Cycles < target)
        {
            var slice = (int)Math.Min(target - _sh2Cycles, 32);
            if (_sh2Reset)
            {
                _sh2Cycles += slice;
                RunPwm(slice);
                continue;
            }

            RunCpu(Master, _ports[0], slice);
            RunCpu(Slave, _ports[1], slice);
            _sh2Cycles += slice;
            RunPwm(slice);
        }
    }

    private static void RunCpu(Sh2Cpu cpu, Port port, int slice)
    {
        port.Budget += slice;
        while (port.Budget > 0)
        {
            cpu.InterruptLevel = port.InterruptLevel;
            port.Budget -= cpu.Step();
        }
    }

    public void EndFrame(long frameClocks) => _frameBase += frameClocks;

    /// <summary>A Genesis line starts: the H interrupt counter, and vertical blank's start and end.</summary>
    public void BeginLine(int line, int activeHeight)
    {
        var vblank = line >= activeHeight;
        if (line == activeHeight)
        {
            Vdp.SetVBlank(true);
            foreach (var port in _ports)
            {
                port.Raise(Port.V);
            }
        }
        else if (line == 0)
        {
            Vdp.SetVBlank(false);
            _hCounter = _hCount;
        }

        foreach (var port in _ports)
        {
            if (!vblank || port.HInVBlank)
            {
                if (_hCounter == 0)
                {
                    port.Raise(Port.H);
                }
            }
        }

        _hCounter = _hCounter == 0 ? _hCount : _hCounter - 1;
    }

    public void SetHBlank(bool hblank) => Vdp.SetHBlank(hblank);

    /// <summary>
    /// Lays the 32X picture over the Genesis' (called as vertical blank starts): a 32X pixel shows in front when PRI
    /// (or its colour's bit 15, swapping it) says so, and always where only the Genesis' background colour shows.
    /// </summary>
    /// <param name="frame">The Genesis frame's palette indices; 32X colours go in as 1536 + their 15-bit colour.</param>
    public void Compose(Span<ushort> frame, ReadOnlySpan<bool> backdrop, int width, int height)
    {
        if (Vdp.Mode == 0)
        {
            return;
        }

        var inFront = Vdp.InFront;
        for (var y = 0; y < height; y++)
        {
            if (!Vdp.RenderLine(y, _line))
            {
                return;
            }

            var row = y * width;
            for (var x = 0; x < Math.Min(width, Mars32XVdp.Width); x++)
            {
                var pixel = _line[x];
                var front = inFront ^ ((pixel & 0x8000) != 0);
                if (front || backdrop[row + x])
                {
                    frame[row + x] = (ushort)(Sega32XPalette.First + (pixel & 0x7FFF));
                }
            }
        }
    }

    // ---- PWM ----

    private void RunPwm(int cycles)
    {
        var cycle = (_pwmCycle - 1) & 0x0FFF;
        if (cycle == 0 || (_pwmControl & 0x0F) == 0)
        {
            return;
        }

        _pwmTimer += cycles;
        while (_pwmTimer >= cycle)
        {
            _pwmTimer -= cycle;
            PwmSample(cycle);
            var tm = (_pwmControl >> 8) & 0x0F;
            var every = tm == 0 ? 16 : tm; // TM: an interrupt every TM samples (0 is 16)
            if (++_pwmPeriods >= every)
            {
                _pwmPeriods = 0;
                foreach (var port in _ports)
                {
                    port.Raise(Port.Pwm);
                    port.Cpu.OnChip.Dreq(1);
                }
            }
        }
    }

    private void PwmSample(int cycle)
    {
        if (Audio is not { } audio)
        {
            return;
        }

        float Level(ushort width) => Math.Clamp(((width & 0x0FFF) - cycle / 2f) / cycle, -0.5f, 0.5f) * 0.5f;
        var left = (_pwmControl & 0x03) == 0 ? 0 : Level((_pwmControl & 0x03) == 2 ? _pwmRight : _pwmLeft);
        var right = (_pwmControl & 0x0C) == 0 ? 0 : Level((_pwmControl & 0x0C) == 0x08 ? _pwmLeft : _pwmRight);
        if (left != _lastLeft || right != _lastRight)
        {
            var clock = (long)(_sh2Cycles * ((double)ClockDenominator / ClockNumerator)) - _frameBase;
            audio.AddDelta((int)Math.Max(0, clock), left - _lastLeft, right - _lastRight);
            _lastLeft = left;
            _lastRight = right;
        }
    }

    // ---- Registers both sides share ($A15100 / $4000) ----

    private ushort ReadShared(int offset, Port? port)
    {
        switch (offset & 0xFE)
        {
            case >= 0x20 and < 0x30:
                return _comm[(offset - 0x20) >> 1];
            case 0x30: return _pwmControl;
            case 0x32: return _pwmCycle;
            case 0x34: return 0x4000; // the FIFOs: never full, always drained
            case 0x36: return 0x4000;
            case 0x38: return 0x4000;
            default: return 0;
        }
    }

    private void WriteShared(int offset, ushort value)
    {
        switch (offset & 0xFE)
        {
            case >= 0x20 and < 0x30:
                _comm[(offset - 0x20) >> 1] = value;
                break;
            case 0x30:
                _pwmControl = (ushort)(value & 0x0F8F);
                break;
            case 0x32:
                _pwmCycle = (ushort)(value & 0x0FFF);
                break;
            case 0x34:
                _pwmLeft = value;
                break;
            case 0x36:
                _pwmRight = value;
                break;
            case 0x38:
                _pwmLeft = _pwmRight = value;
                break;
        }
    }

    // ---- The 68000's side ----

    /// <summary>A read of the 68000's cartridge space ($000000–$3FFFFF and $800000–$9FFFFF).</summary>
    public ushort MainReadWord(uint address)
    {
        address &= 0xFFFFFE;
        if (address < 0x400000)
        {
            if (!_adapterEnabled || _romToVdp)
            {
                return RomWord(address);
            }

            return address < 0x100 ? (ushort)((_bios.Genesis[address] << 8) | _bios.Genesis[address + 1]) : RomWord(address);
        }

        return address switch
        {
            >= 0x840000 and < 0x860000 => Vdp.ReadFrame((int)(address - 0x840000)),
            >= 0x860000 and < 0x880000 => Vdp.ReadFrame((int)(address - 0x860000)),
            >= 0x880000 and < 0x900000 => _romToVdp ? (ushort)0 : RomWord(address - 0x880000),
            >= 0x900000 and < 0xA00000 => _romToVdp ? (ushort)0 : RomWord((uint)(_bank << 20) + (address - 0x900000)),
            _ => 0,
        };
    }

    public byte MainReadByte(uint address)
    {
        var word = MainReadWord(address);
        return (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
    }

    public void MainWriteWord(uint address, ushort value)
    {
        address &= 0xFFFFFE;
        switch (address)
        {
            case >= 0x840000 and < 0x860000:
                Vdp.WriteFrameWord((int)(address - 0x840000), value);
                break;
            case >= 0x860000 and < 0x880000:
                Vdp.OverwriteWord((int)(address - 0x860000), value);
                break;
        }
    }

    public void MainWriteByte(uint address, byte value)
    {
        switch (address & 0xFFFFFF)
        {
            case >= 0x840000 and < 0x860000:
                Vdp.WriteFrameByte((int)(address - 0x840000), value);
                break;
            case >= 0x860000 and < 0x880000:
                Vdp.OverwriteByte((int)(address - 0x860000), value);
                break;
        }
    }

    private ushort RomWord(uint address) =>
        address + 1 < _rom.Length ? (ushort)((_rom[address] << 8) | _rom[address + 1]) : (ushort)0xFFFF;

    /// <summary>The 68000's view of $A15100–$A153FF and the "MARS" ID at $A130EC.</summary>
    public ushort MainReadRegister(uint address)
    {
        var a = (int)(address & 0xFFFE);
        if (a is >= 0x30EC and < 0x30F0)
        {
            return a == 0x30EC ? (ushort)0x4D41 : (ushort)0x5253; // "MARS"
        }

        var offset = a - 0x5100;
        switch (offset)
        {
            case 0x00:
                return (ushort)((_sh2Access ? 0x8000 : 0) | 0x80 | (_sh2Reset ? 0 : 2) | (_adapterEnabled ? 1 : 0));
            case 0x02:
                return (ushort)((_ports[0].CommandRequest ? 1 : 0) | (_ports[1].CommandRequest ? 2 : 0));
            case 0x04:
                return (ushort)_bank;
            case 0x06:
                return (ushort)(_dreqControl | (_fifoCount >= _fifo.Length ? 0x80 : 0));
            case 0x08: return (ushort)(_dreqSource >> 16);
            case 0x0A: return (ushort)_dreqSource;
            case 0x0C: return (ushort)(_dreqDestination >> 16);
            case 0x0E: return (ushort)_dreqDestination;
            case 0x10: return _dreqLength;
            case >= 0x80 and < 0x100:
                return Vdp.ReadRegister(offset - 0x80);
            case >= 0x100 and < 0x300:
                return Vdp.Palette[(offset - 0x100) >> 1];
            default:
                return ReadShared(offset, null);
        }
    }

    public void MainWriteRegister(uint address, ushort value)
    {
        var offset = (int)(address & 0xFFFE) - 0x5100;
        switch (offset)
        {
            case 0x00:
            {
                var reset = (value & 2) == 0;
                if (_sh2Reset && !reset)
                {
                    Master.Reset();
                    Slave.Reset();
                }

                _sh2Reset = reset;
                _adapterEnabled = (value & 1) != 0;
                _sh2Access = (value & 0x8000) != 0;
                break;
            }

            case 0x02:
                // INTM / INTS: the 68000 interrupts an SH-2 (CMD).
                if ((value & 1) != 0)
                {
                    _ports[0].CommandRequest = true;
                    _ports[0].Raise(Port.Command);
                }

                if ((value & 2) != 0)
                {
                    _ports[1].CommandRequest = true;
                    _ports[1].Raise(Port.Command);
                }

                break;
            case 0x04:
                _bank = value & 3;
                break;
            case 0x06:
                _dreqControl = (ushort)(value & 0x07);
                _romToVdp = (value & 1) != 0;
                if ((value & 4) == 0)
                {
                    _fifoCount = _fifoRead = 0;
                }

                break;
            case 0x08: _dreqSource = (_dreqSource & 0xFFFF) | ((uint)(value & 0xFF) << 16); break;
            case 0x0A: _dreqSource = (_dreqSource & 0xFF0000) | value; break;
            case 0x0C: _dreqDestination = (_dreqDestination & 0xFFFF) | ((uint)(value & 0xFF) << 16); break;
            case 0x0E: _dreqDestination = (_dreqDestination & 0xFF0000) | value; break;
            case 0x10: _dreqLength = (ushort)(value & 0xFFFC); break;
            case 0x12:
                PushFifo(value);
                break;
            case >= 0x80 and < 0x100:
                Vdp.WriteRegister(offset - 0x80, value);
                break;
            case >= 0x100 and < 0x300:
                Vdp.Palette[(offset - 0x100) >> 1] = value;
                break;
            default:
                WriteShared(offset, value);
                break;
        }
    }

    /// <summary>A word into the 68000-to-SH-2 FIFO; the master SH-2's DMA channel 0 takes it on DREQ.</summary>
    private void PushFifo(ushort value)
    {
        if ((_dreqControl & 4) == 0 || _fifoCount >= _fifo.Length)
        {
            return;
        }

        _fifo[(_fifoRead + _fifoCount) % _fifo.Length] = value;
        _fifoCount++;
        while (_fifoCount > 0 && Master.OnChip.WaitingForDreq(0))
        {
            Master.OnChip.Dreq(0);
        }
    }

    private ushort PopFifo()
    {
        if (_fifoCount == 0)
        {
            return 0;
        }

        var value = _fifo[_fifoRead];
        _fifoRead = (_fifoRead + 1) % _fifo.Length;
        _fifoCount--;
        return value;
    }

    // ---- The SH-2s' side ----

    private byte[] Boot(bool master) => master ? _bios.Master : _bios.Slave;

    private ushort Sh2Read16(Port port, uint address)
    {
        switch (address >> 24)
        {
            case 0x00:
                if (address < 0x4000)
                {
                    var boot = Boot(port.IsMaster);
                    var at = (int)(address % (uint)boot.Length);
                    return (ushort)((boot[at] << 8) | boot[at + 1]);
                }

                if (address < 0x4100)
                {
                    return port.ReadRegister((int)(address & 0xFE));
                }

                if (address < 0x4200)
                {
                    return Vdp.ReadRegister((int)(address & 0x0E));
                }

                return address < 0x4400 ? Vdp.Palette[(int)(address - 0x4200) >> 1] : (ushort)0;
            case 0x02:
                return RomWord(address & 0x3FFFFF);
            case 0x04:
                return Vdp.ReadFrame((int)(address & 0x1FFFF));
            case 0x06:
                return (ushort)((_sdram[address & 0x3FFFE] << 8) | _sdram[(address & 0x3FFFE) + 1]);
            default:
                return 0;
        }
    }

    private void Sh2Write16(Port port, uint address, ushort value)
    {
        switch (address >> 24)
        {
            case 0x00:
                if (address is >= 0x4000 and < 0x4100)
                {
                    port.WriteRegister((int)(address & 0xFE), value);
                }
                else if (address is >= 0x4100 and < 0x4200)
                {
                    Vdp.WriteRegister((int)(address & 0x0E), value);
                }
                else if (address is >= 0x4200 and < 0x4400)
                {
                    Vdp.Palette[(int)(address - 0x4200) >> 1] = value;
                }

                break;
            case 0x04:
                if ((address & 0x3FFFF) >= 0x20000)
                {
                    Vdp.OverwriteWord((int)(address & 0x1FFFF), value);
                }
                else
                {
                    Vdp.WriteFrameWord((int)(address & 0x1FFFF), value);
                }

                break;
            case 0x06:
                _sdram[address & 0x3FFFE] = (byte)(value >> 8);
                _sdram[(address & 0x3FFFE) + 1] = (byte)value;
                break;
        }
    }

    private byte Sh2Read8(Port port, uint address)
    {
        if (address >> 24 == 0x06)
        {
            return _sdram[address & 0x3FFFF];
        }

        var word = Sh2Read16(port, address & ~1u);
        return (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
    }

    private void Sh2Write8(Port port, uint address, byte value)
    {
        switch (address >> 24)
        {
            case 0x06:
                _sdram[address & 0x3FFFF] = value;
                break;
            case 0x04:
                if ((address & 0x3FFFF) >= 0x20000)
                {
                    Vdp.OverwriteByte((int)(address & 0x1FFFF), value);
                }
                else
                {
                    Vdp.WriteFrameByte((int)(address & 0x1FFFF), value);
                }

                break;
            default:
            {
                // Registers take bytes into their half of the word.
                var word = Sh2Read16(port, address & ~1u);
                word = (address & 1) == 0 ? (ushort)((word & 0x00FF) | (value << 8)) : (ushort)((word & 0xFF00) | value);
                Sh2Write16(port, address & ~1u, word);
                break;
            }
        }
    }

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        Master.SaveState(writer);
        Slave.SaveState(writer);
        Vdp.SaveState(writer);
        writer.WriteBytes(_sdram);
        foreach (var word in _comm)
        {
            writer.WriteUInt16(word);
        }

        foreach (var word in _fifo)
        {
            writer.WriteUInt16(word);
        }

        foreach (var port in _ports)
        {
            port.Save(writer);
        }

        writer.WriteBool(_adapterEnabled);
        writer.WriteBool(_sh2Reset);
        writer.WriteBool(_sh2Access);
        writer.WriteBool(_romToVdp);
        writer.WriteInt32(_bank);
        writer.WriteUInt16(_dreqControl);
        writer.WriteUInt32(_dreqSource);
        writer.WriteUInt32(_dreqDestination);
        writer.WriteUInt16(_dreqLength);
        writer.WriteInt32(_fifoCount);
        writer.WriteInt32(_fifoRead);
        writer.WriteUInt16(_hCount);
        writer.WriteInt32(_hCounter);
        writer.WriteUInt16(_pwmControl);
        writer.WriteUInt16(_pwmCycle);
        writer.WriteUInt16(_pwmLeft);
        writer.WriteUInt16(_pwmRight);
        writer.WriteInt32(_pwmTimer);
        writer.WriteInt32(_pwmPeriods);
        writer.WriteInt64(_sh2Cycles);
        writer.WriteInt64(_frameBase);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Master.LoadState(reader);
        Slave.LoadState(reader);
        Vdp.LoadState(reader);
        reader.ReadBytes(_sdram);
        for (var i = 0; i < _comm.Length; i++)
        {
            _comm[i] = reader.ReadUInt16();
        }

        for (var i = 0; i < _fifo.Length; i++)
        {
            _fifo[i] = reader.ReadUInt16();
        }

        foreach (var port in _ports)
        {
            port.Load(reader);
        }

        _adapterEnabled = reader.ReadBool();
        _sh2Reset = reader.ReadBool();
        _sh2Access = reader.ReadBool();
        _romToVdp = reader.ReadBool();
        _bank = reader.ReadInt32() & 3;
        _dreqControl = reader.ReadUInt16();
        _dreqSource = reader.ReadUInt32();
        _dreqDestination = reader.ReadUInt32();
        _dreqLength = reader.ReadUInt16();
        _fifoCount = Math.Clamp(reader.ReadInt32(), 0, _fifo.Length);
        _fifoRead = Math.Clamp(reader.ReadInt32(), 0, _fifo.Length - 1);
        _hCount = reader.ReadUInt16();
        _hCounter = reader.ReadInt32();
        _pwmControl = reader.ReadUInt16();
        _pwmCycle = reader.ReadUInt16();
        _pwmLeft = reader.ReadUInt16();
        _pwmRight = reader.ReadUInt16();
        _pwmTimer = Math.Max(0, reader.ReadInt32());
        _pwmPeriods = Math.Max(0, reader.ReadInt32());
        _sh2Cycles = reader.ReadInt64();
        _frameBase = reader.ReadInt64();
        reader.EndChunk();
        _lastLeft = _lastRight = 0;
    }

    /// <summary>One SH-2's bus: its boot ROM, its own interrupt mask and pending interrupts, and the shared rest.</summary>
    private sealed class Port(Sega32XUnit unit, bool master) : ISh2Bus
    {
        public const int Pwm = 0, Command = 1, H = 2, V = 3, Vres = 4;
        private static readonly int[] Levels = [6, 8, 10, 12, 14];

        private ushort _mask;
        private int _pending;

        public bool IsMaster => master;

        public Sh2Cpu Cpu => master ? unit.Master : unit.Slave;

        public int Budget { get; set; }

        public bool CommandRequest { get; set; }

        public bool HInVBlank => (_mask & 0x80) != 0;

        public int InterruptLevel
        {
            get
            {
                var active = _pending & ((_mask & 0x0F) | 0x10);
                for (var source = Vres; source >= Pwm; source--)
                {
                    if ((active & (1 << source)) != 0)
                    {
                        return Levels[source];
                    }
                }

                return 0;
            }
        }

        public void Reset()
        {
            _mask = 0;
            _pending = 0;
            Budget = 0;
            CommandRequest = false;
        }

        public void Raise(int source) => _pending |= 1 << source;

        public ushort ReadRegister(int offset)
        {
            switch (offset)
            {
                case 0x00:
                    return (ushort)((unit._sh2Access ? 0x8000 : 0) | (unit._adapterEnabled ? 0x0200 : 0) | (_mask & 0x8F));
                case 0x04:
                    return unit._hCount;
                case 0x06:
                    return (ushort)(unit._dreqControl | (unit._fifoCount >= unit._fifo.Length ? 0x80 : 0) | (unit._fifoCount == 0 ? 0x4000 : 0));
                case 0x08: return (ushort)(unit._dreqSource >> 16);
                case 0x0A: return (ushort)unit._dreqSource;
                case 0x0C: return (ushort)(unit._dreqDestination >> 16);
                case 0x0E: return (ushort)unit._dreqDestination;
                case 0x10: return unit._dreqLength;
                case 0x12: return unit.PopFifo();
                default: return unit.ReadShared(offset, this);
            }
        }

        public void WriteRegister(int offset, ushort value)
        {
            switch (offset)
            {
                case 0x00:
                    _mask = (ushort)(value & 0x8F);
                    if ((value & 0x8000) != 0)
                    {
                        unit._sh2Access = true;
                    }

                    break;
                case 0x04:
                    unit._hCount = (ushort)(value & 0xFF);
                    break;
                case 0x14: _pending &= ~(1 << Vres); break;
                case 0x16: _pending &= ~(1 << V); break;
                case 0x18: _pending &= ~(1 << H); break;
                case 0x1A:
                    _pending &= ~(1 << Command);
                    CommandRequest = false;
                    break;
                case 0x1C: _pending &= ~(1 << Pwm); break;
                default:
                    unit.WriteShared(offset, value);
                    break;
            }
        }

        public byte Read8(uint address) => unit.Sh2Read8(this, address);

        public ushort Read16(uint address) => unit.Sh2Read16(this, address);

        public uint Read32(uint address) => ((uint)unit.Sh2Read16(this, address) << 16) | unit.Sh2Read16(this, address + 2);

        public void Write8(uint address, byte value) => unit.Sh2Write8(this, address, value);

        public void Write16(uint address, ushort value) => unit.Sh2Write16(this, address, value);

        public void Write32(uint address, uint value)
        {
            unit.Sh2Write16(this, address, (ushort)(value >> 16));
            unit.Sh2Write16(this, address + 2, (ushort)value);
        }

        public void Save(StateWriter writer)
        {
            writer.WriteUInt16(_mask);
            writer.WriteInt32(_pending);
            writer.WriteInt32(Budget);
            writer.WriteBool(CommandRequest);
        }

        public void Load(StateReader reader)
        {
            _mask = reader.ReadUInt16();
            _pending = reader.ReadInt32() & 0x1F;
            Budget = reader.ReadInt32();
            CommandRequest = reader.ReadBool();
        }
    }
}

/// <summary>The 32X's colours in the Genesis frame: 1536 Genesis entries, then the 32,768 15-bit colours.</summary>
public static class Sega32XPalette
{
    public const int First = 1536;

    /// <summary>The Genesis' palette followed by every 32X colour (5 bits each of blue, green and red, red lowest).</summary>
    public static uint[] Combined(ReadOnlySpan<uint> genesis)
    {
        var table = new uint[First + 32768];
        genesis[..Math.Min(genesis.Length, First)].CopyTo(table);
        for (var c = 0; c < 32768; c++)
        {
            uint Expand(int v) => (uint)((v << 3) | (v >> 2));
            var r = Expand(c & 0x1F);
            var g = Expand((c >> 5) & 0x1F);
            var b = Expand((c >> 10) & 0x1F);
            table[First + c] = 0xFF000000 | (r << 16) | (g << 8) | b;
        }

        return table;
    }
}
