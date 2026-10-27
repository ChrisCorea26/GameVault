using GameVault.Core.Nintendo64;
using GameVault.Core.Systems;
using GameVault.Platform.Video;

namespace GameVault.Platform.Emulation;

/// <summary>
/// What an N64 needs besides the game: the Expansion Pak setting, the save-type list (data/gamedb/n64/saves.json),
/// the Rumble Pak, and the GPU renderer's internal resolution (1: the software picture).
/// </summary>
public sealed record N64Setup(bool ExpansionPak = true, Func<string, N64SaveType?>? SaveTypes = null, bool RumblePak = false, int InternalResolution = 1);

/// <summary>N64 games (SPEC §7.11, Phase 16): the Expansion Pak from Settings, and the Controller Pak's save file.</summary>
public sealed partial class EmulatorHost
{
    private N64Setup? _n64;

    /// <summary>The GPU renderer drawing the running N64's picture at a higher resolution; null at 1×.</summary>
    private N64RdpGpuRenderer? _n64Renderer;

    /// <summary>Where the GPU renderers' compiled kernels are kept (null: D3D11Compute's default).</summary>
    private readonly string? _shaderCache;

    /// <summary>Counts GPU renderer attaches, so one that finishes after its game was replaced is dropped.</summary>
    private int _gpuAttach;

    // The Controller Pak is saved apart from the cartridge, as a raw .mpk image (the format other emulators read).
    private N64ControllerPak? _pak;
    private byte[] _pakSaved = [];

    /// <summary>The save type for a game code (the app sets it from data/gamedb/n64/saves.json).</summary>
    public Func<string, N64SaveType?>? N64SaveTypes { get; set; }

    /// <summary>The Expansion Pak setting, read when an N64 game is opened.</summary>
    private async Task<N64Setup> LoadN64SetupAsync(CancellationToken cancellationToken)
    {
        var settings = _settings is null ? null : await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new N64Setup(settings?.N64ExpansionPak ?? true, N64SaveTypes, settings?.N64RumblePak ?? false, settings?.N64InternalResolution ?? 1);
    }

    /// <summary>
    /// Attaches the GPU renderer to an N64 that has just started, when Settings asks for more than 1×
    /// (docs/plans/gpu-renderers.md, milestone 2). The software RDP still draws RDRAM. The renderer is made on a
    /// background thread (the first start after an update compiles its kernel, which takes a while) and attached
    /// between frames when it's ready; until then, and without Direct3D 11, the software picture shows. Emulation
    /// thread.
    /// </summary>
    private void AttachN64Renderer(IEmulatedConsole console)
    {
        var scale = Math.Clamp(_n64?.InternalResolution ?? 1, 1, N64RdpGpuRenderer.MaxScale);
        if (console is not N64Console n64 || scale <= 1 || !OperatingSystem.IsWindows())
        {
            return;
        }

        AttachInBackground(
            gpu => new N64RdpGpuRenderer(gpu, n64.Rdram, scale, ownsGpu: true),
            renderer =>
            {
                _n64Renderer = renderer;
                n64.Rdp.Backend = renderer;
            },
            console,
            "Direct3D 11 isn't available, so the N64 is drawn at its own resolution.");
    }

    /// <summary>
    /// Makes a GPU renderer on a background thread and hands it to <paramref name="attach"/> on the emulation thread,
    /// unless <paramref name="console"/> has been replaced since (then it's disposed).
    /// </summary>
    private void AttachInBackground<T>(Func<D3D11Compute, T> create, Action<T> attach, IEmulatedConsole console, string unavailable)
        where T : class, IDisposable
    {
        var attempt = ++_gpuAttach;
        var cache = _shaderCache;
        _ = Task.Run(() =>
        {
            D3D11Compute? gpu = null;
            T renderer;
            try
            {
                gpu = new D3D11Compute(cacheDirectory: cache);
                renderer = create(gpu);
            }
            catch (Exception)
            {
                // Any failure here (no Direct3D 11, a driver's compiler error) leaves the software picture; this runs
                // on a pool thread, so nothing else would see it.
                gpu?.Dispose();
                RaiseNotice(unavailable);
                return;
            }

            Post(() =>
            {
                if (attempt != _gpuAttach || !ReferenceEquals(_console, console))
                {
                    renderer.Dispose();
                    return;
                }

                attach(renderer);
            });
        });
    }

    /// <summary>The N64 GPU renderer's picture of what the VI shows, if it has one this frame.</summary>
    private bool TryPresentN64Picture(IEmulatedConsole console)
    {
        if (_n64Renderer is null || console is not N64Console n64)
        {
            return false;
        }

        var picture = _n64Renderer.ReadDisplay(n64.VideoOutput, out var width, out var height);
        if (picture.IsEmpty)
        {
            return false;
        }

        _video.PresentArgb(picture, width, height, console.PixelAspect);
        return true;
    }

    /// <summary>Detaches and disposes the N64 GPU renderer of a console that is being replaced. Emulation thread.</summary>
    private void ReleaseN64Renderer(IEmulatedConsole? console)
    {
        _gpuAttach++; // one still being made is dropped when it's ready
        if (console is N64Console n64)
        {
            n64.Rdp.Backend = null;
        }

        _n64Renderer?.Dispose();
        _n64Renderer = null;
    }

    /// <summary>Reads the game's Controller Pak, if it has been saved before (UI thread, while loading).</summary>
    private void LoadControllerPak(IEmulatedConsole console)
    {
        if (console is not N64Console n64 || _controllerPaks is null)
        {
            return;
        }

        try
        {
            _controllerPaks.TryLoad(console.RomHash, n64.ControllerPak.Memory);
        }
        catch (IOException)
        {
            RaiseNotice("GameVault couldn't read this game's Controller Pak, so it starts empty.");
        }
    }

    /// <summary>Saves the Controller Pak when the game changed it. Emulation thread.</summary>
    private void FlushControllerPak(bool background)
    {
        if (_pak is null || _controllerPaks is not { } store || _pak.Memory.AsSpan().SequenceEqual(_pakSaved) || _console is null)
        {
            return;
        }

        var data = _pak.Memory.ToArray();
        _pakSaved = data;
        var title = _batteryTitle;
        var hash = _console.RomHash;

        void Write()
        {
            try
            {
                store.Save(title, hash, data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RaiseNotice("GameVault couldn't save this game's Controller Pak. " + ex.Message);
            }
        }

        if (background)
        {
            _ = Task.Run(Write);
        }
        else
        {
            Write();
        }
    }

    /// <summary>The Controller Pak to watch for changes, once the game is running. Emulation thread.</summary>
    private void TrackControllerPak(IEmulatedConsole? console)
    {
        _pak = (console as N64Console)?.ControllerPak;
        _pakSaved = _pak?.Memory.ToArray() ?? [];
    }

    private static bool IsNintendo64(GameSystem system) => system == GameSystem.Nintendo64;
}
