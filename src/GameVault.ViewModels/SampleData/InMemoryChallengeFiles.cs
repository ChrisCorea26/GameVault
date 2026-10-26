using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>Challenge files kept in memory (tests and <c>--sample-data</c>): the next file to open, and what was saved.</summary>
public sealed class InMemoryChallengeFiles : IChallengeFiles
{
    public Dictionary<ChallengeFileKind, string> ToOpen { get; } = [];

    public List<(ChallengeFileKind Kind, string Name, string Content)> Saved { get; } = [];

    public Task<string?> OpenAsync(ChallengeFileKind kind) => Task.FromResult(ToOpen.GetValueOrDefault(kind));

    public Task<bool> SaveAsync(ChallengeFileKind kind, string suggestedName, string content)
    {
        Saved.Add((kind, suggestedName, content));
        return Task.FromResult(true);
    }
}
