using GameVault.Platform.Storage;

namespace GameVault.App.Services;

/// <summary>
/// Runs a handler that WinUI's native code calls (accelerators, AppWindow.Changed, window activation, pad navigation)
/// so an exception in it is written to crash.log and ui-trace.log and the app keeps running. Left alone, WinUI turns
/// such an exception into a fail-fast (0xC0000409) that closes the app with nothing logged (docs/bugs.md GV-027).
/// </summary>
internal static class UiGuard
{
    public static void Run(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            UiTrace.Write($"{what} failed and was stopped: {ex.GetType().Name}: {ex.Message}", onUiThread: true);
            CrashLog.Write(new AppPaths(), ex, $"{what} (caught; the app kept running)");
        }
    }
}
