using GameVault.Library.Models;
using Microsoft.Data.Sqlite;

namespace GameVault.Library.Data;

/// <summary>Reads and writes the user's library: games, collection fields, playtime and folders.</summary>
public sealed class LibraryRepository(LibraryDatabase database)
{
    /// <summary>
    /// Every game the library knows: imported catalog entries (with the user's file, if any) plus scanned
    /// files that matched no catalog entry ("Unknown / Homebrew").
    /// </summary>
    public IReadOnlyList<GameEntry> GetGames()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.key, c.title, COALESCE(c.region, col.custom_region), COALESCE(c.publisher, col.custom_publisher),
                   COALESCE(c.developer, col.custom_developer), COALESCE(c.year, col.custom_year), COALESCE(c.genre, col.custom_genre),
                   COALESCE(c.players, col.custom_players), c.licensed,
                   (SELECT f.path FROM files f WHERE f.game_key = g.key ORDER BY f.path LIMIT 1) AS path,
                   (SELECT f.sha1 FROM files f WHERE f.game_key = g.key ORDER BY f.path LIMIT 1) AS sha1,
                   col.owned, col.status, col.favorite, col.rating, col.notes, col.cover_path, col.custom_title,
                   col.playtime_seconds, col.last_played, c.key IS NOT NULL AS cataloged,
                   COALESCE(c.system, (SELECT f.system FROM files f WHERE f.game_key = g.key ORDER BY f.path LIMIT 1), 0) AS system
            FROM (SELECT key FROM catalog UNION SELECT game_key AS key FROM files) g
            LEFT JOIN catalog c ON c.key = g.key
            LEFT JOIN collection col ON col.game_key = g.key;
            """;
        var games = new List<GameEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            games.Add(Read(reader));
        }

        return games;
    }

    public GameEntry? GetGame(string key) => GetGames().FirstOrDefault(g => g.Key == key);

    /// <summary>
    /// Saves the fields the user edits: ownership, status, favorite, rating, notes and cover, and for games no catalog
    /// lists their title and details (SPEC §6.2; GV-011). Cataloged games keep the catalog's details.
    /// </summary>
    public void SaveCollection(GameEntry game)
    {
        using var connection = database.Open();
        var custom = !game.IsCataloged;
        LibraryDatabase.Execute(
            connection,
            """
            INSERT INTO collection (game_key, owned, status, favorite, rating, notes, cover_path, custom_title,
                custom_region, custom_publisher, custom_developer, custom_year, custom_genre, custom_players)
            VALUES ($k, $o, $s, $f, $r, $n, $c, $t, $rg, $pb, $dv, $yr, $gn, $pl)
            ON CONFLICT(game_key) DO UPDATE SET owned = $o, status = $s, favorite = $f, rating = $r, notes = $n,
                cover_path = $c, custom_title = $t, custom_region = $rg, custom_publisher = $pb, custom_developer = $dv,
                custom_year = $yr, custom_genre = $gn, custom_players = $pl;
            """,
            ("$k", game.Key), ("$o", (int)game.Owned), ("$s", (int)game.Status), ("$f", game.IsFavorite ? 1 : 0),
            ("$r", game.Rating), ("$n", game.Notes), ("$c", game.CoverPath),
            ("$t", custom ? game.Title : null),
            ("$rg", custom && game.Region != GameRegion.Unknown ? (int)game.Region : null),
            ("$pb", custom ? game.Publisher : null),
            ("$dv", custom ? game.Developer : null),
            ("$yr", custom ? game.ReleaseYear : null),
            ("$gn", custom ? game.Genre : null),
            ("$pl", custom && game.Players != 1 ? game.Players : null));
    }

    public void AddPlaytime(string key, TimeSpan time, DateTimeOffset when)
    {
        using var connection = database.Open();
        LibraryDatabase.Execute(
            connection,
            """
            INSERT INTO collection (game_key, playtime_seconds, last_played) VALUES ($k, $t, $w)
            ON CONFLICT(game_key) DO UPDATE SET playtime_seconds = playtime_seconds + $t, last_played = $w;
            """,
            ("$k", key), ("$t", time.TotalSeconds), ("$w", when.ToUnixTimeSeconds()));
    }

    /// <summary>The user's per-game overrides of profile settings (Game Properties), as JSON.</summary>
    public string? GetOverrides(string key)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT overrides FROM collection WHERE game_key = $k;";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    public void SetOverrides(string key, string? json)
    {
        using var connection = database.Open();
        LibraryDatabase.Execute(
            connection,
            "INSERT INTO collection (game_key, overrides) VALUES ($k, $o) ON CONFLICT(game_key) DO UPDATE SET overrides = $o;",
            ("$k", key), ("$o", json));
    }

    public IReadOnlyList<string> GetFolders()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM folders ORDER BY path;";
        var folders = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            folders.Add(reader.GetString(0));
        }

        return folders;
    }

    public void AddFolder(string path)
    {
        using var connection = database.Open();
        LibraryDatabase.Execute(connection, "INSERT OR IGNORE INTO folders (path) VALUES ($p);", ("$p", Path.GetFullPath(path)));
    }

    /// <summary>Removes a folder and forgets its files. Collection data (ownership, notes, playtime) is kept.</summary>
    public void RemoveFolder(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        LibraryDatabase.Execute(connection, "DELETE FROM folders WHERE path = $p;", ("$p", full));
        LibraryDatabase.Execute(
            connection,
            "DELETE FROM files WHERE substr(path, 1, length($p) + 1) = $p || $sep;",
            ("$p", full), ("$sep", Path.DirectorySeparatorChar.ToString()));
        transaction.Commit();
    }

    private static GameEntry Read(SqliteDataReader r)
    {
        var key = r.GetString(0);
        var path = r.IsDBNull(9) ? null : r.GetString(9);
        var cataloged = r.GetInt64(20) != 0;
        var owned = r.IsDBNull(11) ? Ownership.None : (Ownership)r.GetInt64(11);
        var title = !r.IsDBNull(1) ? r.GetString(1)
            : !r.IsDBNull(17) ? r.GetString(17)
            : path is not null ? Path.GetFileNameWithoutExtension(path)
            : key;

        return new GameEntry
        {
            Key = key,
            Title = title,
            Region = r.IsDBNull(2) ? GameRegion.Unknown : (GameRegion)r.GetInt64(2),
            Publisher = r.IsDBNull(3) ? null : r.GetString(3),
            Developer = r.IsDBNull(4) ? null : r.GetString(4),
            ReleaseYear = r.IsDBNull(5) ? null : (int)r.GetInt64(5),
            Genre = r.IsDBNull(6) ? null : r.GetString(6),
            Players = r.IsDBNull(7) ? 1 : (int)r.GetInt64(7),
            IsLicensed = cataloged && r.GetInt64(8) != 0,
            RomPath = path,
            Sha1 = r.IsDBNull(10) ? null : r.GetString(10),

            // A file in the library is a digital dump the user owns.
            Owned = path is null ? owned : owned | Ownership.DigitalDump,
            Status = r.IsDBNull(12) ? PlayStatus.Unplayed : (PlayStatus)r.GetInt64(12),
            IsFavorite = !r.IsDBNull(13) && r.GetInt64(13) != 0,
            Rating = r.IsDBNull(14) ? null : (int)r.GetInt64(14),
            Notes = r.IsDBNull(15) ? null : r.GetString(15),
            CoverPath = r.IsDBNull(16) ? null : r.GetString(16),
            Playtime = r.IsDBNull(18) ? TimeSpan.Zero : TimeSpan.FromSeconds(r.GetDouble(18)),
            LastPlayed = r.IsDBNull(19) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(19)),
            IsCataloged = cataloged,
            System = (GameVault.Core.Systems.GameSystem)r.GetInt64(21),
        };
    }
}
