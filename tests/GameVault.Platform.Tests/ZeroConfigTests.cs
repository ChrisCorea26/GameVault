using System.Diagnostics;
using GameVault.Core.Tests.Cartridge;
using GameVault.Library.Data;
using GameVault.Library.Profiles;
using GameVault.Library.Timeline;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Storage;
using GameVault.Platform.Video;
using GameVault.ViewModels;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>
/// SPEC §12 Phase 5 "done when": on a fresh install a new user adds a ROM folder from the empty Library and
/// is playing within 60 seconds, without opening Settings (SPEC §9.3). Runs the real library (SQLite,
/// scanner) and the real emulator; only the folder picker and navigation are scripted.
/// </summary>
public sealed class ZeroConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameVaultZeroConfig", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_new_user_is_playing_within_60_seconds_without_settings()
    {
        var clock = Stopwatch.StartNew();
        var romFolder = Path.Combine(_root, "My NES games");
        Directory.CreateDirectory(romFolder);
        File.WriteAllBytes(Path.Combine(romFolder, "Homebrew Test.nes"), BankedRom.Build(0, 4, 8));

        // Fresh install: empty database and app folder.
        var paths = new AppPaths(Path.Combine(_root, "AppData"));
        using var database = new LibraryDatabase(paths.LibraryDatabase);
        using var library = new LibraryService(database, new GameProfiles(null));
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()), new InMemorySettingsStore(), paths: paths);
        host.ProfileResolver = library.ResolveProfile;
        var timeline = new TimelineService(host, new TimelineStore(paths.StatesDirectory));
        var navigation = new ScriptedNavigation();
        var picker = new FolderPicker(romFolder);

        // 1. Launch: the Library is empty and offers to add a folder.
        var libraryPage = new LibraryViewModel(library, picker, navigation);
        await libraryPage.LoadCommand.ExecuteAsync(null);
        libraryPage.IsEmptyLibrary.Should().BeTrue();

        // 2. Add a ROM folder: it is scanned and the game appears.
        await libraryPage.AddFolderCommand.ExecuteAsync(null);
        libraryPage.Games.Should().ContainSingle().Which.Title.Should().Be("Homebrew Test");

        // 3. Open the game and press Play.
        libraryPage.OpenDetailsCommand.Execute(libraryPage.Games[0]);
        var details = new GameDetailsViewModel(
            library, new TimelineViewModel(timeline), navigation, picker,
            new PasswordsViewModel(new SamplePasswordService(), new InMemoryClipboard()),
            new JournalViewModel(new SampleJournalService(), picker));
        await details.LoadCommand.ExecuteAsync(navigation.LastParameter as string);
        details.CanPlay.Should().BeTrue();
        details.PlayCommand.Execute(null);

        // 4. The Player starts the game.
        var player = new PlayerViewModel(
            host, navigation, new InMemorySettingsStore(), new SampleInputDevicesService(), new TimelineViewModel(timeline), library,
            new SamplePasswordService(), new SampleJournalService(), new AssistViewModel(host), new PracticeViewModel(host, new InMemoryPracticeRecords()));
        await player.StartCommand.ExecuteAsync(navigation.LastParameter);
        player.HasError.Should().BeFalse(player.ErrorMessage);
        host.Status.Should().Be(EmulatorStatus.Running);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.FrameCount < 30 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        host.FrameCount.Should().BeGreaterThanOrEqualTo(30, "the game is actually running");
        navigation.Visited.Should().Equal(AppPage.GameDetails, AppPage.Player);
        navigation.Visited.Should().NotContain(AppPage.Settings);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));

        player.Stop();
    }

    private sealed class FolderPicker(string folder) : IGameFilePicker
    {
        public Task<string?> PickGameFileAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(folder);

        public Task<string?> PickCatalogFileAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickImageAsync() => Task.FromResult<string?>(null);
    }

    private sealed class ScriptedNavigation : INavigationService
    {
        public List<AppPage> Visited { get; } = [];

        public object? LastParameter { get; private set; }

        public AppPage? CurrentPage { get; private set; }

        public bool CanGoBack => Visited.Count > 1;

        public event EventHandler<AppPage>? Navigated;

        public void NavigateTo(AppPage page) => NavigateTo(page, null);

        public void NavigateTo(AppPage page, object? parameter)
        {
            Visited.Add(page);
            LastParameter = parameter;
            CurrentPage = page;
            Navigated?.Invoke(this, page);
        }

        public void GoBack()
        {
        }
    }
}
