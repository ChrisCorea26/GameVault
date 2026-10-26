using System.Text;
using GameVault.Core.Cartridge;
using GameVault.Core.State;

namespace GameVault.Core.Snes;

public enum SnesMap
{
    LoRom,
    HiRom,
    ExHiRom,
}

/// <summary>A game needs an enhancement chip GameVault doesn't emulate yet.</summary>
public sealed class UnsupportedChipException(string chip) : Exception($"This game needs the {chip} chip, which GameVault doesn't support yet.")
{
    public string Chip { get; } = chip;
}

/// <summary>
/// A Super Nintendo cartridge (SPEC §7.11, Phase 14): LoROM, HiROM or ExHiROM, found by scoring the internal header
/// at $7FC0, $FFC0 or $40FFC0 (map mode, checksum and complement, reset vector, printable title). A 512-byte copier
/// header is skipped. Battery SRAM is sized from the header. Games with enhancement chips are refused clearly.
/// </summary>
/// <remarks>References: fullsnes ("SNES Cartridges"); the SNES Development Manual's header layout.</remarks>
public sealed class SnesCartridge : IStateful, IBatteryBacked
{
    private static readonly StateTag Tag = StateTag.From("SNCR");

    private readonly byte[] _rom;
    private readonly byte[] _sram;
    private readonly int _header;

    public SnesCartridge(byte[] image)
    {
        _rom = image.Length % 1024 == 512 ? image[512..] : image;
        if (_rom.Length < 0x8000)
        {
            throw new RomFormatException("A Super Nintendo cartridge image is at least 32 KB.");
        }

        (Map, _header) = DetectMap(_rom);
        Title = Encoding.ASCII.GetString(_rom, _header, 21).Trim();
        var chip = _rom[_header + 0x16];
        if (chip >= 0x03)
        {
            throw new UnsupportedChipException(ChipName(chip));
        }

        var ramSize = _rom[_header + 0x18];
        _sram = ramSize is > 0 and <= 7 ? new byte[1024 << ramSize] : [];
        Array.Fill(_sram, (byte)0xFF);
        HasBattery = _sram.Length > 0 && chip == 0x02;
        var country = _rom[_header + 0x19];
        Pal = country is >= 0x02 and <= 0x0C;
    }

    public ReadOnlySpan<byte> Rom => _rom;

    public SnesMap Map { get; }

    public string Title { get; }

    /// <summary>A 50 Hz (European) cartridge, from the header's country code.</summary>
    public bool Pal { get; }

    public bool HasBattery { get; }

    public Span<byte> BatteryRam => _sram;

    /// <summary>True when the image looks like an SNES ROM (a header scores well somewhere).</summary>
    public static bool LooksLikeSnes(ReadOnlySpan<byte> image)
    {
        var rom = image.Length % 1024 == 512 ? image[512..] : image;
        return rom.Length >= 0x8000 && (Score(rom, 0x7FC0) >= 4 || (rom.Length >= 0x10000 && Score(rom, 0xFFC0) >= 4));
    }

    private static (SnesMap Map, int Header) DetectMap(ReadOnlySpan<byte> rom)
    {
        var lo = Score(rom, 0x7FC0);
        var hi = rom.Length >= 0x10000 ? Score(rom, 0xFFC0) : -100;
        var exhi = rom.Length >= 0x410000 ? Score(rom, 0x40FFC0) : -100;
        if (exhi > lo && exhi > hi)
        {
            return (SnesMap.ExHiRom, 0x40FFC0);
        }

        return hi > lo ? (SnesMap.HiRom, 0xFFC0) : (SnesMap.LoRom, 0x7FC0);
    }

    private static int Score(ReadOnlySpan<byte> rom, int header)
    {
        if (header + 0x40 > rom.Length)
        {
            return -100;
        }

        var score = 0;
        var mapMode = rom[header + 0x15] & ~0x10;
        var expected = header switch
        {
            0x7FC0 => 0x20,
            0xFFC0 => 0x21,
            _ => 0x25,
        };
        if (mapMode == expected)
        {
            score += 4;
        }

        var checksum = rom[header + 0x1E] | rom[header + 0x1F] << 8;
        var complement = rom[header + 0x1C] | rom[header + 0x1D] << 8;
        if ((checksum ^ complement) == 0xFFFF)
        {
            score += 4;
        }

        var reset = rom[header + 0x3C] | rom[header + 0x3D] << 8;
        if (reset >= 0x8000)
        {
            score += 2;
        }

        var printable = 0;
        for (var i = 0; i < 21; i++)
        {
            if (rom[header + i] is >= 0x20 and < 0x7F)
            {
                printable++;
            }
        }

        if (printable == 21)
        {
            score += 2;
        }

        return score;
    }

    private static string ChipName(byte chip) => (chip >> 4) switch
    {
        0x0 => "DSP",
        0x1 => "Super FX",
        0x2 => "OBC1",
        0x3 => "SA-1",
        0x4 => "S-DD1",
        0x5 => "S-RTC",
        0xE => "Super Game Boy or Satellaview",
        _ => "custom (Cx4, SPC7110 or ST01x)",
    };

    /// <summary>Reads the cartridge's part of the address space. Returns false where the cartridge doesn't answer.</summary>
    public bool TryRead(uint address, out byte value)
    {
        var bank = (int)(address >> 16);
        var offset = (int)(address & 0xFFFF);
        if (TrySramIndex(bank, offset, out var sram))
        {
            value = _sram[sram];
            return true;
        }

        if (TryRomIndex(bank, offset, out var index))
        {
            value = _rom[index % _rom.Length];
            return true;
        }

        value = 0;
        return false;
    }

    public void Write(uint address, byte value)
    {
        if (TrySramIndex((int)(address >> 16), (int)(address & 0xFFFF), out var index))
        {
            _sram[index] = value;
        }
    }

    private bool TryRomIndex(int bank, int offset, out int index)
    {
        switch (Map)
        {
            case SnesMap.LoRom:
                if (offset >= 0x8000 || (bank & 0x7F) >= 0x40 && (bank & 0x7F) < 0x70)
                {
                    index = (bank & 0x7F) * 0x8000 + (offset & 0x7FFF);
                    return (bank & 0x7F) < 0x7E;
                }

                break;
            case SnesMap.HiRom:
                if ((bank & 0x7F) >= 0x40 && (bank & 0x7F) < 0x7E || offset >= 0x8000)
                {
                    index = (bank & 0x3F) << 16 | offset;
                    return true;
                }

                break;
            default:
                if ((bank & 0x7F) >= 0x40 && (bank & 0x7F) < 0x7E || offset >= 0x8000)
                {
                    // Banks $C0–$FF (and $80–$BF upper halves) are the first 4 MB; $40–$7D and $00–$3F the rest.
                    index = ((bank & 0x80) != 0 ? 0 : 0x400000) + ((bank & 0x3F) << 16 | offset);
                    return true;
                }

                break;
        }

        index = 0;
        return false;
    }

    private bool TrySramIndex(int bank, int offset, out int index)
    {
        index = 0;
        if (_sram.Length == 0)
        {
            return false;
        }

        if (Map == SnesMap.LoRom)
        {
            // Banks $70–$7D and $F0–$FF, $0000–$7FFF.
            if ((bank & 0x7F) is >= 0x70 and < 0x7E && offset < 0x8000)
            {
                index = (((bank & 0x0F) << 15) | offset) % _sram.Length;
                return true;
            }

            return false;
        }

        // HiROM/ExHiROM: banks $20–$3F and $A0–$BF (ExHiROM: $80–$BF), $6000–$7FFF.
        if (offset is >= 0x6000 and < 0x8000 && (bank & 0x7F) < 0x40 && ((bank & 0x7F) >= 0x20 || Map == SnesMap.ExHiRom && (bank & 0x80) != 0))
        {
            index = (((bank & 0x1F) << 13) | (offset - 0x6000)) % _sram.Length;
            return true;
        }

        return false;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_sram);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_sram);
        reader.EndChunk();
    }
}
