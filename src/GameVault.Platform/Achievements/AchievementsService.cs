using GameVault.Platform.Emulation;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Achievements;

/// <summary>
/// RetroAchievements for the app (SPEC §7.10): signs in (keeping only the encrypted token), loads each game's
/// achievements as it starts, applies hardcore mode to the emulator, and passes rcheevos' events to the UI thread.
/// </summary>
public sealed class AchievementsService : IAchievementsService, IDisposable
{
    private readonly EmulatorHost _host;
    private readonly AchievementsClient? _client;
    private readonly AchievementsCredentials _credentials;
    private readonly ISettingsStore _settings;
    private readonly SynchronizationContext? _context;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _loadedHash;
    private bool _hardcore = true;

    public AchievementsService(EmulatorHost host, AchievementsClient? client, AchievementsCredentials credentials, ISettingsStore settings)
    {
        _host = host;
        _client = client;
        _credentials = credentials;
        _settings = settings;
        _context = SynchronizationContext.Current;
        if (client is not null)
        {
            host.Achievements = client;
            client.Event += OnClientEvent;
            host.StatusChanged += OnHostStatusChanged;
        }
    }

    public bool IsAvailable => _client is not null;

    public AchievementsUser? User => _client?.User;

    public bool IsSignedIn => User is not null;

    public bool Hardcore => _hardcore;

    public AchievementsGame? Game => _client?.Game;

    public string? RichPresence => _client?.IsGameLoaded == true ? _client.RichPresence : null;

    public event EventHandler<AchievementsEvent>? Event;

    public event EventHandler? Changed;

    /// <summary>Reads the hardcore setting and signs in with the saved token, if there is one. Errors are kept quiet.</summary>
    public async Task StartAsync()
    {
        _hardcore = (await _settings.LoadAsync().ConfigureAwait(false)).RetroAchievementsHardcore;
        if (_client is null)
        {
            return;
        }

        _client.HardcoreEnabled = _hardcore;
        if (_credentials.Load() is not { } saved)
        {
            return;
        }

        try
        {
            await _client.LoginWithTokenAsync(saved.UserName, saved.Token).ConfigureAwait(false);
        }
        catch (AchievementsException ex) when (ex.Code is RcheevosNative.ExpiredToken or RcheevosNative.InvalidCredentials)
        {
            _credentials.Delete(); // the player signs in again from Settings
        }
        catch (AchievementsException)
        {
            return; // offline: try again next start
        }

        await SyncGameAsync().ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task SignInAsync(string userName, string password)
    {
        if (_client is null)
        {
            throw new AchievementsException("RetroAchievements isn't available in this copy of GameVault.");
        }

        await _client.LoginWithPasswordAsync(userName.Trim(), password).ConfigureAwait(false);
        if (_client.User is { } user && _client.Token is { } token)
        {
            _credentials.Save(user.UserName, token);
        }

        await SyncGameAsync().ConfigureAwait(false);
        RaiseChanged();
    }

    public void SignOut()
    {
        if (_client is null)
        {
            return;
        }

        _client.Logout();
        _credentials.Delete();
        _loadedHash = null;
        UpdateHardcore();
        RaiseChanged();
    }

    public async Task SetHardcoreAsync(bool enabled)
    {
        _hardcore = enabled;
        var settings = await _settings.LoadAsync().ConfigureAwait(false);
        await _settings.SaveAsync(settings with { RetroAchievementsHardcore = enabled }).ConfigureAwait(false);
        if (_client is not null)
        {
            // Turning it on with a game loaded makes rcheevos ask for a reset (OnClientEvent restarts the game).
            _client.HardcoreEnabled = enabled;
        }

        UpdateHardcore();
        RaiseChanged();
    }

    public IReadOnlyList<AchievementGroup> GetAchievements() => _client?.Achievements() ?? [];

    public void Dispose()
    {
        if (_client is not null)
        {
            _client.Event -= OnClientEvent;
            _host.StatusChanged -= OnHostStatusChanged;
            if (_host.Achievements == _client)
            {
                _host.Achievements = null;
            }
        }

        _host.HardcoreActive = false;
        _gate.Dispose();
    }

    private async void OnHostStatusChanged(object? sender, EventArgs e)
    {
        try
        {
            await SyncGameAsync().ConfigureAwait(false);
            RaiseChanged();
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    /// <summary>Loads the running game's achievements, or unloads them when the game changed or stopped.</summary>
    private async Task SyncGameAsync()
    {
        if (_client is null)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var hash = _client.User is null ? null : _host.AchievementsHash;
            UpdateHardcore();
            if (hash == _loadedHash)
            {
                return;
            }

            if (_loadedHash is not null)
            {
                _client.UnloadGame();
            }

            _loadedHash = hash;
            if (hash is null)
            {
                return;
            }

            try
            {
                await _client.LoadGameAsync(hash).ConfigureAwait(false);
            }
            catch (AchievementsException ex)
            {
                // An unknown game simply has no achievements; anything else is worth a word.
                if (!ex.Message.Contains("Unknown game", StringComparison.OrdinalIgnoreCase))
                {
                    Raise(new AchievementsEvent(AchievementsEventKind.ServerError, Message: ex.Message));
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The emulator's hardcore rules apply while signed in with hardcore on and a game running.</summary>
    private void UpdateHardcore() =>
        _host.HardcoreActive = _client?.User is not null && _hardcore && _host.AchievementsHash is not null;

    private void OnClientEvent(object? sender, AchievementsEvent e)
    {
        if (e.Kind == AchievementsEventKind.ResetRequested)
        {
            _host.Reset(); // hardcore was turned on mid-game
        }

        Raise(e);
    }

    private void Raise(AchievementsEvent e)
    {
        if (_context is null)
        {
            Event?.Invoke(this, e);
        }
        else
        {
            _context.Post(_ => Event?.Invoke(this, e), null);
        }
    }

    private void RaiseChanged()
    {
        if (_context is null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _context.Post(_ => Changed?.Invoke(this, EventArgs.Empty), null);
        }
    }
}
