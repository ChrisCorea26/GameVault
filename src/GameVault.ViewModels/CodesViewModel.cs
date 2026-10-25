using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.Core.Cheats;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>
/// The Codes list (SPEC §7.8), on Game Details and in the Player's Codes flyout: the player's Game Genie and
/// RAM codes for one game, each with an on/off switch, an add box that checks codes as they are typed,
/// import/export, and suggestions from the bundled database.
/// </summary>
public sealed partial class CodesViewModel(ICheatService service, ICodeFilePicker picker) : ObservableObject
{
    private string? _gameKey;
    private bool _attached;

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

    public async Task ShowAsync(string gameKey)
    {
        _gameKey = gameKey;
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

        IsNewCodeValid = CheatCode.TryParse(value, out var code, out var error);
        Validation = IsNewCodeValid ? "Decodes to " + code.Describe() : error;
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
        if (_gameKey is not null)
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

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(AnyEnabled));
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
        Detail = CheatCode.TryParse(item.Code, out var code, out _) ? code.Describe() : string.Empty;
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
