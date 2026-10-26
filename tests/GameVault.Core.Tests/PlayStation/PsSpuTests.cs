using FluentAssertions;
using GameVault.Core.PlayStation;
using GameVault.Core.State;

namespace GameVault.Core.Tests.PlayStation;

/// <summary>The PlayStation SPU (SPEC §7.11, Phase 15), with ADPCM blocks built in the tests.</summary>
public sealed class PsSpuTests
{
    private const uint SpuCnt = 0x1AA;
    private const uint SpuStat = 0x1AE;
    private const uint TransferAddress = 0x1A6;
    private const uint Fifo = 0x1A8;
    private const uint TransferControl = 0x1AC;
    private const ushort Enabled = 0xC000; // SPU on, unmuted

    private readonly PsSpu _spu = new();

    public PsSpuTests()
    {
        _spu.Power();
        _spu.Write16(SpuCnt, Enabled);
        _spu.Write16(0x180, 0x3FFF); // main volume
        _spu.Write16(0x182, 0x3FFF);
    }

    /// <summary>A 16-byte ADPCM block: shift and filter, flags, then 28 nibbles (low nibble first).</summary>
    private static byte[] Block(int shift, int filter, int flags, params int[] nibbles)
    {
        var block = new byte[16];
        block[0] = (byte)(shift | filter << 4);
        block[1] = (byte)flags;
        for (var i = 0; i < 28; i++)
        {
            var nibble = nibbles[i % nibbles.Length] & 0xF;
            block[2 + i / 2] |= (byte)((i & 1) == 0 ? nibble : nibble << 4);
        }

        return block;
    }

    /// <summary>Writes bytes to sound RAM by the documented manual-write sequence (psx-spx: "SPU Memory Access").</summary>
    private void ManualWrite(uint address, byte[] data)
    {
        _spu.Write16(TransferControl, 0x0004);
        _spu.Write16(SpuCnt, Enabled);
        _spu.Write16(TransferAddress, (ushort)(address / 8));
        for (var i = 0; i < data.Length; i += 2)
        {
            _spu.Write16(Fifo, (ushort)(data[i] | (i + 1 < data.Length ? data[i + 1] << 8 : 0)));
        }

        _spu.Write16(SpuCnt, Enabled | 1 << 4);
        _spu.Write16(SpuCnt, Enabled);
    }

    private byte[] DmaRead(uint address, int length)
    {
        _spu.Write16(TransferAddress, (ushort)(address / 8));
        _spu.Write16(SpuCnt, Enabled | 3 << 4);
        var data = new byte[length];
        for (var i = 0; i < length; i += 4)
        {
            var word = _spu.DmaRead();
            for (var b = 0; b < 4 && i + b < length; b++)
            {
                data[i + b] = (byte)(word >> (8 * b));
            }
        }

        _spu.Write16(SpuCnt, Enabled);
        return data;
    }

    /// <summary>Sets voice <paramref name="voice"/> to play from <paramref name="address"/> at full volume with a fast attack.</summary>
    private void SetUpVoice(int voice, uint address, ushort pitch = 0x1000, uint adsr = 0x0000_000F)
    {
        var b = (uint)voice * 0x10;
        _spu.Write16(b + 0x0, 0x3FFF);
        _spu.Write16(b + 0x2, 0x3FFF);
        _spu.Write16(b + 0x4, pitch);
        _spu.Write16(b + 0x6, (ushort)(address / 8));
        _spu.Write16(b + 0x8, (ushort)adsr);
        _spu.Write16(b + 0xA, (ushort)(adsr >> 16));
    }

    private void KeyOn(int voice) => _spu.Write16(voice < 16 ? 0x188u : 0x18Au, (ushort)(1 << (voice & 15)));

    private void KeyOff(int voice) => _spu.Write16(voice < 16 ? 0x18Cu : 0x18Eu, (ushort)(1 << (voice & 15)));

    private List<int> Run(int samples)
    {
        var output = new List<int>(samples);
        _spu.SampleReady = (left, _) => output.Add(left);
        for (var i = 0; i < samples; i++)
        {
            _spu.Tick();
        }

        return output;
    }

    private static int SignChanges(List<int> samples)
    {
        var changes = 0;
        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i - 1] > 0 != samples[i] > 0)
            {
                changes++;
            }
        }

        return changes;
    }

    /// <summary>One looping block: 14 samples high then 14 low, a square wave 28 samples long.</summary>
    private static byte[] SquareLoop() =>
        Block(0, 0, 0b111, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9);

    // ---- Transfers ----

    [Fact]
    public void Manual_writes_and_dma_round_trip_through_sound_ram()
    {
        var text = "Hello there   :)"u8.ToArray();
        ManualWrite(0x1000, text);
        DmaRead(0x1000, text.Length).Should().Equal(text, "ps1-tests spu/memory-transfer reads back what was written");

        _spu.Write16(TransferAddress, 0x2000 / 8);
        _spu.Write16(SpuCnt, Enabled | 2 << 4);
        _spu.DmaWrite(0x44332211);
        _spu.DmaWrite(0x88776655);
        _spu.Ram[0x2000..0x2008].ToArray().Should().Equal(0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88);
    }

    [Fact]
    public void Spustat_mirrors_the_control_bits_and_the_transfer_control_reads_back()
    {
        foreach (var bits in new ushort[] { 0x3F, 0x00, 0x15, 0x2A })
        {
            _spu.Write16(SpuCnt, (ushort)(Enabled | bits));
            (_spu.Read16(SpuStat) & 0x3F).Should().Be(bits);
        }

        _spu.Write16(SpuCnt, Enabled | 2 << 4);
        (_spu.Read16(SpuStat) & 0x0100).Should().NotBe(0, "DMA write request in DMA-write mode");

        foreach (var value in new ushort[] { 0xFFFF, 0x5555, 0xAAAA, 0x0000 })
        {
            _spu.Write16(TransferControl, value);
            _spu.Read16(TransferControl).Should().Be(value);
        }
    }

    // ---- ADPCM ----

    [Fact]
    public void Adpcm_blocks_decode_with_the_shift_and_prediction_filter()
    {
        // Filter 1 (60/64 of the previous sample), shift 12, every nibble 1: s = 1 + (prev × 60 + 32) >> 6.
        var block = Block(12, 1, 0, 1);
        short previous1 = 0, previous2 = 0;
        var output = new short[28];
        PsSpu.DecodeAdpcm(block, ref previous1, ref previous2, output);

        int expected = 0, old = 0, older = 0;
        for (var i = 0; i < 28; i++)
        {
            expected = 1 + ((old * 60 + older * 0 + 32) >> 6);
            output[i].Should().Be((short)expected, $"sample {i}");
            older = old;
            old = expected;
        }

        previous1.Should().Be((short)expected);

        // Shift 0 puts the nibble at the top: 7 → $7000, 9 (−7) → −$7000.
        var square = new short[28];
        previous1 = previous2 = 0;
        PsSpu.DecodeAdpcm(SquareLoop(), ref previous1, ref previous2, square);
        square[0].Should().Be(0x7000);
        square[27].Should().Be(-0x7000);
    }

    // ---- Voices ----

    [Fact]
    public void A_voice_plays_its_sample_at_the_pitch_it_is_given()
    {
        ManualWrite(0x1000, SquareLoop());
        SetUpVoice(0, 0x1000, pitch: 0x1000);
        KeyOn(0);
        Run(100);
        SignChanges(Run(28 * 50)).Should().BeInRange(99, 101, "pitch $1000 plays one sample per tick: 50 periods");

        _spu.Write16(0x004, 0x0800); // half speed
        SignChanges(Run(28 * 50)).Should().BeInRange(49, 51);
    }

    [Fact]
    public void Adsr_attack_reaches_full_volume_and_release_decays_to_zero()
    {
        ManualWrite(0x1000, SquareLoop());
        // Linear attack, shift 3, step +7 (+1792 a sample); sustain level max; linear release, shift 5 (−512 a sample).
        SetUpVoice(0, 0x1000, adsr: 0x0005_0C0F);
        KeyOn(0);
        Run(1);
        var early = (short)_spu.Read16(0x00C);
        Run(40);
        _spu.Read16(0x00C).Should().Be(0x7FFF);
        early.Should().BeInRange(1, 0x7FFE);

        KeyOff(0);
        Run(5);
        ((short)_spu.Read16(0x00C)).Should().BeInRange(1, 0x7FFE, "release is under way");
        Run(100);
        _spu.Read16(0x00C).Should().Be(0);
    }

    [Fact]
    public void Loop_end_sets_endx_and_a_one_shot_block_stops_the_voice()
    {
        ManualWrite(0x1000, Block(0, 0, 0b001, 7)); // end without repeat
        ManualWrite(0x1010, SquareLoop());
        SetUpVoice(0, 0x1000);
        SetUpVoice(1, 0x1010);
        KeyOn(0);
        KeyOn(1);
        Run(10);
        _spu.Read16(0x19C).Should().Be(0);

        Run(30);
        _spu.Read16(0x19C).Should().Be(0b11, "both voices passed a loop-end flag");
        _spu.Read16(0x00C).Should().Be(0, "the one-shot voice was released at volume zero");
        ((short)_spu.Read16(0x01C)).Should().BeGreaterThan(0, "the looping voice keeps playing");
        _spu.Read16(0x01E).Should().Be(0x1010 / 8, "the loop-start flag set the repeat address");

        KeyOn(1);
        _spu.Read16(0x19C).Should().Be(0b01, "key on clears the voice's ENDX bit");
    }

    [Fact]
    public void Noise_mode_replaces_the_sample_with_the_noise_generator()
    {
        ManualWrite(0x1000, Block(0, 0, 0b111, 0)); // silence
        SetUpVoice(0, 0x1000);
        _spu.Write16(SpuCnt, Enabled | 0x3F00); // fastest noise
        _spu.Write16(0x194, 1);
        KeyOn(0);
        Run(10);

        SignChanges(Run(1000)).Should().BeGreaterThan(100);
    }

    // ---- IRQ ----

    [Fact]
    public void The_irq_fires_when_a_voice_reads_the_irq_address_and_is_acknowledged_through_spucnt()
    {
        var irqs = 0;
        _spu.Irq = () => irqs++;
        ManualWrite(0x1000, SquareLoop());
        ManualWrite(0x1010, SquareLoop());
        _spu.Write16(0x1A4, 0x1010 / 8);
        _spu.Write16(SpuCnt, Enabled | 1 << 6);
        SetUpVoice(0, 0x1000);
        KeyOn(0);
        Run(100);
        irqs.Should().Be(0, "the voice loops on its first block");

        _spu.Write16(0x00E, 0x1010 / 8); // redirect the loop to the IRQ block
        Run(60);
        irqs.Should().Be(1, "the flag stays set until acknowledged");
        (_spu.Read16(SpuStat) & 0x40).Should().NotBe(0);

        _spu.Write16(SpuCnt, Enabled);
        (_spu.Read16(SpuStat) & 0x40).Should().Be(0);
    }

    [Fact]
    public void Transfers_to_the_irq_address_raise_the_irq()
    {
        var irqs = 0;
        _spu.Irq = () => irqs++;
        _spu.Write16(0x1A4, 0x3000 / 8);
        _spu.Write16(SpuCnt, Enabled | 1 << 6);
        _spu.Write16(TransferAddress, 0x3000 / 8);
        _spu.Write16(SpuCnt, Enabled | 1 << 6 | 2 << 4);
        _spu.DmaWrite(0);

        irqs.Should().Be(1);
    }

    // ---- CD audio ----

    private sealed class ConstantCd(short left, short right) : ICdAudioSource
    {
        public void ReadSample(out short l, out short r)
        {
            l = left;
            r = right;
        }
    }

    [Fact]
    public void Cd_audio_is_mixed_at_the_cd_volume_and_captured()
    {
        _spu.CdAudio = new ConstantCd(10000, -8000);
        _spu.Write16(0x180, 0x4000 - 1); // main volume ≈ 1.0
        _spu.Write16(0x1B0, 0x4000); // CD volume 0.5
        _spu.Write16(0x1B2, 0x4000);
        int left = 0, right = 0;
        _spu.SampleReady = (l, r) => (left, right) = (l, r);

        _spu.Tick();
        left.Should().Be(0, "CD audio is off in SPUCNT");

        _spu.Write16(SpuCnt, Enabled | 1);
        _spu.Tick();
        left.Should().BeInRange(4990, 5000);
        right.Should().BeInRange(-4000, -3990);
        BitConverter.ToInt16(_spu.Ram[0x000..0x002]).Should().Be(10000, "the capture buffer holds the CD's left channel");
    }

    // ---- Reverb ----

    [Fact]
    public void Reverb_echoes_a_voice_through_its_buffer()
    {
        ManualWrite(0x1000, SquareLoop());
        SetUpVoice(0, 0x1000);
        _spu.Write16(0x1A2, 0x7F000 / 8); // buffer near the end of RAM
        _spu.Write16(0x1FC, 0x7FFF); // input volume
        _spu.Write16(0x1FE, 0x7FFF);
        _spu.Write16(0x1C6, 0x7FFF); // comb 1 reads where the same-side reflection writes
        _spu.Write16(0x1C4, 0x7FFF); // vIIR
        _spu.Write16(0x1D4, 0x0010);
        _spu.Write16(0x1D8, 0x0010);
        _spu.Write16(0x184, 0x7FFF); // output volume
        _spu.Write16(0x198, 1); // voice 0 to reverb
        _spu.Write16(SpuCnt, Enabled | 1 << 7);
        KeyOn(0);
        Run(200);

        _spu.Ram[0x7F000..0x80000].ToArray().Should().Contain(b => b != 0, "the reverb wrote its buffer");
    }

    // ---- State ----

    [Fact]
    public void Save_states_reproduce_the_following_samples()
    {
        ManualWrite(0x1000, SquareLoop());
        SetUpVoice(0, 0x1000, pitch: 0x0C35);
        _spu.Write16(0x194, 0); // no noise
        KeyOn(0);
        Run(123);

        var writer = new StateWriter();
        _spu.SaveState(writer);
        var expected = Run(500);

        var copy = new PsSpu();
        copy.LoadState(new StateReader(writer.ToArray()));
        var actual = new List<int>();
        copy.SampleReady = (left, _) => actual.Add(left);
        for (var i = 0; i < 500; i++)
        {
            copy.Tick();
        }

        actual.Should().Equal(expected);
    }
}
