using GameVault.Core.Cartridge;
using GameVault.Core.State;

namespace GameVault.Core.Atari;

/// <summary>How a 2600 cartridge maps more than 4 KB into its 4 KB window.</summary>
public enum Atari2600Scheme
{
    /// <summary>2 or 4 KB, no switching.</summary>
    Plain,

    /// <summary>Atari 8K: $1FF8–$1FF9 pick one of two 4K banks.</summary>
    F8,

    /// <summary>Atari 16K: $1FF6–$1FF9 pick one of four.</summary>
    F6,

    /// <summary>Atari 32K: $1FF4–$1FFB pick one of eight.</summary>
    F4,

    /// <summary>Parker Brothers 8K: three 1K windows switched at $1FE0–$1FF7, the last 1K fixed.</summary>
    E0,

    /// <summary>Activision 8K: the bank follows the address the CPU's JSR and RTS pass through $01FE.</summary>
    FE,

    /// <summary>Tigervision: a write to $00–$3F picks the 2K bank at $1000; the last 2K is fixed at $1800.</summary>
    Tigervision3F,

    /// <summary>CBS RAM Plus 12K: $1FF8–$1FFA pick one of three 4K banks, with 256 bytes of RAM.</summary>
    FA,
}

/// <summary>
/// A 2600 cartridge (phase-19.md): its ROM, the bank-switching scheme (from the size and the code's signatures, as
/// Stella detects them, or named by the game's profile), and the SuperChip's 128 bytes of RAM on F8, F6 and F4 boards
/// whose ROM leaves their first 256 bytes of every bank empty. The RAM is written at $1000–$107F (or $1000–$10FF for
/// CBS RAM Plus) and read 128 (or 256) bytes higher.
/// </summary>
/// <remarks>Reference: Kevin Horton's "bankswitch_sizes.txt" and Stella's cartridge classes, read for behaviour only.</remarks>
public sealed class Atari2600Cartridge : IStateful
{
    private static readonly StateTag Tag = StateTag.From("A26C");

    private readonly byte[] _rom;
    private readonly byte[] _ram;
    private readonly int[] _slices = new int[4];
    private int _bank;
    private bool _feWatch;

    /// <exception cref="RomFormatException">A size or board GameVault doesn't know.</exception>
    public Atari2600Cartridge(byte[] rom, string? scheme = null)
    {
        if (rom.Length is 0 or > 512 * 1024)
        {
            throw new RomFormatException("That isn't an Atari 2600 cartridge image.");
        }

        _rom = rom;
        (Scheme, SuperChip) = scheme is null ? Detect(rom) : Parse(scheme, rom.Length);
        if (Scheme == Atari2600Scheme.Plain && rom.Length is not (2048 or 4096))
        {
            throw new RomFormatException($"GameVault doesn't know how a {rom.Length / 1024} KB Atari 2600 cartridge switches its banks.");
        }

        _ram = Scheme == Atari2600Scheme.FA ? new byte[256] : SuperChip ? new byte[128] : [];
        Power();
    }

    public Atari2600Scheme Scheme { get; }

    /// <summary>The F8, F6 or F4 board has the SuperChip's RAM.</summary>
    public bool SuperChip { get; }

    public ReadOnlySpan<byte> Rom => _rom;

    public Span<byte> Ram => _ram;

    private int Banks => Math.Max(1, _rom.Length / 4096);

    public void Power()
    {
        Array.Clear(_ram);
        _bank = Scheme is Atari2600Scheme.F8 or Atari2600Scheme.F6 or Atari2600Scheme.F4 or Atari2600Scheme.FA ? Banks - 1 : 0;
        _slices[0] = 4;
        _slices[1] = 5;
        _slices[2] = 6;
        _slices[3] = 7;
        _feWatch = false;
    }

    /// <summary>Picks the scheme from the size and, for 8K and larger, the code's telltale accesses.</summary>
    public static (Atari2600Scheme Scheme, bool SuperChip) Detect(ReadOnlySpan<byte> rom)
    {
        if (rom.Length <= 4096)
        {
            return (Atari2600Scheme.Plain, false);
        }

        if (HasTigervisionSwitches(rom))
        {
            return (Atari2600Scheme.Tigervision3F, false);
        }

        return rom.Length switch
        {
            8192 when ContainsAny(rom, E0Signatures) => (Atari2600Scheme.E0, false),
            8192 when ContainsAny(rom, FeSignatures) => (Atari2600Scheme.FE, false),
            8192 => (Atari2600Scheme.F8, HasSuperChip(rom)),
            12288 => (Atari2600Scheme.FA, false),
            16384 => (Atari2600Scheme.F6, HasSuperChip(rom)),
            32768 => (Atari2600Scheme.F4, HasSuperChip(rom)),
            _ => (Atari2600Scheme.Plain, false),
        };
    }

    private static (Atari2600Scheme, bool) Parse(string scheme, int size) => scheme.ToUpperInvariant() switch
    {
        "2K" or "4K" => (Atari2600Scheme.Plain, false),
        "F8" => (Atari2600Scheme.F8, false),
        "F8SC" => (Atari2600Scheme.F8, true),
        "F6" => (Atari2600Scheme.F6, false),
        "F6SC" => (Atari2600Scheme.F6, true),
        "F4" => (Atari2600Scheme.F4, false),
        "F4SC" => (Atari2600Scheme.F4, true),
        "E0" => (Atari2600Scheme.E0, false),
        "FE" => (Atari2600Scheme.FE, false),
        "3F" => (Atari2600Scheme.Tigervision3F, false),
        "FA" => (Atari2600Scheme.FA, false),
        _ => throw new RomFormatException($"GameVault doesn't know the Atari 2600 board \"{scheme}\" ({size / 1024} KB)."),
    };

    // "STA $1FE0" and the other ways Parker Brothers games reach their hotspots.
    private static readonly byte[][] E0Signatures =
    [
        [0x8D, 0xE0, 0x1F], [0x8D, 0xE0, 0x5F], [0x8D, 0xE9, 0xFF], [0x0C, 0xE0, 0x1F],
        [0xAD, 0xE0, 0x1F], [0xAD, 0xE9, 0xFF], [0xAD, 0xED, 0xFF], [0xAD, 0xF3, 0xBF],
    ];

    // Activision's bank-switching subroutines.
    private static readonly byte[][] FeSignatures =
    [
        [0x20, 0x00, 0xD0, 0xC6, 0xC5], [0x20, 0xC3, 0xF8, 0xA5, 0x82], [0xD0, 0xFB, 0x20, 0x73, 0xFE],
        [0x20, 0x00, 0xF0, 0x84, 0xD6],
    ];

    private static bool ContainsAny(ReadOnlySpan<byte> rom, byte[][] signatures)
    {
        foreach (var signature in signatures)
        {
            if (rom.IndexOf(signature) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tigervision games store to $3F ("STA $3F") to switch, at least twice.</summary>
    private static bool HasTigervisionSwitches(ReadOnlySpan<byte> rom)
    {
        if (rom.Length % 2048 != 0)
        {
            return false;
        }

        var count = 0;
        for (var i = 0; i + 1 < rom.Length; i++)
        {
            if (rom[i] == 0x85 && rom[i + 1] == 0x3F && ++count >= 2)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A SuperChip board's ROM leaves the RAM's 256 bytes the same value in every 4K bank.</summary>
    private static bool HasSuperChip(ReadOnlySpan<byte> rom)
    {
        for (var bank = 0; bank < rom.Length; bank += 4096)
        {
            var first = rom[bank];
            for (var i = 1; i < 256; i++)
            {
                if (rom[bank + i] != first)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>A read of $1000–$1FFF (the address's low 13 bits).</summary>
    public byte Read(int address)
    {
        var offset = address & 0x0FFF;
        Hotspot(offset);
        if (_ram.Length > 0)
        {
            var size = _ram.Length;
            if (offset >= size && offset < size * 2)
            {
                return _ram[offset - size];
            }
        }

        return _rom[RomIndex(offset)];
    }

    public void Write(int address, byte value)
    {
        var offset = address & 0x0FFF;
        Hotspot(offset);
        if (_ram.Length > 0 && offset < _ram.Length)
        {
            _ram[offset] = value;
        }
    }

    /// <summary>
    /// Every bus access the console makes, for the boards that watch more than their own window: Tigervision's writes
    /// to $00–$3F, and the data on Activision's next access after $01FE.
    /// </summary>
    public void Snoop(int address, byte data, bool write)
    {
        switch (Scheme)
        {
            case Atari2600Scheme.Tigervision3F when write && (address & 0x1FC0) == 0:
                _bank = data % Math.Max(1, _rom.Length / 2048);
                break;
            case Atari2600Scheme.FE:
                if (_feWatch)
                {
                    _feWatch = false;
                    _bank = (data & 0x20) != 0 ? 0 : 1;
                }

                if ((address & 0x1FFF) == 0x01FE)
                {
                    _feWatch = true;
                }

                break;
        }
    }

    private void Hotspot(int offset)
    {
        switch (Scheme)
        {
            case Atari2600Scheme.F8 when offset is 0xFF8 or 0xFF9:
                _bank = offset - 0xFF8;
                break;
            case Atari2600Scheme.F6 when offset is >= 0xFF6 and <= 0xFF9:
                _bank = offset - 0xFF6;
                break;
            case Atari2600Scheme.F4 when offset is >= 0xFF4 and <= 0xFFB:
                _bank = offset - 0xFF4;
                break;
            case Atari2600Scheme.FA when offset is >= 0xFF8 and <= 0xFFA:
                _bank = offset - 0xFF8;
                break;
            case Atari2600Scheme.E0 when offset is >= 0xFE0 and <= 0xFF7:
                _slices[(offset - 0xFE0) >> 3] = offset & 7;
                break;
        }
    }

    private int RomIndex(int offset)
    {
        var index = Scheme switch
        {
            Atari2600Scheme.Plain => offset,
            Atari2600Scheme.E0 => _slices[offset >> 10] * 1024 + (offset & 0x3FF),
            Atari2600Scheme.Tigervision3F => offset < 0x800 ? _bank * 2048 + offset : _rom.Length - 2048 + (offset & 0x7FF),
            _ => _bank * 4096 + offset,
        };
        return index % _rom.Length;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteInt32(_bank);
        for (var i = 0; i < 4; i++)
        {
            writer.WriteInt32(_slices[i]);
        }

        writer.WriteBool(_feWatch);
        writer.WriteBytes(_ram);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        _bank = Math.Clamp(reader.ReadInt32(), 0, Math.Max(Banks, _rom.Length / 2048));
        for (var i = 0; i < 4; i++)
        {
            _slices[i] = reader.ReadInt32() & 7;
        }

        _feWatch = reader.ReadBool();
        reader.ReadBytes(_ram);
        reader.EndChunk();
    }
}
