using GameVault.Library.Journal;
using GameVault.Library.Passwords;
using GameVault.Library.Practice;
using GameVault.Platform.Emulation;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Storage;

/// <summary>
/// Saved passwords (SPEC §7.3). Passwords the host recognizes on screen are stored automatically (once:
/// the same password twice in a row is not saved again); the player adds others by hand.
/// </summary>
public sealed class PasswordService : IPasswordService
{
    private readonly EmulatorHost _host;
    private readonly PasswordStore _store;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public PasswordService(EmulatorHost host, PasswordStore store)
    {
        _host = host;
        _store = store;
        _host.PasswordCaptured += OnPasswordCaptured;
    }

    public event EventHandler<string>? Changed;

    public event EventHandler<string>? Captured;

    public Task<IReadOnlyList<PasswordItem>> GetAsync(string gameKey) => Task.Run(() =>
        (IReadOnlyList<PasswordItem>)_store.List(gameKey)
            .Select(e => new PasswordItem(
                e.Id, e.Password, e.SavedAt, e.Automatic, e.Note,
                File.Exists(_store.ScreenshotPath(gameKey, e.Id)) ? _store.ScreenshotPath(gameKey, e.Id) : null))
            .ToList());

    public async Task AddAsync(string gameKey, string password, byte[]? screenshotPng, string? note = null)
    {
        await Task.Run(() => _store.Add(gameKey, password, screenshotPng ?? [], automatic: false, DateTimeOffset.Now, note));
        Raise(() => Changed?.Invoke(this, gameKey));
    }

    public async Task DeleteAsync(string gameKey, string id)
    {
        await Task.Run(() => _store.Delete(gameKey, id));
        Raise(() => Changed?.Invoke(this, gameKey));
    }

    private async void OnPasswordCaptured(object? sender, PasswordCapturedEventArgs e)
    {
        if (_host.GameKey is not { } key)
        {
            return;
        }

        var saved = await Task.Run(() =>
        {
            if (_store.IsLatest(key, e.Password))
            {
                return false;
            }

            _store.Add(key, e.Password, e.ScreenshotPng, automatic: true, DateTimeOffset.Now);
            return true;
        });

        if (saved)
        {
            Raise(() =>
            {
                Captured?.Invoke(this, e.Password);
                Changed?.Invoke(this, key);
            });
        }
    }

    private void Raise(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}

/// <summary>The game journal (SPEC §7.4) over <see cref="JournalStore"/>.</summary>
public sealed class JournalService(JournalStore store) : IJournalService
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public event EventHandler<string>? Changed;

    public Task<IReadOnlyList<JournalNoteItem>> GetNotesAsync(string gameKey) => Task.Run(() =>
        (IReadOnlyList<JournalNoteItem>)store.Notes(gameKey).Select(n => new JournalNoteItem(n.Id, n.Title, n.Updated, n.Attachments)).ToList());

    public Task<IReadOnlyList<JournalImageItem>> GetImagesAsync(string gameKey) => Task.Run(() =>
        (IReadOnlyList<JournalImageItem>)store.Images(gameKey)
            .Select(i => new JournalImageItem(i.Id, i.Kind == JournalImageKind.Map, i.Created, i.Caption, store.ImagePath(gameKey, i.Id)))
            .ToList());

    public async Task<JournalNoteItem> CreateNoteAsync(string gameKey, string title)
    {
        var note = await Task.Run(() => store.CreateNote(gameKey, title, DateTimeOffset.Now));
        RaiseChanged(gameKey);
        return new JournalNoteItem(note.Id, note.Title, note.Updated, note.Attachments);
    }

    public Task<string> LoadNoteAsync(string gameKey, string noteId) => Task.Run(() => store.LoadNote(gameKey, noteId));

    public async Task SaveNoteAsync(string gameKey, string noteId, string title, string rtf)
    {
        await Task.Run(() => store.SaveNote(gameKey, noteId, title, rtf, DateTimeOffset.Now));
        RaiseChanged(gameKey);
    }

    public async Task DeleteNoteAsync(string gameKey, string noteId)
    {
        await Task.Run(() => store.DeleteNote(gameKey, noteId));
        RaiseChanged(gameKey);
    }

    public async Task<JournalImageItem> AddScreenshotAsync(string gameKey, byte[] png)
    {
        var image = await Task.Run(() => store.AddImage(gameKey, JournalImageKind.Screenshot, png, ".png", DateTimeOffset.Now));
        RaiseChanged(gameKey);
        return new JournalImageItem(image.Id, false, image.Created, null, store.ImagePath(gameKey, image.Id));
    }

    public async Task<JournalImageItem> ImportMapAsync(string gameKey, string filePath)
    {
        var image = await Task.Run(() => store.AddImage(
            gameKey, JournalImageKind.Map, File.ReadAllBytes(filePath), Path.GetExtension(filePath), DateTimeOffset.Now, Path.GetFileNameWithoutExtension(filePath)));
        RaiseChanged(gameKey);
        return new JournalImageItem(image.Id, true, image.Created, image.Caption, store.ImagePath(gameKey, image.Id));
    }

    public async Task DeleteImageAsync(string gameKey, string imageId)
    {
        await Task.Run(() => store.DeleteImage(gameKey, imageId));
        RaiseChanged(gameKey);
    }

    public async Task AttachAsync(string gameKey, string noteId, string imageId)
    {
        await Task.Run(() => store.Attach(gameKey, noteId, imageId));
        RaiseChanged(gameKey);
    }

    private void RaiseChanged(string gameKey)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            Changed?.Invoke(this, gameKey);
        }
        else
        {
            _context.Post(_ => Changed?.Invoke(this, gameKey), null);
        }
    }
}

/// <summary>Practice personal bests on disk (SPEC §7.6).</summary>
public sealed class PracticeRecords(PracticeStore store) : IPracticeRecords
{
    public IReadOnlyDictionary<int, long> Bests(string gameKey) => store.Bests(gameKey);

    public bool Record(string gameKey, int segment, long frames) => store.Record(gameKey, segment, frames);
}
