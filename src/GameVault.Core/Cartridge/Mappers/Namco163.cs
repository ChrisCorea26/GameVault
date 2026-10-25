using GameVault.Core.Audio;
using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Mapper 19, Namco 129/163 (NESdev Wiki: "Namco 163"): 8 KB PRG and 1 KB CHR banks, nametables from
/// CIRAM or CHR-ROM, a 15-bit CPU-cycle IRQ counter, and 128 bytes of internal RAM that also hold the
/// wavetable sound channels.
/// </summary>
public sealed class Namco163 : MapperBase
{
    private readonly byte[] _chr = new byte[8];
    private readonly byte[] _nt = [0xE0, 0xE0, 0xE1, 0xE1];
    private readonly byte[] _prg = new byte[3];
    private readonly Namco163Audio _audio = new();
    private byte _chrRamControl;
    private ushort _irqCounter;
    private bool _irqEnabled;
    private bool _irq;

    public Namco163(INesRom rom)
        : base(rom, "N163") => UpdateBanks();

    public override bool HasCycleHooks => true;

    public override bool IrqPending => _irq;

    public override IExpansionAudio ExpansionAudio => _audio;

    public override Span<byte> BatteryRam => PrgRam; // the 128-byte sound RAM is battery-backed on some boards too; PRG-RAM is what games save to

    public override bool HasNametableHooks => true;

    public override void OnCpuCycle()
    {
        if (_irqEnabled && _irqCounter < 0x7FFF && ++_irqCounter == 0x7FFF)
        {
            _irq = true;
        }
    }

    public override byte CpuRead(ushort address, byte openBus) => address switch
    {
        >= 0x4800 and < 0x5000 => _audio.ReadData(),
        >= 0x5000 and < 0x5800 => (byte)_irqCounter,
        >= 0x5800 and < 0x6000 => (byte)((_irqCounter >> 8) | (_irqEnabled ? 0x80 : 0)),
        _ => base.CpuRead(address, openBus),
    };

    public override void CpuWrite(ushort address, byte value)
    {
        switch (address)
        {
            case >= 0x4800 and < 0x5000:
                _audio.WriteData(value);
                return;
            case >= 0x5000 and < 0x5800:
                _irqCounter = (ushort)((_irqCounter & 0x7F00) | value);
                _irq = false;
                return;
            case >= 0x5800 and < 0x6000:
                _irqCounter = (ushort)((_irqCounter & 0x00FF) | ((value & 0x7F) << 8));
                _irqEnabled = (value & 0x80) != 0;
                _irq = false;
                return;
            default:
                base.CpuWrite(address, value);
                return;
        }
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        switch (address & 0xF800)
        {
            case < 0xC000:
                _chr[(address - 0x8000) >> 11] = value;
                break;
            case < 0xE000:
                _nt[(address - 0xC000) >> 11] = value;
                break;
            case 0xE000:
                _prg[0] = (byte)(value & 0x3F);
                _audio.Enabled = (value & 0x40) == 0;
                break;
            case 0xE800:
                _prg[1] = (byte)(value & 0x3F);
                _chrRamControl = (byte)(value & 0xC0);
                break;
            case 0xF000:
                _prg[2] = (byte)(value & 0x3F);
                break;
            default: // $F800: sound RAM address
                _audio.SetAddress(value);
                return;
        }

        UpdateBanks();
    }

    /// <summary>Values $E0 and up select a CIRAM page (bit 0); anything lower reads CHR-ROM as a nametable.</summary>
    public override byte ReadNametable(ushort address, byte[] ciram)
    {
        var bank = _nt[(address >> 10) & 3];
        return bank >= 0xE0
            ? ciram[((bank & 1) << 10) | (address & 0x3FF)]
            : Chr[((bank % Math.Max(1, Chr.Length >> 10)) << 10) | (address & 0x3FF)];
    }

    public override void WriteNametable(ushort address, byte value, byte[] ciram)
    {
        var bank = _nt[(address >> 10) & 3];
        if (bank >= 0xE0)
        {
            ciram[((bank & 1) << 10) | (address & 0x3FF)] = value;
        }
    }

    private void UpdateBanks()
    {
        SetPrg8K(0, _prg[0]);
        SetPrg8K(1, _prg[1]);
        SetPrg8K(2, _prg[2]);
        SetPrg8K(3, -1);
        for (var i = 0; i < 8; i++)
        {
            SetChr1K(i, _chr[i]); // banks $E0+ as CIRAM in pattern space are not emulated (rare)
        }
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_chr);
        writer.WriteBytes(_nt);
        writer.WriteBytes(_prg);
        writer.WriteByte(_chrRamControl);
        writer.WriteUInt16(_irqCounter);
        writer.WriteBool(_irqEnabled);
        writer.WriteBool(_irq);
        _audio.Save(writer);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_chr);
        reader.ReadBytes(_nt);
        reader.ReadBytes(_prg);
        _chrRamControl = reader.ReadByte();
        _irqCounter = reader.ReadUInt16();
        _irqEnabled = reader.ReadBool();
        _irq = reader.ReadBool();
        _audio.Load(reader);
        UpdateBanks();
    }
}

/// <summary>
/// Namco 163 wavetable sound (NESdev Wiki: "Namco 163 audio"): up to 8 channels whose registers and 4-bit
/// samples live in 128 bytes of RAM. One channel is updated every 15 CPU cycles; the chip time-multiplexes
/// them, which is approximated here by averaging the active channels.
/// </summary>
public sealed class Namco163Audio : IExpansionAudio
{
    private const float Scale = 0.0075f;

    private readonly byte[] _ram = new byte[128];
    private readonly int[] _outputs = new int[8];
    private byte _address;
    private bool _autoIncrement;
    private int _divider;
    private int _channel;

    public bool Enabled { get; set; } = true;

    public float Output { get; private set; }

    public void SetAddress(byte value)
    {
        _address = (byte)(value & 0x7F);
        _autoIncrement = (value & 0x80) != 0;
    }

    public byte ReadData()
    {
        var value = _ram[_address];
        Advance();
        return value;
    }

    public void WriteData(byte value)
    {
        _ram[_address] = value;
        Advance();
    }

    public void Clock()
    {
        if (!Enabled || ++_divider < 15)
        {
            return;
        }

        _divider = 0;
        var active = ((_ram[0x7F] >> 4) & 7) + 1;
        _channel = _channel <= 7 - active ? 7 : _channel - 1; // channels 7, 6, … down to 8 - active
        UpdateChannel(_channel);

        var sum = 0;
        for (var c = 8 - active; c < 8; c++)
        {
            sum += _outputs[c];
        }

        Output = sum * Scale / active * 2f;
    }

    private void UpdateChannel(int c)
    {
        var b = 0x40 + c * 8;
        var frequency = _ram[b] | (_ram[b + 2] << 8) | ((_ram[b + 4] & 0x03) << 16);
        var phase = _ram[b + 1] | (_ram[b + 3] << 8) | (_ram[b + 5] << 16);
        var length = 256 - (_ram[b + 4] & 0xFC);
        phase = (phase + frequency) % (length << 16);
        _ram[b + 1] = (byte)phase;
        _ram[b + 3] = (byte)(phase >> 8);
        _ram[b + 5] = (byte)(phase >> 16);

        var index = ((phase >> 16) + _ram[b + 6]) & 0xFF;
        var sample = (_ram[index >> 1] >> ((index & 1) * 4)) & 0x0F;
        _outputs[c] = (sample - 8) * (_ram[b + 7] & 0x0F);
    }

    private void Advance()
    {
        if (_autoIncrement)
        {
            _address = (byte)((_address + 1) & 0x7F);
        }
    }

    public void Save(StateWriter writer)
    {
        writer.WriteBytes(_ram);
        writer.WriteByte(_address);
        writer.WriteBool(_autoIncrement);
        writer.WriteInt32(_divider);
        writer.WriteInt32(_channel);
        writer.WriteBool(Enabled);
    }

    public void Load(StateReader reader)
    {
        reader.ReadBytes(_ram);
        _address = reader.ReadByte();
        _autoIncrement = reader.ReadBool();
        _divider = reader.ReadInt32();
        _channel = reader.ReadInt32();
        Enabled = reader.ReadBool();
    }
}
