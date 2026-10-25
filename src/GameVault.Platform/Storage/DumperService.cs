using GameVault.Dumper;
using GameVault.Library.Models;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Storage;

/// <summary>
/// The dumper flow (SPEC §8): dump with the INL Retro-Prog, compare with the catalog, save into the first
/// library folder (or Documents\GameVault Dumps), scan it in and mark it "Owned: Cartridge + Digital dump".
/// </summary>
public sealed class DumperService(IDumper? dumper, ILibraryService library) : IDumperService
{
    public string? DumperName => dumper?.Name;

    public IReadOnlyList<DumpBoard> Boards { get; } = dumper is null
        ? DumperBoards.All
        : DumperBoards.All.Where(b => dumper!.SupportedMappers.Contains(b.Mapper)).ToList();

    public async Task<DumpOutcome> DumpAsync(
        string? catalogGameKey, string title, DumpBoard board, int prgKb, int chrKb, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (dumper is null)
        {
            return new DumpOutcome(false, false, "No dumper is set up.", null);
        }

        var folder = (await library.GetFoldersAsync()).FirstOrDefault()
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GameVault Dumps");
        var output = Path.Combine(folder, BatterySaveStore.SafeName(title) + ".nes");
        var expected = catalogGameKey?.StartsWith("sha1:", StringComparison.Ordinal) == true ? catalogGameKey[5..] : null;

        var result = await dumper.DumpAsync(new DumpRequest(board.Mapper, prgKb, chrKb, output, expected), progress, cancellationToken);
        if (!result.Succeeded)
        {
            return new DumpOutcome(false, false, result.Message, null);
        }

        progress?.Report("Adding it to your library…");
        if ((await library.GetFoldersAsync()).Count == 0)
        {
            await library.AddFolderAsync(folder, cancellationToken: cancellationToken);
        }
        else
        {
            await library.ScanAsync(cancellationToken: cancellationToken);
        }

        var key = result.Hashes!.Value.Key;
        if (await library.GetGameAsync(key, cancellationToken) is { } game)
        {
            await library.SaveGameAsync(game with { Owned = game.Owned | Ownership.Cartridge | Ownership.DigitalDump });
        }

        var verified = result.Status == DumpStatus.Verified;
        var message = verified
            ? $"{title} was dumped and matches the catalog. It's in your library as owned."
            : $"{title} was dumped. {result.Message}";
        return new DumpOutcome(true, verified, message, key);
    }
}
