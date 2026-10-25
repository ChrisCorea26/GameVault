namespace GameVault.Library.Models;

/// <summary>A catalog game plus the user's collection data for it. Metadata only: never ROM contents.</summary>
public sealed record GameEntry
{
    /// <summary>
    /// Stable identity: "sha1:…" (headerless ROM hash) for cataloged and scanned games, "crc:…" for catalog
    /// entries that only list a CRC32.
    /// </summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    public GameRegion Region { get; init; }

    public string? Publisher { get; init; }

    public string? Developer { get; init; }

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

    public string? Notes { get; init; }

    /// <summary>The user's own cover picture (SPEC §6.5); GameVault never ships or downloads box art.</summary>
    public string? CoverPath { get; init; }

    /// <summary>True when the game comes from an imported catalog (a "checklist" entry).</summary>
    public bool IsCataloged { get; init; }

    /// <summary>Headerless SHA-1 of the user's file, when they have one.</summary>
    public string? Sha1 { get; init; }

    /// <summary>Path to the user's own ROM file, or null if none is in the library.</summary>
    public string? RomPath { get; init; }

    public bool IsOwned => Owned != Ownership.None;

    public bool IsPlayable => RomPath is not null;
}
