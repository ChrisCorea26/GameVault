using GameVault.Platform.Storage;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameVaultTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_file_loads_defaults()
    {
        var store = new JsonSettingsStore(new AppPaths(_root));

        (await store.LoadAsync()).Should().Be(new AppSettings());
    }

    [Fact]
    public async Task Saved_settings_load_back_and_create_the_folder()
    {
        var paths = new AppPaths(_root);
        var store = new JsonSettingsStore(paths);
        var settings = new AppSettings { CropOverscan = false, RunAheadFrames = 3, PauseWhenInactive = false };

        await store.SaveAsync(settings);

        File.Exists(paths.SettingsFile).Should().BeTrue();
        File.Exists(paths.SettingsFile + ".tmp").Should().BeFalse();
        (await new JsonSettingsStore(paths).LoadAsync()).Should().Be(settings);
    }

    [Fact]
    public async Task Corrupt_file_loads_defaults()
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(paths.SettingsFile, "{ not json");

        (await new JsonSettingsStore(paths).LoadAsync()).Should().Be(new AppSettings());
    }

    [Fact]
    public void Default_root_is_under_local_app_data()
    {
        new AppPaths().Root.Should().EndWith("GameVault");
    }
}
