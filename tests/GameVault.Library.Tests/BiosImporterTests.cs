using FluentAssertions;
using GameVault.Library.Bios;

namespace GameVault.Library.Tests;

/// <summary>Settings → Choose… for a BIOS: the file is checked, then copied into GameVault's bios folder.</summary>
public sealed class BiosImporterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gv-bios-import-" + Guid.NewGuid().ToString("N"));

    public BiosImporterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string name, byte[] data)
    {
        var path = Path.Combine(_folder, "picked", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    private BiosImporter Importer() => new(Path.Combine(_folder, "GameVault", "bios"));

    [Fact]
    public void A_ps2_bios_is_copied_into_its_system_folder_keeping_its_name()
    {
        var source = Write("scph39001.bin", Ps2BiosTests.StandInBios());

        var import = Importer().Import(BiosKind.PlayStation2, $"\"{source}\"");

        import.Succeeded.Should().BeTrue();
        import.Message.Should().Be("PlayStation 2 BIOS version 1.60, Europe, 2002-02-07.");
        import.Path.Should().Be(Path.Combine(_folder, "GameVault", "bios", "ps2", "scph39001.bin"));
        File.ReadAllBytes(import.Path!).Should().Equal(File.ReadAllBytes(source));
    }

    [Fact]
    public void Choosing_a_file_with_the_same_name_again_replaces_the_copy()
    {
        var importer = Importer();
        importer.Import(BiosKind.PlayStation2, Write("bios.bin", new byte[Ps2Bios.Size]));

        var import = importer.Import(BiosKind.PlayStation2, Write("bios.bin", Ps2BiosTests.StandInBios()));

        import.Succeeded.Should().BeTrue();
        File.ReadAllBytes(import.Path!).Should().Equal(Ps2BiosTests.StandInBios());
    }

    [Fact]
    public void Choosing_the_copy_itself_keeps_it()
    {
        var importer = Importer();
        var first = importer.Import(BiosKind.PlayStation2, Write("scph39001.bin", Ps2BiosTests.StandInBios()));

        var again = importer.Import(BiosKind.PlayStation2, first.Path!);

        again.Should().Be(first);
        File.Exists(first.Path).Should().BeTrue();
    }

    [Theory]
    [InlineData(BiosKind.PlayStation2, 512 * 1024, "4 MB")]
    [InlineData(BiosKind.PlayStation, 4 * 1024 * 1024, "512 KB")]
    [InlineData(BiosKind.FamicomDiskSystem, 16 * 1024, "8 KB")]
    public void A_file_of_the_wrong_size_is_rejected_and_not_copied(BiosKind kind, int size, string expectedSize)
    {
        var importer = Importer();

        var import = importer.Import(kind, Write("wrong.bin", new byte[size]));

        import.Succeeded.Should().BeFalse();
        import.Path.Should().BeNull();
        import.Message.Should().Contain(expectedSize);
        Directory.Exists(importer.FolderFor(kind)).Should().BeFalse("nothing is copied");
    }

    [Theory]
    [InlineData(BiosKind.PlayStation, 512 * 1024, "ps1", "scph1001.bin")]
    [InlineData(BiosKind.FamicomDiskSystem, 8 * 1024, "fds", "disksys.rom")]
    public void Other_bioses_are_checked_by_size_and_copied(BiosKind kind, int size, string folder, string name)
    {
        var import = Importer().Import(kind, Write(name, new byte[size]));

        import.Succeeded.Should().BeTrue();
        import.Path.Should().Be(Path.Combine(_folder, "GameVault", "bios", folder, name));
        File.Exists(import.Path).Should().BeTrue();
    }

    [Fact]
    public void A_missing_file_is_rejected()
    {
        foreach (var kind in Enum.GetValues<BiosKind>())
        {
            Importer().Import(kind, Path.Combine(_folder, "missing.bin")).Succeeded.Should().BeFalse();
        }
    }
}
