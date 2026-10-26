using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using GameVault.Core.PlayStation;

namespace GameVault.Library.Discs;

/// <summary>
/// A CD stored as a CHD ("MAME Compressed Hunks of Data", version 5), as chdman's <c>createcd</c> writes it: hunks
/// of CD frames (2,352 bytes of sector plus 96 of subcode), each compressed with one of up to four codecs, a
/// Huffman-coded hunk map, and track metadata. Supports the CD codecs <c>cdzl</c> (deflate), <c>cdlz</c> (LZMA,
/// subcode in deflate) and <c>cdfl</c> (FLAC audio, subcode in deflate), plain <c>zlib</c> and <c>lzma</c>,
/// uncompressed hunks and copies of earlier hunks (the container is read by <see cref="ChdFile"/>). CHDs that need a
/// parent CHD are refused.
/// </summary>
/// <remarks>
/// Written from the CHD v5 format as chdman produces it. Audio in a CHD is stored big-endian and is swapped back on
/// reading. Each track is padded to a multiple of four frames in the CHD; pregaps whose type starts with "V" are
/// stored at the front of the track, others are not stored.
/// </remarks>
public sealed class ChdImage : ExtentCdImage
{
    private const int FrameSize = ChdFile.CdFrameSize;
    private const int TrackPadding = 4;

    private readonly ChdFile _chd;

    /// <summary>Sectors whose sync and ECC were regenerated (tests check the ECC path ran).</summary>
    internal int RegeneratedSectors => _chd.RegeneratedSectors;

    /// <summary>Hunks decoded per codec name ("none" and "self" for stored and copied hunks), for tests.</summary>
    internal Dictionary<string, int> DecodedHunks => _chd.DecodedHunks;

    private ChdImage(ChdFile chd)
    {
        _chd = chd;
    }

    /// <exception cref="InvalidDataException">The file isn't a CD CHD GameVault can read.</exception>
    public static ChdImage Open(string path)
    {
        var chd = ChdFile.Open(path);
        var image = new ChdImage(chd);
        try
        {
            image.Load(Path.GetFileName(path));
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private void Load(string name)
    {
        if (_chd.UnitBytes == 2048 && _chd.ReadMetadata().Exists(m => m.Tag == ChdDvd.MetadataTag))
        {
            throw new InvalidDataException($"'{name}' is a DVD image, not a CD.");
        }

        if (_chd.HunkBytes % FrameSize != 0 || _chd.UnitBytes != FrameSize)
        {
            throw new InvalidDataException($"'{name}' isn't a CD image (its hunks aren't made of CD frames).");
        }

        BuildLayout(ReadTrackMetadata(name), name);
    }

    /// <summary>CRC-16/CCITT, as CHD uses for the map and each hunk.</summary>
    internal static ushort Crc16(ReadOnlySpan<byte> data) => ChdFile.Crc16(data);

    // ---- Track metadata ----

    private sealed record ChdTrack(int Number, string Type, int Frames, int Pregap, string PregapType, int Postgap);

    private List<ChdTrack> ReadTrackMetadata(string name)
    {
        var tracks = new List<ChdTrack>();
        foreach (var (tag, data) in _chd.ReadMetadata())
        {
            if (tag is "CHCD")
            {
                throw new InvalidDataException($"'{name}' uses old binary CD metadata; update it with \"chdman copy\".");
            }

            if (tag is "CHGD")
            {
                throw new InvalidDataException($"'{name}' is a GD-ROM image, not a CD.");
            }

            if (tag is "CHT2" or "CHTR")
            {
                tracks.Add(ParseTrack(Encoding.ASCII.GetString(data).TrimEnd('\0'), name));
            }
        }

        if (tracks.Count == 0)
        {
            throw new InvalidDataException($"'{name}' has no CD track list; it may be a hard disk or LaserDisc CHD.");
        }

        tracks.Sort((a, b) => a.Number.CompareTo(b.Number));
        return tracks;
    }

    private static ChdTrack ParseTrack(string text, string name)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':');
            if (colon > 0)
            {
                fields[part[..colon]] = part[(colon + 1)..];
            }
        }

        int Number(string key) =>
            fields.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        if (!fields.TryGetValue("TYPE", out var type) || Number("TRACK") <= 0)
        {
            throw new InvalidDataException($"'{name}' has corrupt track metadata: \"{text}\".");
        }

        return new ChdTrack(Number("TRACK"), type, Number("FRAMES"), Number("PREGAP"), fields.GetValueOrDefault("PGTYPE", string.Empty), Number("POSTGAP"));
    }

    private static (CdTrackType Type, int SectorSize) TrackFormat(string type, string name) => type.ToUpperInvariant() switch
    {
        "AUDIO" => (CdTrackType.Audio, 2352),
        "MODE1_RAW" => (CdTrackType.Mode1, 2352),
        "MODE1" => (CdTrackType.Mode1, 2048),
        "MODE2_RAW" => (CdTrackType.Mode2, 2352),
        "MODE2" or "MODE2_FORM_MIX" => (CdTrackType.Mode2, 2336),
        "MODE2_FORM1" => (CdTrackType.Mode2, 2048),
        "MODE2_FORM2" => (CdTrackType.Mode2, 2324),
        _ => throw new InvalidDataException($"'{name}' has a track of type {type}, which GameVault doesn't read."),
    };

    private void BuildLayout(List<ChdTrack> chdTracks, string name)
    {
        var tracks = new List<CdTrack>();
        var extents = new List<DiscExtent>();
        var chdFrame = 0L;
        var cursor = -1;
        foreach (var t in chdTracks)
        {
            var (type, size) = TrackFormat(t.Type, name);
            var pregapStored = t.PregapType.StartsWith('V') ? t.Pregap : 0;
            if (cursor < 0)
            {
                cursor = Math.Max(0, 150 - t.Pregap);
            }

            // A pregap that isn't stored reads as zeros; a stored one is the first frames of the track.
            cursor += t.Pregap - pregapStored;
            if (pregapStored > 0)
            {
                extents.Add(new DiscExtent(cursor, pregapStored, true, 0, chdFrame, size, type, type == CdTrackType.Audio));
            }

            var start = cursor + pregapStored;
            var length = t.Frames - pregapStored;
            if (length > 0)
            {
                extents.Add(new DiscExtent(start, length, true, 0, chdFrame + pregapStored, size, type, type == CdTrackType.Audio));
            }

            tracks.Add(new CdTrack(t.Number, type, start, Math.Max(0, length), t.Pregap));
            cursor = start + Math.Max(0, length) + t.Postgap;
            chdFrame += (t.Frames + TrackPadding - 1) / TrackPadding * TrackPadding;
        }

        SetLayout(tracks, extents, cursor);
    }

    // ---- Hunks ----

    private protected override void ReadStored(DiscExtent extent, int index, Span<byte> data)
    {
        var frame = extent.Source + index;
        var framesPerHunk = _chd.HunkBytes / FrameSize;
        var hunk = (int)(frame / framesPerHunk);
        if (hunk >= _chd.HunkCount)
        {
            data.Clear();
            return;
        }

        var offset = (int)(frame % framesPerHunk) * FrameSize;
        _chd.Hunk(hunk).Slice(offset, data.Length).CopyTo(data);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _chd.Dispose();
        }

        base.Dispose(disposing);
    }
}
