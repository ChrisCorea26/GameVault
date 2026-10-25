using GameVault.Platform.Audio;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Input.Controllers;
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
    }

    private static ServiceProvider ConfigureServices(AppEnvironment environment)
    {
        var services = new ServiceCollection();
        services.AddSingleton(environment);

        if (environment.IsSampleData)
        {
            // Fake services: a realistic library with no ROMs and no writes to disk (SPEC §9.1).
            services.AddSingleton<ILibraryService, SampleLibraryService>();
            services.AddSingleton<ISettingsStore>(_ => new InMemorySettingsStore());
            services.AddSingleton<IEmulatorService>(sp => new SampleEmulatorHost(sp.GetRequiredService<FrameSink>()));
            services.AddSingleton<IGameFilePicker, SampleGameFilePicker>();
            services.AddSingleton<IInputDevicesService, SampleInputDevicesService>();
        }
        else
        {
            services.AddSingleton(_ => new AppPaths());
            services.AddSingleton<ILibraryService, EmptyLibraryService>();
            services.AddSingleton<ISettingsStore, JsonSettingsStore>();
            services.AddSingleton<IEmulatorService>(sp => new EmulatorHost(
                sp.GetRequiredService<FrameSink>(),
                sp.GetRequiredService<InputService>(),
                sp.GetRequiredService<ISettingsStore>(),
                new WasapiAudioOutput()));
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
        services.AddTransient<PlayerViewModel>();

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
