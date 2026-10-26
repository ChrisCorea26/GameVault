using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>
/// The Player's Netplay flyout (SPEC §7.7, LAN-only): host the running game and share the invite code, or
/// join with a code from the other player. Both players need the same game file.
/// </summary>
public sealed partial class NetplayViewModel(IEmulatorService emulator, IClipboardService clipboard) : ObservableObject
{
    private bool _attached;

    public NetplayStatus Status => emulator.NetplayStatus;

    public bool IsActive => Status != NetplayStatus.Off;

    public bool IsIdle => !IsActive;

    public string? InviteCode => emulator.NetplayInviteCode;

    public bool HasInviteCode => InviteCode is not null && IsActive;

    public string StatusText => Status switch
    {
        NetplayStatus.Off when emulator.System == GameVault.Core.Systems.GameSystem.PlayStation2 =>
            "Netplay isn't available for PlayStation 2 games: the console is too large to rewind and replay every frame.",
        NetplayStatus.Off => "Play with someone on the same network. They need the same game file.",
        NetplayStatus.WaitingForPlayer => "Waiting for player 2. Give them this code; the game starts over when they join.",
        NetplayStatus.Connecting => "Connecting to the other player…",
        NetplayStatus.Playing => InviteCode is null ? "Connected. You are Player 2." : "Connected. You are Player 1.",
        NetplayStatus.Waiting => "Waiting for the other player to catch up…",
        NetplayStatus.ConnectionLost => "Lost the connection to the other player. Check the network, or leave and start again.",
        NetplayStatus.OutOfSync => "The two games stopped matching, so netplay stopped. Leave and start again.",
        NetplayStatus.DifferentGame => "The other player has a different game or version. You both need the same file.",
        _ => string.Empty,
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JoinCommand))]
    public partial string JoinCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    public void Attach()
    {
        if (!_attached)
        {
            emulator.NetplayChanged += OnChanged;
            _attached = true;
        }

        Refresh();
    }

    public void Detach()
    {
        if (_attached)
        {
            emulator.NetplayChanged -= OnChanged;
            _attached = false;
        }
    }

    [RelayCommand]
    private async Task HostAsync()
    {
        ErrorMessage = null;
        try
        {
            await emulator.HostNetplayAsync();
        }
        catch (NetplayException ex)
        {
            ErrorMessage = ex.Message;
        }

        Refresh();
    }

    private bool CanJoin() => !string.IsNullOrWhiteSpace(JoinCode);

    [RelayCommand(CanExecute = nameof(CanJoin))]
    private async Task JoinAsync()
    {
        ErrorMessage = null;
        try
        {
            await emulator.JoinNetplayAsync(JoinCode);
        }
        catch (NetplayException ex)
        {
            ErrorMessage = ex.Message;
        }

        Refresh();
    }

    [RelayCommand]
    private void Leave()
    {
        emulator.LeaveNetplay();
        Refresh();
    }

    [RelayCommand]
    private void CopyCode()
    {
        if (InviteCode is { } code)
        {
            clipboard.SetText(code);
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(InviteCode));
        OnPropertyChanged(nameof(HasInviteCode));
        OnPropertyChanged(nameof(StatusText));
    }
}
