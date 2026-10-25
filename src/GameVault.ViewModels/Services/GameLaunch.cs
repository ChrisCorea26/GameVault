namespace GameVault.ViewModels.Services;

/// <summary>
/// What the Player should start: a game file, optionally with a save state to load right away (from the
/// timeline on Game Details). A plain string path is also accepted as the navigation parameter.
/// </summary>
public sealed record GameLaunch(string Path, string? GameKey = null, string? StateId = null);
