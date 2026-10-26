using FluentAssertions;
using GameVault.Core.PlayStation;
using GameVault.Core.PlayStation2;
using GameVault.Core.Tests.Harness;
using Xunit.Abstractions;

namespace GameVault.Core.Tests.PlayStation2.Local;

/// <summary>
/// Tests against the owner's own files (skipped unless <c>GAMEVAULT_LOCAL_ROMS</c> is set; GameVault never ships them):
/// <list type="bullet">
/// <item><c>ps2/bios.bin</c>: the owner's 4 MB BIOS, which should boot to a picture (the browser) without a disc.</item>
/// <item><c>ps2/*.iso</c>: the owner's discs (2,048-byte sectors), which should reach a picture with fast boot.</item>
/// <item><c>ps2/*.elf</c>: homebrew, run through the BIOS.</item>
/// </list>
/// </summary>
public sealed class Ps2LocalTests(ITestOutputHelper output)
{
    private static string? Folder =>
        TestRomLocator.LocalRomsDirectory is { } root && Path.Combine(root, "ps2") is var path && Directory.Exists(path) ? path : null;

    private static byte[]? Bios =>
        Folder is { } folder && Path.Combine(folder, "bios.bin") is var path && File.Exists(path) ? File.ReadAllBytes(path) : null;

    [LocalRomFact]
    public void Bios_boots_to_a_picture()
    {
        if (Bios is not { } bios)
        {
            return;
        }

        var console = new Ps2Console(Ps2Boot.FromDisc(new NoDisc()), bios);
        RunToPicture(console, "BIOS").Should().BeTrue("the BIOS should show its browser within 30 seconds");
    }

    [LocalRomFact]
    public void Discs_and_elfs_reach_a_picture_through_the_bios()
    {
        if (Folder is not { } folder || Bios is not { } bios)
        {
            return;
        }

        var blank = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".iso" or ".elf").Order())
        {
            using var disc = Path.GetExtension(file).Equals(".iso", StringComparison.OrdinalIgnoreCase) ? new FileDisc(file) : null;
            var boot = disc is not null ? Ps2Boot.FromDisc(disc) : Ps2Boot.FromElf(File.ReadAllBytes(file), "host:" + Path.GetFileName(file));
            var console = new Ps2Console(boot, bios, Ps2MemoryCard.CreateFormatted(), new Ps2ConsoleOptions { FastBoot = true });
            if (!RunToPicture(console, Path.GetFileName(file)))
            {
                blank.Add(Path.GetFileName(file));
            }
        }

        blank.Should().BeEmpty("every disc and ELF should show a picture within 30 seconds");
    }

    private bool RunToPicture(Ps2Console console, string name)
    {
        console.Power();
        var sawPicture = false;
        for (var frame = 0; frame < 60 * 30 && !sawPicture; frame++)
        {
            console.RunFrame();
            sawPicture = frame > 60 && console.FrameBuffer.ToArray().Distinct().Count() > 16;
        }

        output.WriteLine($"{name}: picture: {sawPicture} after {console.FrameCount} frames");
        return sawPicture;
    }

    private sealed class NoDisc : IPs2Disc
    {
        public Ps2DiscType Type => Ps2DiscType.Dvd;

        public long SectorCount => 0;

        public long Layer1Start => 0;

        public ICdImage? Cd => null;

        public void ReadSector(long lba, Span<byte> destination) => destination[..IPs2Disc.SectorSize].Clear();
    }

    private sealed class FileDisc(string path) : IPs2Disc, IDisposable
    {
        private readonly FileStream _file = File.OpenRead(path);

        public Ps2DiscType Type => _file.Length > 4_700_000_000L ? Ps2DiscType.DvdDualLayer : Ps2DiscType.Dvd;

        public long SectorCount => _file.Length / IPs2Disc.SectorSize;

        public long Layer1Start => 0;

        public ICdImage? Cd => null;

        public void ReadSector(long lba, Span<byte> destination)
        {
            destination[..IPs2Disc.SectorSize].Clear();
            if (lba >= 0 && lba < SectorCount)
            {
                RandomAccess.Read(_file.SafeFileHandle, destination[..IPs2Disc.SectorSize], lba * IPs2Disc.SectorSize);
            }
        }

        public void Dispose() => _file.Dispose();
    }
}
