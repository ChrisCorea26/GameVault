using System.Text;
using FluentAssertions;
using GameVault.Library.Bios;

namespace GameVault.Library.Tests;

/// <summary>The PlayStation 2 BIOS check in Settings (Phase 17), on a stand-in 4 MB image with a ROMDIR.</summary>
public sealed class Ps2BiosTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gv-ps2bios-" + Guid.NewGuid().ToString("N"));

    public Ps2BiosTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>
    /// A stand-in BIOS laid out as ps2tek describes SCPH-39001's: RESET (0x2740 bytes, holding ROMDIR's entries at its
    /// end), ROMDIR, EXTINFO, then ROMVER with <paramref name="romver"/>.
    /// </summary>
    internal static byte[] StandInBios(string romver = "0160EC20020207")
    {
        var bios = new byte[Ps2Bios.Size];
        void Entry(int at, string name, int size)
        {
            Encoding.ASCII.GetBytes(name).CopyTo(bios, at);
            BitConverter.GetBytes(size).CopyTo(bios, at + 12);
        }

        Entry(0x2740, "RESET", 0x2740);
        Entry(0x2750, "ROMDIR", 0x50);
        Entry(0x2760, "EXTINFO", 0x13); // padded to 0x20
        Entry(0x2770, "ROMVER", 0x10);
        Encoding.ASCII.GetBytes(romver).CopyTo(bios, 0x2740 + 0x50 + 0x20);
        return bios;
    }

    [Fact]
    public void The_version_comes_from_romver()
    {
        var version = Ps2Bios.ReadVersion(StandInBios());

        version.Should().Be(new Ps2BiosVersion("1.60", "Europe", "2002-02-07"));
        Ps2Bios.ReadVersion(StandInBios("0220AC20060905"))!.ToString().Should().Be("version 2.20, USA, 2006-09-05");
        Ps2Bios.ReadVersion(new byte[Ps2Bios.Size]).Should().BeNull("there's no ROMDIR");
    }

    [Fact]
    public void Check_reports_the_version_or_whats_wrong()
    {
        var good = Path.Combine(_folder, "scph39001.bin");
        File.WriteAllBytes(good, StandInBios());
        var noVersion = Path.Combine(_folder, "blank.bin");
        File.WriteAllBytes(noVersion, new byte[Ps2Bios.Size]);
        var ps1 = Path.Combine(_folder, "scph1001.bin");
        File.WriteAllBytes(ps1, new byte[512 * 1024]);

        Ps2Bios.Check($"\"{good}\" ").Should().BeEquivalentTo(new { IsValid = true, Message = "PlayStation 2 BIOS version 1.60, Europe, 2002-02-07." });
        Ps2Bios.Check(noVersion).IsValid.Should().BeTrue();
        Ps2Bios.Check(ps1).Should().BeEquivalentTo(new { IsValid = false, Message = "This file isn't a PlayStation 2 BIOS: a BIOS is 4 MB." });
        Ps2Bios.Check(Path.Combine(_folder, "missing.bin")).IsValid.Should().BeFalse();
        Ps2Bios.Check("").IsValid.Should().BeFalse();
    }
}
