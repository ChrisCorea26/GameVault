using GameVault.Core;
using GameVault.Core.Tests.Cartridge;
using GameVault.Platform.Emulation;

namespace GameVault.Platform.Tests;

public class RunAheadTests
{
    private static NesConsole NewConsole()
    {
        var console = NesConsole.FromBytes(BankedRom.Build(4, 16, 128));
        console.Power();
        return console;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Shows_the_frame_a_plain_console_reaches_n_frames_later(int frames)
    {
        var plain = NewConsole();
        var ahead = NewConsole();
        var runAhead = new RunAhead { Frames = frames, Budget = TimeSpan.FromHours(1) };
        var plainFrames = new List<ushort[]>();
        for (var i = 0; i < 30 + frames; i++)
        {
            plain.RunFrame();
            plainFrames.Add(plain.Ppu.FrameBuffer.ToArray());
        }

        for (var i = 0; i < 30; i++)
        {
            ushort[]? shown = null;
            runAhead.RunFrame(ahead, c => shown = c.Ppu.FrameBuffer.ToArray());

            shown.Should().Equal(plainFrames[i + frames], $"host frame {i} should show emulated frame {i + frames}");
        }

        // The real timeline is untouched: the consoles agree on the frame both have really run.
        ahead.Ppu.FrameCount.Should().Be(30);
        ahead.SaveSnapshot().Should().Equal(Snapshot(30));
    }

    [Fact]
    public void Turns_itself_off_after_half_a_second_over_budget()
    {
        var console = NewConsole();
        var runAhead = new RunAhead { Frames = 2, Budget = TimeSpan.Zero };

        var switchedOff = Enumerable.Range(0, RunAhead.OverBudgetLimit).Select(_ => runAhead.RunFrame(console, _ => { })).ToList();

        switchedOff.Take(RunAhead.OverBudgetLimit - 1).Should().AllBeEquivalentTo(false);
        switchedOff.Last().Should().BeTrue();
        runAhead.AutoDisabled.Should().BeTrue();

        runAhead.ResetAutoDisable();
        runAhead.AutoDisabled.Should().BeFalse();
    }

    private static byte[] Snapshot(int frames)
    {
        var console = NewConsole();
        for (var i = 0; i < frames; i++)
        {
            console.RunFrame();
        }

        return console.SaveSnapshot();
    }
}

public class ProfileApplicationTests
{
    [Fact]
    public async Task Host_applies_header_corrections_input_type_and_run_ahead_safety()
    {
        using var host = new EmulatorHost(new GameVault.Platform.Video.FrameSink(), new GameVault.Platform.Input.InputService(GameVault.Platform.Input.KeyboardMapping.CreateDefault()));
        host.ProfileResolver = sha1 => new GameVault.Library.Profiles.GameProfile
        {
            Sha1 = sha1,
            Input = GameVault.Library.Profiles.InputType.Zapper,
            RunAheadSafe = false,
            RemoveSpriteLimit = true,
            Header = new GameVault.Library.Profiles.HeaderCorrection { Mirroring = "vertical", Battery = true },
        };

        host.Load(BankedRom.Build(0, 4, 8), "Light gun game");

        host.UsesZapper.Should().BeTrue();
        host.GameKey.Should().StartWith("sha1:");
        (await host.InspectAsync(c => c.InputSetup)).Should().Be(GameVault.Core.Input.InputSetup.Zapper);
        (await host.InspectAsync(c => c.Mapper.Mirroring)).Should().Be(GameVault.Core.Cartridge.Mirroring.Vertical);
        (await host.InspectAsync(c => c.Ppu.RemoveSpriteLimit)).Should().BeTrue();
    }
}
