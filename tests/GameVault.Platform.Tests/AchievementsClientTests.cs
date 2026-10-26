using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using GameVault.Platform.Achievements;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>
/// The rcheevos binding (SPEC §7.10, Phase 10) against a fake RetroAchievements server, answering in the
/// formats rcheevos' own client tests use. No network.
/// </summary>
public sealed class AchievementsClientTests
{
    internal const string LoginResponse =
        """{"Success":true,"User":"Player","Token":"ApiToken","Score":120,"SoftcoreScore":5,"Messages":0,"Permissions":1,"AccountType":"Registered"}""";

    /// <summary>Achievement 5501 unlocks when $0010 is 5; leaderboard 4401 runs while $000C is 1; rich presence shows $0001.</summary>
    internal const string GameResponse =
        """
        {"Success":true,"GameId":1234,"Title":"Test Game","ConsoleId":7,
         "ImageIconUrl":"http://server/Images/112233.png",
         "RichPresenceGameId":1234,"RichPresencePatch":"Display:\r\nWorld @Number(0xH0001)",
         "Sets":[{"AchievementSetId":1111,"GameId":1234,"Title":null,"Type":"core",
           "ImageIconUrl":"http://server/Images/112233.png",
           "Achievements":[
             {"ID":5501,"Title":"Five","Description":"Reach five","Flags":3,"Points":5,"MemAddr":"0xH0010=5",
              "Author":"Someone","BadgeName":"00234","Created":1367266583,"Modified":1376929305},
             {"ID":5502,"Title":"Nine","Description":"Reach nine","Flags":3,"Points":10,"MemAddr":"0xH0010=9",
              "Author":"Someone","BadgeName":"00235","Created":1367266583,"Modified":1376929305}],
           "Leaderboards":[
             {"ID":4401,"Title":"Score attack","Description":"Most points",
              "Mem":"STA:0xH000C=1::CAN:0xH000D=1::SUB:0xH000D=2::VAL:0xH000E","Format":"SCORE"}]}]}
        """;

    internal const string NoUnlocks = """{"Success":true,"Unlocks":[],"HardcoreUnlocks":[]}""";

    [Fact]
    public async Task Signing_in_with_a_password_keeps_the_token()
    {
        var server = new FakeServer { ["login2"] = LoginResponse };
        using var client = new AchievementsClient(server);

        await client.LoginWithPasswordAsync("Player", "Pa$$word");

        client.User.Should().Be(new AchievementsUser("Player", "Player", 120, 5, "https://media.retroachievements.org/UserPic/Player.png"));
        client.Token.Should().Be("ApiToken");
        server.Requests.Should().ContainSingle(r => r.Contains("r=login2") && r.Contains("p=Pa%24%24word"));
        client.UserAgentClause.Should().StartWith("rcheevos/");
    }

    [Fact]
    public async Task A_rejected_sign_in_says_why()
    {
        var server = new FakeServer
        {
            ["login2"] = """{"Success":false,"Error":"Invalid User/Password combination. Please try again","Code":"invalid_credentials","Status":401}""",
        };
        server.Status["login2"] = 401;
        using var client = new AchievementsClient(server);

        var act = () => client.LoginWithPasswordAsync("Player", "wrong");

        (await act.Should().ThrowAsync<AchievementsException>()).Which.Message.Should().Contain("username and password");
        client.User.Should().BeNull();
    }

    [Fact]
    public async Task An_achievement_unlocks_when_its_memory_condition_holds()
    {
        var server = new FakeServer { ["login2"] = LoginResponse, ["achievementsets"] = GameResponse, ["startsession"] = NoUnlocks };
        server["awardachievement"] = """{"Success":true,"Score":125,"SoftcoreScore":5,"AchievementID":5501,"AchievementsRemaining":1}""";
        using var client = new AchievementsClient(server);
        var ram = new byte[0x800];
        client.ReadMemory = (address, buffer) => Read(ram, address, buffer);
        var events = new List<AchievementsEvent>();
        client.Event += (_, e) => events.Add(e);

        await client.LoginWithTokenAsync("Player", "ApiToken");
        await LoadAsync(client);

        client.Game.Should().Be(new AchievementsGame(1234, "Test Game", "http://server/Images/112233.png", 2, 0));
        client.DoFrame();
        events.Should().BeEmpty();

        ram[0x10] = 5;
        client.DoFrame();

        var unlocked = events.Should().ContainSingle(e => e.Kind == AchievementsEventKind.AchievementUnlocked).Which.Achievement!;
        unlocked.Should().BeEquivalentTo(new { Id = 5501u, Title = "Five", Points = 5u, Unlocked = true });
        server.Requests.Should().Contain(r => r.Contains("r=awardachievement") && r.Contains("a=5501") && r.Contains("h=1"));
        client.Achievements().SelectMany(g => g.Achievements).Single(a => a.Id == 5501).Unlocked.Should().BeTrue();
    }

    [Fact]
    public async Task Leaderboards_start_track_and_submit_and_rich_presence_reads_memory()
    {
        var server = new FakeServer { ["login2"] = LoginResponse, ["achievementsets"] = GameResponse, ["startsession"] = NoUnlocks };
        server["submitlbentry"] =
            """{"Success":true,"Response":{"Score":42,"BestScore":42,"TopEntries":[{"User":"Player","Score":42,"Rank":1}],"RankInfo":{"Rank":1,"NumEntries":"1"}}}""";
        using var client = new AchievementsClient(server);
        var ram = new byte[0x800];
        ram[0x01] = 3;
        client.ReadMemory = (address, buffer) => Read(ram, address, buffer);
        var events = new List<AchievementsEvent>();
        client.Event += (_, e) => events.Add(e);

        await client.LoginWithTokenAsync("Player", "ApiToken");
        await LoadAsync(client);
        client.DoFrame();
        client.RichPresence.Should().Be("World 3");

        ram[0x0C] = 1;
        ram[0x0E] = 42;
        client.DoFrame();
        events.Should().Contain(e => e.Kind == AchievementsEventKind.LeaderboardStarted && e.Leaderboard!.Id == 4401);
        events.Should().Contain(e => e.Kind == AchievementsEventKind.TrackerShow && e.TrackerText == "000042");

        ram[0x0D] = 2;
        client.DoFrame();
        events.Should().Contain(e => e.Kind == AchievementsEventKind.LeaderboardSubmitted && e.Leaderboard!.Title == "Score attack");
        server.Requests.Should().Contain(r => r.Contains("r=submitlbentry") && r.Contains("i=4401") && r.Contains("s=42"));
    }

    [Fact]
    public async Task Progress_round_trips_for_save_states()
    {
        var server = new FakeServer { ["login2"] = LoginResponse, ["achievementsets"] = GameResponse, ["startsession"] = NoUnlocks };
        using var client = new AchievementsClient(server);
        var ram = new byte[0x800];
        client.ReadMemory = (address, buffer) => Read(ram, address, buffer);
        await client.LoginWithTokenAsync("Player", "ApiToken");
        await LoadAsync(client);
        client.DoFrame();

        var progress = client.SerializeProgress();
        progress.Should().NotBeEmpty();
        client.DeserializeProgress(progress).Should().BeTrue();
        client.DeserializeProgress([]).Should().BeTrue("an old state without progress resets it");
    }

    /// <summary>
    /// rcheevos finishes loading on the next frame or idle call (memory is only read on the emulation thread),
    /// so tests pump <see cref="AchievementsClient.Idle"/> as the host's frame loop would.
    /// </summary>
    internal static async Task LoadAsync(AchievementsClient client, string hash = "0123456789abcdef0123456789abcdef")
    {
        var load = client.LoadGameAsync(hash);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!load.IsCompleted && DateTime.UtcNow < deadline)
        {
            client.Idle();
            await Task.Delay(5);
        }

        await load.WaitAsync(TimeSpan.FromSeconds(1));
    }

    internal static uint Read(byte[] ram, uint address, Span<byte> buffer)
    {
        var count = 0u;
        for (var i = 0; i < buffer.Length; i++)
        {
            var a = address + (uint)i;
            buffer[i] = a < ram.Length ? ram[a] : (byte)0;
            count++;
        }

        return count;
    }

    /// <summary>Answers by the request's <c>r=</c> parameter; records every request's POST data.</summary>
    internal sealed class FakeServer : IAchievementsServer
    {
        private readonly Dictionary<string, string> _responses = [];

        public ConcurrentQueue<string> Requests { get; } = new();

        public Dictionary<string, int> Status { get; } = [];

        public string this[string api]
        {
            set => _responses[api] = value;
        }

        public Task<AchievementsServerResponse> PostAsync(string url, string? postData, string? contentType)
        {
            var query = postData ?? new Uri(url).Query.TrimStart('?');
            Requests.Enqueue(query);
            var api = query.Split('&').Select(p => p.Split('=')).FirstOrDefault(p => p[0] == "r")?[1] ?? string.Empty;
            var body = _responses.TryGetValue(api, out var json) ? json : """{"Success":false,"Error":"Not mocked"}""";
            return Task.FromResult(new AchievementsServerResponse(Status.GetValueOrDefault(api, 200), Encoding.UTF8.GetBytes(body)));
        }
    }
}
