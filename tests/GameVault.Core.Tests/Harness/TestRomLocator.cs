namespace GameVault.Core.Tests.Harness;

/// <summary>
/// Finds test ROMs. Redistributable suites live in <c>tests/TestRoms</c> (listed in SOURCES.md).
/// A developer's own dumps are only used when <c>GAMEVAULT_LOCAL_ROMS</c> is set (SPEC §11.3).
/// </summary>
public static class TestRomLocator
{
    public const string LocalRomsVariable = "GAMEVAULT_LOCAL_ROMS";

    /// <summary>When set to 1 (CI does this), missing downloaded test ROMs fail tests instead of skipping them.</summary>
    public const string RequireTestRomsVariable = "GAMEVAULT_REQUIRE_TEST_ROMS";

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

    /// <summary>The repository root (the parent of <c>tests/</c>).</summary>
    public static string RepoRoot => Path.GetFullPath(Path.Combine(TestRomsDirectory, "..", ".."));

    /// <summary>Where <c>fetch-test-roms</c> puts downloaded suites.</summary>
    public static string CacheDirectory => Path.Combine(TestRomsDirectory, ".cache");

    public static bool RequireTestRoms => Environment.GetEnvironmentVariable(RequireTestRomsVariable) == "1";

    public static bool IsCached(string cachePath) => File.Exists(Path.Combine(CacheDirectory, cachePath));

    /// <summary>Reads a downloaded test file, with a helpful message if it hasn't been fetched.</summary>
    public static byte[] ReadCached(string cachePath)
    {
        var path = Path.Combine(CacheDirectory, cachePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Test ROM '{cachePath}' is missing. Run tests/TestRoms/fetch-test-roms.sh (or .ps1) first.", path);
        }

        return File.ReadAllBytes(path);
    }

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
