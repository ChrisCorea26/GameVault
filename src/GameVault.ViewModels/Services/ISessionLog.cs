namespace GameVault.ViewModels.Services;

/// <summary>A play session: when it started and how long the game ran.</summary>
public sealed record PlaySession(DateTimeOffset Start, TimeSpan Played);

/// <summary>Recent play sessions per game, for the "Previously on…" recap (SPEC §7.9).</summary>
public interface ISessionLog
{
    Task<PlaySession?> GetLastAsync(string gameKey);

    Task RecordAsync(string gameKey, PlaySession session);
}
