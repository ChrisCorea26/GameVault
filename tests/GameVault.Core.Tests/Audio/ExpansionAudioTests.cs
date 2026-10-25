using GameVault.Core.Cartridge;
using GameVault.Core.Cartridge.Mappers;
using GameVault.Core.Tests.Cartridge;

namespace GameVault.Core.Tests.Audio;

/// <summary>Phase 7 expansion audio completion: 5B noise and envelope, MMC5 sound.</summary>
public class ExpansionAudioTests
{
    private static List<float> Run(Action clock, Func<float> output, int cycles)
    {
        var samples = new List<float>(cycles);
        for (var i = 0; i < cycles; i++)
        {
            clock();
            samples.Add(output());
        }

        return samples;
    }

    private static void Write(Sunsoft5BAudio audio, int register, byte value)
    {
        audio.Select((byte)register);
        audio.Write(value);
    }

    [Fact]
    public void Sunsoft_5b_envelope_ramps_down_then_holds_silent()
    {
        var audio = new Sunsoft5BAudio();
        Write(audio, 7, 0x3F);   // tone and noise off: the channel outputs its level directly
        Write(audio, 8, 0x10);   // channel A uses the envelope
        Write(audio, 11, 4);     // envelope period
        Write(audio, 13, 0x00);  // \___ : decay, then hold at 0

        var samples = Run(audio.Clock, () => audio.Output, 16 * 4 * 40);

        samples[20].Should().BeGreaterThan(samples[16 * 4 * 16], "the level falls");
        samples[^1].Should().Be(0f, "and holds at silence");
    }

    [Fact]
    public void Sunsoft_5b_triangle_envelope_repeats()
    {
        var audio = new Sunsoft5BAudio();
        Write(audio, 7, 0x3F);
        Write(audio, 8, 0x10);
        Write(audio, 11, 1);
        Write(audio, 13, 0x0E); // /\/\ : continue, attack, alternate

        var samples = Run(audio.Clock, () => audio.Output, 16 * 32 * 4);

        samples.Max().Should().BeGreaterThan(0.07f);
        samples.Count(s => s == samples.Max()).Should().BeGreaterThan(1, "the peak comes around again");
        samples.Skip(16 * 64).Min().Should().Be(0f);
    }

    [Fact]
    public void Sunsoft_5b_noise_is_irregular()
    {
        var audio = new Sunsoft5BAudio();
        Write(audio, 7, 0x37);  // tone off, noise on for channel A
        Write(audio, 8, 0x0F);
        Write(audio, 6, 1);

        var samples = Run(audio.Clock, () => audio.Output, 16 * 2000);
        var runs = samples.Zip(samples.Skip(1)).Count(p => p.First != p.Second);

        runs.Should().BeGreaterThan(50);
        samples.Distinct().Should().HaveCount(2, "on or off at the fixed volume");
    }

    [Fact]
    public void Mmc5_pulses_play_through_the_mapper_and_report_length_status()
    {
        var console = NesConsole.FromBytes(BankedRom.Build(5, 32, 128));
        var mmc5 = (Mmc5)console.Mapper;
        console.Apu.ExpansionAudio.Should().BeSameAs(mmc5.ExpansionAudio);

        mmc5.CpuWrite(0x5015, 0x01);
        mmc5.CpuWrite(0x5000, 0xBF); // duty 2, constant volume 15
        mmc5.CpuWrite(0x5002, 0x80);
        mmc5.CpuWrite(0x5003, 0x08); // length index 1

        (mmc5.CpuRead(0x5015, 0) & 1).Should().Be(1);
        var audio = mmc5.ExpansionAudio;
        var samples = Run(audio.Clock, () => audio.Output, 4000);
        samples.Distinct().Should().HaveCountGreaterThan(1, "the pulse toggles");

        mmc5.CpuWrite(0x5011, 0x80); // PCM
        audio.Clock();
        audio.Output.Should().BeGreaterThan(0.1f);
    }

    private static void Fm(Vrc7Audio audio, int register, byte value)
    {
        audio.SelectRegister((byte)register);
        audio.WriteData(value);
    }

    private static double Frequency(List<float> samples, double sampleRate)
    {
        var crossings = samples.Zip(samples.Skip(1)).Count(p => p.First <= 0 && p.Second > 0);
        return crossings / (samples.Count / sampleRate);
    }

    [Fact]
    public void Vrc7_plays_a_built_in_instrument_at_the_programmed_pitch_and_releases()
    {
        var audio = new Vrc7Audio();
        // Instrument 3 (a flute-like patch) at volume 0 (loudest), A4: fnum 290, block 4 → 440 Hz.
        Fm(audio, 0x30, 0x30);
        Fm(audio, 0x10, 290 & 0xFF);
        Fm(audio, 0x20, 0x10 | (4 << 1) | (290 >> 8));

        var samples = new List<float>();
        for (var i = 0; i < 36 * 20000; i++)
        {
            audio.Clock();
            if (i % 36 == 0)
            {
                samples.Add(audio.Output);
            }
        }

        var playing = samples.Skip(4000).ToList();
        playing.Max().Should().BeGreaterThan(0.01f, "the note sounds");
        playing.Max().Should().BeLessThan(0.5f, "and stays well below clipping");
        Frequency(playing, 1789772.7 / 36).Should().BeApproximately(440, 15);

        Fm(audio, 0x20, (4 << 1) | (290 >> 8)); // key off
        for (var i = 0; i < 36 * 50000; i++)
        {
            audio.Clock();
        }

        audio.Output.Should().BeApproximately(0f, 0.001f, "the note has released");
    }

    [Fact]
    public void Vrc7_is_wired_through_the_mapper_registers()
    {
        var console = NesConsole.FromBytes(BankedRom.Build(85, 32, 64));
        var vrc7 = (Vrc7)console.Mapper;

        console.Apu.ExpansionAudio.Should().BeSameAs(vrc7.ExpansionAudio);
        vrc7.CpuWrite(0x9010, 0x30);
        vrc7.CpuWrite(0x9030, 0x10);
        vrc7.CpuWrite(0x9010, 0x10);
        vrc7.CpuWrite(0x9030, 0xAC);
        vrc7.CpuWrite(0x9010, 0x20);
        vrc7.CpuWrite(0x9030, 0x18);
        var audio = vrc7.ExpansionAudio;
        var levels = new HashSet<float>();
        for (var i = 0; i < 36 * 4000; i++)
        {
            audio.Clock();
            levels.Add(audio.Output);
        }

        levels.Count.Should().BeGreaterThan(10);
    }
}
