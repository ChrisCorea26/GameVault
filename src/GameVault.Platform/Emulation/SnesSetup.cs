using GameVault.ViewModels.Services;

namespace GameVault.Platform.Emulation;

/// <summary>
/// What a Super Nintendo game may need besides itself (outstanding-work plan §4): the player's own enhancement chip
/// program ROMs (DSP-1 and the others), read from the folder chosen in Settings → Super Nintendo. GameVault never ships
/// them.
/// </summary>
public sealed record SnesSetup(IReadOnlyDictionary<string, byte[]> ChipRoms)
{
    /// <summary>The program ROM files GameVault looks for, by the names emulators use for them.</summary>
    public static IReadOnlyList<string> ChipRomFiles { get; } = ["dsp1b.rom", "dsp1.rom", "dsp2.rom", "dsp3.rom", "dsp4.rom"];

    /// <summary>The largest chip ROM read (the µPD7725's are 8 KB; later chips' are up to 64 KB).</summary>
    private const int MaxChipRomSize = 64 * 1024;

    public static SnesSetup None { get; } = new(new Dictionary<string, byte[]>());

    /// <summary>A chip ROM by file name, or null when the player hasn't put it in the folder.</summary>
    public byte[]? ChipRom(string fileName) => ChipRoms.TryGetValue(fileName, out var rom) ? rom : null;

    /// <summary>Reads the chip ROMs in the folder chosen in <paramref name="settings"/>; none when no folder is chosen.</summary>
    /// <exception cref="GameLoadException">The folder can't be read.</exception>
    public static async Task<SnesSetup> LoadAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var folder = settings.SnesChipRomFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return None;
        }

        var roms = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(path);
                if (ChipRomFiles.Contains(name, StringComparer.OrdinalIgnoreCase) && new FileInfo(path).Length <= MaxChipRomSize)
                {
                    roms[name.ToLowerInvariant()] = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GameLoadException(
                "GameVault couldn't read your enhancement chip ROMs. Check the folder chosen in Settings → Super Nintendo.", ex);
        }

        return new SnesSetup(roms);
    }
}
