namespace GameVault.Platform.Storage;

/// <summary>
/// PlayStation 2 memory cards (Phase 17): one 8 MB card per game in slot 1, saved next to the other saves in
/// <see cref="AppPaths.SavesDirectory"/> as a raw <c>.ps2</c> image ("Title - hash.ps2"), the format other emulators
/// use: 16,384 pages of 512 bytes plus 16 bytes of ECC each. A new card comes from the core, formatted
/// (<c>Ps2MemoryCard.CreateFormatted()</c>); this store only reads and writes the bytes.
/// </summary>
public sealed class Ps2MemoryCardStore(AppPaths paths)
{
    /// <summary>The size of a raw 8 MB card image: 16,384 × (512 + 16) bytes.</summary>
    public const int Size = 16_384 * (512 + 16);

    public const string Extension = ".ps2";

    private readonly BatterySaveStore _files = new(paths, Extension);

    /// <summary>Where the game's card is (or will be) saved.</summary>
    public string PathFor(string title, ulong romHash) => _files.PathFor(title, romHash);

    /// <summary>The game's card, or null when it has none yet (the caller then creates a formatted one).</summary>
    /// <exception cref="InvalidDataException">The file isn't an 8 MB card image; it's left alone.</exception>
    /// <exception cref="IOException">The file can't be read.</exception>
    public byte[]? TryLoad(ulong romHash)
    {
        var data = _files.TryRead(romHash);
        if (data is not null && data.Length != Size)
        {
            throw new InvalidDataException($"This game's memory card file isn't an 8 MB PlayStation 2 card ({data.Length:N0} bytes, not {Size:N0}).");
        }

        return data;
    }

    /// <summary>Writes the card atomically (a temporary file, then a replace).</summary>
    public void Save(string title, ulong romHash, ReadOnlySpan<byte> card)
    {
        if (card.Length != Size)
        {
            throw new ArgumentException($"A PlayStation 2 card image is {Size:N0} bytes.", nameof(card));
        }

        _files.Save(title, romHash, card);
    }
}
