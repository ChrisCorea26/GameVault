using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>
/// Settings → Codes (SPEC §7.8): the code collections the player has imported, such as a spreadsheet of
/// published Game Genie codes. They stay in the player's own app folder; GameVault doesn't include any.
/// </summary>
public sealed partial class CodeCollectionsViewModel(ICheatService service, ICodeFilePicker picker) : ObservableObject
{
    public ObservableCollection<CodeCollectionItemViewModel> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool IsImporting { get; private set; }

    [RelayCommand]
    public async Task LoadAsync()
    {
        Items.Clear();
        foreach (var collection in await service.GetCollectionsAsync())
        {
            Items.Add(new CodeCollectionItemViewModel(this, collection));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool CanImport() => !IsImporting;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (await picker.PickCollectionFileAsync() is not { } path)
        {
            return;
        }

        IsImporting = true;
        try
        {
            var result = await service.ImportCollectionAsync(path);
            Message = result.Errors.Count > 0
                ? string.Join(" ", result.Errors)
                : $"Imported {result.Codes:N0} codes for {result.Games:N0} games from \"{result.Name}\". They appear under Codes for each game.";
        }
        finally
        {
            IsImporting = false;
        }

        await LoadAsync();
    }

    internal async Task RemoveAsync(CodeCollectionItemViewModel item)
    {
        await service.RemoveCollectionAsync(item.Name);
        Message = $"Removed \"{item.Name}\".";
        await LoadAsync();
    }
}

public sealed partial class CodeCollectionItemViewModel(CodeCollectionsViewModel owner, CodeCollectionInfo info) : ObservableObject
{
    public string Name => info.Name;

    public string Summary => $"{info.Codes:N0} codes for {info.Games:N0} games";

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAsync(this);
}
