using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>
/// RetroAchievements under <c>--sample-data</c> and in tests: signed in as "SamplePlayer" with a few of the sample
/// game's achievements unlocked. <see cref="Raise"/> sends events as rcheevos would.
/// </summary>
public sealed class SampleAchievementsService : IAchievementsService
{
    private readonly List<AchievementInfo> _achievements =
    [
        new(1, "First steps", "Leave the starting cave", 1, null, true),
        new(2, "Crystal collector", "Collect 10 crystals", 5, null, true),
        new(3, "Bat wrangler", "Defeat 25 bats", 10, null, false, "18/25", 72),
        new(4, "No light needed", "Finish the dark cavern without the lantern", 25, null, false),
        new(5, "Cave master", "Beat the final guardian", 50, null, false),
    ];

    private readonly TimeSpan? _previewUnlockDelay;
    private EventHandler<AchievementsEvent>? _event;
    private bool _previewScheduled;

    /// <param name="signedIn">Start signed in as "SamplePlayer".</param>
    /// <param name="previewUnlockDelay">
    /// For <c>--sample-data</c>: this long after the Player first listens, show <see cref="SampleUnlock"/>, so the
    /// notification card can be previewed. Null in tests.
    /// </param>
    public SampleAchievementsService(bool signedIn = true, TimeSpan? previewUnlockDelay = null)
    {
        User = signedIn ? new AchievementsUser("SamplePlayer", "SamplePlayer", 1234, 56, null) : null;
        _previewUnlockDelay = previewUnlockDelay;
    }

    public bool IsAvailable => true;

    public AchievementsUser? User { get; private set; }

    public bool Hardcore { get; private set; } = true;

    /// <summary>The sample game, once a game is running (tests set it; previews keep it on).</summary>
    public bool GameRunning { get; set; } = true;

    public AchievementsGame? Game => User is null || !GameRunning
        ? null
        : new AchievementsGame(1, "Crystal Caverns", null, _achievements.Count, _achievements.Count(a => a.Unlocked));

    public string? RichPresence => Game is null ? null : "Exploring the Crystal Caverns, 3 lives";

    /// <summary>The password last used to sign in; "wrong" is refused.</summary>
    public string? LastPassword { get; private set; }

    public event EventHandler<AchievementsEvent>? Event
    {
        add
        {
            _event += value;
            if (_previewUnlockDelay is { } delay && !_previewScheduled)
            {
                _previewScheduled = true;
                _ = PreviewUnlockAsync(delay, SynchronizationContext.Current);
            }
        }

        remove => _event -= value;
    }

    public event EventHandler? Changed;

    public Task SetHardcoreAsync(bool enabled)
    {
        Hardcore = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SignInAsync(string userName, string password)
    {
        LastPassword = password;
        if (password == "wrong")
        {
            return Task.FromException(new AchievementsException("RetroAchievements didn't accept that username and password.", -34));
        }

        User = new AchievementsUser(userName.Trim(), userName.Trim(), 1234, 56, null);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void SignOut()
    {
        User = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<AchievementGroup> GetAchievements() => Game is null
        ? []
        :
        [
            new AchievementGroup("Almost there", _achievements.Where(a => !a.Unlocked && a.MeasuredPercent > 0).ToList()),
            new AchievementGroup("Locked", _achievements.Where(a => !a.Unlocked && a.MeasuredPercent == 0).ToList()),
            new AchievementGroup("Unlocked", _achievements.Where(a => a.Unlocked).ToList()),
        ];

    /// <summary>Sends an event as rcheevos would; an unlock also marks the achievement unlocked.</summary>
    public void Raise(AchievementsEvent e)
    {
        if (e is { Kind: AchievementsEventKind.AchievementUnlocked, Achievement: { } a })
        {
            var index = _achievements.FindIndex(x => x.Id == a.Id);
            if (index >= 0)
            {
                _achievements[index] = _achievements[index] with { Unlocked = true };
            }
        }

        _event?.Invoke(this, e);
    }

    private async Task PreviewUnlockAsync(TimeSpan delay, SynchronizationContext? context)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        if (context is null)
        {
            Raise(SampleUnlock);
        }
        else
        {
            context.Post(_ => Raise(SampleUnlock), null);
        }
    }

    /// <summary>The unlock the preview shows a few seconds into the sample game.</summary>
    public AchievementsEvent SampleUnlock => new(AchievementsEventKind.AchievementUnlocked, _achievements[2] with { Unlocked = true });
}
