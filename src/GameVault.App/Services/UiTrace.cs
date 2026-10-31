using GameVault.Platform.Storage;

namespace GameVault.App.Services;

/// <summary>
/// The last things the app asked WinUI to do, in <c>logs\ui-trace.log</c> (started afresh each run, flushed line by
/// line): WinUI ends the process with a fail-fast (0xC0000409) that no exception handler or crash.log sees, so these
/// lines are what's left to show what led to it (docs/bugs.md GV-027). Each line names the thread and whether it is
/// the UI thread, since calling WinUI from another thread is one cause of such a fail-fast.
/// </summary>
internal static class UiTrace
{
    private const int MaxLines = 5000;

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static bool _opened;
    private static int _lines;

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
}
