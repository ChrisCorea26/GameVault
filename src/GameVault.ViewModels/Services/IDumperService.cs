namespace GameVault.ViewModels.Services;

/// <summary>A board the dumper can read, with typical ROM sizes to choose from.</summary>
public sealed record DumpBoard(int Mapper, string Name, IReadOnlyList<int> PrgSizesKb, IReadOnlyList<int> ChrSizesKb);

/// <summary>The result of a dump in plain language (SPEC §8).</summary>
public sealed record DumpOutcome(bool Succeeded, bool MatchesCatalog, string Message, string? GameKey);

/// <summary>
/// The cartridge dumper (SPEC §8): optional, and the app works fully without one. A successful dump is
/// added to the library and marked "Owned: Cartridge + Digital dump".
/// </summary>
public interface IDumperService
{
    /// <summary>The dumper's name ("INL Retro-Prog"), or null when none is set up.</summary>
    string? DumperName { get; }

    IReadOnlyList<DumpBoard> Boards { get; }

    /// <param name="catalogGameKey">The catalog game the player picked, to verify against; null if unknown.</param>
    Task<DumpOutcome> DumpAsync(
        string? catalogGameKey,
        string title,
        DumpBoard board,
        int prgKb,
        int chrKb,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
