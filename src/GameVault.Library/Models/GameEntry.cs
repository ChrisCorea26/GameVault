namespace GameVault.Library.Models;

/// <summary>A catalog game plus the user's collection data for it. Metadata only: never ROM contents.</summary>
public sealed record GameEntry
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public GameRegion Region { get; init; }

    public string? Publisher { get; init; }

    public int? ReleaseYear { get; init; }

    public string? Genre { get; init; }

    public int Players { get; init; } = 1;

    /// <summary>False for homebrew, unlicensed and unknown releases.</summary>
    public bool IsLicensed { get; init; } = true;

    public Ownership Owned { get; init; }

    public PlayStatus Status { get; init; }

    public bool IsFavorite { get; init; }

    /// <summary>1–5, or null if unrated.</summary>
    public int? Rating { get; init; }

    public TimeSpan Playtime { get; init; }

    public DateTimeOffset? LastPlayed { get; init; }

    /// <summary>Path to the user's own ROM file, or null if none is in the library.</summary>
    public string? RomPath { get; init; }

    public bool IsOwned => Owned != Ownership.None;

    public bool IsPlayable => RomPath is not null;
}
