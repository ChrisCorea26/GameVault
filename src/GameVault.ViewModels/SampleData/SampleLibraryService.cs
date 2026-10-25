using GameVault.Library.Models;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>
/// A realistic but entirely fictional library for <c>--sample-data</c> UI previews and tests.
/// Titles and publishers are invented so no real product names or artwork are involved.
/// </summary>
public sealed class SampleLibraryService : ILibraryService
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<GameEntry> Games { get; } =
    [
        Game(1, "Star Courier", GameRegion.NorthAmerica, "Bitwave", 1987, "Shooter",
            Ownership.Cartridge | Ownership.Box | Ownership.Manual | Ownership.DigitalDump, PlayStatus.Completed,
            favorite: true, rating: 5, hours: 31.5, lastPlayedDaysAgo: 2),
        Game(2, "Crystal Caverns", GameRegion.NorthAmerica, "Lumen Soft", 1988, "Platformer",
            Ownership.Cartridge | Ownership.DigitalDump, PlayStatus.Playing, rating: 4, hours: 6.2, lastPlayedDaysAgo: 0),
        Game(3, "Knights of Ember Hall", GameRegion.NorthAmerica, "Redstone", 1989, "RPG",
            Ownership.Cartridge | Ownership.Box | Ownership.DigitalDump, PlayStatus.Beaten, rating: 5, hours: 48.0,
            lastPlayedDaysAgo: 40),
        Game(4, "Turbo Alley", GameRegion.NorthAmerica, "Bitwave", 1990, "Racing", Ownership.Cartridge,
            PlayStatus.Unplayed),
        Game(5, "Puzzle Pagoda", GameRegion.NorthAmerica, "Kitefox", 1991, "Puzzle",
            Ownership.DigitalDump, PlayStatus.Playing, hours: 2.75, lastPlayedDaysAgo: 5, players: 2),
        Game(6, "Harbor Patrol", GameRegion.NorthAmerica, "Northlight", 1988, "Action"),
        Game(7, "Moonlit Manor", GameRegion.NorthAmerica, "Lumen Soft", 1990, "Adventure"),
        Game(8, "Gridiron Legends", GameRegion.NorthAmerica, "Redstone", 1991, "Sports", players: 2),
        Game(9, "Sky Duelist", GameRegion.NorthAmerica, "Kitefox", 1989, "Shooter", players: 2),
        Game(10, "Rocket Rally", GameRegion.Japan, "Hoshi Works", 1986, "Racing",
            Ownership.Cartridge, PlayStatus.Beaten, hours: 3.0, lastPlayedDaysAgo: 120),
        Game(11, "Dragon Lantern", GameRegion.Japan, "Hoshi Works", 1987, "RPG"),
        Game(12, "Castle Tumble", GameRegion.Europe, "Northlight", 1992, "Platformer",
            Ownership.Cartridge | Ownership.Manual, PlayStatus.Unplayed),
        Game(13, "Byte Garden (Homebrew)", GameRegion.World, "Community", 2021, "Puzzle",
            Ownership.DigitalDump, PlayStatus.Playing, hours: 1.25, lastPlayedDaysAgo: 1, licensed: false),
        Game(14, "Pixel Pilgrim (Homebrew)", GameRegion.World, "Community", 2023, "Adventure",
            Ownership.DigitalDump, PlayStatus.Unplayed, licensed: false),
    ];

    public Task<IReadOnlyList<GameEntry>> GetGamesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Games);

    private static GameEntry Game(
        int id,
        string title,
        GameRegion region,
        string publisher,
        int year,
        string genre,
        Ownership owned = Ownership.None,
        PlayStatus status = PlayStatus.Unplayed,
        bool favorite = false,
        int? rating = null,
        double hours = 0,
        int? lastPlayedDaysAgo = null,
        int players = 1,
        bool licensed = true) => new()
        {
            Id = new Guid(id, 0, 0, new byte[8]),
            Title = title,
            Region = region,
            Publisher = publisher,
            ReleaseYear = year,
            Genre = genre,
            Players = players,
            IsLicensed = licensed,
            Owned = owned,
            Status = status,
            IsFavorite = favorite,
            Rating = rating,
            Playtime = TimeSpan.FromHours(hours),
            LastPlayed = lastPlayedDaysAgo is { } days ? Now.AddDays(-days) : null,
            RomPath = owned.HasFlag(Ownership.DigitalDump) ? $@"C:\Games\NES\{title}.nes" : null,
        };
}
