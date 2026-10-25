using GameVault.Platform.Storage;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Emulation;

/// <summary>Save-state slots for the running game, stored by <see cref="SaveStateStore"/>.</summary>
public sealed class SaveStateService : ISaveStateService
{
    private readonly EmulatorHost _host;
    private readonly SaveStateStore _store;

    public SaveStateService(EmulatorHost host, SaveStateStore store)
    {
        _host = host;
        _store = store;
        _host.QuickSaveRequested += async (_, _) => await SaveAsync(SelectedSlot);
        _host.QuickLoadRequested += async (_, _) => await LoadAsync(SelectedSlot);
    }

    public int SelectedSlot { get; set; }

    public event EventHandler<string>? Notice;

    public Task<IReadOnlyList<SaveSlotInfo>> GetSlotsAsync()
    {
        IReadOnlyList<SaveSlotInfo> slots = _host.RomHash is { } hash
            ? Enumerable.Range(0, ISaveStateService.SlotCount)
                .Select(slot => _store.SavedAt(hash, slot) is { } at
                    ? new SaveSlotInfo(slot, at, _store.ThumbnailPath(hash, slot))
                    : new SaveSlotInfo(slot, null, null))
                .ToList()
            : [];
        return Task.FromResult(slots);
    }

    public async Task SaveAsync(int slot)
    {
        var capture = await _host.CaptureAsync();
        if (capture is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => _store.Save(capture.RomHash, slot, capture.State, capture.ThumbnailPng));
            Notice?.Invoke(this, $"Saved to slot {slot + 1}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice?.Invoke(this, "GameVault couldn't save the game state. " + ex.Message);
        }
    }

    public async Task<bool> LoadAsync(int slot)
    {
        if (_host.RomHash is not { } hash)
        {
            return false;
        }

        byte[]? state;
        try
        {
            state = await Task.Run(() => _store.Load(hash, slot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice?.Invoke(this, "GameVault couldn't read that save state. " + ex.Message);
            return false;
        }

        if (state is null)
        {
            Notice?.Invoke(this, $"Slot {slot + 1} is empty");
            return false;
        }

        var loaded = await _host.RestoreAsync(state);
        Notice?.Invoke(this, loaded ? $"Loaded slot {slot + 1}" : $"Slot {slot + 1} doesn't work with this version of the game");
        return loaded;
    }
}
