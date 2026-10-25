using GameVault.Core.Audio;
using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Fds;

/// <summary>
/// The FDS sound channel (NESdev Wiki: "FDS audio"): a 64-step, 6-bit wavetable with a volume envelope and a
/// frequency modulation unit driven by a 64-entry table of pitch steps. Clocked every CPU cycle.
/// </summary>
public sealed class FdsAudio : IExpansionAudio
{
    /// <summary>Master volume 2/2, 2/3, 2/4, 2/5, as multipliers over 1152 (wave 63 × gain 32 × 36 / 1152 = 63).</summary>
    private static readonly int[] MasterVolume = [36, 24, 17, 14];

    /// <summary>Mod table steps: 0, +1, +2, +4, reset, -4, -2, -1.</summary>
    private static readonly int[] ModSteps = [0, 1, 2, 4, 0, -4, -2, -1];

    /// <summary>
    /// Relative to the 2A03 mixer: the FDS at full volume is a little louder than a full-volume square
    /// (approximate; the wiki's comparison).
    /// </summary>
    private const float Scale = 0.25f / 63f;

    private readonly byte[] _wave = new byte[64];
    private readonly byte[] _modTable = new byte[64];

    private readonly Envelope _volume = new();
    private readonly Envelope _mod = new();

    private int _waveFrequency;
    private bool _waveHalted = true;
    private bool _envelopesHalted;
    private bool _waveWrite;
    private int _masterVolume;
    private int _masterEnvelopeSpeed = 0xE8;
    private int _waveAccumulator;
    private int _wavePosition;

    private int _modFrequency;
    private bool _modHalted = true;
    private int _modAccumulator;
    private int _modPosition;
    private int _modCounter;
    private int _modOutput;
    private int _level;

    public float Output { get; private set; }

    public void Clock()
    {
        if (!_waveHalted && !_envelopesHalted && _masterEnvelopeSpeed > 0)
        {
            _volume.Tick(_masterEnvelopeSpeed);
            if (_mod.Tick(_masterEnvelopeSpeed))
            {
                UpdateModOutput();
            }
        }

        if (!_modHalted && _modFrequency > 0)
        {
            _modAccumulator += _modFrequency;
            if (_modAccumulator > 0xFFFF)
            {
                _modAccumulator &= 0xFFFF;
                var step = _modTable[_modPosition];
                _modCounter = step == 4 ? 0 : ((_modCounter + ModSteps[step] + 64) & 0x7F) - 64;
                _modPosition = (_modPosition + 1) & 0x3F;
                UpdateModOutput();
            }
        }

        if (_waveHalted)
        {
            _wavePosition = 0;
            _waveAccumulator = 0;
        }
        else if (!_waveWrite)
        {
            var pitch = _waveFrequency + _modOutput;
            if (pitch > 0)
            {
                _waveAccumulator += pitch;
                if (_waveAccumulator > 0xFFFF)
                {
                    _waveAccumulator &= 0xFFFF;
                    _wavePosition = (_wavePosition + 1) & 0x3F;
                }
            }
        }

        if (!_waveWrite)
        {
            // While the wave RAM is writable the output holds its last value.
            _level = _wave[_wavePosition] * Math.Min(_volume.Gain, 32) * MasterVolume[_masterVolume] / 1152;
        }

        Output = _level * Scale;
    }

    /// <summary>
    /// The pitch change from the mod counter and gain, rounded the way the hardware does (NESdev Wiki: "FDS
    /// audio", "Frequency calculation").
    /// </summary>
    private void UpdateModOutput()
    {
        var temp = _modCounter * _mod.Gain;
        var remainder = temp & 0x0F;
        temp >>= 4;
        if (remainder > 0 && (temp & 0x80) == 0)
        {
            temp += _modCounter < 0 ? -1 : 2;
        }

        if (temp >= 192)
        {
            temp -= 256;
        }
        else if (temp < -64)
        {
            temp += 256;
        }

        temp *= _waveFrequency;
        remainder = temp & 0x3F;
        temp >>= 6;
        if (remainder >= 32)
        {
            temp++;
        }

        _modOutput = temp;
    }

    public byte Read(ushort address, byte openBus) => address switch
    {
        >= 0x4040 and <= 0x407F => (byte)((openBus & 0xC0) | (_waveWrite ? _wave[address & 0x3F] : _wave[_wavePosition])),
        0x4090 => (byte)((openBus & 0xC0) | _volume.Gain),
        0x4092 => (byte)((openBus & 0xC0) | _mod.Gain),
        _ => openBus,
    };

    public void Write(ushort address, byte value)
    {
        switch (address)
        {
            case >= 0x4040 and <= 0x407F:
                if (_waveWrite)
                {
                    _wave[address & 0x3F] = (byte)(value & 0x3F);
                }

                break;
            case 0x4080:
                _volume.Write(value);
                break;
            case 0x4082:
                _waveFrequency = (_waveFrequency & 0xF00) | value;
                UpdateModOutput();
                break;
            case 0x4083:
                _waveFrequency = (_waveFrequency & 0xFF) | ((value & 0x0F) << 8);
                _waveHalted = (value & 0x80) != 0;
                _envelopesHalted = (value & 0x40) != 0;
                if (_envelopesHalted)
                {
                    _volume.ResetTimer(_masterEnvelopeSpeed);
                    _mod.ResetTimer(_masterEnvelopeSpeed);
                }

                UpdateModOutput();
                break;
            case 0x4084:
                _mod.Write(value);
                UpdateModOutput();
                break;
            case 0x4085:
                _modCounter = ((value & 0x7F) ^ 0x40) - 0x40;
                UpdateModOutput();
                break;
            case 0x4086:
                _modFrequency = (_modFrequency & 0xF00) | value;
                break;
            case 0x4087:
                _modFrequency = (_modFrequency & 0xFF) | ((value & 0x0F) << 8);
                _modHalted = (value & 0x80) != 0;
                if (_modHalted)
                {
                    _modAccumulator = 0;
                }

                break;
            case 0x4088:
                if (_modHalted)
                {
                    _modTable[_modPosition] = (byte)(value & 7);
                    _modTable[(_modPosition + 1) & 0x3F] = (byte)(value & 7);
                    _modPosition = (_modPosition + 2) & 0x3F;
                }

                break;
            case 0x4089:
                _waveWrite = (value & 0x80) != 0;
                _masterVolume = value & 3;
                break;
            case 0x408A:
                _masterEnvelopeSpeed = value;
                break;
        }
    }

    public void Save(StateWriter writer)
    {
        writer.WriteBytes(_wave);
        writer.WriteBytes(_modTable);
        _volume.Save(writer);
        _mod.Save(writer);
        writer.WriteInt32(_waveFrequency);
        writer.WriteBool(_waveHalted);
        writer.WriteBool(_envelopesHalted);
        writer.WriteBool(_waveWrite);
        writer.WriteInt32(_masterVolume);
        writer.WriteInt32(_masterEnvelopeSpeed);
        writer.WriteInt32(_waveAccumulator);
        writer.WriteInt32(_wavePosition);
        writer.WriteInt32(_modFrequency);
        writer.WriteBool(_modHalted);
        writer.WriteInt32(_modAccumulator);
        writer.WriteInt32(_modPosition);
        writer.WriteInt32(_modCounter);
        writer.WriteInt32(_modOutput);
        writer.WriteInt32(_level);
    }

    public void Load(StateReader reader)
    {
        reader.ReadBytes(_wave);
        reader.ReadBytes(_modTable);
        _volume.Load(reader);
        _mod.Load(reader);
        _waveFrequency = reader.ReadInt32();
        _waveHalted = reader.ReadBool();
        _envelopesHalted = reader.ReadBool();
        _waveWrite = reader.ReadBool();
        _masterVolume = reader.ReadInt32();
        _masterEnvelopeSpeed = reader.ReadInt32();
        _waveAccumulator = reader.ReadInt32();
        _wavePosition = reader.ReadInt32();
        _modFrequency = reader.ReadInt32();
        _modHalted = reader.ReadBool();
        _modAccumulator = reader.ReadInt32();
        _modPosition = reader.ReadInt32();
        _modCounter = reader.ReadInt32();
        _modOutput = reader.ReadInt32();
        _level = reader.ReadInt32();
        Output = _level * Scale;
    }

    /// <summary>
    /// The volume and mod envelopes ($4080 / $4084: MDSSSSSS). With M set the gain is S directly; otherwise
    /// the gain steps by one toward 0 or 32 every 8 × (S + 1) × master-speed CPU cycles.
    /// </summary>
    private sealed class Envelope
    {
        private bool _off = true;
        private bool _increase;
        private int _speed;
        private int _timer;

        public int Gain { get; private set; }

        public void Write(byte value)
        {
            _off = (value & 0x80) != 0;
            _increase = (value & 0x40) != 0;
            _speed = value & 0x3F;
            if (_off)
            {
                Gain = _speed;
            }

            _timer = 0;
        }

        public void ResetTimer(int masterSpeed) => _timer = 8 * (_speed + 1) * masterSpeed;

        /// <summary>Returns true when the gain changed.</summary>
        public bool Tick(int masterSpeed)
        {
            if (_off)
            {
                return false;
            }

            if (_timer > 0 && --_timer > 0)
            {
                return false;
            }

            ResetTimer(masterSpeed);
            if (_increase && Gain < 32)
            {
                Gain++;
                return true;
            }

            if (!_increase && Gain > 0)
            {
                Gain--;
                return true;
            }

            return false;
        }

        public void Save(StateWriter writer)
        {
            writer.WriteBool(_off);
            writer.WriteBool(_increase);
            writer.WriteInt32(_speed);
            writer.WriteInt32(_timer);
            writer.WriteInt32(Gain);
        }

        public void Load(StateReader reader)
        {
            _off = reader.ReadBool();
            _increase = reader.ReadBool();
            _speed = reader.ReadInt32();
            _timer = reader.ReadInt32();
            Gain = reader.ReadInt32();
        }
    }
}
