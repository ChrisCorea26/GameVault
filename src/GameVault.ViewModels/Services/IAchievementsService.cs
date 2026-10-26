namespace GameVault.ViewModels.Services;

/// <summary>RetroAchievements (SPEC §7.10): sign-in, hardcore mode, the running game's achievements and events.</summary>
public interface IAchievementsService
{
    /// <summary>False when the rcheevos component is missing from this install.</summary>
    bool IsAvailable { get; }

    AchievementsUser? User { get; }

    bool IsSignedIn => User is not null;

    /// <summary>The player's hardcore choice (kept while signed out).</summary>
    bool Hardcore { get; }

    /// <summary>Turning hardcore on restarts the running game, as RetroAchievements requires.</summary>
    Task SetHardcoreAsync(bool enabled);

    /// <summary>Signs in; the password is only sent to RetroAchievements, never stored.</summary>
    Task SignInAsync(string userName, string password);

    void SignOut();

    /// <summary>The running game, when RetroAchievements recognised it.</summary>
    AchievementsGame? Game { get; }

    IReadOnlyList<AchievementGroup> GetAchievements();

    /// <summary>"World 1-2, 3 lives", when the game has rich presence.</summary>
    string? RichPresence { get; }

    /// <summary>Raised on the UI thread.</summary>
    event EventHandler<AchievementsEvent>? Event;

    /// <summary>Sign-in, hardcore or the game changed. Raised on the UI thread.</summary>
    event EventHandler? Changed;
}

/// <summary>One achievement as RetroAchievements describes it (SPEC §7.10).</summary>
public sealed record AchievementInfo(
    uint Id,
    string Title,
    string Description,
    uint Points,
    string? BadgeUrl,
    bool Unlocked,
    string MeasuredProgress = "",
    float MeasuredPercent = 0);

/// <summary>A group in the achievement list ("Locked", "Unlocked", "Almost there"…), as rcheevos groups them.</summary>
public sealed record AchievementGroup(string Label, IReadOnlyList<AchievementInfo> Achievements);

public sealed record LeaderboardInfo(uint Id, string Title, string Description);

public enum AchievementsEventKind
{
    AchievementUnlocked,
    LeaderboardStarted,
    LeaderboardFailed,
    LeaderboardSubmitted,
    ChallengeIndicatorShow,
    ChallengeIndicatorHide,
    ProgressIndicatorShow,
    ProgressIndicatorHide,
    ProgressIndicatorUpdate,
    TrackerShow,
    TrackerHide,
    TrackerUpdate,
    Scoreboard,

    /// <summary>Hardcore was turned on: the game must restart.</summary>
    ResetRequested,
    GameCompleted,
    ServerError,
    Disconnected,
    Reconnected,
    SubsetCompleted,
}

/// <summary>Something rcheevos reported while processing a frame or a server answer.</summary>
public sealed record AchievementsEvent(
    AchievementsEventKind Kind,
    AchievementInfo? Achievement = null,
    LeaderboardInfo? Leaderboard = null,
    uint TrackerId = 0,
    string? TrackerText = null,
    string? Message = null);

/// <summary>The signed-in RetroAchievements player.</summary>
public sealed record AchievementsUser(string UserName, string DisplayName, uint Score, uint SoftcoreScore, string? AvatarUrl);

/// <summary>The game RetroAchievements recognised.</summary>
public sealed record AchievementsGame(uint Id, string Title, string? BadgeUrl, int AchievementCount, int UnlockedCount);

/// <summary>A RetroAchievements action failed. <see cref="Exception.Message"/> is safe to show.</summary>
public sealed class AchievementsException(string message, int code = 0) : Exception(message)
{
    /// <summary>rcheevos' result code (for example -34, invalid credentials).</summary>
    public int Code { get; } = code;
}
