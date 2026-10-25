using GameVault.Core.Cheats;
using GameVault.Library.Cheats;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Storage;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>Codes reaching the running game, and the list following loads, rewinds and F3 (SPEC §7.8).</summary>
public sealed class CheatServiceTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gamevault-codes-");
    private readonly InputService _input = new(KeyboardMapping.CreateDefault());
    private readonly EmulatorHost _host;
    private readonly CheatService _service;

    public CheatServiceTests()
    {
        _host = new EmulatorHost(new FrameSink(), _input);
        _service = new CheatService(_host, new CheatStore(_root.FullName), new CheatSuggestions(null));
    }

    public void Dispose()
    {
        _host.Dispose();
        _root.Delete(recursive: true);
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        // Generous: the emulator thread competes with CPU-heavy tests running in parallel.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(10);
        }
    }

    private string StartGame()
    {
        _host.Load(GameVault.Core.Tests.Cartridge.BankedRom.Build(0, 4, 8), "Game");
        return _host.GameKey!;
    }

    [Fact]
    public async Task Enabled_codes_reach_the_running_game_and_switch_off_again()
    {
        var key = StartGame();
        await _service.AddAsync(key, "SXIOPO", "Lives");
        await _service.AddAsync(key, "0300:09", "Hearts");

        await Eventually(async () => (await _host.InspectAsync(c => c.Cheats.Count)) == 2);
        _host.CheatsActive.Should().BeTrue();

        var items = await _service.GetAsync(key);
        await _service.SetEnabledAsync(key, items[0].Id, false);
        await Eventually(async () => (await _host.InspectAsync(c => c.Cheats.Count)) == 1);
        (await _host.InspectAsync(c => c.Bus.Peek(0x0300))).Should().Be(0x09, "the RAM code is held");

        await _service.SetAllEnabledAsync(key, false);
        await Eventually(async () => (await _host.InspectAsync(c => c.Cheats.Count)) == 0);
        _host.CheatsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Loading_a_state_brings_back_its_codes_and_the_list_follows()
    {
        var key = StartGame();
        await _service.AddAsync(key, "0300:09", "Hearts");
        await Eventually(async () => (await _host.InspectAsync(c => c.Cheats.Count)) == 1);
        var withCodes = await _host.CaptureStateAsync();

        await _service.SetAllEnabledAsync(key, false);
        await Eventually(async () => (await _host.InspectAsync(c => c.Cheats.Count)) == 0);

        var changed = new TaskCompletionSource();
        _service.Changed += (_, _) => changed.TrySetResult();
        (await _host.RestoreStateAsync(withCodes!)).Should().BeTrue();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (await _service.GetAsync(key)).Should().ContainSingle().Which.Enabled.Should().BeTrue();
        _host.CheatsActive.Should().BeTrue();
    }

    [Fact]
    public async Task F3_turns_every_code_off()
    {
        var key = StartGame();
        await _service.AddAsync(key, "SXIOPO", "Lives");
        await Eventually(async () => (await _host.InspectAsync(c => c.Cheats.Count)) == 1);
        var notices = new List<string>();
        _host.Notice += (_, n) => notices.Add(n);

        _input.KeyDown(VirtualKeys.F3);
        await Eventually(async () => (await _host.InspectAsync(c => c.Cheats.Count)) == 0);
        _input.KeyUp(VirtualKeys.F3);

        await Eventually(async () => (await _service.GetAsync(key)).All(c => !c.Enabled));
        notices.Should().Contain("All codes are off.");
    }
}
