using GameVault.Core.Audio;
using GameVault.Core.PlayStation;
using GameVault.Core.PlayStation2;
using GameVault.Core.Snes;
using GameVault.Core.Tests.PlayStation2.Homebrew;
using GameVault.Core.Tests.Snes;

namespace GameVault.Core.Tests.Audio;

/// <summary>
/// The S-DSP, SPU and SPU2 each produce a left and a right sample; the console hands both to the resampler, so a sound
/// panned to one side is heard only there (outstanding-work plan §2).
/// </summary>
public class StereoConsoleTests
{
    /// <summary>Feeds a left-only square wave and a steady right level through a console's sample hook.</summary>
    private static (float Left, float Right) LastFrame(BandLimitedResampler audio, Action<short, short> onSample)
    {
        for (var i = 0; i < 2000; i++)
        {
            onSample((short)(i / 40 % 2 == 0 ? 16000 : -16000), -8192);
        }

        audio.EndFrame(2000);
        var frames = new float[16384];
        var count = audio.ReadStereo(frames);
        count.Should().BeGreaterThan(1000);
        var left = 0f;
        for (var i = count - 200; i < count; i++)
        {
            left = Math.Max(left, frames[2 * i]);
        }

        return (left, frames[2 * (count - 1) + 1]);
    }

    [Fact]
    public void The_super_nintendo_keeps_left_and_right_apart()
    {
        var console = new SnesConsole(new SnesCartridge(SnesTests.Rom([0x80, 0xFE])));
        console.Power();
        var audio = console.EnableAudio();

        var (left, right) = LastFrame(audio, console.OnSample);

        left.Should().BeGreaterThan(0.3f);
        right.Should().BeApproximately(-8192 * 0.9f / 32768, 0.01f);
    }

    [Fact]
    public void The_playstation_keeps_left_and_right_apart()
    {
        var console = new PlayStationConsole();
        var audio = console.EnableAudio();

        var (left, right) = LastFrame(audio, (l, r) => console.OnSample(l, r));

        left.Should().BeGreaterThan(0.3f);
        right.Should().BeApproximately(-8192 * 0.9f / 32768, 0.01f);
    }

    [Fact]
    public void The_playstation_2_keeps_left_and_right_apart()
    {
        var console = new Ps2Console(Ps2Boot.FromElf(File.ReadAllBytes(Ps2HomebrewRunner.ElfPath("gs-basic")), "host:gs-basic.elf"));
        var audio = console.EnableAudio();

        var (left, right) = LastFrame(audio, console.OnSample);

        left.Should().BeGreaterThan(0.3f);
        right.Should().BeApproximately(-8192 * 0.9f / 32768, 0.01f);
    }
}
