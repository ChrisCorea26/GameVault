using System.Buffers.Binary;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation;

/// <summary>The CD-ROM drive's audio (CD-DA and XA-ADPCM) as 44.1 kHz stereo, pulled by the SPU once per sample.</summary>
public interface ICdAudioSource
{
    void ReadSample(out short left, out short right);
}

/// <summary>
/// The PlayStation's Sound Processing Unit (SPEC §7.11, Phase 15): 512 KB of sound RAM; 24 voices playing
/// SPU-ADPCM samples with 4-point Gaussian interpolation, ADSR envelopes, sweep volumes, pitch modulation and noise;
/// the reverb unit; the CD audio input; capture buffers; and the IRQ on an address match. It makes a stereo sample
/// every 768 CPU cycles (44.1 kHz).
/// </summary>
/// <remarks>
/// Reference: psx-spx "Sound Processing Unit (SPU)". Register writes take effect at once rather than at the next
/// 44.1 kHz tick, and the reverb runs both sides on alternate ticks at 22.05 kHz without the hardware's resampling
/// filters.
/// </remarks>
public sealed partial class PsSpu : IStateful
{
    public const int SampleRate = 44100;
    public const int CyclesPerSample = 768;
    public const int RamSize = 512 * 1024;

    private const int RamMask = RamSize - 1;
    private const int VoiceCount = 24;

    // SPUCNT bits.
    private const ushort CntCdEnable = 1 << 0;
    private const ushort CntCdReverb = 1 << 2;
    private const ushort CntIrqEnable = 1 << 6;
    private const ushort CntReverbEnable = 1 << 7;
    private const ushort CntUnmute = 1 << 14;
    private const ushort CntEnable = 1 << 15;

    private static readonly StateTag Tag = StateTag.From("PSPU");

    private readonly byte[] _ram = new byte[RamSize];
    private readonly ushort[] _registers = new ushort[0x200];
    private readonly Voice[] _voices = new Voice[VoiceCount];
    private readonly ushort[] _fifo = new ushort[32];
    private int _fifoCount;

    private ushort _control;
    private bool _irqFlag;
    private uint _transferAddress; // the internal current address, in bytes
    private uint _pitchModulation;
    private uint _noiseOn;
    private uint _reverbOn;
    private uint _endx;

    private readonly Sweep _mainLeft = new();
    private readonly Sweep _mainRight = new();

    private int _noiseTimer;
    private int _noiseLevel;
    private int _capturePosition;

    // Reverb: the current buffer address (bytes), and the inputs gathered on the tick before a reverb tick.
    private uint _reverbAddress;
    private bool _reverbTick;
    private int _reverbInputLeft;
    private int _reverbInputRight;
    private int _reverbOutputLeft;
    private int _reverbOutputRight;

    public PsSpu()
    {
        for (var i = 0; i < VoiceCount; i++)
        {
            _voices[i] = new Voice();
        }
    }

    /// <summary>Called with each new sample (44.1 kHz), left then right, in the signed 16-bit range.</summary>
    public Action<int, int>? SampleReady { get; set; }

    /// <summary>The CD audio input, or null for silence.</summary>
    public ICdAudioSource? CdAudio { get; set; }

    /// <summary>Invoked when the SPU raises IRQ9 (the console raises interrupt 9).</summary>
    public Action? Irq { get; set; }

    public ReadOnlySpan<byte> Ram => _ram;

    /// <summary>The latest stereo sample.</summary>
    public int Left { get; private set; }

    public int Right { get; private set; }

    public void Power()
    {
        Array.Clear(_ram);
        Array.Clear(_registers);
        foreach (var voice in _voices)
        {
            voice.Reset();
        }

        _fifoCount = 0;
        _control = 0;
        _irqFlag = false;
        _transferAddress = 0;
        _pitchModulation = _noiseOn = _reverbOn = _endx = 0;
        _mainLeft.Reset();
        _mainRight.Reset();
        _noiseTimer = 0;
        _noiseLevel = 1;
        _capturePosition = 0;
        _reverbAddress = 0;
        _reverbTick = false;
        _reverbInputLeft = _reverbInputRight = 0;
        _reverbOutputLeft = _reverbOutputRight = 0;
        Left = Right = 0;
    }

    // ---- Registers ----

    private ushort Status
    {
        get
        {
            var mode = (_control >> 4) & 3;
            var status = (ushort)(_control & 0x3F);
            status |= (ushort)(_irqFlag ? 1 << 6 : 0);
            status |= (ushort)((_control & 0x20) << 2); // bit 7: DMA read/write request (SPUCNT bit 5)
            status |= (ushort)(mode == 2 ? 1 << 8 : 0);
            status |= (ushort)(mode == 3 ? 1 << 9 : 0);
            status |= (ushort)(_capturePosition >= 0x100 ? 1 << 11 : 0);
            return status;
        }
    }

    public ushort Read16(uint offset)
    {
        offset &= 0x3FE;
        if (offset < 0x180)
        {
            var voice = _voices[offset >> 4];
            return (offset & 0xF) switch
            {
                0x0C => (ushort)voice.Envelope,
                0x0E => (ushort)(voice.RepeatAddress >> 3),
                _ => _registers[offset >> 1],
            };
        }

        switch (offset)
        {
            case 0x19C:
                return (ushort)_endx;
            case 0x19E:
                return (ushort)(_endx >> 16);
            case 0x1AA:
                return _control;
            case 0x1AE:
                return Status;
            case 0x1B8:
                return (ushort)_mainLeft.Level;
            case 0x1BA:
                return (ushort)_mainRight.Level;
            case >= 0x200 and < 0x260:
            {
                var voice = _voices[(offset - 0x200) >> 2];
                return (ushort)((offset & 2) == 0 ? voice.VolumeLeft.Level : voice.VolumeRight.Level);
            }

            default:
                return _registers[offset >> 1];
        }
    }

    public void Write16(uint offset, ushort value)
    {
        offset &= 0x3FE;
        _registers[offset >> 1] = value;
        if (offset < 0x180)
        {
            WriteVoice(_voices[offset >> 4], offset & 0xF, value);
            return;
        }

        switch (offset)
        {
            case 0x180:
                _mainLeft.Set(value);
                break;
            case 0x182:
                _mainRight.Set(value);
                break;
            case 0x188:
                KeyOn(value);
                break;
            case 0x18A:
                KeyOn((uint)value << 16);
                break;
            case 0x18C:
                KeyOff(value);
                break;
            case 0x18E:
                KeyOff((uint)value << 16);
                break;
            case 0x190:
                _pitchModulation = (_pitchModulation & 0xFFFF0000) | value;
                break;
            case 0x192:
                _pitchModulation = (_pitchModulation & 0xFFFF) | (uint)(value & 0xFF) << 16;
                break;
            case 0x194:
                _noiseOn = (_noiseOn & 0xFFFF0000) | value;
                break;
            case 0x196:
                _noiseOn = (_noiseOn & 0xFFFF) | (uint)(value & 0xFF) << 16;
                break;
            case 0x198:
                _reverbOn = (_reverbOn & 0xFFFF0000) | value;
                break;
            case 0x19A:
                _reverbOn = (_reverbOn & 0xFFFF) | (uint)(value & 0xFF) << 16;
                break;
            case 0x1A2:
                // mBASE: writing it also moves the reverb buffer's current address there (psx-spx: "SPU Reverb Registers").
                _reverbAddress = (uint)value << 3;
                break;
            case 0x1A6:
                _transferAddress = (uint)value << 3;
                break;
            case 0x1A8:
                WriteFifo(value);
                break;
            case 0x1AA:
                WriteControl(value);
                break;
        }
    }

    private void WriteVoice(Voice voice, uint register, ushort value)
    {
        switch (register)
        {
            case 0x0:
                voice.VolumeLeft.Set(value);
                break;
            case 0x2:
                voice.VolumeRight.Set(value);
                break;
            case 0x4:
                voice.Pitch = value;
                break;
            case 0x6:
                voice.StartAddress = (uint)value << 3;
                break;
            case 0x8:
                voice.Adsr = (voice.Adsr & 0xFFFF0000) | value;
                break;
            case 0xA:
                voice.Adsr = (voice.Adsr & 0xFFFF) | (uint)value << 16;
                break;
            case 0xC:
                voice.Envelope = (short)value;
                break;
            case 0xE:
                // A repeat address written by the program wins over the loop-start flags of the blocks played until
                // the next key on (as games that redirect one-shot samples rely on).
                voice.RepeatAddress = (uint)value << 3;
                voice.IgnoreLoopStart = true;
                break;
        }
    }

    private void WriteControl(ushort value)
    {
        var oldMode = (_control >> 4) & 3;
        _control = value;

        if ((value & CntIrqEnable) == 0)
        {
            _irqFlag = false; // clearing the enable acknowledges IRQ9
        }

        if ((value & CntEnable) == 0)
        {
            // With the SPU disabled, every voice is keyed off at volume zero (psx-spx: "SPU Control and Status Register").
            foreach (var voice in _voices)
            {
                voice.Phase = AdsrPhase.Off;
                voice.Envelope = 0;
            }
        }

        var mode = (value >> 4) & 3;
        if (mode == 1 && oldMode != 1)
        {
            FlushFifo();
        }
    }

    // ---- Transfers ----

    /// <summary>
    /// A FIFO write. In manual-write mode it goes on to sound RAM; otherwise it waits (up to 32 halfwords) until the
    /// mode changes to manual write (psx-spx: "SPU Memory Access").
    /// </summary>
    private void WriteFifo(ushort value)
    {
        if (((_control >> 4) & 3) == 1)
        {
            WriteTransfer(value);
            return;
        }

        if (_fifoCount < _fifo.Length)
        {
            _fifo[_fifoCount++] = value;
        }
    }

    /// <summary>Writes the FIFO to sound RAM in the transfer type set in $1F801DAC (2 = normal).</summary>
    private void FlushFifo()
    {
        var type = (_registers[0x1AC >> 1] >> 1) & 7;
        for (var i = 0; i < _fifoCount; i++)
        {
            var value = type switch
            {
                2 => _fifo[i],
                3 => _fifo[i & ~1],
                4 => _fifo[i & ~3],
                5 => _fifo[(i | 7) < _fifoCount ? i | 7 : _fifoCount - 1],
                _ => _fifo[_fifoCount - 1],
            };
            WriteTransfer(value);
        }

        _fifoCount = 0;
    }

    private void WriteTransfer(ushort value)
    {
        CheckIrq(_transferAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(_ram.AsSpan((int)_transferAddress), value);
        _transferAddress = (_transferAddress + 2) & RamMask;
    }

    private ushort ReadTransfer()
    {
        CheckIrq(_transferAddress);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_ram.AsSpan((int)_transferAddress));
        _transferAddress = (_transferAddress + 2) & RamMask;
        return value;
    }

    /// <summary>DMA channel 4, sound RAM to main RAM: two halfwords, the low one first.</summary>
    public uint DmaRead()
    {
        var low = ReadTransfer();
        var high = ReadTransfer();
        return (uint)(low | high << 16);
    }

    /// <summary>DMA channel 4, main RAM to sound RAM.</summary>
    public void DmaWrite(uint word)
    {
        WriteTransfer((ushort)word);
        WriteTransfer((ushort)(word >> 16));
    }

    // ---- IRQ ----

    /// <summary>Raises IRQ9 when an access touches the IRQ address ($1F801DA4), if enabled (psx-spx: "SPU Interrupt").</summary>
    private void CheckIrq(uint address)
    {
        if ((_control & (CntEnable | CntIrqEnable)) != (CntEnable | CntIrqEnable) || _irqFlag)
        {
            return;
        }

        if ((address & ~1u) == (uint)_registers[0x1A4 >> 1] << 3)
        {
            RaiseIrq();
        }
    }

    /// <summary>A voice reading an ADPCM block raises IRQ9 if the IRQ address lies anywhere inside it.</summary>
    private void CheckBlockIrq(uint blockAddress)
    {
        if ((_control & (CntEnable | CntIrqEnable)) != (CntEnable | CntIrqEnable) || _irqFlag)
        {
            return;
        }

        var irq = (uint)_registers[0x1A4 >> 1] << 3;
        if (irq - blockAddress < 16)
        {
            RaiseIrq();
        }
    }

    private void RaiseIrq()
    {
        _irqFlag = true;
        Irq?.Invoke();
    }

    // ---- Key on/off ----

    private void KeyOn(uint bits)
    {
        if ((_control & CntEnable) == 0)
        {
            return; // key on is ignored while the SPU is disabled
        }

        for (var i = 0; i < VoiceCount; i++)
        {
            if ((bits & (1u << i)) == 0)
            {
                continue;
            }

            var voice = _voices[i];
            voice.CurrentAddress = voice.StartAddress;
            voice.RepeatAddress = voice.StartAddress;
            voice.IgnoreLoopStart = false;
            voice.Counter = 0;
            voice.Envelope = 0;
            voice.EnvelopeCounter = 0;
            voice.Phase = AdsrPhase.Attack;
            voice.Previous1 = voice.Previous2 = 0;
            voice.Active = true;
            Array.Clear(voice.Samples);
            DecodeBlock(voice);
            _endx &= ~(1u << i);
        }
    }

    private void KeyOff(uint bits)
    {
        for (var i = 0; i < VoiceCount; i++)
        {
            if ((bits & (1u << i)) != 0 && _voices[i].Phase != AdsrPhase.Off)
            {
                _voices[i].Phase = AdsrPhase.Release;
                _voices[i].EnvelopeCounter = 0;
            }
        }
    }

    // ---- Sample generation ----

    /// <summary>Produces one 44.1 kHz sample.</summary>
    public void Tick()
    {
        StepNoise();

        int dryLeft = 0, dryRight = 0, wetLeft = 0, wetRight = 0;
        var previousOutput = 0;
        for (var i = 0; i < VoiceCount; i++)
        {
            var voice = _voices[i];
            var output = 0;
            if (voice.Active)
            {
                output = VoiceSample(voice, i, previousOutput);
            }

            voice.VolumeLeft.Step();
            voice.VolumeRight.Step();
            var left = output * voice.VolumeLeft.Level >> 15;
            var right = output * voice.VolumeRight.Level >> 15;
            dryLeft += left;
            dryRight += right;
            if ((_reverbOn & (1u << i)) != 0)
            {
                wetLeft += left;
                wetRight += right;
            }

            if (i == 1)
            {
                Capture(0x800, output);
            }
            else if (i == 3)
            {
                Capture(0xC00, output);
            }

            previousOutput = output;
        }

        if ((_control & CntUnmute) == 0)
        {
            dryLeft = dryRight = 0; // mute silences the voices but not the CD (psx-spx: "SPUCNT" bits 14–15)
        }

        // CD audio: captured before its volume, mixed after it.
        short cdLeft = 0, cdRight = 0;
        CdAudio?.ReadSample(out cdLeft, out cdRight);
        Capture(0x000, cdLeft);
        Capture(0x400, cdRight);
        if ((_control & CntCdEnable) != 0)
        {
            var left = cdLeft * (short)_registers[0x1B0 >> 1] >> 15;
            var right = cdRight * (short)_registers[0x1B2 >> 1] >> 15;
            dryLeft += left;
            dryRight += right;
            if ((_control & CntCdReverb) != 0)
            {
                wetLeft += left;
                wetRight += right;
            }
        }

        _capturePosition = (_capturePosition + 1) & 0x1FF;

        RunReverb(Clamp16(wetLeft), Clamp16(wetRight));

        _mainLeft.Step();
        _mainRight.Step();
        var outLeft = (Clamp16(dryLeft) * _mainLeft.Level >> 15)
            + (_reverbOutputLeft * (short)_registers[0x184 >> 1] >> 15);
        var outRight = (Clamp16(dryRight) * _mainRight.Level >> 15)
            + (_reverbOutputRight * (short)_registers[0x186 >> 1] >> 15);
        Left = Clamp16(outLeft);
        Right = Clamp16(outRight);
        SampleReady?.Invoke(Left, Right);
    }

    /// <summary>One voice's output for this sample (after its envelope), then the pitch counter's step.</summary>
    private int VoiceSample(Voice voice, int index, int previousOutput)
    {
        int sample;
        if ((_noiseOn & (1u << index)) != 0)
        {
            sample = (short)_noiseLevel;
        }
        else
        {
            // Bits 4–11 of the counter pick the interpolation weights; bit 12 up, the sample (psx-spx: "SPU ADPCM Pitch").
            var i = (voice.Counter >> 4) & 0xFF;
            var s = voice.Counter >> 12;
            var samples = voice.Samples;
            sample = (Gauss[0xFF - i] * samples[s] >> 15)
                + (Gauss[0x1FF - i] * samples[s + 1] >> 15)
                + (Gauss[0x100 + i] * samples[s + 2] >> 15)
                + (Gauss[i] * samples[s + 3] >> 15);
        }

        StepEnvelope(voice);
        var output = sample * voice.Envelope >> 15;

        // The pitch counter: with pitch modulation, the previous voice's output scales the step.
        int step = voice.Pitch;
        if (index > 0 && (_pitchModulation & (1u << index)) != 0)
        {
            var factor = previousOutput + 0x8000;
            step = (int)((short)voice.Pitch * factor >> 15) & 0xFFFF;
        }

        if (step > 0x3FFF)
        {
            step = 0x4000;
        }

        voice.Counter += step;
        if (voice.Counter >= 28 << 12)
        {
            voice.Counter -= 28 << 12;
            EndBlock(voice, index);
        }

        return output;
    }

    /// <summary>
    /// The end of a block's 28 samples: the loop-end flag sets ENDX and jumps to the repeat address, and without the
    /// repeat flag it also releases the voice at volume zero (psx-spx: "SPU ADPCM Samples").
    /// </summary>
    private void EndBlock(Voice voice, int index)
    {
        if ((voice.Flags & 1) != 0)
        {
            _endx |= 1u << index;
            voice.CurrentAddress = voice.RepeatAddress;
            if ((voice.Flags & 2) == 0 && (_noiseOn & (1u << index)) == 0)
            {
                voice.Phase = AdsrPhase.Release;
                voice.Envelope = 0;
            }
        }
        else
        {
            voice.CurrentAddress = (voice.CurrentAddress + 16) & RamMask;
        }

        DecodeBlock(voice);
    }

    /// <summary>
    /// Decodes the 16-byte block at the voice's address into 28 samples, keeping the last three of the previous
    /// block for the interpolation.
    /// </summary>
    private void DecodeBlock(Voice voice)
    {
        var address = voice.CurrentAddress;
        CheckBlockIrq(address);

        var samples = voice.Samples;
        samples[0] = samples[28];
        samples[1] = samples[29];
        samples[2] = samples[30];

        Span<byte> block = stackalloc byte[16];
        for (var i = 0; i < 16; i++)
        {
            block[i] = _ram[(address + (uint)i) & RamMask];
        }

        voice.Flags = block[1];
        if ((voice.Flags & 4) != 0 && !voice.IgnoreLoopStart)
        {
            voice.RepeatAddress = address;
        }

        DecodeAdpcm(block, ref voice.Previous1, ref voice.Previous2, samples.AsSpan(3, 28));
    }

    /// <summary>
    /// Decodes one 16-byte SPU-ADPCM block into 28 samples: each nibble shifted into place, plus the prediction
    /// filter applied to the two previous samples (psx-spx: "SPU ADPCM Samples", "CDROM XA Audio ADPCM Compression").
    /// </summary>
    internal static void DecodeAdpcm(ReadOnlySpan<byte> block, ref short previous1, ref short previous2, Span<short> output)
    {
        var header = block[0];
        var shift = header & 0x0F;
        if (shift > 12)
        {
            shift = 9; // shift values 13–15 act as 9
        }

        var filter = Math.Min((header >> 4) & 7, 4);
        var pos = FilterPositive[filter];
        var neg = FilterNegative[filter];
        int old = previous1, older = previous2;
        for (var i = 0; i < 28; i++)
        {
            var data = block[2 + (i >> 1)];
            var nibble = (i & 1) == 0 ? data & 0xF : data >> 4;
            var sample = Clamp16(((short)(nibble << 12) >> shift) + ((old * pos + older * neg + 32) >> 6));
            output[i] = (short)sample;
            older = old;
            old = sample;
        }

        previous1 = (short)old;
        previous2 = (short)older;
    }

    // ---- ADSR ----

    private enum AdsrPhase
    {
        Off,
        Attack,
        Decay,
        Sustain,
        Release,
    }

    /// <summary>Advances a voice's ADSR envelope by one sample (psx-spx: "SPU Volume and ADSR Generator").</summary>
    private static void StepEnvelope(Voice voice)
    {
        var adsr = voice.Adsr;
        bool exponential, decrease;
        int shift, step;
        switch (voice.Phase)
        {
            case AdsrPhase.Attack:
                exponential = (adsr & 0x8000) != 0;
                decrease = false;
                shift = (int)(adsr >> 10) & 0x1F;
                step = 7 - (int)((adsr >> 8) & 3);
                break;
            case AdsrPhase.Decay:
                exponential = true;
                decrease = true;
                shift = (int)(adsr >> 4) & 0xF;
                step = -8;
                break;
            case AdsrPhase.Sustain:
                exponential = (adsr & 0x80000000) != 0;
                decrease = (adsr & 0x40000000) != 0;
                shift = (int)(adsr >> 24) & 0x1F;
                step = decrease ? -8 + (int)((adsr >> 22) & 3) : 7 - (int)((adsr >> 22) & 3);
                break;
            case AdsrPhase.Release:
                exponential = (adsr & 0x00200000) != 0;
                decrease = true;
                shift = (int)(adsr >> 16) & 0x1F;
                step = -8;
                break;
            default:
                voice.Envelope = 0;
                return;
        }

        var level = (int)voice.Envelope;
        if (EnvelopeStep(ref level, ref voice.EnvelopeCounter, exponential, decrease, shift, step))
        {
            voice.Envelope = (short)level;
        }

        switch (voice.Phase)
        {
            case AdsrPhase.Attack when voice.Envelope >= 0x7FFF:
                voice.Phase = AdsrPhase.Decay;
                voice.EnvelopeCounter = 0;
                break;
            case AdsrPhase.Decay when voice.Envelope <= (int)(((adsr & 0xF) + 1) * 0x800):
                voice.Phase = AdsrPhase.Sustain;
                voice.EnvelopeCounter = 0;
                break;
            case AdsrPhase.Release when voice.Envelope <= 0:
                voice.Envelope = 0;
                voice.Phase = AdsrPhase.Off;
                break;
        }
    }

    /// <summary>
    /// One sample of an envelope (ADSR or sweep): it waits 1 SHL max(0, shift − 11) samples, then adds
    /// step SHL max(0, 11 − shift), four times slower for an exponential increase above $6000, and scaled by the level
    /// for an exponential decrease; the level saturates to 0–$7FFF. Returns whether the level changed.
    /// </summary>
    private static bool EnvelopeStep(ref int level, ref int counter, bool exponential, bool decrease, int shift, int step)
    {
        var cycles = 1 << Math.Max(0, shift - 11);
        var delta = step << Math.Max(0, 11 - shift);
        if (exponential && !decrease && level > 0x6000)
        {
            cycles *= 4;
        }

        if (exponential && decrease)
        {
            delta = delta * level >> 15;
        }

        if (++counter < cycles)
        {
            return false;
        }

        counter = 0;
        level = Math.Clamp(level + delta, 0, 0x7FFF);
        return true;
    }

    // ---- Noise ----

    /// <summary>The noise generator, clocked by SPUCNT's shift and step (psx-spx: "SPU Noise Generator").</summary>
    private void StepNoise()
    {
        var shift = (_control >> 10) & 0xF;
        var step = 4 + ((_control >> 8) & 3);
        _noiseTimer -= step;
        var parity = ((_noiseLevel >> 15) ^ (_noiseLevel >> 12) ^ (_noiseLevel >> 11) ^ (_noiseLevel >> 10) ^ 1) & 1;
        if (_noiseTimer < 0)
        {
            _noiseLevel = ((_noiseLevel << 1) | parity) & 0xFFFF;
            _noiseTimer += 0x20000 >> shift;
            if (_noiseTimer < 0)
            {
                _noiseTimer += 0x20000 >> shift;
            }
        }
    }

    // ---- Capture buffers ----

    /// <summary>
    /// Writes a sample to one of the four 1 KB capture buffers at the start of sound RAM; the IRQ address can match
    /// these writes when $1F801DAC bits 2–3 allow it (psx-spx: "SPU Interrupt").
    /// </summary>
    private void Capture(uint buffer, int sample)
    {
        var address = buffer + (uint)(_capturePosition * 2);
        BinaryPrimitives.WriteInt16LittleEndian(_ram.AsSpan((int)address), (short)Clamp16(sample));
        if ((_registers[0x1AC >> 1] & 0x0C) != 0)
        {
            CheckIrq(address);
        }
    }

    // ---- Reverb ----

    private short Register(int offset) => (short)_registers[offset >> 1];

    private uint ReverbBase => (uint)_registers[0x1A2 >> 1] << 3;

    /// <summary>An address relative to the reverb buffer's current address, wrapped within mBASE–$7FFFE.</summary>
    private uint ReverbAddress(int offsetBytes)
    {
        var baseAddress = ReverbBase;
        var size = (long)RamSize - baseAddress;
        if (size <= 0)
        {
            return 0x7FFFE;
        }

        var relative = ((long)_reverbAddress - baseAddress + offsetBytes) % size;
        if (relative < 0)
        {
            relative += size;
        }

        return (uint)(baseAddress + relative) & 0x7FFFE;
    }

    private int ReverbRead(int registerOffset, int extraBytes = 0)
    {
        var address = ReverbAddress(((ushort)_registers[registerOffset >> 1] << 3) + extraBytes);
        return BinaryPrimitives.ReadInt16LittleEndian(_ram.AsSpan((int)address));
    }

    private void ReverbWrite(int registerOffset, int value)
    {
        if ((_control & CntReverbEnable) == 0)
        {
            return; // the master enable stops writes to the buffer, but reads go on
        }

        var address = ReverbAddress((ushort)_registers[registerOffset >> 1] << 3);
        CheckIrq(address);
        BinaryPrimitives.WriteInt16LittleEndian(_ram.AsSpan((int)address), (short)Clamp16(value));
    }

    private static int Mul(int a, int b) => a * b >> 15;

    /// <summary>
    /// The reverb unit, at 22.05 kHz: same-side and different-side reflections, four combs and two all-pass filters,
    /// with every buffer in sound RAM (psx-spx: "SPU Reverb Formula"). The input is the latest tick's reverb mix.
    /// </summary>
    private void RunReverb(int inputLeft, int inputRight)
    {
        _reverbTick = !_reverbTick;
        if (!_reverbTick)
        {
            _reverbInputLeft = inputLeft;
            _reverbInputRight = inputRight;
            return;
        }

        var lin = Mul(Register(0x1FC), (inputLeft + _reverbInputLeft) >> 1);
        var rin = Mul(Register(0x1FE), (inputRight + _reverbInputRight) >> 1);
        var wall = Register(0x1CE);
        var iir = Register(0x1C4);

        // Same-side reflection.
        var lsamePrev = ReverbRead(0x1D4, -2);
        var rsamePrev = ReverbRead(0x1D6, -2);
        ReverbWrite(0x1D4, Mul(lin + Mul(ReverbRead(0x1E0), wall) - lsamePrev, iir) + lsamePrev);
        ReverbWrite(0x1D6, Mul(rin + Mul(ReverbRead(0x1E2), wall) - rsamePrev, iir) + rsamePrev);

        // Different-side reflection.
        var ldiffPrev = ReverbRead(0x1E4, -2);
        var rdiffPrev = ReverbRead(0x1E6, -2);
        ReverbWrite(0x1E4, Mul(lin + Mul(ReverbRead(0x1F2), wall) - ldiffPrev, iir) + ldiffPrev);
        ReverbWrite(0x1E6, Mul(rin + Mul(ReverbRead(0x1F0), wall) - rdiffPrev, iir) + rdiffPrev);

        // Early echo (combs).
        var lout = Mul(Register(0x1C6), ReverbRead(0x1D8)) + Mul(Register(0x1C8), ReverbRead(0x1DC))
            + Mul(Register(0x1CA), ReverbRead(0x1E8)) + Mul(Register(0x1CC), ReverbRead(0x1EC));
        var rout = Mul(Register(0x1C6), ReverbRead(0x1DA)) + Mul(Register(0x1C8), ReverbRead(0x1DE))
            + Mul(Register(0x1CA), ReverbRead(0x1EA)) + Mul(Register(0x1CC), ReverbRead(0x1EE));

        // Late reverb: two all-pass filters.
        var dapf1 = -((ushort)_registers[0x1C0 >> 1] << 3);
        var dapf2 = -((ushort)_registers[0x1C2 >> 1] << 3);
        var vapf1 = Register(0x1D0);
        var vapf2 = Register(0x1D2);

        var lapf1 = ReverbRead(0x1F4, dapf1);
        var rapf1 = ReverbRead(0x1F6, dapf1);
        lout = Clamp16(lout - Mul(vapf1, lapf1));
        rout = Clamp16(rout - Mul(vapf1, rapf1));
        ReverbWrite(0x1F4, lout);
        ReverbWrite(0x1F6, rout);
        lout = Mul(lout, vapf1) + lapf1;
        rout = Mul(rout, vapf1) + rapf1;

        var lapf2 = ReverbRead(0x1F8, dapf2);
        var rapf2 = ReverbRead(0x1FA, dapf2);
        lout = Clamp16(lout - Mul(vapf2, lapf2));
        rout = Clamp16(rout - Mul(vapf2, rapf2));
        ReverbWrite(0x1F8, lout);
        ReverbWrite(0x1FA, rout);
        lout = Mul(lout, vapf2) + lapf2;
        rout = Mul(rout, vapf2) + rapf2;

        _reverbOutputLeft = Clamp16(lout);
        _reverbOutputRight = Clamp16(rout);

        _reverbAddress = (_reverbAddress + 2) & 0x7FFFE;
        if (_reverbAddress < ReverbBase)
        {
            _reverbAddress = ReverbBase;
        }
    }

    private static int Clamp16(int value) => Math.Clamp(value, short.MinValue, short.MaxValue);

    // ---- Voices and volumes ----

    private sealed class Voice
    {
        public readonly short[] Samples = new short[31]; // three from the previous block, then 28
        public readonly Sweep VolumeLeft = new();
        public readonly Sweep VolumeRight = new();
        public ushort Pitch;
        public uint StartAddress;
        public uint RepeatAddress;
        public uint CurrentAddress;
        public bool IgnoreLoopStart;
        public uint Adsr;
        public short Envelope;
        public int EnvelopeCounter;
        public AdsrPhase Phase;
        public int Counter;
        public byte Flags;
        public short Previous1;
        public short Previous2;
        public bool Active;

        public void Reset()
        {
            Array.Clear(Samples);
            VolumeLeft.Reset();
            VolumeRight.Reset();
            Pitch = 0;
            StartAddress = RepeatAddress = CurrentAddress = 0;
            IgnoreLoopStart = false;
            Adsr = 0;
            Envelope = 0;
            EnvelopeCounter = 0;
            Phase = AdsrPhase.Off;
            Counter = 0;
            Flags = 0;
            Previous1 = Previous2 = 0;
            Active = false;
        }

        public void Save(StateWriter writer)
        {
            foreach (var sample in Samples)
            {
                writer.WriteUInt16((ushort)sample);
            }

            VolumeLeft.Save(writer);
            VolumeRight.Save(writer);
            writer.WriteUInt16(Pitch);
            writer.WriteUInt32(StartAddress);
            writer.WriteUInt32(RepeatAddress);
            writer.WriteUInt32(CurrentAddress);
            writer.WriteBool(IgnoreLoopStart);
            writer.WriteUInt32(Adsr);
            writer.WriteUInt16((ushort)Envelope);
            writer.WriteInt32(EnvelopeCounter);
            writer.WriteByte((byte)Phase);
            writer.WriteInt32(Counter);
            writer.WriteByte(Flags);
            writer.WriteUInt16((ushort)Previous1);
            writer.WriteUInt16((ushort)Previous2);
            writer.WriteBool(Active);
        }

        public void Load(StateReader reader)
        {
            for (var i = 0; i < Samples.Length; i++)
            {
                Samples[i] = (short)reader.ReadUInt16();
            }

            VolumeLeft.Load(reader);
            VolumeRight.Load(reader);
            Pitch = reader.ReadUInt16();
            StartAddress = reader.ReadUInt32() & RamMask;
            RepeatAddress = reader.ReadUInt32() & RamMask;
            CurrentAddress = reader.ReadUInt32() & RamMask;
            IgnoreLoopStart = reader.ReadBool();
            Adsr = reader.ReadUInt32();
            Envelope = (short)reader.ReadUInt16();
            EnvelopeCounter = reader.ReadInt32();
            Phase = (AdsrPhase)(reader.ReadByte() % 5);
            Counter = Math.Clamp(reader.ReadInt32(), 0, (28 << 12) - 1);
            Flags = reader.ReadByte();
            Previous1 = (short)reader.ReadUInt16();
            Previous2 = (short)reader.ReadUInt16();
            Active = reader.ReadBool();
        }
    }

    /// <summary>
    /// A volume register: a fixed volume (bit 15 clear: bits 0–14 are the volume halved), or a sweep that moves the
    /// current level up to $7FFF or down to 0 like an envelope (psx-spx: "SPU Volume and ADSR Generator").
    /// </summary>
    private sealed class Sweep
    {
        private ushort _register;
        private int _counter;

        /// <summary>The current volume, −$8000 to +$7FFF.</summary>
        public int Level { get; private set; }

        public void Reset()
        {
            _register = 0;
            _counter = 0;
            Level = 0;
        }

        public void Set(ushort value)
        {
            _register = value;
            _counter = 0;
            if ((value & 0x8000) == 0)
            {
                Level = (short)(value << 1);
            }
        }

        public void Step()
        {
            if ((_register & 0x8000) == 0)
            {
                return;
            }

            var exponential = (_register & 0x4000) != 0;
            var decrease = (_register & 0x2000) != 0;
            var negative = (_register & 0x1000) != 0;
            var shift = (_register >> 2) & 0x1F;
            var step = decrease ? -8 + (_register & 3) : 7 - (_register & 3);
            var level = Math.Abs(Level);
            if (EnvelopeStep(ref level, ref _counter, exponential, decrease, shift, step))
            {
                Level = negative ? -level : level;
            }
        }

        public void Save(StateWriter writer)
        {
            writer.WriteUInt16(_register);
            writer.WriteInt32(_counter);
            writer.WriteInt32(Level);
        }

        public void Load(StateReader reader)
        {
            _register = reader.ReadUInt16();
            _counter = reader.ReadInt32();
            Level = Math.Clamp(reader.ReadInt32(), -0x8000, 0x7FFF);
        }
    }

    // ---- Save state ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_ram);
        foreach (var register in _registers)
        {
            writer.WriteUInt16(register);
        }

        foreach (var voice in _voices)
        {
            voice.Save(writer);
        }

        foreach (var value in _fifo)
        {
            writer.WriteUInt16(value);
        }

        writer.WriteInt32(_fifoCount);
        writer.WriteUInt16(_control);
        writer.WriteBool(_irqFlag);
        writer.WriteUInt32(_transferAddress);
        writer.WriteUInt32(_pitchModulation);
        writer.WriteUInt32(_noiseOn);
        writer.WriteUInt32(_reverbOn);
        writer.WriteUInt32(_endx);
        _mainLeft.Save(writer);
        _mainRight.Save(writer);
        writer.WriteInt32(_noiseTimer);
        writer.WriteInt32(_noiseLevel);
        writer.WriteInt32(_capturePosition);
        writer.WriteUInt32(_reverbAddress);
        writer.WriteBool(_reverbTick);
        writer.WriteInt32(_reverbInputLeft);
        writer.WriteInt32(_reverbInputRight);
        writer.WriteInt32(_reverbOutputLeft);
        writer.WriteInt32(_reverbOutputRight);
        writer.WriteInt32(Left);
        writer.WriteInt32(Right);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_ram);
        for (var i = 0; i < _registers.Length; i++)
        {
            _registers[i] = reader.ReadUInt16();
        }

        foreach (var voice in _voices)
        {
            voice.Load(reader);
        }

        for (var i = 0; i < _fifo.Length; i++)
        {
            _fifo[i] = reader.ReadUInt16();
        }

        _fifoCount = Math.Clamp(reader.ReadInt32(), 0, _fifo.Length);
        _control = reader.ReadUInt16();
        _irqFlag = reader.ReadBool();
        _transferAddress = reader.ReadUInt32() & RamMask & ~1u;
        _pitchModulation = reader.ReadUInt32();
        _noiseOn = reader.ReadUInt32();
        _reverbOn = reader.ReadUInt32();
        _endx = reader.ReadUInt32();
        _mainLeft.Load(reader);
        _mainRight.Load(reader);
        _noiseTimer = reader.ReadInt32();
        _noiseLevel = reader.ReadInt32() & 0xFFFF;
        _capturePosition = reader.ReadInt32() & 0x1FF;
        _reverbAddress = reader.ReadUInt32() & 0x7FFFE;
        _reverbTick = reader.ReadBool();
        _reverbInputLeft = reader.ReadInt32();
        _reverbInputRight = reader.ReadInt32();
        _reverbOutputLeft = reader.ReadInt32();
        _reverbOutputRight = reader.ReadInt32();
        Left = reader.ReadInt32();
        Right = reader.ReadInt32();
        reader.EndChunk();
    }
}
