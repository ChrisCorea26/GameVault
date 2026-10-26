using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>
/// The daily limit while playing (SPEC §7.9): a warning 5 minutes before, then at the limit the game is saved
/// and paused, and stays paused until a parent enters the PIN.
/// </summary>
public sealed partial class PlayLimitViewModel(IEmulatorService emulator, IFamilyService family, ITimelineService timeline) : ObservableObject
{
    /// <summary>The warning comes this long before the limit.</summary>
    public static readonly TimeSpan WarnBefore = TimeSpan.FromMinutes(5);

    private bool _attached;
    private bool _warned;

    /// <summary>Shown above the game when the limit is close.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string? Warning { get; private set; }

    public bool HasWarning => Warning is not null;

    [ObservableProperty]
    public partial bool IsLimitReached { get; private set; }

    public string LimitMessage => $"That's {FamilyViewModel.Minutes(family.Current.DailyLimitMinutes ?? 0)} of play for today, {family.Current.Name}. Your game is saved right where you left it.";

    [ObservableProperty]
    public partial string PinEntry { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? PinMessage { get; private set; }

    /// <summary>Minutes left today, or null with no limit (or after a parent lifted it).</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (family.Current.DailyLimitMinutes is not { } minutes)
            {
                return null;
            }

            var play = family.PlayToday();
            return play.LimitLifted ? null : TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(play.Seconds);
        }
    }

    public void Attach()
    {
        if (!_attached)
        {
            emulator.PlaytimeElapsed += OnPlaytimeElapsed;
            emulator.StatusChanged += OnStatusChanged;
            _attached = true;
        }

        Check();
    }

    public void Detach()
    {
        if (_attached)
        {
            emulator.PlaytimeElapsed -= OnPlaytimeElapsed;
            emulator.StatusChanged -= OnStatusChanged;
            _attached = false;
        }
    }

    [RelayCommand]
    private void Continue()
    {
        if (!family.VerifyPin(PinEntry))
        {
            PinMessage = "That PIN isn't right.";
            PinEntry = string.Empty;
            return;
        }

        family.LiftLimit();
        PinEntry = string.Empty;
        PinMessage = null;
        IsLimitReached = false;
        Warning = null;
        emulator.Resume();
    }

    private void OnPlaytimeElapsed(object? sender, TimeSpan played)
    {
        family.AddPlay(played);
        Check();
    }

    // Unpausing (Esc, the Resume button, a controller's Start) or starting another game can't get past the limit.
    private void OnStatusChanged(object? sender, EventArgs e)
    {
        if (IsLimitReached && emulator.Status == EmulatorStatus.Running)
        {
            emulator.Pause();
        }
    }

    private void Check()
    {
        if (Remaining is not { } left)
        {
            Warning = null;
            return;
        }

        if (left <= TimeSpan.Zero)
        {
            if (!IsLimitReached)
            {
                Warning = null;
                OnPropertyChanged(nameof(LimitMessage));
                IsLimitReached = true;
                if (emulator.Status is EmulatorStatus.Running or EmulatorStatus.Paused && emulator.GameKey is not null)
                {
                    _ = timeline.SaveOnExitAsync(); // the resume point, so tomorrow starts right here
                }

                if (emulator.Status == EmulatorStatus.Running)
                {
                    emulator.Pause();
                }
            }
        }
        else if (left <= WarnBefore && !_warned)
        {
            _warned = true;
            var minutes = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
            Warning = minutes == 1 ? "1 minute of play left today." : $"{minutes} minutes of play left today.";
        }
    }
}
