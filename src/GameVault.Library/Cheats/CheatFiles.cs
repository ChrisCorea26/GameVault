using System.Globalization;
using System.Text;
using GameVault.Core.Cheats;

namespace GameVault.Library.Cheats;

/// <summary>An imported code list: every code, or (if any line was bad) none and the reasons.</summary>
public sealed record CheatImport(IReadOnlyList<(string Code, string Description, bool Enabled)> Codes, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>
/// Reads and writes code lists (SPEC §7.8) so players can bring their own collections:
/// <list type="bullet">
/// <item>Plain text: one <c>CODE  description</c> per line, <c># comments</c> and blank lines ignored.</item>
/// <item>FCEUX <c>.cht</c> files: <c>[C][S][:]AAAA:VV[:CC]:name</c> (C = has a compare value, S = replaces
/// reads, ':' = switched off).</item>
/// </list>
/// Nothing is half-imported: one bad line rejects the file, with line numbers in the errors.
/// </summary>
public static class CheatFiles
{
    public static CheatImport Import(string text, string fileName) =>
        fileName.EndsWith(".cht", StringComparison.OrdinalIgnoreCase) ? ImportCht(text) : ImportText(text);

    public static string Export(IEnumerable<SavedCode> codes)
    {
        var builder = new StringBuilder("# GameVault codes: CODE  description\n");
        foreach (var code in codes)
        {
            builder.Append(code.Code);
            if (code.Description.Length > 0)
            {
                builder.Append("  ").Append(code.Description.ReplaceLineEndings(" "));
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static CheatImport ImportText(string text)
    {
        var codes = new List<(string, string, bool)>();
        var errors = new List<string>();
        var lineNumber = 0;
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var space = line.IndexOfAny([' ', '\t']);
            var codeText = space < 0 ? line : line[..space];
            var description = space < 0 ? string.Empty : line[space..].Trim();
            if (CheatCode.TryParse(codeText, out _, out var error))
            {
                codes.Add((CheatCode.Normalize(codeText), description, true));
            }
            else
            {
                errors.Add($"Line {lineNumber}: {error}");
            }
        }

        return errors.Count > 0 ? new CheatImport([], errors) : new CheatImport(codes, []);
    }

    private static CheatImport ImportCht(string text)
    {
        var codes = new List<(string, string, bool)>();
        var errors = new List<string>();
        var lineNumber = 0;
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // FCEUX writes "C" (has a compare value), then "S" (replaces reads), then ":" when switched off.
            var position = 0;
            var hasCompare = false;
            while (position < line.Length && line[position] is 'S' or 'C' or 's' or 'c')
            {
                hasCompare |= char.ToUpperInvariant(line[position]) == 'C';
                position++;
            }

            var enabled = !(position < line.Length && line[position] == ':');
            var fields = line[(enabled ? position : position + 1)..].Split(':', hasCompare ? 4 : 3);
            if (fields.Length < (hasCompare ? 4 : 3)
                || !TryHex(fields[0], 0xFFFF, out var address)
                || !TryHex(fields[1], 0xFF, out var value)
                || (hasCompare && !TryHex(fields[2], 0xFF, out _)))
            {
                errors.Add($"Line {lineNumber}: this isn't an FCEUX code line (AAAA:VV:name).");
                continue;
            }

            byte? compare = hasCompare ? (byte)int.Parse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
            var name = fields[^1].Trim();

            // ROM addresses become read replacements (like the Game Genie); RAM addresses are written every frame.
            var kind = address >= 0x8000 ? CheatKind.Rom : CheatKind.Ram;
            if (kind == CheatKind.Ram && address is >= 0x0800 and < 0x6000)
            {
                errors.Add($"Line {lineNumber}: ${address:X4} isn't RAM or ROM, so GameVault can't apply it.");
                continue;
            }

            codes.Add((new CheatCode(kind, (ushort)address, (byte)value, compare).ToString(), name, enabled));
        }

        return errors.Count > 0 ? new CheatImport([], errors) : new CheatImport(codes, []);
    }

    private static bool TryHex(string text, int max, out int value) =>
        int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) && text.Length is > 0 and <= 4 && value <= max;
}
