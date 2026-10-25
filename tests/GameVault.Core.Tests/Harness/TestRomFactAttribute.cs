namespace GameVault.Core.Tests.Harness;

/// <summary>
/// A test that needs downloaded test ROMs (see tests/TestRoms/SOURCES.md). Skipped when they haven't been
/// fetched, unless <c>GAMEVAULT_REQUIRE_TEST_ROMS=1</c>, in which case the test runs and fails loudly.
/// </summary>
public sealed class TestRomFactAttribute : FactAttribute
{
    public TestRomFactAttribute(params string[] cachePaths)
    {
        Skip = TestRomSkip.Reason(cachePaths);
    }
}

/// <inheritdoc cref="TestRomFactAttribute"/>
public sealed class TestRomTheoryAttribute : TheoryAttribute
{
    public TestRomTheoryAttribute(params string[] cachePaths)
    {
        Skip = TestRomSkip.Reason(cachePaths);
    }
}

internal static class TestRomSkip
{
    public static string? Reason(string[] cachePaths)
    {
        if (TestRomLocator.RequireTestRoms || cachePaths.All(TestRomLocator.IsCached))
        {
            return null;
        }

        return "Test ROMs not downloaded. Run tests/TestRoms/fetch-test-roms.sh (or .ps1) to enable this test.";
    }
}
