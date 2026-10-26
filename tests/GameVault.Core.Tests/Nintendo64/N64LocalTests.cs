using System.Text;
using FluentAssertions;
using GameVault.Core.Nintendo64;
using GameVault.Core.Tests.Harness;
using Xunit.Abstractions;

namespace GameVault.Core.Tests.Nintendo64;

/// <summary>
/// Tests against the owner's own files (skipped unless <c>GAMEVAULT_LOCAL_ROMS</c> is set; GameVault never ships them):
/// <list type="bullet">
/// <item><c>n64/*.z64</c> (or .v64, .n64): the owner's cartridges, which should boot to a picture.</item>
/// <item><c>n64/n64-systemtest.z64</c>: lemmy-64's MIT-licensed n64-systemtest, built from its repository. It prints
/// its results through the IS-Viewer port.</item>
/// </list>
/// </summary>
public sealed class N64LocalTests(ITestOutputHelper output)
{
    private static string? Folder =>
        TestRomLocator.LocalRomsDirectory is { } root && Path.Combine(root, "n64") is var path && Directory.Exists(path) ? path : null;

    [LocalRomFact]
    public void Cartridges_boot_to_a_picture()
    {
        if (Folder is not { } folder)
        {
            return;
        }

        var blank = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".z64" or ".v64" or ".n64").Order())
        {
            var cartridge = new N64Cartridge(File.ReadAllBytes(file));
            var console = new N64Console(cartridge);
            console.Power();
            var sawPicture = false;
            for (var frame = 0; frame < 60 * 20 && !sawPicture; frame++)
            {
                console.RunFrame();
                sawPicture = frame > 60 && console.FrameBuffer.ToArray().Distinct().Count() > 16;
            }

            output.WriteLine($"{Path.GetFileName(file)}: {cartridge.Name} ({cartridge.GameCode}), {cartridge.Cic}, {cartridge.SaveType}, picture: {sawPicture}");
            if (!sawPicture)
            {
                blank.Add(Path.GetFileName(file));
            }
        }

        blank.Should().BeEmpty("every cartridge should show a picture within 20 seconds");
    }

    [LocalRomFact]
    public void N64_systemtest_reports_no_failures()
    {
        if (Folder is not { } folder || Path.Combine(folder, "n64-systemtest.z64") is var path && !File.Exists(path))
        {
            return;
        }

        var text = new StringBuilder();
        var console = new N64Console(new N64Cartridge(File.ReadAllBytes(path))) { DebugOutput = c => text.Append(c) };
        console.Power();
        for (var frame = 0; frame < 60 * 60 && !text.ToString().Contains("Done!", StringComparison.Ordinal); frame++)
        {
            console.RunFrame();
        }

        output.WriteLine(text.ToString());
        text.ToString().Should().Contain("Failed: 0");
    }
}
