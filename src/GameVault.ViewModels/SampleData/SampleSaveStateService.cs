using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>Save states under <c>--sample-data</c>: three filled slots (no thumbnails on disk) and the rest empty.</summary>
public sealed class SampleSaveStateService : ISaveStateService
{
    private readonly DateTimeOffset?[] _saved = new DateTimeOffset?[ISaveStateService.SlotCount];

    public SampleSaveStateService()
    {
        var now = new DateTimeOffset(2026, 10, 24, 20, 15, 0, TimeSpan.Zero);
        _saved[0] = now.AddMinutes(-3);
        _saved[1] = now.AddHours(-2);
        _saved[4] = now.AddDays(-1);
    }

    public int SelectedSlot { get; set; }

    public event EventHandler<string>? Notice;

    public Task<IReadOnlyList<SaveSlotInfo>> GetSlotsAsync() =>
        Task.FromResult<IReadOnlyList<SaveSlotInfo>>(
            _saved.Select((at, slot) => new SaveSlotInfo(slot, at, null)).ToList());

    public Task SaveAsync(int slot)
    {
        _saved[slot] = DateTimeOffset.Now;
        Notice?.Invoke(this, $"Saved to slot {slot + 1}");
        return Task.CompletedTask;
    }

    public Task<bool> LoadAsync(int slot)
    {
        var filled = _saved[slot] is not null;
        Notice?.Invoke(this, filled ? $"Loaded slot {slot + 1}" : $"Slot {slot + 1} is empty");
        return Task.FromResult(filled);
    }
}
