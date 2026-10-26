using GameVault.Core.State;

namespace GameVault.Core.Genesis;

/// <summary>
/// Yamaha YM2612 (OPN2), the Genesis' FM chip (SPEC §7.11, Phase 13): six channels of four operators with the eight
/// algorithms, operator 1's feedback, detune and multiple, attack/decay/sustain/release envelopes with key scaling
/// and SSG-EG, the LFO (amplitude and pitch), channel 3's special mode, channel 6's DAC, timers A and B, and
/// left/right panning. It makes one sample every 144 of its clocks (about 53.3 kHz).
/// </summary>
/// <remarks>
/// The log-sine and attenuation tables are computed at start-up from the published formulas, not copied from the
/// chip. The structure follows the OPN documentation and the widely used MAME "fm.c" model (Jarek Burczynski,
/// Tatsuyuki Satoh). Phase and envelope state are integers, so the chip is deterministic.
/// </remarks>
public sealed class Ym2612 : IStateful
{
    private const int SinBits = 10;
    private const int SinLength = 1 << SinBits;
    private const int TlResolution = 256;
    private const int TlTableLength = 13 * 2 * TlResolution;
    private const int MaxAttenuation = 1023;
    private const int EnvelopeQuiet = TlTableLength >> 3;

    private const int Attack = 4, Decay = 3, Sustain = 2, Release = 1, Off = 0;

    private static readonly StateTag Tag = StateTag.From("YM26");
    private static readonly int[] SinTable = BuildSinTable();
    private static readonly int[] TlTable = BuildTlTable();

    /// <summary>Detune in phase-increment units, by DT (0–3) and key code (0–31).</summary>
    private static readonly byte[] DetuneTable =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 8, 8,
        1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 9, 10, 11, 12, 13, 14, 16, 16, 16, 16,
        2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 20, 22, 22, 22, 22,
    ];

    /// <summary>Envelope increments: 19 rows of 8 steps.</summary>
    private static readonly byte[] EnvelopeIncrement =
    [
        0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 1, 1, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 2, 2, 1, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 4, 2, 4, 2, 4, 2, 4, 2, 4, 2, 4, 4, 4, 2, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 8, 4, 4, 4, 8, 4, 8, 4, 8, 4, 8, 4, 8, 4, 8, 8, 8, 4, 8, 8, 8,
        8, 8, 8, 8, 8, 8, 8, 8, 16, 16, 16, 16, 16, 16, 16, 16, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    private static readonly byte[] RateSelect = BuildRateSelect();
    private static readonly byte[] RateShift = BuildRateShift();

    /// <summary>LFO steps every this many samples, by frequency setting.</summary>
    private static readonly int[] LfoPeriod = [108, 77, 71, 67, 62, 44, 8, 5];

    /// <summary>Amplitude modulation depth as a right shift of the LFO's 0–126 value, by AMS.</summary>
    private static readonly int[] AmsShift = [8, 3, 1, 0];

    /// <summary>Pitch modulation depth, as F-number fraction ×2048 at the LFO's peak, by PMS (0–80 cents).</summary>
    private static readonly int[] PmsDepth = [0, 4, 8, 12, 16, 24, 48, 96];

    private readonly Operator[] _operators = new Operator[24];
    private readonly Channel[] _channels = new Channel[6];
    private readonly byte[] _registers = new byte[0x200];

    // Per-sample routing slots: 0 m2, 1 c1, 2 c2, 3 mem, 4 output, 5 nowhere.
    private readonly int[] _bus = new int[6];

    private int _address;
    private int _part;
    private int _envelopeCounter;
    private int _envelopeTimer;
    private bool _lfoEnabled;
    private int _lfoFrequency;
    private int _lfoTimer;
    private int _lfoCounter;
    private int _lfoAm;
    private int _lfoPm;
    private int _timerA;
    private int _timerB;
    private int _timerACounter;
    private int _timerBCounter;
    private int _timerControl;
    private int _status;
    private int _channel3Mode;
    private bool _dacEnabled;
    private int _dacData = 0x80;
    private int _busyCycles;
    private int _clocks;

    public Ym2612()
    {
        for (var i = 0; i < _operators.Length; i++)
        {
            _operators[i] = new Operator();
        }

        for (var i = 0; i < _channels.Length; i++)
        {
            _channels[i] = new Channel();
        }

        Reset();
    }

    /// <summary>The last sample, left and right, each about ±8192 per channel.</summary>
    public int Left { get; private set; }

    public int Right { get; private set; }

    public void Reset()
    {
        Array.Clear(_registers);
        foreach (var op in _operators)
        {
            op.Reset();
        }

        foreach (var channel in _channels)
        {
            channel.Reset();
        }

        _address = 0;
        _part = 0;
        _envelopeCounter = 1;
        _envelopeTimer = 0;
        _lfoEnabled = false;
        _lfoFrequency = 0;
        _lfoTimer = 0;
        _lfoCounter = 0;
        _lfoAm = 0;
        _lfoPm = 0;
        _timerA = _timerB = 0;
        _timerACounter = _timerBCounter = 0;
        _timerControl = 0;
        _status = 0;
        _channel3Mode = 0;
        _dacEnabled = false;
        _dacData = 0x80;
        _busyCycles = 0;
        _clocks = 0;
        Left = Right = 0;
        for (var ch = 0; ch < 6; ch++)
        {
            // Both speakers on after reset.
            WriteRegister(ch < 3 ? 0 : 1, 0xB4 + (ch % 3), 0xC0);
        }
    }

    /// <summary>The status register: timer A (bit 0) and B (bit 1) overflow, busy (bit 7).</summary>
    public byte ReadStatus() => (byte)(_status | (_busyCycles > 0 ? 0x80 : 0));

    /// <summary>A write to one of the four ports ($4000–$4003 on the Z80; $A04000–$A04003 for the 68000).</summary>
    public void Write(int port, byte value)
    {
        switch (port & 3)
        {
            case 0:
                _address = value;
                _part = 0;
                break;
            case 2:
                _address = value;
                _part = 1;
                break;
            default:
                if ((port & 2) != 0 && _part == 0)
                {
                    // Port 3 with an address latched through port 0 still writes part II.
                    _part = 1;
                }

                WriteRegister(_part, _address, value);
                _busyCycles = 32 * 6;
                break;
        }
    }

    /// <summary>Advances by <paramref name="clocks"/> of the chip's clock (the 68000's, 7.67 MHz); returns the samples made.</summary>
    public int Clock(int clocks)
    {
        _busyCycles = Math.Max(0, _busyCycles - clocks);
        _clocks += clocks;
        var samples = 0;
        while (_clocks >= 144)
        {
            _clocks -= 144;
            Sample();
            samples++;
        }

        return samples;
    }

    /// <summary>How many chip clocks until the next sample.</summary>
    public int ClocksToNextSample => 144 - _clocks;

    private void WriteRegister(int part, int address, byte value)
    {
        _registers[part * 0x100 + address] = value;
        if (part == 0 && address < 0x30)
        {
            WriteGlobal(address, value);
            return;
        }

        if (address < 0x30)
        {
            return;
        }

        var channelInPart = address & 3;
        if (channelInPart == 3)
        {
            return;
        }

        var channelIndex = part * 3 + channelInPart;
        var channel = _channels[channelIndex];
        if (address < 0xA0)
        {
            // Register order within a channel is operator 1, 3, 2, 4.
            var slot = ((address >> 2) & 3) switch
            {
                0 => 0,
                1 => 2,
                2 => 1,
                _ => 3,
            };
            WriteOperator(_operators[channelIndex * 4 + slot], address & 0xF0, value);
            return;
        }

        switch (address & 0xFC)
        {
            case 0xA0:
                channel.FNumber = ((_registers[part * 0x100 + 0xA4 + channelInPart] & 7) << 8) | value;
                channel.Block = (_registers[part * 0x100 + 0xA4 + channelInPart] >> 3) & 7;
                UpdateFrequency(channelIndex);
                break;
            case 0xA4:
                break; // latched; takes effect with the A0 write
            case 0xA8:
                if (part == 0)
                {
                    // Channel 3's special-mode frequencies: A8 operator 3, A9 operator 1, AA operator 2.
                    var opSlot = channelInPart switch
                    {
                        0 => 2,
                        1 => 0,
                        _ => 1,
                    };
                    var high = _registers[0xAC + channelInPart];
                    _channels[2].SpecialFNumber[opSlot] = ((high & 7) << 8) | value;
                    _channels[2].SpecialBlock[opSlot] = (high >> 3) & 7;
                    UpdateFrequency(2);
                }

                break;
            case 0xAC:
                break;
            case 0xB0:
                channel.Algorithm = value & 7;
                var feedback = (value >> 3) & 7;
                channel.Feedback = feedback == 0 ? 0 : feedback + 6;
                break;
            case 0xB4:
                channel.Left = (value & 0x80) != 0;
                channel.Right = (value & 0x40) != 0;
                channel.Ams = AmsShift[(value >> 4) & 3];
                channel.Pms = value & 7;
                break;
        }
    }

    private void WriteGlobal(int address, byte value)
    {
        switch (address)
        {
            case 0x22:
                _lfoEnabled = (value & 8) != 0;
                _lfoFrequency = value & 7;
                if (!_lfoEnabled)
                {
                    _lfoCounter = 0;
                    _lfoAm = 0;
                    _lfoPm = 0;
                }

                break;
            case 0x24:
                _timerA = (_timerA & 3) | (value << 2);
                break;
            case 0x25:
                _timerA = (_timerA & 0x3FC) | (value & 3);
                break;
            case 0x26:
                _timerB = value;
                break;
            case 0x27:
                // Timer A ticks twice a sample (every 72 clocks), timer B every 8 samples.
                if ((value & 1) != 0 && (_timerControl & 1) == 0)
                {
                    _timerACounter = 1024 - _timerA;
                }

                if ((value & 2) != 0 && (_timerControl & 2) == 0)
                {
                    _timerBCounter = (256 - _timerB) * 8;
                }

                if ((value & 0x10) != 0)
                {
                    _status &= ~1;
                }

                if ((value & 0x20) != 0)
                {
                    _status &= ~2;
                }

                _timerControl = value & 0x0F;
                if ((value >> 6) != _channel3Mode)
                {
                    _channel3Mode = value >> 6;
                    UpdateFrequency(2);
                }

                break;
            case 0x28:
            {
                var channelIndex = (value & 3) + ((value & 4) != 0 ? 3 : 0);
                if ((value & 3) == 3)
                {
                    break;
                }

                // Bits 4–7 are operators 1, 2, 3, 4.
                for (var slot = 0; slot < 4; slot++)
                {
                    var op = _operators[channelIndex * 4 + slot];
                    if ((value & (0x10 << slot)) != 0)
                    {
                        KeyOn(op);
                    }
                    else
                    {
                        KeyOff(op);
                    }
                }

                break;
            }

            case 0x2A:
                _dacData = value;
                break;
            case 0x2B:
                _dacEnabled = (value & 0x80) != 0;
                break;
        }
    }

    private void WriteOperator(Operator op, int register, byte value)
    {
        switch (register)
        {
            case 0x30:
                op.Detune = (value >> 4) & 7;
                op.Multiple = (value & 0x0F) == 0 ? 1 : (value & 0x0F) * 2;
                break;
            case 0x40:
                op.TotalLevel = (value & 0x7F) << 3;
                break;
            case 0x50:
                op.KeyScale = 3 - (value >> 6);
                op.AttackRate = (value & 0x1F) != 0 ? 32 + ((value & 0x1F) << 1) : 0;
                break;
            case 0x60:
                op.AmOn = (value & 0x80) != 0;
                op.DecayRate = (value & 0x1F) != 0 ? 32 + ((value & 0x1F) << 1) : 0;
                break;
            case 0x70:
                op.SustainRate = (value & 0x1F) != 0 ? 32 + ((value & 0x1F) << 1) : 0;
                break;
            case 0x80:
            {
                var level = value >> 4;
                op.SustainLevel = (level == 15 ? 31 : level) << 5;
                op.ReleaseRate = 34 + ((value & 0x0F) << 2);
                break;
            }

            case 0x90:
                op.Ssg = value & 0x0F;
                break;
        }

        // Key scaling depends on the channel's key code; refresh all rates.
        op.RatesDirty = true;
    }

    private void UpdateFrequency(int channelIndex)
    {
        var channel = _channels[channelIndex];
        for (var slot = 0; slot < 4; slot++)
        {
            int fnumber, block;
            if (channelIndex == 2 && _channel3Mode != 0 && slot < 3)
            {
                fnumber = channel.SpecialFNumber[slot];
                block = channel.SpecialBlock[slot];
            }
            else
            {
                fnumber = channel.FNumber;
                block = channel.Block;
            }

            var op = _operators[channelIndex * 4 + slot];
            op.FNumber = fnumber;
            op.Block = block;
            op.KeyCode = (block << 2) | KeyCodeLow(fnumber);
            op.RatesDirty = true;
        }
    }

    /// <summary>The key code's low bits: F11 (bit 10), and F11 combined with F10–F8 per the OPN2 manual.</summary>
    private static int KeyCodeLow(int fnumber)
    {
        var n4 = (fnumber >> 10) & 1;
        var f10 = (fnumber >> 9) & 1;
        var f9 = (fnumber >> 8) & 1;
        var f8 = (fnumber >> 7) & 1;
        var n3 = (n4 & (f10 | f9 | f8)) | ((1 - n4) & f10 & f9 & f8);
        return (n4 << 1) | n3;
    }

    private static void KeyOn(Operator op)
    {
        if (op.Key)
        {
            return;
        }

        op.Key = true;
        op.Phase = 0;
        op.SsgInverted = false;
        op.RefreshRates();
        if (op.AttackRate + op.KeyScaleRate >= 32 + 62)
        {
            op.Volume = 0;
            op.State = Decay;
        }
        else
        {
            op.State = Attack;
        }
    }

    private static void KeyOff(Operator op)
    {
        if (!op.Key)
        {
            return;
        }

        op.Key = false;
        if (op.State > Release)
        {
            op.State = Release;

            // SSG-EG inverted output becomes the real level at key off.
            if (op.Ssg >= 8 && op.SsgInverted ^ ((op.Ssg & 4) != 0))
            {
                op.Volume = (0x200 - op.Volume) & MaxAttenuation;
            }
        }
    }

    // ---- One sample ----

    private void Sample()
    {
        AdvanceLfo();
        AdvanceTimers();

        var left = 0;
        var right = 0;
        for (var ch = 0; ch < 6; ch++)
        {
            var channel = _channels[ch];
            int output;
            if (ch == 5 && _dacEnabled)
            {
                output = (_dacData - 0x80) << 6;
            }
            else
            {
                output = ChannelOutput(ch, channel);
            }

            // The DAC has 9 bits: the low bits of the 14-bit result are lost.
            output = Math.Clamp(output, -8192, 8191) & ~0x1F;
            if (channel.Left)
            {
                left += output;
            }

            if (channel.Right)
            {
                right += output;
            }
        }

        Left = left;
        Right = right;

        if (++_envelopeTimer >= 3)
        {
            _envelopeTimer = 0;
            _envelopeCounter++;
            if (_envelopeCounter == 4096)
            {
                _envelopeCounter = 1;
            }

            foreach (var op in _operators)
            {
                AdvanceEnvelope(op);
            }
        }

        for (var i = 0; i < _operators.Length; i++)
        {
            AdvancePhase(_operators[i], _channels[i >> 2]);
        }
    }

    private void AdvanceLfo()
    {
        if (!_lfoEnabled)
        {
            return;
        }

        if (++_lfoTimer >= LfoPeriod[_lfoFrequency])
        {
            _lfoTimer = 0;
            _lfoCounter = (_lfoCounter + 1) & 127;
            _lfoAm = _lfoCounter < 64 ? (_lfoCounter ^ 63) << 1 : (_lfoCounter & 63) << 1;
            _lfoPm = _lfoCounter >> 2;
        }
    }

    private void AdvanceTimers()
    {
        if ((_timerControl & 1) != 0)
        {
            _timerACounter -= 2;
            if (_timerACounter <= 0)
            {
                _timerACounter += 1024 - _timerA;
                if ((_timerControl & 4) != 0)
                {
                    _status |= 1;
                }

                if (_channel3Mode == 2)
                {
                    // CSM: timer A keys channel 3's operators on and off.
                    for (var slot = 0; slot < 4; slot++)
                    {
                        KeyOn(_operators[8 + slot]);
                        KeyOff(_operators[8 + slot]);
                    }
                }
            }
        }

        if ((_timerControl & 2) != 0)
        {
            if (--_timerBCounter <= 0)
            {
                _timerBCounter += (256 - _timerB) * 8;
                if ((_timerControl & 8) != 0)
                {
                    _status |= 2;
                }
            }
        }
    }

    private int ChannelOutput(int index, Channel channel)
    {
        var bus = _bus;
        Array.Clear(bus);
        var (m1To, c1To, m2To, memTo) = channel.Algorithm switch
        {
            0 => (1, 3, 2, 0),
            1 => (3, 3, 2, 0),
            2 => (2, 3, 2, 0),
            3 => (1, 3, 2, 2),
            4 => (1, 4, 2, 3),
            5 => (-1, 4, 4, 0),
            6 => (1, 4, 4, 3),
            _ => (4, 4, 4, 3),
        };

        // The delayed MEM value from the previous sample.
        bus[memTo == 3 ? 5 : memTo] = channel.Memory;

        var am = _lfoAm >> channel.Ams;
        var op1 = _operators[index * 4];
        var feedbackInput = channel.Op1Previous[0] + channel.Op1Previous[1];
        channel.Op1Previous[0] = channel.Op1Previous[1];
        if (m1To < 0)
        {
            // Algorithm 5: operator 1 modulates the other three.
            bus[3] = bus[1] = bus[2] = channel.Op1Previous[0];
        }
        else
        {
            bus[m1To] += channel.Op1Previous[0];
        }

        channel.Op1Previous[1] = 0;
        var envelope = Attenuation(op1, am);
        if (envelope < EnvelopeQuiet)
        {
            var modulation = channel.Feedback == 0 ? 0 : feedbackInput << channel.Feedback;
            channel.Op1Previous[1] = OperatorOutput(op1.Phase + (modulation >> 6), envelope);
        }

        // Operator 3 (M2), then 2 (C1), then 4 (C2).
        var op3 = _operators[index * 4 + 2];
        envelope = Attenuation(op3, am);
        if (envelope < EnvelopeQuiet)
        {
            bus[m2To] += OperatorOutput(op3.Phase + (bus[0] << 9), envelope);
        }

        var op2 = _operators[index * 4 + 1];
        envelope = Attenuation(op2, am);
        if (envelope < EnvelopeQuiet)
        {
            bus[c1To] += OperatorOutput(op2.Phase + (bus[1] << 9), envelope);
        }

        var op4 = _operators[index * 4 + 3];
        envelope = Attenuation(op4, am);
        if (envelope < EnvelopeQuiet)
        {
            bus[4] += OperatorOutput(op4.Phase + (bus[2] << 9), envelope);
        }

        channel.Memory = bus[3];
        return bus[4];
    }

    /// <summary>
    /// One operator's output for a 20-bit phase (10 bits of sine index) at an attenuation in 1/32 dB-ish steps:
    /// a log-sine lookup plus the attenuation, then the power table. About ±8192.
    /// </summary>
    private static int OperatorOutput(int phase, int attenuation)
    {
        var p = (attenuation << 3) + SinTable[(phase >> 10) & (SinLength - 1)];
        return p >= TlTableLength ? 0 : TlTable[p];
    }

    private static int Attenuation(Operator op, int am)
    {
        var volume = op.Volume;
        if (op.Ssg >= 8 && op.State > Release && (op.SsgInverted ^ ((op.Ssg & 4) != 0)))
        {
            volume = (0x200 - volume) & MaxAttenuation;
        }

        var level = volume + op.TotalLevel + (op.AmOn ? am : 0);
        return Math.Min(level, MaxAttenuation);
    }

    private void AdvancePhase(Operator op, Channel channel)
    {
        var fnumber = op.FNumber;
        if (channel.Pms != 0 && _lfoEnabled)
        {
            // A triangle over the LFO's 32 steps, scaled to the PMS depth.
            var step = _lfoPm & 31;
            var triangle = step < 8 ? step : step < 16 ? 15 - step : step < 24 ? -(step - 16) : -(31 - step);
            fnumber += (fnumber * PmsDepth[channel.Pms] * triangle) >> 14;
        }

        var increment = (fnumber << op.Block) >> 1;
        var detune = DetuneTable[(op.Detune & 3) * 32 + op.KeyCode];
        increment += (op.Detune & 4) != 0 ? -detune : detune;
        increment &= 0x1FFFF;
        op.Phase = (op.Phase + ((increment * op.Multiple) >> 1)) & 0xFFFFF;
    }

    private void AdvanceEnvelope(Operator op)
    {
        if (op.RatesDirty)
        {
            op.RefreshRates();
        }

        switch (op.State)
        {
            case Attack:
            {
                var shift = op.AttackShift;
                if ((_envelopeCounter & ((1 << shift) - 1)) == 0)
                {
                    var increment = EnvelopeIncrement[op.AttackSelect + ((_envelopeCounter >> shift) & 7)];
                    op.Volume += (~op.Volume * increment) >> 4;
                    if (op.Volume <= 0)
                    {
                        op.Volume = 0;
                        op.State = Decay;
                    }
                }

                break;
            }

            case Decay:
                if (StepEnvelope(op, op.DecayShift, op.DecaySelect) && op.Volume >= op.SustainLevel)
                {
                    op.State = Sustain;
                }

                break;
            case Sustain:
                StepEnvelope(op, op.SustainShift, op.SustainSelect);
                break;
            case Release:
                if (StepEnvelope(op, op.ReleaseShift, op.ReleaseSelect) && op.Volume >= MaxAttenuation)
                {
                    op.Volume = MaxAttenuation;
                    op.State = Off;
                }

                break;
        }
    }

    /// <summary>A decay, sustain or release step, with SSG-EG's repeat, hold and alternate at its half-way level.</summary>
    private bool StepEnvelope(Operator op, int shift, int select)
    {
        if ((_envelopeCounter & ((1 << shift) - 1)) != 0)
        {
            return false;
        }

        var increment = EnvelopeIncrement[select + ((_envelopeCounter >> shift) & 7)];
        if (op.Ssg >= 8 && op.State != Release)
        {
            if (op.Volume < 0x200)
            {
                op.Volume += increment * 4;
            }

            if (op.Volume >= 0x200)
            {
                if ((op.Ssg & 1) != 0)
                {
                    // Hold: stay at the end level (inverted per alternate).
                    if ((op.Ssg & 2) != 0)
                    {
                        op.SsgInverted = true;
                    }

                    op.Volume = 0x200;
                }
                else
                {
                    // Repeat: restart the envelope (alternate flips the output).
                    if ((op.Ssg & 2) != 0)
                    {
                        op.SsgInverted = !op.SsgInverted;
                    }
                    else
                    {
                        op.Phase = 0;
                    }

                    op.Volume = 0;
                    op.State = op.AttackRate + op.KeyScaleRate >= 32 + 62 ? Decay : Attack;
                }
            }

            return true;
        }

        op.Volume = Math.Min(MaxAttenuation, op.Volume + increment);
        return true;
    }

    // ---- Tables ----

    private static int[] BuildTlTable()
    {
        var table = new int[TlTableLength];
        for (var x = 0; x < TlResolution; x++)
        {
            var m = Math.Floor((1 << 16) / Math.Pow(2, (x + 1) * (128.0 / 1024 / 4) / 8.0));
            var n = (int)m >> 4;
            n = (n >> 1) + (n & 1);
            n <<= 2;
            for (var i = 0; i < 13; i++)
            {
                table[x * 2 + i * 2 * TlResolution] = n >> i;
                table[x * 2 + 1 + i * 2 * TlResolution] = -(n >> i);
            }
        }

        return table;
    }

    private static int[] BuildSinTable()
    {
        var table = new int[SinLength];
        for (var i = 0; i < SinLength; i++)
        {
            var m = Math.Sin((i * 2 + 1) * Math.PI / SinLength);
            var o = 8 * Math.Log(1.0 / Math.Abs(m)) / Math.Log(2);
            o /= 128.0 / 1024 / 4;
            var n = (int)(2 * o);
            n = (n >> 1) + (n & 1);
            table[i] = n * 2 + (m >= 0 ? 0 : 1);
        }

        return table;
    }

    private static byte[] BuildRateSelect()
    {
        var table = new byte[128];
        for (var i = 0; i < 32; i++)
        {
            table[i] = 18 * 8;
        }

        for (var r = 0; r < 48; r++)
        {
            table[32 + r] = (byte)((r & 3) * 8);
        }

        for (var r = 0; r < 16; r++)
        {
            table[80 + r] = (byte)((4 + r) * 8 > 16 * 8 ? 16 * 8 : (4 + r) * 8);
        }

        for (var r = 96; r < 128; r++)
        {
            table[r] = 16 * 8;
        }

        return table;
    }

    private static byte[] BuildRateShift()
    {
        var table = new byte[128];
        for (var r = 0; r < 48; r++)
        {
            table[32 + r] = (byte)(11 - r / 4);
        }

        return table;
    }

    private sealed class Operator
    {
        public int Detune;
        public int Multiple = 1;
        public int TotalLevel;
        public int KeyScale = 3;
        public int AttackRate;
        public int DecayRate;
        public int SustainRate;
        public int ReleaseRate = 34;
        public int SustainLevel;
        public bool AmOn;
        public int Ssg;
        public bool SsgInverted;

        public int FNumber;
        public int Block;
        public int KeyCode;
        public int Phase;
        public int Volume = MaxAttenuation;
        public int State;
        public bool Key;

        public bool RatesDirty = true;
        public int KeyScaleRate;
        public int AttackShift, AttackSelect, DecayShift, DecaySelect, SustainShift, SustainSelect, ReleaseShift, ReleaseSelect;

        public void Reset()
        {
            Detune = 0;
            Multiple = 1;
            TotalLevel = 0;
            KeyScale = 3;
            AttackRate = DecayRate = SustainRate = 0;
            ReleaseRate = 34;
            SustainLevel = 0;
            AmOn = false;
            Ssg = 0;
            SsgInverted = false;
            FNumber = Block = KeyCode = 0;
            Phase = 0;
            Volume = MaxAttenuation;
            State = Off;
            Key = false;
            RatesDirty = true;
        }

        public void RefreshRates()
        {
            KeyScaleRate = KeyCode >> KeyScale;
            (AttackShift, AttackSelect) = Rate(AttackRate);
            (DecayShift, DecaySelect) = Rate(DecayRate);
            (SustainShift, SustainSelect) = Rate(SustainRate);
            (ReleaseShift, ReleaseSelect) = Rate(ReleaseRate);
            RatesDirty = false;
        }

        private (int Shift, int Select) Rate(int rate)
        {
            var index = Math.Min(127, rate + KeyScaleRate);
            return (RateShift[index], RateSelect[index]);
        }

        public void Save(StateWriter writer)
        {
            writer.WriteInt32(Detune);
            writer.WriteInt32(Multiple);
            writer.WriteInt32(TotalLevel);
            writer.WriteInt32(KeyScale);
            writer.WriteInt32(AttackRate);
            writer.WriteInt32(DecayRate);
            writer.WriteInt32(SustainRate);
            writer.WriteInt32(ReleaseRate);
            writer.WriteInt32(SustainLevel);
            writer.WriteBool(AmOn);
            writer.WriteInt32(Ssg);
            writer.WriteBool(SsgInverted);
            writer.WriteInt32(FNumber);
            writer.WriteInt32(Block);
            writer.WriteInt32(KeyCode);
            writer.WriteInt32(Phase);
            writer.WriteInt32(Volume);
            writer.WriteInt32(State);
            writer.WriteBool(Key);
        }

        public void Load(StateReader reader)
        {
            Detune = reader.ReadInt32();
            Multiple = reader.ReadInt32();
            TotalLevel = reader.ReadInt32();
            KeyScale = reader.ReadInt32();
            AttackRate = reader.ReadInt32();
            DecayRate = reader.ReadInt32();
            SustainRate = reader.ReadInt32();
            ReleaseRate = reader.ReadInt32();
            SustainLevel = reader.ReadInt32();
            AmOn = reader.ReadBool();
            Ssg = reader.ReadInt32();
            SsgInverted = reader.ReadBool();
            FNumber = reader.ReadInt32();
            Block = reader.ReadInt32();
            KeyCode = reader.ReadInt32();
            Phase = reader.ReadInt32();
            Volume = reader.ReadInt32();
            State = reader.ReadInt32();
            Key = reader.ReadBool();
            RatesDirty = true;
        }
    }

    private sealed class Channel
    {
        public int FNumber;
        public int Block;
        public readonly int[] SpecialFNumber = new int[3];
        public readonly int[] SpecialBlock = new int[3];
        public int Algorithm;
        public int Feedback;
        public bool Left = true;
        public bool Right = true;
        public int Ams = 8;
        public int Pms;
        public readonly int[] Op1Previous = new int[2];
        public int Memory;

        public void Reset()
        {
            FNumber = Block = 0;
            Array.Clear(SpecialFNumber);
            Array.Clear(SpecialBlock);
            Algorithm = Feedback = 0;
            Left = Right = true;
            Ams = 8;
            Pms = 0;
            Array.Clear(Op1Previous);
            Memory = 0;
        }

        public void Save(StateWriter writer)
        {
            writer.WriteInt32(FNumber);
            writer.WriteInt32(Block);
            for (var i = 0; i < 3; i++)
            {
                writer.WriteInt32(SpecialFNumber[i]);
                writer.WriteInt32(SpecialBlock[i]);
            }

            writer.WriteInt32(Algorithm);
            writer.WriteInt32(Feedback);
            writer.WriteBool(Left);
            writer.WriteBool(Right);
            writer.WriteInt32(Ams);
            writer.WriteInt32(Pms);
            writer.WriteInt32(Op1Previous[0]);
            writer.WriteInt32(Op1Previous[1]);
            writer.WriteInt32(Memory);
        }

        public void Load(StateReader reader)
        {
            FNumber = reader.ReadInt32();
            Block = reader.ReadInt32();
            for (var i = 0; i < 3; i++)
            {
                SpecialFNumber[i] = reader.ReadInt32();
                SpecialBlock[i] = reader.ReadInt32();
            }

            Algorithm = reader.ReadInt32();
            Feedback = reader.ReadInt32();
            Left = reader.ReadBool();
            Right = reader.ReadBool();
            Ams = reader.ReadInt32();
            Pms = reader.ReadInt32();
            Op1Previous[0] = reader.ReadInt32();
            Op1Previous[1] = reader.ReadInt32();
            Memory = reader.ReadInt32();
        }
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_registers);
        foreach (var op in _operators)
        {
            op.Save(writer);
        }

        foreach (var channel in _channels)
        {
            channel.Save(writer);
        }

        writer.WriteInt32(_address);
        writer.WriteInt32(_part);
        writer.WriteInt32(_envelopeCounter);
        writer.WriteInt32(_envelopeTimer);
        writer.WriteBool(_lfoEnabled);
        writer.WriteInt32(_lfoFrequency);
        writer.WriteInt32(_lfoTimer);
        writer.WriteInt32(_lfoCounter);
        writer.WriteInt32(_lfoAm);
        writer.WriteInt32(_lfoPm);
        writer.WriteInt32(_timerA);
        writer.WriteInt32(_timerB);
        writer.WriteInt32(_timerACounter);
        writer.WriteInt32(_timerBCounter);
        writer.WriteInt32(_timerControl);
        writer.WriteInt32(_status);
        writer.WriteInt32(_channel3Mode);
        writer.WriteBool(_dacEnabled);
        writer.WriteInt32(_dacData);
        writer.WriteInt32(_busyCycles);
        writer.WriteInt32(_clocks);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_registers);
        foreach (var op in _operators)
        {
            op.Load(reader);
        }

        foreach (var channel in _channels)
        {
            channel.Load(reader);
        }

        _address = reader.ReadInt32();
        _part = reader.ReadInt32();
        _envelopeCounter = reader.ReadInt32();
        _envelopeTimer = reader.ReadInt32();
        _lfoEnabled = reader.ReadBool();
        _lfoFrequency = reader.ReadInt32();
        _lfoTimer = reader.ReadInt32();
        _lfoCounter = reader.ReadInt32();
        _lfoAm = reader.ReadInt32();
        _lfoPm = reader.ReadInt32();
        _timerA = reader.ReadInt32();
        _timerB = reader.ReadInt32();
        _timerACounter = reader.ReadInt32();
        _timerBCounter = reader.ReadInt32();
        _timerControl = reader.ReadInt32();
        _status = reader.ReadInt32();
        _channel3Mode = reader.ReadInt32();
        _dacEnabled = reader.ReadBool();
        _dacData = reader.ReadInt32();
        _busyCycles = reader.ReadInt32();
        _clocks = reader.ReadInt32();
        reader.EndChunk();
    }
}
