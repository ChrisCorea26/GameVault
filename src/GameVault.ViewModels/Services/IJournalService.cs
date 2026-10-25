namespace GameVault.ViewModels.Services;

public sealed record JournalNoteItem(string Id, string Title, DateTimeOffset Updated, IReadOnlyList<string> Attachments);

public sealed record JournalImageItem(string Id, bool IsMap, DateTimeOffset Created, string? Caption, string Path);

/// <summary>The game journal (SPEC §7.4): rich-text notes, F12 screenshots and imported maps.</summary>
public interface IJournalService
{
    /// <summary>The journal of a game changed; the argument is its key. Raised on the UI thread.</summary>
    event EventHandler<string>? Changed;

    Task<IReadOnlyList<JournalNoteItem>> GetNotesAsync(string gameKey);

    Task<IReadOnlyList<JournalImageItem>> GetImagesAsync(string gameKey);

    Task<JournalNoteItem> CreateNoteAsync(string gameKey, string title);

    /// <summary>The note's rich text (RTF).</summary>
    Task<string> LoadNoteAsync(string gameKey, string noteId);

    Task SaveNoteAsync(string gameKey, string noteId, string title, string rtf);

    Task DeleteNoteAsync(string gameKey, string noteId);

    Task<JournalImageItem> AddScreenshotAsync(string gameKey, byte[] png);

    Task<JournalImageItem> ImportMapAsync(string gameKey, string filePath);

    Task DeleteImageAsync(string gameKey, string imageId);

    Task AttachAsync(string gameKey, string noteId, string imageId);
}

/// <summary>Personal bests for practice segments (SPEC §7.6), in frames.</summary>
public interface IPracticeRecords
{
    IReadOnlyDictionary<int, long> Bests(string gameKey);

    /// <summary>Returns true for a new personal best.</summary>
    bool Record(string gameKey, int segment, long frames);
}
