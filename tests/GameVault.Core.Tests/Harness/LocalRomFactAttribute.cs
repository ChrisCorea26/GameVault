namespace GameVault.Core.Tests.Harness;

/// <summary>
/// A test that runs against the developer's own ROM dumps. It is skipped unless
/// <c>GAMEVAULT_LOCAL_ROMS</c> points to an existing folder, so CI never needs commercial ROMs.
/// </summary>
public sealed class LocalRomFactAttribute : FactAttribute
{
    public LocalRomFactAttribute()
    {
        if (TestRomLocator.LocalRomsDirectory is null)
        {
            Skip = $"Set {TestRomLocator.LocalRomsVariable} to a folder of your own ROM dumps to run this test.";
        }
    }
}
