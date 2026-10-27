using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>
/// Under <c>--sample-data</c>, "Browse…" returns a made-up path (nothing exists there, so a BIOS import shows its
/// "can't find this file" warning and nothing is written to disk).
/// </summary>
public sealed class SampleFilePathPicker : IFilePathPicker
{
    public const string SampleProgram = @"C:\INL-retro\inlretro.exe";

    public const string SampleFile = @"C:\BIOS\Sample BIOS.bin";

    /// <summary>What picks return instead of the made-up paths (tests set it).</summary>
    public string? Next { get; set; }

    /// <summary>True to act like the user pressed Cancel.</summary>
    public bool Cancels { get; set; }

    /// <summary>The last request, for tests.</summary>
    public FilePathRequest? LastRequest { get; private set; }

    public Task<string?> PickFileAsync(FilePathRequest request)
    {
        LastRequest = request;
        if (Cancels)
        {
            return Task.FromResult<string?>(null);
        }

        var path = Next ?? (request.FileTypes.Contains(".exe") ? SampleProgram : SampleFile);
        return Task.FromResult<string?>(path);
    }
}
