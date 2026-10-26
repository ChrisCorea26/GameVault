using GameVault.Core.Systems;

namespace GameVault.Platform.Emulation;

/// <summary>
/// Per-system limits on the features that save or load states all the time: rewind, run-ahead and netplay's
/// rollback (docs/design/host-budgets.md). Every system but the PlayStation 2 uses the defaults. A PS2 state is about
/// 49 MB (32 MB of RDRAM, the 8.6 MB memory card inside the IOP's state, GS memory, IOP RAM, sound RAM, the VU
/// memories): saving it into a reused writer takes about 6 ms and loading it about 7 ms, and compressing it for the
/// rewind history 60–110 ms, measured on the homebrew ELFs (tests/GameVault.Platform.Tests/PlayStation2HostTests.cs).
/// </summary>
public static class SystemBudgets
{
    /// <summary>The PS2's rewind capture interval: every 30 frames, about every half second.</summary>
    public const int PlayStation2RewindInterval = 30;

    /// <summary>The message for a netplay attempt with a PS2 game.</summary>
    public const string PlayStation2NetplayMessage =
        "Netplay isn't available for PlayStation 2 games: its rollback saves and loads the whole console every frame, and a PlayStation 2 is too large for that.";

    /// <summary>Frames between rewind captures.</summary>
    public static int RewindInterval(GameSystem system) =>
        system == GameSystem.PlayStation2 ? PlayStation2RewindInterval : RewindBuffer.Interval;

    /// <summary>True when rewind compresses its captures on a worker thread (only the snapshot is taken between frames).</summary>
    public static bool CompressRewindInBackground(GameSystem system) => system == GameSystem.PlayStation2;

    /// <summary>
    /// False when run-ahead would save, run and load a state every frame beyond the frame's budget: a PS2 frame with
    /// one frame of run-ahead takes about 28 ms on the homebrew, more than the 16.6 ms of a frame.
    /// </summary>
    public static bool AllowsRunAhead(GameSystem system) => system != GameSystem.PlayStation2;

    /// <summary>False when netplay's rollback (a save every frame, a load and replays on each misprediction) can't keep up.</summary>
    public static bool AllowsNetplay(GameSystem system) => system != GameSystem.PlayStation2;
}
