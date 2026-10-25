using GameVault.Platform.Storage;
using GameVault.ViewModels;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;
using GameVault.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace GameVault.App;

public partial class App : Application
{
    private IServiceProvider? _services;
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>Resolves a service from the app's container. For views that WinUI constructs (pages).</summary>
    public static T GetService<T>()
        where T : notnull =>
        ((App)Current)._services is { } services
            ? services.GetRequiredService<T>()
            : throw new InvalidOperationException("Services are not configured yet.");

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
        }
        else
        {
            services.AddSingleton(_ => new AppPaths());
            services.AddSingleton<ILibraryService, EmptyLibraryService>();
            services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        }

        services.AddSingleton<FrameNavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<FrameNavigationService>());

        services.AddSingleton<ShellViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<SettingsViewModel>();

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
