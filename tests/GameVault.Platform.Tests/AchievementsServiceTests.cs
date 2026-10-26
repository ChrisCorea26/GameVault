using System.Text;
using FluentAssertions;
using GameVault.Core.Cheats;
using GameVault.Platform.Achievements;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Video;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>RetroAchievements in the running emulator (SPEC §7.10): sign-in, unlocks from real frames, hardcore rules.</summary>
public sealed class AchievementsServiceTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-ra-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>The lives ROM starts with 3 lives in $10 and loses one every 256 frames.</summary>
    private static string GameWithLivesAchievement(string condition) => AchievementsClientTests.GameResponse
        .Replace("\"MemAddr\":\"0xH0010=5\"", $"\"MemAddr\":\"{condition}\"");

    [Fact]
    public async Task Signing_in_keeps_only_an_encrypted_token_and_the_next_start_uses_it()
    {
        var server = new AchievementsClientTests.FakeServer { ["login2"] = AchievementsClientTests.LoginResponse };
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));
        var credentialsPath = Path.Combine(_root.FullName, "retroachievements.json");
        using (var client = new AchievementsClient(server))
        using (var service = new AchievementsService(host, client, new AchievementsCredentials(credentialsPath, new XorProtector()), new InMemorySettingsStore()))
        {
            await service.StartAsync();
            service.IsSignedIn.Should().BeFalse();
            await service.SignInAsync(" Player ", "Pa$$word");
            service.User!.UserName.Should().Be("Player");
        }

        var saved = File.ReadAllText(credentialsPath);
        saved.Should().Contain("Player").And.NotContain("ApiToken").And.NotContain("Pa$$word");

        using var again = new AchievementsClient(server);
        using var restarted = new AchievementsService(host, again, new AchievementsCredentials(credentialsPath, new XorProtector()), new InMemorySettingsStore());
        await restarted.StartAsync();
        restarted.IsSignedIn.Should().BeTrue();
        server.Requests.Should().Contain(r => r.Contains("r=login2") && r.Contains("t=ApiToken"));

        restarted.SignOut();
        File.Exists(credentialsPath).Should().BeFalse();
    }

    [Fact]
    public async Task An_achievement_unlocks_while_the_game_runs_and_hardcore_blocks_what_it_should()
    {
        var server = new AchievementsClientTests.FakeServer
        {
            ["login2"] = AchievementsClientTests.LoginResponse,
            ["achievementsets"] = GameWithLivesAchievement("0xH0010=2"),
            ["startsession"] = AchievementsClientTests.NoUnlocks,
            ["awardachievement"] = """{"Success":true,"Score":125,"SoftcoreScore":5,"AchievementID":5501,"AchievementsRemaining":1}""",
        };
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));
        using var client = new AchievementsClient(server);
        using var service = new AchievementsService(
            host, client, new AchievementsCredentials(Path.Combine(_root.FullName, "ra.json"), new XorProtector()), new InMemorySettingsStore());
        var unlocked = new TaskCompletionSource<AchievementsEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Event += (_, e) =>
        {
            if (e.Kind == AchievementsEventKind.AchievementUnlocked)
            {
                unlocked.TrySetResult(e);
            }
        };
        await service.StartAsync();
        await service.SignInAsync("Player", "Pa$$word");

        var rom = GameWatcherTests.LivesAndRoomsRom();
        host.Load(rom, "Lives");
        host.AchievementsHash.Should().Be(AchievementsHashing.Nes(rom));
        await WaitFor(() => service.Game is not null, 15);
        service.Game!.Title.Should().Be("Test Game");
        server.Requests.Should().Contain(r => r.Contains("r=achievementsets") && r.Contains("m=" + host.AchievementsHash));

        // Hardcore (the default): states don't load, codes stay off, netplay and attempts are refused.
        host.HardcoreActive.Should().BeTrue();
        var state = await host.CaptureStateAsync();
        (await host.RestoreAsync(state!)).Should().BeFalse();
        host.SetCheats([CheatCode.Parse("SXIOPO")]);
        host.CheatsActive.Should().BeFalse();
        await host.Invoking(h => h.HostNetplayAsync()).Should().ThrowAsync<NetplayException>();

        // The ROM loses a life every 256 frames, so $10 reaches 2 within a few seconds.
        var e = await unlocked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        e.Achievement!.Title.Should().Be("Five");
        await WaitFor(() => server.Requests.Any(r => r.Contains("r=awardachievement") && r.Contains("a=5501") && r.Contains("h=1")), 15);

        // Softcore: loading states works again.
        await service.SetHardcoreAsync(false);
        host.HardcoreActive.Should().BeFalse();
        (await host.RestoreAsync(state!)).Should().BeTrue();

        host.Stop();
        await WaitFor(() => service.Game is null, 15);
    }

    [Fact]
    public void The_nes_hash_skips_the_header()
    {
        var body = Encoding.ASCII.GetBytes("program and character data");
        var headered = "NES\u001A"u8.ToArray().Concat(new byte[12]).Concat(body).ToArray();

        AchievementsHashing.Nes(headered).Should().Be(AchievementsHashing.Nes(body));
        AchievementsHashing.Nes(body).Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(body)));
    }

    private static async Task WaitFor(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Stands in for DPAPI (Windows-only) so the file format can be checked on any OS.</summary>
    private sealed class XorProtector : ITokenProtector
    {
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[]? Unprotect(byte[] data) => Protect(data);
    }
}
