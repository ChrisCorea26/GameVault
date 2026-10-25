using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>Under <c>--sample-data</c>, "picking" a file returns a made-up path; the sample emulator ignores it.</summary>
public sealed class SampleGameFilePicker : IGameFilePicker
{
    public const string SamplePath = @"C:\Games\NES\Sample Game.nes";

    public Task<string?> PickGameFileAsync() => Task.FromResult<string?>(SamplePath);
}
