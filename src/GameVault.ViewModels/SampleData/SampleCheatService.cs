using GameVault.Core.Cheats;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>Codes for <c>--sample-data</c> and tests: made-up codes for the fictional sample games.</summary>
public sealed class SampleCheatService : ICheatService
{
    private readonly Dictionary<string, List<CodeItem>> _codes = [];
    private int _next;

    public event EventHandler<string>? Changed;

    /// <summary>Every call to <see cref="ApplyAsync"/> and every change to the running game's codes, newest last.</summary>
    public List<(string GameKey, IReadOnlyList<CheatCode> Codes)> Applied { get; } = [];

    /// <summary>Text "files" for import, keyed by path.</summary>
    public Dictionary<string, string> Files { get; } = [];

    public Task<IReadOnlyList<CodeItem>> GetAsync(string gameKey)
    {
        if (!_codes.ContainsKey(gameKey))
        {
            // Every sample game starts with two made-up codes, one of them on.
            _codes[gameKey] =
            [
                new CodeItem(NewId(), "SXIOPO", "Infinite lives", true),
                new CodeItem(NewId(), "0300:09", "Start with 9 hearts", false),
            ];
        }

        return Task.FromResult<IReadOnlyList<CodeItem>>([.. _codes[gameKey]]);
    }

    public Task<IReadOnlyList<CodeSuggestion>> GetSuggestionsAsync(string gameKey) =>
        Task.FromResult<IReadOnlyList<CodeSuggestion>>([new CodeSuggestion("AEKPIL", "Jump higher"), new CodeSuggestion("SXIOPO", "Infinite lives")]);

    public async Task AddAsync(string gameKey, string code, string description)
    {
        await GetAsync(gameKey);
        _codes[gameKey].Add(new CodeItem(NewId(), CheatCode.Normalize(code), description.Trim(), true));
        Raise(gameKey);
    }

    public async Task SetEnabledAsync(string gameKey, string id, bool enabled)
    {
        await GetAsync(gameKey);
        _codes[gameKey] = _codes[gameKey].Select(c => c.Id == id ? c with { Enabled = enabled } : c).ToList();
        Raise(gameKey);
    }

    public async Task SetAllEnabledAsync(string gameKey, bool enabled)
    {
        await GetAsync(gameKey);
        _codes[gameKey] = _codes[gameKey].Select(c => c with { Enabled = enabled }).ToList();
        Raise(gameKey);
    }

    public async Task DeleteAsync(string gameKey, string id)
    {
        await GetAsync(gameKey);
        _codes[gameKey].RemoveAll(c => c.Id == id);
        Raise(gameKey);
    }

    public async Task<CodeImportResult> ImportAsync(string gameKey, string path)
    {
        await GetAsync(gameKey);
        var errors = new List<string>();
        var added = new List<CodeItem>();
        var line = 0;
        foreach (var text in (Files.GetValueOrDefault(path) ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries))
        {
            line++;
            if (text.Length == 0)
            {
                continue;
            }

            var code = text.Split(' ', 2)[0];
            if (CheatCode.TryParse(code, out _, out var error))
            {
                added.Add(new CodeItem(NewId(), CheatCode.Normalize(code), text.Length > code.Length ? text[code.Length..].Trim() : string.Empty, true));
            }
            else
            {
                errors.Add($"Line {line}: {error}");
            }
        }

        if (errors.Count > 0)
        {
            return new CodeImportResult(0, errors);
        }

        _codes[gameKey].AddRange(added);
        Raise(gameKey);
        return new CodeImportResult(added.Count, []);
    }

    public Task ExportAsync(string gameKey, string path)
    {
        Files[path] = string.Join("\n", _codes.GetValueOrDefault(gameKey, []).Select(c => $"{c.Code}  {c.Description}"));
        return Task.CompletedTask;
    }

    public async Task ApplyAsync(string gameKey)
    {
        var codes = await GetAsync(gameKey);
        Applied.Add((gameKey, codes.Where(c => c.Enabled).Select(c => CheatCode.Parse(c.Code)).ToList()));
    }

    private string NewId() => (++_next).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void Raise(string gameKey)
    {
        Applied.Add((gameKey, _codes[gameKey].Where(c => c.Enabled).Select(c => CheatCode.Parse(c.Code)).ToList()));
        Changed?.Invoke(this, gameKey);
    }
}

/// <summary>The file picker for <c>--sample-data</c>: returns preset paths (null means the player cancelled).</summary>
public sealed class SampleCodeFilePicker : ICodeFilePicker
{
    public string? ImportPath { get; set; }

    public string? ExportPath { get; set; }

    public Task<string?> PickImportFileAsync() => Task.FromResult(ImportPath);

    public Task<string?> PickExportFileAsync(string suggestedName) => Task.FromResult(ExportPath);
}
