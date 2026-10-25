using System.Text;
using System.Text.RegularExpressions;

namespace GameVault.Library.Cheats;

/// <summary>
/// Matches game titles written in different ways: "Super Mario Bros. (World)" (No-Intro), "SUPER MARIO BROS.",
/// "Legend of Zelda, The", "Mega Man II" and "Mega Man 2" all compare equal after <see cref="Key"/>.
/// </summary>
public static partial class GameTitles
{
    private static readonly Dictionary<string, string> Roman = new()
    {
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9", ["x"] = "10",
    };

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Bracketed();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    /// <summary>A comparison key: lower case, no region tags, punctuation, leading/trailing "the", or roman numerals.</summary>
    public static string Key(string? title)
    {
        var text = Bracketed().Replace(title ?? string.Empty, " ").ToLowerInvariant().Replace("&", " and ").Replace("'", string.Empty);
        var words = NonAlphanumeric().Split(text).Where(w => w.Length > 0).ToList();
        if (words.Count > 1 && words[0] == "the")
        {
            words.RemoveAt(0);
        }

        if (words.Count > 1 && words[^1] == "the")
        {
            words.RemoveAt(words.Count - 1);
        }

        var key = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            // Roman numerals only count as numbers after the first word ("X-Men" stays "x men").
            key.Append(i > 0 && Roman.TryGetValue(words[i], out var number) ? number : words[i]);
        }

        return key.ToString();
    }
}
