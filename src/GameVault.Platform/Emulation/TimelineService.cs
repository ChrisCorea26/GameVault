using GameVault.Library.Timeline;
using GameVault.Platform.Storage;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Emulation;

/// <summary>
/// The save-state timeline (SPEC §7.1) for <see cref="EmulatorHost"/>, stored by <see cref="TimelineStore"/>.
/// F5 adds a quick state and F8 loads the newest one. Phase 4's ten fixed slots are imported once, the
/// first time their game is opened.
/// </summary>
public sealed class TimelineService : ITimelineService
{
    private readonly EmulatorHost _host;
    private readonly TimelineStore _store;
    private readonly SaveStateStore? _legacySlots;
    private readonly Func<string, TimeSpan>? _playtime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public TimelineService(EmulatorHost host, TimelineStore store, SaveStateStore? legacySlots = null, Func<string, TimeSpan>? playtime = null)
    {
        _host = host;
        _store = store;
        _legacySlots = legacySlots;
        _playtime = playtime;
        _host.QuickSaveRequested += async (_, _) => await AddAsync(TimelineKind.Quick, null, "Saved");
        _host.QuickLoadRequested += async (_, _) => await LoadLatestAsync();
    }

    public event EventHandler<string>? Notice;

    public event EventHandler<string>? Changed;

    public Task<IReadOnlyList<TimelineItem>> GetAsync(string gameKey) => Locked(() =>
    {
        ImportLegacySlots(gameKey);
        return (IReadOnlyList<TimelineItem>)_store.List(gameKey).Select(e => ToItem(gameKey, e)).ToList();
    });

    public Task SaveAsync(string? label = null) => AddAsync(TimelineKind.Manual, label, "Saved");

    public Task SaveOnExitAsync() => AddAsync(TimelineKind.AutoExit, null, null);

    public async Task<bool> LoadAsync(string gameKey, string id)
    {
        if (_host.GameKey != gameKey)
        {
            return false;
        }

        var state = await Locked(() => _store.LoadState(gameKey, id));
        if (state is null)
        {
            Raise(() => Notice?.Invoke(this, "That save state is missing"));
            return false;
        }

        var loaded = await _host.RestoreAsync(state);
        Raise(() => Notice?.Invoke(this, loaded ? "Loaded" : "That save state doesn't work with this version of the game"));
        return loaded;
    }

    public Task RenameAsync(string gameKey, string id, string? label) => Change(gameKey, () => _store.Rename(gameKey, id, label));

    public Task SetPinnedAsync(string gameKey, string id, bool pinned) => Change(gameKey, () => _store.SetPinned(gameKey, id, pinned));

    public Task DeleteAsync(string gameKey, string id) => Change(gameKey, () => _store.Delete(gameKey, id));

    public Task UndoDeleteAsync(string gameKey, string id) => Change(gameKey, () => _store.Undelete(gameKey, id));

    public Task<TimelineItem?> GetResumePointAsync(string gameKey) => Locked(() =>
        _store.Latest(gameKey, TimelineKind.AutoExit) is { } entry ? ToItem(gameKey, entry) : null);

    private async Task LoadLatestAsync()
    {
        if (_host.GameKey is not { } key)
        {
            return;
        }

        var latest = await Locked(() => _store.Latest(key));
        if (latest is null)
        {
            Raise(() => Notice?.Invoke(this, "No save states yet. Press F5 to save."));
            return;
        }

        await LoadAsync(key, latest.Id);
    }

    private async Task AddAsync(TimelineKind kind, string? label, string? notice)
    {
        var key = _host.GameKey;
        var capture = await _host.CaptureAsync();
        if (key is null || capture is null)
        {
            return;
        }

        try
        {
            var playtime = _playtime?.Invoke(key) ?? TimeSpan.Zero;
            await Locked(() => _store.Add(key, capture.State, capture.ThumbnailPng, playtime, kind, DateTimeOffset.Now, label));
            Raise(() =>
            {
                if (notice is not null)
                {
                    Notice?.Invoke(this, notice);
                }

                Changed?.Invoke(this, key);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Raise(() => Notice?.Invoke(this, "GameVault couldn't save the game state. " + ex.Message));
        }
    }

    private async Task Change(string gameKey, Action change)
    {
        await Locked(change);
        Raise(() => Changed?.Invoke(this, gameKey));
    }

    private TimelineItem ToItem(string gameKey, TimelineEntry e) => new(
        e.Id,
        e.SavedAt,
        e.Playtime,
        e.Label,
        e.Pinned,
        (TimelineItemKind)e.Kind,
        _store.ThumbnailPath(gameKey, e.Id));

    /// <summary>Moves Phase 4 slot files (stored by ROM CRC-style hash) onto this game's timeline.</summary>
    private void ImportLegacySlots(string gameKey)
    {
        if (_legacySlots is null || _host.GameKey != gameKey || _host.RomHash is not { } romHash)
        {
            return;
        }

        for (var slot = 0; slot < SaveStateStore.SlotCount; slot++)
        {
            if (_legacySlots.Load(romHash, slot) is not { } state)
            {
                continue;
            }

            var thumbnail = File.Exists(_legacySlots.ThumbnailPath(romHash, slot)) ? File.ReadAllBytes(_legacySlots.ThumbnailPath(romHash, slot)) : [];
            var savedAt = _legacySlots.SavedAt(romHash, slot) ?? DateTimeOffset.Now;
            _store.Add(gameKey, state, thumbnail, TimeSpan.Zero, TimelineKind.Manual, savedAt, $"Slot {slot + 1}");
            File.Delete(_legacySlots.StatePath(romHash, slot));
            File.Delete(_legacySlots.ThumbnailPath(romHash, slot));
        }
    }

    private async Task<T> Locked<T>(Func<T> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(work).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task Locked(Action work) => Locked(() =>
    {
        work();
        return true;
    });

    private void Raise(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}
