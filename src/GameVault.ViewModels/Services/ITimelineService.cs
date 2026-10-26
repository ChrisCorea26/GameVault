namespace GameVault.ViewModels.Services;

public enum TimelineItemKind
{
    Manual,
    Quick,
    AutoExit,

    /// <summary>Saved automatically a few seconds before a life was lost (SPEC §7.9).</summary>
    BeforeDeath,
}

/// <summary>One save state on a game's timeline (SPEC §7.1).</summary>
public sealed record TimelineItem(
    string Id,
    DateTimeOffset SavedAt,
    TimeSpan Playtime,
    string? Label,
    bool Pinned,
    TimelineItemKind Kind,
    string? ThumbnailPath);

/// <summary>
/// Unlimited save states per game, shown as a timeline (SPEC §7.1). F5 adds a state, F8 loads the newest
/// (docs/design/input.md §8), and closing a game saves an automatic "resume" point. Events are raised on the UI
/// thread.
/// </summary>
public interface ITimelineService
{
    /// <summary>A short message after a save or load ("Saved", "Loaded").</summary>
    event EventHandler<string>? Notice;

    /// <summary>A game's timeline changed; the argument is its key.</summary>
    event EventHandler<string>? Changed;

    Task<IReadOnlyList<TimelineItem>> GetAsync(string gameKey);

    /// <summary>Saves the running game.</summary>
    Task SaveAsync(string? label = null);

    /// <summary>Saves the running game as its "resume where you left off" point. Call before stopping it.</summary>
    Task SaveOnExitAsync();

    /// <summary>Loads a state into the running game. False when it's a different game or the state is gone.</summary>
    Task<bool> LoadAsync(string gameKey, string id);

    Task RenameAsync(string gameKey, string id, string? label);

    Task SetPinnedAsync(string gameKey, string id, bool pinned);

    /// <summary>Deletes a state; <see cref="UndoDeleteAsync"/> brings it back until the app restarts.</summary>
    Task DeleteAsync(string gameKey, string id);

    Task UndoDeleteAsync(string gameKey, string id);

    /// <summary>The automatic exit save, if the game has one.</summary>
    Task<TimelineItem?> GetResumePointAsync(string gameKey);
}
