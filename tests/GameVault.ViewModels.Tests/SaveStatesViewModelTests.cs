using GameVault.ViewModels.SampleData;

namespace GameVault.ViewModels.Tests;

public class SaveStatesViewModelTests
{
    private readonly SampleSaveStateService _service = new();
    private readonly SaveStatesViewModel _vm;

    public SaveStatesViewModelTests()
    {
        _vm = new SaveStatesViewModel(_service);
    }

    [Fact]
    public async Task Sample_data_shows_ten_slots_with_three_filled()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);

        _vm.Slots.Should().HaveCount(10);
        _vm.Slots.Count(s => !s.IsEmpty).Should().Be(3);
        _vm.Slots[2].Subtitle.Should().Be("Empty");
        _vm.Slots[2].LoadCommand.CanExecute(null).Should().BeFalse();
        _vm.Slots[0].IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task Saving_selects_the_slot_and_refreshes_it()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);

        await _vm.Slots[6].SaveCommand.ExecuteAsync(null);

        _vm.SelectedSlot.Should().Be(6);
        _service.SelectedSlot.Should().Be(6);
        _vm.Slots[6].IsEmpty.Should().BeFalse();
        _vm.Slots[6].IsSelected.Should().BeTrue();
        _vm.Slots[0].IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task Loading_reports_through_the_service()
    {
        string? notice = null;
        _service.Notice += (_, n) => notice = n;
        await _vm.RefreshCommand.ExecuteAsync(null);

        await _vm.Slots[1].LoadCommand.ExecuteAsync(null);

        notice.Should().Be("Loaded slot 2");
        _vm.SelectedSlot.Should().Be(1);
    }
}
