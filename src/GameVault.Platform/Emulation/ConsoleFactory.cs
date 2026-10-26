using GameVault.Core;
using GameVault.Core.Cartridge;
using GameVault.Core.Sega;
using GameVault.Core.Systems;
using GameVault.Library.Profiles;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Emulation;

/// <summary>Builds the right console for a game file (SPEC §7.11): loading, netplay and challenge checks all use it.</summary>
internal static class ConsoleFactory
{
    /// <summary>Throws <see cref="GameLoadException"/> with a message for the player.</summary>
    public static IEmulatedConsole Create(byte[] data, GameSystem system, GameProfile profile, byte[]? fdsBios)
    {
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
