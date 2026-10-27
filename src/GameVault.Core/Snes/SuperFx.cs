using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>
/// The Super FX (GSU-1 and GSU-2) of Star Fox, Yoshi's Island and the other Super FX games (outstanding-work plan §4):
/// a 16-bit RISC processor with sixteen registers, a one-byte instruction pipeline (so a branch's next instruction runs
/// before the target), a 512-byte instruction cache, a ROM buffer for GETB/GETC, a pixel cache that PLOT fills and
/// that is written to its RAM as SNES characters, and its own ROM and RAM buses. No program ROM is needed: the game's
/// code runs on it.
/// </summary>
/// <remarks>
/// <para>
/// The SNES sees its registers at $3000–$32FF of banks $00–$3F/$80–$BF, the game's ROM at $8000–$FFFF of those banks
/// and at banks $40–$5F/$C0–$DF, and its RAM at banks $70–$71 (the first 8 KB also at $6000–$7FFF). While the GSU runs
/// and owns the ROM (SCMR's RON) the SNES reads interrupt vectors pointing into WRAM; while it owns the RAM (RAN) the
/// SNES reads open bus.
/// </para>
/// <para>
/// Timing is counted in master clocks (21.477 MHz): a cached byte costs 1 (CLSR=1, 21 MHz) or 2 (10.7 MHz), a ROM or
/// RAM access 5 or 6, as bsnes counts them. References: fullsnes ("SNES Cart GSU-n"), the Super FX chapters of the
/// SNES Development Manual as fullsnes and bsnes describe them.
/// </para>
/// </remarks>
public sealed partial class SuperFx : ISnesCoprocessor
{
    private static readonly StateTag Tag = StateTag.From("GSU ");

    // The vectors the SNES reads from ROM while the GSU owns it: every one points at $0100–$010C in WRAM.
    private static readonly byte[] s_ownedRomVectors =
        [0x00, 0x01, 0x00, 0x01, 0x04, 0x01, 0x00, 0x01, 0x00, 0x01, 0x08, 0x01, 0x00, 0x01, 0x0C, 0x01];

    private readonly byte[] _rom;
    private readonly byte[] _ram;
    private readonly int _ramMask;

    // Registers.
    private readonly ushort[] _r = new ushort[16];
    private bool _z;
    private bool _cy;
    private bool _s;
    private bool _ov;
    private bool _go;
    private bool _alt1;
    private bool _alt2;
    private bool _il;
    private bool _ih;
    private bool _b;
    private bool _irq;
    private byte _pbr;
    private byte _rombr;
    private byte _rambr;
    private ushort _cbr;
    private byte _scbr;
    private byte _scmr;
    private byte _colr;
    private byte _por;
    private byte _bramr;
    private byte _cfgr;
    private byte _clsr;
    private int _sreg;
    private int _dreg;

    // The pipeline, the ROM and RAM buffers.
    private byte _pipeline = 0x01;
    private bool _r14Modified;
    private bool _r15Modified;
    private byte _romBuffer;
    private ushort _ramAddress;

    // The instruction cache and the pixel caches (0 is the one being filled, 1 the one waiting to be written).
    private readonly byte[] _cache = new byte[512];
    private readonly bool[] _cacheValid = new bool[32];
    private readonly PixelCache[] _pixels = [new PixelCache(), new PixelCache()];

    private long _clock;

    /// <param name="rom">The cartridge's ROM (the GSU's program and data).</param>
    /// <param name="ramSize">The game pak RAM: 32, 64 or 128 KB.</param>
    /// <param name="version">VCR: 1 for the GSU-1 (Star Fox), 4 for the GSU-2.</param>
    public SuperFx(byte[] rom, int ramSize, byte version)
    {
        _rom = rom;
        _ram = new byte[ramSize];
        _ramMask = ramSize - 1;
        Version = version;
    }

    public string Name => "Super FX";

    public byte Version { get; }

    /// <summary>The game pak RAM (battery-backed on the boards that have one).</summary>
    public Span<byte> Ram => _ram;

    public bool Irq => _irq;

    /// <summary>True while the GSU runs (SFR's GO).</summary>
    public bool Running => _go;

    /// <summary>Instructions run so far (tests and diagnostics; not part of the state).</summary>
    public long Instructions { get; private set; }

    public void Power()
    {
        Array.Clear(_ram);
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_r);
        _z = _cy = _s = _ov = _go = _alt1 = _alt2 = _il = _ih = _b = _irq = false;
        _pbr = _rombr = _rambr = _scbr = _scmr = _colr = _por = _bramr = _cfgr = _clsr = 0;
        _cbr = 0;
        _sreg = _dreg = 0;
        _pipeline = 0x01;
        _r14Modified = _r15Modified = false;
        _romBuffer = 0;
        _ramAddress = 0;
        Array.Clear(_cache);
        Array.Clear(_cacheValid);
        _pixels[0].Clear();
        _pixels[1].Clear();
        _clock = 0;
    }

    // ---- The SNES side ----

    public bool Claims(uint address)
    {
        var bank = (int)(address >> 16) & 0x7F;
        var offset = (int)(address & 0xFFFF);
        if (bank < 0x40)
        {
            return offset >= 0x6000 || (offset >= 0x3000 && offset < 0x3300);
        }

        return bank < 0x60 || bank is 0x70 or 0x71;
    }

    public byte Read(uint address, byte openBus)
    {
        var bank = (int)(address >> 16) & 0x7F;
        var offset = (int)(address & 0xFFFF);
        if (bank < 0x40 && offset < 0x6000)
        {
            return ReadRegister(offset, openBus);
        }

        if (bank is 0x70 or 0x71 || bank < 0x40 && offset < 0x8000)
        {
            if (_go && (_scmr & 0x08) != 0)
            {
                return openBus; // the GSU owns the RAM
            }

            return _ram[SnesRamIndex(bank, offset)];
        }

        if (_go && (_scmr & 0x10) != 0)
        {
            return s_ownedRomVectors[offset & 15]; // the GSU owns the ROM
        }

        return ReadRom(bank, offset);
    }

    public void Write(uint address, byte value)
    {
        var bank = (int)(address >> 16) & 0x7F;
        var offset = (int)(address & 0xFFFF);
        if (bank < 0x40 && offset < 0x6000)
        {
            WriteRegister(offset, value);
            return;
        }

        if (bank is 0x70 or 0x71 || bank < 0x40 && offset < 0x8000)
        {
            if (!_go || (_scmr & 0x08) == 0)
            {
                _ram[SnesRamIndex(bank, offset)] = value;
            }
        }
    }

    private int SnesRamIndex(int bank, int offset) =>
        bank < 0x40 ? (offset - 0x6000) & _ramMask : (((bank & 1) << 16) | offset) & _ramMask;

    private byte ReadRegister(int offset, byte openBus)
    {
        if (offset is >= 0x3100 and < 0x3300)
        {
            return _cache[(offset - 0x3100 + _cbr) & 511];
        }

        if (offset is >= 0x3000 and < 0x3020)
        {
            var register = _r[(offset >> 1) & 15];
            return (byte)((offset & 1) == 0 ? register : register >> 8);
        }

        switch (offset)
        {
            case 0x3030:
                return (byte)Sfr;
            case 0x3031:
            {
                var value = (byte)(Sfr >> 8);
                _irq = false; // reading SFR's high byte acknowledges the interrupt
                return value;
            }

            case 0x3034: return _pbr;
            case 0x3036: return _rombr;
            case 0x303B: return Version;
            case 0x303C: return _rambr;
            case 0x303E: return (byte)_cbr;
            case 0x303F: return (byte)(_cbr >> 8);
            default: return openBus;
        }
    }

    private void WriteRegister(int offset, byte value)
    {
        if (offset is >= 0x3100 and < 0x3300)
        {
            var index = (offset - 0x3100 + _cbr) & 511;
            _cache[index] = value;
            if ((index & 15) == 15)
            {
                _cacheValid[index >> 4] = true;
            }

            return;
        }

        if (offset is >= 0x3000 and < 0x3020)
        {
            var n = (offset >> 1) & 15;
            _r[n] = (offset & 1) == 0 ? (ushort)((_r[n] & 0xFF00) | value) : (ushort)((value << 8) | (_r[n] & 0x00FF));
            if (offset == 0x301F)
            {
                _go = true; // writing R15's high byte starts the GSU
            }

            return;
        }

        switch (offset)
        {
            case 0x3030:
            {
                var wasGoing = _go;
                Sfr = (ushort)((Sfr & 0xFF00) | value);
                if (wasGoing && !_go)
                {
                    _cbr = 0;
                    FlushCache();
                }

                break;
            }

            case 0x3031: Sfr = (ushort)((value << 8) | (Sfr & 0x00FF)); break;
            case 0x3033: _bramr = (byte)(value & 1); break;
            case 0x3034:
                _pbr = (byte)(value & 0x7F);
                FlushCache();
                break;
            case 0x3037: _cfgr = value; break;
            case 0x3038: _scbr = value; break;
            case 0x3039: _clsr = (byte)(value & 1); break;
            case 0x303A: _scmr = value; break;
        }
    }

    /// <summary>The status and flag register: Z, CY, S, OV, GO, R, ALT1, ALT2, IL, IH, B and IRQ.</summary>
    internal ushort Sfr
    {
        get => (ushort)((_z ? 0x0002 : 0) | (_cy ? 0x0004 : 0) | (_s ? 0x0008 : 0) | (_ov ? 0x0010 : 0) | (_go ? 0x0020 : 0)
                        | (_alt1 ? 0x0100 : 0) | (_alt2 ? 0x0200 : 0) | (_il ? 0x0400 : 0) | (_ih ? 0x0800 : 0)
                        | (_b ? 0x1000 : 0) | (_irq ? 0x8000 : 0));
        set
        {
            _z = (value & 0x0002) != 0;
            _cy = (value & 0x0004) != 0;
            _s = (value & 0x0008) != 0;
            _ov = (value & 0x0010) != 0;
            _go = (value & 0x0020) != 0;
            _alt1 = (value & 0x0100) != 0;
            _alt2 = (value & 0x0200) != 0;
            _il = (value & 0x0400) != 0;
            _ih = (value & 0x0800) != 0;
            _b = (value & 0x1000) != 0;
            _irq = (value & 0x8000) != 0;
        }
    }

    // ---- Running ----

    public void CatchUp(long clock)
    {
        while (_clock < clock)
        {
            if (!_go)
            {
                _clock = clock;
                return;
            }

            Execute(Pipe(advance: false));
            if (_r14Modified)
            {
                _r14Modified = false;
                FillRomBuffer();
            }

            if (_r15Modified)
            {
                _r15Modified = false;
            }
            else
            {
                _r[15]++;
            }

            Instructions++;
        }
    }

    public void EndFrame(long frameClocks) => _clock -= frameClocks;

    /// <summary>Runs until the GSU stops or <paramref name="limit"/> master clocks pass (tests).</summary>
    internal void RunUntilStopped(long limit = 10_000_000)
    {
        var end = _clock + limit;
        while (_go && _clock < end)
        {
            CatchUp(_clock + 1);
        }
    }

    private void Step(int fast, int slow) => _clock += _clsr != 0 ? fast : slow;

    /// <summary>
    /// The pipeline: returns the byte in it and fetches the next one. The opcode's own fetch reads the byte at R15
    /// (<paramref name="advance"/> false); an operand's first moves R15 on to the byte after it.
    /// </summary>
    private byte Pipe(bool advance)
    {
        var result = _pipeline;
        if (advance)
        {
            _r[15]++;
        }

        _pipeline = ReadOpcode(_r[15]);
        _r15Modified = false;
        return result;
    }

    private byte Operand() => Pipe(advance: true);

    private byte ReadOpcode(ushort address)
    {
        var offset = (ushort)(address - _cbr);
        if (offset < 512)
        {
            if (!_cacheValid[offset >> 4])
            {
                var line = offset & 0x1F0;
                var source = (ushort)((_cbr + line) & 0xFFF0);
                for (var i = 0; i < 16; i++)
                {
                    Step(5, 6);
                    _cache[line + i] = GsuRead(_pbr, (ushort)(source + i));
                }

                _cacheValid[offset >> 4] = true;
            }
            else
            {
                Step(1, 2);
            }

            return _cache[offset];
        }

        Step(5, 6);
        return GsuRead(_pbr, address);
    }

    private void FlushCache() => Array.Clear(_cacheValid);

    // ---- The GSU's buses ----

    private byte GsuRead(int bank, ushort address)
    {
        bank &= 0x7F;
        if (bank < 0x60)
        {
            return ReadRom(bank, address);
        }

        return bank >= 0x70 ? _ram[(((bank & 1) << 16) | address) & _ramMask] : (byte)0;
    }

    private byte ReadRom(int bank, int offset)
    {
        if (_rom.Length == 0)
        {
            return 0;
        }

        var index = bank < 0x40 ? (bank << 15) | (offset & 0x7FFF) : ((bank - 0x40) << 16) | offset;
        return _rom[index % _rom.Length];
    }

    private byte ReadRam(ushort address)
    {
        Step(5, 6);
        return _ram[((_rambr << 16) | address) & _ramMask];
    }

    private void WriteRam(ushort address, byte value)
    {
        Step(5, 6);
        _ram[((_rambr << 16) | address) & _ramMask] = value;
    }

    /// <summary>R14 changed (or ROMB): the ROM buffer loads the byte at ROMBR:R14.</summary>
    private void FillRomBuffer()
    {
        Step(5, 6);
        _romBuffer = ReadRom(_rombr & 0x7F, _r[14]);
    }

    // ---- Tests ----

    internal ushort Register(int index) => _r[index & 15];

    internal void PokeRegister(int index, ushort value) => _r[index & 15] = value;

    internal byte ColorRegister => _colr;

    internal long Clock => _clock;

    // ---- Save states ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_ram);
        foreach (var r in _r)
        {
            writer.WriteUInt16(r);
        }

        writer.WriteUInt16(Sfr);
        writer.WriteByte(_pbr);
        writer.WriteByte(_rombr);
        writer.WriteByte(_rambr);
        writer.WriteUInt16(_cbr);
        writer.WriteByte(_scbr);
        writer.WriteByte(_scmr);
        writer.WriteByte(_colr);
        writer.WriteByte(_por);
        writer.WriteByte(_bramr);
        writer.WriteByte(_cfgr);
        writer.WriteByte(_clsr);
        writer.WriteByte((byte)_sreg);
        writer.WriteByte((byte)_dreg);
        writer.WriteByte(_pipeline);
        writer.WriteBool(_r14Modified);
        writer.WriteBool(_r15Modified);
        writer.WriteByte(_romBuffer);
        writer.WriteUInt16(_ramAddress);
        writer.WriteBytes(_cache);
        foreach (var valid in _cacheValid)
        {
            writer.WriteBool(valid);
        }

        foreach (var pixels in _pixels)
        {
            writer.WriteUInt16(pixels.Offset);
            writer.WriteByte(pixels.Pending);
            writer.WriteBytes(pixels.Data);
        }

        writer.WriteInt64(_clock);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_ram);
        for (var i = 0; i < 16; i++)
        {
            _r[i] = reader.ReadUInt16();
        }

        Sfr = reader.ReadUInt16();
        _pbr = reader.ReadByte();
        _rombr = reader.ReadByte();
        _rambr = reader.ReadByte();
        _cbr = reader.ReadUInt16();
        _scbr = reader.ReadByte();
        _scmr = reader.ReadByte();
        _colr = reader.ReadByte();
        _por = reader.ReadByte();
        _bramr = reader.ReadByte();
        _cfgr = reader.ReadByte();
        _clsr = reader.ReadByte();
        _sreg = reader.ReadByte() & 15;
        _dreg = reader.ReadByte() & 15;
        _pipeline = reader.ReadByte();
        _r14Modified = reader.ReadBool();
        _r15Modified = reader.ReadBool();
        _romBuffer = reader.ReadByte();
        _ramAddress = reader.ReadUInt16();
        reader.ReadBytes(_cache);
        for (var i = 0; i < _cacheValid.Length; i++)
        {
            _cacheValid[i] = reader.ReadBool();
        }

        foreach (var pixels in _pixels)
        {
            pixels.Offset = reader.ReadUInt16();
            pixels.Pending = reader.ReadByte();
            reader.ReadBytes(pixels.Data);
        }

        _clock = reader.ReadInt64();
        reader.EndChunk();
    }

    /// <summary>Eight pixels of one character row waiting to be written to RAM.</summary>
    private sealed class PixelCache
    {
        public ushort Offset;
        public byte Pending;
        public readonly byte[] Data = new byte[8];

        public void Clear()
        {
            Offset = 0;
            Pending = 0;
            Array.Clear(Data);
        }

        public void CopyFrom(PixelCache other)
        {
            Offset = other.Offset;
            Pending = other.Pending;
            other.Data.CopyTo(Data, 0);
        }
    }
}
