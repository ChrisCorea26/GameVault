using GameVault.Core.PlayStation;

namespace GameVault.Library.Discs;

/// <summary>Opens the disc image formats GameVault reads for CD systems: .cue/.bin, .chd, and .m3u playlists of discs.</summary>
public static class DiscImages
{
    public static bool IsDiscImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cue" or ".chd" or ".m3u";

    /// <summary>Opens one disc (.cue or .chd). The result is also <see cref="IDisposable"/>: dispose it to close the files.</summary>
    /// <exception cref="InvalidDataException">The image is malformed, missing files, or in a format GameVault doesn't read.</exception>
    public static ICdImage Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' doesn't exist.");
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cue" => CueImage.Open(path),
            ".chd" => ChdImage.Open(path),
            ".m3u" => throw new InvalidDataException($"'{Path.GetFileName(path)}' is a playlist: open one of its discs."),
            _ => throw new InvalidDataException($"'{Path.GetFileName(path)}' isn't a disc image (.cue or .chd)."),
        };
    }

    /// <summary>The discs an .m3u lists, as full paths in order. Blank lines and '#' comments are skipped.</summary>
    public static IReadOnlyList<string> ReadPlaylist(string m3uPath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(m3uPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"The playlist '{Path.GetFileName(m3uPath)}' can't be read: {ex.Message}", ex);
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(m3uPath)) ?? ".";
        var discs = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            discs.Add(Path.GetFullPath(Path.Combine(folder, line.Replace('\\', Path.DirectorySeparatorChar))));
        }

        if (discs.Count == 0)
        {
            throw new InvalidDataException($"The playlist '{Path.GetFileName(m3uPath)}' lists no discs.");
        }

        return discs;
    }

    /// <summary>The files a disc is made of: the .cue and its tracks, the .chd, or for an .m3u, itself and every disc's files.</summary>
    public static IReadOnlyList<string> Files(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cue" => CueImage.ListFiles(path),
        ".m3u" => [Path.GetFullPath(path), .. ReadPlaylist(path).SelectMany(Files)],
        _ => [Path.GetFullPath(path)],
    };
}
