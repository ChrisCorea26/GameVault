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
/// A 5200 cartridge (phase-19.md, milestone 2): plain ROM with no bank switching. A 16 KB game is one 16 KB chip or two
/// 8 KB chips; the profile's model can say which ("one-chip" or "two-chip"), and otherwise the ROM's code decides
/// (<see cref="LooksTwoChip"/>). Mapped the wrong way, the BIOS still shows the game's title (the name and the start
/// address sit in the same place either way) and the game then crashes into the wrong chip (docs/bugs.md GV-026).
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
        var model = board?.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        var twoChip = model switch
        {
            "twochip" or "16k2" => true,
            "onechip" or "standard" or "16k1" => false,
            _ => LooksTwoChip(rom),
        };
        Board = twoChip && rom.Length == 16384 ? Atari5200Board.TwoChip16K : Atari5200Board.Standard;
    }

    /// <summary>
    /// Whether a 16 KB ROM's code is built for two 8 KB chips. A one-chip game is linked for $8000–$BFFF (the window's
    /// lower half only mirrors it), so its JSRs and JMPs hardly ever point below $8000; a two-chip game keeps half its
    /// code in the first chip at $4000–$7FFF and calls into it. Data that happens to look like a JSR or JMP points
    /// anywhere, so a one-chip game still shows a few, far fewer than its calls into $8000–$BFFF.
    /// </summary>
    public static bool LooksTwoChip(ReadOnlySpan<byte> rom)
    {
        var (low, high) = CallTargets(rom);
        return rom.Length == 16384 && low >= 16 && low * 2 >= high;
    }

    /// <summary>How many JSR and JMP look-alikes in the ROM point into $4000–$7FFF and into $8000–$BFFF.</summary>
    public static (int Low, int High) CallTargets(ReadOnlySpan<byte> rom)
    {
        int low = 0, high = 0;
        for (var i = 0; i + 2 < rom.Length; i++)
        {
            if (rom[i] is not (0x20 or 0x4C))
            {
                continue;
            }

            var target = rom[i + 1] | (rom[i + 2] << 8);
            if (target is >= 0x4000 and < 0x8000)
            {
                low++;
            }
            else if (target is >= 0x8000 and < 0xC000)
            {
                high++;
            }
        }

        return (low, high);
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
