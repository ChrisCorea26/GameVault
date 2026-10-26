namespace GameVault.ViewModels.Services;

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
