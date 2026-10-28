namespace GameVault.Core.PlayStation;

/// <summary>What a track holds: CD-DA audio, or data sectors in mode 1 or mode 2.</summary>
public enum CdTrackType
{
    Audio,
    Mode1,
    Mode2,
}

/// <summary>
/// One track of a disc. <paramref name="Start"/> is the disc address (LBA, 75 sectors a second from MSF 00:00:00) of
/// the track's index 1, and <paramref name="Pregap"/> the sectors of index 0 before it.
/// </summary>
public sealed record CdTrack(int Number, CdTrackType Type, int Start, int Length, int Pregap = 0)
{
    /// <summary>The first disc address after the track.</summary>
    public int End => Start + Length;
}

/// <summary>
/// A CD image the PlayStation's drive reads (SPEC §7.11, Phase 15). The platform implements it over the image files
/// (.cue/.bin, .chd), so a 700 MB disc never has to sit in memory and the core does no file I/O. Reads are pure: the
/// same address always gives the same bytes, which keeps emulation deterministic.
/// </summary>
/// <remarks>
/// Disc addresses count from MSF 00:00:00, so track 1's data starts at 150 (00:02:00) on a normal disc, after its
/// two-second pregap.
/// </remarks>
public interface ICdImage
{
    public const int SectorSize = 2352;

    /// <summary>The tracks in order, numbered from 1.</summary>
    IReadOnlyList<CdTrack> Tracks { get; }

    /// <summary>The disc address of the lead-out (the end of the last track).</summary>
    int LeadOut { get; }

    /// <summary>
    /// Reads the raw 2,352-byte sector at disc address <paramref name="lba"/> (sync, header and data for data tracks;
    /// 588 stereo 16-bit little-endian samples for audio). Pregaps and addresses outside the image read as zeros.
    /// </summary>
    void ReadSector(int lba, Span<byte> destination);
}

/// <summary>
/// A disc image that also holds each sector's subcode (the P–W channels; CD+G graphics live in R–W), as the Sega CD's
/// gate array reads it (genesis-gaps.md §4). Reads are pure, like <see cref="ICdImage.ReadSector"/>.
/// </summary>
public interface ICdSubcode
{
    public const int Size = 96;

    /// <summary>False when the image came without subcode (most don't); then every read is zeros.</summary>
    bool HasSubcode { get; }

    /// <summary>
    /// The 96 subcode symbols of the sector at disc address <paramref name="lba"/> (after the two sync symbols), one
    /// byte each with P in bit 7 down to W in bit 0.
    /// </summary>
    void ReadSubcode(int lba, Span<byte> destination);
}

/// <summary>Subcode layouts.</summary>
public static class CdSubcode
{
    /// <summary>
    /// Turns subcode stored channel by channel (12 bytes of P, then Q, … then W, each MSB first: CloneCD's .sub
    /// layout) into a byte per symbol.
    /// </summary>
    public static void FromChannels(ReadOnlySpan<byte> channels, Span<byte> symbols)
    {
        for (var symbol = 0; symbol < ICdSubcode.Size; symbol++)
        {
            var value = 0;
            for (var channel = 0; channel < 8; channel++)
            {
                var bit = (channels[channel * 12 + (symbol >> 3)] >> (7 - (symbol & 7))) & 1;
                value |= bit << (7 - channel);
            }

            symbols[symbol] = (byte)value;
        }
    }
}

/// <summary>A disc image held in memory (tests and small homebrew discs).</summary>
public sealed class MemoryCdImage : ICdImage, ICdSubcode
{
    private readonly byte[] _data;
    private readonly byte[]? _subcode;

    /// <param name="sectors">Raw 2,352-byte sectors from track 1's index 1 onwards.</param>
    /// <param name="tracks">The tracks, or null for one mode 2 data track covering every sector.</param>
    /// <param name="subcode">96 subcode symbols per sector, in the same order as <paramref name="sectors"/>, or none.</param>
    public MemoryCdImage(byte[] sectors, IReadOnlyList<CdTrack>? tracks = null, byte[]? subcode = null)
    {
        _data = sectors;
        _subcode = subcode;
        var count = sectors.Length / ICdImage.SectorSize;
        Tracks = tracks ?? [new CdTrack(1, CdTrackType.Mode2, 150, count, 150)];
        LeadOut = Tracks[^1].End;
    }

    public bool HasSubcode => _subcode is not null;

    public void ReadSubcode(int lba, Span<byte> destination)
    {
        var offset = (long)(lba - Tracks[0].Start) * ICdSubcode.Size;
        if (_subcode is null || offset < 0 || offset + ICdSubcode.Size > _subcode.Length)
        {
            destination[..ICdSubcode.Size].Clear();
            return;
        }

        _subcode.AsSpan((int)offset, ICdSubcode.Size).CopyTo(destination);
    }

    public IReadOnlyList<CdTrack> Tracks { get; }

    public int LeadOut { get; }

    public void ReadSector(int lba, Span<byte> destination)
    {
        var offset = (long)(lba - Tracks[0].Start) * ICdImage.SectorSize;
        if (offset < 0 || offset + ICdImage.SectorSize > _data.Length)
        {
            destination[..ICdImage.SectorSize].Clear();
            return;
        }

        _data.AsSpan((int)offset, ICdImage.SectorSize).CopyTo(destination);
    }
}
