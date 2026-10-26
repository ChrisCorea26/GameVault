namespace GameVault.ViewModels.Services;

public enum ChallengeFileKind
{
    /// <summary>A challenge (<c>.gvchallenge</c>).</summary>
    Challenge,

    /// <summary>An attempt's result (<c>.gvresult</c>).</summary>
    Result,
}

/// <summary>Opens and saves challenge and result files through the system's file dialogs (SPEC §7.9).</summary>
public interface IChallengeFiles
{
    /// <summary>The chosen file's text, or null if the player cancelled.</summary>
    Task<string?> OpenAsync(ChallengeFileKind kind);

    /// <summary>Saves <paramref name="content"/>; false if the player cancelled.</summary>
    Task<bool> SaveAsync(ChallengeFileKind kind, string suggestedName, string content);
}
