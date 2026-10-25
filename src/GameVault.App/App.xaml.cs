using GameVault.Dumper;
using GameVault.Library.Data;
using GameVault.Library.Cheats;
using GameVault.Library.Journal;
using GameVault.Library.Passwords;
using GameVault.Library.Practice;
using GameVault.Library.Profiles;
using GameVault.Library.Timeline;
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
            services.AddSingleton<ICodeFilePicker, SampleCodeFilePicker>();
        }
        else
        {
            services.AddSingleton(_ => new AppPaths());
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
            services.AddSingleton<IJournalService>(sp => new JournalService(new JournalStore(sp.GetRequiredService<AppPaths>().JournalDirectory)));
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
        services.AddTransient<DumpCartridgeViewModel>();
        services.AddTransient<PlayerViewModel>();

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
