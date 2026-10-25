using GameVault.Core.Audio;
using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Mapper 69, Sunsoft FME-7 / 5A / 5B (NESdev Wiki: "Sunsoft FME-7"): a command/parameter register pair for
/// 1 KB CHR and 8 KB PRG banking (including ROM or RAM at $6000), mirroring, and a 16-bit CPU-cycle IRQ
/// counter. The 5B adds its sound chip (<see cref="Sunsoft5BAudio"/>).
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
/// The Sunsoft 5B's sound, a YM2149F core (NESdev Wiki: "Sunsoft 5B audio"). It has three square-wave tone
/// channels, a 17-bit LFSR noise generator and a 32-step envelope generator with the AY shapes. The chip
/// runs at half the CPU clock with a /8 prescaler, so its tone, noise and envelope counters advance every 16
/// CPU cycles. Volume is logarithmic in 1.5 dB steps (32 levels; fixed volumes use every other level).
/// </summary>
public sealed class Sunsoft5BAudio : IExpansionAudio, IStateful
{
    private static readonly StateTag Tag = StateTag.From("S5BA");

    /// <summary>Loudest channel relative to the 2A03 mix; three channels together sit near the 2A03's own level.</summary>
    private const float ChannelScale = 0.08f;

    private static readonly float[] Levels = BuildLevels();

    private readonly byte[] _registers = new byte[16];
    private readonly int[] _toneCounters = new int[3];
    private readonly bool[] _toneHigh = new bool[3];
    private int _prescaler;
    private byte _select;
    private int _noiseCounter;
    private int _noiseLfsr = 1;
    private int _envelopeCounter;
    private int _envelopeStep;
    private bool _envelopeHolding;
    private bool _envelopeAttack;

    public float Output { get; private set; }

    public void Select(byte value) => _select = (byte)(value & 0x0F);

    public void Write(byte value)
    {
        _registers[_select] = value;
        if (_select == 13)
        {
            // Writing the shape restarts the envelope.
            _envelopeStep = 0;
            _envelopeCounter = 0;
            _envelopeHolding = false;
            _envelopeAttack = (value & 0x04) != 0;
        }
    }

    public void Clock()
    {
        if (++_prescaler < 16)
        {
            return;
        }

        _prescaler = 0;
        ClockTones();
        ClockNoise();
        ClockEnvelope();

        var envelopeLevel = _envelopeAttack ? _envelopeStep : 31 - _envelopeStep;
        var noiseHigh = (_noiseLfsr & 1) != 0;
        var output = 0f;
        for (var ch = 0; ch < 3; ch++)
        {
            var toneOff = (_registers[7] & (1 << ch)) != 0;
            var noiseOff = (_registers[7] & (8 << ch)) != 0;
            if ((toneOff || _toneHigh[ch]) && (noiseOff || noiseHigh))
            {
                var volume = _registers[8 + ch];
                var level = (volume & 0x10) != 0 ? envelopeLevel : (volume & 0x0F) == 0 ? 0 : ((volume & 0x0F) * 2) + 1;
                output += Levels[level];
            }
        }

        Output = output * ChannelScale;
    }

    private void ClockTones()
    {
        for (var ch = 0; ch < 3; ch++)
        {
            var period = _registers[ch * 2] | ((_registers[ch * 2 + 1] & 0x0F) << 8);
            if (++_toneCounters[ch] >= Math.Max(1, period))
            {
                _toneCounters[ch] = 0;
                _toneHigh[ch] = !_toneHigh[ch];
            }
        }
    }

    private void ClockNoise()
    {
        // The noise period counts at half the tone rate.
        var period = Math.Max(1, _registers[6] & 0x1F) * 2;
        if (++_noiseCounter >= period)
        {
            _noiseCounter = 0;
            var feedback = (_noiseLfsr ^ (_noiseLfsr >> 3)) & 1;
            _noiseLfsr = (_noiseLfsr >> 1) | (feedback << 16);
        }
    }

    private void ClockEnvelope()
    {
        var period = Math.Max(1, _registers[11] | (_registers[12] << 8));
        if (_envelopeHolding || ++_envelopeCounter < period)
        {
            return;
        }

        _envelopeCounter = 0;
        if (++_envelopeStep < 32)
        {
            return;
        }

        // End of a ramp: the shape decides what happens (AY/YM "CONT, ATT, ALT, HOLD").
        var shape = _registers[13];
        var cont = (shape & 0x08) != 0;
        var alternate = (shape & 0x02) != 0;
        var hold = (shape & 0x01) != 0;
        if (!cont)
        {
            _envelopeStep = 31;
            _envelopeAttack = false; // 31 - 31 = 0: silent
            _envelopeHolding = true;
        }
        else if (hold)
        {
            _envelopeStep = 31;
            if (alternate)
            {
                _envelopeAttack = !_envelopeAttack;
            }

            _envelopeHolding = true;
        }
        else
        {
            _envelopeStep = 0;
            if (alternate)
            {
                _envelopeAttack = !_envelopeAttack;
            }
        }
    }

    private static float[] BuildLevels()
    {
        var table = new float[32];
        for (var i = 1; i < 32; i++)
        {
            table[i] = MathF.Pow(10f, -1.5f * (31 - i) / 20f);
        }

        return table;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 2);
        writer.WriteBytes(_registers);
        for (var i = 0; i < 3; i++)
        {
            writer.WriteInt32(_toneCounters[i]);
            writer.WriteBool(_toneHigh[i]);
        }

        writer.WriteInt32(_prescaler);
        writer.WriteByte(_select);
        writer.WriteInt32(_noiseCounter);
        writer.WriteInt32(_noiseLfsr);
        writer.WriteInt32(_envelopeCounter);
        writer.WriteInt32(_envelopeStep);
        writer.WriteBool(_envelopeHolding);
        writer.WriteBool(_envelopeAttack);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_registers);
        for (var i = 0; i < 3; i++)
        {
            _toneCounters[i] = reader.ReadInt32();
            _toneHigh[i] = reader.ReadBool();
        }

        _prescaler = reader.ReadInt32();
        _select = reader.ReadByte();
        _noiseCounter = reader.ReadInt32();
        _noiseLfsr = reader.ReadInt32();
        _envelopeCounter = reader.ReadInt32();
        _envelopeStep = reader.ReadInt32();
        _envelopeHolding = reader.ReadBool();
        _envelopeAttack = reader.ReadBool();
        reader.EndChunk();
    }
}
