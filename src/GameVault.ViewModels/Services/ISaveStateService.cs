namespace GameVault.ViewModels.Services;

/// <summary>One save-state slot. <see cref="ThumbnailPath"/> is a PNG of the screen when it was saved.</summary>
public sealed record SaveSlotInfo(int Slot, DateTimeOffset? SavedAt, string? ThumbnailPath)
{
    public bool IsEmpty => SavedAt is null;
}

/// <summary>
/// Save states for the running game (SPEC §4.6). F5 / F8 save to and load from <see cref="SelectedSlot"/>
/// (docs/design/input.md §8). Results are reported through <see cref="Notice"/> on the UI thread.
/// </summary>
public interface ISaveStateService
{
    const int SlotCount = 10;

    int SelectedSlot { get; set; }

    /// <summary>A short message after a save or load ("Saved to slot 3").</summary>
    event EventHandler<string>? Notice;

    Task<IReadOnlyList<SaveSlotInfo>> GetSlotsAsync();

    Task SaveAsync(int slot);

    /// <summary>Returns false when the slot is empty or no game is running.</summary>
    Task<bool> LoadAsync(int slot);
}
