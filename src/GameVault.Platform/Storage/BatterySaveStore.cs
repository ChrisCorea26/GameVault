namespace GameVault.Platform.Storage;

/// <summary>
/// Battery-backed cartridge RAM as <c>.sav</c> files in <see cref="AppPaths.SavesDirectory"/>, never next to
/// the ROM (SPEC §4.5). Files are named "Title - hash.sav" and found by the hash, so renaming a ROM keeps
/// its save.
/// </summary>
public sealed class BatterySaveStore(AppPaths paths)
{
    public string PathFor(string title, ulong romHash)
    {
        var existing = Find(romHash);
        return existing ?? Path.Combine(paths.SavesDirectory, $"{SafeName(title)} - {romHash:x16}.sav");
    }

    /// <summary>Reads the save into <paramref name="destination"/>. Returns false when there is none.</summary>
    public bool TryLoad(ulong romHash, Span<byte> destination)
    {
        var path = Find(romHash);
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
    public void Save(string title, ulong romHash, ReadOnlySpan<byte> data)
    {
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

        return Directory.EnumerateFiles(paths.SavesDirectory, $"*{romHash:x16}.sav").FirstOrDefault();
    }
}
