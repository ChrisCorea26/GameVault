namespace GameVault.ViewModels.Services;

/// <summary>
/// Asks before a destructive action (SPEC §9.3: "Every destructive action … supports undo or asks for confirmation").
/// The app shows a dialog; sample data and tests answer without one.
/// </summary>
public interface IConfirmationService
{
    /// <summary>True when the player chose <paramref name="confirmLabel"/>; false when they cancelled.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel);
}

/// <summary>Answers every question with a fixed choice (sample data, and ViewModels built without a dialog).</summary>
public sealed class FixedConfirmation(bool answer = true) : IConfirmationService
{
    public static FixedConfirmation Yes { get; } = new(true);

    /// <summary>The questions asked so far, newest last.</summary>
    public List<string> Asked { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        Asked.Add(title);
        return Task.FromResult(answer);
    }
}
