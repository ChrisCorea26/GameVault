using GameVault.Core.Audio;
using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Konami VRC6, mappers 24 (VRC6a) and 26 (VRC6b, A0/A1 swapped) (NESdev Wiki: "VRC6"): 16 KB + 8 KB PRG,
/// eight 1 KB CHR banks (mode 0), mirroring, VRC IRQ, and two pulse channels plus a sawtooth.
/// </summary>
public sealed class Vrc6 : MapperBase
{
    private readonly bool _swapLines;
    private readonly byte[] _chr = new byte[8];
    private readonly VrcIrq _irq = new();
    private readonly Vrc6Audio _audio = new();
    private byte _prg16;
    private byte _prg8;
    private byte _control;

    public Vrc6(INesRom rom)
        : base(rom, "VRC6")
    {
        _swapLines = rom.Mapper == 26;
        UpdateBanks();
    }

    public override bool HasCycleHooks => true;

    public override bool IrqPending => _irq.Pending;

    public override IExpansionAudio ExpansionAudio => _audio;

    public override void OnCpuCycle() => _irq.Clock();

    protected override void WriteRegister(ushort address, byte value)
    {
        var sub = _swapLines ? ((address & 1) << 1) | ((address >> 1) & 1) : address & 3;
        switch (address & 0xF000)
        {
            case 0x8000:
                _prg16 = (byte)(value & 0x0F);
                break;
            case 0x9000 or 0xA000 or 0xB000 when !((address & 0xF000) == 0xB000 && sub == 3):
                _audio.Write((address & 0xF000) >> 12, sub, value);
                return;
            case 0xB000:
                _control = value;
                PrgRamReadable = PrgRamWritable = (value & 0x80) != 0;
                Mirroring = ((value >> 2) & 3) switch
                {
                    0 => Mirroring.Vertical,
                    1 => Mirroring.Horizontal,
                    2 => Mirroring.SingleScreenA,
                    _ => Mirroring.SingleScreenB,
                };
                return;
            case 0xC000:
                _prg8 = (byte)(value & 0x1F);
                break;
            case 0xD000:
                _chr[sub] = value;
                break;
            case 0xE000:
                _chr[4 + sub] = value;
                break;
            default: // $F000
                switch (sub)
                {
                    case 0:
                        _irq.Latch = value;
                        break;
                    case 1:
                        _irq.WriteControl(value);
                        break;
                    case 2:
                        _irq.Acknowledge();
                        break;
                }

                return;
        }

        UpdateBanks();
    }

    private void UpdateBanks()
    {
        SetPrg16K(0, _prg16);
        SetPrg8K(2, _prg8);
        SetPrg8K(3, -1);
        for (var i = 0; i < 8; i++)
        {
            SetChr1K(i, _chr[i]);
        }
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_chr);
        writer.WriteByte(_prg16);
        writer.WriteByte(_prg8);
        writer.WriteByte(_control);
        writer.WriteByte((byte)Mirroring);
        _irq.Save(writer);
        _audio.Save(writer);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_chr);
        _prg16 = reader.ReadByte();
        _prg8 = reader.ReadByte();
        _control = reader.ReadByte();
        PrgRamReadable = PrgRamWritable = (_control & 0x80) != 0;
        Mirroring = (Mirroring)reader.ReadByte();
        _irq.Load(reader);
        _audio.Load(reader);
        UpdateBanks();
    }
}

/// <summary>
/// VRC6 sound (NESdev Wiki: "VRC6 audio"): two pulse channels with 8 duty settings and a sawtooth whose
/// accumulator adds its rate every other step for 7 steps. $9003 can halt all three.
/// </summary>
public sealed class Vrc6Audio : IExpansionAudio
{
    /// <summary>Per output unit; the VRC6 is mixed at about the 2A03 pulses' level (NESdev Wiki mixing notes).</summary>
    private const float Scale = 0.0094f;

    private readonly Pulse _pulse1 = new();
    private readonly Pulse _pulse2 = new();
    private bool _halt;

    // Sawtooth.
    private byte _rate;
    private int _sawPeriod;
    private bool _sawEnabled;
    private int _sawTimer;
    private int _sawStep;
    private byte _accumulator;

    public float Output { get; private set; }

    /// <summary>A write to $9000-$9003 (register group 9), $A000-$A002 (10) or $B000-$B002 (11).</summary>
    public void Write(int group, int sub, byte value)
    {
        switch (group)
        {
            case 9 when sub == 3:
                _halt = (value & 1) != 0;
                break;
            case 9:
                _pulse1.Write(sub, value);
                break;
            case 10:
                _pulse2.Write(sub, value);
                break;
            default:
                switch (sub)
                {
                    case 0:
                        _rate = (byte)(value & 0x3F);
                        break;
                    case 1:
                        _sawPeriod = (_sawPeriod & 0xF00) | value;
                        break;
                    default:
                        _sawPeriod = (_sawPeriod & 0x0FF) | ((value & 0x0F) << 8);
                        _sawEnabled = (value & 0x80) != 0;
                        if (!_sawEnabled)
                        {
                            _sawStep = 0;
                            _accumulator = 0;
                        }

                        break;
                }

                break;
        }
    }

    public void Clock()
    {
        if (!_halt)
        {
            _pulse1.Clock();
            _pulse2.Clock();
            if (_sawEnabled && --_sawTimer < 0)
            {
                _sawTimer = _sawPeriod;
                _sawStep++;
                if (_sawStep == 14)
                {
                    _sawStep = 0;
                    _accumulator = 0;
                }
                else if ((_sawStep & 1) == 0)
                {
                    _accumulator += _rate;
                }
            }
        }

        var saw = _sawEnabled ? _accumulator >> 3 : 0;
        Output = (_pulse1.Output + _pulse2.Output + saw) * Scale;
    }

    public void Save(StateWriter writer)
    {
        _pulse1.Save(writer);
        _pulse2.Save(writer);
        writer.WriteBool(_halt);
        writer.WriteByte(_rate);
        writer.WriteInt32(_sawPeriod);
        writer.WriteBool(_sawEnabled);
        writer.WriteInt32(_sawTimer);
        writer.WriteInt32(_sawStep);
        writer.WriteByte(_accumulator);
    }

    public void Load(StateReader reader)
    {
        _pulse1.Load(reader);
        _pulse2.Load(reader);
        _halt = reader.ReadBool();
        _rate = reader.ReadByte();
        _sawPeriod = reader.ReadInt32();
        _sawEnabled = reader.ReadBool();
        _sawTimer = reader.ReadInt32();
        _sawStep = reader.ReadInt32();
        _accumulator = reader.ReadByte();
    }

    private sealed class Pulse
    {
        private byte _volume;
        private byte _duty;
        private bool _constant;
        private int _period;
        private bool _enabled;
        private int _timer;
        private int _step;

        public int Output => _enabled && (_constant || _step <= _duty) ? _volume : 0;

        public void Write(int sub, byte value)
        {
            switch (sub)
            {
                case 0:
                    _volume = (byte)(value & 0x0F);
                    _duty = (byte)((value >> 4) & 7);
                    _constant = (value & 0x80) != 0;
                    break;
                case 1:
                    _period = (_period & 0xF00) | value;
                    break;
                case 2:
                    _period = (_period & 0x0FF) | ((value & 0x0F) << 8);
                    _enabled = (value & 0x80) != 0;
                    if (!_enabled)
                    {
                        _step = 15;
                    }

                    break;
            }
        }

        public void Clock()
        {
            if (_enabled && --_timer < 0)
            {
                _timer = _period;
                _step = (_step - 1) & 15;
            }
        }

        public void Save(StateWriter writer)
        {
            writer.WriteByte(_volume);
            writer.WriteByte(_duty);
            writer.WriteBool(_constant);
            writer.WriteInt32(_period);
            writer.WriteBool(_enabled);
            writer.WriteInt32(_timer);
            writer.WriteInt32(_step);
        }

        public void Load(StateReader reader)
        {
            _volume = reader.ReadByte();
            _duty = reader.ReadByte();
            _constant = reader.ReadBool();
            _period = reader.ReadInt32();
            _enabled = reader.ReadBool();
            _timer = reader.ReadInt32();
            _step = reader.ReadInt32();
        }
    }
}
