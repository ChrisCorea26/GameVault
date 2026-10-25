using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>The Passwords panel (SPEC §7.3): saved passwords for one game, newest first, each with Copy.</summary>
public sealed partial class PasswordsViewModel(IPasswordService service, IClipboardService clipboard) : ObservableObject
{
    private string? _gameKey;
    private bool _attached;

    public ObservableCollection<PasswordItemViewModel> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty]
    public partial string? CopiedText { get; private set; }

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

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_gameKey is null)
        {
            return;
        }

        Items.Clear();
        foreach (var item in await service.GetAsync(_gameKey))
        {
            Items.Add(new PasswordItemViewModel(this, item));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    internal void Copy(PasswordItemViewModel item)
    {
        clipboard.SetText(item.Password);
        CopiedText = $"Copied {item.Password}";
    }

    internal Task DeleteAsync(PasswordItemViewModel item) =>
        _gameKey is { } key ? service.DeleteAsync(key, item.Id) : Task.CompletedTask;

    private async void OnChanged(object? sender, string gameKey)
    {
        if (gameKey == _gameKey)
        {
            await RefreshAsync();
        }
    }
}

public sealed partial class PasswordItemViewModel(PasswordsViewModel owner, PasswordItem item)
{
    public string Id => item.Id;

    public string Password => item.Password;

    public string Subtitle => string.Join(" · ", new[]
    {
        item.SavedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
        item.Automatic ? "Captured automatically" : "Entered by you",
        item.Note,
    }.Where(s => !string.IsNullOrEmpty(s)));

    public Uri? ScreenshotUri => item.ScreenshotPath is { Length: > 0 } path ? new Uri(path) : null;

    public bool HasScreenshot => ScreenshotUri is not null;

    [RelayCommand]
    private void Copy() => owner.Copy(this);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteAsync(this);
}
