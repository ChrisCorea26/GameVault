using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>A running leaderboard's value on screen ("01:23.45", "004200").</summary>
public sealed partial class TrackerItem(uint id, string text) : ObservableObject
{
    public uint Id { get; } = id;

    [ObservableProperty]
    public partial string Text { get; set; } = text;
}

/// <summary>
/// RetroAchievements in the Player (SPEC §7.10): unlock and leaderboard notifications, on-screen trackers and
/// challenge badges, and the achievement list.
/// </summary>
public sealed partial class AchievementsViewModel(IAchievementsService service, TimeProvider? clock = null) : ObservableObject
{
    /// <summary>How long a notification stays up.</summary>
    public static readonly TimeSpan NotificationTime = TimeSpan.FromSeconds(5);

    private readonly Queue<(string Heading, string Title, string Text, string? Badge)> _pending = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private bool _attached;
    private bool _showing;

    public bool IsAvailable => service.IsAvailable;

    /// <summary>The Achievements button shows while a recognised game with achievements is running.</summary>
    [ObservableProperty]
    public partial bool HasGame { get; private set; }

    /// <summary>"3 of 12 achievements · Hardcore".</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>What the game says the player is doing ("World 1-2, 3 lives").</summary>
    [ObservableProperty]
    public partial string? RichPresence { get; private set; }

    public ObservableCollection<AchievementGroup> Groups { get; } = [];

    public ObservableCollection<TrackerItem> Trackers { get; } = [];

    /// <summary>Achievements whose challenge is running ("don't get hit"): small badges on screen.</summary>
    public ObservableCollection<AchievementInfo> Challenges { get; } = [];

    // ---------------------------------------------------------------- the notification card

    [ObservableProperty]
    public partial bool HasNotification { get; private set; }

    /// <summary>"Achievement unlocked", "Leaderboard submitted"…</summary>
    [ObservableProperty]
    public partial string NotificationHeading { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string NotificationTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string NotificationText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotificationBadgeUri), nameof(HasNotificationBadge))]
    public partial string? NotificationBadge { get; private set; }

    public Uri? NotificationBadgeUri => Uri.TryCreate(NotificationBadge, UriKind.Absolute, out var uri) ? uri : null;

    public bool HasNotificationBadge => NotificationBadgeUri is not null;

    public void Attach()
    {
        if (!_attached)
        {
            service.Event += OnEvent;
            service.Changed += OnChanged;
            _attached = true;
        }

        RefreshSummary();
    }

    public void Detach()
    {
        if (_attached)
        {
            service.Event -= OnEvent;
            service.Changed -= OnChanged;
            _attached = false;
        }

        Trackers.Clear();
        Challenges.Clear();
        _pending.Clear();
        HasNotification = false;
    }

    /// <summary>Called when the list opens: the latest unlocks and progress.</summary>
    [RelayCommand]
    private void Refresh()
    {
        Groups.Clear();
        foreach (var group in service.GetAchievements())
        {
            Groups.Add(group);
        }

        RichPresence = service.RichPresence;
        RefreshSummary();
    }

    [RelayCommand]
    private void DismissNotification() => HasNotification = false;

    private void OnChanged(object? sender, EventArgs e)
    {
        RefreshSummary();
        if (!HasGame)
        {
            Trackers.Clear();
            Challenges.Clear();
        }
    }

    private void RefreshSummary()
    {
        var game = service.Game;
        HasGame = game is { AchievementCount: > 0 };
        Summary = game is null
            ? string.Empty
            : $"{game.UnlockedCount} of {game.AchievementCount} achievements" + (service.Hardcore ? " · Hardcore" : string.Empty);
    }

    private void OnEvent(object? sender, AchievementsEvent e)
    {
        switch (e.Kind)
        {
            case AchievementsEventKind.AchievementUnlocked when e.Achievement is { } a:
                Notify("Achievement unlocked", a.Title, $"{a.Description} ({a.Points} points)", a.BadgeUrl);
                RefreshSummary();
                break;
            case AchievementsEventKind.GameCompleted:
                Notify(service.Hardcore ? "Game mastered" : "Game completed", service.Game?.Title ?? string.Empty, "Every achievement earned. Well played!", service.Game?.BadgeUrl);
                break;
            case AchievementsEventKind.LeaderboardStarted when e.Leaderboard is { } lb:
                Notify("Leaderboard attempt started", lb.Title, lb.Description, null);
                break;
            case AchievementsEventKind.LeaderboardFailed when e.Leaderboard is { } lb:
                Notify("Leaderboard attempt failed", lb.Title, lb.Description, null);
                break;
            case AchievementsEventKind.LeaderboardSubmitted when e.Leaderboard is { } lb:
                Notify("Leaderboard submitted", lb.Title, lb.Description, null);
                break;
            case AchievementsEventKind.TrackerShow:
                Trackers.Add(new TrackerItem(e.TrackerId, e.TrackerText ?? string.Empty));
                break;
            case AchievementsEventKind.TrackerUpdate when Trackers.FirstOrDefault(t => t.Id == e.TrackerId) is { } tracker:
                tracker.Text = e.TrackerText ?? string.Empty;
                break;
            case AchievementsEventKind.TrackerHide when Trackers.FirstOrDefault(t => t.Id == e.TrackerId) is { } tracker:
                Trackers.Remove(tracker);
                break;
            case AchievementsEventKind.ChallengeIndicatorShow when e.Achievement is { } a && Challenges.All(c => c.Id != a.Id):
                Challenges.Add(a);
                break;
            case AchievementsEventKind.ChallengeIndicatorHide when e.Achievement is { } a && Challenges.FirstOrDefault(c => c.Id == a.Id) is { } shown:
                Challenges.Remove(shown);
                break;
            case AchievementsEventKind.ServerError when e.Message is { } message:
                Notify("RetroAchievements", "Something went wrong", message, null);
                break;
            case AchievementsEventKind.Disconnected:
                Notify("RetroAchievements", "Offline", "Unlocks are kept and sent when the connection is back.", null);
                break;
        }
    }

    private void Notify(string heading, string title, string text, string? badge)
    {
        _pending.Enqueue((heading, title, text, badge));
        if (!_showing)
        {
            _ = ShowNotificationsAsync();
        }
    }

    private async Task ShowNotificationsAsync()
    {
        _showing = true;
        try
        {
            while (_pending.TryDequeue(out var next))
            {
                NotificationHeading = next.Heading;
                NotificationTitle = next.Title;
                NotificationText = next.Text;
                NotificationBadge = next.Badge;
                HasNotification = true;
                await Task.Delay(NotificationTime, _clock);
            }
        }
        finally
        {
            HasNotification = false;
            _showing = false;
        }
    }
}
