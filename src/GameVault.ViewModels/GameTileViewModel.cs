using GameVault.Library.Models;

namespace GameVault.ViewModels;

/// <summary>Display data for one tile in the library grid or list.</summary>
public sealed class GameTileViewModel(GameEntry game)
{
    public GameEntry Game { get; } = game;

    public string Title => Game.Title;

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
