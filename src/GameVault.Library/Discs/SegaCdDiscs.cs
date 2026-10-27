using System.Text;
using GameVault.Core.Genesis;
using GameVault.Core.PlayStation;

namespace GameVault.Library.Discs;

/// <summary>
/// Sega CD discs (sega-cd-32x.md): a .cue or .chd whose first data sector starts "SEGADISCSYSTEM". As for the
/// PlayStation, a disc game's data is a small reference holding the image's path; the console opens the disc from it.
/// </summary>
public static class SegaCdDiscs
{
    private static ReadOnlySpan<byte> Marker => "GVSEGACD-DISC\n"u8;

    public static byte[] Reference(string path) => [.. Marker, .. Encoding.UTF8.GetBytes(Path.GetFullPath(path))];

    public static bool TryGetPath(ReadOnlySpan<byte> data, out string path)
    {
        if (data.StartsWith(Marker))
        {
            path = Encoding.UTF8.GetString(data[Marker.Length..]);
            return true;
        }

        path = string.Empty;
        return false;
    }

    /// <summary>The first data track's first 2,048 bytes (the system area), or null for a disc without one.</summary>
    private static byte[]? SystemArea(ICdImage disc)
    {
        var track = disc.Tracks.FirstOrDefault(t => t.Type != CdTrackType.Audio);
        if (track is null)
        {
            return null;
        }

        var sector = new byte[ICdImage.SectorSize];
        disc.ReadSector(track.Start, sector);
        return sector.AsSpan(track.Type == CdTrackType.Mode2 ? 24 : 16, 2048).ToArray();
    }

    /// <summary>The disc is a Sega CD disc: its system area starts "SEGADISCSYSTEM".</summary>
    public static bool IsSegaCd(ICdImage disc) => SystemArea(disc) is { } area && area.AsSpan().StartsWith("SEGADISCSYSTEM"u8);

    /// <summary>Opens the image at <paramref name="path"/> and checks it; false for anything unreadable.</summary>
    public static bool IsSegaCd(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".cue" or ".chd"))
        {
            return false;
        }

        try
        {
            var disc = DiscImages.Open(path);
            try
            {
                return IsSegaCd(disc);
            }
            finally
            {
                (disc as IDisposable)?.Dispose();
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The region the disc's header names at $1F0 (as a cartridge's does), or null when it names none.</summary>
    public static GenesisRegion? Region(ICdImage disc)
    {
        if (SystemArea(disc) is not { } area)
        {
            return null;
        }

        var codes = Encoding.ASCII.GetString(area, 0x1F0, 3);
        return codes.Contains('U') ? GenesisRegion.Americas
            : codes.Contains('E') ? GenesisRegion.Europe
            : codes.Contains('J') ? GenesisRegion.Japan
            : null;
    }

    /// <summary>Track 1's raw sectors' CRC-32 and SHA-1, as Redump lists them.</summary>
    public static (string Crc32, string Sha1) HashReference(string path)
    {
        var disc = DiscImages.Open(path);
        try
        {
            return PlayStationGames.HashFirstTrack(disc);
        }
        finally
        {
            (disc as IDisposable)?.Dispose();
        }
    }
}
