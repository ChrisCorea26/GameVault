using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>
/// The game journal (SPEC §7.4): notes in rich text, screenshots taken with F12, and maps the player imports.
/// The page moves the selected note's RTF in and out of its RichEditBox; this ViewModel saves it.
/// </summary>
public sealed partial class JournalViewModel(IJournalService service, IGameFilePicker picker, IConfirmationService? confirm = null) : ObservableObject
{
    private string? _gameKey;
    private bool _attached;

    public ObservableCollection<JournalNoteItem> Notes { get; } = [];

    public ObservableCollection<JournalImageViewModel> Images { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedNote), nameof(SelectedNoteItem))]
    [NotifyCanExecuteChangedFor(nameof(DeleteNoteCommand))]
    public partial JournalNoteItem? SelectedNote { get; set; }

    public bool HasSelectedNote => SelectedNote is not null;

    /// <summary><see cref="SelectedNote"/> as an object, for a ListView's two-way SelectedItem binding.</summary>
    public object? SelectedNoteItem
    {
        get => SelectedNote;
        set => SelectedNote = value as JournalNoteItem;
    }

    /// <summary>The selected note's title, edited in place.</summary>
    [ObservableProperty]
    public partial string NoteTitle { get; set; } = string.Empty;

    /// <summary>The selected note's rich text (RTF). Set when a note is selected; the page reads it into the editor.</summary>
    [ObservableProperty]
    public partial string NoteRtf { get; private set; } = string.Empty;

    public bool HasNoNotes => Notes.Count == 0;

    public bool HasNoImages => Images.Count == 0;

    public async Task ShowAsync(string gameKey)
    {
        _gameKey = gameKey;
        if (!_attached)
        {
            service.Changed += OnChanged;
            _attached = true;
        }

        await RefreshAsync();
        SelectedNote ??= Notes.FirstOrDefault();
    }

    public void Detach()
    {
        if (_attached)
        {
            service.Changed -= OnChanged;
            _attached = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_gameKey is null)
        {
            return;
        }

        var selected = SelectedNote?.Id;
        Notes.Clear();
        foreach (var note in await service.GetNotesAsync(_gameKey))
        {
            Notes.Add(note);
        }

        Images.Clear();
        foreach (var image in await service.GetImagesAsync(_gameKey))
        {
            Images.Add(new JournalImageViewModel(this, image));
        }

        if (selected is not null && Notes.FirstOrDefault(n => n.Id == selected) is { } same && !ReferenceEquals(same, SelectedNote))
        {
            _keepText = true;
            SelectedNote = same;
            _keepText = false;
        }

        OnPropertyChanged(nameof(HasNoNotes));
        OnPropertyChanged(nameof(HasNoImages));
    }

    private bool _keepText;

    [RelayCommand]
    private async Task NewNoteAsync()
    {
        if (_gameKey is null)
        {
            return;
        }

        var note = await service.CreateNoteAsync(_gameKey, "New note");
        await RefreshAsync();
        SelectedNote = Notes.FirstOrDefault(n => n.Id == note.Id);
    }

    /// <summary>Saves the selected note's title and the editor's RTF (called when the editor loses focus).</summary>
    public async Task SaveNoteAsync(string rtf)
    {
        if (_gameKey is null || SelectedNote is null)
        {
            return;
        }

        NoteRtf = rtf;
        await service.SaveNoteAsync(_gameKey, SelectedNote.Id, NoteTitle, rtf);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedNote))]
    private async Task DeleteNoteAsync()
    {
        if (_gameKey is null || SelectedNote is null
            || !await (confirm ?? FixedConfirmation.Yes).ConfirmAsync(
                "Delete this note?", $"\"{SelectedNote.Title}\" is removed from the journal. This can't be undone.", "Delete"))
        {
            return;
        }

        await service.DeleteNoteAsync(_gameKey, SelectedNote.Id);
        SelectedNote = null;
        await RefreshAsync();
        SelectedNote = Notes.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ImportMapAsync()
    {
        if (_gameKey is not null && await picker.PickImageAsync() is { } path)
        {
            await service.ImportMapAsync(_gameKey, path);
        }
    }

    internal async Task AttachAsync(JournalImageViewModel image)
    {
        if (_gameKey is not null && SelectedNote is not null)
        {
            await service.AttachAsync(_gameKey, SelectedNote.Id, image.Id);
        }
    }

    internal async Task DeleteImageAsync(JournalImageViewModel image)
    {
        if (_gameKey is { } key
            && await (confirm ?? FixedConfirmation.Yes).ConfirmAsync(
                "Delete this picture?", $"\"{image.Title}\" is removed from the journal. This can't be undone.", "Delete"))
        {
            await service.DeleteImageAsync(key, image.Id);
        }
    }

    async partial void OnSelectedNoteChanged(JournalNoteItem? value)
    {
        if (_keepText)
        {
            return;
        }

        NoteTitle = value?.Title ?? string.Empty;
        NoteRtf = value is not null && _gameKey is not null ? await service.LoadNoteAsync(_gameKey, value.Id) : string.Empty;
    }

    private async void OnChanged(object? sender, string gameKey)
    {
        if (gameKey == _gameKey)
        {
            await RefreshAsync();
        }
    }
}

public sealed partial class JournalImageViewModel(JournalViewModel owner, JournalImageItem image)
{
    public string Id => image.Id;

    public string Title => image.Caption ?? (image.IsMap ? "Map" : "Screenshot");

    public string Subtitle => image.Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public Uri? ImageUri => image.Path is { Length: > 0 } path && System.IO.Path.IsPathRooted(path) ? new Uri(path) : null;

    public bool HasImage => ImageUri is not null;

    [RelayCommand]
    private Task AttachToNoteAsync() => owner.AttachAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteImageAsync(this);
}
