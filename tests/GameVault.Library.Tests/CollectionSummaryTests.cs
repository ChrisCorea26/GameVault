using GameVault.Library.Models;

namespace GameVault.Library.Tests;

public class CollectionSummaryTests
{
    private static GameEntry Game(GameRegion region, Ownership owned = Ownership.None, bool licensed = true) => new()
    {
        Id = Guid.NewGuid(),
        Title = "Test Game",
        Region = region,
        Owned = owned,
        IsLicensed = licensed,
    };

    [Fact]
    public void Counts_owned_licensed_games_in_the_requested_region()
    {
        GameEntry[] games =
        [
            Game(GameRegion.NorthAmerica, Ownership.Cartridge),
            Game(GameRegion.NorthAmerica, Ownership.DigitalDump | Ownership.Box),
            Game(GameRegion.NorthAmerica),
            Game(GameRegion.Japan, Ownership.Cartridge),
            Game(GameRegion.NorthAmerica, Ownership.Cartridge, licensed: false),
        ];

        var summary = CollectionSummary.Compute(games, GameRegion.NorthAmerica);

        summary.Owned.Should().Be(2);
        summary.Total.Should().Be(3);
        summary.ToString().Should().Be("2 / 3 licensed NA releases owned");
    }

    [Fact]
    public void Without_a_region_counts_every_licensed_game()
    {
        GameEntry[] games =
        [
            Game(GameRegion.NorthAmerica, Ownership.Cartridge),
            Game(GameRegion.Japan),
            Game(GameRegion.Europe, Ownership.Manual),
        ];

        var summary = CollectionSummary.Compute(games);

        summary.Should().Be(new CollectionSummary(2, 3, null));
        summary.ToString().Should().Be("2 / 3 licensed releases owned");
        summary.Fraction.Should().BeApproximately(2.0 / 3, 1e-9);
    }

    [Fact]
    public void Empty_collection_has_zero_fraction()
    {
        CollectionSummary.Compute([]).Fraction.Should().Be(0);
    }
}
