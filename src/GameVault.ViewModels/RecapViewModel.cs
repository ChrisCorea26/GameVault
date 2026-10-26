using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>
/// "Previously on…" (SPEC §7.9): when a game is opened after a break of a day or more, a card recalls the last
/// session: how long ago and for how long, the newest screenshot and explored map, the newest journal note
/// and password, and whether there is a resume point.
/// </summary>
public sealed partial class RecapViewModel(
    ISessionLog sessions,
    IJournalService journal,
    IPasswordService passwords,
    ITimelineService timeline) : ObservableObject
{
    /// <summary>How long a break has to be before the recap appears.</summary>
    public static readonly TimeSpan Break = TimeSpan.FromDays(1);

    [ObservableProperty]
    public partial bool IsVisible { get; private set; }

    [ObservableProperty]
    public partial string Heading { get; private set; } = string.Empty;

    /// <summary>"Last played 3 days ago, for 42 minutes."</summary>
    [ObservableProperty]
    public partial string LastSession { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScreenshot))]
    public partial Uri? ScreenshotUri { get; private set; }

    public bool HasScreenshot => ScreenshotUri is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMap))]
    public partial Uri? MapUri { get; private set; }

    public bool HasMap => MapUri is not null;

    /// <summary>"Your newest note: Where the key is (Monday)".</summary>
    [ObservableProperty]
    public partial string? Note { get; private set; }

    [ObservableProperty]
    public partial string? Password { get; private set; }

    [ObservableProperty]
    public partial bool HasResumePoint { get; private set; }

    public async Task ShowAsync(string gameKey, string title, DateTimeOffset now)
    {
        IsVisible = false;
        if (await sessions.GetLastAsync(gameKey) is not { } last || now - last.Start < Break)
        {
            return;
        }

        Heading = $"Previously on {title}…";
        LastSession = $"Last played {Ago(now - last.Start)}, for {Length(last.Played)}.";

        var images = await journal.GetImagesAsync(gameKey);
        ScreenshotUri = images.Where(i => !i.IsMap).MaxBy(i => i.Created) is { } shot ? new Uri(shot.Path) : null;
        MapUri = images.Where(i => i.IsMap).MaxBy(i => i.Created) is { } map ? new Uri(map.Path) : null;
        Note = (await journal.GetNotesAsync(gameKey)).MaxBy(n => n.Updated) is { } note
            ? $"Your newest note: {note.Title} ({note.Updated.LocalDateTime.ToString("d", CultureInfo.CurrentCulture)})"
            : null;
        Password = (await passwords.GetAsync(gameKey)).MaxBy(p => p.SavedAt)?.Password;
        HasResumePoint = await timeline.GetResumePointAsync(gameKey) is not null;
        IsVisible = true;
    }

    [RelayCommand]
    public void Dismiss() => IsVisible = false;

    public static string Ago(TimeSpan span) => span.TotalDays switch
    {
        < 2 => "yesterday",
        < 14 => $"{(int)span.TotalDays} days ago",
        < 60 => $"{(int)(span.TotalDays / 7)} weeks ago",
        < 730 => $"{(int)(span.TotalDays / 30)} months ago",
        _ => $"{(int)(span.TotalDays / 365)} years ago",
    };

    public static string Length(TimeSpan span) => span.TotalMinutes switch
    {
        < 1 => "less than a minute",
        < 2 => "a minute",
        < 60 => $"{(int)span.TotalMinutes} minutes",
        < 120 => $"an hour and {(int)span.TotalMinutes - 60} minutes",
        _ => $"{(int)span.TotalHours} hours and {span.Minutes} minutes",
    };
}
