using GameVault.Core.PlayStation;
using GameVault.Core.PlayStation2;

namespace GameVault.Library.Discs;

/// <summary>A disc image file opened for the PS2's drive: dispose it to close the files.</summary>
public interface IPs2DiscImage : IPs2Disc, IDisposable;

/// <summary>
/// A PlayStation 2 CD (or any CD image) as <see cref="IPs2Disc"/>: sector <c>n</c> is track 1's sector <c>n</c>, and its
/// user data is bytes 24–2,071 of a mode 2 (form 1) sector or 16–2,063 of a mode 1 sector. Disposing it disposes the
/// CD image. Reads are serialized, so the emulation thread and a hashing thread can share it.
/// </summary>
public sealed class CdPs2Disc : IPs2DiscImage
{
    private readonly Lock _gate = new();
    private readonly byte[] _raw = new byte[ICdImage.SectorSize];
    private readonly int _first;

    public CdPs2Disc(ICdImage cd)
    {
        Cd = cd;
        _first = cd.Tracks[0].Start;
        SectorCount = cd.Tracks[0].Length;
    }

    public Ps2DiscType Type => Ps2DiscType.Cd;

    /// <summary>The sectors of track 1 (the data track).</summary>
    public long SectorCount { get; }

    public long Layer1Start => 0;

    public ICdImage Cd { get; }

    public void ReadSector(long lba, Span<byte> destination)
    {
        if (lba < 0 || lba >= SectorCount)
        {
            destination[..IPs2Disc.SectorSize].Clear();
            return;
        }

        lock (_gate)
        {
            Cd.ReadSector(_first + (int)lba, _raw);
            var start = _raw[15] == 1 ? 16 : 24;
            _raw.AsSpan(start, IPs2Disc.SectorSize).CopyTo(destination);
        }
    }

    public void Dispose() => (Cd as IDisposable)?.Dispose();
}

/// <summary>
/// A DVD image of 2,048-byte sectors (an .iso) as <see cref="IPs2Disc"/>. The file is read in place with positional
/// reads, which are safe from several threads. A dual-layer disc is recognized by the second layer's volume
/// descriptor right after the first layer's volume (see <see cref="Ps2Discs.FindLayer1"/>).
/// </summary>
public sealed class IsoPs2Disc : IPs2DiscImage
{
    private readonly FileStream _stream;

    private IsoPs2Disc(FileStream stream)
    {
        _stream = stream;
        SectorCount = stream.Length / IPs2Disc.SectorSize;
    }

    /// <exception cref="InvalidDataException">The file can't be opened.</exception>
    public static IsoPs2Disc Open(string path)
    {
        var disc = new IsoPs2Disc(DiscFiles.OpenRead(path));
        disc.Layer1Start = Ps2Discs.FindLayer1(disc);
        return disc;
    }

    public Ps2DiscType Type => Layer1Start > 0 ? Ps2DiscType.DvdDualLayer : Ps2DiscType.Dvd;

    public long SectorCount { get; }

    public long Layer1Start { get; private set; }

    public ICdImage? Cd => null;

    public void ReadSector(long lba, Span<byte> destination)
    {
        var sector = destination[..IPs2Disc.SectorSize];
        if (lba < 0 || lba >= SectorCount)
        {
            sector.Clear();
            return;
        }

        DiscFiles.ReadAt(_stream, lba * IPs2Disc.SectorSize, sector);
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// A DVD stored as a CHD, as chdman's <c>createdvd</c> writes it: hunks of 2,048-byte sectors (two by default) in the
/// generic codecs (zlib, lzma, huff, flac), with a <c>DVD </c> metadata entry. Reads are serialized.
/// </summary>
public sealed class ChdDvd : IPs2DiscImage
{
    /// <summary>The metadata tag chdman writes for a DVD.</summary>
    public const string MetadataTag = "DVD ";

    private readonly Lock _gate = new();
    private readonly ChdFile _chd;

    private ChdDvd(ChdFile chd)
    {
        _chd = chd;
        SectorCount = chd.LogicalBytes / IPs2Disc.SectorSize;
    }

    /// <summary>True when the CHD at <paramref name="path"/> holds a DVD (false for CDs and other CHDs).</summary>
    public static bool IsDvd(string path)
    {
        using var chd = ChdFile.Open(path);
        return IsDvd(chd);
    }

    private static bool IsDvd(ChdFile chd) =>
        chd.UnitBytes == IPs2Disc.SectorSize && chd.ReadMetadata().Exists(m => m.Tag == MetadataTag);

    /// <exception cref="InvalidDataException">The file isn't a DVD CHD GameVault can read.</exception>
    public static ChdDvd Open(string path)
    {
        var chd = ChdFile.Open(path);
        if (!IsDvd(chd))
        {
            chd.Dispose();
            throw new InvalidDataException($"'{Path.GetFileName(path)}' isn't a DVD image.");
        }

        var disc = new ChdDvd(chd);
        disc.Layer1Start = Ps2Discs.FindLayer1(disc);
        return disc;
    }

    public Ps2DiscType Type => Layer1Start > 0 ? Ps2DiscType.DvdDualLayer : Ps2DiscType.Dvd;

    public long SectorCount { get; }

    public long Layer1Start { get; private set; }

    public ICdImage? Cd => null;

    /// <summary>Hunks decoded per codec, for tests.</summary>
    internal Dictionary<string, int> DecodedHunks => _chd.DecodedHunks;

    public void ReadSector(long lba, Span<byte> destination)
    {
        var sector = destination[..IPs2Disc.SectorSize];
        if (lba < 0 || lba >= SectorCount)
        {
            sector.Clear();
            return;
        }

        var offset = lba * IPs2Disc.SectorSize;
        lock (_gate)
        {
            _chd.Hunk((int)(offset / _chd.HunkBytes)).Slice((int)(offset % _chd.HunkBytes), IPs2Disc.SectorSize).CopyTo(sector);
        }
    }

    public void Dispose() => _chd.Dispose();
}

/// <summary>
/// An .iso (or .bin) holding one data track as a CD image, for the PlayStation's drive: raw 2,352-byte sectors are read
/// as they are, and 2,048-byte sectors are rebuilt as mode 2 form 1 sectors (sync, header, subheader, EDC and ECC), as
/// a PlayStation disc's data track is.
/// </summary>
public sealed class IsoCdImage : ExtentCdImage
{
    private readonly FileStream _stream;

    private IsoCdImage(FileStream stream, int sectorSize)
    {
        _stream = stream;
        var count = (int)Math.Min(int.MaxValue / 2, stream.Length / sectorSize);
        SetLayout(
            [new CdTrack(1, CdTrackType.Mode2, 150, count, 150)],
            [new DiscExtent(150, count, true, 0, 0, sectorSize, CdTrackType.Mode2, false)],
            150 + count);
    }

    /// <summary>Opens <paramref name="path"/> with the sector size <see cref="Ps2Discs.IsRawImage"/> finds.</summary>
    public static IsoCdImage Open(string path)
    {
        var stream = DiscFiles.OpenRead(path);
        return new IsoCdImage(stream, Ps2Discs.IsRawImage(stream) ? ICdImage.SectorSize : IPs2Disc.SectorSize);
    }

    private protected override void ReadStored(DiscExtent extent, int index, Span<byte> data) =>
        DiscFiles.ReadAt(_stream, extent.Source + (long)index * extent.SectorSize, data);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>File helpers shared by the image readers.</summary>
internal static class DiscFiles
{
    public static FileStream OpenRead(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' can't be opened: {ex.Message}", ex);
        }
    }

    /// <summary>Reads at <paramref name="offset"/> without moving the stream (safe from several threads); zeros past the end.</summary>
    public static void ReadAt(FileStream stream, long offset, Span<byte> data)
    {
        var read = 0;
        while (read < data.Length)
        {
            var n = RandomAccess.Read(stream.SafeFileHandle, data[read..], offset + read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        data[read..].Clear();
    }
}
