using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>
/// The Cx4 (snes-chips-3.md §3), Capcom's chip in Mega Man X2 and X3: a Hitachi HG51B169 running code from the
/// cartridge ROM. It holds two 256-instruction cache pages, 3 KB of data RAM, its own 1,024-word data ROM (the
/// player's <c>cx4.rom</c>), a 24-bit accumulator, sixteen 24-bit registers and a 48-bit multiplier.
/// </summary>
/// <remarks>
/// <para>The SNES CPU's view (LoROM boards): the ROM at $00–$3F/$80–$BF $8000–$FFFF and $C0–$FF; the data RAM at
/// $6000–$6BFF and $7000–$7BFF; the registers at $7F40–$7FFF (mirrored from $6C00). While the chip loads a cache page
/// or runs a DMA the CPU can't reach the ROM. It reads open bus, except for $00:FFC0–$FFFF, where the chip's vector
/// registers answer.</para>
/// <para>Writing the program counter ($7F4F) while the chip is halted starts it at the program bank ($7F4D/E) and that
/// address. Its pages are fetched from the ROM at the cache base ($7F49–$7F4B) + 512 × bank. HALT raises the CPU's
/// IRQ unless $7F51 masks it.</para>
/// <para>Low-level, as ares describes the chip (ares/component/processor/hg51b and sfc/coprocessor/hitachidsp, ISC
/// licence); the chip runs at 20 MHz. Subtraction's overflow flag uses the same formula as addition's, as in ares.</para>
/// </remarks>
public sealed class Cx4 : ISnesCoprocessor
{
    public const int DataRomSize = 3 * 1024;
    private const double Hz = 20_000_000;

    private static readonly StateTag Tag = StateTag.From("CX4 ");
    private static readonly int[] Shifts = [0, 1, 8, 16];

    private readonly byte[] _rom;
    private readonly uint[] _dataRom = new uint[1024];
    private readonly byte[] _dataRam = new byte[3072];
    private readonly ushort[] _programRam = new ushort[2 * 256];
    private readonly uint[] _gpr = new uint[16];
    private readonly uint[] _stack = new uint[8];
    private readonly byte[] _vector = new byte[32];
    private readonly double _clocksPerMaster;

    private long _clock;        // master clocks the chip has been caught up to
    private double _budget;     // chip clocks owed to (+) or run ahead of (−) the CPU
    private bool _holding;      // a cache load or DMA, done in one go, still has the bus at the CPU's time

    // Registers.
    private uint _pb;   // 15 bits
    private uint _pc;   // 8 bits
    private bool _n, _z, _c, _v, _i;
    private uint _a, _p, _mdr, _romBuffer, _ramBuffer, _mar, _dpr; // 24 bits (P: 15)
    private ulong _mul; // 48 bits

    // I/O.
    private bool _lock, _halt = true, _irqMasked, _romSelect = true;
    private int _waitRom = 3, _waitRam = 3;
    private bool _suspend;
    private int _suspendDuration;
    private bool _cacheEnable, _cachePreload;
    private int _cachePage;
    private readonly bool[] _cacheLock = new bool[2];
    private readonly uint[] _cacheAddress = new uint[2];
    private uint _cacheBase, _cachePb, _cachePc;
    private bool _dmaEnable;
    private uint _dmaSource, _dmaTarget, _dmaLength;
    private bool _busEnable, _busReading, _busWriting;
    private int _busPending;
    private uint _busAddress;
    private bool _irqLine;

    /// <param name="rom">The cartridge ROM (the chip's program).</param>
    /// <param name="dataRom">The player's 3 KB <c>cx4.rom</c>: 1,024 little-endian 24-bit words.</param>
    public Cx4(byte[] rom, byte[] dataRom, double masterClock)
    {
        _rom = rom;
        for (var i = 0; i < 1024; i++)
        {
            _dataRom[i] = (uint)(dataRom[i * 3] | dataRom[i * 3 + 1] << 8 | dataRom[i * 3 + 2] << 16);
        }

        _clocksPerMaster = Hz / masterClock;
    }

    public string Name => "Cx4";

    public bool Irq => _irqLine;

    /// <summary>The data RAM (for tests).</summary>
    public Span<byte> DataRam => _dataRam;

    public bool Halted => _halt;

    public uint Accumulator => _a;

    public uint Register(int index) => _gpr[index & 15];

    private bool Busy => _cacheEnable || _dmaEnable || _busPending > 0 || (_holding && _budget < 0);

    private bool Running => Busy || !_halt;

    // ---- The CPU's side ----

    public bool Claims(uint address) =>
        IsRom(address) || IsDataRam(address) || IsIo(address);

    private static bool IsRom(uint address) => (address & 0x408000) == 0x008000 || (address & 0xC00000) == 0xC00000;

    private static bool IsRam(uint address) => (address & 0xF88000) == 0x700000;

    private static bool IsDataRam(uint address) => (address & 0x40E000) == 0x006000 && (address & 0x0C00) != 0x0C00;

    private static bool IsIo(uint address) => (address & 0x40EC00) == 0x006C00;

    private static uint RomIndex(uint address) => (((address & 0x3F0000) >> 1) | (address & 0x7FFF)) & 0x1FFFFF;

    public byte Read(uint address, byte openBus)
    {
        if (IsRom(address))
        {
            if (!Busy)
            {
                return RomByte(RomIndex(address));
            }

            // The chip has the bus: the CPU's vectors come from the chip's registers.
            return (address & 0xBFFFC0) == 0x007FC0 ? ReadIo(0x7F40 | (address & 0x3F)) : openBus;
        }

        if (IsDataRam(address))
        {
            return (address & 0xFFF) < 0xC00 ? _dataRam[address & 0xFFF] : openBus;
        }

        return ReadIo(address);
    }

    public void Write(uint address, byte value)
    {
        if (IsDataRam(address))
        {
            if ((address & 0xFFF) < 0xC00)
            {
                _dataRam[address & 0xFFF] = value;
            }
        }
        else if (IsIo(address))
        {
            WriteIo(address, value);
        }
    }

    private byte RomByte(uint index) => _rom.Length == 0 ? (byte)0 : _rom[Spc7110.Mirror((int)index, _rom.Length)];

    private byte ReadIo(uint address)
    {
        address = 0x7C00 | (address & 0x3FF);
        switch (address)
        {
            case 0x7F40: return (byte)_dmaSource;
            case 0x7F41: return (byte)(_dmaSource >> 8);
            case 0x7F42: return (byte)(_dmaSource >> 16);
            case 0x7F43: return (byte)_dmaLength;
            case 0x7F44: return (byte)(_dmaLength >> 8);
            case 0x7F45: return (byte)_dmaTarget;
            case 0x7F46: return (byte)(_dmaTarget >> 8);
            case 0x7F47: return (byte)(_dmaTarget >> 16);
            case 0x7F48: return (byte)_cachePage;
            case 0x7F49: return (byte)_cacheBase;
            case 0x7F4A: return (byte)(_cacheBase >> 8);
            case 0x7F4B: return (byte)(_cacheBase >> 16);
            case 0x7F4C: return (byte)((_cacheLock[0] ? 1 : 0) | (_cacheLock[1] ? 2 : 0));
            case 0x7F4D: return (byte)_cachePb;
            case 0x7F4E: return (byte)(_cachePb >> 8);
            case 0x7F4F: return (byte)_cachePc;
            case 0x7F50: return (byte)(_waitRam | _waitRom << 4);
            case 0x7F51: return (byte)(_irqMasked ? 1 : 0);
            case 0x7F52: return (byte)(_romSelect ? 1 : 0);
            case 0x7F53 or 0x7F54 or 0x7F55 or 0x7F56 or 0x7F57 or 0x7F59 or 0x7F5B or 0x7F5C or 0x7F5D or 0x7F5E or 0x7F5F:
                return (byte)((_suspend ? 1 : 0) | (_i ? 2 : 0) | (Running ? 0x40 : 0) | (Busy ? 0x80 : 0));
        }

        if (address is >= 0x7F60 and <= 0x7F7F)
        {
            return _vector[address & 0x1F];
        }

        if (address is >= 0x7F80 and <= 0x7FAF or >= 0x7FC0 and <= 0x7FEF)
        {
            var at = (int)(address & 0x3F);
            return (byte)(_gpr[at / 3] >> (8 * (at % 3)));
        }

        return 0;
    }

    private void WriteIo(uint address, byte data)
    {
        address = 0x7C00 | (address & 0x3FF);
        switch (address)
        {
            case 0x7F40: _dmaSource = (_dmaSource & 0xFFFF00) | data; return;
            case 0x7F41: _dmaSource = (_dmaSource & 0xFF00FF) | (uint)data << 8; return;
            case 0x7F42: _dmaSource = (_dmaSource & 0x00FFFF) | (uint)data << 16; return;
            case 0x7F43: _dmaLength = (_dmaLength & 0xFF00) | data; return;
            case 0x7F44: _dmaLength = (_dmaLength & 0x00FF) | (uint)data << 8; return;
            case 0x7F45: _dmaTarget = (_dmaTarget & 0xFFFF00) | data; return;
            case 0x7F46: _dmaTarget = (_dmaTarget & 0xFF00FF) | (uint)data << 8; return;
            case 0x7F47:
                _dmaTarget = (_dmaTarget & 0x00FFFF) | (uint)data << 16;
                if (_halt)
                {
                    _dmaEnable = true;
                }

                return;
            case 0x7F48:
                _cachePage = data & 1;
                if (_halt)
                {
                    _pb = _cachePb;
                    _cachePreload = true;
                    _cacheEnable = true;
                }

                return;
            case 0x7F49: _cacheBase = (_cacheBase & 0xFFFF00) | data; return;
            case 0x7F4A: _cacheBase = (_cacheBase & 0xFF00FF) | (uint)data << 8; return;
            case 0x7F4B: _cacheBase = (_cacheBase & 0x00FFFF) | (uint)data << 16; return;
            case 0x7F4C:
                _cacheLock[0] = (data & 1) != 0;
                _cacheLock[1] = (data & 2) != 0;
                return;
            case 0x7F4D: _cachePb = (_cachePb & 0x7F00) | data; return;
            case 0x7F4E: _cachePb = (_cachePb & 0x00FF) | (uint)(data & 0x7F) << 8; return;
            case 0x7F4F:
                _cachePc = data;
                if (_halt)
                {
                    _halt = false;
                    _pb = _cachePb;
                    _pc = _cachePc;
                }

                return;
            case 0x7F50:
                _waitRam = data & 7;
                _waitRom = (data >> 4) & 7;
                return;
            case 0x7F51:
                _irqMasked = (data & 1) != 0;
                if (_irqMasked)
                {
                    _i = false;
                    _irqLine = false;
                }

                return;
            case 0x7F52:
                _romSelect = (data & 1) != 0;
                return;
            case 0x7F53:
                _lock = false;
                _halt = true;
                return;
            case >= 0x7F55 and <= 0x7F5C:
                _suspend = true;
                _suspendDuration = (int)(address - 0x7F55) * 32; // $7F55 is indefinite
                return;
            case 0x7F5D:
                _suspend = false;
                return;
            case 0x7F5E:
                _i = false; // the CPU's IRQ line stays as it is
                return;
        }

        if (address is >= 0x7F60 and <= 0x7F7F)
        {
            _vector[address & 0x1F] = data;
            return;
        }

        if (address is >= 0x7F80 and <= 0x7FAF or >= 0x7FC0 and <= 0x7FEF)
        {
            var at = (int)(address & 0x3F);
            var shift = 8 * (at % 3);
            _gpr[at / 3] = (_gpr[at / 3] & ~(0xFFu << shift)) | (uint)data << shift;
        }
    }

    // ---- The chip's own bus ----

    private int Wait(uint address) => IsRom(address) ? 1 + _waitRom : IsRam(address) ? 1 + _waitRam : 0;

    private byte BusRead(uint address)
    {
        address &= 0xFFFFFF;
        if (IsRom(address))
        {
            return RomByte(RomIndex(address));
        }

        if (IsRam(address))
        {
            return 0; // no save RAM on the Cx4 boards
        }

        if (IsDataRam(address))
        {
            return (address & 0xFFF) < 0xC00 ? _dataRam[address & 0xFFF] : (byte)0;
        }

        return IsIo(address) ? ReadIo(address) : (byte)0;
    }

    private void BusWrite(uint address, byte value)
    {
        address &= 0xFFFFFF;
        if (IsDataRam(address))
        {
            if ((address & 0xFFF) < 0xC00)
            {
                _dataRam[address & 0xFFF] = value;
            }
        }
        else if (IsIo(address))
        {
            WriteIo(address, value);
        }
    }

    // ---- Running ----

    public void CatchUp(long clock)
    {
        if (clock <= _clock)
        {
            return;
        }

        _budget += (clock - _clock) * _clocksPerMaster;
        _clock = clock;
        if (_budget >= 0)
        {
            _holding = false; // the CPU has caught up with the end of the last cache load or DMA
        }

        while (_budget >= 1)
        {
            var before = _budget;
            Main();
            if (_budget >= before)
            {
                _budget -= 1; // nothing to do this clock
            }
        }

    }

    public void EndFrame(long frameClocks) => _clock -= frameClocks;

    private void Step(int clocks)
    {
        _budget -= clocks;
        if (!_busEnable)
        {
            return;
        }

        if (_busPending > clocks)
        {
            _busPending -= clocks;
            return;
        }

        _busEnable = false;
        _busPending = 0;
        if (_busReading)
        {
            _busReading = false;
            _mdr = BusRead(_busAddress);
        }

        if (_busWriting)
        {
            _busWriting = false;
            BusWrite(_busAddress, (byte)_mdr);
        }
    }

    /// <summary>Waiting (locked, halted or suspended without end) uses up the time the CPU has given in one go.</summary>
    private void Idle() => Step(Math.Max(1, (int)Math.Min(_budget, int.MaxValue)));

    private void Main()
    {
        if (_lock)
        {
            Idle();
        }
        else if (_suspend)
        {
            if (_suspendDuration == 0)
            {
                Idle();
            }
            else
            {
                Step(_suspendDuration);
                _suspendDuration = 0;
                _suspend = false;
            }
        }
        else if (_cacheEnable)
        {
            Hold(() => LoadCache());
        }
        else if (_dmaEnable)
        {
            Hold(Dma);
        }
        else if (_halt)
        {
            Idle();
        }
        else
        {
            Execute();
        }
    }

    /// <summary>Runs a cache load or DMA at once; the CPU sees the chip busy until its own time reaches the end of it.</summary>
    private void Hold(Action work)
    {
        work();
        _holding = true;
    }

    private void Halt()
    {
        _halt = true;
        if (!_irqMasked)
        {
            _i = true;
            _irqLine = true;
        }
    }

    private void Execute()
    {
        if (!LoadCache())
        {
            Halt();
            return;
        }

        var opcode = _programRam[_cachePage * 256 + (int)_pc];
        Advance();
        Step(1);
        Run(opcode);
    }

    private void Advance()
    {
        _pc = (_pc + 1) & 0xFF;
        if (_pc != 0)
        {
            return;
        }

        if (_cachePage == 1)
        {
            Halt();
            return;
        }

        _cachePage = 1;
        if (_cacheLock[_cachePage])
        {
            Halt();
            return;
        }

        _pb = _p;
        if (!LoadCache())
        {
            Halt();
        }
    }

    /// <summary>
    /// Makes sure the page for the program bank is cached: the current page, else the other; otherwise loads it
    /// into the other page (or the first, if that's locked). False when both are locked.
    /// </summary>
    private bool LoadCache()
    {
        var address = (_cacheBase + _pb * 512) & 0xFFFFFF;
        if (!_cachePreload)
        {
            if (_cacheAddress[_cachePage] == address)
            {
                _cacheEnable = false;
                return true;
            }

            _cachePage ^= 1;
            if (_cacheAddress[_cachePage] == address)
            {
                _cacheEnable = false;
                return true;
            }

            if (_cacheLock[_cachePage])
            {
                _cachePage ^= 1;
            }

            if (_cacheLock[_cachePage])
            {
                _cacheEnable = false;
                return false;
            }
        }

        _cachePreload = false;
        _cacheAddress[_cachePage] = address;
        for (var offset = 0; offset < 256; offset++)
        {
            Step(Wait(address));
            var low = BusRead(address);
            address = (address + 1) & 0xFFFFFF;
            Step(Wait(address));
            var high = BusRead(address);
            address = (address + 1) & 0xFFFFFF;
            _programRam[_cachePage * 256 + offset] = (ushort)(low | high << 8);
        }

        _cacheEnable = false;
        return true;
    }

    private void Dma()
    {
        for (uint offset = 0; offset < _dmaLength; offset++)
        {
            var source = (_dmaSource + offset) & 0xFFFFFF;
            var target = (_dmaTarget + offset) & 0xFFFFFF;
            if ((IsRom(source) && IsRom(target)) || (IsRam(source) && IsRam(target)))
            {
                _lock = true; // the chip locks up
                return;
            }

            Step(Wait(source));
            var data = BusRead(source);
            Step(Wait(target));
            BusWrite(target, data);
        }

        _dmaEnable = false;
    }

    // ---- Instructions ----

    private uint ReadRegister(int address)
    {
        switch (address & 0x7F)
        {
            case 0x01: return (uint)(_mul >> 24) & 0xFFFFFF;
            case 0x02: return (uint)_mul & 0xFFFFFF;
            case 0x03: return _mdr;
            case 0x08: return _romBuffer;
            case 0x0C: return _ramBuffer;
            case 0x13: return _mar;
            case 0x1C: return _dpr;
            case 0x20: return _pc;
            case 0x28: return _p;
            case 0x2E: StartBus(reading: true, _waitRom); return 0;
            case 0x2F: StartBus(reading: true, _waitRam); return 0;
            case 0x50: return 0x000000;
            case 0x51: return 0xFFFFFF;
            case 0x52: return 0x00FF00;
            case 0x53: return 0xFF0000;
            case 0x54: return 0x00FFFF;
            case 0x55: return 0xFFFF00;
            case 0x56: return 0x800000;
            case 0x57: return 0x7FFFFF;
            case 0x58: return 0x008000;
            case 0x59: return 0x007FFF;
            case 0x5A: return 0xFF7FFF;
            case 0x5B: return 0xFFFF7F;
            case 0x5C: return 0x010000;
            case 0x5D: return 0xFEFFFF;
            case 0x5E: return 0x000100;
            case 0x5F: return 0x00FEFF;
            case >= 0x60: return _gpr[address & 15];
            default: return 0;
        }
    }

    private void WriteRegister(int address, uint data)
    {
        data &= 0xFFFFFF;
        switch (address & 0x7F)
        {
            case 0x01: _mul = (_mul & 0xFFFFFF) | (ulong)data << 24; return;
            case 0x02: _mul = (_mul & 0xFFFFFF000000) | data; return;
            case 0x03: _mdr = data; return;
            case 0x08: _romBuffer = data; return;
            case 0x0C: _ramBuffer = data; return;
            case 0x13: _mar = data; return;
            case 0x1C: _dpr = data; return;
            case 0x20: _pc = data & 0xFF; return;
            case 0x28: _p = data & 0x7FFF; return;
            case 0x2E: StartBus(reading: false, _waitRom); return;
            case 0x2F: StartBus(reading: false, _waitRam); return;
            case >= 0x60: _gpr[address & 15] = data; return;
        }
    }

    /// <summary>Registers $2E and $2F: a ROM or RAM access at MAR through MDR, done when its wait states pass.</summary>
    private void StartBus(bool reading, int wait)
    {
        _busEnable = true;
        _busReading = reading;
        _busWriting = !reading;
        _busPending = wait;
        _busAddress = _mar;
    }

    private void Push()
    {
        for (var i = 7; i > 0; i--)
        {
            _stack[i] = _stack[i - 1];
        }

        _stack[0] = (_pb << 8 | _pc) & 0x7FFFFF;
    }

    private void Pull()
    {
        var pc = _stack[0];
        for (var i = 0; i < 7; i++)
        {
            _stack[i] = _stack[i + 1];
        }

        _stack[7] = 0;
        _pb = (pc >> 8) & 0x7FFF;
        _pc = pc & 0xFF;
    }

    private uint Flags(uint value)
    {
        value &= 0xFFFFFF;
        _n = (value & 0x800000) != 0;
        _z = value == 0;
        return value;
    }

    private uint Add(uint x, uint y)
    {
        x &= 0xFFFFFF;
        y &= 0xFFFFFF;
        var z = x + y;
        _n = (z & 0x800000) != 0;
        _z = (z & 0xFFFFFF) == 0;
        _c = z > 0xFFFFFF;
        _v = (~(x ^ y) & (x ^ z) & 0x800000) != 0;
        return z & 0xFFFFFF;
    }

    private uint Sub(uint x, uint y)
    {
        x &= 0xFFFFFF;
        y &= 0xFFFFFF;
        var z = (int)x - (int)y;
        _n = (z & 0x800000) != 0;
        _z = (z & 0xFFFFFF) == 0;
        _c = z >= 0;
        _v = (~(x ^ y) & (x ^ (uint)z) & 0x800000) != 0; // as ares has it
        return (uint)z & 0xFFFFFF;
    }

    private static uint SignExtend24(uint value) => (uint)(((int)(value << 8)) >> 8);

    private void Mul(uint y)
    {
        long product = (long)(int)SignExtend24(_a) * (int)SignExtend24(y & 0xFFFFFF);
        _mul = (ulong)product & 0xFFFFFFFFFFFF;
    }

    private uint ShiftAmount(uint s)
    {
        s &= 31;
        return s > 24 ? 0 : s;
    }

    private void RdRam(int part, uint address)
    {
        address &= 0xFFF;
        if (address >= 0xC00)
        {
            address -= 0x400;
        }

        var shift = 8 * part;
        _ramBuffer = (_ramBuffer & ~(0xFFu << shift)) | (uint)_dataRam[address] << shift;
    }

    private void WrRam(int part, uint address)
    {
        address &= 0xFFF;
        if (address >= 0xC00)
        {
            address -= 0x400;
        }

        _dataRam[address] = (byte)(_ramBuffer >> (8 * part));
    }

    private void Jump(int op, bool take, bool subroutine)
    {
        if (!take)
        {
            return;
        }

        if (subroutine)
        {
            Push();
        }

        if ((op & 0x200) != 0)
        {
            _pb = _p;
        }

        _pc = (uint)(op & 0xFF);
        Step(2);
    }

    private void Run(int op)
    {
        var shift = Shifts[(op >> 8) & 3];
        var reg = op & 0x7F;
        uint imm = (uint)(op & 0xFF);
        uint shifted = (_a << shift) & 0xFFFFFF;
        switch (op >> 10)
        {
            case 0x02: Jump(op, true, false); break;
            case 0x03: Jump(op, _z, false); break;
            case 0x04: Jump(op, _c, false); break;
            case 0x05: Jump(op, _n, false); break;
            case 0x06: Jump(op, _v, false); break;
            case 0x07: // WAIT: until a bus access finishes
                if (_busEnable)
                {
                    Step(_busPending);
                }

                break;
            case 0x09: // SKIP: the next instruction when the flag equals the bit
            {
                var flag = ((op >> 8) & 3) switch { 0 => _v, 1 => _c, 2 => _z, _ => _n };
                if (flag == ((op & 1) != 0))
                {
                    Advance();
                    Step(1);
                }

                break;
            }

            case 0x0A: Jump(op, true, true); break;
            case 0x0B: Jump(op, _z, true); break;
            case 0x0C: Jump(op, _c, true); break;
            case 0x0D: Jump(op, _n, true); break;
            case 0x0E: Jump(op, _v, true); break;
            case 0x0F: // RTS
                Pull();
                Step(2);
                break;
            case 0x10: _mar = (_mar + 1) & 0xFFFFFF; break;
            case 0x12: Sub(ReadRegister(reg), shifted); break;  // CMPR A<<s,reg
            case 0x13: Sub(imm, shifted); break;                 // CMPR A<<s,imm
            case 0x14: Sub(shifted, ReadRegister(reg)); break;   // CMP
            case 0x15: Sub(shifted, imm); break;
            case 0x16:
                switch ((op >> 8) & 3)
                {
                    case 1: _a = Flags((uint)(sbyte)_a); break;  // SXB
                    case 2: _a = Flags((uint)(short)_a); break;  // SXW
                }

                break;
            case 0x18:
                switch ((op >> 8) & 3)
                {
                    case 0: _a = ReadRegister(reg); break;
                    case 1: _mdr = ReadRegister(reg); break;
                    case 2: _mar = ReadRegister(reg); break;
                    default: _p = _gpr[op & 15] & 0x7FFF; break;
                }

                break;
            case 0x19:
                switch ((op >> 8) & 3)
                {
                    case 0: _a = imm; break;
                    case 1: _mdr = imm; break;
                    case 2: _mar = imm; break;
                    default: _p = imm; break;
                }

                break;
            case 0x1A:
                if (((op >> 8) & 3) != 3)
                {
                    RdRam((op >> 8) & 3, _a);
                }

                break;
            case 0x1B:
                if (((op >> 8) & 3) != 3)
                {
                    RdRam((op >> 8) & 3, _dpr + imm);
                }

                break;
            case 0x1C: _romBuffer = _dataRom[_a & 0x3FF]; break;
            case 0x1D: _romBuffer = _dataRom[op & 0x3FF]; break;
            case 0x1F:
                switch ((op >> 8) & 3)
                {
                    case 0: _p = (_p & 0x7F00) | imm; break;                  // LD PL
                    case 1: _p = (_p & 0x00FF) | (uint)(op & 0x7F) << 8; break; // LD PH
                }

                break;
            case 0x20: _a = Add(shifted, ReadRegister(reg)); break;
            case 0x21: _a = Add(shifted, imm); break;
            case 0x22: _a = Sub(ReadRegister(reg), shifted); break;   // SUBR
            case 0x23: _a = Sub(imm, shifted); break;
            case 0x24: _a = Sub(shifted, ReadRegister(reg)); break;   // SUB
            case 0x25: _a = Sub(shifted, imm); break;
            case 0x26: Mul(ReadRegister(reg)); break;
            case 0x27: Mul(imm); break;
            case 0x28: _a = Flags(~shifted ^ ReadRegister(reg)); break; // XNOR
            case 0x29: _a = Flags(~shifted ^ imm); break;
            case 0x2A: _a = Flags(shifted ^ ReadRegister(reg)); break;
            case 0x2B: _a = Flags(shifted ^ imm); break;
            case 0x2C: _a = Flags(shifted & ReadRegister(reg)); break;
            case 0x2D: _a = Flags(shifted & imm); break;
            case 0x2E: _a = Flags(shifted | ReadRegister(reg)); break;
            case 0x2F: _a = Flags(shifted | imm); break;
            case 0x30: _a = Flags(_a >> (int)ShiftAmount(ReadRegister(reg))); break;      // SHR
            case 0x31: _a = Flags(_a >> (int)ShiftAmount((uint)op)); break;
            case 0x32: _a = Flags((uint)((int)SignExtend24(_a) >> (int)ShiftAmount(ReadRegister(reg)))); break; // ASR
            case 0x33: _a = Flags((uint)((int)SignExtend24(_a) >> (int)ShiftAmount((uint)op))); break;
            case 0x34: _a = Flags(Ror(_a, ShiftAmount(ReadRegister(reg)))); break;
            case 0x35: _a = Flags(Ror(_a, ShiftAmount((uint)op))); break;
            case 0x36: _a = Flags(_a << (int)ShiftAmount(ReadRegister(reg))); break;      // SHL
            case 0x37: _a = Flags(_a << (int)ShiftAmount((uint)op)); break;
            case 0x38:
                switch ((op >> 8) & 3)
                {
                    case 0: WriteRegister(reg, _a); break;
                    case 1: WriteRegister(reg, _mdr); break;
                }

                break;
            case 0x3A:
                if (((op >> 8) & 3) != 3)
                {
                    WrRam((op >> 8) & 3, _a);
                }

                break;
            case 0x3B:
                if (((op >> 8) & 3) != 3)
                {
                    WrRam((op >> 8) & 3, _dpr + imm);
                }

                break;
            case 0x3C: (_a, _gpr[op & 15]) = (_gpr[op & 15], _a); break; // SWAP
            case 0x3E: // CLEAR
                _a = 0;
                _p = 0;
                _ramBuffer = 0;
                _dpr = 0;
                break;
            case 0x3F: Halt(); break;

            // $00, $01, $08, $11, $17, $1E, $39, $3D: no operation.
        }
    }

    private static uint Ror(uint a, uint s) => s == 0 ? a : ((a >> (int)s) | (a << (int)(24 - s))) & 0xFFFFFF;

    // ---- Power and state ----

    public void Power()
    {
        Array.Clear(_dataRam);
        Array.Clear(_programRam);
        Reset();
        _clock = 0;
        _budget = 0;
    }

    public void Reset()
    {
        _pb = _pc = 0;
        _n = _z = _c = _v = _i = false;
        _a = _p = _mdr = _romBuffer = _ramBuffer = _mar = _dpr = 0;
        _mul = 0;
        Array.Clear(_gpr);
        Array.Clear(_stack);
        Array.Clear(_vector);
        _lock = false;
        _halt = true;
        _irqMasked = false;
        _romSelect = true;
        _waitRom = _waitRam = 3;
        _suspend = false;
        _suspendDuration = 0;
        _cacheEnable = _cachePreload = false;
        _cachePage = 0;
        _cacheLock[0] = _cacheLock[1] = false;
        _cacheAddress[0] = _cacheAddress[1] = 0;
        _cacheBase = _cachePb = _cachePc = 0;
        _dmaEnable = false;
        _dmaSource = _dmaTarget = _dmaLength = 0;
        _busEnable = _busReading = _busWriting = false;
        _busPending = 0;
        _busAddress = 0;
        _irqLine = false;
        _holding = false;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_dataRam);
        foreach (var word in _programRam)
        {
            writer.WriteUInt16(word);
        }

        foreach (var value in _gpr)
        {
            writer.WriteUInt32(value);
        }

        foreach (var value in _stack)
        {
            writer.WriteUInt32(value);
        }

        writer.WriteBytes(_vector);
        writer.WriteInt64(_clock);
        writer.WriteInt64(BitConverter.DoubleToInt64Bits(_budget));
        writer.WriteBool(_holding);
        foreach (var value in new[] { _pb, _pc, _a, _p, _mdr, _romBuffer, _ramBuffer, _mar, _dpr })
        {
            writer.WriteUInt32(value);
        }

        writer.WriteUInt64(_mul);
        foreach (var flag in new[] { _n, _z, _c, _v, _i, _lock, _halt, _irqMasked, _romSelect, _suspend, _cacheEnable, _cachePreload,
                     _cacheLock[0], _cacheLock[1], _dmaEnable, _busEnable, _busReading, _busWriting, _irqLine })
        {
            writer.WriteBool(flag);
        }

        writer.WriteInt32(_waitRom);
        writer.WriteInt32(_waitRam);
        writer.WriteInt32(_suspendDuration);
        writer.WriteInt32(_cachePage);
        writer.WriteUInt32(_cacheAddress[0]);
        writer.WriteUInt32(_cacheAddress[1]);
        foreach (var value in new[] { _cacheBase, _cachePb, _cachePc, _dmaSource, _dmaTarget, _dmaLength, _busAddress })
        {
            writer.WriteUInt32(value);
        }

        writer.WriteInt32(_busPending);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_dataRam);
        for (var i = 0; i < _programRam.Length; i++)
        {
            _programRam[i] = reader.ReadUInt16();
        }

        for (var i = 0; i < _gpr.Length; i++)
        {
            _gpr[i] = reader.ReadUInt32() & 0xFFFFFF;
        }

        for (var i = 0; i < _stack.Length; i++)
        {
            _stack[i] = reader.ReadUInt32() & 0x7FFFFF;
        }

        reader.ReadBytes(_vector);
        _clock = reader.ReadInt64();
        _budget = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        _holding = reader.ReadBool();
        _pb = reader.ReadUInt32() & 0x7FFF;
        _pc = reader.ReadUInt32() & 0xFF;
        _a = reader.ReadUInt32() & 0xFFFFFF;
        _p = reader.ReadUInt32() & 0x7FFF;
        _mdr = reader.ReadUInt32() & 0xFFFFFF;
        _romBuffer = reader.ReadUInt32() & 0xFFFFFF;
        _ramBuffer = reader.ReadUInt32() & 0xFFFFFF;
        _mar = reader.ReadUInt32() & 0xFFFFFF;
        _dpr = reader.ReadUInt32() & 0xFFFFFF;
        _mul = reader.ReadUInt64() & 0xFFFFFFFFFFFF;
        _n = reader.ReadBool();
        _z = reader.ReadBool();
        _c = reader.ReadBool();
        _v = reader.ReadBool();
        _i = reader.ReadBool();
        _lock = reader.ReadBool();
        _halt = reader.ReadBool();
        _irqMasked = reader.ReadBool();
        _romSelect = reader.ReadBool();
        _suspend = reader.ReadBool();
        _cacheEnable = reader.ReadBool();
        _cachePreload = reader.ReadBool();
        _cacheLock[0] = reader.ReadBool();
        _cacheLock[1] = reader.ReadBool();
        _dmaEnable = reader.ReadBool();
        _busEnable = reader.ReadBool();
        _busReading = reader.ReadBool();
        _busWriting = reader.ReadBool();
        _irqLine = reader.ReadBool();
        _waitRom = reader.ReadInt32() & 7;
        _waitRam = reader.ReadInt32() & 7;
        _suspendDuration = reader.ReadInt32() & 0xFF;
        _cachePage = reader.ReadInt32() & 1;
        _cacheAddress[0] = reader.ReadUInt32() & 0xFFFFFF;
        _cacheAddress[1] = reader.ReadUInt32() & 0xFFFFFF;
        _cacheBase = reader.ReadUInt32() & 0xFFFFFF;
        _cachePb = reader.ReadUInt32() & 0x7FFF;
        _cachePc = reader.ReadUInt32() & 0xFF;
        _dmaSource = reader.ReadUInt32() & 0xFFFFFF;
        _dmaTarget = reader.ReadUInt32() & 0xFFFFFF;
        _dmaLength = reader.ReadUInt32() & 0xFFFF;
        _busAddress = reader.ReadUInt32() & 0xFFFFFF;
        _busPending = reader.ReadInt32() & 15;
        reader.EndChunk();
    }
}
