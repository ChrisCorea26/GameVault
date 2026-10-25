using System.Text.Json;

namespace GameVault.Library.Profiles;

/// <summary>
/// The bundled game database: every <c>*.json</c> in a folder (normally <c>data/gamedb</c>), each holding one
/// profile or an array of them. Files that fail to parse are skipped and reported, never fatal.
/// </summary>
public sealed class GameProfiles
{
    private readonly Dictionary<string, GameProfile> _bySha1 = new(StringComparer.OrdinalIgnoreCase);

    public GameProfiles(string? folder)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            try
            {
                foreach (var profile in GameProfile.ReadFile(File.ReadAllText(file)))
                {
                    if (profile.Sha1 is { Length: 40 } sha1)
                    {
                        _bySha1[sha1] = profile;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    public int Count => _bySha1.Count;

    /// <summary>Files that could not be read.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>The profile for a game with the user's overrides applied (an empty profile when unknown).</summary>
    public GameProfile Resolve(string? sha1, GameProfile? userOverrides)
    {
        var profile = sha1 is not null && _bySha1.TryGetValue(sha1, out var known) ? known : new GameProfile { Sha1 = sha1 };
        return profile.Merge(userOverrides);
    }
}
