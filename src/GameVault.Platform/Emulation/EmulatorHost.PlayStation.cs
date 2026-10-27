using GameVault.Core.PlayStation;
using GameVault.Library.Discs;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Emulation;

/// <summary>What a PlayStation needs besides the game: the player's BIOS from Settings, fast boot, and analog pads.</summary>
public sealed record PlayStationSetup(byte[]? Bios, bool FastBoot = false, bool AnalogPads = false);

/// <summary>PlayStation games (SPEC §7.11, Phase 15): the BIOS from Settings, and changing discs in multi-disc games.</summary>
public sealed partial class EmulatorHost
{
    private PlayStationSetup? _playStation;
    private IReadOnlyList<string> _discPaths = [];

    /// <summary>The discs of an .m3u game, by file name, for the Player's disc picker; empty for other games.</summary>
    public IReadOnlyList<string> DiscNames { get; private set; } = [];

    /// <summary>The player's BIOS and fast-boot setting, read when a PlayStation game is opened. Null without settings.</summary>
    private async Task<PlayStationSetup?> LoadPlayStationSetupAsync(CancellationToken cancellationToken)
    {
        if (_settings is null)
        {
            return null;
        }

        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.PlayStationBiosPath))
        {
            return new PlayStationSetup(null, settings.PlayStationFastBoot, settings.PlayStationAnalog);
        }

        try
        {
            var bios = await File.ReadAllBytesAsync(settings.PlayStationBiosPath, cancellationToken).ConfigureAwait(false);
            return new PlayStationSetup(bios, settings.PlayStationFastBoot, settings.PlayStationAnalog);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GameLoadException("GameVault couldn't open your PlayStation BIOS. Check the file chosen in Settings → PlayStation.", ex);
        }
    }

    /// <summary>Sets up the disc picker for a newly loaded game (UI thread, before it shows).</summary>
    private void SetUpDiscs(byte[] data, Core.Systems.GameSystem system)
    {
        _discPaths = system == Core.Systems.GameSystem.PlayStation && PlayStationGames.TryGetPath(data, out var path) && Path.GetExtension(path).Equals(".m3u", StringComparison.OrdinalIgnoreCase)
            ? PlayStationGames.DiscPaths(path)
            : [];
        DiscNames = _discPaths.Select(p => Path.GetFileNameWithoutExtension(p)).ToList();
    }

    /// <summary>Swaps the disc in a PlayStation's drive. Emulation thread.</summary>
    private void ChangePlayStationDisc(PlayStationConsole console, int index)
    {
        if (index < 0 || index >= _discPaths.Count)
        {
            return;
        }

        ICdImage disc;
        try
        {
            disc = DiscImages.Open(_discPaths[index]);
        }
        catch (InvalidDataException ex)
        {
            RaiseNotice($"GameVault couldn't open {DiscNames[index]}: {ex.Message}");
            return;
        }

        var old = console.CdRom.Disc;
        console.ChangeDisc(disc);
        (old as IDisposable)?.Dispose();
        _insertedDiskSide = index;
        _diskSideRequested = index;
        Raise(() => DiskChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Opens the drive's shell: the disc stops until another goes in. Emulation thread.</summary>
    private void OpenPlayStationShell(PlayStationConsole console)
    {
        var old = console.CdRom.Disc;
        console.CdRom.OpenShell();
        (old as IDisposable)?.Dispose();
        _insertedDiskSide = -1;
        Raise(() => DiskChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Closes the disc files of a console that is being replaced. Emulation thread.</summary>
    private static void ReleaseDiscs(Core.Systems.IEmulatedConsole? console)
    {
        if (console is PlayStationConsole ps)
        {
            (ps.CdRom.Disc as IDisposable)?.Dispose();
        }

        ReleasePs2Disc(console);
    }
}
