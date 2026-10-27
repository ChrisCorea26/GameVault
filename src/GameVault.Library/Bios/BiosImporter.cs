namespace GameVault.Library.Bios;

/// <summary>A BIOS the player supplies from their own console. GameVault never includes one.</summary>
public enum BiosKind
{
    /// <summary>The Famicom Disk System BIOS (disksys.rom, 8 KB).</summary>
    FamicomDiskSystem,

    /// <summary>The PlayStation BIOS (512 KB, such as SCPH-1001).</summary>
    PlayStation,

    /// <summary>The PlayStation 2 BIOS (4 MB, such as SCPH-39001).</summary>
    PlayStation2,

    /// <summary>The Atari 5200 BIOS (5200.rom, 2 KB).</summary>
    Atari5200,
    /// <summary>The Sega CD BIOS (128 KB, such as the Model 1 or 2 US BIOS).</summary>
    SegaCd,
}

/// <summary>What happened when the player chose a BIOS file.</summary>
/// <param name="Succeeded">True when the file was checked and copied.</param>
/// <param name="Message">A sentence for the player: what was imported, or what's wrong.</param>
/// <param name="Path">Where the copy is (the path Settings should keep); null when it failed.</param>
public sealed record BiosImport(bool Succeeded, string Message, string? Path = null);

/// <summary>
/// Copies a BIOS the player chose into GameVault's own folder (<c>%LOCALAPPDATA%\GameVault\bios\&lt;system&gt;\</c>),
/// after checking that it's the right size, so it keeps working if the original is moved or on a removable drive.
/// </summary>
/// <param name="biosDirectory">The <c>bios</c> folder; each system gets a subfolder.</param>
public sealed class BiosImporter(string biosDirectory)
{
    public const int FdsBiosSize = 8 * 1024;

    public const int PlayStationBiosSize = 512 * 1024;

    public const int Atari5200BiosSize = 2 * 1024;
    public const int SegaCdBiosSize = 128 * 1024;

    public string BiosDirectory { get; } = biosDirectory;

    /// <summary>The folder a BIOS of this kind is copied to.</summary>
    public string FolderFor(BiosKind kind) => Path.Combine(BiosDirectory, kind switch
    {
        BiosKind.FamicomDiskSystem => "fds",
        BiosKind.PlayStation => "ps1",
        BiosKind.PlayStation2 => "ps2",
        BiosKind.Atari5200 => "a5200",
        BiosKind.SegaCd => "segacd",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    });

    /// <summary>
    /// Checks <paramref name="sourcePath"/> and, if it's a BIOS of this kind, copies it (keeping its name, replacing a
    /// copy with the same name). Nothing is copied when the check fails.
    /// </summary>
    public BiosImport Import(BiosKind kind, string sourcePath)
    {
        var source = (sourcePath ?? string.Empty).Trim().Trim('"');
        var check = Check(kind, source);
        if (!check.Succeeded)
        {
            return check;
        }

        try
        {
            var folder = FolderFor(kind);
            Directory.CreateDirectory(folder);
            var destination = Path.Combine(folder, Path.GetFileName(source));
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, destination, overwrite: true);
            }

            return new BiosImport(true, check.Message, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new BiosImport(false, $"GameVault couldn't copy this file: {ex.Message}");
        }
    }

    private static BiosImport Check(BiosKind kind, string source)
    {
        if (kind == BiosKind.PlayStation2)
        {
            var ps2 = Ps2Bios.Check(source);
            return new BiosImport(ps2.IsValid, ps2.Message);
        }

        if (source.Length == 0)
        {
            return new BiosImport(false, "Choose a BIOS file.");
        }

        long length;
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists)
            {
                return new BiosImport(false, "GameVault can't find this file.");
            }

            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new BiosImport(false, $"GameVault can't read this file: {ex.Message}");
        }

        return kind switch
        {
            BiosKind.FamicomDiskSystem => length == FdsBiosSize
                ? new BiosImport(true, "Disk System BIOS (8 KB).")
                : new BiosImport(false, "This file isn't a Disk System BIOS: disksys.rom is 8 KB."),
            BiosKind.PlayStation => length == PlayStationBiosSize
                ? new BiosImport(true, "PlayStation BIOS (512 KB).")
                : new BiosImport(false, "This file isn't a PlayStation BIOS: a BIOS is 512 KB."),
            BiosKind.Atari5200 => length == Atari5200BiosSize
                ? new BiosImport(true, "Atari 5200 BIOS (2 KB).")
                : new BiosImport(false, "This file isn't an Atari 5200 BIOS: 5200.rom is 2 KB."),
            BiosKind.SegaCd => length == SegaCdBiosSize
                ? new BiosImport(true, "Sega CD BIOS (128 KB).")
                : new BiosImport(false, "This file isn't a Sega CD BIOS: a BIOS is 128 KB."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}
