using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>A settings store that never touches disk. Used for <c>--sample-data</c> and tests.</summary>
public sealed class InMemorySettingsStore(AppSettings? initial = null) : ISettingsStore
{
    public AppSettings Current { get; private set; } = initial ?? new AppSettings();

    public int SaveCount { get; private set; }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        SaveCount++;
        return Task.CompletedTask;
    }
}
