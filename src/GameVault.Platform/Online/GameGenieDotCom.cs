using System.Net;
using System.Text.RegularExpressions;
using GameVault.Core.Cheats;
using GameVault.Library.Cheats;

namespace GameVault.Platform.Online;

/// <summary>A game's codes as listed on GameGenie.com.</summary>
public sealed record OnlineCodePage(string Title, Uri PageUrl, IReadOnlyList<(string Code, string Description)> Codes);

/// <summary>
/// Gets a game's codes from GameGenie.com's NES Game Genie pages (SPEC §7.8) when the player asks for them.
/// The codes belong to GameGenie.com (© Danworld, Inc.): GameVault doesn't include them, it shows them with
/// credit and a link to the page they came from. Pages are cached in the player's app folder (the game list
/// for a week, each game's page for 30 days) so the site isn't asked again for the same game.
/// </summary>
public sealed partial class GameGenieDotCom(HttpClient http, string cacheFolder)
{
    public static readonly Uri NesIndex = new("https://gamegenie.com/cheats/gamegenie/nes/index.html");

    [GeneratedRegex("<a href=\"([a-z0-9_]+\\.html)\">([^<]+)</a>")]
    private static partial Regex GameLink();

    [GeneratedRegex("<pre id=\"content\">(.*?)</pre>", RegexOptions.Singleline)]
    private static partial Regex ContentBlock();

    /// <summary>A code (or "A + B" codes) followed by two or more spaces and what it does.</summary>
    [GeneratedRegex(@"^([A-Za-z]{6}(?:[A-Za-z]{2})?(?:\s*\+\s*[A-Za-z]{6}(?:[A-Za-z]{2})?)*)\s{2,}(.+)$")]
    private static partial Regex CodeLine();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    /// <summary>The games the site lists, as (title, page).</summary>
    public async Task<IReadOnlyList<(string Title, Uri Page)>> GetGamesAsync(CancellationToken cancellationToken = default)
    {
        var html = await GetCachedAsync(NesIndex, "index.html", TimeSpan.FromDays(7), cancellationToken).ConfigureAwait(false);
        return ParseIndex(html);
    }

    /// <summary>The site's page for a game, matched by title; null when the site has no page for it.</summary>
    public async Task<OnlineCodePage?> GetCodesAsync(string title, CancellationToken cancellationToken = default)
    {
        var key = GameTitles.Key(title);
        var games = await GetGamesAsync(cancellationToken).ConfigureAwait(false);
        var (match, page) = games.FirstOrDefault(g => GameTitles.Key(g.Title) == key);
        if (page is null)
        {
            return null;
        }

        var html = await GetCachedAsync(page, Path.GetFileName(page.AbsolutePath), TimeSpan.FromDays(30), cancellationToken).ConfigureAwait(false);
        return new OnlineCodePage(match, page, ParseCodes(html));
    }

    internal static IReadOnlyList<(string Title, Uri Page)> ParseIndex(string html) =>
        GameLink().Matches(html)
            .Select(m => (WebUtility.HtmlDecode(m.Groups[2].Value).Trim(), new Uri(NesIndex, m.Groups[1].Value)))
            .ToList();

    internal static IReadOnlyList<(string Code, string Description)> ParseCodes(string html)
    {
        var block = ContentBlock().Match(html);
        if (!block.Success)
        {
            return [];
        }

        var codes = new List<(string, string)>();
        foreach (var raw in WebUtility.HtmlDecode(Tag().Replace(block.Groups[1].Value, string.Empty)).Split('\n'))
        {
            var line = CodeLine().Match(raw.Trim());
            if (line.Success && CheatCode.TryParseList(line.Groups[1].Value, out _, out _))
            {
                codes.Add((CheatCode.Normalize(line.Groups[1].Value), line.Groups[2].Value.Trim()));
            }
        }

        return codes;
    }

    private async Task<string> GetCachedAsync(Uri url, string fileName, TimeSpan maxAge, CancellationToken cancellationToken)
    {
        var path = Path.Combine(cacheFolder, fileName);
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < maxAge)
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var html = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(cacheFolder);
            await File.WriteAllTextAsync(path, html, cancellationToken).ConfigureAwait(false);
            return html;
        }
        catch (HttpRequestException) when (File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false); // offline: an old copy will do
        }
    }
}
