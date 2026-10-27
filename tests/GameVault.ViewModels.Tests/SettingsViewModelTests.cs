using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public async Task Load_copies_saved_settings_without_saving_them_back()
    {
        var store = new InMemorySettingsStore(new AppSettings { CropOverscan = false, RunAheadFrames = 3, PauseWhenInactive = false });
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store), new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()));

        await vm.LoadCommand.ExecuteAsync(null);

        vm.CropOverscan.Should().BeFalse();
        vm.RunAheadFrames.Should().Be(3);
        vm.PauseWhenInactive.Should().BeFalse();
        store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Changes_are_saved_immediately()
    {
        var store = new InMemorySettingsStore();
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store), new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()));
        await vm.LoadCommand.ExecuteAsync(null);

        vm.RunAheadFrames = 2;
        vm.CropOverscan = false;
        vm.AllowOppositeDirections = true;
        vm.Volume = 55.4;

        store.Current.Should().Be(new AppSettings { RunAheadFrames = 2, CropOverscan = false, AllowOppositeDirections = true, Volume = 55 });
        store.SaveCount.Should().Be(4);
    }

    [Fact]
    public async Task The_snes_chip_rom_folder_is_saved_from_the_box_or_the_folder_picker()
    {
        var store = new InMemorySettingsStore();
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store), new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()), picker: new SampleGameFilePicker());
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SnesChipRomFolder = "\"D:\\Chips\" ";
        vm.SaveSnesChipRomFolderCommand.Execute(null);
        store.Current.SnesChipRomFolder.Should().Be(@"D:\Chips");

        await vm.ChooseSnesChipRomFolderCommand.ExecuteAsync(null);
        store.Current.SnesChipRomFolder.Should().Be(await new SampleGameFilePicker().PickFolderAsync());
    }

    [Fact]
    public async Task The_playstation_bios_path_and_fast_boot_are_saved()
    {
        var store = new InMemorySettingsStore();
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store), new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()));
        await vm.LoadCommand.ExecuteAsync(null);

        vm.PlayStationBiosPath = "\"C:\\BIOS\\scph1001.bin\" ";
        vm.SavePlayStationBiosPathCommand.Execute(null);
        vm.PlayStationFastBoot = true;

        store.Current.PlayStationBiosPath.Should().Be(@"C:\BIOS\scph1001.bin", "quotes from a pasted path are removed");
        store.Current.PlayStationFastBoot.Should().BeTrue();
    }

    [Fact]
    public async Task The_playstation_2_bios_is_saved_and_checked()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gv-ps2-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new InMemorySettingsStore();
            var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store), new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()));
            await vm.LoadCommand.ExecuteAsync(null);
            vm.HasPlayStation2BiosProblem.Should().BeFalse("nothing is chosen yet");
            vm.PlayStation2BiosStatus.Should().Contain("BIOS");

            var wrong = Path.Combine(folder, "scph1001.bin");
            await File.WriteAllBytesAsync(wrong, new byte[512 * 1024]);
            vm.PlayStation2BiosPath = $"\"{wrong}\"";
            await vm.SavePlayStation2BiosPathCommand.ExecuteAsync(null);

            store.Current.PlayStation2BiosPath.Should().Be(wrong, "quotes from a pasted path are removed");
            vm.HasPlayStation2BiosProblem.Should().BeTrue();
            vm.PlayStation2BiosProblem.Should().Contain("4 MB");
            vm.PlayStation2BiosStatus.Should().BeEmpty();

            var bios = Path.Combine(folder, "scph39001.bin");
            var image = new byte[4 * 1024 * 1024];
            System.Text.Encoding.ASCII.GetBytes("RESET").CopyTo(image, 0x2740);
            BitConverter.GetBytes(0x2740).CopyTo(image, 0x2740 + 12);
            System.Text.Encoding.ASCII.GetBytes("ROMDIR").CopyTo(image, 0x2750);
            BitConverter.GetBytes(0x40).CopyTo(image, 0x2750 + 12);
            System.Text.Encoding.ASCII.GetBytes("ROMVER").CopyTo(image, 0x2760);
            BitConverter.GetBytes(0x10).CopyTo(image, 0x2760 + 12);
            System.Text.Encoding.ASCII.GetBytes("0200JC20040614").CopyTo(image, 0x2740 + 0x40); // after RESET and ROMDIR
            await File.WriteAllBytesAsync(bios, image);
            vm.PlayStation2BiosPath = bios;
            await vm.SavePlayStation2BiosPathCommand.ExecuteAsync(null);
            vm.PlayStation2FastBoot = true;

            vm.HasPlayStation2BiosProblem.Should().BeFalse();
            vm.PlayStation2BiosStatus.Should().Be("PlayStation 2 BIOS version 2.00, Japan, 2004-06-14.");
            store.Current.PlayStation2BiosPath.Should().Be(bios);
            store.Current.PlayStation2FastBoot.Should().BeTrue();
            store.Current.PlayStationFastBoot.Should().BeFalse("the PlayStation's own setting is separate");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_n64_expansion_pak_is_on_by_default_and_saved()
    {
        var store = new InMemorySettingsStore();
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store), new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()));
        await vm.LoadCommand.ExecuteAsync(null);
        vm.N64ExpansionPak.Should().BeTrue();

        vm.N64ExpansionPak = false;

        store.Current.N64ExpansionPak.Should().BeFalse();
    }

    [Fact]
    public async Task Display_preset_and_pixel_shape_are_saved()
    {
        var store = new InMemorySettingsStore(new AppSettings { DisplayPreset = DisplayPreset.Smooth });
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store), new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()));
        await vm.LoadCommand.ExecuteAsync(null);
        vm.DisplayPresetIndex.Should().Be(1);

        vm.DisplayPresetIndex = (int)DisplayPreset.ArcadeMonitor;
        vm.TvPixelShape = true;
        vm.DisplayPresetIndex = -1; // RadioButtons clearing its selection

        store.Current.DisplayPreset.Should().Be(DisplayPreset.ArcadeMonitor);
        store.Current.TvPixelShape.Should().BeTrue();
    }

    [Fact]
    public async Task Out_of_range_run_ahead_from_disk_is_clamped()
    {
        var store = new InMemorySettingsStore(new AppSettings { RunAheadFrames = 99 });
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store), new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()));

        await vm.LoadCommand.ExecuteAsync(null);

        vm.RunAheadFrames.Should().Be(AppSettings.MaxRunAheadFrames);
    }

    [Fact]
    public async Task Transient_negative_run_ahead_is_not_saved()
    {
        var store = new InMemorySettingsStore();
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store), new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()));
        await vm.LoadCommand.ExecuteAsync(null);

        vm.RunAheadFrames = -1;

        store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Reset_restores_and_saves_defaults()
    {
        var store = new InMemorySettingsStore(new AppSettings { CropOverscan = false, RunAheadFrames = 0 });
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store), new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()));
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        vm.CropOverscan.Should().BeTrue();
        vm.RunAheadFrames.Should().Be(1);
        store.Current.Should().Be(new AppSettings());
        store.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Choose_buttons_fill_the_path_boxes_from_a_file_picker_and_save()
    {
        var store = new InMemorySettingsStore();
        var vm = new SettingsViewModel(
            store,
            new InputSettingsViewModel(new SampleInputDevicesService(), store),
            new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()),
            picker: new SampleGameFilePicker());
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.ChooseFdsBiosCommand.ExecuteAsync(null);
        await vm.ChooseAppleDiskRomCommand.ExecuteAsync(null);
        await vm.ChoosePlayStationBiosCommand.ExecuteAsync(null);

        vm.FdsBiosPath.Should().Be(@"C:\Games\BIOS\sample.rom");
        store.Current.FdsBiosPath.Should().Be(@"C:\Games\BIOS\sample.rom");
        store.Current.AppleIIDiskRomPath.Should().Be(@"C:\Games\BIOS\sample.rom");
        store.Current.PlayStationBiosPath.Should().Be(@"C:\Games\BIOS\sample.bin");
    }

    [Fact]
    public async Task A_chosen_bios_is_copied_into_gamevaults_bios_folder()
    {
        var root = Directory.CreateTempSubdirectory("gamevault-bios-");
        try
        {
            var picked = Path.Combine(root.FullName, "picked", "disksys.rom");
            Directory.CreateDirectory(Path.GetDirectoryName(picked)!);
            File.WriteAllBytes(picked, new byte[8 * 1024]);
            var wrongSize = Path.Combine(root.FullName, "picked", "scph1001.bin");
            File.WriteAllBytes(wrongSize, new byte[100]);

            var store = new InMemorySettingsStore();
            var picker = new OnePathPicker();
            var vm = new SettingsViewModel(
                store,
                new InputSettingsViewModel(new SampleInputDevicesService(), store),
                new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()),
                picker: picker,
                biosImporter: new GameVault.Library.Bios.BiosImporter(Path.Combine(root.FullName, "bios")));
            await vm.LoadCommand.ExecuteAsync(null);

            picker.Path = picked;
            await vm.ChooseFdsBiosCommand.ExecuteAsync(null);
            var copy = Path.Combine(root.FullName, "bios", "fds", "disksys.rom");
            File.Exists(copy).Should().BeTrue("the BIOS is copied");
            store.Current.FdsBiosPath.Should().Be(copy, "Settings keeps the copy");

            // A file that fails the check isn't copied; its own path is kept, so the launch message can explain it.
            picker.Path = wrongSize;
            await vm.ChoosePlayStationBiosCommand.ExecuteAsync(null);
            Directory.Exists(Path.Combine(root.FullName, "bios", "ps1")).Should().BeFalse();
            store.Current.PlayStationBiosPath.Should().Be(wrongSize);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private sealed class OnePathPicker : IGameFilePicker
    {
        public string? Path { get; set; }

        public Task<string?> PickGameFileAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickCatalogFileAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickImageAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickSystemFileAsync(IReadOnlyList<string> extensions) => Task.FromResult(Path);
    }

    [Fact]
    public async Task Hide_screen_edges_offers_an_amount()
    {
        var store = new InMemorySettingsStore();
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store), new LibrarySettingsViewModel(new SampleLibraryService(), new SampleGameFilePicker()));
        await vm.LoadCommand.ExecuteAsync(null);
        vm.OverscanIndex.Should().Be(2, "8 lines by default");

        vm.OverscanIndex = 4;
        store.Current.CropOverscan.Should().BeTrue();
        store.Current.OverscanCropLines.Should().Be(16);
        store.Current.CropLines().Should().Be(16);

        vm.OverscanIndex = 0;
        store.Current.CropOverscan.Should().BeFalse();
        store.Current.CropLines().Should().Be(0);

        await vm.LoadCommand.ExecuteAsync(null);
        vm.OverscanIndex.Should().Be(0);
        vm.OverscanIndex = 1;
        store.Current.CropLines().Should().Be(4);
    }
}
