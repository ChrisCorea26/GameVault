using GameVault.Core.Cheats;
using GameVault.Library.Cheats;
using GameVault.Platform.Emulation;
using GameVault.Platform.Online;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Storage;

/// <summary>
/// Game Genie and RAM codes (SPEC §7.8): the player's lists in <see cref="CheatStore"/>, suggestions from the
/// bundled database, and the running game's active codes in <see cref="EmulatorHost"/>. When a save state or
/// rewind brings back other codes, the list follows, so the switches always show what is on.
/// </summary>
public sealed class CheatService : ICheatService
{
    private readonly EmulatorHost _host;
    private readonly CheatStore _store;
    private readonly CheatSuggestions _suggestions;
    private readonly CodeCollections? _collections;
    private readonly GameGenieDotCom? _online;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public CheatService(EmulatorHost host, CheatStore store, CheatSuggestions suggestions, CodeCollections? collections = null, GameGenieDotCom? online = null)
    {
        _host = host;
        _store = store;
        _suggestions = suggestions;
        _collections = collections;
        _online = online;
        _host.CheatsChanged += OnHostCheatsChanged;
    }

    /// <summary>Shown with every code that comes from GameGenie.com.</summary>
    public const string GameGenieDotComCredit =
        "These codes come from GameGenie.com (© Danworld, Inc.). GameVault downloads them from the site when you ask; they aren't part of GameVault.";

    public Task<IReadOnlyList<CodeCollectionInfo>> GetCollectionsAsync() => Task.Run(() =>
        (IReadOnlyList<CodeCollectionInfo>)(_collections?.List().Select(c => new CodeCollectionInfo(c.Name, c.Games, c.Codes)).ToList() ?? []));

    public Task<CodeCollectionImportResult> ImportCollectionAsync(string path) => Task.Run(() =>
    {
        if (_collections is null)
        {
            return new CodeCollectionImportResult(Path.GetFileNameWithoutExtension(path), 0, 0, ["Code collections aren't available."]);
        }

        var result = _collections.Import(path);
        return new CodeCollectionImportResult(result.Name, result.Games, result.Codes, result.Errors);
    });

    public Task RemoveCollectionAsync(string name) => Task.Run(() => _collections?.Remove(name));

    public Task<CodeSourceResult?> GetCollectionCodesAsync(string gameKey, string? title) => Task.Run(() =>
    {
        if (_collections is null)
        {
            return null;
        }

        var matches = _collections.Find(_store.GetCollectionTitle(gameKey) ?? title);
        if (matches.Count == 0)
        {
            return null;
        }

        var codes = matches.SelectMany(m => m.Game.Codes).DistinctBy(c => c.Code).Select(c => new CodeSuggestion(c.Code, c.Description)).ToList();
        var sources = matches.SelectMany(m => m.Game.Codes).Select(c => c.Source).Where(s => s.Length > 0).Distinct().ToList();
        var credit = $"From your code collection \"{string.Join("\", \"", matches.Select(m => m.Collection).Distinct())}\""
            + (sources.Count > 0 ? $" (source: {string.Join(", ", sources)})." : ".");
        return (CodeSourceResult?)new CodeSourceResult(matches[0].Game.Title, credit, null, codes, matches.SelectMany(m => m.Game.Notes).ToList());
    });

    public Task<IReadOnlyList<string>> SearchCollectionsAsync(string text) =>
        Task.Run(() => _collections?.Search(text) ?? (IReadOnlyList<string>)[]);

    public Task SetCollectionTitleAsync(string gameKey, string collectionTitle) => Task.Run(() => _store.SetCollectionTitle(gameKey, collectionTitle));

    public async Task<CodeSourceResult?> GetOnlineCodesAsync(string gameKey, string? title)
    {
        if (_online is null || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        try
        {
            var page = await _online.GetCodesAsync(_store.GetCollectionTitle(gameKey) ?? title).ConfigureAwait(false)
                ?? await _online.GetCodesAsync(title).ConfigureAwait(false);
            return page is null
                ? null
                : new CodeSourceResult(page.Title, GameGenieDotComCredit, page.PageUrl, page.Codes.Select(c => new CodeSuggestion(c.Code, c.Description)).ToList(), []);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new CodeSourceException("GameVault couldn't reach GameGenie.com. Check the internet connection and try again.", ex);
        }
    }

    public event EventHandler<string>? Changed;

    public Task<IReadOnlyList<CodeItem>> GetAsync(string gameKey) => Task.Run(() =>
        (IReadOnlyList<CodeItem>)_store.List(gameKey).Select(c => new CodeItem(c.Id, c.Code, c.Description, c.Enabled)).ToList());

    public Task<IReadOnlyList<CodeSuggestion>> GetSuggestionsAsync(string gameKey) => Task.FromResult(
        (IReadOnlyList<CodeSuggestion>)_suggestions.For(Sha1(gameKey)).Select(s => new CodeSuggestion(s.Code, s.Description)).ToList());

    public Task AddAsync(string gameKey, string code, string description) =>
        ChangeAsync(gameKey, () => _store.Add(gameKey, code, description));

    public Task SetEnabledAsync(string gameKey, string id, bool enabled) => ChangeAsync(gameKey, () =>
    {
        if (_store.List(gameKey).FirstOrDefault(c => c.Id == id) is { } code)
        {
            _store.Update(gameKey, code with { Enabled = enabled });
        }
    });

    public Task SetAllEnabledAsync(string gameKey, bool enabled) => ChangeAsync(gameKey, () => _store.SetAllEnabled(gameKey, enabled));

    public Task DeleteAsync(string gameKey, string id) => ChangeAsync(gameKey, () => _store.Delete(gameKey, id));

    public async Task<CodeImportResult> ImportAsync(string gameKey, string path)
    {
        CheatImport import;
        try
        {
            import = CheatFiles.Import(await File.ReadAllTextAsync(path).ConfigureAwait(false), Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CodeImportResult(0, ["GameVault couldn't read that file. " + ex.Message]);
        }

        if (!import.Succeeded)
        {
            return new CodeImportResult(0, import.Errors);
        }

        var added = 0;
        await ChangeAsync(gameKey, () => added = _store.AddRange(gameKey, import.Codes)).ConfigureAwait(false);
        return new CodeImportResult(added, []);
    }

    public Task ExportAsync(string gameKey, string path) =>
        File.WriteAllTextAsync(path, CheatFiles.Export(_store.List(gameKey)));

    public Task ApplyAsync(string gameKey) => Task.Run(() => PushIfRunning(gameKey));

    private async Task ChangeAsync(string gameKey, Action change)
    {
        await Task.Run(() =>
        {
            change();
            PushIfRunning(gameKey);
        }).ConfigureAwait(false);
        Raise(gameKey);
    }

    private void PushIfRunning(string gameKey)
    {
        if (_host.GameKey == gameKey)
        {
            _host.SetCheats(_store.List(gameKey).Where(c => c.Enabled).SelectMany(c => c.Parsed).ToList());
        }
    }

    /// <summary>A load, rewind or F3 changed what is on: make the saved switches match.</summary>
    private async void OnHostCheatsChanged(object? sender, IReadOnlyList<CheatCode> active)
    {
        if (_host.GameKey is not { } key)
        {
            return;
        }

        await Task.Run(() =>
        {
            foreach (var code in _store.List(key))
            {
                var on = code.Parsed.All(active.Contains);
                if (on != code.Enabled)
                {
                    _store.Update(key, code with { Enabled = on });
                }
            }
        });
        Raise(key);
    }

    private static string? Sha1(string gameKey) => gameKey.StartsWith("sha1:", StringComparison.Ordinal) ? gameKey[5..] : null;

    private void Raise(string gameKey)
    {
        if (_context is null)
        {
            Changed?.Invoke(this, gameKey);
        }
        else
        {
            _context.Post(_ => Changed?.Invoke(this, gameKey), null);
        }
    }
}
