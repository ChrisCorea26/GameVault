using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>
/// Sessions for <c>--sample-data</c> and tests. Every game was last played three days ago for 42 minutes
/// until a session is recorded, so the recap shows.
/// </summary>
public sealed class InMemorySessionLog : ISessionLog
{
    private readonly Dictionary<string, PlaySession> _last = [];

    public bool SampleHistory { get; init; } = true;

    public Task<PlaySession?> GetLastAsync(string gameKey) => Task.FromResult(
        _last.TryGetValue(gameKey, out var session)
            ? session
            : SampleHistory ? new PlaySession(DateTimeOffset.Now.AddDays(-3), TimeSpan.FromMinutes(42)) : null);

    public Task RecordAsync(string gameKey, PlaySession session)
    {
        _last[gameKey] = session;
        return Task.CompletedTask;
    }
}
