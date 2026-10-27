using GameVault.Core.Cartridge;
using GameVault.Core.State;

namespace GameVault.Core.Atari;

/// <summary>How a 5200 cartridge's ROM fills the 32 KB window at $4000–$BFFF.</summary>
public enum Atari5200Board
{
    /// <summary>One chip of 2, 4, 8, 16 or 32 KB, repeated across the window.</summary>
    Standard,

    /// <summary>16 KB in two 8 KB chips: the first at $4000–$7FFF, the second at $8000–$BFFF (each shown twice).</summary>
    TwoChip16K,
}

/// <summary>
/// A 5200 cartridge (phase-19.md, milestone 2): plain ROM with no bank switching. Most 16 KB games use one chip; the few
/// built from two 8 KB chips are named by the game's profile ("two-chip"), since nothing in the ROM tells them apart.
/// </summary>
public sealed class Atari5200Cartridge : IStateful
{
    private static readonly StateTag Tag = StateTag.From("A52C");

    private readonly byte[] _rom;

    /// <exception cref="RomFormatException">A size GameVault doesn't know.</exception>
    public Atari5200Cartridge(byte[] rom, string? board = null)
    {
        if (rom.Length is not (2048 or 4096 or 8192 or 16384 or 32768))
        {
            throw new RomFormatException(rom.Length == 40960
                ? "GameVault can't play 40 KB Atari 5200 cartridges (Bounty Bob Strikes Back!) yet."
                : "That isn't an Atari 5200 cartridge image.");
        }

        _rom = rom;
        Board = board?.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant() is "twochip" or "16k2" && rom.Length == 16384
            ? Atari5200Board.TwoChip16K
            : Atari5200Board.Standard;
    }

    public Atari5200Board Board { get; }

    public ReadOnlySpan<byte> Rom => _rom;

    /// <summary>A read of $4000–$BFFF.</summary>
    public byte Read(int address)
    {
        var offset = (address - 0x4000) & 0x7FFF;
        return Board == Atari5200Board.TwoChip16K
            ? _rom[((offset & 0x4000) >> 1) | (offset & 0x1FFF)]
            : _rom[offset % _rom.Length];
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.EndChunk();
    }
}
