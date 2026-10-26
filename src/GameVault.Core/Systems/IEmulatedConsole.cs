using GameVault.Core.Audio;
using GameVault.Core.Cartridge;
using GameVault.Core.Input;
using GameVault.Core.State;

namespace GameVault.Core.Systems;

/// <summary>The systems GameVault emulates (SPEC §7.11).</summary>
public enum GameSystem
{
    Nes,
    Sg1000,
    MasterSystem,
}

/// <summary>
/// What the platform layer needs from any emulated console (SPEC §7.11): frames, audio, input, memory reads
/// and save states. The frame loop, rewind, run-ahead, netplay, challenges, memory maps and achievements all
/// work through this; system-specific features check for the concrete console. Deterministic, like every core.
/// </summary>
public interface IEmulatedConsole : IStateful
{
    public const int ScreenWidth = 256;
    public const int ScreenHeight = 240;

    GameSystem System { get; }

    /// <summary>Identifies the game a save state belongs to (not a security hash).</summary>
    ulong RomHash { get; }

    /// <summary>
    /// The finished frame: 256×240 indices into <see cref="Palette"/>. Systems with a shorter picture
    /// centre it and fill the rest with the border colour, as a TV shows it.
    /// </summary>
    ReadOnlySpan<ushort> FrameBuffer { get; }

    /// <summary>ARGB colours for <see cref="FrameBuffer"/> indices (the NES uses the player's palette preset instead).</summary>
    ReadOnlySpan<uint> Palette { get; }

    /// <summary>Frames completed since power-on.</summary>
    long FrameCount { get; }

    /// <summary>The clock the audio is produced at, in Hz.</summary>
    double CpuClockRate { get; }

    /// <summary>Resampled audio, once <see cref="EnableAudio"/> has been called.</summary>
    BandLimitedResampler? Audio { get; }

    BandLimitedResampler EnableAudio(int sampleRate = 48000);

    /// <summary>Controller ports the console reads (players beyond this are ignored).</summary>
    int ControllerPorts { get; }

    /// <summary>The cartridge's battery-backed RAM, or null when it has none.</summary>
    IBatteryBacked? Battery { get; }

    void Power();

    /// <summary>The console's reset button (on systems without one, a power cycle that keeps cartridge RAM).</summary>
    void Reset();

    void RunFrame();

    /// <summary>Runs a frame without audio output (run-ahead's hidden frames).</summary>
    void RunFrameSilently();

    /// <summary>
    /// A player's buttons, in the NES pad's layout: A is the system's main button (Button 2 on Sega pads),
    /// B the other (Button 1), Start is Pause on the Master System.
    /// </summary>
    void SetControllerState(int port, ControllerState state);

    /// <summary>Reads memory as the CPU sees it, without side effects (memory maps and achievements).</summary>
    byte PeekMemory(int address);

    void SaveSnapshot(StateWriter writer);

    byte[] SaveSnapshot();

    /// <exception cref="StateFormatException">The data is corrupt or belongs to a different game.</exception>
    void LoadSnapshot(ReadOnlyMemory<byte> data);
}
