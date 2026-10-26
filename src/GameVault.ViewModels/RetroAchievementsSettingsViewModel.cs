using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>Settings → RetroAchievements (SPEC §7.10): sign in and out, and hardcore mode.</summary>
public sealed partial class RetroAchievementsSettingsViewModel : ObservableObject
{
    private readonly IAchievementsService _service;
    private bool _updating;

    public RetroAchievementsSettingsViewModel(IAchievementsService service)
    {
        _service = service;
        Refresh();
    }

    public bool IsAvailable => _service.IsAvailable;

    public bool IsUnavailable => !_service.IsAvailable;

    public bool IsSignedIn => _service.IsSignedIn;

    public bool IsSignedOut => _service.IsAvailable && !_service.IsSignedIn;

    /// <summary>"Signed in as Player · 1,234 points".</summary>
    public string SignedInText => _service.User is { } user
        ? string.Create(CultureInfo.CurrentCulture, $"Signed in as {user.DisplayName} · {user.Score:N0} points")
        : string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string UserName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Hardcore { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Message { get; private set; }

    public void Attach() => _service.Changed += OnChanged;

    public void Detach() => _service.Changed -= OnChanged;

    private bool CanSignIn() => !IsBusy && UserName.Trim().Length > 0 && Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        IsBusy = true;
        Message = null;
        try
        {
            await _service.SignInAsync(UserName, Password);
            Password = string.Empty;
        }
        catch (AchievementsException ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void SignOut()
    {
        _service.SignOut();
        Message = null;
        Refresh();
    }

    partial void OnHardcoreChanged(bool value)
    {
        if (!_updating && value != _service.Hardcore)
        {
            _ = _service.SetHardcoreAsync(value);
            Message = value
                ? "Hardcore is on. A running game restarts, and loading states, rewind, codes, slow motion and netplay are off."
                : "Hardcore is off. Achievements you earn now count as softcore.";
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        _updating = true;
        Hardcore = _service.Hardcore;
        _updating = false;
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(SignedInText));
    }
}
