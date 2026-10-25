namespace GameVault.Core.Tests.Harness;

public class TestRomLocatorTests
{
    [Fact]
    public void Finds_the_TestRoms_folder_with_its_sources_registry()
    {
        File.Exists(TestRomLocator.GetPath("SOURCES.md")).Should().BeTrue();
    }

    [LocalRomFact]
    public void Local_roms_folder_is_available_when_configured()
    {
        Directory.Exists(TestRomLocator.LocalRomsDirectory).Should().BeTrue();
    }
}
