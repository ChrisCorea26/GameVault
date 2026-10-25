using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>The Player's "Save states" flyout: ten slots with thumbnails (SPEC §4.6).</summary>
public sealed partial class SaveStatesViewModel(ISaveStateService service) : ObservableObject
{
    public ObservableCollection<SaveSlotViewModel> Slots { get; } = [];

    /// <summary>The slot F5 and F8 use.</summary>
    public int SelectedSlot
    {
        get => service.SelectedSlot;
        set
        {
            if (service.SelectedSlot == value)
            {
                return;
            }

            service.SelectedSlot = value;
            OnPropertyChanged();
            foreach (var slot in Slots)
            {
                slot.IsSelected = slot.Slot == value;
            }
        }
    }

    internal ISaveStateService Service => service;

    /// <summary>Reloads the slot list (when the flyout opens, and after a save).</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        var slots = await service.GetSlotsAsync();
        Slots.Clear();
        foreach (var info in slots)
        {
            Slots.Add(new SaveSlotViewModel(this, info) { IsSelected = info.Slot == SelectedSlot });
        }
    }

    internal async Task SaveAsync(int slot)
    {
        SelectedSlot = slot;
        await service.SaveAsync(slot);
        await RefreshAsync();
    }

    internal async Task LoadAsync(int slot)
    {
        SelectedSlot = slot;
        await service.LoadAsync(slot);
    }
}

public sealed partial class SaveSlotViewModel(SaveStatesViewModel owner, SaveSlotInfo info) : ObservableObject
{
    public int Slot => info.Slot;

    public string Title => $"Slot {info.Slot + 1}";

    public string Subtitle => info.SavedAt is { } at
        ? at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "Empty";

    public string? ThumbnailPath => info.ThumbnailPath;

    /// <summary>The thumbnail as a file URI for an image source.</summary>
    public Uri? ThumbnailUri => info.ThumbnailPath is { } path ? new Uri(path) : null;

    public bool HasThumbnail => info.ThumbnailPath is not null;

    public bool IsEmpty => info.IsEmpty;

    public bool CanLoad => !info.IsEmpty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private Task SaveAsync() => owner.SaveAsync(info.Slot);

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task LoadAsync() => owner.LoadAsync(info.Slot);
}
