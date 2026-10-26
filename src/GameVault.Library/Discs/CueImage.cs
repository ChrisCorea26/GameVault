using System.Globalization;
using GameVault.Core.PlayStation;

namespace GameVault.Library.Discs;

/// <summary>
/// A disc as a cue sheet over one or more .bin files (one bin for the whole disc, or one per track). Understands FILE
/// (BINARY, and MOTOROLA for byte-swapped audio), TRACK (AUDIO, MODE1/2048, MODE1/2352, MODE2/2336, MODE2/2352),
/// INDEX 00/01, PREGAP and POSTGAP. Sectors are read from the files as needed.
/// </summary>
/// <remarks>
/// Track 1's index 1 sits at disc address 150 (MSF 00:02:00), after the lead-in pregap. A later track's pregap is
/// its PREGAP (not in the file, read as zeros) followed by its INDEX 00 frames (in the file).
/// </remarks>
public sealed class CueImage : ExtentCdImage
{
    private readonly List<FileStream> _streams = [];

    private CueImage()
    {
    }

    /// <summary>The cue sheet and the .bin files it names.</summary>
    public IReadOnlyList<string> Files { get; private set; } = [];

    private sealed class CueFile(string path, bool bigEndian)
    {
        public string Path { get; } = path;

        public bool BigEndian { get; } = bigEndian;

        public List<CueTrack> Tracks { get; } = [];
    }

    private sealed class CueTrack(int number, CdTrackType type, int sectorSize)
    {
        public int Number { get; } = number;

        public CdTrackType Type { get; } = type;

        public int SectorSize { get; } = sectorSize;

        public int? Index0 { get; set; }

        public int? Index1 { get; set; }

        public int Pregap { get; set; }

        public int Postgap { get; set; }
    }

    /// <exception cref="InvalidDataException">The cue sheet is malformed, or names a file that's missing or unsupported.</exception>
    public static CueImage Open(string cuePath)
    {
        var files = Parse(cuePath);
        var image = new CueImage();
        try
        {
            image.Build(cuePath, files);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>The files a cue sheet is made of (itself, then its .bin files), without opening them.</summary>
    public static IReadOnlyList<string> ListFiles(string cuePath) =>
        [Path.GetFullPath(cuePath), .. Parse(cuePath).Select(f => f.Path)];

    private static List<CueFile> Parse(string cuePath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(cuePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"The cue sheet '{Path.GetFileName(cuePath)}' can't be read: {ex.Message}", ex);
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
        var files = new List<CueFile>();
        CueTrack? track = null;
        for (var lineNumber = 1; lineNumber <= lines.Length; lineNumber++)
        {
            var line = lines[lineNumber - 1].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var space = line.IndexOf(' ');
            var keyword = (space < 0 ? line : line[..space]).ToUpperInvariant();
            var rest = space < 0 ? string.Empty : line[(space + 1)..].Trim();
            switch (keyword)
            {
                case "FILE":
                    files.Add(ParseFile(rest, folder, cuePath, lineNumber));
                    track = null;
                    break;
                case "TRACK":
                    if (files.Count == 0)
                    {
                        throw Error(cuePath, lineNumber, "TRACK comes before any FILE");
                    }

                    track = ParseTrack(rest, cuePath, lineNumber);
                    files[^1].Tracks.Add(track);
                    break;
                case "INDEX":
                {
                    if (track is null)
                    {
                        throw Error(cuePath, lineNumber, "INDEX comes before any TRACK");
                    }

                    var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    {
                        throw Error(cuePath, lineNumber, "INDEX needs a number and a time");
                    }

                    var frames = ParseTime(parts[1], cuePath, lineNumber);
                    if (index == 0)
                    {
                        track.Index0 = frames;
                    }
                    else if (index == 1)
                    {
                        track.Index1 = frames;
                    }

                    break;
                }

                case "PREGAP":
                    if (track is null)
                    {
                        throw Error(cuePath, lineNumber, "PREGAP comes before any TRACK");
                    }

                    track.Pregap = ParseTime(rest, cuePath, lineNumber);
                    break;
                case "POSTGAP":
                    if (track is null)
                    {
                        throw Error(cuePath, lineNumber, "POSTGAP comes before any TRACK");
                    }

                    track.Postgap = ParseTime(rest, cuePath, lineNumber);
                    break;

                // REM, CATALOG, TITLE, PERFORMER, SONGWRITER, FLAGS, ISRC and CDTEXTFILE don't affect the layout.
            }
        }

        if (files.Count == 0 || files.All(f => f.Tracks.Count == 0))
        {
            throw new InvalidDataException($"The cue sheet '{Path.GetFileName(cuePath)}' has no tracks.");
        }

        foreach (var t in files.SelectMany(f => f.Tracks))
        {
            if (t.Index1 is null)
            {
                throw new InvalidDataException($"Track {t.Number} in '{Path.GetFileName(cuePath)}' has no INDEX 01.");
            }
        }

        return files;
    }

    private static CueFile ParseFile(string rest, string folder, string cuePath, int lineNumber)
    {
        var lastSpace = rest.LastIndexOf(' ');
        if (lastSpace < 0)
        {
            throw Error(cuePath, lineNumber, "FILE needs a name and a type");
        }

        var type = rest[(lastSpace + 1)..].ToUpperInvariant();
        var name = rest[..lastSpace].Trim();
        if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
        {
            name = name[1..^1];
        }

        bool bigEndian;
        switch (type)
        {
            case "BINARY":
                bigEndian = false;
                break;
            case "MOTOROLA":
                bigEndian = true;
                break;
            default:
                throw new InvalidDataException(
                    $"'{name}' in '{Path.GetFileName(cuePath)}' is a {type} file. GameVault reads raw .bin tracks only; convert the audio tracks to .bin.");
        }

        return new CueFile(Resolve(folder, name, cuePath), bigEndian);
    }

    /// <summary>Finds a file the cue names: as written, then ignoring case, then by its name alone in the cue's folder.</summary>
    private static string Resolve(string folder, string name, string cuePath)
    {
        var direct = Path.GetFullPath(Path.Combine(folder, name));
        if (File.Exists(direct))
        {
            return direct;
        }

        var fileName = Path.GetFileName(name.Replace('\\', '/'));
        if (Directory.Exists(folder))
        {
            foreach (var candidate in Directory.EnumerateFiles(folder))
            {
                if (string.Equals(Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        throw new InvalidDataException($"The cue sheet '{Path.GetFileName(cuePath)}' names '{name}', which isn't in its folder.");
    }

    private static CueTrack ParseTrack(string rest, string cuePath, int lineNumber)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            throw Error(cuePath, lineNumber, "TRACK needs a number and a mode");
        }

        var (type, size) = parts[1].ToUpperInvariant() switch
        {
            "AUDIO" => (CdTrackType.Audio, 2352),
            "MODE1/2352" => (CdTrackType.Mode1, 2352),
            "MODE1/2048" => (CdTrackType.Mode1, 2048),
            "MODE2/2352" => (CdTrackType.Mode2, 2352),
            "MODE2/2336" => (CdTrackType.Mode2, 2336),
            "MODE2/2048" => (CdTrackType.Mode2, 2048),
            "MODE2/2324" => (CdTrackType.Mode2, 2324),
            var mode => throw Error(cuePath, lineNumber, $"track mode {mode} isn't supported"),
        };
        return new CueTrack(number, type, size);
    }

    private static int ParseTime(string text, string cuePath, int lineNumber)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var frames)
            || seconds >= 60 || frames >= 75)
        {
            throw Error(cuePath, lineNumber, $"'{text}' isn't a time (mm:ss:ff)");
        }

        return (minutes * 60 + seconds) * 75 + frames;
    }

    private static InvalidDataException Error(string cuePath, int lineNumber, string message) =>
        new($"'{Path.GetFileName(cuePath)}', line {lineNumber}: {message}.");

    private void Build(string cuePath, List<CueFile> files)
    {
        var tracks = new List<CdTrack>();
        var extents = new List<DiscExtent>();
        var cursor = -1;
        foreach (var file in files)
        {
            if (file.Tracks.Count == 0)
            {
                continue;
            }

            FileStream stream;
            try
            {
                stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"'{Path.GetFileName(file.Path)}' can't be opened: {ex.Message}", ex);
            }

            _streams.Add(stream);
            var source = _streams.Count - 1;

            // Each track's bytes start at its first index; tracks in one file may differ in sector size.
            var byteStart = (long)(file.Tracks[0].Index0 ?? file.Tracks[0].Index1!.Value) * file.Tracks[0].SectorSize;
            for (var i = 0; i < file.Tracks.Count; i++)
            {
                var t = file.Tracks[i];
                var first = t.Index0 ?? t.Index1!.Value;
                var storedPregap = t.Index1!.Value - first;
                if (storedPregap < 0)
                {
                    throw new InvalidDataException($"Track {t.Number} in '{Path.GetFileName(cuePath)}' has INDEX 00 after INDEX 01.");
                }

                int frames;
                if (i + 1 < file.Tracks.Count)
                {
                    var next = file.Tracks[i + 1];
                    frames = (next.Index0 ?? next.Index1!.Value) - first;
                }
                else
                {
                    frames = (int)Math.Max(0, (stream.Length - byteStart) / t.SectorSize);
                }

                if (frames < storedPregap)
                {
                    throw new InvalidDataException($"Track {t.Number} in '{Path.GetFileName(cuePath)}' is shorter than its pregap.");
                }

                var pregap = t.Pregap + storedPregap;
                if (cursor < 0)
                {
                    cursor = Math.Max(0, 150 - pregap);
                }

                // PREGAP frames aren't in the file: leave them unmapped, so they read as zeros.
                cursor += t.Pregap;
                if (storedPregap > 0)
                {
                    extents.Add(new DiscExtent(cursor, storedPregap, true, source, byteStart, t.SectorSize, t.Type, file.BigEndian));
                }

                var start = cursor + storedPregap;
                var length = frames - storedPregap;
                if (length > 0)
                {
                    extents.Add(new DiscExtent(start, length, true, source, byteStart + (long)storedPregap * t.SectorSize, t.SectorSize, t.Type, file.BigEndian));
                }

                tracks.Add(new CdTrack(t.Number, t.Type, start, length, pregap));
                cursor = start + length + t.Postgap;
                byteStart += (long)frames * t.SectorSize;
            }
        }

        Files = [Path.GetFullPath(cuePath), .. files.Select(f => f.Path)];
        SetLayout(tracks, extents, cursor);
    }

    private protected override void ReadStored(DiscExtent extent, int index, Span<byte> data)
    {
        var stream = _streams[extent.SourceIndex];
        var offset = extent.Source + (long)index * extent.SectorSize;
        var read = 0;
        while (read < data.Length && offset + read < stream.Length)
        {
            var n = RandomAccess.Read(stream.SafeFileHandle, data[read..], offset + read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        // A file that ends early (a truncated dump) reads as zeros past its end.
        data[read..].Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }

            _streams.Clear();
        }

        base.Dispose(disposing);
    }
}
