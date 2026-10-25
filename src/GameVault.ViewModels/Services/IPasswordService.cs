namespace GameVault.ViewModels.Services;

/// <summary>A saved password (SPEC §7.3).</summary>
public sealed record PasswordItem(string Id, string Password, DateTimeOffset SavedAt, bool Automatic, string? Note, string? ScreenshotPath);

/// <summary>
/// Captured passwords per game. Automatic captures come from the game database's definitions while
/// playing; manual ones are typed by the player (F9). Events are raised on the UI thread.
/// </summary>
public interface IPasswordService
{
    /// <summary>A password was saved; the argument is the game key.</summary>
    event EventHandler<string>? Changed;

    /// <summary>A password was recognized on screen; the argument is the password.</summary>
    event EventHandler<string>? Captured;

    Task<IReadOnlyList<PasswordItem>> GetAsync(string gameKey);

    Task AddAsync(string gameKey, string password, byte[]? screenshotPng, string? note = null);

    Task DeleteAsync(string gameKey, string id);
}

/// <summary>Puts text on the system clipboard.</summary>
public interface IClipboardService
{
    void SetText(string text);
}
