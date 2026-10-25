using GameVault.Core.Audio;
using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Mapper 69, Sunsoft FME-7 / 5A / 5B (NESdev Wiki: "Sunsoft FME-7"): a command/parameter register pair for
/// 1 KB CHR and 8 KB PRG banking (including ROM or RAM at $6000), mirroring, and a 16-bit CPU-cycle IRQ
/// counter. The 5B adds three square-wave channels (<see cref="Sunsoft5BAudio"/>).
/// </summary>
public sealed class Fme7 : MapperBase
{
    private readonly byte[] _chr = new byte[8];
    private readonly byte[] _prg = new byte[4]; // $6000, $8000, $A000, $C000
    private readonly Sunsoft5BAudio _audio = new();
    private byte _command;
    private bool _irqEnabled;
    private bool _counterEnabled;
    private ushort _counter;
    private bool _irq;

    public Fme7(INesRom rom)
        : base(rom, "FME7")
    {
        _prg[1] = 0;
        _prg[2] = 1;
        _prg[3] = 2;
        UpdateBanks();
    }

    public override bool HasCycleHooks => true;

    public override bool IrqPending => _irq;

    public override IExpansionAudio ExpansionAudio => _audio;

    public override void OnCpuCycle()
    {
        if (_counterEnabled)
        {
            if (_counter == 0 && _irqEnabled)
            {
                _irq = true;
            }

            _counter--;
        }
    }

    public override byte CpuRead(ushort address, byte openBus)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            var bank = _prg[0];
            var isRam = (bank & 0x40) != 0;
            if (!isRam)
            {
                return PrgRom[((bank & 0x3F) % Math.Max(1, PrgBanks8K) << 13) + (address & 0x1FFF)];
            }

            return (bank & 0x80) != 0 ? PrgRam[address & 0x1FFF] : openBus;
        }

        return base.CpuRead(address, openBus);
    }

    public override void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            if ((_prg[0] & 0xC0) == 0xC0)
            {
                PrgRam[address & 0x1FFF] = value;
            }

            return;
        }

        base.CpuWrite(address, value);
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        switch (address & 0xE000)
        {
            case 0x8000:
                _command = (byte)(value & 0x0F);
                break;
            case 0xA000:
                Execute(value);
                break;
            case 0xC000:
                _audio.Select(value);
                break;
            default:
                _audio.Write(value);
                break;
        }
    }

    private void Execute(byte value)
    {
        switch (_command)
        {
            case < 8:
                _chr[_command] = value;
                break;
            case 8:
                _prg[0] = value;
                break;
            case 9 or 10 or 11:
                _prg[_command - 8] = (byte)(value & 0x3F);
                break;
            case 12:
                Mirroring = (value & 3) switch
                {
                    0 => Mirroring.Vertical,
                    1 => Mirroring.Horizontal,
                    2 => Mirroring.SingleScreenA,
                    _ => Mirroring.SingleScreenB,
                };
                return;
            case 13:
                _irqEnabled = (value & 0x01) != 0;
                _counterEnabled = (value & 0x80) != 0;
                _irq = false; // any write acknowledges
                return;
            case 14:
                _counter = (ushort)((_counter & 0xFF00) | value);
                return;
            default:
                _counter = (ushort)((_counter & 0x00FF) | (value << 8));
                return;
        }

        UpdateBanks();
    }

    private void UpdateBanks()
    {
        SetPrg8K(0, _prg[1]);
        SetPrg8K(1, _prg[2]);
        SetPrg8K(2, _prg[3]);
        SetPrg8K(3, -1);
        for (var i = 0; i < 8; i++)
        {
            SetChr1K(i, _chr[i]);
        }
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_chr);
        writer.WriteBytes(_prg);
        writer.WriteByte(_command);
        writer.WriteBool(_irqEnabled);
        writer.WriteBool(_counterEnabled);
        writer.WriteUInt16(_counter);
        writer.WriteBool(_irq);
        writer.WriteByte((byte)Mirroring);
        _audio.SaveState(writer);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_chr);
        reader.ReadBytes(_prg);
        _command = reader.ReadByte();
        _irqEnabled = reader.ReadBool();
        _counterEnabled = reader.ReadBool();
        _counter = reader.ReadUInt16();
        _irq = reader.ReadBool();
        Mirroring = (Mirroring)reader.ReadByte();
        _audio.LoadState(reader);
        UpdateBanks();
    }
}

/// <summary>
/// The Sunsoft 5B's three square-wave tone channels (a YM2149F core; NESdev Wiki: "Sunsoft 5B audio").
/// Each tone toggles every 16 × period CPU cycles; volume is logarithmic in 3 dB steps. The noise generator
/// and envelope are not emulated yet (no known licensed game relies on them).
/// </summary>
public sealed class Sunsoft5BAudio : IExpansionAudio, IStateful
{
    private static readonly StateTag Tag = StateTag.From("S5BA");

    /// <summary>Loudest channel relative to the 2A03 mix; three channels together sit near the 2A03's own level.</summary>
    private const float ChannelScale = 0.08f;

    private static readonly float[] Volume = BuildVolumeTable();

    private readonly byte[] _registers = new byte[16];
    private readonly int[] _counters = new int[3];
    private readonly bool[] _high = new bool[3];
    private int _prescaler;
    private byte _select;

    public float Output { get; private set; }

    public void Select(byte value) => _select = (byte)(value & 0x0F);

    public void Write(byte value) => _registers[_select] = value;

    public void Clock()
    {
        if (++_prescaler < 16)
        {
            return;
        }

        _prescaler = 0;
        var output = 0f;
        for (var ch = 0; ch < 3; ch++)
        {
            var period = _registers[ch * 2] | ((_registers[ch * 2 + 1] & 0x0F) << 8);
            if (++_counters[ch] >= Math.Max(1, period))
            {
                _counters[ch] = 0;
                _high[ch] = !_high[ch];
            }

            var toneOn = (_registers[7] & (1 << ch)) == 0;
            var level = _registers[8 + ch] & 0x0F;
            if (!toneOn || _high[ch])
            {
                output += Volume[level];
            }
        }

        Output = output * ChannelScale;
    }

    private static float[] BuildVolumeTable()
    {
        var table = new float[16];
        for (var i = 1; i < 16; i++)
        {
            table[i] = MathF.Pow(10f, -3f * (15 - i) / 20f); // 3 dB per step
        }

        return table;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_registers);
        foreach (var counter in _counters)
        {
            writer.WriteInt32(counter);
        }

        foreach (var high in _high)
        {
            writer.WriteBool(high);
        }

        writer.WriteInt32(_prescaler);
        writer.WriteByte(_select);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_registers);
        for (var i = 0; i < 3; i++)
        {
            _counters[i] = reader.ReadInt32();
        }

        for (var i = 0; i < 3; i++)
        {
            _high[i] = reader.ReadBool();
        }

        _prescaler = reader.ReadInt32();
        _select = reader.ReadByte();
        reader.EndChunk();
    }
}
