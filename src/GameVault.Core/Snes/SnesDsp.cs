using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>
/// The S-DSP (SPEC §7.11, Phase 14): eight voices playing BRR-compressed samples from audio RAM with 4-point
/// interpolation, ADSR and GAIN envelopes, pitch modulation, noise, and echo with its 8-tap FIR filter and feedback.
/// It makes a stereo sample every 32 SPC700 cycles (32 kHz).
/// </summary>
/// <remarks>
/// References: anomie's "S-DSP doc"; fullsnes ("SNES APU DSP"). The interpolation uses a Gaussian kernel computed
/// here, close to (not copied from) the chip's table.
/// </remarks>
public sealed class SnesDsp : IStateful
{
    private static readonly StateTag Tag = StateTag.From("SDSP");

    /// <summary>How many samples apart an envelope or noise step at each rate falls (0 = never).</summary>
    private static readonly int[] RatePeriods =
    [
        0, 2048, 1536, 1280, 1024, 768, 640, 512, 384, 320, 256, 192, 160, 128, 96, 80,
        64, 48, 40, 32, 24, 20, 16, 12, 10, 8, 6, 5, 4, 3, 2, 1,
    ];

    private static readonly short[] Gaussian = BuildGaussian();

    private readonly byte[] _ram;
    private readonly byte[] _registers = new byte[128];
    private readonly Voice[] _voices = new Voice[8];
    private readonly short[] _echoHistoryLeft = new short[8];
    private readonly short[] _echoHistoryRight = new short[8];
    private int _echoPosition;
    private int _echoIndex;
    private int _cycles;
    private int _counter;
    private int _noise = 0x4000;

    public SnesDsp(byte[] ram)
    {
        _ram = ram;
        for (var i = 0; i < 8; i++)
        {
            _voices[i] = new Voice();
        }
    }

    /// <summary>The latest stereo sample.</summary>
    public short Left { get; private set; }

    public short Right { get; private set; }

    /// <summary>Called with each new sample (32 kHz).</summary>
    public Action<short, short>? SampleReady { get; set; }

    public void Reset()
    {
        Array.Clear(_registers);
        _registers[0x6C] = 0xE0; // reset, mute, echo writes off
        foreach (var voice in _voices)
        {
            voice.Reset();
        }

        Array.Clear(_echoHistoryLeft);
        Array.Clear(_echoHistoryRight);
        _echoPosition = _echoIndex = 0;
        _cycles = 0;
        _counter = 0;
        _noise = 0x4000;
        Left = Right = 0;
    }

    public byte Read(int address)
    {
        address &= 0x7F;
        var voice = _voices[address >> 4];
        return (address & 0x0F) switch
        {
            0x08 => (byte)(voice.Envelope >> 4),
            0x09 => (byte)(voice.Output >> 8),
            _ => _registers[address],
        };
    }

    public void Write(int address, byte value)
    {
        address &= 0x7F;
        _registers[address] = value;
        switch (address)
        {
            case 0x4C: // KON
                for (var i = 0; i < 8; i++)
                {
                    if ((value & (1 << i)) != 0)
                    {
                        KeyOn(i);
                    }
                }

                break;
            case 0x5C: // KOFF
                for (var i = 0; i < 8; i++)
                {
                    if ((value & (1 << i)) != 0)
                    {
                        _voices[i].State = EnvelopeState.Release;
                    }
                }

                break;
            case 0x7C: // ENDX: any write clears it
                _registers[0x7C] = 0;
                break;
        }
    }

    public void Run(int spcCycles)
    {
        _cycles += spcCycles;
        while (_cycles >= 32)
        {
            _cycles -= 32;
            Sample();
        }
    }

    private int Directory => _registers[0x5D] << 8;

    private void KeyOn(int index)
    {
        var voice = _voices[index];
        var entry = Directory + _registers[index << 4 | 4] * 4;
        voice.Address = _ram[entry & 0xFFFF] | _ram[(entry + 1) & 0xFFFF] << 8;
        voice.Header = _ram[voice.Address & 0xFFFF];
        voice.BlockOffset = 0;
        voice.Position = 0;
        voice.BufferPosition = 0;
        Array.Clear(voice.Buffer);
        voice.Envelope = 0;
        voice.State = EnvelopeState.Attack;
        voice.Delay = 5;
        NextGroup(index, voice);
        _registers[0x7C] &= (byte)~(1 << index);
    }

    private void Sample()
    {
        var flags = _registers[0x6C];
        if ((flags & 0x80) != 0)
        {
            // Soft reset: every voice released and silent.
            foreach (var v in _voices)
            {
                v.State = EnvelopeState.Release;
                v.Envelope = 0;
            }
        }

        _counter = _counter == 0 ? 0x77FF : _counter - 1; // the global counter the rates divide
        if (Due(flags & 0x1F))
        {
            var feedback = ((_noise << 13) ^ (_noise << 14)) & 0x4000;
            _noise = feedback | (_noise >> 1);
        }

        int mainLeft = 0, mainRight = 0, echoLeft = 0, echoRight = 0;
        var previousOutput = 0;
        for (var i = 0; i < 8; i++)
        {
            var voice = _voices[i];
            var baseRegister = i << 4;
            var output = VoiceOutput(i, voice, previousOutput);
            previousOutput = output;
            voice.Output = (short)output;
            var left = (output * (sbyte)_registers[baseRegister]) >> 7;
            var right = (output * (sbyte)_registers[baseRegister + 1]) >> 7;
            mainLeft = Clamp16(mainLeft + left);
            mainRight = Clamp16(mainRight + right);
            if ((_registers[0x4D] & (1 << i)) != 0)
            {
                echoLeft = Clamp16(echoLeft + left);
                echoRight = Clamp16(echoRight + right);
            }
        }

        var (echoOutLeft, echoOutRight) = Echo(echoLeft, echoRight, flags);
        var outLeft = Clamp16(((mainLeft * (sbyte)_registers[0x0C]) >> 7) + ((echoOutLeft * (sbyte)_registers[0x2C]) >> 7));
        var outRight = Clamp16(((mainRight * (sbyte)_registers[0x1C]) >> 7) + ((echoOutRight * (sbyte)_registers[0x3C]) >> 7));
        if ((flags & 0x40) != 0)
        {
            outLeft = outRight = 0;
        }

        Left = (short)outLeft;
        Right = (short)outRight;
        SampleReady?.Invoke(Left, Right);
    }

    private bool Due(int rate) => rate != 0 && (_counter % RatePeriods[rate]) == 0;

    private static int Clamp16(int value) => Math.Clamp(value, short.MinValue, short.MaxValue);

    private int VoiceOutput(int index, Voice voice, int previousOutput)
    {
        var baseRegister = index << 4;
        if (voice.Delay > 0)
        {
            voice.Delay--;
            return 0;
        }

        // Pitch (14 bits), modulated by the previous voice's output when PMON says so.
        var pitch = (_registers[baseRegister + 2] | _registers[baseRegister + 3] << 8) & 0x3FFF;
        if (index > 0 && (_registers[0x2D] & (1 << index)) != 0)
        {
            pitch += ((previousOutput >> 5) * pitch) >> 10;
            pitch = Math.Clamp(pitch, 0, 0x7FFF);
        }

        int sample;
        if ((_registers[0x3D] & (1 << index)) != 0)
        {
            sample = (short)(_noise << 1);
        }
        else
        {
            // Four taps from the oldest decoded group, weighted by the pitch counter's fraction.
            var fraction = (voice.Position >> 4) & 0xFF;
            var at = voice.BufferPosition + (voice.Position >> 12);
            sample = 0;
            for (var tap = 0; tap < 4; tap++)
            {
                sample += Gaussian[fraction * 4 + tap] * voice.Buffer[(at + tap) % 12] >> 11;
            }

            sample = Clamp16(sample) & ~1;
        }

        AdvanceEnvelope(index, voice);
        var output = (sample * voice.Envelope) >> 11;

        // Step through the BRR data: 4 samples are decoded each time the counter passes 4 samples.
        voice.Position += pitch;
        while (voice.Position >= 0x4000)
        {
            voice.Position -= 0x4000;
            NextGroup(index, voice);
        }

        return output;
    }

    /// <summary>
    /// Decodes the next 4 samples. At the end of a 9-byte block (16 samples) the end flag sets ENDX and either loops
    /// to the sample's loop address or silences the voice.
    /// </summary>
    private void NextGroup(int index, Voice voice)
    {
        if (voice.BlockOffset == 16)
        {
            voice.BlockOffset = 0;
            if ((voice.Header & 1) != 0)
            {
                _registers[0x7C] |= (byte)(1 << index);
                if ((voice.Header & 2) == 0)
                {
                    voice.State = EnvelopeState.Release;
                    voice.Envelope = 0;
                }

                var entry = Directory + _registers[index << 4 | 4] * 4;
                voice.Address = _ram[(entry + 2) & 0xFFFF] | _ram[(entry + 3) & 0xFFFF] << 8;
            }
            else
            {
                voice.Address = (voice.Address + 9) & 0xFFFF;
            }

            voice.Header = _ram[voice.Address];
        }

        DecodeGroup(voice);
        voice.BlockOffset += 4;
    }

    /// <summary>Decodes 4 samples of the current block (2 data bytes) into the ring buffer, with the block's filter.</summary>
    private void DecodeGroup(Voice voice)
    {
        var shift = voice.Header >> 4;
        var filter = (voice.Header >> 2) & 3;
        for (var i = 0; i < 4; i++)
        {
            var sampleIndex = voice.BlockOffset + i;
            var data = _ram[(voice.Address + 1 + (sampleIndex >> 1)) & 0xFFFF];
            int nibble = (sampleIndex & 1) == 0 ? data >> 4 : data & 0x0F;
            nibble = nibble >= 8 ? nibble - 16 : nibble;
            var s = shift <= 12 ? (nibble << shift) >> 1 : (nibble < 0 ? -2048 : 0);
            var p1 = voice.Buffer[(voice.BufferPosition + 11) % 12];
            var p2 = voice.Buffer[(voice.BufferPosition + 10) % 12] >> 1;
            switch (filter)
            {
                case 1:
                    s += p1 >> 1;
                    s += (-p1) >> 5;
                    break;
                case 2:
                    s += p1;
                    s -= p2;
                    s += p2 >> 4;
                    s += (p1 * -3) >> 6;
                    break;
                case 3:
                    s += p1;
                    s -= p2;
                    s += (p1 * -13) >> 7;
                    s += (p2 * 3) >> 4;
                    break;
            }

            s = Clamp16(s);
            s = (short)(s << 1);
            voice.Buffer[voice.BufferPosition] = (short)s;
            voice.BufferPosition = (voice.BufferPosition + 1) % 12;
        }
    }

    private void AdvanceEnvelope(int index, Voice voice)
    {
        var baseRegister = index << 4;
        var adsr1 = _registers[baseRegister + 5];
        var adsr2 = _registers[baseRegister + 6];
        var gain = _registers[baseRegister + 7];
        var envelope = voice.Envelope;
        if (voice.State == EnvelopeState.Release)
        {
            envelope -= 8;
            voice.Envelope = Math.Max(0, envelope);
            return;
        }

        int rate;
        if ((adsr1 & 0x80) != 0)
        {
            switch (voice.State)
            {
                case EnvelopeState.Attack:
                    rate = (adsr1 & 0x0F) * 2 + 1;
                    if (Due(rate))
                    {
                        envelope += rate == 31 ? 1024 : 32;
                    }

                    if (envelope >= 0x7E0)
                    {
                        voice.State = EnvelopeState.Decay;
                    }

                    break;
                case EnvelopeState.Decay:
                    rate = ((adsr1 >> 4) & 7) * 2 + 16;
                    if (Due(rate))
                    {
                        envelope -= ((envelope - 1) >> 8) + 1;
                    }

                    if ((envelope >> 8) == (adsr2 >> 5))
                    {
                        voice.State = EnvelopeState.Sustain;
                    }

                    break;
                default:
                    rate = adsr2 & 0x1F;
                    if (Due(rate))
                    {
                        envelope -= ((envelope - 1) >> 8) + 1;
                    }

                    break;
            }
        }
        else if ((gain & 0x80) == 0)
        {
            envelope = (gain & 0x7F) * 16;
        }
        else if (Due(gain & 0x1F))
        {
            envelope += ((gain >> 5) & 3) switch
            {
                0 => -32,                               // linear decrease
                1 => -(((envelope - 1) >> 8) + 1),      // exponential decrease
                2 => 32,                                // linear increase
                _ => envelope < 0x600 ? 32 : 8,         // bent increase
            };
        }

        voice.Envelope = Math.Clamp(envelope, 0, 0x7FF);
    }

    /// <summary>The echo: an 8-tap FIR over the delayed buffer in audio RAM, with feedback, written back unless FLG turns it off.</summary>
    private (int Left, int Right) Echo(int inputLeft, int inputRight, byte flags)
    {
        var start = _registers[0x6D] << 8;
        var length = (_registers[0x7D] & 0x0F) * 0x800;
        var address = (start + _echoPosition) & 0xFFFF;
        short ReadSample(int at) => (short)(_ram[at & 0xFFFF] | _ram[(at + 1) & 0xFFFF] << 8);

        _echoHistoryLeft[_echoIndex] = (short)(ReadSample(address) >> 1);
        _echoHistoryRight[_echoIndex] = (short)(ReadSample(address + 2) >> 1);
        int firLeft = 0, firRight = 0;
        for (var tap = 0; tap < 8; tap++)
        {
            var coefficient = (sbyte)_registers[(tap << 4) | 0x0F];
            firLeft += (_echoHistoryLeft[(_echoIndex + 1 + tap) & 7] * coefficient) >> 6;
            firRight += (_echoHistoryRight[(_echoIndex + 1 + tap) & 7] * coefficient) >> 6;
        }

        firLeft = Clamp16(firLeft);
        firRight = Clamp16(firRight);
        _echoIndex = (_echoIndex + 1) & 7;

        if ((flags & 0x20) == 0)
        {
            var feedback = (sbyte)_registers[0x0D];
            var writeLeft = Clamp16(inputLeft + ((firLeft * feedback) >> 7)) & ~1;
            var writeRight = Clamp16(inputRight + ((firRight * feedback) >> 7)) & ~1;
            _ram[address] = (byte)writeLeft;
            _ram[(address + 1) & 0xFFFF] = (byte)(writeLeft >> 8);
            _ram[(address + 2) & 0xFFFF] = (byte)writeRight;
            _ram[(address + 3) & 0xFFFF] = (byte)(writeRight >> 8);
        }

        _echoPosition += 4;
        if (_echoPosition >= Math.Max(4, length))
        {
            _echoPosition = 0;
        }

        return (firLeft, firRight);
    }

    /// <summary>
    /// 4-tap weights for each of 256 fractions, in 11-bit fixed point summing to 2048: a Gaussian (σ = 0.63 samples)
    /// over the distances to the two samples on each side, close to the chip's curve.
    /// </summary>
    private static short[] BuildGaussian()
    {
        var table = new short[256 * 4];
        for (var f = 0; f < 256; f++)
        {
            var t = f / 256.0;
            Span<double> weights = [G(1 + t), G(t), G(1 - t), G(2 - t)];
            var sum = weights[0] + weights[1] + weights[2] + weights[3];
            for (var tap = 0; tap < 4; tap++)
            {
                table[f * 4 + tap] = (short)Math.Round(weights[tap] / sum * 2048);
            }
        }

        return table;

        // Exp by its series, with only +, × and ÷, so every machine builds the same table (echo writes land in audio
        // RAM, which save states and netplay compare).
        static double G(double distance)
        {
            var x = -(distance * distance) / (2 * 0.63 * 0.63);
            double term = 1, sum = 1;
            for (var n = 1; n < 40; n++)
            {
                term = term * x / n;
                sum += term;
            }

            return sum;
        }
    }

    private enum EnvelopeState
    {
        Attack,
        Decay,
        Sustain,
        Release,
    }

    private sealed class Voice
    {
        public readonly short[] Buffer = new short[12];
        public int BufferPosition;
        public int Address;
        public byte Header;
        public int BlockOffset;
        public int Position;
        public int Envelope;
        public EnvelopeState State = EnvelopeState.Release;
        public int Delay;
        public short Output;

        public void Reset()
        {
            Array.Clear(Buffer);
            BufferPosition = 0;
            Address = 0;
            Header = 0;
            BlockOffset = 0;
            Position = 0;
            Envelope = 0;
            State = EnvelopeState.Release;
            Delay = 0;
            Output = 0;
        }

        public void Save(StateWriter writer)
        {
            foreach (var value in Buffer)
            {
                writer.WriteUInt16((ushort)value);
            }

            writer.WriteInt32(BufferPosition);
            writer.WriteInt32(Address);
            writer.WriteByte(Header);
            writer.WriteInt32(BlockOffset);
            writer.WriteInt32(Position);
            writer.WriteInt32(Envelope);
            writer.WriteInt32((int)State);
            writer.WriteInt32(Delay);
            writer.WriteUInt16((ushort)Output);
        }

        public void Load(StateReader reader)
        {
            for (var i = 0; i < Buffer.Length; i++)
            {
                Buffer[i] = (short)reader.ReadUInt16();
            }

            BufferPosition = reader.ReadInt32();
            Address = reader.ReadInt32();
            Header = reader.ReadByte();
            BlockOffset = reader.ReadInt32();
            Position = reader.ReadInt32();
            Envelope = reader.ReadInt32();
            State = (EnvelopeState)reader.ReadInt32();
            Delay = reader.ReadInt32();
            Output = (short)reader.ReadUInt16();
        }
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_registers);
        foreach (var voice in _voices)
        {
            voice.Save(writer);
        }

        foreach (var value in _echoHistoryLeft)
        {
            writer.WriteUInt16((ushort)value);
        }

        foreach (var value in _echoHistoryRight)
        {
            writer.WriteUInt16((ushort)value);
        }

        writer.WriteInt32(_echoPosition);
        writer.WriteInt32(_echoIndex);
        writer.WriteInt32(_cycles);
        writer.WriteInt32(_counter);
        writer.WriteInt32(_noise);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_registers);
        foreach (var voice in _voices)
        {
            voice.Load(reader);
        }

        for (var i = 0; i < 8; i++)
        {
            _echoHistoryLeft[i] = (short)reader.ReadUInt16();
        }

        for (var i = 0; i < 8; i++)
        {
            _echoHistoryRight[i] = (short)reader.ReadUInt16();
        }

        _echoPosition = reader.ReadInt32();
        _echoIndex = reader.ReadInt32();
        _cycles = reader.ReadInt32();
        _counter = reader.ReadInt32();
        _noise = reader.ReadInt32();
        reader.EndChunk();
    }
}
