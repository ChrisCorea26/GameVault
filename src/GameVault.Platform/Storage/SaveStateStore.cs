namespace GameVault.Platform.Storage;

/// <summary>
/// Save-state slots on disk: <c>States\&lt;rom hash&gt;\slot-N.state</c> plus a <c>slot-N.png</c> thumbnail.
/// </summary>
public sealed class SaveStateStore(AppPaths paths)
{
    public const int SlotCount = 10;

    public string StatePath(ulong romHash, int slot) => Path.Combine(Folder(romHash), $"slot-{slot}.state");

    public string ThumbnailPath(ulong romHash, int slot) => Path.Combine(Folder(romHash), $"slot-{slot}.png");

    public void Save(ulong romHash, int slot, ReadOnlySpan<byte> state, ReadOnlySpan<byte> thumbnailPng)
    {
        Check(slot);
        BatterySaveStore.WriteAtomically(ThumbnailPath(romHash, slot), thumbnailPng);
        BatterySaveStore.WriteAtomically(StatePath(romHash, slot), state);
    }

    public byte[]? Load(ulong romHash, int slot)
    {
        Check(slot);
        var path = StatePath(romHash, slot);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>When each slot was saved (null when empty).</summary>
    public DateTimeOffset? SavedAt(ulong romHash, int slot)
    {
        var path = StatePath(romHash, slot);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    private string Folder(ulong romHash) => Path.Combine(paths.StatesDirectory, romHash.ToString("x16"));

    private static void Check(int slot) => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)slot, (uint)SlotCount);
}
