using GameVault.Core.PlayStation;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

public sealed partial class Ps2Spu2
{
    /// <summary>
    /// Produces one 48 kHz sample: key offs then key ons written since the last sample, both cores' voices, core 0's
    /// mix into core 1's external input, and core 1's output (PCSX2: TimeUpdate, spu2Mix).
    /// </summary>
    public void Tick()
    {
        for (var c = 0; c < CoreCount; c++)
        {
            ApplyKeys(c);
        }

        var position = (uint)_bufferPosition;

        // Core 0: no external input; its output, after its master volume, is written to $800/$A00 and feeds core 1.
        MixCore(0, 0, 0, out var left0, out var right0);
        _cores[0].MasterLeft.Step();
        _cores[0].MasterRight.Step();
        var externalLeft = Clamp16(left0) * _cores[0].MasterLeft.Level >> 15;
        var externalRight = Clamp16(right0) * _cores[0].MasterRight.Level >> 15;
        WriteBuffer(0x800 + position, externalLeft);
        WriteBuffer(0xA00 + position, externalRight);

        MixCore(1, externalLeft * Ext(1, ExtAvolL) >> 15, externalRight * Ext(1, ExtAvolR) >> 15, out var left1, out var right1);
        _cores[1].MasterLeft.Step();
        _cores[1].MasterRight.Step();
        Left = (short)Clamp16(Clamp16(left1) * _cores[1].MasterLeft.Level >> 15);
        Right = (short)Clamp16(Clamp16(right1) * _cores[1].MasterRight.Level >> 15);

        _bufferPosition = (_bufferPosition + 1) & 0x1FF;
        SampleReady?.Invoke(Left, Right);
    }

    /// <summary>
    /// One core's mix: the voices (dry and wet, per VMIXL/VMIXR/VMIXEL/VMIXER), the MEMIN input (after BVOL) and the
    /// external input, each gated into the dry and wet paths by MMIX; then the reverb on the wet path, after EVOL.
    /// MMIX bits (PCSX2: REG_P_MMIX): 0/1 wet external R/L, 2/3 dry external R/L, 4/5 wet input R/L, 6/7 dry input
    /// R/L, 8/9 wet voices R/L, 10/11 dry voices R/L; core 0 has no external input.
    /// </summary>
    private void MixCore(int c, int externalLeft, int externalRight, out int outLeft, out int outRight)
    {
        var core = _cores[c];
        var position = (uint)_bufferPosition;
        var attr = Reg(c, RegAttr);
        StepNoise(core, attr);

        var pmon = VoiceMask(c, RegPmon);
        var non = VoiceMask(c, RegNon);
        var vmixL = VoiceMask(c, RegVmixL);
        var vmixR = VoiceMask(c, RegVmixR);
        var vmixEL = VoiceMask(c, RegVmixEL);
        var vmixER = VoiceMask(c, RegVmixER);

        int dryLeft = 0, dryRight = 0, wetLeft = 0, wetRight = 0;
        var previousOutput = 0;
        var voices = core.Voices;
        var outputBase = c == 0 ? 0x400u : 0xC00u;
        for (var i = 0; i < VoiceCount; i++)
        {
            var voice = voices[i];
            var bit = 1u << i;
            var output = VoiceSample(core, voice, i, (non & bit) != 0, (pmon & bit) != 0 && i > 0, previousOutput);
            voice.VolumeLeft.Step();
            voice.VolumeRight.Step();
            var left = output * voice.VolumeLeft.Level >> 15;
            var right = output * voice.VolumeRight.Level >> 15;
            if ((vmixL & bit) != 0)
            {
                dryLeft += left;
            }

            if ((vmixR & bit) != 0)
            {
                dryRight += right;
            }

            if ((vmixEL & bit) != 0)
            {
                wetLeft += left;
            }

            if ((vmixER & bit) != 0)
            {
                wetRight += right;
            }

            // Voices 1 and 3 are written to sound RAM after their envelope (PCSX2: MixVoice).
            if (i == 1)
            {
                WriteBuffer(outputBase + position, output);
            }
            else if (i == 3)
            {
                WriteBuffer(outputBase + 0x200 + position, output);
            }

            previousOutput = output;
        }

        dryLeft = Clamp16(dryLeft);
        dryRight = Clamp16(dryRight);
        wetLeft = Clamp16(wetLeft);
        wetRight = Clamp16(wetRight);
        var mixBase = c == 0 ? 0x1000u : 0x1800u;
        WriteBuffer(mixBase + position, dryLeft);
        WriteBuffer(mixBase + 0x200 + position, dryRight);
        WriteBuffer(mixBase + 0x400 + position, wetLeft);
        WriteBuffer(mixBase + 0x600 + position, wetRight);

        // MEMIN: the ADMA (or program-written) PCM input at $2000/$2200 (+$400 for core 1).
        var inputAddress = 0x2000u + (uint)c * 0x400 + position;
        CheckIrq(inputAddress);
        CheckIrq(inputAddress + 0x200);
        var inputLeft = ReadRam(inputAddress) * Ext(c, ExtBvolL) >> 15;
        var inputRight = ReadRam(inputAddress + 0x200) * Ext(c, ExtBvolR) >> 15;

        int mmix = Reg(c, RegMmix);
        if (c == 0)
        {
            mmix &= 0xFF0;
        }

        var totalDryLeft = Gate(mmix, 0x800, dryLeft) + Gate(mmix, 0x080, inputLeft) + Gate(mmix, 0x008, externalLeft);
        var totalDryRight = Gate(mmix, 0x400, dryRight) + Gate(mmix, 0x040, inputRight) + Gate(mmix, 0x004, externalRight);
        var totalWetLeft = Gate(mmix, 0x200, wetLeft) + Gate(mmix, 0x020, inputLeft) + Gate(mmix, 0x002, externalLeft);
        var totalWetRight = Gate(mmix, 0x100, wetRight) + Gate(mmix, 0x010, inputRight) + Gate(mmix, 0x001, externalRight);

        RunReverb(c, attr, Clamp16(totalWetLeft), Clamp16(totalWetRight));
        outLeft = totalDryLeft + (core.ReverbOutputLeft * Ext(c, ExtEvolL) >> 15);
        outRight = totalDryRight + (core.ReverbOutputRight * Ext(c, ExtEvolR) >> 15);
    }

    private static int Gate(int mmix, int bit, int value) => (mmix & bit) != 0 ? value : 0;

    private void WriteBuffer(uint address, int value)
    {
        CheckIrq(address);
        WriteRam(address, Clamp16(value));
    }

    private static int Clamp16(int value) => Math.Clamp(value, short.MinValue, short.MaxValue);

    // ---- Voices ----

    /// <summary>Key offs, then key ons, latched since the last sample (PCSX2: TimeUpdate's StopVoices/StartVoices).</summary>
    private void ApplyKeys(int c)
    {
        var core = _cores[c];
        var off = core.PendingKeyOff;
        var on = core.PendingKeyOn;
        core.PendingKeyOff = core.PendingKeyOn = 0;
        if ((off | on) == 0)
        {
            return;
        }

        for (var i = 0; i < VoiceCount; i++)
        {
            var voice = core.Voices[i];
            if ((off & (1u << i)) != 0 && voice.Phase != AdsrPhase.Off)
            {
                voice.Phase = AdsrPhase.Release;
                voice.EnvelopeCounter = 0;
            }

            if ((on & (1u << i)) != 0)
            {
                // Key on: decoding restarts at SSA; a loop-start flag sets LSAX again until LSAX is written.
                voice.CurrentAddress = voice.StartAddress;
                voice.IgnoreLoopStart = false;
                voice.Counter = 0;
                voice.Envelope = 0;
                voice.EnvelopeCounter = 0;
                voice.Phase = AdsrPhase.Attack;
                voice.Previous1 = voice.Previous2 = 0;
                Array.Clear(voice.Samples);
                DecodeBlock(voice, keepHistory: false);
            }
        }

        core.Endx &= ~on;
    }

    /// <summary>
    /// One voice's output for this sample (after its envelope; 0 while off), then the pitch counter's step. A voice
    /// keeps walking through sound RAM, setting ENDX, even when its envelope is off (as PCSX2's DecodeSamples).
    /// </summary>
    private int VoiceSample(Core core, Voice voice, int index, bool noise, bool modulated, int previousOutput)
    {
        var output = 0;
        if (voice.Phase != AdsrPhase.Off)
        {
            int sample;
            if (noise)
            {
                sample = (short)core.NoiseLevel;
            }
            else
            {
                // Counter bits 4–11 pick the Gaussian weights, bits 12 up the sample (psx-spx: "SPU ADPCM Pitch").
                var g = (voice.Counter >> 4) & 0xFF;
                var s = voice.Counter >> 12;
                var samples = voice.Samples;
                sample = (Gauss[0xFF - g] * samples[s] >> 15)
                    + (Gauss[0x1FF - g] * samples[s + 1] >> 15)
                    + (Gauss[0x100 + g] * samples[s + 2] >> 15)
                    + (Gauss[g] * samples[s + 3] >> 15);
            }

            StepEnvelope(voice);
            output = sample * voice.Envelope >> 15;
        }

        // Pitch: up to $3FFF (just under 4×); modulation scales it by the previous voice's output (PCSX2: UpdatePitch).
        int step = voice.Pitch;
        if (modulated)
        {
            step = Math.Clamp(step * (0x8000 + previousOutput) >> 15, 0, 0x3FFF);
        }

        voice.Counter += Math.Min(step, 0x3FFF);
        if (voice.Counter >= 28 << 12)
        {
            voice.Counter -= 28 << 12;
            EndBlock(core, voice, index);
        }

        return output;
    }

    /// <summary>
    /// The end of a block's 28 samples: the loop-end flag sets ENDX and jumps to LSAX, and without the repeat flag
    /// also stops the voice (PCSX2: DecodeSamples; psx-spx: "SPU ADPCM Samples").
    /// </summary>
    private void EndBlock(Core core, Voice voice, int index)
    {
        if ((voice.Flags & 1) != 0)
        {
            core.Endx |= 1u << index;
            voice.CurrentAddress = voice.RepeatAddress;
            if ((voice.Flags & 2) == 0)
            {
                voice.Phase = AdsrPhase.Off;
                voice.Envelope = 0;
            }
        }
        else
        {
            voice.CurrentAddress = (voice.CurrentAddress + 8) & AddressMask;
        }

        DecodeBlock(voice, keepHistory: true);
    }

    /// <summary>
    /// Decodes the 8-halfword block at the voice's address with the PlayStation SPU's decoder, keeping the last three
    /// samples of the previous block for the interpolation.
    /// </summary>
    private void DecodeBlock(Voice voice, bool keepHistory)
    {
        var address = voice.CurrentAddress & AddressMask & ~7u;
        voice.CurrentAddress = address;
        CheckBlockIrq(address);

        var samples = voice.Samples;
        if (keepHistory)
        {
            samples[0] = samples[28];
            samples[1] = samples[29];
            samples[2] = samples[30];
        }

        var block = _ram.AsSpan((int)address * 2, 16);
        voice.Flags = block[1];
        if ((voice.Flags & 4) != 0 && !voice.IgnoreLoopStart)
        {
            voice.RepeatAddress = address;
        }

        PsSpu.DecodeAdpcm(block, ref voice.Previous1, ref voice.Previous2, samples.AsSpan(3, 28));
    }

    // ---- ADSR and volumes ----

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
    /// One sample of an envelope (ADSR or sweep), as the PlayStation SPU's: it waits 1 SHL max(0, shift − 11)
    /// samples, then adds step SHL max(0, 11 − shift), four times slower for an exponential increase above $6000, and
    /// scaled by the level for an exponential decrease; the level saturates to 0–$7FFF. Returns whether it changed.
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

    /// <summary>The noise generator, clocked by ATTR bits 8–13 laid out as the PlayStation's SPUCNT (psx-spx: "SPU Noise Generator").</summary>
    private static void StepNoise(Core core, ushort attr)
    {
        var shift = (attr >> 10) & 0xF;
        var step = 4 + ((attr >> 8) & 3);
        core.NoiseTimer -= step;
        var level = core.NoiseLevel;
        var parity = ((level >> 15) ^ (level >> 12) ^ (level >> 11) ^ (level >> 10) ^ 1) & 1;
        if (core.NoiseTimer < 0)
        {
            core.NoiseLevel = ((level << 1) | parity) & 0xFFFF;
            core.NoiseTimer += 0x20000 >> shift;
            if (core.NoiseTimer < 0)
            {
                core.NoiseTimer += 0x20000 >> shift;
            }
        }
    }

    // ---- Reverb ----

    // The reverb address registers' order from $2E4, two halfwords each (PCSX2: regs.h).
    private const int ApfSize1 = 0, ApfSize2 = 1, SameLDst = 2, SameRDst = 3, Comb1L = 4, Comb1R = 5, Comb2L = 6,
        Comb2R = 7, SameLSrc = 8, SameRSrc = 9, DiffLDst = 10, DiffRDst = 11, Comb3L = 12, Comb3R = 13, Comb4L = 14,
        Comb4R = 15, DiffLSrc = 16, DiffRSrc = 17, Apf1LDst = 18, Apf1RDst = 19, Apf2LDst = 20, Apf2RDst = 21;

    // The coefficients' order from $774 (+$28 for core 1).
    private const int VIir = 0, VComb1 = 1, VComb2 = 2, VComb3 = 3, VComb4 = 4, VWall = 5, VApf1 = 6, VApf2 = 7,
        VInLeft = 8, VInRight = 9;

    private uint ReverbOffset(int c, int index) => AddressPair(c, RegReverbAddress + index * 4);

    private short ReverbCoefficient(int c, int index) => Ext(c, ExtReverbCoefficients + index * 2);

    /// <summary>
    /// An address relative to the reverb's current position, wrapped within ESA–EEA (EEA's register holds the high
    /// bits; its low 16 bits are all ones) (PCSX2: RevbGetIndexer).
    /// </summary>
    private uint ReverbAddress(Core core, uint start, long size, long offset)
    {
        var relative = (core.ReverbPosition + offset) % size;
        if (relative < 0)
        {
            relative += size;
        }

        return (uint)(start + relative) & AddressMask;
    }

    private int ReverbRead(int c, Core core, uint start, long size, long offset, bool checkIrq)
    {
        var address = ReverbAddress(core, start, size, offset);
        if (checkIrq)
        {
            CheckIrq(address);
        }

        return ReadRam(address);
    }

    private static int Mul(int a, int b) => a * b >> 15;

    /// <summary>
    /// The core's reverb at 24 kHz: same-side and different-side reflections, four combs and two all-pass filters in
    /// the work area, as the PlayStation's (psx-spx: "SPU Reverb Formula"; PCSX2: DoReverb). Reads always happen;
    /// writes only with ATTR's effects enable. The output holds between reverb samples.
    /// </summary>
    private void RunReverb(int c, ushort attr, int inputLeft, int inputRight)
    {
        var core = _cores[c];
        core.ReverbTick = !core.ReverbTick;
        if (core.ReverbTick)
        {
            core.ReverbInputLeft = inputLeft;
            core.ReverbInputRight = inputRight;
            return;
        }

        var start = AddressPair(c, RegEsa) & AddressMask;
        var end = (uint)(Reg(c, RegEea) & 0xF) << 16 | 0xFFFF;
        if (start >= end)
        {
            core.ReverbOutputLeft = core.ReverbOutputRight = 0;
            return;
        }

        var size = (long)end - start + 1;
        var enabled = (attr & AttrEffectsEnable) != 0;

        var lin = Mul(ReverbCoefficient(c, VInLeft), (inputLeft + core.ReverbInputLeft) >> 1);
        var rin = Mul(ReverbCoefficient(c, VInRight), (inputRight + core.ReverbInputRight) >> 1);
        var wall = ReverbCoefficient(c, VWall);
        var iir = ReverbCoefficient(c, VIir);

        int Read(long offset) => ReverbRead(c, core, start, size, offset, enabled);

        void Write(long offset, int value)
        {
            if (!enabled)
            {
                return;
            }

            var address = ReverbAddress(core, start, size, offset);
            CheckIrq(address);
            WriteRam(address, Clamp16(value));
        }

        long O(int index) => ReverbOffset(c, index);

        // Same-side reflection.
        var lsamePrev = Read(O(SameLDst) - 1);
        var rsamePrev = Read(O(SameRDst) - 1);
        Write(O(SameLDst), Mul(lin + Mul(Read(O(SameLSrc)), wall) - lsamePrev, iir) + lsamePrev);
        Write(O(SameRDst), Mul(rin + Mul(Read(O(SameRSrc)), wall) - rsamePrev, iir) + rsamePrev);

        // Different-side reflection.
        var ldiffPrev = Read(O(DiffLDst) - 1);
        var rdiffPrev = Read(O(DiffRDst) - 1);
        Write(O(DiffLDst), Mul(lin + Mul(Read(O(DiffRSrc)), wall) - ldiffPrev, iir) + ldiffPrev);
        Write(O(DiffRDst), Mul(rin + Mul(Read(O(DiffLSrc)), wall) - rdiffPrev, iir) + rdiffPrev);

        // Early echo (combs).
        var comb1 = ReverbCoefficient(c, VComb1);
        var comb2 = ReverbCoefficient(c, VComb2);
        var comb3 = ReverbCoefficient(c, VComb3);
        var comb4 = ReverbCoefficient(c, VComb4);
        var lout = Mul(comb1, Read(O(Comb1L))) + Mul(comb2, Read(O(Comb2L)))
            + Mul(comb3, Read(O(Comb3L))) + Mul(comb4, Read(O(Comb4L)));
        var rout = Mul(comb1, Read(O(Comb1R))) + Mul(comb2, Read(O(Comb2R)))
            + Mul(comb3, Read(O(Comb3R))) + Mul(comb4, Read(O(Comb4R)));

        // Late reverb: two all-pass filters.
        var vapf1 = ReverbCoefficient(c, VApf1);
        var vapf2 = ReverbCoefficient(c, VApf2);
        var lapf1 = Read(O(Apf1LDst) - O(ApfSize1));
        var rapf1 = Read(O(Apf1RDst) - O(ApfSize1));
        lout = Clamp16(lout - Mul(vapf1, lapf1));
        rout = Clamp16(rout - Mul(vapf1, rapf1));
        Write(O(Apf1LDst), lout);
        Write(O(Apf1RDst), rout);
        lout = Mul(lout, vapf1) + lapf1;
        rout = Mul(rout, vapf1) + rapf1;

        var lapf2 = Read(O(Apf2LDst) - O(ApfSize2));
        var rapf2 = Read(O(Apf2RDst) - O(ApfSize2));
        lout = Clamp16(lout - Mul(vapf2, lapf2));
        rout = Clamp16(rout - Mul(vapf2, rapf2));
        Write(O(Apf2LDst), lout);
        Write(O(Apf2RDst), rout);
        lout = Mul(lout, vapf2) + lapf2;
        rout = Mul(rout, vapf2) + rapf2;

        core.ReverbOutputLeft = Clamp16(lout);
        core.ReverbOutputRight = Clamp16(rout);
        core.ReverbPosition = (uint)((core.ReverbPosition + 1) % size);
    }

    // ---- State ----

    private sealed class Core
    {
        public readonly Voice[] Voices = new Voice[VoiceCount];
        public readonly Sweep MasterLeft = new();
        public readonly Sweep MasterRight = new();
        public uint PendingKeyOn;
        public uint PendingKeyOff;
        public uint TransferAddress;
        public uint Endx;
        public ushort Statx;
        public int NoiseTimer;
        public int NoiseLevel;
        public uint ReverbPosition;
        public bool ReverbTick;
        public int ReverbInputLeft;
        public int ReverbInputRight;
        public int ReverbOutputLeft;
        public int ReverbOutputRight;
        public int AdmaHalf;
        public int AdmaFill;

        public Core()
        {
            for (var i = 0; i < VoiceCount; i++)
            {
                Voices[i] = new Voice();
            }
        }

        public void Reset()
        {
            foreach (var voice in Voices)
            {
                voice.Reset();
            }

            MasterLeft.Reset();
            MasterRight.Reset();
            PendingKeyOn = PendingKeyOff = 0;
            TransferAddress = 0;
            Endx = 0xFFFFFF;
            Statx = 0x80;
            NoiseTimer = 0;
            NoiseLevel = 1;
            ReverbPosition = 0;
            ReverbTick = false;
            ReverbInputLeft = ReverbInputRight = 0;
            ReverbOutputLeft = ReverbOutputRight = 0;
            AdmaHalf = 1;
            AdmaFill = 0;
        }

        public void Save(StateWriter writer)
        {
            foreach (var voice in Voices)
            {
                voice.Save(writer);
            }

            MasterLeft.Save(writer);
            MasterRight.Save(writer);
            writer.WriteUInt32(PendingKeyOn);
            writer.WriteUInt32(PendingKeyOff);
            writer.WriteUInt32(TransferAddress);
            writer.WriteUInt32(Endx);
            writer.WriteUInt16(Statx);
            writer.WriteInt32(NoiseTimer);
            writer.WriteInt32(NoiseLevel);
            writer.WriteUInt32(ReverbPosition);
            writer.WriteBool(ReverbTick);
            writer.WriteInt32(ReverbInputLeft);
            writer.WriteInt32(ReverbInputRight);
            writer.WriteInt32(ReverbOutputLeft);
            writer.WriteInt32(ReverbOutputRight);
            writer.WriteInt32(AdmaHalf);
            writer.WriteInt32(AdmaFill);
        }

        public void Load(StateReader reader)
        {
            foreach (var voice in Voices)
            {
                voice.Load(reader);
            }

            MasterLeft.Load(reader);
            MasterRight.Load(reader);
            PendingKeyOn = reader.ReadUInt32() & 0xFFFFFF;
            PendingKeyOff = reader.ReadUInt32() & 0xFFFFFF;
            TransferAddress = reader.ReadUInt32() & AddressMask;
            Endx = reader.ReadUInt32() & 0xFFFFFF;
            Statx = reader.ReadUInt16();
            NoiseTimer = reader.ReadInt32();
            NoiseLevel = reader.ReadInt32() & 0xFFFF;
            ReverbPosition = reader.ReadUInt32() & AddressMask;
            ReverbTick = reader.ReadBool();
            ReverbInputLeft = reader.ReadInt32();
            ReverbInputRight = reader.ReadInt32();
            ReverbOutputLeft = reader.ReadInt32();
            ReverbOutputRight = reader.ReadInt32();
            AdmaHalf = reader.ReadInt32() & 1;
            AdmaFill = Math.Clamp(reader.ReadInt32(), 0, 511);
        }
    }

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

        /// <summary>NAX: the next halfword the decoder reads (header at +0, four samples per halfword after it).</summary>
        public uint NextAddress => CurrentAddress + 1 + (uint)((Counter >> 12) >> 2);

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
            StartAddress = reader.ReadUInt32() & AddressMask & ~7u;
            RepeatAddress = reader.ReadUInt32() & AddressMask & ~7u;
            CurrentAddress = reader.ReadUInt32() & AddressMask & ~7u;
            IgnoreLoopStart = reader.ReadBool();
            Adsr = reader.ReadUInt32();
            Envelope = (short)reader.ReadUInt16();
            EnvelopeCounter = reader.ReadInt32();
            Phase = (AdsrPhase)(reader.ReadByte() % 5);
            Counter = Math.Clamp(reader.ReadInt32(), 0, (28 << 12) - 1);
            Flags = reader.ReadByte();
            Previous1 = (short)reader.ReadUInt16();
            Previous2 = (short)reader.ReadUInt16();
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

        /// <summary>The current volume, −$8000 to +$7FFF (VOLX).</summary>
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
}
