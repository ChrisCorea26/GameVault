namespace GameVault.Core.Tests.Harness;

/// <summary>
/// Finds test ROMs. Redistributable suites live in <c>tests/TestRoms</c> (listed in SOURCES.md).
/// A developer's own dumps are only used when <c>GAMEVAULT_LOCAL_ROMS</c> is set (SPEC §11.3).
/// </summary>
public static class TestRomLocator
{
    public const string LocalRomsVariable = "GAMEVAULT_LOCAL_ROMS";

    /// <summary>The repository's <c>tests/TestRoms</c> folder.</summary>
    public static string TestRomsDirectory { get; } = FindTestRomsDirectory();

    /// <summary>The folder named by <c>GAMEVAULT_LOCAL_ROMS</c>, or null if it is unset or missing.</summary>
    public static string? LocalRomsDirectory
    {
        get
        {
            var path = Environment.GetEnvironmentVariable(LocalRomsVariable);
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
        }
    }

    public static string GetPath(string relativePath) => Path.Combine(TestRomsDirectory, relativePath);

    private static string FindTestRomsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "TestRoms");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not locate tests/TestRoms above " + AppContext.BaseDirectory);
    }
}
