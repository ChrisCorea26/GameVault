using GameVault.Dumper;
using GameVault.Library.Data;
using GameVault.Library.Family;
using GameVault.Library.Challenges;
using GameVault.Library.Cheats;
using GameVault.Library.Journal;
using GameVault.Library.Maps;
using GameVault.Library.Memory;
using GameVault.Library.Sessions;
using GameVault.Library.Passwords;
using GameVault.Library.Practice;
using GameVault.Library.Profiles;
using GameVault.Library.Timeline;
using GameVault.Platform.Achievements;
using GameVault.Platform.Audio;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Input.Controllers;
using GameVault.Platform.Online;
using GameVault.Platform.Storage;
using GameVault.Platform.Video;
using GameVault.ViewModels;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;
using GameVault.ViewModels.Services.Input;
using GameVault.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace GameVault.App;

public partial class App : Application
{
    private IServiceProvider? _services;
    private MainWindow? _window;

    public App()
    {
        // Hook these first so failures while loading XAML are logged too.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                CrashLog.Write(new AppPaths(), ex, "AppDomain.UnhandledException");
            }
        };
        UnhandledException += (_, e) => CrashLog.Write(new AppPaths(), e.Exception, "Application.UnhandledException");

        InitializeComponent();
    }

    /// <summary>The app's only window. Available from <see cref="OnLaunched"/> on.</summary>
    public static MainWindow MainWindow =>
        ((App)Current)._window ?? throw new InvalidOperationException("The main window has not been created yet.");

    /// <summary>Resolves a service from the app's container. For views that WinUI constructs (pages).</summary>
    public static T GetService<T>()
        where T : notnull =>
        ((App)Current)._services is { } services
            ? services.GetRequiredService<T>()
            : throw new InvalidOperationException("Services are not configured yet.");

    /// <summary>Resolves an optional service (for example the controller manager, absent under --sample-data).</summary>
    public static T? TryGetService<T>()
        where T : class =>
        ((App)Current)._services?.GetService<T>();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Unpackaged apps receive their arguments on the process command line.
        var environment = AppEnvironment.FromCommandLine(Environment.GetCommandLineArgs().Skip(1));
        _services = ConfigureServices(environment);

        _window = _services.GetRequiredService<MainWindow>();
        _window.Activate();

        // Automatic maps listen to the emulator from the start (SPEC §7.9); not used with --sample-data.
        _ = _services.GetService<MapService>();

        // RetroAchievements signs in with the saved token in the background (SPEC §7.10).
        if (_services.GetService<AchievementsService>() is { } achievements)
        {
            _ = achievements.StartAsync();
        }

        // Housekeeping off the UI thread: states deleted last session are removed for good (SPEC §7.1 undo).
        if (_services.GetService<TimelineStore>() is { } timeline)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    timeline.PurgeDeleted();
                }
                catch (IOException ex)
                {
                    CrashLog.Write(new AppPaths(), ex, "Purging deleted save states");
                }
            });
        }
    }

    /// <summary>
    /// Restarts GameVault as another family profile: the game is closed and saved first (leaving the Player
    /// saves the resume point; disposing the emulator writes it), then a new GameVault starts and this one exits.
    /// </summary>
    private static void Restart(string profileId)
    {
        GetService<INavigationService>().NavigateTo(AppPage.Library);
        TryGetService<EmulatorHost>()?.Dispose();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            ArgumentList = { AppEnvironment.ProfileArgumentPrefix + profileId },
            UseShellExecute = false,
        });
        Current.Exit();
    }

    private static ServiceProvider ConfigureServices(AppEnvironment environment)
    {
        var services = new ServiceCollection();
        services.AddSingleton(environment);

        if (environment.IsSampleData)
        {
            // Fake services: a realistic library with no ROMs and no writes to disk (SPEC §9.1).
            services.AddSingleton<ILibraryService>(_ => new SampleLibraryService(environment.IsEmptySampleLibrary));
            services.AddSingleton<ISettingsStore>(_ => new InMemorySettingsStore());
            services.AddSingleton<IEmulatorService>(sp => new SampleEmulatorHost(sp.GetRequiredService<FrameSink>()));
            services.AddSingleton<IGameFilePicker, SampleGameFilePicker>();
            services.AddSingleton<IInputDevicesService, SampleInputDevicesService>();
            services.AddSingleton<ITimelineService, SampleTimelineService>();
            services.AddSingleton<IPasswordService, SamplePasswordService>();
            services.AddSingleton<IJournalService, SampleJournalService>();
            services.AddSingleton<IPracticeRecords, InMemoryPracticeRecords>();
            services.AddSingleton<IDumperService, SampleDumperService>();
            services.AddSingleton<IClipboardService, InMemoryClipboard>();
            services.AddSingleton<ICheatService, SampleCheatService>();
            services.AddSingleton<ISessionLog, InMemorySessionLog>();
            services.AddSingleton<ICodeFilePicker, SampleCodeFilePicker>();
            services.AddSingleton<IFamilyService>(_ => new InMemoryFamilyService());
            services.AddSingleton<IAchievementsService>(_ => new SampleAchievementsService(previewUnlockDelay: TimeSpan.FromSeconds(4)));
            services.AddSingleton<IChallengeFiles>(_ => new InMemoryChallengeFiles
            {
                ToOpen =
                {
                    [ChallengeFileKind.Challenge] = ChallengeFiles.Save(SampleEmulatorHost.SampleChallenge),
                    [ChallengeFileKind.Result] = ChallengeFiles.Save(new ChallengeResult
                    {
                        ChallengeId = SampleEmulatorHost.SampleChallenge.Id,
                        RomSha1 = "sample:2",
                        Player = "Sam",
                        Frames = 2154,
                        Inputs = string.Empty,
                    }),
                },
            });
        }
        else
        {
            // Family profiles (SPEC §7.9): the main profile uses GameVault's own folder, the others Profiles/<id>.
            var familyStore = new FamilyStore(new AppPaths().Root);
            var family = new FamilyService(familyStore, environment.ProfileId ?? FamilyProfile.MainId, Restart);
            services.AddSingleton<IFamilyService>(family);
            services.AddSingleton(sp =>
            {
                // RetroAchievements (SPEC §7.10): off, not broken, when the rcheevos component is missing.
                var paths = sp.GetRequiredService<AppPaths>();
                AchievementsClient? client = null;
                if (AchievementsClient.IsAvailable)
                {
                    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    client = new AchievementsClient(new HttpAchievementsServer(http));
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"GameVault/{typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0"} {client.UserAgentClause}");
                }

                return new AchievementsService(
                    sp.GetRequiredService<EmulatorHost>(),
                    client,
                    new AchievementsCredentials(Path.Combine(paths.Root, "retroachievements.json"), new DpapiTokenProtector()),
                    sp.GetRequiredService<ISettingsStore>());
            });
            services.AddSingleton<IAchievementsService>(sp => sp.GetRequiredService<AchievementsService>());
            services.AddSingleton(_ => new AppPaths(familyStore.DataRootFor(family.Current.Id)));
            services.AddSingleton(sp => new LibraryDatabase(sp.GetRequiredService<AppPaths>().LibraryDatabase));
            services.AddSingleton(_ => new GameProfiles(Path.Combine(AppContext.BaseDirectory, "data", "gamedb")));
            services.AddSingleton<LibraryService>();
            services.AddSingleton<ILibraryService>(sp => sp.GetRequiredService<LibraryService>());
            services.AddSingleton<ISettingsStore, JsonSettingsStore>();
            services.AddSingleton(sp => new EmulatorHost(
                sp.GetRequiredService<FrameSink>(),
                sp.GetRequiredService<InputService>(),
                sp.GetRequiredService<ISettingsStore>(),
                new WasapiAudioOutput(),
                sp.GetRequiredService<AppPaths>())
            {
                ProfileResolver = sp.GetRequiredService<LibraryService>().ResolveProfile,
                PasswordDefinitionResolver = new PasswordDefinitions(Path.Combine(AppContext.BaseDirectory, "data", "gamedb", "passwords")).For,
                MemoryMapResolver = new MemoryMaps(Path.Combine(AppContext.BaseDirectory, "data", "gamedb", "memory")).For,
            });
            services.AddSingleton<IEmulatorService>(sp => sp.GetRequiredService<EmulatorHost>());
            services.AddSingleton(sp => new TimelineStore(sp.GetRequiredService<AppPaths>().StatesDirectory));
            services.AddSingleton<ITimelineService>(sp => new TimelineService(
                sp.GetRequiredService<EmulatorHost>(),
                sp.GetRequiredService<TimelineStore>(),
                new SaveStateStore(sp.GetRequiredService<AppPaths>()),
                sp.GetRequiredService<LibraryService>().PlaytimeOf));
            services.AddSingleton<IPasswordService>(sp => new PasswordService(
                sp.GetRequiredService<EmulatorHost>(), new PasswordStore(sp.GetRequiredService<AppPaths>().PasswordsDirectory)));
            services.AddSingleton(sp => new JournalService(new JournalStore(sp.GetRequiredService<AppPaths>().JournalDirectory)));
            services.AddSingleton<IJournalService>(sp => sp.GetRequiredService<JournalService>());
            services.AddSingleton(sp => new MapService(
                sp.GetRequiredService<EmulatorHost>(),
                new MapStore(sp.GetRequiredService<AppPaths>().MapsDirectory),
                sp.GetRequiredService<JournalService>()));
            services.AddSingleton<IPracticeRecords>(sp => new PracticeRecords(new PracticeStore(sp.GetRequiredService<AppPaths>().PracticeDirectory)));
            services.AddSingleton<IClipboardService, WinUiClipboard>();
            services.AddSingleton<ICheatService>(sp => new CheatService(
                sp.GetRequiredService<EmulatorHost>(),
                new CheatStore(sp.GetRequiredService<AppPaths>().CheatsDirectory),
                new CheatSuggestions(Path.Combine(AppContext.BaseDirectory, "data", "gamedb", "cheats")),
                new CodeCollections(sp.GetRequiredService<AppPaths>().CodeCollectionsDirectory),
                new GameGenieDotCom(
                    new HttpClient { DefaultRequestHeaders = { { "User-Agent", "GameVault (NES emulator; code lookup on request)" } } },
                    sp.GetRequiredService<AppPaths>().OnlineCodesCacheDirectory)));
            services.AddSingleton<ICodeFilePicker, WinUiCodeFilePicker>();
            services.AddSingleton<IChallengeFiles, WinUiChallengeFiles>();
            services.AddSingleton<ISessionLog>(sp => new SessionLog(new SessionStore(sp.GetRequiredService<AppPaths>().SessionsDirectory)));
            services.AddSingleton<IDumperService>(sp =>
            {
                // The dumper is optional (SPEC §8): only set up when its program is configured.
                var store = sp.GetRequiredService<ISettingsStore>();
                var path = Task.Run(() => store.LoadAsync()).GetAwaiter().GetResult().DumperPath;
                IDumper? dumper = string.IsNullOrWhiteSpace(path) ? null : new InlRetroDumper(new SystemProcessRunner(), path);
                return new DumperService(dumper, sp.GetRequiredService<ILibraryService>());
            });
            services.AddSingleton<IGameFilePicker, WinUiGameFilePicker>();
            services.AddSingleton(sp => new ControllerManager(
                sp.GetRequiredService<InputService>(),
                new SdlControllerBackend(),
                sp.GetRequiredService<ISettingsStore>()));
            services.AddSingleton<IInputDevicesService>(sp => sp.GetRequiredService<ControllerManager>());
        }

        // Video and input (SPEC §5): shared by the emulation thread and the GameView.
        services.AddSingleton<FrameSink>();
        services.AddSingleton<IWindowService, WinUiWindowService>();
        services.AddSingleton(_ => new InputService(KeyboardMapping.CreateDefault()));

        services.AddSingleton<FrameNavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<FrameNavigationService>());

        services.AddSingleton<ShellViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<InputSettingsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<PadNavigation>();
        services.AddTransient<TimelineViewModel>();
        services.AddTransient<GameDetailsViewModel>();
        services.AddTransient<CatalogImportViewModel>();
        services.AddTransient<LibrarySettingsViewModel>();
        services.AddTransient<PasswordsViewModel>();
        services.AddTransient<JournalViewModel>();
        services.AddTransient<AssistViewModel>();
        services.AddTransient<PracticeViewModel>();
        services.AddTransient<CodesViewModel>();
        services.AddTransient<NetplayViewModel>();
        services.AddTransient<CodeCollectionsViewModel>();
        services.AddTransient<RecapViewModel>();
        services.AddTransient<ChallengesViewModel>();
        services.AddTransient<PlayLimitViewModel>();
        services.AddSingleton<FamilyViewModel>();
        services.AddTransient<AchievementsViewModel>(sp => new AchievementsViewModel(sp.GetRequiredService<IAchievementsService>()));
        services.AddTransient<RetroAchievementsSettingsViewModel>();
        services.AddTransient<DumpCartridgeViewModel>();
        services.AddTransient<PlayerViewModel>();

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
