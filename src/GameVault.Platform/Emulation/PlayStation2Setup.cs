using GameVault.Library.Bios;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Emulation;

/// <summary>
/// What a PlayStation 2 needs besides the game (Phase 17): the player's BIOS from Settings (null when none is chosen:
/// ELFs then run on GameVault's own kernel) and the "Skip the startup screens" choice.
/// </summary>
public sealed record PlayStation2Setup(byte[]? Bios, bool FastBoot = false)
{
    /// <summary>The message for a disc opened without a BIOS.</summary>
    public const string NeedsBiosMessage =
        "This is a PlayStation 2 disc. To play it, choose your own PlayStation 2 BIOS in Settings → PlayStation 2. GameVault doesn't include it.";

    /// <summary>Reads the BIOS chosen in <paramref name="settings"/> and checks its size (4 MB).</summary>
    /// <exception cref="GameLoadException">The file can't be read, or isn't a 4 MB BIOS.</exception>
    public static async Task<PlayStation2Setup> LoadAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.PlayStation2BiosPath))
        {
            return new PlayStation2Setup(null, settings.PlayStation2FastBoot);
        }

        byte[] bios;
        try
        {
            bios = await File.ReadAllBytesAsync(settings.PlayStation2BiosPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GameLoadException("GameVault couldn't open your PlayStation 2 BIOS. Check the file chosen in Settings → PlayStation 2.", ex);
        }

        if (bios.Length != Ps2Bios.Size)
        {
            throw new GameLoadException(
                "The PlayStation 2 BIOS chosen in Settings → PlayStation 2 isn't a 4 MB BIOS file. Choose the BIOS dumped from your own console.");
        }

        return new PlayStation2Setup(bios, settings.PlayStation2FastBoot);
    }
}
