using GameVault.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameVault.App.Views;

public sealed partial class GameDetailsPage : Page
{
    private string? _gameKey;

    public GameDetailsPage()
    {
        ViewModel = App.GetService<GameDetailsViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public GameDetailsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _gameKey = e.Parameter as string;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Journal.PropertyChanged -= OnJournalChanged;
        ViewModel.Detach();
        base.OnNavigatedFrom(e);
    }

    // Data loads on Loaded, never in the constructor (SPEC §9.1).
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Journal.PropertyChanged += OnJournalChanged;
        await ViewModel.LoadCommand.ExecuteAsync(_gameKey);
        LoadNoteIntoEditor();
        PlayButton.Focus(FocusState.Programmatic);
    }

    // RichEditBox has no bindable text, so the note's RTF moves in and out here (view-only glue).
    private void OnJournalChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JournalViewModel.NoteRtf))
        {
            LoadNoteIntoEditor();
        }
    }

    private void LoadNoteIntoEditor() =>
        NoteEditor.Document.SetText(Microsoft.UI.Text.TextSetOptions.FormatRtf, ViewModel.Journal.NoteRtf);

    private async void NoteEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        NoteEditor.Document.GetText(Microsoft.UI.Text.TextGetOptions.FormatRtf, out var rtf);
        if (rtf != ViewModel.Journal.NoteRtf)
        {
            await ViewModel.Journal.SaveNoteAsync(rtf);
        }
        else
        {
            await ViewModel.Journal.SaveNoteAsync(ViewModel.Journal.NoteRtf); // title only
        }
    }

    private void Text_LostFocus(object sender, RoutedEventArgs e) => ViewModel.SaveTextCommand.Execute(null);
}
