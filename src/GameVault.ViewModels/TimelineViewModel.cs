using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>
/// A game's save-state timeline (SPEC §7.1), oldest to newest. Used by the Player's flyout (where it can
/// save and load into the running game) and by Game Details (where loading starts the game first).
/// </summary>
public sealed partial class TimelineViewModel(ITimelineService service) : ObservableObject
{
    private bool _attached;

    public ObservableCollection<TimelineItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string? GameKey { get; private set; }

    /// <summary>True in the Player, where the running game can be saved.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool CanSave { get; set; }

    /// <summary>The state just deleted, for "Undo".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUndo), nameof(UndoText))]
    public partial TimelineItemViewModel? LastDeleted { get; private set; }

    public bool HasUndo => LastDeleted is not null;

    public string UndoText => LastDeleted is null ? string.Empty : $"Deleted \"{LastDeleted.Title}\"";

    public bool IsEmpty => Items.Count == 0;

    /// <summary>Raised when a state should be loaded and no game is running here (Game Details).</summary>
    public event EventHandler<TimelineItemViewModel>? LoadRequested;

    internal ITimelineService Service => service;

    public async Task ShowAsync(string gameKey)
    {
        GameKey = gameKey;
        if (!_attached)
        {
            service.Changed += OnChanged;
            _attached = true;
        }

        await RefreshAsync();
    }

    /// <summary>Stops listening for changes (when the page is left).</summary>
    public void Detach()
    {
        if (_attached)
        {
            service.Changed -= OnChanged;
            _attached = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (GameKey is null)
        {
            return;
        }

        var items = await service.GetAsync(GameKey);
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(new TimelineItemViewModel(this, item));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => service.SaveAsync();

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (LastDeleted is { } item && GameKey is { } key)
        {
            LastDeleted = null;
            await service.UndoDeleteAsync(key, item.Id);
        }
    }

    internal async Task LoadAsync(TimelineItemViewModel item)
    {
        if (CanSave && GameKey is { } key)
        {
            await service.LoadAsync(key, item.Id);
        }
        else
        {
            LoadRequested?.Invoke(this, item);
        }
    }

    internal Task RenameAsync(TimelineItemViewModel item, string? label) =>
        GameKey is { } key ? service.RenameAsync(key, item.Id, label) : Task.CompletedTask;

    internal Task SetPinnedAsync(TimelineItemViewModel item, bool pinned) =>
        GameKey is { } key ? service.SetPinnedAsync(key, item.Id, pinned) : Task.CompletedTask;

    internal async Task DeleteAsync(TimelineItemViewModel item)
    {
        if (GameKey is { } key)
        {
            await service.DeleteAsync(key, item.Id);
            LastDeleted = item;
        }
    }

    private async void OnChanged(object? sender, string gameKey)
    {
        if (gameKey == GameKey)
        {
            await RefreshAsync();
        }
    }
}

public sealed partial class TimelineItemViewModel(TimelineViewModel owner, TimelineItem item) : ObservableObject
{
    public string Id => item.Id;

    public TimelineItem Item => item;

    public string Title => item.Label ?? item.Kind switch
    {
        TimelineItemKind.AutoExit => "Where you left off",
        TimelineItemKind.Quick => "Quick save",
        _ => "Saved game",
    };

    public string Subtitle
    {
        get
        {
            var when = item.SavedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            return item.Playtime >= TimeSpan.FromMinutes(1) ? $"{when} · {FormatPlaytime(item.Playtime)} played" : when;
        }
    }

    public Uri? ThumbnailUri => item.ThumbnailPath is { } path ? new Uri(path) : null;

    public bool HasThumbnail => item.ThumbnailPath is not null;

    public bool IsPinned => item.Pinned;

    public string PinLabel => item.Pinned ? "Unpin" : "Pin";

    /// <summary>Edited in place by the Rename flyout; committed by <see cref="RenameCommand"/>.</summary>
    [ObservableProperty]
    public partial string EditLabel { get; set; } = item.Label ?? string.Empty;

    [RelayCommand]
    private Task LoadAsync() => owner.LoadAsync(this);

    [RelayCommand]
    private Task RenameAsync() => owner.RenameAsync(this, EditLabel);

    [RelayCommand]
    private Task TogglePinAsync() => owner.SetPinnedAsync(this, !item.Pinned);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteAsync(this);

    internal static string FormatPlaytime(TimeSpan time) => time.TotalHours >= 1
        ? $"{time.TotalHours:0.#} h"
        : $"{(int)time.TotalMinutes} min";
}
