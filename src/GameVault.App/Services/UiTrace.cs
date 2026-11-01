using System.Diagnostics;
using System.Runtime.ExceptionServices;
using GameVault.Platform.Storage;

namespace GameVault.App.Services;

/// <summary>
/// The last things the app asked WinUI to do, in <c>logs\ui-trace.log</c> (started afresh each run, flushed line by
/// line): WinUI ends the process with a fail-fast (0xC0000409) that no exception handler or crash.log sees, so these
/// lines are what's left to show what led to it (docs/bugs.md GV-027). Each line names the thread and whether it is
/// the UI thread, since calling WinUI from another thread is one cause of such a fail-fast.
/// </summary>
/// <remarks>
/// The other usual cause is a .NET exception thrown in a handler that WinUI's native code called (AppWindow.Changed,
/// input callbacks): WinUI fails fast on it instead of raising <c>Application.UnhandledException</c>. Every exception
/// is therefore written here when it is thrown (first chance), with the stack that threw it, before anything can
/// end the process.
/// </remarks>
internal static class UiTrace
{
    private const int MaxLines = 5000;

    /// <summary>First-chance exceptions logged per run; the rest are counted only, so a noisy loop can't fill the log.</summary>
    private const int MaxExceptions = 200;

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static bool _opened;
    private static int _lines;
    private static int _exceptions;

    [ThreadStatic]
    private static bool _inFirstChance;

    /// <summary>Opens the log and starts recording exceptions; called first thing in <c>App</c>'s constructor.</summary>
    public static void Start()
    {
        Write($"GameVault {typeof(UiTrace).Assembly.GetName().Version} started", onUiThread: true);
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
    }

    public static void Write(string what, bool onUiThread)
    {
        lock (Gate)
        {
            if (!_opened)
            {
                _opened = true;
                try
                {
                    var path = Path.Combine(Path.GetDirectoryName(new AppPaths().CrashLogFile)!, "ui-trace.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    _writer = new StreamWriter(path, append: false) { AutoFlush = true };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _writer = null;
                }
            }

            if (_writer is null || _lines++ >= MaxLines)
            {
                return;
            }

            try
            {
                _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [thread {Environment.CurrentManagedThreadId}{(onUiThread ? ", UI" : ", NOT the UI thread")}] {what}");
            }
            catch (IOException)
            {
                _writer = null;
            }
        }
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        // Writing can itself throw (and be caught); don't log those, or loop.
        if (_inFirstChance)
        {
            return;
        }

        _inFirstChance = true;
        try
        {
            if (Interlocked.Increment(ref _exceptions) > MaxExceptions)
            {
                return;
            }

            var ex = e.Exception;
            var hresult = ex.HResult != 0 ? $" (0x{ex.HResult:X8})" : string.Empty;
            Write(
                $"exception thrown: {ex.GetType().FullName}{hresult}: {ex.Message}{Environment.NewLine}{new StackTrace(1, fNeedFileInfo: false)}",
                onUiThread: SynchronizationContext.Current is Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext);
        }
        catch
        {
            // Logging must never turn into a crash of its own.
        }
        finally
        {
            _inFirstChance = false;
        }
    }
}
