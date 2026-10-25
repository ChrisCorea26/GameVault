using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>Timeline under <c>--sample-data</c>: a few states for "Crystal Caverns" (key "sample:2"), no files.</summary>
public sealed class SampleTimelineService : ITimelineService
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);
    private readonly List<(string Key, TimelineItem Item, bool Deleted)> _items =
    [
        ("sample:2", new TimelineItem("a", Now.AddDays(-6), TimeSpan.FromHours(1.5), "World 1 start", true, TimelineItemKind.Manual, null), false),
        ("sample:2", new TimelineItem("b", Now.AddDays(-2), TimeSpan.FromHours(4.1), null, false, TimelineItemKind.Quick, null), false),
        ("sample:2", new TimelineItem("c", Now.AddHours(-20), TimeSpan.FromHours(5.6), "Before the ice boss", false, TimelineItemKind.Manual, null), false),
        ("sample:2", new TimelineItem("d", Now.AddHours(-1), TimeSpan.FromHours(6.2), null, false, TimelineItemKind.AutoExit, null), false),
    ];

    private int _next;

    public event EventHandler<string>? Notice;

    public event EventHandler<string>? Changed;

    public Task<IReadOnlyList<TimelineItem>> GetAsync(string gameKey) =>
        Task.FromResult<IReadOnlyList<TimelineItem>>(
            _items.Where(i => i.Key == gameKey && !i.Deleted).Select(i => i.Item).OrderBy(i => i.SavedAt).ToList());

    public Task SaveAsync(string? label = null) => Add(label, TimelineItemKind.Manual, "Saved");

    public Task SaveOnExitAsync()
    {
        _items.RemoveAll(i => i.Key == "sample:2" && i.Item.Kind == TimelineItemKind.AutoExit);
        return Add(null, TimelineItemKind.AutoExit, null);
    }

    public Task<bool> LoadAsync(string gameKey, string id)
    {
        Notice?.Invoke(this, "Loaded");
        return Task.FromResult(_items.Any(i => i.Key == gameKey && i.Item.Id == id && !i.Deleted));
    }

    public Task RenameAsync(string gameKey, string id, string? label) =>
        Update(gameKey, id, i => i with { Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim() });

    public Task SetPinnedAsync(string gameKey, string id, bool pinned) => Update(gameKey, id, i => i with { Pinned = pinned });

    public Task DeleteAsync(string gameKey, string id) => SetDeleted(gameKey, id, true);

    public Task UndoDeleteAsync(string gameKey, string id) => SetDeleted(gameKey, id, false);

    public Task<TimelineItem?> GetResumePointAsync(string gameKey) =>
        Task.FromResult(_items.Where(i => i.Key == gameKey && !i.Deleted && i.Item.Kind == TimelineItemKind.AutoExit)
            .Select(i => (TimelineItem?)i.Item).LastOrDefault());

    private Task Add(string? label, TimelineItemKind kind, string? notice)
    {
        var item = new TimelineItem($"new{_next++}", Now.AddMinutes(_next), TimeSpan.FromHours(6.3), label, false, kind, null);
        _items.Add(("sample:2", item, false));
        if (notice is not null)
        {
            Notice?.Invoke(this, notice);
        }

        Changed?.Invoke(this, "sample:2");
        return Task.CompletedTask;
    }

    private Task Update(string gameKey, string id, Func<TimelineItem, TimelineItem> change)
    {
        var index = _items.FindIndex(i => i.Key == gameKey && i.Item.Id == id);
        if (index >= 0)
        {
            _items[index] = _items[index] with { Item = change(_items[index].Item) };
            Changed?.Invoke(this, gameKey);
        }

        return Task.CompletedTask;
    }

    private Task SetDeleted(string gameKey, string id, bool deleted)
    {
        var index = _items.FindIndex(i => i.Key == gameKey && i.Item.Id == id);
        if (index >= 0)
        {
            _items[index] = _items[index] with { Deleted = deleted };
            Changed?.Invoke(this, gameKey);
        }

        return Task.CompletedTask;
    }
}
