using GameVault.Core;
using GameVault.Library.Models;
using GameVault.Library.Scanning;

namespace GameVault.Library.Tests;

/// <summary>SPEC §6.4 (GV-008): every bundled homebrew game has its license and plays.</summary>
public sealed class BundledHomebrewTests
{
    private static string Folder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GameVault.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("The repository root wasn't found."), "data", "homebrew");
    }

    [Fact]
    public void Every_folder_has_a_license_a_readme_and_a_game_the_library_reads()
    {
        var games = BundledHomebrew.Read(Folder());

        games.Should().HaveCountGreaterThanOrEqualTo(4);
        Directory.GetDirectories(Folder()).Should().HaveCount(games.Count, "every folder has a readable game.json");
        foreach (var (path, game) in games)
        {
            var folder = Path.GetDirectoryName(path)!;
            File.Exists(Path.Combine(folder, "LICENSE")).Should().BeTrue(game.Title);
            File.Exists(Path.Combine(folder, "README.md")).Should().BeTrue(game.Title);
            game.License.Should().NotBeNullOrWhiteSpace(game.Title);
            RomFiles.Read(path).Should().NotBeNull(game.Title);
        }
    }

    [Fact]
    public void Every_game_starts_and_runs()
    {
        foreach (var (path, game) in BundledHomebrew.Read(Folder()))
        {
            var console = NesConsole.FromBytes(File.ReadAllBytes(path));
            console.Power();
            for (var frame = 0; frame < 300; frame++)
            {
                console.RunFrame();
            }

            console.Ppu.FrameBuffer.ToArray().Distinct().Count().Should().BeGreaterThan(1, $"{game.Title} draws something in 5 seconds");
        }
    }

    [Fact]
    public void Bundled_details_fill_in_an_unnamed_entry_but_never_a_players_own()
    {
        var game = new BundledGame { File = "x.nes", Title = "Thwaite", Developer = "Damian Yerrick", Year = 2011, Players = 2 };
        var entry = new GameEntry { Key = "sha1:aa", Title = "thwaite", RomPath = Path.Combine("data", "homebrew", "thwaite", "thwaite.nes") };

        var described = BundledHomebrew.Describe(entry, game);
        described.Title.Should().Be("Thwaite");
        described.Players.Should().Be(2);
        described.Region.Should().Be(GameRegion.World);

        BundledHomebrew.Describe(entry with { Title = "My name" }, game).Title.Should().Be("My name");
    }
}
