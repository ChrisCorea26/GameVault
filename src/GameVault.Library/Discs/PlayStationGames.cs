using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using GameVault.Core.PlayStation;

namespace GameVault.Library.Discs;

/// <summary>
/// PlayStation games in the library and on the emulator (SPEC §7.11, Phase 15). A disc is far too big to pass around
/// as bytes like a cartridge, so a disc game's data is a small reference holding the path of its .cue, .chd or .m3u;
/// the console opens the disc from it. Homebrew PS-EXE files are passed as their bytes.
/// </summary>
public static class PlayStationGames
{
    private static ReadOnlySpan<byte> Marker => "GVPS-DISC\n"u8;

    /// <summary>The data that stands for the disc (or .m3u playlist of discs) at <paramref name="path"/>.</summary>
    public static byte[] Reference(string path) => [.. Marker, .. Encoding.UTF8.GetBytes(Path.GetFullPath(path))];

    /// <summary>The path a reference holds; false for other data (a PS-EXE).</summary>
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

    /// <summary>The discs of a game in order: the playlist's for an .m3u, else the one disc.</summary>
    public static IReadOnlyList<string> DiscPaths(string path) =>
        Path.GetExtension(path).Equals(".m3u", StringComparison.OrdinalIgnoreCase) ? DiscImages.ReadPlaylist(path) : [Path.GetFullPath(path)];

    /// <summary>
    /// CRC-32 and SHA-1 of the first track's raw sectors, as Redump lists for "(Track 1).bin". Read through the disc
    /// image, so a .cue/.bin and a .chd of the same disc hash alike.
    /// </summary>
    public static (string Crc32, string Sha1) HashFirstTrack(ICdImage disc)
    {
        var track = disc.Tracks[0];
        var crc = new Crc32();
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var sector = new byte[ICdImage.SectorSize];
        for (var i = 0; i < track.Length; i++)
        {
            disc.ReadSector(track.Start + i, sector);
            crc.Append(sector);
            sha1.AppendData(sector);
        }

        return (crc.GetCurrentHashAsUInt32().ToString("x8"), Convert.ToHexStringLower(sha1.GetHashAndReset()));
    }

    /// <summary>Hashes the first disc of a reference (see <see cref="HashFirstTrack"/>).</summary>
    public static (string Crc32, string Sha1) HashReference(string path)
    {
        var disc = DiscImages.Open(DiscPaths(path)[0]);
        try
        {
            return HashFirstTrack(disc);
        }
        finally
        {
            (disc as IDisposable)?.Dispose();
        }
    }
}
