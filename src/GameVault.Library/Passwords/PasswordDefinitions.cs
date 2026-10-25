using System.Text.Json;

namespace GameVault.Library.Passwords;

/// <summary>
/// The bundled password definitions: every <c>*.json</c> in <c>data/gamedb/passwords</c>, keyed by headerless
/// SHA-1 (SPEC §7.3, §7.5). Broken files are skipped and reported.
/// </summary>
public sealed class PasswordDefinitions
{
    private readonly Dictionary<string, PasswordDefinition> _bySha1 = new(StringComparer.OrdinalIgnoreCase);

    public PasswordDefinitions(string? folder)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var definition = PasswordDefinition.Parse(File.ReadAllText(file));
                if (definition.Sha1 is { Length: 40 } sha1)
                {
                    _bySha1[sha1] = definition;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or FormatException)
            {
                Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    public int Count => _bySha1.Count;

    public List<string> Errors { get; } = [];

    public PasswordDefinition? For(string? sha1) => sha1 is not null && _bySha1.TryGetValue(sha1, out var d) ? d : null;
}
