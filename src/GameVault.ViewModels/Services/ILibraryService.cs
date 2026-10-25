using GameVault.Library.Models;

namespace GameVault.ViewModels.Services;

/// <summary>Provides the games in the user's library and catalog.</summary>
public interface ILibraryService
{
    Task<IReadOnlyList<GameEntry>> GetGamesAsync(CancellationToken cancellationToken = default);
}
