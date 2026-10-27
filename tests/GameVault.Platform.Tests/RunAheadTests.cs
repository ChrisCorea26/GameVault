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
            runAhead.RunFrame(ahead, c => shown = c.FrameBuffer.ToArray());

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
        (await host.InspectNesAsync(c => c.InputSetup)).Should().Be(GameVault.Core.Input.InputSetup.Zapper);
        (await host.InspectNesAsync(c => c.Mapper.Mirroring)).Should().Be(GameVault.Core.Cartridge.Mirroring.Vertical);
        (await host.InspectNesAsync(c => c.Ppu.RemoveSpriteLimit)).Should().BeTrue();
    }

    [Fact]
    public async Task Power_pad_and_paddle_games_get_their_devices_and_the_keyboard_or_mouse_drives_them()
    {
        var input = new GameVault.Platform.Input.InputService(GameVault.Platform.Input.KeyboardMapping.CreateDefault());
        using var host = new EmulatorHost(new GameVault.Platform.Video.FrameSink(), input);
        host.ProfileResolver = sha1 => new GameVault.Library.Profiles.GameProfile { Sha1 = sha1, Input = GameVault.Library.Profiles.InputType.PowerPad };

        host.Load(BankedRom.Build(0, 4, 8), "Mat game");

        (await host.InspectNesAsync(c => c.InputSetup)).Should().Be(GameVault.Core.Input.InputSetup.PowerPad);
        input.PowerPadActive.Should().BeTrue();
        input.KeyDown(GameVault.Platform.Input.VirtualKeys.O); // button 3
        await WaitForAsync(async () => await host.InspectNesAsync(c => c.PowerPad.Buttons) == 1 << 2);

        host.ProfileResolver = sha1 => new GameVault.Library.Profiles.GameProfile { Sha1 = sha1, Input = GameVault.Library.Profiles.InputType.ArkanoidPaddle };
        host.Load(BankedRom.Build(0, 4, 8), "Paddle game");

        host.UsesPaddle.Should().BeTrue();
        input.PowerPadActive.Should().BeFalse();
        input.SetPaddle(1, fire: true);
        await WaitForAsync(async () => await host.InspectNesAsync(c => c.Paddle.Position) == GameVault.Core.Input.ArkanoidPaddle.MaxPosition);
        (await host.InspectNesAsync(c => c.Paddle.Fire)).Should().BeTrue();
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the condition should hold within 10 seconds");
            await Task.Delay(10);
        }
    }
}
