using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>Under <c>--sample-data</c>, "picking" a file returns a made-up path; the sample emulator ignores it.</summary>
public sealed class SampleGameFilePicker : IGameFilePicker
{
    public const string SamplePath = @"C:\Games\NES\Sample Game.nes";

    public const string SampleFolder = @"C:\Games\NES";

    public const string SampleCatalog = @"C:\Games\Catalogs\Sample Catalog.dat";

    public Task<string?> PickGameFileAsync() => Task.FromResult<string?>(SamplePath);

    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(SampleFolder);

    public Task<string?> PickCatalogFileAsync() => Task.FromResult<string?>(SampleCatalog);

    public Task<string?> PickImageAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickDiskImageAsync() => Task.FromResult<string?>(@"C:\Games\Apple II\Sample Disk 2.dsk");
}
