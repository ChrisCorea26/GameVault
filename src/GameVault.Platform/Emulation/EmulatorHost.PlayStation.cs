using GameVault.Core.PlayStation;
using GameVault.Library.Discs;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Emulation;

/// <summary>
/// What a PlayStation needs besides the game: the player's BIOS from Settings, fast boot, analog pads, and the GPU
/// renderer's internal resolution (1: the software picture).
/// </summary>
public sealed record PlayStationSetup(byte[]? Bios, bool FastBoot = false, bool AnalogPads = false, int InternalResolution = 1);

/// <summary>PlayStation games (SPEC §7.11, Phase 15): the BIOS from Settings, and changing discs in multi-disc games.</summary>
public sealed partial class EmulatorHost
{
    private PlayStationSetup? _playStation;
    private IReadOnlyList<string> _discPaths = [];

    /// <summary>The Direct3D renderer drawing the running PlayStation's picture at a higher resolution; null at 1×.</summary>
    private D3D11PsGpuRenderer? _psRenderer;

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
            return new PlayStationSetup(null, settings.PlayStationFastBoot, settings.PlayStationAnalog, settings.PlayStationInternalResolution);
        }

        try
        {
            var bios = await File.ReadAllBytesAsync(settings.PlayStationBiosPath, cancellationToken).ConfigureAwait(false);
            return new PlayStationSetup(bios, settings.PlayStationFastBoot, settings.PlayStationAnalog, settings.PlayStationInternalResolution);
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

    /// <summary>
    /// Attaches the GPU renderer to a PlayStation that has just started, when Settings asks for more than 1×
    /// (docs/plans/gpu-renderers.md). Without Direct3D 11 the software picture stays. Emulation thread.
    /// </summary>
    private void AttachGpuRenderer(Core.Systems.IEmulatedConsole console)
    {
        var scale = Math.Clamp(_playStation?.InternalResolution ?? 1, 1, D3D11PsGpuRenderer.MaxScale);
        if (console is not PlayStationConsole ps || scale <= 1 || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _psRenderer = new D3D11PsGpuRenderer(ps.Gpu, scale);
            ps.Gpu.Backend = _psRenderer;
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or DllNotFoundException or InvalidOperationException)
        {
            _psRenderer = null;
            RaiseNotice("Direct3D 11 isn't available, so the PlayStation is drawn at its own resolution.");
        }
    }

    /// <summary>The GPU renderer's picture, if one is attached and drawing this frame (not 24-bit video).</summary>
    private bool TryPresentGpuPicture(Core.Systems.IEmulatedConsole console)
    {
        if (_psRenderer is null || console is not PlayStationConsole)
        {
            return false;
        }

        var picture = _psRenderer.ReadDisplay(out var width, out var height);
        if (picture.IsEmpty)
        {
            return false;
        }

        _video.PresentArgb(picture, width, height, console.PixelAspect);
        return true;
    }

    /// <summary>
    /// Closes the disc files of a console that is being replaced (PlayStation, PlayStation 2, Sega CD), and its GPU
    /// renderer. Emulation thread.
    /// </summary>
    private void ReleaseDiscs(Core.Systems.IEmulatedConsole? console)
    {
        if (console is PlayStationConsole ps)
        {
            (ps.CdRom.Disc as IDisposable)?.Dispose();
            ps.Gpu.Backend = null;
        }

        if (console is Core.Genesis.GenesisConsole { SegaCd: { } segaCd })
        {
            (segaCd.Drive.Disc as IDisposable)?.Dispose();
        }

        _psRenderer?.Dispose();
        _psRenderer = null;

        ReleasePs2Disc(console);
    }
}
