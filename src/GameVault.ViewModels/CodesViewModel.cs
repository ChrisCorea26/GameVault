using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.Core.Cheats;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>
/// The Codes list (SPEC §7.8), on Game Details and in the Player's Codes flyout: the player's Game Genie and
/// RAM codes for one game, each with an on/off switch, an add box that checks codes as they are typed,
/// import/export, and suggestions: from the bundled database, from the player's own code collections, and
/// from GameGenie.com on request, each shown with where it came from.
/// </summary>
public sealed partial class CodesViewModel(ICheatService service, ICodeFilePicker picker, IConfirmationService? confirm = null) : ObservableObject
{
    private string? _gameKey;
    private string? _title;
    private bool _attached;
    private CodeSourceResult? _collection;
    private CodeSourceResult? _online;

    /// <summary>Codes for this game from the player's imported collections.</summary>
    public ObservableCollection<CodeSuggestionViewModel> CollectionCodes { get; } = [];

    public bool HasCollectionCodes => CollectionCodes.Count > 0;

    /// <summary>"From your code collection … (source: …)", shown with the codes.</summary>
    public string CollectionCredit => _collection?.Credit ?? string.Empty;

    /// <summary>The collection's notes for this game ("Codes 8 and 9 work in 2-player mode only").</summary>
    public string CollectionNotes => _collection is { Notes.Count: > 0 } c ? string.Join("\n", c.Notes) : string.Empty;

    public bool HasCollectionNotes => CollectionNotes.Length > 0;

    /// <summary>"Codes for Super Mario Bros. in your collection", naming the collection's game.</summary>
    public string CollectionHeading => _collection is { } c ? $"Codes for {c.Title} in your collection" : string.Empty;

    /// <summary>True when the player has imported a collection, so they can pick this game's entry by name.</summary>
    [ObservableProperty]
    public partial bool HasCollections { get; private set; }

    /// <summary>Text to find this game in the collections when its title doesn't match by itself.</summary>
    [ObservableProperty]
    public partial string CollectionSearch { get; set; } = string.Empty;

    public ObservableCollection<string> CollectionMatches { get; } = [];

    /// <summary>Codes for this game from GameGenie.com, once asked for.</summary>
    public ObservableCollection<CodeSuggestionViewModel> OnlineCodes { get; } = [];

    public bool HasOnlineCodes => OnlineCodes.Count > 0;

    /// <summary>The credit for GameGenie.com's codes; always shown with them.</summary>
    public string OnlineCredit => _online?.Credit ?? string.Empty;

    /// <summary>The GameGenie.com page the codes came from.</summary>
    public Uri? OnlineUrl => _online?.SourceUrl;

    [ObservableProperty]
    public partial string? OnlineMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GetOnlineCodesCommand))]
    public partial bool IsFetchingOnline { get; private set; }

    public ObservableCollection<CodeItemViewModel> Items { get; } = [];

    public ObservableCollection<CodeSuggestionViewModel> Suggestions { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>True while any code is on (the Player shows a "Codes on" badge).</summary>
    public bool AnyEnabled => Items.Any(i => i.IsEnabled);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NewCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewDescription { get; set; } = string.Empty;

    /// <summary>What the typed code does ("Decodes to $91D9 = $AD"), or why it isn't a code.</summary>
    [ObservableProperty]
    public partial string Validation { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool IsNewCodeValid { get; private set; }

    /// <summary>The result of the last import or export.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <param name="title">The game's title, used to find it in code collections and on GameGenie.com.</param>
    public async Task ShowAsync(string gameKey, string? title = null)
    {
        if (gameKey != _gameKey)
        {
            _online = null;
            OnlineMessage = null;
        }

        _gameKey = gameKey;
        _title = title;
        if (!_attached)
        {
            service.Changed += OnChanged;
            _attached = true;
        }

        await RefreshAsync();
    }

    public void Detach()
    {
        if (_attached)
        {
            service.Changed -= OnChanged;
            _attached = false;
        }
    }

    partial void OnNewCodeChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Validation = string.Empty;
            IsNewCodeValid = false;
            return;
        }

        IsNewCodeValid = CheatCode.TryParseList(value, out var codes, out var error);
        Validation = IsNewCodeValid ? "Decodes to " + CheatCode.Describe(codes) : error;
    }

    [RelayCommand(CanExecute = nameof(IsNewCodeValid))]
    private async Task AddAsync()
    {
        if (_gameKey is null || !IsNewCodeValid)
        {
            return;
        }

        await service.AddAsync(_gameKey, NewCode, NewDescription);
        NewCode = string.Empty;
        NewDescription = string.Empty;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task AllOffAsync()
    {
        if (_gameKey is not null)
        {
            await service.SetAllEnabledAsync(_gameKey, false);
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_gameKey is null || await picker.PickImportFileAsync() is not { } path)
        {
            return;
        }

        var result = await service.ImportAsync(_gameKey, path);
        Message = result.Errors.Count > 0
            ? "Nothing was imported. " + string.Join(" ", result.Errors.Take(3)) + (result.Errors.Count > 3 ? $" (and {result.Errors.Count - 3} more)" : string.Empty)
            : result.Added switch
            {
                0 => "Those codes are already in the list.",
                1 => "Added 1 code.",
                var n => $"Added {n} codes.",
            };
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_gameKey is null || await picker.PickExportFileAsync("codes.txt") is not { } path)
        {
            return;
        }

        await service.ExportAsync(_gameKey, path);
        Message = "Saved the codes to " + Path.GetFileName(path) + ".";
    }

    partial void OnCollectionSearchChanged(string value) => _ = SearchCollectionsAsync(value);

    private async Task SearchCollectionsAsync(string text)
    {
        var matches = string.IsNullOrWhiteSpace(text) ? [] : await service.SearchCollectionsAsync(text);
        if (text != CollectionSearch)
        {
            return; // a newer search is on its way
        }

        CollectionMatches.Clear();
        foreach (var match in matches)
        {
            CollectionMatches.Add(match);
        }
    }

    /// <summary>The player says which collection game this is; remembered for next time.</summary>
    [RelayCommand]
    private async Task ChooseCollectionGameAsync(string? title)
    {
        if (_gameKey is null || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        await service.SetCollectionTitleAsync(_gameKey, title);
        CollectionSearch = string.Empty;
        await RefreshAsync();
    }

    private bool CanGetOnlineCodes() => !IsFetchingOnline;

    [RelayCommand(CanExecute = nameof(CanGetOnlineCodes))]
    private async Task GetOnlineCodesAsync()
    {
        if (_gameKey is null)
        {
            return;
        }

        IsFetchingOnline = true;
        OnlineMessage = null;
        try
        {
            _online = await service.GetOnlineCodesAsync(_gameKey, _title);
            if (_online is null)
            {
                OnlineMessage = $"GameGenie.com has no page for {_title ?? "this game"}. If it's listed under another name, choose it from your collection or type the codes in.";
            }
        }
        catch (CodeSourceException ex)
        {
            OnlineMessage = ex.Message;
        }
        finally
        {
            IsFetchingOnline = false;
        }

        await RefreshAsync();
    }

    internal async Task SetEnabledAsync(CodeItemViewModel item, bool enabled)
    {
        if (_gameKey is not null)
        {
            await service.SetEnabledAsync(_gameKey, item.Id, enabled);
            OnPropertyChanged(nameof(AnyEnabled));
        }
    }

    internal async Task DeleteAsync(CodeItemViewModel item)
    {
        if (_gameKey is not null
            && await (confirm ?? FixedConfirmation.Yes).ConfirmAsync("Delete this code?", "The code is removed from this game. This can't be undone.", "Delete"))
        {
            await service.DeleteAsync(_gameKey, item.Id);
            await RefreshAsync();
        }
    }

    internal async Task UseSuggestionAsync(CodeSuggestionViewModel suggestion)
    {
        if (_gameKey is not null)
        {
            await service.AddAsync(_gameKey, suggestion.Code, suggestion.Description);
            await RefreshAsync();
        }
    }

    private async void OnChanged(object? sender, string gameKey)
    {
        if (gameKey == _gameKey)
        {
            await RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        if (_gameKey is null)
        {
            return;
        }

        var items = await service.GetAsync(_gameKey);
        var suggestions = await service.GetSuggestionsAsync(_gameKey);
        _collection = await service.GetCollectionCodesAsync(_gameKey, _title);
        HasCollections = (await service.GetCollectionsAsync()).Count > 0;
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(new CodeItemViewModel(this, item));
        }

        var known = items.Select(i => i.Code).ToHashSet(StringComparer.Ordinal);
        Suggestions.Clear();
        foreach (var suggestion in suggestions.Where(s => !known.Contains(s.Code)))
        {
            Suggestions.Add(new CodeSuggestionViewModel(this, suggestion));
        }

        Fill(CollectionCodes, _collection?.Codes, known);
        Fill(OnlineCodes, _online?.Codes, known);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(AnyEnabled));
        OnPropertyChanged(nameof(HasCollectionCodes));
        OnPropertyChanged(nameof(CollectionCredit));
        OnPropertyChanged(nameof(CollectionNotes));
        OnPropertyChanged(nameof(HasCollectionNotes));
        OnPropertyChanged(nameof(CollectionHeading));
        OnPropertyChanged(nameof(HasOnlineCodes));
        OnPropertyChanged(nameof(OnlineCredit));
        OnPropertyChanged(nameof(OnlineUrl));
    }

    /// <summary>Lists the codes the player hasn't added yet.</summary>
    private void Fill(ObservableCollection<CodeSuggestionViewModel> target, IReadOnlyList<CodeSuggestion>? codes, HashSet<string> known)
    {
        target.Clear();
        foreach (var code in codes ?? [])
        {
            if (!known.Contains(code.Code))
            {
                target.Add(new CodeSuggestionViewModel(this, code));
            }
        }
    }
}

/// <summary>One code in the list, with its on/off switch.</summary>
public sealed partial class CodeItemViewModel : ObservableObject
{
    private readonly CodesViewModel _owner;

    internal CodeItemViewModel(CodesViewModel owner, CodeItem item)
    {
        _owner = owner;
        Id = item.Id;
        Code = item.Code;
        Description = item.Description.Length > 0 ? item.Description : "(no description)";
        Detail = CheatCode.TryParseList(item.Code, out var codes, out _) ? CheatCode.Describe(codes) : string.Empty;
        _isEnabled = item.Enabled;
    }

    public string Id { get; }

    public string Code { get; }

    public string Description { get; }

    /// <summary>What the code changes, for players who want to know ("$91D9 = $AD").</summary>
    public string Detail { get; }

    private bool _isEnabled;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                _ = _owner.SetEnabledAsync(this, value);
            }
        }
    }

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(this);
}

/// <summary>A suggested code from the bundled database.</summary>
public sealed partial class CodeSuggestionViewModel(CodesViewModel owner, CodeSuggestion suggestion) : ObservableObject
{
    public string Code => suggestion.Code;

    public string Description => suggestion.Description;

    [RelayCommand]
    private Task UseAsync() => owner.UseSuggestionAsync(this);
}
