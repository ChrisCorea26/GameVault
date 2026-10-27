using GameVault.Library.Models;

namespace GameVault.ViewModels;

/// <summary>Display data for one tile in the library grid or list.</summary>
public sealed class GameTileViewModel(GameEntry game)
{
    public GameEntry Game { get; } = game;

    public string Title => Game.Title;

    /// <summary>"NES", "Master System", "SG-1000": the tile's system badge and the library's system filter.</summary>
    public string SystemText => SystemName(Game.System);

    public static string SystemName(GameVault.Core.Systems.GameSystem system) => system switch
    {
        GameVault.Core.Systems.GameSystem.MasterSystem => "Master System",
        GameVault.Core.Systems.GameSystem.Sg1000 => "SG-1000",
        GameVault.Core.Systems.GameSystem.AppleII => "Apple II",
        GameVault.Core.Systems.GameSystem.Genesis => "Genesis",
        GameVault.Core.Systems.GameSystem.Snes => "Super Nintendo",
        GameVault.Core.Systems.GameSystem.PlayStation => "PlayStation",
        GameVault.Core.Systems.GameSystem.Nintendo64 => "Nintendo 64",
        GameVault.Core.Systems.GameSystem.PlayStation2 => "PlayStation 2",
        GameVault.Core.Systems.GameSystem.SegaCd => "Sega CD",
        GameVault.Core.Systems.GameSystem.Atari2600 => "Atari 2600",
        GameVault.Core.Systems.GameSystem.Atari5200 => "Atari 5200",
        GameVault.Core.Systems.GameSystem.GameCube => "GameCube",
        _ => "NES",
    };

    /// <summary>For example "1988 · Lumen Soft · Platformer".</summary>
    public string Subtitle => string.Join(" · ", new[]
    {
        Game.ReleaseYear?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Game.Publisher,
        Game.Genre,
    }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Up to two letters for the placeholder cover until the user adds their own artwork.</summary>
    public string Initials
    {
        get
        {
            var words = Game.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => char.IsLetterOrDigit(w[0]))
                .ToArray();
            return words.Length switch
            {
                0 => "?",
                1 => words[0][..1].ToUpperInvariant(),
                _ => string.Concat(words[0][0], words[1][0]).ToUpperInvariant(),
            };
        }
    }

    public string StatusText => Game.Status switch
    {
        PlayStatus.Playing => "Playing",
        PlayStatus.Beaten => "Beaten",
        PlayStatus.Completed => "100%",
        _ => "Unplayed",
    };

    public bool IsOwned => Game.IsOwned;

    public bool IsFavorite => Game.IsFavorite;

    public bool IsPlayable => Game.IsPlayable;

    /// <summary>The user's own cover picture (SPEC §6.5), as a file URI.</summary>
    public Uri? CoverUri => Game.CoverPath is { } path ? new Uri(path) : null;

    public bool HasCover => Game.CoverPath is not null;

    /// <summary>Plain-language playtime, or an empty string if never played.</summary>
    public string PlaytimeText => Game.Playtime switch
    {
        { TotalMinutes: < 1 } => string.Empty,
        { TotalHours: < 1 } t => $"{(int)t.TotalMinutes} min played",
        var t => $"{t.TotalHours:0.#} h played",
    };

    public bool Matches(string search) =>
        string.IsNullOrWhiteSpace(search)
        || Game.Title.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || (Game.Publisher?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false)
        || (Game.Genre?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
}
