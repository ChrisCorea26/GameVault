using GameVault.Library.Data;
using GameVault.Library.Profiles;
using GameVault.Platform.Storage;

namespace GameVault.Platform.Tests;

/// <summary>SPEC §6.4 (GV-008): on first launch the bundled homebrew is in the library, with its details.</summary>
public sealed class BundledHomebrewLibraryTests
{
    [Fact]
    public async Task Adding_the_bundled_folder_lists_its_games_by_name()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "GameVault.sln")))
        {
            directory = directory.Parent;
        }

        using var library = new LibraryService(new LibraryDatabase(":memory:"), new GameProfiles(null));
        await library.AddBundledHomebrewAsync(Path.Combine(directory.FullName, "data", "homebrew"));

        var games = await library.GetGamesAsync();
        games.Select(g => g.Title).Should().Contain(["Thwaite", "Concentration Room", "RHDE: Furniture Fight", "robotfindskitten"]);
        var thwaite = games.Single(g => g.Title == "Thwaite");
        thwaite.IsPlayable.Should().BeTrue();
        thwaite.Developer.Should().Be("Damian Yerrick");
        thwaite.Players.Should().Be(2);
        thwaite.IsLicensed.Should().BeFalse("homebrew isn't a licensed release");
    }
}
