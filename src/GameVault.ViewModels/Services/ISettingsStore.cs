namespace GameVault.ViewModels.Services;

public interface ISettingsStore
{
    /// <summary>Returns the saved settings, or defaults if none exist or they can't be read.</summary>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
