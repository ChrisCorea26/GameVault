using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>Settings → Library (SPEC §9.2): the folders GameVault looks in, and a manual rescan.</summary>
public sealed partial class LibrarySettingsViewModel(ILibraryService library, IGameFilePicker picker) : ObservableObject
{
    public ObservableCollection<LibraryFolderViewModel> Folders { get; } = [];

    [ObservableProperty]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    public bool HasNoFolders => Folders.Count == 0;

    [RelayCommand]
    private async Task LoadAsync()
    {
        Folders.Clear();
        foreach (var folder in await library.GetFoldersAsync())
        {
            Folders.Add(new LibraryFolderViewModel(this, folder));
        }

        OnPropertyChanged(nameof(HasNoFolders));
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (await picker.PickFolderAsync() is not { } path)
        {
            return;
        }

        IsScanning = true;
        Status = "Looking for games…";
        try
        {
            var result = await library.AddFolderAsync(path);
            Status = $"Found {result.Total} games";
        }
        finally
        {
            IsScanning = false;
        }

        await LoadAsync();
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        IsScanning = true;
        Status = "Looking for games…";
        try
        {
            var result = await library.ScanAsync();
            Status = $"{result.Total} games · {result.Added} new · {result.Removed} removed";
        }
        finally
        {
            IsScanning = false;
        }
    }

    internal async Task RemoveAsync(LibraryFolderViewModel folder)
    {
        await library.RemoveFolderAsync(folder.Path);
        Folders.Remove(folder);
        OnPropertyChanged(nameof(HasNoFolders));
    }
}

public sealed partial class LibraryFolderViewModel(LibrarySettingsViewModel owner, string path)
{
    public string Path => path;

    /// <summary>Removing a folder forgets its files but keeps ownership, notes and playtime.</summary>
    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAsync(this);
}
