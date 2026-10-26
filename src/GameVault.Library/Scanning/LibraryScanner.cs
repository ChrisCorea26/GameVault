using System.IO.Compression;
using GameVault.Library.Data;
using GameVault.Library.Hashing;

namespace GameVault.Library.Scanning;

public sealed record ScanProgress(int FilesFound, int FilesHashed, string? CurrentFile);

public sealed record ScanResult(int Added, int Updated, int Removed, int Unchanged, int Unreadable)
{
    public int Total => Added + Updated + Unchanged;
}

/// <summary>
/// Finds the user's ROMs in their library folders (SPEC §6.2): <c>.nes</c> and <c>.fds</c> (Famicom Disk
/// System) files, and <c>.zip</c> files with exactly one of them inside. Hashes them without the header and matches them against the catalog by
/// SHA-1, then CRC32. Unmatched files are kept as "Unknown / Homebrew". Files whose size and modified time
/// haven't changed since the last scan are skipped.
/// </summary>
public sealed class LibraryScanner(LibraryDatabase database)
{
    /// <summary>Largest file considered a ROM (the biggest NES 2.0 images are well under this).</summary>

    public ScanResult Scan(IEnumerable<string> folders, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var candidates = folders
            .Where(Directory.Exists)
            .SelectMany(EnumerateCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        progress?.Report(new ScanProgress(candidates.Count, 0, null));

        using var connection = database.Open();
        var known = new Dictionary<string, (long Size, long Mtime)>(StringComparer.OrdinalIgnoreCase);
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT path, size, mtime FROM files;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                known[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
            }
        }

        // Hash in parallel; the database writes happen afterwards on this thread.
        var changed = new System.Collections.Concurrent.ConcurrentBag<(string Path, long Size, long Mtime, RomHashes Hashes, GameVault.Core.Systems.GameSystem System)>();
        var unchanged = 0;
        var unreadable = 0;
        var hashed = 0;
        Parallel.ForEach(
            candidates,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            path =>
            {
                try
                {
                    var info = new FileInfo(path);
                    var mtime = info.LastWriteTimeUtc.Ticks;
                    if (known.TryGetValue(path, out var previous) && previous.Size == info.Length && previous.Mtime == mtime)
                    {
                        Interlocked.Increment(ref unchanged);
                    }
                    else if (RomFiles.Read(path) is { } rom)
                    {
                        changed.Add((path, info.Length, mtime, RomHasher.Hash(rom.Data, rom.System), rom.System));
                    }
                    else
                    {
                        Interlocked.Increment(ref unreadable);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Interlocked.Increment(ref unreadable);
                }

                var done = Interlocked.Increment(ref hashed);
                if (done % 25 == 0)
                {
                    progress?.Report(new ScanProgress(candidates.Count, done, path));
                }
            });

        var present = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        var removedPaths = known.Keys.Where(p => !present.Contains(p) && IsUnder(p, folders)).ToList();
        var added = 0;
        var updated = 0;

        using (var transaction = connection.BeginTransaction())
        {
            foreach (var file in changed)
            {
                var gameKey = Match(connection, file.Hashes);
                LibraryDatabase.Execute(
                    connection,
                    "INSERT OR REPLACE INTO files (path, size, mtime, crc32, sha1, game_key, system) VALUES ($p, $s, $m, $c, $h, $g, $y);",
                    ("$p", file.Path), ("$s", file.Size), ("$m", file.Mtime), ("$c", file.Hashes.Crc32), ("$h", file.Hashes.Sha1), ("$g", gameKey),
                    ("$y", (int)file.System));
                if (known.ContainsKey(file.Path))
                {
                    updated++;
                }
                else
                {
                    added++;
                }
            }

            foreach (var path in removedPaths)
            {
                LibraryDatabase.Execute(connection, "DELETE FROM files WHERE path = $p;", ("$p", path));
            }

            transaction.Commit();
        }

        progress?.Report(new ScanProgress(candidates.Count, candidates.Count, null));
        return new ScanResult(added, updated, removedPaths.Count, unchanged, unreadable);
    }

    /// <summary>Re-matches every file after a catalog import, so newly cataloged games are recognized.</summary>
    public void Rematch()
    {
        using var connection = database.Open();
        var files = new List<(string Path, RomHashes Hashes)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT path, crc32, sha1 FROM files;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                files.Add((reader.GetString(0), new RomHashes(reader.GetString(1), reader.GetString(2))));
            }
        }

        using var transaction = connection.BeginTransaction();
        foreach (var (path, hashes) in files)
        {
            LibraryDatabase.Execute(connection, "UPDATE files SET game_key = $g WHERE path = $p;", ("$g", Match(connection, hashes)), ("$p", path));
        }

        transaction.Commit();
    }

    /// <summary>True for the file types GameVault plays (see <see cref="RomFiles"/>).</summary>
    public static bool IsGameFile(string path) => RomFiles.IsGameFile(path);

    /// <summary>A game file's bytes, or the single game file inside a .zip. Null when it isn't a usable ROM file.</summary>
    public static byte[]? ReadRom(string path) => RomFiles.Read(path)?.Data;

    private static string Match(Microsoft.Data.Sqlite.SqliteConnection connection, RomHashes hashes)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key FROM catalog WHERE key = $sha OR (sha1 IS NULL AND crc32 = $crc) ORDER BY (key = $sha) DESC LIMIT 1;";
        command.Parameters.AddWithValue("$sha", hashes.Key);
        command.Parameters.AddWithValue("$crc", hashes.Crc32);
        return command.ExecuteScalar() as string ?? hashes.Key;
    }

    private static IEnumerable<string> EnumerateCandidates(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        return RomFiles.Extensions.Append(".zip").SelectMany(extension => Directory.EnumerateFiles(folder, "*" + extension, options));
    }

    private static bool IsUnder(string path, IEnumerable<string> folders) =>
        folders.Any(f => path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
}
