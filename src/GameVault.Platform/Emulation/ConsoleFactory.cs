using GameVault.Core;
using GameVault.Core.Apple;
using GameVault.Core.Cartridge;
using GameVault.Core.Genesis;
using GameVault.Core.Nintendo64;
using GameVault.Core.PlayStation;
using GameVault.Core.PlayStation2;
using GameVault.Core.Sega;
using GameVault.Core.Snes;
using GameVault.Core.Systems;
using GameVault.Library.Discs;
using GameVault.Library.Profiles;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Emulation;

/// <summary>Builds the right console for a game file (SPEC §7.11): loading, netplay and challenge checks all use it.</summary>
internal static class ConsoleFactory
{
    /// <summary>Throws <see cref="GameLoadException"/> with a message for the player.</summary>
    public static IEmulatedConsole Create(
        byte[] data,
        GameSystem system,
        GameProfile profile,
        byte[]? fdsBios,
        string extension = "",
        AppleRoms? appleRoms = null,
        PlayStationSetup? playStation = null,
        N64Setup? n64 = null,
        PlayStation2Setup? playStation2 = null)
    {
        if (system == GameSystem.PlayStation)
        {
            return CreatePlayStation(data, playStation);
        }

        if (system == GameSystem.PlayStation2)
        {
            return CreatePlayStation2(data, playStation2);
        }

        if (system == GameSystem.Nintendo64)
        {
            return CreateNintendo64(data, profile, n64 ?? new N64Setup());
        }

        if (system == GameSystem.AppleII)
        {
            if (appleRoms is null)
            {
                throw new GameLoadException(
                    "This is an Apple II disk. To play it, choose your own Apple II ROM files in Settings → Apple II. GameVault doesn't include them.");
            }

            try
            {
                var model = profile.Model?.ToLowerInvariant() switch
                {
                    "ii+" or "iiplus" => AppleModel.IIPlus,
                    "iie" => AppleModel.IIe,
                    "iie-enhanced" => AppleModel.IIeEnhanced,
                    _ => (AppleModel?)null,
                };
                var apple = new AppleConsole(appleRoms, model);
                apple.InsertDisk(0, AppleDisk.Open(data, extension.Length > 0 ? extension : ".dsk"), AppleConsole.HashDisk(data));
                return apple;
            }
            catch (RomFormatException ex)
            {
                throw new GameLoadException(ex.Message, ex);
            }
        }

        if (system == GameSystem.Genesis)
        {
            try
            {
                var region = profile.Region?.ToLowerInvariant() switch
                {
                    "usa" or "ntsc" => GenesisRegion.Americas,
                    "japan" => GenesisRegion.Japan,
                    "europe" or "pal" => GenesisRegion.Europe,
                    _ => (GenesisRegion?)null,
                };
                return new GenesisConsole(new GenesisCartridge(data), region, sixButton: profile.Pad != "3");
            }
            catch (RomFormatException ex)
            {
                throw new GameLoadException("That Genesis cartridge file is empty or damaged.", ex);
            }
        }

        if (system == GameSystem.Snes)
        {
            try
            {
                var pal = profile.Region?.ToLowerInvariant() switch
                {
                    "pal" or "europe" => true,
                    "ntsc" or "usa" or "japan" => false,
                    _ => (bool?)null,
                };
                return new SnesConsole(new SnesCartridge(data), pal);
            }
            catch (UnsupportedChipException ex)
            {
                throw new GameLoadException(ex.Message, ex);
            }
            catch (RomFormatException ex)
            {
                throw new GameLoadException("That Super Nintendo cartridge file is empty or damaged.", ex);
            }
        }

        if (system != GameSystem.Nes)
        {
            try
            {
                return SegaConsole.FromBytes(system, data, pal: string.Equals(profile.Region, "pal", StringComparison.OrdinalIgnoreCase));
            }
            catch (RomFormatException ex)
            {
                throw new GameLoadException("That game file is empty or damaged.", ex);
            }
        }

        try
        {
            if (Core.Cartridge.Fds.FdsImage.IsFdsImage(data))
            {
                if (fdsBios is null)
                {
                    throw new GameLoadException(
                        "This is a Famicom Disk System game. To play it, choose your own Disk System BIOS " +
                        "(disksys.rom) in Settings → Disk System. GameVault doesn't include it.");
                }

                return NesConsole.FromFds(data, fdsBios);
            }

            return new NesConsole(ApplyProfile(INesRom.Parse(data), profile));
        }
        catch (RomFormatException ex) when (Core.Cartridge.Fds.FdsImage.IsFdsImage(data))
        {
            throw new GameLoadException(ex.Message, ex); // a damaged disk image or a BIOS of the wrong size
        }
        catch (RomFormatException ex)
        {
            throw new GameLoadException("That file isn't an NES game, or it's damaged. GameVault plays .nes and .fds files.", ex);
        }
        catch (UnsupportedMapperException ex)
        {
            throw new GameLoadException(
                $"This game uses a cartridge type GameVault doesn't support yet (mapper {ex.Mapper}).", ex);
        }
    }

    /// <summary>
    /// An N64 with the Expansion Pak as Settings has it. The save type comes from the game's profile, then the
    /// homebrew header, then the save-type list, and is otherwise a 4 Kbit EEPROM (data/gamedb/n64/README.md).
    /// </summary>
    private static N64Console CreateNintendo64(byte[] data, GameProfile profile, N64Setup setup)
    {
        if (!N64Cartridge.IsN64Rom(data))
        {
            throw new GameLoadException("That file isn't an N64 cartridge, or it's damaged. GameVault plays .z64, .v64 and .n64 files.");
        }

        try
        {
            var rom = N64Cartridge.ToBigEndian(data);
            var gameCode = System.Text.Encoding.ASCII.GetString(rom, 0x3B, 4);
            var saveType = Library.Profiles.N64SaveTypes.Parse(profile.Save)
                ?? N64Cartridge.HomebrewSaveType(rom)
                ?? setup.SaveTypes?.Invoke(gameCode);
            return new N64Console(new N64Cartridge(rom, saveType), setup.ExpansionPak);
        }
        catch (RomFormatException ex)
        {
            throw new GameLoadException("That N64 cartridge file is empty or damaged.", ex);
        }
    }

    /// <summary>
    /// A PlayStation with the player's BIOS and the game's first disc (for an .m3u, the first in the list), or a
    /// PS-EXE: run after the BIOS boots, or straight away without one (bare-metal homebrew).
    /// </summary>
    private static PlayStationConsole CreatePlayStation(byte[] data, PlayStationSetup? setup)
    {
        var bios = setup?.Bios;
        if (bios is not null && bios.Length != PlayStationConsole.BiosSize)
        {
            throw new GameLoadException(
                "The PlayStation BIOS chosen in Settings → PlayStation isn't a 512 KB BIOS file. Choose the BIOS dumped from your own console.");
        }

        if (!PlayStationGames.TryGetPath(data, out var path))
        {
            try
            {
                return new PlayStationConsole(bios, exe: PsExe.Parse(data));
            }
            catch (InvalidDataException ex)
            {
                throw new GameLoadException("That file isn't a PlayStation program (PS-EXE), or it's damaged.", ex);
            }
        }

        if (bios is null)
        {
            throw new GameLoadException(
                "This is a PlayStation disc. To play it, choose your own PlayStation BIOS in Settings → PlayStation. GameVault doesn't include it.");
        }

        ICdImage disc;
        try
        {
            disc = DiscImages.Open(PlayStationGames.DiscPaths(path)[0]);
        }
        catch (InvalidDataException ex)
        {
            throw new GameLoadException("GameVault couldn't open that disc: " + ex.Message, ex);
        }

        return new PlayStationConsole(bios, disc, fastBoot: setup!.FastBoot);
    }

    /// <summary>
    /// A PlayStation 2 with a formatted memory card in slot 1 (the host loads the game's saved card over it): an ELF
    /// boots through the player's BIOS when one is set (fast boot at EELOAD) and on GameVault's own kernel otherwise; a disc (for an .m3u, the first in the list) boots
    /// through the player's BIOS, which it needs.
    /// </summary>
    private static Ps2Console CreatePlayStation2(byte[] data, PlayStation2Setup? setup)
    {
        var bios = setup?.Bios;
        if (bios is not null && bios.Length != Ps2Console.BiosSize)
        {
            throw new GameLoadException(
                "The PlayStation 2 BIOS chosen in Settings → PlayStation 2 isn't a 4 MB BIOS file. Choose the BIOS dumped from your own console.");
        }

        if (!Ps2Discs.TryGetPath(data, out var path))
        {
            if (!Ps2Discs.IsPs2Elf(data))
            {
                throw new GameLoadException("That file isn't a PlayStation 2 program (ELF) or disc, or it's damaged.");
            }

            try
            {
                return new Ps2Console(Ps2Boot.FromElf(data), bios, Ps2MemoryCard.CreateFormatted());
            }
            catch (InvalidDataException ex)
            {
                throw new GameLoadException("That file isn't a PlayStation 2 program (ELF), or it's damaged.", ex);
            }
        }

        if (bios is null)
        {
            throw new GameLoadException(PlayStation2Setup.NeedsBiosMessage);
        }

        IPs2DiscImage disc;
        try
        {
            disc = Ps2Discs.Open(path);
        }
        catch (InvalidDataException ex)
        {
            throw new GameLoadException("GameVault couldn't open that disc: " + ex.Message, ex);
        }

        try
        {
            return new Ps2Console(Ps2Boot.FromDisc(disc), bios, Ps2MemoryCard.CreateFormatted(), new Ps2ConsoleOptions { FastBoot = setup!.FastBoot });
        }
        catch
        {
            disc.Dispose();
            throw;
        }
    }

    /// <summary>Header corrections and region from the game database (SPEC §4.5, §7.5).</summary>
    public static INesRom ApplyProfile(INesRom rom, GameProfile profile)
    {
        if (profile.Header is { } header)
        {
            rom = rom with
            {
                Mapper = header.Mapper ?? rom.Mapper,
                Submapper = header.Submapper ?? rom.Submapper,
                HasBattery = header.Battery ?? rom.HasBattery,
                PrgRamSize = header.PrgRamSize ?? rom.PrgRamSize,
                Mirroring = header.Mirroring?.ToLowerInvariant() switch
                {
                    "horizontal" => Mirroring.Horizontal,
                    "vertical" => Mirroring.Vertical,
                    "four-screen" => Mirroring.FourScreen,
                    _ => rom.Mirroring,
                },
            };
        }

        return profile.Region?.ToLowerInvariant() switch
        {
            "ntsc" => rom with { TvSystem = TvSystem.Ntsc },
            "pal" => rom with { TvSystem = TvSystem.Pal },
            "dendy" => rom with { TvSystem = TvSystem.Dendy },
            _ => rom,
        };
    }
}
