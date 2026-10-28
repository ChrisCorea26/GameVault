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
/// header is skipped. Battery SRAM is sized from the header. The DSP-n (with the player's own program ROM) and the
/// Super FX are emulated (outstanding-work plan §4), and the chips of snes-chips-3.md as they arrive; games with other
/// enhancement chips are refused clearly.
/// </summary>
/// <remarks>References: fullsnes ("SNES Cartridges", "SNES Cart DSP-n", "SNES Cart GSU-n"); the SNES Development
/// Manual's header layout; the boards' DSP addresses as bsnes's board database lists them.</remarks>
public sealed class SnesCartridge : IStateful, IBatteryBacked
{
    private static readonly StateTag Tag = StateTag.From("SNCR");

    private readonly byte[] _rom;
    private readonly byte[] _sram;
    private readonly int _header;

    /// <param name="image">The cartridge dump.</param>
    /// <param name="chipRoms">
    /// The player's enhancement chip program ROMs by file name ("dsp1b.rom"), or null when none are set up.
    /// </param>
    /// <exception cref="UnsupportedChipException">A chip GameVault doesn't emulate.</exception>
    /// <exception cref="MissingChipRomException">A chip whose program ROM isn't among <paramref name="chipRoms"/>.</exception>
    public SnesCartridge(byte[] image, Func<string, byte[]?>? chipRoms = null)
    {
        _rom = image.Length % 1024 == 512 ? image[512..] : image;
        if (_rom.Length < 0x8000)
        {
            throw new RomFormatException("A Super Nintendo cartridge image is at least 32 KB.");
        }

        (Map, _header) = DetectMap(_rom);
        Title = Encoding.ASCII.GetString(_rom, _header, 21).Trim();
        var chip = _rom[_header + 0x16];
        Chip = chip;
        var battery = (chip & 0x0F) is 0x02 or 0x05 or 0x06 || chip is 0x1A or 0xF9;
        var ramSize = _rom[_header + 0x18];
        var country = _rom[_header + 0x19];
        Pal = country is >= 0x02 and <= 0x0C;
        if (chip >= 0x03)
        {
            Coprocessor = (chip >> 4) switch
            {
                0x0 => CreateDsp(chipRoms),
                0x1 => CreateSuperFx(chip),
                0x2 => new Obc1(),
                0x3 => new Sa1(_rom, ramSize is > 0 and <= 8 ? 1024 << ramSize : 0, Pal),
                0x4 => new Sdd1(_rom),
                0x5 => new SharpRtc(Pal ? SnesConsole.PalMasterClock : SnesConsole.NtscMasterClock),
                0xF when chip == 0xF6 => CreateSeta(chipRoms),
                0xF when chip == 0xF3 => CreateCx4(chipRoms),
                0xF when IsSpc7110(chip) => new Spc7110(_rom, ramSize is > 0 and <= 8 ? 1024 << ramSize : 0, withRtc: chip == 0xF9,
                    Pal ? SnesConsole.PalMasterClock : SnesConsole.NtscMasterClock),
                _ => throw new UnsupportedChipException(ChipName(chip)),
            };
        }

        _sram = Coprocessor is SuperFx or Sa1 or Obc1 or Spc7110 or NecDsp { IsUpd96050: true } || ramSize is 0 or > 7 ? [] : new byte[1024 << ramSize];
        Array.Fill(_sram, (byte)0xFF);
        HasBattery = battery && (_sram.Length > 0 || Coprocessor is SuperFx or Sa1 or Obc1 or Spc7110 { Ram.Length: > 0 } or NecDsp { IsUpd96050: true });
    }

    public ReadOnlySpan<byte> Rom => _rom;

    /// <summary>The header's chipset byte ($xFD6): the chip in the high nibble, ROM/RAM/battery in the low one.</summary>
    public byte Chip { get; }

    /// <summary>The cartridge's enhancement chip, if it has one GameVault emulates.</summary>
    public ISnesCoprocessor? Coprocessor { get; }

    public SnesMap Map { get; }

    public string Title { get; }

    /// <summary>A 50 Hz (European) cartridge, from the header's country code.</summary>
    public bool Pal { get; }

    public bool HasBattery { get; }

    /// <summary>The battery-backed RAM: the SRAM, the Super FX's game pak RAM or the SA-1's BW-RAM.</summary>
    public Span<byte> BatteryRam => Coprocessor switch
    {
        SuperFx gsu => gsu.Ram,
        Sa1 sa1 => sa1.BwRam,
        Obc1 obc1 => obc1.Ram,
        Spc7110 spc7110 => spc7110.Ram,
        NecDsp { IsUpd96050: true } seta => seta.DataRam,
        _ => _sram,
    };

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
        if (mapMode == expected || (header == 0xFFC0 && mapMode == 0x2A)) // $3A: HiROM with the SPC7110
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

    /// <summary>
    /// The DSP-n by game (they share the header's chip code): DSP-2 for Dungeon Master, DSP-3 for SD Gundam GX, DSP-4
    /// for Top Gear 3000, otherwise the DSP-1. Each board puts the chip's ports at its own addresses.
    /// </summary>
    private NecDsp CreateDsp(Func<string, byte[]?>? chipRoms)
    {
        var title = Title.ToUpperInvariant();
        var (name, files, map) = title switch
        {
            _ when title.StartsWith("DUNGEON MASTER", StringComparison.Ordinal) =>
                ("DSP-2", new[] { "dsp2.rom" }, new NecDspMap(0x60, 0x20, 0x6000, 0x7FFF, 0x1000)),
            _ when title.Contains("GUNDAM GX", StringComparison.Ordinal) =>
                ("DSP-3", new[] { "dsp3.rom" }, new NecDspMap(0x60, 0x20, 0x8000, 0xFFFF, 0x4000)),
            _ when title.Contains("TOP GEAR 3000", StringComparison.Ordinal) || title.Contains("TG3000", StringComparison.Ordinal) =>
                ("DSP-4", new[] { "dsp4.rom" }, NecDspMap.LoRom1Mb),
            _ => ("DSP-1", new[] { "dsp1b.rom", "dsp1.rom" }, Map == SnesMap.LoRom
                ? (_rom.Length > 0x100000 ? NecDspMap.LoRom2Mb : NecDspMap.LoRom1Mb)
                : NecDspMap.HiRom),
        };

        foreach (var file in files)
        {
            if (chipRoms?.Invoke(file) is { Length: NecDsp.DumpSize } dump)
            {
                var (program, data) = NecDsp.SplitDump(dump);
                return new NecDsp(name, program, data, map);
            }
        }

        throw new MissingChipRomException(name, files);
    }

    /// <summary>
    /// Seta's ST010 (F1 ROC II, Exhaust Heat II) or ST011 (Hayazashi Nidan Morita Shogi), both a µPD96050 running the
    /// player's own program ROM; the shogi game's is the ST011.
    /// </summary>
    private NecDsp CreateSeta(Func<string, byte[]?>? chipRoms)
    {
        var st011 = Title.Contains("MORITA", StringComparison.OrdinalIgnoreCase);
        var name = st011 ? "ST011" : "ST010";
        var file = st011 ? "st011.rom" : "st010.rom";
        if (chipRoms?.Invoke(file) is { Length: NecDsp.Upd96050DumpSize } dump)
        {
            var (program, data) = NecDsp.SplitDump(dump);
            return new NecDsp(name, program, data, NecDspMap.Seta, upd96050: true, hz: st011 ? 15_000_000 : 11_000_000);
        }

        throw new MissingChipRomException(name, [file]);
    }

    /// <summary>Capcom's Cx4 (chipset $F3), with the player's own data ROM.</summary>
    private Cx4 CreateCx4(Func<string, byte[]?>? chipRoms)
    {
        if (chipRoms?.Invoke("cx4.rom") is { Length: Cx4.DataRomSize } dataRom)
        {
            return new Cx4(_rom, dataRom, Pal ? SnesConsole.PalMasterClock : SnesConsole.NtscMasterClock);
        }

        throw new MissingChipRomException("Cx4", ["cx4.rom"]);
    }

    /// <summary>
    /// Hudson's SPC7110 boards: chipset $F9 (with the RTC), or $F5 whose extended header's subtype ($xFBF) is 0 (the
    /// ST018 shares $F5 with subtype 2). A cartridge without an extended header counts by its size, the program ROM's
    /// megabyte and a data ROM.
    /// </summary>
    private bool IsSpc7110(byte chip)
    {
        if (chip == 0xF9)
        {
            return true;
        }

        if (chip != 0xF5)
        {
            return false;
        }

        var extended = _rom[_header + 0x1A] == 0x33 && _header >= 1;
        return extended ? _rom[_header - 1] == 0x00 : _rom.Length > 0x100000;
    }

    /// <summary>
    /// The Super FX: the GSU-2 when the header says so ($1A), else the GSU-1. Its RAM size is in the extended header
    /// ($xFBD, as 1 KB shifted left) when there is one, else 32 KB.
    /// </summary>
    private SuperFx CreateSuperFx(byte chip)
    {
        var ramSize = 0x8000;
        if (_rom[_header + 0x1A] == 0x33 && _header >= 3 && _rom[_header - 3] is >= 5 and <= 7 and var shift)
        {
            ramSize = 1024 << shift;
        }

        return new SuperFx(_rom, ramSize, chip == 0x1A ? (byte)4 : (byte)1);
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
        _ => "ST018",
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
        Coprocessor?.SaveState(writer); // only cartridges with a chip have this, so other games' states are unchanged
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_sram);
        Coprocessor?.LoadState(reader);
        reader.EndChunk();
    }
}
