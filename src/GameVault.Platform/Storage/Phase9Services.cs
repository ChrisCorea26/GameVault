using GameVault.Library.Sessions;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Storage;

/// <summary>Play sessions on disk, for the "Previously on…" recap (SPEC §7.9).</summary>
public sealed class SessionLog(SessionStore store) : ISessionLog
{
    public Task<PlaySession?> GetLastAsync(string gameKey) =>
        Task.Run(() => store.Last(gameKey) is { } s ? new PlaySession(s.Start, s.Played) : null);

    public Task RecordAsync(string gameKey, PlaySession session) =>
        Task.Run(() =>
        {
            try
            {
                store.Add(gameKey, new SessionRecord(session.Start, session.Played));
            }
            catch (IOException)
            {
                // Missing one session in the recap's history isn't worth interrupting the player for.
            }
        });
}
