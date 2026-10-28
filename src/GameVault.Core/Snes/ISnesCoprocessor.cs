using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>
/// A cartridge's enhancement chip (outstanding-work plan §4): it answers part of the CPU's address space and runs
/// alongside the CPU. The console catches it up to the CPU's time before every access it answers, and at each frame's
/// end, so the chip is always exactly as far along as the CPU when they meet. Deterministic, like every core.
/// </summary>
public interface ISnesCoprocessor : IStateful
{
    /// <summary>The chip's name for messages ("DSP-1", "Super FX").</summary>
    string Name { get; }

    /// <summary>True when the chip answers <paramref name="address"/> (its registers, or the memory it owns).</summary>
    bool Claims(uint address);

    byte Read(uint address, byte openBus);

    void Write(uint address, byte value);

    /// <summary>Runs the chip up to <paramref name="clock"/> master clocks since the frame started.</summary>
    void CatchUp(long clock);

    /// <summary>The frame ended after <paramref name="frameClocks"/> master clocks: the chip's clock is counted from the next one.</summary>
    void EndFrame(long frameClocks);

    /// <summary>The chip's interrupt line to the CPU.</summary>
    bool Irq { get; }

    void Power();

    void Reset();
}

/// <summary>A game needs an enhancement chip whose program ROM the player supplies, and it isn't in the chip ROM folder.</summary>
public sealed class MissingChipRomException(string chip, IReadOnlyList<string> fileNames)
    : Exception($"This game needs the {chip} chip's program ROM. Put your own dump ({string.Join(" or ", fileNames)}) in the folder chosen in Settings → Super Nintendo → Enhancement chip ROMs. GameVault doesn't include it.")
{
    public string Chip { get; } = chip;

    public IReadOnlyList<string> FileNames { get; } = fileNames;
}

/// <summary>
/// A cartridge clock chip (snes-chips-3.md): it counts emulated time from the master clock, so save states and replays
/// see the same time. The host sets it to the PC's local time when a game starts for normal play; netplay and
/// challenge replays leave it at its fixed start (1 January 2000, midnight).
/// </summary>
public interface ISnesRealTimeClock
{
    void SetTime(DateTime time);
}
