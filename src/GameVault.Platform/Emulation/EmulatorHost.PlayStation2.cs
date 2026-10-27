using GameVault.Core.PlayStation2;
using GameVault.Core.Systems;
using GameVault.Platform.Storage;

namespace GameVault.Platform.Emulation;

/// <summary>
/// PlayStation 2 games (SPEC §7.11, Phase 17): the BIOS from Settings, the game's memory card, and the budgets that keep
/// rewind, run-ahead and netplay within a frame (<see cref="SystemBudgets"/>).
/// </summary>
public sealed partial class EmulatorHost
{
    private PlayStation2Setup? _playStation2;

    // One 8 MB card per game in slot 1, saved as a raw .ps2 image (Ps2MemoryCardStore).
    private Ps2MemoryCardStore? _ps2Cards;
    private Ps2MemoryCard? _ps2Card;
    private byte[] _ps2CardSaved = [];

    // Slot 2: one card shared by every game.
    private Ps2MemoryCard? _ps2Card2;
    private byte[] _ps2Card2Saved = [];

    /// <summary>The player's PS2 BIOS and fast-boot setting, read when a PS2 game is opened. Null without settings.</summary>
    private async Task<PlayStation2Setup?> LoadPlayStation2SetupAsync(CancellationToken cancellationToken) =>
        _settings is null
            ? null
            : await PlayStation2Setup.LoadAsync(await _settings.LoadAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Puts the game's saved memory card in slot 1 (UI thread, while loading). Without one the console keeps the
    /// formatted card <see cref="ConsoleFactory"/> gave it, which is saved once the game writes to it.
    /// </summary>
    private void LoadPs2MemoryCard(IEmulatedConsole console)
    {
        if (console is not Ps2Console ps2 || _ps2Cards is null)
        {
            return;
        }

        try
        {
            if (_ps2Cards.TryLoad(console.RomHash) is { } saved)
            {
                saved.CopyTo(ps2.MemoryCard.Data, 0);
                ps2.MemoryCard.Dirty = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RaiseNotice("GameVault couldn't read this game's memory card, so the game starts with an empty one. " + ex.Message);
        }

        if (ps2.MemoryCard2 is not { } card2)
        {
            return;
        }

        try
        {
            if (_ps2Cards.TryLoadShared() is { } shared)
            {
                shared.CopyTo(card2.Data, 0);
                card2.Dirty = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ps2.MemoryCard2 = null; // leave the file alone: slot 2 stays empty this time
            RaiseNotice("GameVault couldn't read the memory card in slot 2, so slot 2 is empty for now. " + ex.Message);
        }
    }

    /// <summary>The memory card to watch for changes, once the game is running. Emulation thread.</summary>
    private void TrackPs2MemoryCard(IEmulatedConsole? console)
    {
        _ps2Card = (console as Ps2Console)?.MemoryCard;
        _ps2CardSaved = _ps2Card?.Data.ToArray() ?? [];
        _ps2Card2 = (console as Ps2Console)?.MemoryCard2;
        _ps2Card2Saved = _ps2Card2?.Data.ToArray() ?? [];
    }

    /// <summary>
    /// Saves the memory card when the game wrote to it, or a save state or rewind brought back another card, as for the
    /// PlayStation's cards. Emulation thread.
    /// </summary>
    private void FlushPs2MemoryCard(bool background)
    {
        FlushPs2SharedCard(background);
        if (_ps2Card is not { } card || _ps2Cards is not { } store || _console is null
            || (!card.Dirty && card.Data.AsSpan().SequenceEqual(_ps2CardSaved)))
        {
            return;
        }

        card.Dirty = false;
        var data = card.Data.ToArray();
        _ps2CardSaved = data;
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
                RaiseNotice("GameVault couldn't save this game's memory card. " + ex.Message);
            }
        }

        if (background)
        {
            _ = Task.Run(Write); // keep disk writes off the frame loop
        }
        else
        {
            Write();
        }
    }

    /// <summary>Saves slot 2's shared card when the game wrote to it. Emulation thread.</summary>
    private void FlushPs2SharedCard(bool background)
    {
        if (_ps2Card2 is not { } card || _ps2Cards is not { } store
            || (!card.Dirty && card.Data.AsSpan().SequenceEqual(_ps2Card2Saved)))
        {
            return;
        }

        card.Dirty = false;
        var data = card.Data.ToArray();
        _ps2Card2Saved = data;

        void Write()
        {
            try
            {
                store.SaveShared(data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RaiseNotice("GameVault couldn't save the memory card in slot 2. " + ex.Message);
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

    /// <summary>The rewind history (tests: its capture interval and size). Emulation thread.</summary>
    internal RewindBuffer RewindHistory => _rewind;

    /// <summary>Run-ahead (tests: whether the running system allows it). Emulation thread.</summary>
    internal RunAhead RunAheadState => _runAhead;

    /// <summary>Applies the system's rewind and run-ahead budgets to a game that is starting. Emulation thread.</summary>
    private void ApplySystemBudgets(GameSystem system)
    {
        _rewind.Clear();
        _rewind.CaptureInterval = SystemBudgets.RewindInterval(system);
        _rewind.CompressInBackground = SystemBudgets.CompressRewindInBackground(system);
        _runAhead.Allowed = SystemBudgets.AllowsRunAhead(system);
    }

    /// <summary>Closes the disc of a PS2 console that is being replaced. Emulation thread.</summary>
    private static void ReleasePs2Disc(IEmulatedConsole? console)
    {
        if (console is Ps2Console ps2)
        {
            (ps2.Iop.Disc as IDisposable)?.Dispose();
        }
    }
}
