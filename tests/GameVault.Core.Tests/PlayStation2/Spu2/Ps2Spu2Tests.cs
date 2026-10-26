using FluentAssertions;
using GameVault.Core.PlayStation;
using GameVault.Core.PlayStation2;
using GameVault.Core.State;

namespace GameVault.Core.Tests.PlayStation2.Spu2;

/// <summary>The PlayStation 2's SPU2 (Phase 17), driven through its registers with ADPCM blocks built in the tests.</summary>
public sealed class Ps2Spu2Tests
{
    // Per-core register offsets (core 1 adds $400).
    private const uint Pmon = 0x180;
    private const uint Non = 0x184;
    private const uint VmixL = 0x188;
    private const uint VmixEL = 0x18C;
    private const uint VmixR = 0x190;
    private const uint VmixER = 0x194;
    private const uint Mmix = 0x198;
    private const uint Attr = 0x19A;
    private const uint Irqa = 0x19C;
    private const uint Kon = 0x1A0;
    private const uint Koff = 0x1A4;
    private const uint Tsa = 0x1A8;
    private const uint Std = 0x1AC;
    private const uint Admas = 0x1B0;
    private const uint Esa = 0x2E0;
    private const uint Eea = 0x33C;
    private const uint Endx = 0x340;
    private const uint Statx = 0x344;
    private const uint IrqInfo = 0x7C2;

    private readonly Ps2Spu2 _spu = new();

    private static uint CoreBase(int core) => (uint)core * 0x400;

    /// <summary>The $760 area: MVOL, EVOL, AVOL, BVOL, MVOLX, then the reverb coefficients; $28 per core.</summary>
    private static uint ExtBase(int core) => 0x760 + (uint)core * 0x28;

    private void Write(int core, uint register, ushort value) => _spu.Write16(CoreBase(core) + register, value);

    private ushort Read(int core, uint register) => _spu.Read16(CoreBase(core) + register);

    private void WriteAddress(int core, uint register, uint address)
    {
        Write(core, register, (ushort)(address >> 16));
        Write(core, register + 2, (ushort)address);
    }

    private uint ReadAddress(int core, uint register) => (uint)Read(core, register) << 16 | Read(core, register + 2);

    /// <summary>A 16-byte ADPCM block (shift and filter, flags, then 28 nibbles, low nibble first) as 8 halfwords.</summary>
    private static ushort[] Block(int shift, int filter, int flags, params int[] nibbles)
    {
        var block = new byte[16];
        block[0] = (byte)(shift | filter << 4);
        block[1] = (byte)flags;
        for (var i = 0; i < 28; i++)
        {
            var nibble = nibbles[i % nibbles.Length] & 0xF;
            block[2 + i / 2] |= (byte)((i & 1) == 0 ? nibble : nibble << 4);
        }

        return ToHalfwords(block);
    }

    private static ushort[] ToHalfwords(byte[] bytes)
    {
        var halfwords = new ushort[bytes.Length / 2];
        for (var i = 0; i < halfwords.Length; i++)
        {
            halfwords[i] = (ushort)(bytes[2 * i] | bytes[2 * i + 1] << 8);
        }

        return halfwords;
    }

    private static byte[] ToBytes(ushort[] halfwords)
    {
        var bytes = new byte[halfwords.Length * 2];
        for (var i = 0; i < halfwords.Length; i++)
        {
            bytes[2 * i] = (byte)halfwords[i];
            bytes[2 * i + 1] = (byte)(halfwords[i] >> 8);
        }

        return bytes;
    }

    /// <summary>Writes halfwords to sound RAM through TSA and the data port (STD).</summary>
    private void ManualWrite(int core, uint address, ushort[] data)
    {
        WriteAddress(core, Tsa, address);
        foreach (var value in data)
        {
            Write(core, Std, value);
        }
    }

    private ushort RamAt(uint address) => (ushort)(_spu.Ram[(int)address * 2] | _spu.Ram[(int)address * 2 + 1] << 8);

    private void SetUpVoice(int core, int voice, uint address, ushort pitch = 0x1000, ushort adsr1 = 0x000F, ushort adsr2 = 0x0000)
    {
        var p = CoreBase(core) + (uint)voice * 0x10;
        _spu.Write16(p + 0x0, 0x3FFF);
        _spu.Write16(p + 0x2, 0x3FFF);
        _spu.Write16(p + 0x4, pitch);
        _spu.Write16(p + 0x6, adsr1);
        _spu.Write16(p + 0x8, adsr2);
        var a = CoreBase(core) + 0x1C0 + (uint)voice * 12;
        _spu.Write16(a + 0, (ushort)(address >> 16));
        _spu.Write16(a + 2, (ushort)address);
    }

    private void KeyOn(int core, int voice) => Write(core, voice < 16 ? Kon : Kon + 2, (ushort)(1 << (voice & 15)));

    private void KeyOff(int core, int voice) => Write(core, voice < 16 ? Koff : Koff + 2, (ushort)(1 << (voice & 15)));

    private ushort Envx(int core, int voice) => _spu.Read16(CoreBase(core) + (uint)voice * 0x10 + 0xA);

    private List<(short Left, short Right)> Run(int samples)
    {
        var output = new List<(short, short)>(samples);
        _spu.SampleReady = (left, right) => output.Add((left, right));
        for (var i = 0; i < samples; i++)
        {
            _spu.Tick();
        }

        return output;
    }

    /// <summary>Silences everything the reset state mixes, so a test enables only what it checks.</summary>
    private void Quiet()
    {
        for (var core = 0; core < 2; core++)
        {
            foreach (var mask in new[] { VmixL, VmixEL, VmixR, VmixER })
            {
                Write(core, mask, 0);
                Write(core, mask + 2, 0);
            }

            Write(core, Mmix, 0);
        }
    }

    [Fact]
    public void Reset_values_match_the_documented_power_on_state()
    {
        Read(0, Endx).Should().Be(0xFFFF);
        Read(0, Endx + 2).Should().Be(0xFF);
        Read(1, Endx).Should().Be(0xFFFF);
        Read(0, Mmix).Should().Be(0xFF0);
        Read(1, Mmix).Should().Be(0xFFC);
        Read(0, Statx).Should().Be(0x80);
        ReadAddress(0, Irqa).Should().Be(0x800);
        ReadAddress(0, Esa).Should().Be(0xEFFF8);
        ReadAddress(1, Esa).Should().Be(0xFFFF8);
        _spu.Read16(CoreBase(0) + 0x4).Should().Be(0x3FFF, "every voice's pitch starts at $3FFF");
        ReadAddress(0, 0x1C0).Should().Be(0x2800, "SSA starts at $2800");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Registers_read_back_what_was_written(int core)
    {
        var plain = new (uint Offset, ushort Value)[]
        {
            (0x000, 0x1234), (0x002, 0x2345), (0x004, 0x0FED), (0x006, 0x8F3A), (0x008, 0x1FC5), // voice 0
            (0x170 + 0x4, 0x2000), (0x170 + 0x6, 0xABCD), (0x170 + 0x8, 0x5678), // voice 23
            (Pmon, 0xFFFE), (Pmon + 2, 0x00AA), (Non, 0x1111), (Non + 2, 0x0022), (VmixL, 0x3333), (VmixL + 2, 0x44),
            (VmixEL, 0x5555), (VmixEL + 2, 0x66), (VmixR, 0x7777), (VmixR + 2, 0x88), (VmixER, 0x9999), (VmixER + 2, 0xAA),
            (Mmix, 0x0F30), (Attr, 0xC0F0), (Irqa, 0x0001), (Irqa + 2, 0x2340), (Admas, 0x0000),
            (Esa, 0x000E), (Esa + 2, 0x0000), (0x2E4, 0x0000), (0x2E6, 0x04C0), (0x338, 0x0001), (0x33A, 0x8000),
            (Eea, 0x000F),
        };
        foreach (var (offset, value) in plain)
        {
            Write(core, offset, value);
        }

        foreach (var (offset, value) in plain)
        {
            Read(core, offset).Should().Be(value, $"register ${offset:X3} of core {core}");
        }

        // Voice addresses: SSA and LSAX, 20 bits aligned to the block (the low three bits read as zero).
        WriteAddress(core, 0x1C0 + 5 * 12, 0xF_1238);
        ReadAddress(core, 0x1C0 + 5 * 12).Should().Be(0xF_1238);
        WriteAddress(core, 0x1C0 + 5 * 12 + 4, 0x3_456F);
        ReadAddress(core, 0x1C0 + 5 * 12 + 4).Should().Be(0x3_4568);

        WriteAddress(core, Tsa, 0xA_BCDE);
        ReadAddress(core, Tsa).Should().Be(0xA_BCDE);

        // The $760 area: fixed master volumes read back, and their current level (MVOLX) is the volume doubled.
        var ext = ExtBase(core);
        _spu.Write16(ext + 0x0, 0x3000);
        _spu.Write16(ext + 0x2, 0x2000);
        _spu.Read16(ext + 0x0).Should().Be(0x3000);
        _spu.Read16(ext + 0x10).Should().Be(0x6000);
        _spu.Read16(ext + 0x12).Should().Be(0x4000);
        for (uint offset = 0x4; offset < 0x10; offset += 2)
        {
            _spu.Write16(ext + offset, (ushort)(0x1000 + offset));
            _spu.Read16(ext + offset).Should().Be((ushort)(0x1000 + offset));
        }

        for (uint offset = 0x14; offset < 0x28; offset += 2)
        {
            _spu.Write16(ext + offset, (ushort)(0x8000 | offset));
            _spu.Read16(ext + offset).Should().Be((ushort)(0x8000 | offset), $"reverb coefficient at ${ext + offset:X3}");
        }

        // Voice volume (VOLXL/VOLXR) shows a fixed volume doubled.
        Write(core, 0x10, 0x1000);
        Read(core, 0x1C).Should().Be(0x2000);
    }

    [Fact]
    public void Shared_spdif_registers_read_back()
    {
        _spu.Write16(0x7C0, 0x0100);
        _spu.Write16(0x7C6, 0x0802);
        _spu.Write16(0x7C8, 0x0200);
        _spu.Write16(0x7CC, 0x8000);
        _spu.Read16(0x7C0).Should().Be(0x0100);
        _spu.Read16(0x7C6).Should().Be(0x0802);
        _spu.Read16(0x7C8).Should().Be(0x0200);
        _spu.Read16(0x7CC).Should().Be(0x8000);
    }

    [Fact]
    public void Core_registers_are_separate()
    {
        Write(0, 0x004, 0x1111);
        Write(1, 0x004, 0x2222);
        Read(0, 0x004).Should().Be(0x1111);
        Read(1, 0x004).Should().Be(0x2222);
        WriteAddress(0, Tsa, 0x1000);
        WriteAddress(1, Tsa, 0x2000);
        ReadAddress(0, Tsa).Should().Be(0x1000);
        ReadAddress(1, Tsa).Should().Be(0x2000);
    }

    [Theory]
    [InlineData(0, 0x1000, 0x000F, 0x0000)]
    [InlineData(0, 0x1234, 0x24CA, 0x1FC3)]
    [InlineData(1, 0x0800, 0x8F3A, 0x4E8A)]
    [InlineData(1, 0x3000, 0x000F, 0x0000)]
    public void A_voice_plays_the_samples_the_PlayStation_SPU_plays(int core, ushort pitch, ushort adsr1, ushort adsr2)
    {
        // Three blocks with different filters and shifts; the last loops back to the first.
        var data = Block(4, 0, 0b100, 1, 3, 7, -8, -3, 0, 5, 2, -1, 6, 4, -6)
            .Concat(Block(2, 2, 0, 7, 7, 6, 5, 4, 3, 2, 1, 0, -1, -2, -3, -4, -5, -6, -7, -8))
            .Concat(Block(8, 3, 0b011, -8, 7, -8, 7, 0, 0, 1, 1, 3, -3))
            .ToArray();
        const uint address = 0x4000; // halfwords

        Quiet();
        ManualWrite(core, address, data);
        SetUpVoice(core, 1, address, pitch, adsr1, adsr2);
        KeyOn(core, 1);

        var ps1 = new PsSpu();
        ps1.Power();
        ps1.Write16(0x1AA, 0xC000);
        ps1.Write16(0x1AC, 0x0004);
        ps1.Write16(0x1A6, (ushort)(address * 2 / 8));
        ps1.Write16(0x1AA, 0xC000 | 1 << 4);
        foreach (var value in data)
        {
            ps1.Write16(0x1A8, value);
        }

        ps1.Write16(0x1AA, 0xC000);
        ps1.Write16(0x10 + 0x4, pitch);
        ps1.Write16(0x10 + 0x6, (ushort)(address * 2 / 8));
        ps1.Write16(0x10 + 0x8, adsr1);
        ps1.Write16(0x10 + 0xA, adsr2);
        ps1.Write16(0x188, 1 << 1);

        const int samples = 400;
        Run(samples);
        for (var i = 0; i < samples; i++)
        {
            ps1.Tick();
        }

        // Voice 1's output (after its envelope) goes to $400 (core 0) or $C00 (core 1) on the SPU2 and to the capture
        // buffer at byte $800 on the PlayStation.
        var spu2Base = core == 0 ? 0x400u : 0xC00u;
        var nonZero = 0;
        for (var i = 0; i < samples; i++)
        {
            var expected = (ushort)(ps1.Ram[0x800 + 2 * i] | ps1.Ram[0x800 + 2 * i + 1] << 8);
            RamAt(spu2Base + (uint)i).Should().Be(expected, $"sample {i}");
            nonZero += expected != 0 ? 1 : 0;
        }

        nonZero.Should().BeGreaterThan(samples / 2);
    }

    [Fact]
    public void Key_on_takes_effect_at_the_next_sample_and_a_one_shot_block_sets_ENDX_and_stops()
    {
        Quiet();
        ManualWrite(0, 0x3000, Block(0, 0, 0b001, 4)); // loop end without repeat
        SetUpVoice(0, 18, 0x3000);
        Write(0, Endx + 2, 0); // clear voices 16–23
        Read(0, Endx + 2).Should().Be(0);

        KeyOn(0, 18);
        Envx(0, 18).Should().Be(0, "key on waits for the next sample");
        Run(1);
        Envx(0, 18).Should().BeGreaterThan(0);

        Run(26);
        Read(0, Endx + 2).Should().Be(0, "the block hasn't ended yet");
        Envx(0, 18).Should().BeGreaterThan(0);
        Run(1);
        Read(0, Endx + 2).Should().Be(1 << 2, "the end flag sets ENDX bit 18");
        Envx(0, 18).Should().Be(0, "without the repeat flag the voice stops");

        // A key on clears its ENDX bit again.
        KeyOn(0, 18);
        Run(1);
        Read(0, Endx + 2).Should().Be(0);

        // Any write to ENDX clears the half it addresses.
        Run(28);
        Read(0, Endx + 2).Should().Be(1 << 2);
        Write(0, Endx + 2, 0x1234);
        Read(0, Endx + 2).Should().Be(0);
    }

    [Fact]
    public void A_looping_voice_repeats_from_its_loop_start_and_LSAX_follows_the_flag()
    {
        Quiet();
        var data = Block(0, 0, 0, 1).Concat(Block(0, 0, 0b100, 2)).Concat(Block(0, 0, 0b011, 3)).ToArray();
        ManualWrite(0, 0x5000, data);
        Write(0, Endx, 0);
        SetUpVoice(0, 0, 0x5000);
        KeyOn(0, 0);
        Run(28 * 3);
        ReadAddress(0, 0x1C0 + 4).Should().Be(0x5008, "the second block's loop-start flag set LSAX");
        Read(0, Endx).Should().Be(1, "the third block's end flag set ENDX");
        Envx(0, 0).Should().BeGreaterThan(0, "the repeat flag keeps the voice playing");
        ReadAddress(0, 0x1C0 + 8).Should().Be(0x5009, "NAX points into the loop-start block");
    }

    [Fact]
    public void The_ADSR_envelope_steps_through_attack_decay_sustain_and_release()
    {
        Quiet();
        ManualWrite(0, 0x3000, Block(0, 0, 0b111, 1));
        // Attack linear, shift 0, step 7 (+$3800 a sample); decay shift 0; sustain level 7 ($4000);
        // sustain increase linear with shift 31 (no change here); release linear shift 10 (−16 a sample).
        SetUpVoice(0, 2, 0x3000, adsr1: 0x0007, adsr2: 0x1F0A);
        KeyOn(0, 2);

        var levels = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            Run(1);
            levels.Add(Envx(0, 2));
        }

        levels.Should().Equal(0x3800, 0x7000, 0x7FFF, 0x3FFF, 0x3FFF, 0x3FFF);

        KeyOff(0, 2);
        Envx(0, 2).Should().Be(0x3FFF, "key off waits for the next sample");
        Run(1);
        Envx(0, 2).Should().Be(0x3FFF - 16);
        Run(1);
        Envx(0, 2).Should().Be(0x3FFF - 32);
    }

    [Fact]
    public void Exponential_release_reaches_zero_and_ENVX_is_writable()
    {
        Quiet();
        ManualWrite(0, 0x3000, Block(0, 0, 0b111, 1));
        SetUpVoice(0, 3, 0x3000, adsr1: 0x000F, adsr2: 0x0020); // exponential release, shift 0 (halves each sample)
        KeyOn(0, 3);
        Run(3);
        Envx(0, 3).Should().Be(0x7FFF);
        KeyOff(0, 3);
        Run(400);
        Envx(0, 3).Should().Be(0);

        _spu.Write16(3 * 0x10 + 0xA, 0x1234);
        Envx(0, 3).Should().Be(0x1234);
    }

    [Fact]
    public void Manual_and_DMA_transfers_move_data_through_TSA()
    {
        ManualWrite(0, 0x10000, [0x1111, 0x2222, 0x3333]);
        ReadAddress(0, Tsa).Should().Be(0x10003, "the data port advances TSA");
        RamAt(0x10001).Should().Be(0x2222);

        WriteAddress(1, Tsa, 0x20000);
        var data = Enumerable.Range(0, 300).Select(i => (ushort)(i * 7 + 1)).ToArray();
        _spu.DmaWrite(1, data).Should().Be(300);
        ReadAddress(1, Tsa).Should().Be(0x2012C);

        WriteAddress(0, Tsa, 0x20000);
        var back = new ushort[300];
        _spu.DmaRead(0, back).Should().Be(300);
        back.Should().Equal(data);
        ReadAddress(0, Tsa).Should().Be(0x2012C);

        // Transfers wrap at the top of the 2 MB.
        WriteAddress(0, Tsa, 0xFFFFE);
        _spu.DmaWrite(0, [0xAAAA, 0xBBBB, 0xCCCC]).Should().Be(3);
        RamAt(0xFFFFF).Should().Be(0xBBBB);
        RamAt(0).Should().Be(0xCCCC);
        ReadAddress(0, Tsa).Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AutoDMA_fills_the_free_half_buffer_and_paces_the_DMA(int core)
    {
        Write(core, Admas, (ushort)(1 << core));
        var block = Enumerable.Range(0, 512).Select(i => (ushort)(i < 256 ? 1000 + i : 30000 - i)).ToArray();

        // The mixer plays half 0, so the first block goes to half 1: left at $2100, right at $2300 (+$400 for core 1).
        _spu.DmaWrite(core, block.AsSpan(0, 300)).Should().Be(300);
        _spu.DmaWrite(core, block.AsSpan(300)).Should().Be(212);
        _spu.DmaWrite(core, block).Should().Be(0, "both halves are busy until the mixer moves on");
        var memin = 0x2000u + (uint)core * 0x400;
        RamAt(memin + 0x100).Should().Be(1000);
        RamAt(memin + 0x1FF).Should().Be(1255);
        RamAt(memin + 0x300).Should().Be(30000 - 256);

        Run(255);
        _spu.DmaWrite(core, block).Should().Be(0);
        Run(1);
        _spu.DmaWrite(core, block).Should().Be(512, "the mixer has moved to half 1, so half 0 is free");
        RamAt(memin).Should().Be(1000);
        RamAt(memin + 0x200).Should().Be(30000 - 256);
        _spu.DmaWrite(core, block).Should().Be(0);

        // With ADMA off, a DMA goes to TSA again.
        Write(core, Admas, 0);
        WriteAddress(core, Tsa, 0x30000);
        _spu.DmaWrite(core, block).Should().Be(512);
        RamAt(0x30000).Should().Be(1000);
    }

    [Fact]
    public void AutoDMA_input_is_played_through_both_cores()
    {
        Quiet();
        Write(0, Mmix, 0x0C0); // core 0: input to dry
        Write(1, Mmix, 0x00C); // core 1: core 0's output (external input) to dry
        _spu.Write16(ExtBase(0) + 0x0, 0x3FFF);
        _spu.Write16(ExtBase(0) + 0x2, 0x3FFF);
        _spu.Write16(ExtBase(1) + 0x0, 0x3FFF);
        _spu.Write16(ExtBase(1) + 0x2, 0x3FFF);
        Write(0, Admas, 1);
        var block = Enumerable.Range(0, 512).Select(i => (ushort)(short)(i < 256 ? i * 50 : -(i - 256) * 40)).ToArray();
        _spu.DmaWrite(0, block).Should().Be(512);

        var output = Run(512);
        output.Take(256).Should().OnlyContain(s => s.Left == 0 && s.Right == 0, "half 0 is still empty");
        for (var i = 0; i < 256; i++)
        {
            output[256 + i].Left.Should().Be(Through((short)block[i]), $"left sample {i}");
            output[256 + i].Right.Should().Be(Through((short)block[256 + i]), $"right sample {i}");
        }

        // BVOL ($7FFF), core 0's MVOL ($7FFE), AVOL ($7FFF), core 1's MVOL ($7FFE).
        static short Through(int x) => (short)((((x * 0x7FFF >> 15) * 0x7FFE >> 15) * 0x7FFF >> 15) * 0x7FFE >> 15);
    }

    [Fact]
    public void Core_0_feeds_core_1_through_MMIX()
    {
        Quiet();
        ManualWrite(0, 0x3000, Block(0, 0, 0b111, 3));
        SetUpVoice(0, 0, 0x3000);
        Write(0, VmixL, 1);
        Write(0, VmixR, 1);
        Write(0, Mmix, 0xC00); // core 0: dry voices
        _spu.Write16(ExtBase(0) + 0x0, 0x3FFF);
        _spu.Write16(ExtBase(0) + 0x2, 0x3FFF);
        _spu.Write16(ExtBase(1) + 0x0, 0x3FFF);
        _spu.Write16(ExtBase(1) + 0x2, 0x3FFF);
        KeyOn(0, 0);

        var muted = Run(50);
        muted.Should().OnlyContain(s => s.Left == 0 && s.Right == 0, "core 1 doesn't take its external input yet");
        RamAt(0x800 + 49).Should().NotBe(0, "core 0's output is written to $800");

        Write(1, Mmix, 0x008); // core 1: external left to dry
        var output = Run(50);
        output.Should().OnlyContain(s => s.Left > 0 && s.Right == 0);
        output[^1].Left.Should().Be((short)(((short)RamAt(0x800 + 99) * 0x7FFF >> 15) * 0x7FFE >> 15));
    }

    [Fact]
    public void Voices_mix_into_their_own_core_only()
    {
        Quiet();
        ManualWrite(1, 0x3000, Block(0, 0, 0b111, 3));
        SetUpVoice(1, 23, 0x3000);
        Write(1, VmixR + 2, 0x80); // voice 23, dry right
        Write(1, Mmix, 0x400); // core 1: dry voices right
        _spu.Write16(ExtBase(1) + 0x0, 0x3FFF);
        _spu.Write16(ExtBase(1) + 0x2, 0x3FFF);
        KeyOn(1, 23);

        var output = Run(40);
        output.Should().OnlyContain(s => s.Left == 0);
        output[^1].Right.Should().BeGreaterThan(0);
        RamAt(0x1800 + 39).Should().Be(0, "core 1's dry left mix is silent");
        RamAt(0x1A00 + 39).Should().NotBe(0, "core 1's dry right mix is written to $1A00");
    }

    [Fact]
    public void Noise_voices_play_the_core_noise_generator()
    {
        Quiet();
        ManualWrite(0, 0x3000, Block(0, 0, 0b111, 0)); // silent ADPCM
        SetUpVoice(0, 1, 0x3000);
        Write(0, Non, 1 << 1);
        Write(0, Attr, 0x3F00); // fastest noise clock
        KeyOn(0, 1);
        Run(64);

        var distinct = new HashSet<ushort>();
        for (uint i = 0; i < 64; i++)
        {
            distinct.Add(RamAt(0x400 + i));
        }

        distinct.Count.Should().BeGreaterThan(8, "voice 1's output follows the noise generator");
    }

    [Fact]
    public void Pitch_modulation_follows_the_previous_voice()
    {
        Quiet();
        ManualWrite(0, 0x3000, Block(0, 0, 0b111, 7)); // a high constant
        ManualWrite(0, 0x3008, Block(0, 0, 0b111, 1, 2, 3, 4, 5, 6, 7, 6, 5, 4, 3, 2, 1, 0));
        SetUpVoice(0, 0, 0x3000);
        SetUpVoice(0, 1, 0x3008, pitch: 0x0800);
        Write(0, Pmon, 1 << 1);
        KeyOn(0, 0);
        KeyOn(0, 1);
        Run(100);
        var modulated = ReadAddress(0, 0x1C0 + 12 + 8);

        _spu.Reset();
        Quiet();
        ManualWrite(0, 0x3000, Block(0, 0, 0b111, 7));
        ManualWrite(0, 0x3008, Block(0, 0, 0b111, 1, 2, 3, 4, 5, 6, 7, 6, 5, 4, 3, 2, 1, 0));
        SetUpVoice(0, 0, 0x3000);
        SetUpVoice(0, 1, 0x3008, pitch: 0x0800);
        KeyOn(0, 0);
        KeyOn(0, 1);
        Run(100);
        var plain = ReadAddress(0, 0x1C0 + 12 + 8);

        plain.Should().Be(0x3008 + 1 + (50 - 28) / 4, "at half speed, 50 samples in: 22 into the looped block");
        modulated.Should().NotBe(plain, "a positive previous voice speeds voice 1 up");
    }

    [Fact]
    public void An_access_to_IRQA_raises_the_IRQ_once_until_acknowledged()
    {
        var irqs = 0;
        _spu.Irq = () => irqs++;
        WriteAddress(0, Irqa, 0x6000);
        WriteAddress(1, Irqa, 0x6001);

        ManualWrite(0, 0x5FFF, [1]);
        ManualWrite(0, 0x6000, [1]);
        irqs.Should().Be(0, "IRQs are disabled");

        Write(0, Attr, 0x40);
        ManualWrite(0, 0x5FFF, [1]);
        irqs.Should().Be(0);
        ManualWrite(0, 0x6000, [1]);
        irqs.Should().Be(1);
        _spu.Read16(IrqInfo).Should().Be(4);
        ManualWrite(0, 0x6000, [1]);
        irqs.Should().Be(1, "the IRQ stays raised until acknowledged");

        // Clearing ATTR's IRQ enable acknowledges it.
        Write(0, Attr, 0);
        _spu.Read16(IrqInfo).Should().Be(0);
        Write(0, Attr, 0x40);

        // Core 1's IRQ address is hit by a core 0 access too.
        Write(1, Attr, 0x40);
        ManualWrite(0, 0x6000, [1, 2]);
        irqs.Should().Be(3);
        _spu.Read16(IrqInfo).Should().Be(0xC);
    }

    [Fact]
    public void DMA_and_voices_hit_the_IRQ_address()
    {
        var irqs = 0;
        _spu.Irq = () => irqs++;
        Write(0, Attr, 0x40);

        // A DMA raises it for an address inside the transfer (not its first halfword).
        WriteAddress(0, Irqa, 0x7000);
        WriteAddress(0, Tsa, 0x7000);
        _spu.DmaWrite(0, new ushort[16]);
        irqs.Should().Be(0);
        WriteAddress(0, Tsa, 0x6FF0);
        _spu.DmaWrite(0, new ushort[32]);
        irqs.Should().Be(1);

        // A voice raises it when it fetches the block holding the address.
        Write(0, Attr, 0);
        Write(0, Attr, 0x40);
        Quiet();
        ManualWrite(0, 0x7100, Block(0, 0, 0, 1).Concat(Block(0, 0, 0b011, 1)).ToArray());
        WriteAddress(0, Irqa, 0x710C);
        irqs = 0;
        SetUpVoice(0, 4, 0x7100);
        KeyOn(0, 4);
        Run(27);
        irqs.Should().Be(0);
        Run(1);
        irqs.Should().Be(1);
    }

    [Fact]
    public void Reverb_writes_its_work_area_only_with_effects_enabled()
    {
        Quiet();
        ManualWrite(0, 0x3000, Block(0, 0, 0b111, 5));
        SetUpVoice(0, 0, 0x3000);
        Write(0, VmixEL, 1);
        Write(0, VmixER, 1);
        Write(0, Mmix, 0x300); // wet voices to the reverb
        WriteAddress(0, Esa, 0x80000);
        Write(0, Eea, 0x8); // the area ends at $8FFFF
        WriteAddress(0, 0x2EC, 0x100); // SAME_L_DST
        WriteAddress(0, 0x2F0, 0x200); // SAME_R_DST
        WriteAddress(0, 0x2F4, 0x100); // COMB1_L_SRC
        WriteAddress(0, 0x2F8, 0x200); // COMB1_R_SRC
        var ext = ExtBase(0);
        _spu.Write16(ext + 0x14, 0x4000); // IIR
        _spu.Write16(ext + 0x16, 0x4000); // COMB1
        _spu.Write16(ext + 0x24, 0x4000); // IN_COEF_L
        _spu.Write16(ext + 0x26, 0x4000); // IN_COEF_R
        _spu.Write16(ext + 0x04, 0x7FFF); // EVOLL
        _spu.Write16(ext + 0x06, 0x7FFF); // EVOLR
        _spu.Write16(ext + 0x00, 0x3FFF); // MVOLL
        _spu.Write16(ext + 0x02, 0x3FFF); // MVOLR
        KeyOn(0, 0);

        Run(200);
        AreaUsed().Should().BeFalse("effects are disabled");

        Write(0, Attr, 0x80);
        Run(200);
        AreaUsed().Should().BeTrue();
        RamAt(0x800 + 399).Should().NotBe(0, "the reverb output reaches core 0's output (sample 399)");

        bool AreaUsed()
        {
            for (var i = 0x80000; i < 0x80400; i++)
            {
                if (RamAt((uint)i) != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    [Fact]
    public void Each_tick_produces_one_sample()
    {
        Run(1000).Should().HaveCount(1000);
        IPs2Spu2.CyclesPerSample.Should().Be(768);
    }

    [Fact]
    public void Ticking_does_not_allocate()
    {
        ManualWrite(0, 0x3000, Block(0, 0, 0b111, 5));
        SetUpVoice(0, 0, 0x3000);
        Write(0, Attr, 0xC0); // effects and IRQs on
        WriteAddress(0, Esa, 0x80000);
        Write(0, Eea, 0x8);
        KeyOn(0, 0);
        Run(20_000); // warm up past the JIT's tiering
        _spu.SampleReady = null;

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++)
        {
            _spu.Tick();
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(10_000, "nothing per sample");
    }

    [Fact]
    public void Save_state_round_trips()
    {
        Quiet();
        var data = Block(4, 1, 0b100, 1, 3, 7, -8, -3).Concat(Block(3, 2, 0b011, 2, -2, 5)).ToArray();
        ManualWrite(0, 0x4000, data);
        ManualWrite(1, 0x4100, data);
        SetUpVoice(0, 5, 0x4000, pitch: 0x1555, adsr1: 0x24CA, adsr2: 0x1FC3);
        SetUpVoice(1, 9, 0x4100, pitch: 0x0FFF);
        Write(0, VmixL, 1 << 5);
        Write(0, VmixEL, 1 << 5);
        Write(1, VmixR, 1 << 9);
        Write(0, Mmix, 0xF00);
        Write(1, Mmix, 0xF0C);
        Write(0, Attr, 0x80);
        WriteAddress(0, Esa, 0x80000);
        Write(0, Eea, 0x8);
        WriteAddress(0, 0x2EC, 0x40);
        _spu.Write16(ExtBase(0) + 0x24, 0x3000);
        _spu.Write16(ExtBase(0) + 0x14, 0x3000);
        _spu.Write16(ExtBase(0) + 0x04, 0x3000);
        for (var core = 0; core < 2; core++)
        {
            _spu.Write16(ExtBase(core) + 0x0, 0x8030); // a master volume sweeping up
            _spu.Write16(ExtBase(core) + 0x2, 0x3FFF);
        }

        KeyOn(0, 5);
        KeyOn(1, 9);
        Run(300);
        Write(1, Admas, 2);
        _spu.DmaWrite(1, Enumerable.Range(0, 400).Select(i => (ushort)(i * 31)).ToArray());

        var writer = new StateWriter();
        _spu.SaveState(writer);
        var state = writer.ToArray();
        var first = Run(700);

        var other = new Ps2Spu2();
        other.LoadState(new StateReader(state));
        var second = new List<(short, short)>();
        other.SampleReady = (l, r) => second.Add((l, r));
        for (var i = 0; i < 700; i++)
        {
            other.Tick();
        }

        first.Should().Contain(s => s.Left != 0 || s.Right != 0);
        second.Should().Equal(first);
        other.Ram.ToArray().Should().Equal(_spu.Ram.ToArray());

        var again = new StateWriter();
        other.SaveState(again);
        var mine = new StateWriter();
        _spu.SaveState(mine);
        again.ToArray().Should().Equal(mine.ToArray());
    }
}
