namespace GameVault.Platform.Storage;

/// <summary>
/// Battery-backed cartridge RAM as <c>.sav</c> files in <see cref="AppPaths.SavesDirectory"/>, never next to
/// the ROM unless the player opts in (SPEC §4.5). Files are named "Title - hash.sav" and found by the hash, so renaming
/// a ROM keeps its save. Famicom Disk System games use the same scheme with <c>.fdssave</c> disk patches.
/// </summary>
/// <remarks>
/// With Settings → Saves → "Keep saves next to the game files" (GV-012), callers pass a <c>besideRom</c> path such as
/// "C:\Games\Game.sav": that file is read first and written instead, and when its folder can't be written to the save
/// goes to the Saves folder as before.
/// </remarks>
public sealed class BatterySaveStore(AppPaths paths, string extension = ".sav")
{
    public string PathFor(string title, ulong romHash)
    {
        var existing = Find(romHash);
        return existing ?? Path.Combine(paths.SavesDirectory, $"{SafeName(title)} - {romHash:x16}{extension}");
    }

    /// <summary>The whole save file, or null when there is none.</summary>
    public byte[]? TryRead(ulong romHash) => Find(romHash) is { } path ? File.ReadAllBytes(path) : null;

    /// <summary>Where a save next to the game file goes: the file's name with this store's extension.</summary>
    public string BesideRom(string romPath) => Path.ChangeExtension(romPath, extension);

    /// <summary>Reads the save into <paramref name="destination"/>. Returns false when there is none.</summary>
    public bool TryLoad(ulong romHash, Span<byte> destination, string? besideRom = null)
    {
        var path = besideRom is not null && File.Exists(besideRom) ? besideRom : Find(romHash);
        if (path is null)
        {
            return false;
        }

        var data = File.ReadAllBytes(path);
        destination.Clear();
        data.AsSpan(0, Math.Min(data.Length, destination.Length)).CopyTo(destination);
        return true;
    }

    /// <summary>Writes atomically: a temporary file first, then a replace, so a crash never leaves half a save.</summary>
    public void Save(string title, ulong romHash, ReadOnlySpan<byte> data, string? besideRom = null)
    {
        if (besideRom is not null)
        {
            try
            {
                WriteAtomically(besideRom, data);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A read-only folder (a CD, a network share): keep the save in GameVault's folder instead.
            }
        }

        var path = PathFor(title, romHash);
        WriteAtomically(path, data);
    }

    internal static void WriteAtomically(string path, ReadOnlySpan<byte> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(data);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    internal static string SafeName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = title.Select(c => invalid.Contains(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? '_' : c).ToArray();
        var name = new string(chars).Trim();
        return name.Length == 0 ? "Game" : name;
    }

    private string? Find(ulong romHash)
    {
        if (!Directory.Exists(paths.SavesDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(paths.SavesDirectory, $"*{romHash:x16}{extension}").FirstOrDefault();
    }
}
