using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>Sample passwords for "Crystal Caverns" (key "sample:2") under <c>--sample-data</c>.</summary>
public sealed class SamplePasswordService : IPasswordService
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);
    private readonly List<(string Key, PasswordItem Item)> _items =
    [
        ("sample:2", new PasswordItem("p1", "BLUE MOON 4421", Now.AddDays(-3), true, null, null)),
        ("sample:2", new PasswordItem("p2", "STAR GATE 0917", Now.AddHours(-5), false, "Before the ice palace", null)),
    ];

    public event EventHandler<string>? Changed;

    public event EventHandler<string>? Captured
    {
        add { }
        remove { }
    }

    public Task<IReadOnlyList<PasswordItem>> GetAsync(string gameKey) =>
        Task.FromResult<IReadOnlyList<PasswordItem>>(_items.Where(i => i.Key == gameKey).Select(i => i.Item).OrderByDescending(i => i.SavedAt).ToList());

    public Task AddAsync(string gameKey, string password, byte[]? screenshotPng, string? note = null)
    {
        _items.Add((gameKey, new PasswordItem($"p{_items.Count + 1}", password.Trim(), Now.AddMinutes(_items.Count), false, note, null)));
        Changed?.Invoke(this, gameKey);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string gameKey, string id)
    {
        _items.RemoveAll(i => i.Key == gameKey && i.Item.Id == id);
        Changed?.Invoke(this, gameKey);
        return Task.CompletedTask;
    }
}

/// <summary>A sample journal with one note and no image files.</summary>
public sealed class SampleJournalService : IJournalService
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);
    private readonly Dictionary<string, (JournalNoteItem Note, string Rtf)> _notes = new()
    {
        ["n1"] = (new JournalNoteItem("n1", "Secrets of World 2", Now.AddDays(-1), []), @"{\rtf1\ansi The {\b second pipe} after the bridge leads to a warp room.}"),
    };

    private readonly List<JournalImageItem> _images = [];

    public event EventHandler<string>? Changed;

    public Task<IReadOnlyList<JournalNoteItem>> GetNotesAsync(string gameKey) =>
        Task.FromResult<IReadOnlyList<JournalNoteItem>>(gameKey == "sample:2" ? _notes.Values.Select(n => n.Note).OrderByDescending(n => n.Updated).ToList() : []);

    public Task<IReadOnlyList<JournalImageItem>> GetImagesAsync(string gameKey) =>
        Task.FromResult<IReadOnlyList<JournalImageItem>>(gameKey == "sample:2" ? _images.ToList() : []);

    public Task<JournalNoteItem> CreateNoteAsync(string gameKey, string title)
    {
        var note = new JournalNoteItem($"n{_notes.Count + 1}", string.IsNullOrWhiteSpace(title) ? "Untitled note" : title, Now.AddMinutes(_notes.Count), []);
        _notes[note.Id] = (note, string.Empty);
        Changed?.Invoke(this, gameKey);
        return Task.FromResult(note);
    }

    public Task<string> LoadNoteAsync(string gameKey, string noteId) =>
        Task.FromResult(_notes.TryGetValue(noteId, out var n) ? n.Rtf : string.Empty);

    public Task SaveNoteAsync(string gameKey, string noteId, string title, string rtf)
    {
        if (_notes.TryGetValue(noteId, out var n))
        {
            _notes[noteId] = (n.Note with { Title = title }, rtf);
            Changed?.Invoke(this, gameKey);
        }

        return Task.CompletedTask;
    }

    public Task DeleteNoteAsync(string gameKey, string noteId)
    {
        _notes.Remove(noteId);
        Changed?.Invoke(this, gameKey);
        return Task.CompletedTask;
    }

    public Task<JournalImageItem> AddScreenshotAsync(string gameKey, byte[] png)
    {
        var image = new JournalImageItem($"s{_images.Count + 1}.png", false, Now.AddMinutes(_images.Count), null, string.Empty);
        _images.Insert(0, image);
        Changed?.Invoke(this, gameKey);
        return Task.FromResult(image);
    }

    public Task<JournalImageItem> ImportMapAsync(string gameKey, string filePath)
    {
        var image = new JournalImageItem($"m{_images.Count + 1}.png", true, Now.AddMinutes(_images.Count), System.IO.Path.GetFileNameWithoutExtension(filePath), filePath);
        _images.Insert(0, image);
        Changed?.Invoke(this, gameKey);
        return Task.FromResult(image);
    }

    public Task DeleteImageAsync(string gameKey, string imageId)
    {
        _images.RemoveAll(i => i.Id == imageId);
        Changed?.Invoke(this, gameKey);
        return Task.CompletedTask;
    }

    public Task AttachAsync(string gameKey, string noteId, string imageId)
    {
        if (_notes.TryGetValue(noteId, out var n) && !n.Note.Attachments.Contains(imageId))
        {
            _notes[noteId] = (n.Note with { Attachments = [.. n.Note.Attachments, imageId] }, n.Rtf);
            Changed?.Invoke(this, gameKey);
        }

        return Task.CompletedTask;
    }
}

/// <summary>In-memory practice bests.</summary>
public sealed class InMemoryPracticeRecords : IPracticeRecords
{
    private readonly Dictionary<string, Dictionary<int, long>> _bests = [];

    public IReadOnlyDictionary<int, long> Bests(string gameKey) =>
        _bests.TryGetValue(gameKey, out var b) ? b : new Dictionary<int, long>();

    public bool Record(string gameKey, int segment, long frames)
    {
        var bests = _bests.TryGetValue(gameKey, out var b) ? b : _bests[gameKey] = [];
        if (bests.TryGetValue(segment, out var best) && best <= frames)
        {
            return false;
        }

        bests[segment] = frames;
        return true;
    }
}

/// <summary>Remembers the last copied text (sample mode and tests).</summary>
public sealed class InMemoryClipboard : IClipboardService
{
    public string? Text { get; private set; }

    public void SetText(string text) => Text = text;
}
