using GameVault.Library.Bios;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.Tests;

/// <summary>Settings' "Browse…" and "Choose BIOS file…" buttons next to the boxes that take a file path.</summary>
public sealed class SettingsBrowseTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gv-settings-browse-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySettingsStore _store = new();
    private readonly SampleFilePathPicker _picker = new();

    public SettingsBrowseTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>Stands in for <c>%LOCALAPPDATA%\GameVault\bios</c>.</summary>
    private string BiosFolder => Path.Combine(_folder, "LocalAppData", "GameVault", "bios");

    private async Task<SettingsViewModel> LoadedAsync()
    {
        var vm = new SettingsViewModel(
            _store,
            new InputSettingsViewModel(new SampleInputDevicesService(), _store),
            new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()),
            pathPicker: _picker,
            biosImporter: new BiosImporter(BiosFolder));
        await vm.LoadCommand.ExecuteAsync(null);
        return vm;
    }

    private string Picked(string name, byte[] data)
    {
        var path = Path.Combine(_folder, "Downloads", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        _picker.Next = path;
        return path;
    }

    /// <summary>A 4 MB stand-in PS2 BIOS whose ROMVER says version 2.00, Japan, 2004-06-14.</summary>
    private static byte[] StandInPs2Bios()
    {
        var image = new byte[Ps2Bios.Size];
        System.Text.Encoding.ASCII.GetBytes("RESET").CopyTo(image, 0x2740);
        BitConverter.GetBytes(0x2740).CopyTo(image, 0x2740 + 12);
        System.Text.Encoding.ASCII.GetBytes("ROMDIR").CopyTo(image, 0x2750);
        BitConverter.GetBytes(0x40).CopyTo(image, 0x2750 + 12);
        System.Text.Encoding.ASCII.GetBytes("ROMVER").CopyTo(image, 0x2760);
        BitConverter.GetBytes(0x10).CopyTo(image, 0x2760 + 12);
        System.Text.Encoding.ASCII.GetBytes("0200JC20040614").CopyTo(image, 0x2740 + 0x40);
        return image;
    }

    [Fact]
    public async Task Choosing_a_ps2_bios_copies_it_and_uses_the_copy()
    {
        var vm = await LoadedAsync();
        var source = Picked("scph39001.bin", StandInPs2Bios());

        await vm.ChoosePlayStation2BiosFileCommand.ExecuteAsync(null);

        var copy = Path.Combine(BiosFolder, "ps2", "scph39001.bin");
        File.ReadAllBytes(copy).Should().Equal(File.ReadAllBytes(source));
        vm.PlayStation2BiosPath.Should().Be(copy);
        _store.Current.PlayStation2BiosPath.Should().Be(copy);
        vm.HasPlayStation2BiosProblem.Should().BeFalse();
        vm.PlayStation2BiosStatus.Should().Be("PlayStation 2 BIOS version 2.00, Japan, 2004-06-14.");
    }

    [Fact]
    public async Task A_file_that_isnt_a_ps2_bios_is_not_copied_and_the_warning_says_why()
    {
        var vm = await LoadedAsync();
        Picked("scph1001.bin", new byte[512 * 1024]);

        await vm.ChoosePlayStation2BiosFileCommand.ExecuteAsync(null);

        Directory.Exists(Path.Combine(BiosFolder, "ps2")).Should().BeFalse("nothing is copied");
        vm.PlayStation2BiosPath.Should().BeEmpty();
        _store.Current.PlayStation2BiosPath.Should().BeEmpty();
        _store.SaveCount.Should().Be(0);
        vm.HasPlayStation2BiosProblem.Should().BeTrue();
        vm.PlayStation2BiosProblem.Should().Be("This file isn't a PlayStation 2 BIOS: a BIOS is 4 MB.");
        vm.PlayStation2BiosStatus.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelling_the_picker_changes_nothing()
    {
        var vm = await LoadedAsync();
        _picker.Cancels = true;

        await vm.ChoosePlayStation2BiosFileCommand.ExecuteAsync(null);
        await vm.BrowseDumperPathCommand.ExecuteAsync(null);

        vm.HasPlayStation2BiosProblem.Should().BeFalse();
        _store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Choosing_a_playstation_bios_copies_it_into_ps1()
    {
        var vm = await LoadedAsync();
        Picked("scph1001.bin", new byte[512 * 1024]);

        await vm.ChoosePlayStationBiosFileCommand.ExecuteAsync(null);

        var copy = Path.Combine(BiosFolder, "ps1", "scph1001.bin");
        File.Exists(copy).Should().BeTrue();
        _store.Current.PlayStationBiosPath.Should().Be(copy);
        vm.HasPlayStationBiosProblem.Should().BeFalse();
    }

    [Fact]
    public async Task A_wrong_playstation_bios_shows_a_warning_until_a_path_is_typed()
    {
        var vm = await LoadedAsync();
        Picked("big.bin", new byte[4 * 1024 * 1024]);

        await vm.ChoosePlayStationBiosFileCommand.ExecuteAsync(null);

        vm.HasPlayStationBiosProblem.Should().BeTrue();
        vm.PlayStationBiosProblem.Should().Contain("512 KB");
        Directory.Exists(Path.Combine(BiosFolder, "ps1")).Should().BeFalse();

        vm.PlayStationBiosPath = @"C:\BIOS\scph1001.bin";
        vm.SavePlayStationBiosPathCommand.Execute(null);
        vm.HasPlayStationBiosProblem.Should().BeFalse();
    }

    [Fact]
    public async Task Choosing_a_disk_system_bios_copies_it_into_fds()
    {
        var vm = await LoadedAsync();
        Picked("disksys.rom", new byte[8 * 1024]);

        await vm.ChooseFdsBiosFileCommand.ExecuteAsync(null);

        var copy = Path.Combine(BiosFolder, "fds", "disksys.rom");
        File.Exists(copy).Should().BeTrue();
        _store.Current.FdsBiosPath.Should().Be(copy);
        vm.HasFdsBiosProblem.Should().BeFalse();
    }

    [Fact]
    public async Task A_wrong_disk_system_bios_is_rejected()
    {
        var vm = await LoadedAsync();
        Picked("disksys.rom", new byte[100]);

        await vm.ChooseFdsBiosFileCommand.ExecuteAsync(null);

        vm.HasFdsBiosProblem.Should().BeTrue();
        vm.FdsBiosProblem.Should().Contain("8 KB");
        _store.Current.FdsBiosPath.Should().BeEmpty();
    }

    [Fact]
    public async Task Browse_fills_in_and_saves_the_other_paths_without_copying()
    {
        var vm = await LoadedAsync();

        _picker.Next = @"C:\INL-retro\inlretro.exe";
        await vm.BrowseDumperPathCommand.ExecuteAsync(null);
        _picker.LastRequest!.FileTypes.Should().Equal(".exe");

        _picker.Next = @"C:\Apple\apple2e-enhanced.rom";
        await vm.BrowseAppleIIRomPathCommand.ExecuteAsync(null);
        _picker.LastRequest!.FileTypes.Should().Equal("*");

        _picker.Next = @"C:\Apple\disk2.rom";
        await vm.BrowseAppleIIDiskRomPathCommand.ExecuteAsync(null);

        vm.DumperPath.Should().Be(@"C:\INL-retro\inlretro.exe");
        _store.Current.DumperPath.Should().Be(@"C:\INL-retro\inlretro.exe");
        _store.Current.AppleIIRomPath.Should().Be(@"C:\Apple\apple2e-enhanced.rom");
        _store.Current.AppleIIDiskRomPath.Should().Be(@"C:\Apple\disk2.rom");
        Directory.Exists(BiosFolder).Should().BeFalse("only BIOS files are copied");
    }

    [Fact]
    public async Task Without_a_picker_the_buttons_do_nothing()
    {
        var vm = new SettingsViewModel(_store, new InputSettingsViewModel(new SampleInputDevicesService(), _store), new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()));
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.ChoosePlayStation2BiosFileCommand.ExecuteAsync(null);
        await vm.BrowseDumperPathCommand.ExecuteAsync(null);

        _store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Under_sample_data_choosing_a_bios_shows_a_warning_and_writes_nothing()
    {
        var vm = await LoadedAsync(); // the sample picker returns a made-up path that doesn't exist

        await vm.ChoosePlayStation2BiosFileCommand.ExecuteAsync(null);

        vm.HasPlayStation2BiosProblem.Should().BeTrue();
        Directory.Exists(BiosFolder).Should().BeFalse();
    }
}
