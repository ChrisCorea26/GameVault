using GameVault.Library.Models;

namespace GameVault.ViewModels.Services;

/// <summary>The library service for a fresh install until the catalog and scanner arrive in Phase 5.</summary>
public sealed class EmptyLibraryService : ILibraryService
{
    public Task<IReadOnlyList<GameEntry>> GetGamesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GameEntry>>([]);
}
