using System.Runtime.InteropServices;
using GameVault.ViewModels.Services;
using Microsoft.UI.Dispatching;

namespace GameVault.App.Services;

/// <summary>
/// Writes the process's USER and GDI object counts to ui-trace.log every few seconds, with the page shown, and a
/// warning as USER objects near Windows' per-process limit of 10,000. The reporter's crash dump showed WinUI failing to
/// create a popup with ERROR_NO_MORE_USER_HANDLES (0x80070486) and then failing fast: something leaks USER objects, and
/// these lines show how fast and on which screen (docs/bugs.md GV-027).
/// </summary>
internal sealed partial class HandleMonitor
{
    private const uint GrGdiObjects = 0;
    private const uint GrUserObjects = 1;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly INavigationService _navigation;
    private DispatcherQueueTimer? _timer;
    private uint _lastUser;
    private uint _lastGdi;

    public HandleMonitor(INavigationService navigation) => _navigation = navigation;

    /// <summary>Starts the timer on the UI thread's queue; called once the window has loaded.</summary>
    public void Start(DispatcherQueue queue)
    {
        if (_timer is not null)
        {
            return;
        }

        _navigation.Navigated += (_, page) => Write($"navigated to {page}");
        Write("start");
        _timer = queue.CreateTimer();
        _timer.Interval = Interval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Write(null);
        _timer.Start();
    }

    private void Write(string? why)
    {
        var process = GetCurrentProcess();
        var user = GetGuiResources(process, GrUserObjects);
        var gdi = GetGuiResources(process, GrGdiObjects);
        var warning = user >= 8000 ? "  ** near the 10,000 USER object limit: popups will start to fail **" : string.Empty;
        UiTrace.Write(
            $"handles: USER {user} ({Delta(user, _lastUser)}), GDI {gdi} ({Delta(gdi, _lastGdi)}), page {_navigation.CurrentPage?.ToString() ?? "none"}{(why is null ? string.Empty : $", {why}")}{warning}",
            onUiThread: true);
        _lastUser = user;
        _lastGdi = gdi;
    }

    private static string Delta(uint now, uint before) => before == 0 ? "first" : $"{(int)now - (int)before:+0;-0;0}";

    [LibraryImport("user32.dll")]
    private static partial uint GetGuiResources(nint process, uint flags);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();
}
