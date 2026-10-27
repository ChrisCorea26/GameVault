using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.Library.Family;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>One profile in the title bar's switcher and in Settings → Family.</summary>
public sealed partial class FamilyProfileItem(FamilyProfile profile, bool isCurrent, FamilyViewModel owner) : ObservableObject
{
    public FamilyProfile Profile { get; } = profile;

    public string Name => Profile.Name;

    public string Icon => Profile.Icon;

    public bool IsCurrent { get; } = isCurrent;

    public bool CanRemove => Profile.Id != FamilyProfile.MainId && !IsCurrent;

    public bool CanSwitch => !IsCurrent;

    /// <summary>"1 hour a day · Settings locked".</summary>
    public string Details => string.Join(" · ", new[]
    {
        Profile.DailyLimitMinutes is { } minutes ? $"{FamilyViewModel.Minutes(minutes)} a day" : "No daily limit",
        Profile.CanChangeSettings ? null : "Settings locked",
        IsCurrent ? "Playing now" : null,
    }.Where(s => s is not null));

    [RelayCommand]
    private void Switch() => owner.SwitchTo(this);

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAsync(this);
}

/// <summary>
/// Family profiles (SPEC §7.9): the title bar's switcher, and Settings → Family, where a parent adds profiles,
/// sets daily limits and the PIN. Changing anything here needs the PIN once one is set.
/// </summary>
public sealed partial class FamilyViewModel : ObservableObject
{
    private readonly IFamilyService _family;
    private readonly IConfirmationService _confirm;

    public FamilyViewModel(IFamilyService family, IConfirmationService? confirm = null)
    {
        _family = family;
        _confirm = confirm ?? FixedConfirmation.Yes;
        Refresh();
    }

    public ObservableCollection<FamilyProfileItem> Profiles { get; } = [];

    public string CurrentName => _family.Current.Name;

    public string CurrentIcon => _family.Current.Icon;

    /// <summary>The switcher only shows when there's someone to switch to.</summary>
    public bool HasOtherProfiles => _family.Profiles.Count > 1;

    public bool HasPin => _family.HasPin;

    /// <summary>True when Settings can be changed: the profile allows it, or a parent entered the PIN.</summary>
    public bool CanChangeSettings => _family.Current.CanChangeSettings || IsUnlocked;

    public bool SettingsLocked => !CanChangeSettings;

    /// <summary>Profiles and limits can be edited: no PIN yet, or the PIN was entered this session.</summary>
    public bool CanManage => !_family.HasPin || IsUnlocked;

    public bool ManageLocked => !CanManage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeSettings), nameof(SettingsLocked), nameof(CanManage), nameof(ManageLocked))]
    public partial bool IsUnlocked { get; private set; }

    /// <summary>The PIN typed to unlock.</summary>
    [ObservableProperty]
    public partial string PinEntry { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewName { get; set; } = string.Empty;

    /// <summary>Minutes a day for the new profile; empty for no limit.</summary>
    [ObservableProperty]
    public partial string NewLimitMinutes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool NewCanChangeSettings { get; set; }

    [ObservableProperty]
    public partial string NewPin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [RelayCommand]
    private void Unlock()
    {
        if (_family.VerifyPin(PinEntry))
        {
            IsUnlocked = true;
            Message = null;
        }
        else
        {
            Message = "That PIN isn't right.";
        }

        PinEntry = string.Empty;
    }

    [RelayCommand]
    private void Lock() => IsUnlocked = false;

    [RelayCommand]
    private void AddProfile()
    {
        if (!CanManage)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(NewName))
        {
            Message = "Give the profile a name.";
            return;
        }

        int? limit = null;
        if (!string.IsNullOrWhiteSpace(NewLimitMinutes))
        {
            if (!int.TryParse(NewLimitMinutes, NumberStyles.Integer, CultureInfo.CurrentCulture, out var minutes) || minutes is < 5 or > 24 * 60)
            {
                Message = "A daily limit is 5 to 1440 minutes, or leave it empty for none.";
                return;
            }

            limit = minutes;
        }

        var added = _family.Add(NewName, limit, NewCanChangeSettings);
        Message = $"Added {added.Name}. Switch to them from the title bar; they start with an empty library.";
        NewName = string.Empty;
        NewLimitMinutes = string.Empty;
        NewCanChangeSettings = false;
        Refresh();
    }

    [RelayCommand]
    private void SetPin()
    {
        if (!CanManage)
        {
            return;
        }

        if (!FamilyStore.IsValidPin(NewPin))
        {
            Message = "A PIN is 4 to 12 digits.";
            return;
        }

        _family.SetPin(NewPin);
        NewPin = string.Empty;
        IsUnlocked = true; // whoever just set it can keep editing
        Message = "PIN set. It's needed to change profiles and limits, and to keep playing past a limit.";
        Refresh();
    }

    [RelayCommand]
    private async Task RemovePinAsync()
    {
        if (CanManage && _family.HasPin
            && await _confirm.ConfirmAsync(
                "Remove the PIN?",
                "Anyone will be able to change profiles and limits, and to keep playing past a daily limit.",
                "Remove PIN"))
        {
            _family.SetPin(null);
            Message = "PIN removed.";
            Refresh();
        }
    }

    internal void SwitchTo(FamilyProfileItem item)
    {
        if (!item.IsCurrent)
        {
            _family.SwitchTo(item.Profile.Id);
            IsUnlocked = false;
            Refresh();
        }
    }

    internal async Task RemoveAsync(FamilyProfileItem item)
    {
        if (!CanManage || !item.CanRemove
            || !await _confirm.ConfirmAsync(
                $"Remove {item.Name}?",
                $"{item.Name} no longer appears in the profile list. Their games and saves are kept in GameVault's folder.",
                "Remove"))
        {
            return;
        }

        _family.Remove(item.Profile.Id);
        Message = $"Removed {item.Name}. Their games and saves are kept in GameVault's folder.";
        Refresh();
    }

    private void Refresh()
    {
        Profiles.Clear();
        foreach (var profile in _family.Profiles)
        {
            Profiles.Add(new FamilyProfileItem(profile, profile.Id == _family.Current.Id, this));
        }

        OnPropertyChanged(nameof(CurrentName));
        OnPropertyChanged(nameof(CurrentIcon));
        OnPropertyChanged(nameof(HasOtherProfiles));
        OnPropertyChanged(nameof(HasPin));
        OnPropertyChanged(nameof(CanChangeSettings));
        OnPropertyChanged(nameof(SettingsLocked));
        OnPropertyChanged(nameof(CanManage));
        OnPropertyChanged(nameof(ManageLocked));
    }

    /// <summary>"45 minutes", "1 hour", "1 hour 30 minutes".</summary>
    public static string Minutes(int minutes)
    {
        var hours = minutes / 60;
        var rest = minutes % 60;
        var h = hours switch { 0 => null, 1 => "1 hour", _ => $"{hours} hours" };
        var m = rest switch { 0 => null, 1 => "1 minute", _ => $"{rest} minutes" };
        return string.Join(" ", new[] { h, m }.Where(s => s is not null)) is { Length: > 0 } text ? text : "0 minutes";
    }
}
