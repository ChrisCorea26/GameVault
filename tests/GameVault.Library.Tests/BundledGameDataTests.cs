using GameVault.Library.Memory;
using GameVault.Library.Passwords;
using GameVault.Library.Profiles;

namespace GameVault.Library.Tests;

/// <summary>
/// The game data GameVault ships in <c>data/gamedb</c> (GV-006, GV-007): it all loads, and the definitions read what the
/// disassemblies they come from say (docs/gamedb-sources.md). Real games can't be in the repository, so the memory here is
/// written by the test.
/// </summary>
public sealed class BundledGameDataTests
{
    private static string GameDb()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GameVault.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("The repository root wasn't found."), "data", "gamedb");
    }

    [Fact]
    public void Every_file_loads_without_errors()
    {
        var profiles = new GameProfiles(GameDb());
        var maps = new MemoryMaps(Path.Combine(GameDb(), "memory"));
        var passwords = new PasswordDefinitions(Path.Combine(GameDb(), "passwords"));

        profiles.Errors.Should().BeEmpty();
        maps.Errors.Should().BeEmpty();
        passwords.Errors.Should().BeEmpty();
        profiles.Count.Should().BeGreaterThanOrEqualTo(60);
        maps.Count.Should().BeGreaterThanOrEqualTo(3);
        passwords.Count.Should().BeGreaterThanOrEqualTo(1);
    }

    [Theory]
    [InlineData("fb853a77e02f1ceae933dff762cd0c2f6e0b2e27", InputType.Zapper)] // Duck Hunt (World)
    [InlineData("d529e7fb2ba1aeefd7f7a5258b53732461f85ba0", InputType.FourScore)] // Nintendo World Cup (USA)
    [InlineData("22806f1070617b4d3aa0fd82d005c1ded6cf8779", InputType.PowerPad)] // World Class Track Meet (USA)
    [InlineData("230fc31d2c2eb20e78711c82574f29f28117eba3", InputType.ArkanoidPaddle)] // Arkanoid (USA)
    public void Accessory_games_get_their_controls_automatically(string sha1, InputType input)
    {
        new GameProfiles(GameDb()).Resolve(sha1, null).Input.Should().Be(input);
    }

    [Fact]
    public void Combo_carts_keep_a_controller_for_player_2()
    {
        // Super Mario Bros. + Duck Hunt (USA): Mario's two-player game needs the second controller, so no Zapper by default.
        new GameProfiles(GameDb()).Resolve("36e695ba6cacc8a5098fb67d9c1f8e6828d6c2c6", null).Input.Should().BeNull();
    }

    [Fact]
    public void Metroids_password_is_read_from_its_buffer_on_the_password_screen()
    {
        var definition = new PasswordDefinitions(Path.Combine(GameDb(), "passwords")).For("fdbfc7871962f72a1ef57e5a7e456164fb93430b")!;
        var ram = new byte[0x8000];
        ram[0x1D] = 1; // GameMode: title/password screen
        ram[0x1F] = 0x1A; // TitleRoutine: WaitForSTART, showing the password
        for (var i = 0; i < 24; i++)
        {
            ram[0x699A + i] = (byte)(i == 23 ? 63 : 10 + i); // "ABCDEF…" and "-" last
        }

        definition.IsPasswordScreen(a => ram[a]).Should().BeTrue();
        definition.Decode(a => ram[a], _ => 0).Replace(" ", string.Empty).Should().Be("ABCDEFGHIJKLMNOPQRSTUVW-");

        ram[0x1F] = 0x18; // EnterPassword: what the player is typing isn't captured
        definition.IsPasswordScreen(a => ram[a]).Should().BeFalse();
    }

    [Fact]
    public void Super_mario_bros_counts_lives_only_during_play()
    {
        var map = new MemoryMaps(Path.Combine(GameDb(), "memory")).For("facee9c577a5262dbe33ac4930bb0b58c8c037f7")!;
        var ram = new byte[0x800];
        ram[0x075A] = 2; // NumberofLives: a new game's three lives, stored as lives - 1

        map.IsPlaying(a => ram[a]).Should().BeFalse("the title screen and demo are OperMode 0");
        ram[0x0770] = 1;
        map.IsPlaying(a => ram[a]).Should().BeTrue();
        map.Read(map.Lives!, a => ram[a]).Should().Be(3);
        map.Read("world", a => ram[a]).Should().Be(1);
    }

    [Fact]
    public void Zelda_reads_hearts_and_rooms_for_the_map()
    {
        var map = new MemoryMaps(Path.Combine(GameDb(), "memory")).For("a12d74c73a0481599a5d832361d168f4737bbcf6")!;
        var ram = new byte[0x800];
        ram[0x0012] = 5; // GameMode: play
        ram[0x066F] = 0x22; // a new game: 3 containers, 3 full hearts
        ram[0x00EB] = 0x77; // the starting screen

        map.IsPlaying(a => ram[a]).Should().BeTrue();
        map.Read("containers", a => ram[a]).Should().Be(3);
        map.Read("hearts", a => ram[a]).Should().Be(3);
        map.Map!.Columns.Should().Be(16);
        map.Read(map.Map.Room, a => ram[a]).Should().Be(0x77);
    }
}
