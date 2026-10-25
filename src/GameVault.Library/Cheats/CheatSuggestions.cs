using System.Text.Json;
using GameVault.Core.Cheats;

namespace GameVault.Library.Cheats;

/// <summary>A code from the bundled database, shown read-only as a suggestion.</summary>
public sealed record SuggestedCode(string Code, string Description);

/// <summary>
/// The bundled code database (SPEC §7.8): <c>data/gamedb/cheats/&lt;sha1&gt;.json</c>, each
/// <c>{ "sha1": "…", "title": "…", "codes": [ { "code": "…", "description": "…" } ] }</c>. Only codes from
/// sources whose license allows redistribution are added, each listed in <c>data/gamedb/cheats/SOURCES.md</c>.
/// Broken files and codes are skipped and reported.
/// </summary>
public sealed class CheatSuggestions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, IReadOnlyList<SuggestedCode>> _bySha1 = new(StringComparer.OrdinalIgnoreCase);

    public CheatSuggestions(string? folder)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(file), Json);
                if (entry?.Sha1 is not { Length: 40 } sha1)
                {
                    Errors.Add($"{Path.GetFileName(file)}: missing \"sha1\".");
                    continue;
                }

                var codes = new List<SuggestedCode>();
                foreach (var code in entry.Codes ?? [])
                {
                    if (CheatCode.TryParse(code.Code, out _, out var error))
                    {
                        codes.Add(new SuggestedCode(CheatCode.Normalize(code.Code), code.Description ?? string.Empty));
                    }
                    else
                    {
                        Errors.Add($"{Path.GetFileName(file)}: {code.Code}: {error}");
                    }
                }

                _bySha1[sha1] = codes;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    public List<string> Errors { get; } = [];

    public int Count => _bySha1.Count;

    public IReadOnlyList<SuggestedCode> For(string? sha1) =>
        sha1 is not null && _bySha1.TryGetValue(sha1, out var codes) ? codes : [];

    private sealed record FileModel(string? Sha1, string? Title, List<CodeModel>? Codes);

    private sealed record CodeModel(string Code, string? Description);
}
