using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>A simulated INL Retro-Prog for <c>--sample-data</c> and tests: every dump "matches the catalog".</summary>
public sealed class SampleDumperService : IDumperService
{
    public string? DumperName => "INL Retro-Prog (simulated)";

    public IReadOnlyList<DumpBoard> Boards { get; } = DumperBoards.All;

    public async Task<DumpOutcome> DumpAsync(
        string? catalogGameKey, string title, DumpBoard board, int prgKb, int chrKb, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report("Reading the cartridge…");
        await Task.Delay(30, cancellationToken);
        progress?.Report("Reading again to check (try 2 of 3)…");
        await Task.Delay(30, cancellationToken);
        return catalogGameKey is not null
            ? new DumpOutcome(true, true, $"{title} was dumped and matches the catalog. It's in your library as owned.", catalogGameKey)
            : new DumpOutcome(true, false, $"{title} was dumped. Two reads matched; the catalog doesn't list it.", "sample:13");
    }
}

/// <summary>The boards the INL Retro-Prog scripts read, with common sizes (SPEC §8).</summary>
public static class DumperBoards
{
    public static IReadOnlyList<DumpBoard> All { get; } =
    [
        new(0, "NROM", [32, 16], [8]),
        new(1, "MMC1 (SxROM)", [128, 256, 512], [0, 8, 16, 32, 64, 128]),
        new(2, "UxROM", [128, 256], [0]),
        new(3, "CNROM", [32, 16], [32, 16]),
        new(4, "MMC3 (TxROM)", [128, 256, 512], [128, 256, 0]),
        new(7, "AxROM", [128, 256], [0]),
        new(9, "MMC2 (PxROM)", [128], [128]),
        new(10, "MMC4 (FxROM)", [128, 256], [128]),
        new(11, "Color Dreams", [64, 128], [32, 64, 128]),
        new(34, "BNROM", [128], [0]),
        new(66, "GxROM", [64, 128], [16, 32]),
        new(69, "Sunsoft FME-7", [128, 256], [128, 256]),
    ];
}
