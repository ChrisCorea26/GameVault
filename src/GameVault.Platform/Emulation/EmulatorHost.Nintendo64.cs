using GameVault.Core.Nintendo64;
using GameVault.Core.Systems;

namespace GameVault.Platform.Emulation;

/// <summary>
/// What an N64 needs besides the game: the Expansion Pak setting and the save-type list (data/gamedb/n64/saves.json).
/// </summary>
public sealed record N64Setup(bool ExpansionPak = true, Func<string, N64SaveType?>? SaveTypes = null);

/// <summary>N64 games (SPEC §7.11, Phase 16): the Expansion Pak from Settings, and the Controller Pak's save file.</summary>
public sealed partial class EmulatorHost
{
    private N64Setup? _n64;

    // The Controller Pak is saved apart from the cartridge, as a raw .mpk image (the format other emulators read).
    private N64ControllerPak? _pak;
    private byte[] _pakSaved = [];

    /// <summary>The save type for a game code (the app sets it from data/gamedb/n64/saves.json).</summary>
    public Func<string, N64SaveType?>? N64SaveTypes { get; set; }

    /// <summary>The Expansion Pak setting, read when an N64 game is opened.</summary>
    private async Task<N64Setup> LoadN64SetupAsync(CancellationToken cancellationToken)
    {
        var expansionPak = _settings is null || (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).N64ExpansionPak;
        return new N64Setup(expansionPak, N64SaveTypes);
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
