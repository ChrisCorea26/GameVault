using GameVault.Core.Cpu.Arm;
using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>
/// The ST018 (snes-chips-3.md §4), Seta's chip in Hayazashi Nidan Morita Shogi 2: an ARM60 (ARMv3) at 21.44 MHz with
/// 128 KB of program ROM and 32 KB of data ROM built in (the player's <c>st018.rom</c>) and 16 KB of RAM, joined to
/// the SNES by a one-byte mailbox each way.
/// </summary>
/// <remarks>
/// <para>The SNES side, $00–$3F/$80–$BF $3800–$38FF (A1, A2 and A8–A15 decoded; $3800–$3807 mirrored): reading $3800
/// takes the ARM's byte; writing $3802 gives it one, and reading $3802 clears its signal; $3804 reads the status
/// (bit 0: a byte from the ARM waits, bit 2: the signal, bit 3: the SNES's byte is still unread, bit 7: ready) and its
/// bit 0 written holds the ARM in reset. Releasing reset takes 65,536 clocks before ready.</para>
/// <para>The ARM's map by the address's top three bits: $0 the program ROM, $A the data ROM, $E the RAM; $4 the
/// bridge ($40000000 the byte to the SNES, $40000010 the SNES's byte (writing it raises the signal), $40000020 the
/// status, $40000020–$4000002C the timer latch and start); $6 reads $40404001; the rest read the last instruction
/// fetched. Behaviour follows ares' armdsp (ISC licence); ares notes the timings aren't verified.</para>
/// </remarks>
public sealed class St018 : ISnesCoprocessor, IArmBus
{
    public const int ProgramRomSize = 128 * 1024, DataRomSize = 32 * 1024, DumpSize = ProgramRomSize + DataRomSize;
    private const double Hz = 21_440_000;
    private const int ResetDelay = 65_536;

    private static readonly StateTag Tag = StateTag.From("ST18");

    private readonly byte[] _programRom;
    private readonly byte[] _dataRom;
    private readonly byte[] _ram = new byte[16 * 1024];
    private readonly double _clocksPerMaster;

    private long _clock;
    private double _budget;
    private int _resetDelay;

    private bool _cpuToArmReady, _armToCpuReady;
    private byte _cpuToArm, _armToCpu;
    private uint _timer, _timerLatch;
    private bool _reset, _ready, _signal;

    /// <param name="dump">The player's 160 KB <c>st018.rom</c>: the program ROM, then the data ROM.</param>
    public St018(byte[] dump, double masterClock)
    {
        _programRom = dump[..ProgramRomSize];
        _dataRom = dump[ProgramRomSize..DumpSize];
        _clocksPerMaster = Hz / masterClock;
        Cpu = new ArmCpu(this);
    }

    public string Name => "ST018";

    public bool Irq => false;

    public ArmCpu Cpu { get; }

    /// <summary>The ARM's RAM (for tests).</summary>
    public Span<byte> Ram => _ram;

    public bool Ready => _ready;

    // ---- The SNES side ----

    public bool Claims(uint address) => (address & 0x40FF00) == 0x003800;

    public byte Read(uint address, byte openBus)
    {
        switch (address & 0xFF06)
        {
            case 0x3800:
                if (_armToCpuReady)
                {
                    _armToCpuReady = false;
                    return _armToCpu;
                }

                return 0;
            case 0x3802:
                _signal = false;
                return 0;
            case 0x3804:
                return Status;
            default:
                return 0;
        }
    }

    public void Write(uint address, byte value)
    {
        switch (address & 0xFF06)
        {
            case 0x3802:
                _cpuToArmReady = true;
                _cpuToArm = value;
                break;
            case 0x3804:
                var hold = (value & 1) != 0;
                if (!_reset && hold)
                {
                    ResetArm();
                }

                _reset = hold;
                break;
        }
    }

    private byte Status => (byte)((_armToCpuReady ? 1 : 0) | (_signal ? 4 : 0) | (_cpuToArmReady ? 8 : 0) | (_ready ? 0x80 : 0));

    // ---- The ARM's side ----

    uint IArmBus.ReadWord(uint address)
    {
        switch (address >> 29)
        {
            case 0: return Word(_programRom, address & 0x1FFFF);
            case 2: return Bridge(address);
            case 3: return 0x40404001;
            case 5: return Word(_dataRom, address & 0x7FFF);
            case 7: return Word(_ram, address & 0x3FFF);
            default: return Cpu.LastFetch;
        }
    }

    byte IArmBus.ReadByte(uint address)
    {
        switch (address >> 29)
        {
            case 0: return _programRom[address & 0x1FFFF];
            case 2: return (byte)Bridge(address);
            case 3: return 0x01;
            case 5: return _dataRom[address & 0x7FFF];
            case 7: return _ram[address & 0x3FFF];
            default: return (byte)Cpu.LastFetch;
        }
    }

    void IArmBus.WriteWord(uint address, uint value)
    {
        if (address >> 29 == 7)
        {
            var at = (int)(address & 0x3FFC);
            _ram[at] = (byte)value;
            _ram[at + 1] = (byte)(value >> 8);
            _ram[at + 2] = (byte)(value >> 16);
            _ram[at + 3] = (byte)(value >> 24);
        }
        else if (address >> 29 == 2)
        {
            WriteBridge(address, (byte)value);
        }
    }

    void IArmBus.WriteByte(uint address, byte value)
    {
        if (address >> 29 == 7)
        {
            _ram[address & 0x3FFF] = value;
        }
        else if (address >> 29 == 2)
        {
            WriteBridge(address, value);
        }
    }

    void IArmBus.Clock(int clocks)
    {
        _budget -= clocks;
        if (_timer != 0)
        {
            _timer = (uint)Math.Max(0, (long)_timer - clocks);
        }
    }

    private static uint Word(byte[] memory, uint address)
    {
        var at = (int)(address & ~3u);
        return (uint)(memory[at] | memory[at + 1] << 8 | memory[at + 2] << 16 | memory[at + 3] << 24);
    }

    private uint Bridge(uint address)
    {
        switch (address & 0xE000003F)
        {
            case 0x40000010:
                if (_cpuToArmReady)
                {
                    _cpuToArmReady = false;
                    return _cpuToArm;
                }

                return 0;
            case 0x40000020:
                return Status;
            default:
                return 0;
        }
    }

    private void WriteBridge(uint address, byte value)
    {
        switch (address & 0xE000003F)
        {
            case 0x40000000:
                _armToCpuReady = true;
                _armToCpu = value;
                break;
            case 0x40000010:
                _signal = true;
                break;
            case 0x40000020:
                _timerLatch = (_timerLatch & 0xFFFF00) | value;
                break;
            case 0x40000024:
                _timerLatch = (_timerLatch & 0xFF00FF) | (uint)value << 8;
                break;
            case 0x40000028:
                _timerLatch = (_timerLatch & 0x00FFFF) | (uint)value << 16;
                break;
            case 0x4000002C:
                _timer = _timerLatch;
                break;
        }
    }

    // ---- Running ----

    private void ResetArm()
    {
        Cpu.Reset();
        _ready = false;
        _signal = false;
        _timer = _timerLatch = 0;
        _cpuToArmReady = _armToCpuReady = false;
        _resetDelay = ResetDelay;
    }

    public void CatchUp(long clock)
    {
        if (clock <= _clock)
        {
            return;
        }

        _budget += (clock - _clock) * _clocksPerMaster;
        _clock = clock;
        while (_budget > 0)
        {
            if (_reset)
            {
                _budget = 0; // held in reset
                break;
            }

            if (!_ready)
            {
                // The reset sequence's delay, then ready.
                var wait = (int)Math.Min(_resetDelay, Math.Ceiling(_budget));
                ((IArmBus)this).Clock(wait);
                _resetDelay -= wait;
                if (_resetDelay <= 0)
                {
                    _ready = true;
                }

                continue;
            }

            Cpu.Step();
        }
    }

    public void EndFrame(long frameClocks) => _clock -= frameClocks;

    public void Power()
    {
        Array.Clear(_ram);
        _reset = false;
        _clock = 0;
        _budget = 0;
        ResetArm();
    }

    public void Reset() => Power();

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_ram);
        Cpu.SaveState(writer);
        writer.WriteInt64(_clock);
        writer.WriteInt64(BitConverter.DoubleToInt64Bits(_budget));
        writer.WriteInt32(_resetDelay);
        writer.WriteBool(_cpuToArmReady);
        writer.WriteBool(_armToCpuReady);
        writer.WriteByte(_cpuToArm);
        writer.WriteByte(_armToCpu);
        writer.WriteUInt32(_timer);
        writer.WriteUInt32(_timerLatch);
        writer.WriteBool(_reset);
        writer.WriteBool(_ready);
        writer.WriteBool(_signal);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_ram);
        Cpu.LoadState(reader);
        _clock = reader.ReadInt64();
        _budget = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        _resetDelay = Math.Clamp(reader.ReadInt32(), 0, ResetDelay);
        _cpuToArmReady = reader.ReadBool();
        _armToCpuReady = reader.ReadBool();
        _cpuToArm = reader.ReadByte();
        _armToCpu = reader.ReadByte();
        _timer = reader.ReadUInt32() & 0xFFFFFF;
        _timerLatch = reader.ReadUInt32() & 0xFFFFFF;
        _reset = reader.ReadBool();
        _ready = reader.ReadBool();
        _signal = reader.ReadBool();
        reader.EndChunk();
    }
}
