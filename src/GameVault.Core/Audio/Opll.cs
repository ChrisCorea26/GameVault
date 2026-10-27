using GameVault.Core.State;

namespace GameVault.Core.Audio;

/// <summary>
/// The FM engine of the YM2413 (OPLL) family, shared by the NES's VRC7 (six channels, its own instruments) and the
/// Master System's FM Sound Unit, a YM2413 (nine channels, or six and five rhythm sounds; outstanding-work plan §8).
/// <list type="bullet">
/// <item>Two-operator channels (modulator → carrier, with modulator self-feedback).</item>
/// <item>15 built-in instruments plus one custom instrument in registers $00-$07.</item>
/// <item>Each operator has ADSR envelope, key scaling, half-sine waveform option, tremolo and vibrato.</item>
/// </list>
/// One sample per <see cref="Step"/>, at 49,716 Hz (the NES CPU ÷ 36, the Master System's Z80 ÷ 72). This is a
/// behavioral model in floating point, tuned to the documented rates and levels rather than a die-level copy.
/// </summary>
/// <remarks>
/// Rhythm mode (register $0E bit 5, YM2413 only) turns channels 7–9 into the bass drum (both operators of channel
/// 7), hi-hat and snare drum (channel 8's modulator and carrier) and tom-tom and top cymbal (channel 9's), keyed by
/// $0E's low five bits, with the volumes in $36–$38 and the three rhythm instruments after the 15 melodic ones. The
/// noisy rhythm sounds come from a 23-bit noise generator mixed with the operators' phases, as the YM2413
/// application manual describes them.
/// </remarks>
public sealed class Opll
{
    private const double SampleRate = 1789772.7 / 36;

    /// <summary>Frequency multipliers ×2 (the "0.5" setting becomes 1).</summary>
    private static readonly int[] Multiplier2 = [1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 20, 24, 24, 30, 30];

    /// <summary>Key-scale-level base attenuation by the top four F-number bits, in 0.75 dB units (OPLL table).</summary>
    private static readonly int[] KslBase = [0, 24, 32, 37, 40, 43, 45, 47, 48, 50, 51, 52, 53, 54, 55, 56];

    private const int SineSize = 4096;

    /// <summary>Lookup tables so the per-sample work is table reads, not transcendental functions.</summary>
    private static readonly double[] Sine = Enumerable.Range(0, SineSize).Select(i => Math.Sin(i * 2 * Math.PI / SineSize)).ToArray();

    /// <summary>dB attenuation in 1/16 dB steps → linear gain.</summary>
    private static readonly double[] Gain = Enumerable.Range(0, 48 * 16 + 1).Select(i => Math.Pow(10, -(i / 16.0) / 20)).ToArray();

    /// <summary>Envelope fraction per sample for each effective rate (rate × 4 + key scale), 0-63.</summary>
    internal static readonly double[] Rates = Enumerable.Range(0, 64)
        .Select(effective => 1.0 / Math.Max(1, 13.0 / Math.Pow(2, (effective - 4) / 4.0) * SampleRate))
        .ToArray();

    private readonly byte[,] _patches;
    private readonly byte[] _registers = new byte[0x40];
    private readonly Channel[] _channels;
    private readonly bool _rhythmCapable;
    private byte _address;
    private double _amPhase;
    private double _pmPhase;
    private long _envelopeClock;
    private int _noise = 1;
    private byte _rhythmKeys;

    /// <param name="patches">The built-in instruments: rows 1–15 melodic (row 0 is unused), then the rhythm ones.</param>
    /// <param name="channels">6 (VRC7) or 9 (YM2413).</param>
    public Opll(byte[,] patches, int channels)
    {
        _patches = patches;
        _channels = new Channel[channels];
        for (var c = 0; c < channels; c++)
        {
            _channels[c] = new Channel();
        }

        _rhythmCapable = channels == 9 && patches.GetLength(0) >= 19;
    }

    public ReadOnlySpan<byte> Registers => _registers;

    private bool RhythmMode => _rhythmCapable && (_registers[0x0E] & 0x20) != 0;

    public void SelectRegister(byte value) => _address = value;

    public void WriteData(byte value)
    {
        var reg = _address & 0x3F;
        _registers[reg] = value;
        if (reg >= 0x20 && reg < 0x20 + _channels.Length)
        {
            var channel = _channels[reg - 0x20];
            var keyOn = (value & 0x10) != 0;
            if (keyOn && !channel.KeyOn)
            {
                channel.Modulator.KeyOn();
                channel.Carrier.KeyOn();
            }
            else if (!keyOn && channel.KeyOn)
            {
                channel.Modulator.KeyOff();
                channel.Carrier.KeyOff();
            }

            channel.KeyOn = keyOn;
        }
        else if (reg == 0x0E && _rhythmCapable)
        {
            KeyRhythm(value);
        }
    }

    /// <summary>$0E's key bits: bass drum (4), snare drum (3), tom-tom (2), top cymbal (1), hi-hat (0).</summary>
    private void KeyRhythm(byte value)
    {
        var keys = (byte)((value & 0x20) != 0 ? value & 0x1F : 0);
        var changed = keys ^ _rhythmKeys;
        void Key(int bit, Operator op)
        {
            if ((changed & bit) == 0)
            {
                return;
            }

            if ((keys & bit) != 0)
            {
                op.KeyOn();
            }
            else
            {
                op.KeyOff();
            }
        }

        Key(0x10, _channels[6].Modulator);
        Key(0x10, _channels[6].Carrier);
        Key(0x01, _channels[7].Modulator); // hi-hat
        Key(0x08, _channels[7].Carrier);   // snare drum
        Key(0x04, _channels[8].Modulator); // tom-tom
        Key(0x02, _channels[8].Carrier);   // top cymbal
        _rhythmKeys = keys;
    }

    /// <summary>One output sample: the sum of the channels (each about ±1 at full volume).</summary>
    public double Step()
    {
        _envelopeClock++;

        // Tremolo: 3.7 Hz, 4.8 dB deep. Vibrato: 6.4 Hz, ±14 cents.
        _amPhase += 3.7 / SampleRate;
        _pmPhase += 6.4 / SampleRate;
        _amPhase %= 1.0;
        _pmPhase %= 1.0;
        var am = (1 - Sine[((int)(_amPhase * SineSize) + (SineSize / 4)) & (SineSize - 1)]) * 0.5 * 4.8;
        var pm = 1 + (Sine[(int)(_pmPhase * SineSize) & (SineSize - 1)] * 0.0081); // ±14 cents

        double mix = 0;
        var melodic = RhythmMode ? 6 : _channels.Length;
        for (var c = 0; c < melodic; c++)
        {
            mix += RenderChannel(c, am, pm);
        }

        if (melodic < _channels.Length)
        {
            mix += RenderRhythm(am, pm);
        }

        return mix;
    }

    private double RenderChannel(int c, double am, double pm)
    {
        var channel = _channels[c];
        var fnum = _registers[0x10 + c] | ((_registers[0x20 + c] & 1) << 8);
        var block = (_registers[0x20 + c] >> 1) & 7;
        var sustainOn = (_registers[0x20 + c] & 0x20) != 0;
        var instrument = _registers[0x30 + c] >> 4;
        var volume = _registers[0x30 + c] & 0x0F;

        byte P(int i) => instrument == 0 ? _registers[i] : _patches[instrument, i];

        var keyScale = (block << 1) | (fnum >> 8);
        var ksl = Math.Max(0, KslBase[fnum >> 5] - (8 * (7 - block)));

        // Modulator.
        var modTl = (P(2) & 0x3F) * 0.75;
        var modKsl = KslAttenuation(ksl, P(2) >> 6);
        var feedback = P(3) & 7;
        var modOut = channel.Modulator.Render(
            fnum, block, P(0), P(4), P(6), keyScale, sustainOn, halfSine: (P(3) & 0x08) != 0,
            attenuationDb: modTl + modKsl, am, pm, _envelopeClock, phaseOffset: feedback == 0 ? 0 : (channel.Feedback1 + channel.Feedback2) / 2 * Math.Pow(2, feedback - 9));
        channel.Feedback2 = channel.Feedback1;
        channel.Feedback1 = modOut;

        // Carrier, phase-modulated by the modulator.
        var carKsl = KslAttenuation(ksl, P(3) >> 6);
        return channel.Carrier.Render(
            fnum, block, P(1), P(5), P(7), keyScale, sustainOn, halfSine: (P(3) & 0x10) != 0,
            attenuationDb: (volume * 3.0) + carKsl, am, pm, _envelopeClock, phaseOffset: modOut * 2);
    }

    /// <summary>KSL setting 0-3 → 0, 1.5, 3 or 6 dB per octave.</summary>
    private static double KslAttenuation(int ksl, int setting) => setting switch
    {
        0 => 0,
        1 => ksl * 0.75 / 2,
        2 => ksl * 0.75,
        _ => ksl * 0.75 * 2,
    };

    /// <summary>The five rhythm sounds; each is twice a melodic channel's level, as on the chip.</summary>
    private double RenderRhythm(double am, double pm)
    {
        // The noise generator: 23 bits, taps at 0 and 14 (the YM2413's).
        _noise = (_noise >> 1) | ((((_noise ^ (_noise >> 14)) & 1)) << 22);
        var noise = (_noise & 1) != 0;

        double mix = 0;

        // Bass drum: channel 7 as a melodic channel with the bass drum instrument, volume in $36's low nibble.
        mix += 2 * RenderRhythmChannel(6, 16, _registers[0x36] & 0x0F, am, pm);

        var hihatSnare = _channels[7];
        var tomCymbal = _channels[8];
        var fnum7 = _registers[0x17] | ((_registers[0x27] & 1) << 8);
        var block7 = (_registers[0x27] >> 1) & 7;
        var fnum8 = _registers[0x18] | ((_registers[0x28] & 1) << 8);
        var block8 = (_registers[0x28] >> 1) & 7;

        // Hi-hat (channel 8's modulator) and top cymbal (channel 9's carrier): noise against two operators' phases.
        var square = ((hihatSnare.Modulator.Phase > 0.5) ^ (tomCymbal.Carrier.Phase > 0.5)) ? 1.0 : -1.0;
        mix += 2 * hihatSnare.Modulator.RenderWave(fnum7, block7, _patches[17, 0], _patches[17, 4], _patches[17, 6],
            (_registers[0x37] >> 4) * 3.0, am, pm, noise ? square : -square * 0.5);
        mix += 2 * tomCymbal.Carrier.RenderWave(fnum8, block8, _patches[18, 1], _patches[18, 5], _patches[18, 7],
            (_registers[0x38] & 0x0F) * 3.0, am, pm, square);

        // Snare drum (channel 8's carrier): its sine with noise flipping the sign; tom-tom (channel 9's modulator): a sine.
        var snare = Sine[(int)(hihatSnare.Carrier.Phase * SineSize) & (SineSize - 1)];
        mix += 2 * hihatSnare.Carrier.RenderWave(fnum7, block7, _patches[17, 1], _patches[17, 5], _patches[17, 7],
            (_registers[0x37] & 0x0F) * 3.0, am, pm, noise ? snare : -snare);
        var tom = Sine[(int)(tomCymbal.Modulator.Phase * SineSize) & (SineSize - 1)];
        mix += 2 * tomCymbal.Modulator.RenderWave(fnum8, block8, _patches[18, 0], _patches[18, 4], _patches[18, 6],
            (_registers[0x38] >> 4) * 3.0, am, pm, tom);
        return mix;
    }

    private double RenderRhythmChannel(int c, int patch, int volume, double am, double pm)
    {
        var channel = _channels[c];
        var fnum = _registers[0x10 + c] | ((_registers[0x20 + c] & 1) << 8);
        var block = (_registers[0x20 + c] >> 1) & 7;
        byte P(int i) => _patches[patch, i];
        var keyScale = (block << 1) | (fnum >> 8);
        var ksl = Math.Max(0, KslBase[fnum >> 5] - (8 * (7 - block)));
        var feedback = P(3) & 7;
        var modOut = channel.Modulator.Render(
            fnum, block, P(0), P(4), P(6), keyScale, sustainOn: false, halfSine: (P(3) & 0x08) != 0,
            attenuationDb: ((P(2) & 0x3F) * 0.75) + KslAttenuation(ksl, P(2) >> 6), am, pm, _envelopeClock,
            phaseOffset: feedback == 0 ? 0 : (channel.Feedback1 + channel.Feedback2) / 2 * Math.Pow(2, feedback - 9));
        channel.Feedback2 = channel.Feedback1;
        channel.Feedback1 = modOut;
        return channel.Carrier.Render(
            fnum, block, P(1), P(5), P(7), keyScale, sustainOn: false, halfSine: (P(3) & 0x10) != 0,
            attenuationDb: (volume * 3.0) + KslAttenuation(ksl, P(3) >> 6), am, pm, _envelopeClock, phaseOffset: modOut * 2);
    }

    /// <summary>The registers and the address latch (the first part of the VRC7's saved state).</summary>
    public void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_registers);
        writer.WriteByte(_address);
    }

    public void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_registers);
        _address = reader.ReadByte();
    }

    /// <summary>The LFOs, the envelope clock and the channels (the rest of the VRC7's saved state).</summary>
    public void SaveVoices(StateWriter writer)
    {
        writer.WriteInt64(BitConverter.DoubleToInt64Bits(_amPhase));
        writer.WriteInt64(BitConverter.DoubleToInt64Bits(_pmPhase));
        writer.WriteInt64(_envelopeClock);
        foreach (var channel in _channels)
        {
            channel.Save(writer);
        }
    }

    public void LoadVoices(StateReader reader)
    {
        _amPhase = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        _pmPhase = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        _envelopeClock = reader.ReadInt64();
        foreach (var channel in _channels)
        {
            channel.Load(reader);
        }
    }

    /// <summary>The YM2413's extra state: the noise generator and the rhythm keys.</summary>
    public void SaveRhythm(StateWriter writer)
    {
        writer.WriteInt32(_noise);
        writer.WriteByte(_rhythmKeys);
    }

    public void LoadRhythm(StateReader reader)
    {
        _noise = reader.ReadInt32() & 0x7FFFFF;
        _rhythmKeys = (byte)(reader.ReadByte() & 0x1F);
    }

    private sealed class Channel
    {
        public Operator Modulator { get; } = new();

        public Operator Carrier { get; } = new();

        public bool KeyOn { get; set; }

        public double Feedback1 { get; set; }

        public double Feedback2 { get; set; }

        public void Save(StateWriter writer)
        {
            Modulator.Save(writer);
            Carrier.Save(writer);
            writer.WriteBool(KeyOn);
            writer.WriteInt64(BitConverter.DoubleToInt64Bits(Feedback1));
            writer.WriteInt64(BitConverter.DoubleToInt64Bits(Feedback2));
        }

        public void Load(StateReader reader)
        {
            Modulator.Load(reader);
            Carrier.Load(reader);
            KeyOn = reader.ReadBool();
            Feedback1 = BitConverter.Int64BitsToDouble(reader.ReadInt64());
            Feedback2 = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        }
    }

    private enum Stage : byte
    {
        Off,
        Attack,
        Decay,
        Sustain,
        Release,
    }

    /// <summary>One FM operator: phase generator, envelope generator, sine (or half-sine) output.</summary>
    private sealed class Operator
    {
        private const double MaxAttenuation = 48.0; // dB: the envelope's floor (OPLL: 7 bits of 0.375 dB)

        private double _phase;
        private double _envelope = MaxAttenuation;
        private Stage _stage = Stage.Off;

        public double Phase => _phase;

        public void KeyOn()
        {
            _stage = Stage.Attack;
            _phase = 0;
        }

        public void KeyOff()
        {
            if (_stage != Stage.Off)
            {
                _stage = Stage.Release;
            }
        }

        /// <param name="flags">Register $00/$01: AM, VIB, EG type (sustained), KSR, multiplier.</param>
        /// <param name="attackDecay">Register $04/$05.</param>
        /// <param name="sustainRelease">Register $06/$07.</param>
        public double Render(
            int fnum, int block, byte flags, byte attackDecay, byte sustainRelease, int keyScale, bool sustainOn, bool halfSine,
            double attenuationDb, double am, double pm, long envelopeClock, double phaseOffset)
        {
            // Phase: f = 49716 Hz × fnum × 2^(block - 19) × multiple.
            var vibrato = (flags & 0x40) != 0 ? pm : 1.0;
            var increment = fnum * (1 << block) / 524288.0 * (Multiplier2[flags & 0x0F] / 2.0) * vibrato;
            _phase = (_phase + increment) % 1.0;

            StepEnvelope(flags, attackDecay, sustainRelease, keyScale, sustainOn);
            if (_stage == Stage.Off)
            {
                return 0;
            }

            var total = _envelope + attenuationDb + ((flags & 0x80) != 0 ? am : 0);
            if (total >= MaxAttenuation)
            {
                return 0;
            }

            var index = (int)((_phase + phaseOffset) * SineSize) & (SineSize - 1);
            var wave = Sine[index];
            if (halfSine && wave < 0)
            {
                wave = 0;
            }

            return wave * Gain[(int)(total * 16)];
        }

        /// <summary>A rhythm sound: this operator's phase and envelope, with the given waveform value instead of its sine.</summary>
        public double RenderWave(int fnum, int block, byte flags, byte attackDecay, byte sustainRelease, double attenuationDb, double am, double pm, double wave)
        {
            var vibrato = (flags & 0x40) != 0 ? pm : 1.0;
            var increment = fnum * (1 << block) / 524288.0 * (Multiplier2[flags & 0x0F] / 2.0) * vibrato;
            _phase = (_phase + increment) % 1.0;
            var keyScale = (block << 1) | (fnum >> 8);
            StepEnvelope(flags, attackDecay, sustainRelease, keyScale, sustainOn: false);
            if (_stage == Stage.Off)
            {
                return 0;
            }

            var total = _envelope + attenuationDb + ((flags & 0x80) != 0 ? am : 0);
            return total >= MaxAttenuation ? 0 : wave * Gain[(int)(total * 16)];
        }

        private void StepEnvelope(byte flags, byte attackDecay, byte sustainRelease, int keyScale, bool sustainOn)
        {
            var ksr = (flags & 0x10) != 0 ? keyScale : keyScale >> 2;
            switch (_stage)
            {
                case Stage.Attack:
                {
                    var rate = attackDecay >> 4;
                    if (rate == 15)
                    {
                        _envelope = 0;
                    }
                    else if (rate > 0)
                    {
                        // Exponential approach to full volume (OPLL attack curve).
                        _envelope -= (_envelope + 1.5) * RateFactor(rate, ksr) * 0.5;
                    }

                    if (_envelope <= 0)
                    {
                        _envelope = 0;
                        _stage = Stage.Decay;
                    }

                    break;
                }

                case Stage.Decay:
                {
                    var sustainLevel = (sustainRelease >> 4) * 3.0;
                    Decay(attackDecay & 0x0F, ksr);
                    if (_envelope >= sustainLevel)
                    {
                        _envelope = sustainLevel;
                        _stage = Stage.Sustain;
                    }

                    break;
                }

                case Stage.Sustain:
                    // Sustained instruments hold; percussive ones keep falling at the release rate.
                    if ((flags & 0x20) == 0)
                    {
                        Decay(sustainRelease & 0x0F, ksr);
                    }

                    break;

                case Stage.Release:
                {
                    var rate = (flags & 0x20) == 0 ? 7 : sustainOn ? 5 : sustainRelease & 0x0F;
                    Decay(rate, ksr);
                    if (_envelope >= MaxAttenuation)
                    {
                        _stage = Stage.Off;
                    }

                    break;
                }
            }
        }

        private void Decay(int rate, int ksr)
        {
            if (rate > 0)
            {
                _envelope = Math.Min(MaxAttenuation, _envelope + (MaxAttenuation * RateFactor(rate, ksr)));
            }
        }

        /// <summary>
        /// Fraction of the full range covered per sample. A full decay at rate 1 takes about 13 s and each rate
        /// step halves the time (YM2413 data sheet decay table); key scaling adds a quarter step per unit.
        /// </summary>
        private static double RateFactor(int rate, int ksr) => Rates[Math.Min(63, (rate * 4) + ksr)];

        public void Save(StateWriter writer)
        {
            writer.WriteInt64(BitConverter.DoubleToInt64Bits(_phase));
            writer.WriteInt64(BitConverter.DoubleToInt64Bits(_envelope));
            writer.WriteByte((byte)_stage);
        }

        public void Load(StateReader reader)
        {
            _phase = BitConverter.Int64BitsToDouble(reader.ReadInt64());
            _envelope = BitConverter.Int64BitsToDouble(reader.ReadInt64());
            _stage = (Stage)reader.ReadByte();
        }
    }
}
