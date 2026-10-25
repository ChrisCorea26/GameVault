using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public async Task Load_copies_saved_settings_without_saving_them_back()
    {
        var store = new InMemorySettingsStore(new AppSettings { CropOverscan = false, RunAheadFrames = 3, PauseWhenInactive = false });
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store));

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
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store));
        await vm.LoadCommand.ExecuteAsync(null);

        vm.RunAheadFrames = 2;
        vm.CropOverscan = false;
        vm.AllowOppositeDirections = true;
        vm.Volume = 55.4;

        store.Current.Should().Be(new AppSettings { RunAheadFrames = 2, CropOverscan = false, AllowOppositeDirections = true, Volume = 55 });
        store.SaveCount.Should().Be(4);
    }

    [Fact]
    public async Task Out_of_range_run_ahead_from_disk_is_clamped()
    {
        var store = new InMemorySettingsStore(new AppSettings { RunAheadFrames = 99 });
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store));

        await vm.LoadCommand.ExecuteAsync(null);

        vm.RunAheadFrames.Should().Be(AppSettings.MaxRunAheadFrames);
    }

    [Fact]
    public async Task Transient_negative_run_ahead_is_not_saved()
    {
        var store = new InMemorySettingsStore();
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store));
        await vm.LoadCommand.ExecuteAsync(null);

        vm.RunAheadFrames = -1;

        store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Reset_restores_and_saves_defaults()
    {
        var store = new InMemorySettingsStore(new AppSettings { CropOverscan = false, RunAheadFrames = 0 });
        var vm = new SettingsViewModel(store, new InputSettingsViewModel(new SampleInputDevicesService(), store));
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        vm.CropOverscan.Should().BeTrue();
        vm.RunAheadFrames.Should().Be(1);
        store.Current.Should().Be(new AppSettings());
        store.SaveCount.Should().Be(1);
    }
}
