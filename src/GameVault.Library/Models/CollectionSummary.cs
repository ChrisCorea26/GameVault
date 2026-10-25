namespace GameVault.Library.Models;

/// <summary>Collection completion, such as "214 / 677 licensed NA releases owned" (SPEC §6.3).</summary>
public sealed record CollectionSummary(int Owned, int Total, GameRegion? Region)
{
    public double Fraction => Total == 0 ? 0 : (double)Owned / Total;

    /// <summary>Counts licensed releases, optionally limited to one region.</summary>
    public static CollectionSummary Compute(IEnumerable<GameEntry> games, GameRegion? region = null)
    {
        ArgumentNullException.ThrowIfNull(games);

        int owned = 0, total = 0;
        foreach (var game in games)
        {
            if (!game.IsLicensed || (region is not null && game.Region != region))
            {
                continue;
            }

            total++;
            if (game.IsOwned)
            {
                owned++;
            }
        }

        return new CollectionSummary(owned, total, region);
    }

    public override string ToString() =>
        $"{Owned} / {Total} licensed {RegionLabel(Region)}releases owned";

    private static string RegionLabel(GameRegion? region) => region switch
    {
        GameRegion.NorthAmerica => "NA ",
        GameRegion.Europe => "EU ",
        GameRegion.Japan => "JP ",
        _ => string.Empty,
    };
}
