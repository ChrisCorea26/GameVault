using GameVault.Core.Audio;
using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// VRC7 FM sound (NESdev Wiki: "VRC7 audio"), a YM2413 (OPLL) derivative.
/// <list type="bullet">
/// <item>Six two-operator channels (modulator → carrier, with modulator self-feedback).</item>
/// <item>15 built-in instruments plus one custom instrument in registers $00-$07.</item>
/// <item>Each operator has ADSR envelope, key scaling, half-sine waveform option, tremolo and vibrato.</item>
/// </list>
/// The chip produces a sample every 36 CPU cycles (about 49.7 kHz). This is a behavioral model in floating
/// point, tuned to the documented rates and levels rather than a die-level copy.
/// </summary>
public sealed class Vrc7Audio : IExpansionAudio
{
    private const int CyclesPerSample = 36;
    private const double SampleRate = 1789772.7 / CyclesPerSample;
    private const float ChannelScale = 0.075f;

    /// <summary>
    /// The VRC7's built-in instruments 1-15 (8 bytes each, same layout as registers $00-$07), as read from the
    /// chip and published on the NESdev Wiki ("VRC7 audio", instrument ROM).
    /// </summary>
    private static readonly byte[,] Patches =
    {
        { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, // 0: custom (registers $00-$07)
        { 0x03, 0x21, 0x05, 0x06, 0xE8, 0x81, 0x42, 0x27 },
        { 0x13, 0x41, 0x14, 0x0D, 0xD8, 0xF6, 0x23, 0x12 },
        { 0x11, 0x11, 0x08, 0x08, 0xFA, 0xB2, 0x20, 0x12 },
        { 0x31, 0x61, 0x0C, 0x07, 0xA8, 0x64, 0x61, 0x27 },
        { 0x32, 0x21, 0x1E, 0x06, 0xE1, 0x76, 0x01, 0x28 },
        { 0x02, 0x01, 0x06, 0x00, 0xA3, 0xE2, 0xF4, 0xF4 },
        { 0x21, 0x61, 0x1D, 0x07, 0x82, 0x81, 0x11, 0x07 },
        { 0x23, 0x21, 0x22, 0x17, 0xA2, 0x72, 0x01, 0x17 },
        { 0x35, 0x11, 0x25, 0x00, 0x40, 0x73, 0x72, 0x01 },
        { 0xB5, 0x01, 0x0F, 0x0F, 0xA8, 0xA5, 0x51, 0x02 },
        { 0x17, 0xC1, 0x24, 0x07, 0xF8, 0xF8, 0x22, 0x12 },
        { 0x71, 0x23, 0x11, 0x06, 0x65, 0x74, 0x18, 0x16 },
        { 0x01, 0x02, 0xD3, 0x05, 0xC9, 0x95, 0x03, 0x02 },
        { 0x61, 0x63, 0x0C, 0x00, 0x94, 0xC0, 0x33, 0xF6 },
        { 0x21, 0x72, 0x0D, 0x00, 0xC1, 0xD5, 0x56, 0x06 },
    };

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
    private static readonly double[] Rates = Enumerable.Range(0, 64)
        .Select(effective => 1.0 / Math.Max(1, 13.0 / Math.Pow(2, (effective - 4) / 4.0) * SampleRate))
        .ToArray();

    private readonly byte[] _registers = new byte[0x40];
    private readonly Channel[] _channels = [new(), new(), new(), new(), new(), new()];
    private byte _address;
    private int _divider;
    private double _amPhase;
    private double _pmPhase;
    private long _envelopeClock;

    public float Output { get; private set; }

    public void SelectRegister(byte value) => _address = value;

    public void WriteData(byte value)
    {
        var reg = _address & 0x3F;
        _registers[reg] = value;
        if (reg is >= 0x20 and <= 0x25)
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
    }

    public void Clock()
    {
        if (++_divider < CyclesPerSample)
        {
            return;
        }

        _divider = 0;
        _envelopeClock++;

        // Tremolo: 3.7 Hz, 4.8 dB deep. Vibrato: 6.4 Hz, ±14 cents.
        _amPhase += 3.7 / SampleRate;
        _pmPhase += 6.4 / SampleRate;
        _amPhase %= 1.0;
        _pmPhase %= 1.0;
        var am = (1 - Sine[((int)(_amPhase * SineSize) + (SineSize / 4)) & (SineSize - 1)]) * 0.5 * 4.8;
        var pm = 1 + (Sine[(int)(_pmPhase * SineSize) & (SineSize - 1)] * 0.0081); // ±14 cents

        double mix = 0;
        for (var c = 0; c < 6; c++)
        {
            mix += RenderChannel(c, am, pm);
        }

        Output = (float)(mix * ChannelScale);
    }

    private double RenderChannel(int c, double am, double pm)
    {
        var channel = _channels[c];
        var fnum = _registers[0x10 + c] | ((_registers[0x20 + c] & 1) << 8);
        var block = (_registers[0x20 + c] >> 1) & 7;
        var sustainOn = (_registers[0x20 + c] & 0x20) != 0;
        var instrument = _registers[0x30 + c] >> 4;
        var volume = _registers[0x30 + c] & 0x0F;

        byte P(int i) => instrument == 0 ? _registers[i] : Patches[instrument, i];

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

    public void Save(StateWriter writer)
    {
        writer.WriteBytes(_registers);
        writer.WriteByte(_address);
        writer.WriteInt32(_divider);
        writer.WriteInt64(BitConverter.DoubleToInt64Bits(_amPhase));
        writer.WriteInt64(BitConverter.DoubleToInt64Bits(_pmPhase));
        writer.WriteInt64(_envelopeClock);
        foreach (var channel in _channels)
        {
            channel.Save(writer);
        }
    }

    public void Load(StateReader reader)
    {
        reader.ReadBytes(_registers);
        _address = reader.ReadByte();
        _divider = reader.ReadInt32();
        _amPhase = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        _pmPhase = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        _envelopeClock = reader.ReadInt64();
        foreach (var channel in _channels)
        {
            channel.Load(reader);
        }
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
