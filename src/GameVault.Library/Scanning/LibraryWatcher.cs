namespace GameVault.Library.Scanning;

/// <summary>
/// Watches the library folders while the app runs (SPEC §6.2) and raises <see cref="Changed"/> once things
/// settle, so a folder copy of hundreds of files triggers one rescan, not hundreds.
/// </summary>
public sealed class LibraryWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _debounce;
    private readonly TimeSpan _delay;

    public LibraryWatcher(TimeSpan? delay = null)
    {
        _delay = delay ?? TimeSpan.FromSeconds(2);
        _debounce = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Raised on a thread-pool thread after changes stop for the debounce delay.</summary>
    public event EventHandler? Changed;

    public void Watch(IEnumerable<string> folders)
    {
        StopWatching();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += OnChange;
            watcher.Deleted += OnChange;
            watcher.Renamed += OnChange;
            watcher.Changed += OnChange;
            watcher.Error += (_, _) => Poke(); // buffer overflow: rescan to be safe
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    /// <summary>Restarts the debounce timer (also what every file event does).</summary>
    public void Poke() => _debounce.Change(_delay, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        StopWatching();
        _debounce.Dispose();
    }

    private void OnChange(object sender, FileSystemEventArgs e)
    {
        var path = e.FullPath;
        if (LibraryScanner.IsGameFile(path)
            || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || !Path.HasExtension(path)) // a folder
        {
            Poke();
        }
    }

    private void StopWatching()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }
}
