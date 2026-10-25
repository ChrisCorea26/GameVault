using GameVault.Library.Models;
using GameVault.Library.Profiles;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.Tests;

public class LibraryScreensTests
{
    private readonly FakeNavigationService _navigation = new();
    private readonly SampleLibraryService _library = new();
    private readonly SampleTimelineService _timeline = new();

    private GameDetailsViewModel Details() =>
        new(_library, new TimelineViewModel(_timeline), _navigation, new SampleGameFilePicker());

    [Fact]
    public async Task Details_show_the_game_and_its_timeline()
    {
        var vm = Details();

        await vm.LoadCommand.ExecuteAsync("sample:2");

        vm.Title.Should().Be("Crystal Caverns");
        vm.Subtitle.Should().Be("1988 · Lumen Soft · Platformer");
        vm.OwnsCartridge.Should().BeTrue();
        vm.OwnsBox.Should().BeFalse();
        vm.StatusIndex.Should().Be((int)PlayStatus.Playing);
        vm.CanPlay.Should().BeTrue();
        vm.PlaytimeText.Should().StartWith("6.2 h played");
        vm.Timeline.Items.Should().HaveCount(4);
        vm.Timeline.CanSave.Should().BeFalse();
    }

    [Fact]
    public async Task Edits_are_saved_immediately()
    {
        var vm = Details();
        await vm.LoadCommand.ExecuteAsync("sample:2");

        vm.OwnsBox = true;
        vm.IsFavorite = true;
        vm.Rating = 3;
        vm.StatusIndex = (int)PlayStatus.Beaten;
        vm.Notes = "Use the hidden spring";
        await vm.SaveTextCommand.ExecuteAsync(null);

        var saved = (await _library.GetGameAsync("sample:2"))!;
        saved.Owned.Should().HaveFlag(Ownership.Box).And.HaveFlag(Ownership.Cartridge);
        saved.IsFavorite.Should().BeTrue();
        saved.Rating.Should().Be(3);
        saved.Status.Should().Be(PlayStatus.Beaten);
        saved.Notes.Should().Be("Use the hidden spring");
    }

    [Fact]
    public async Task Game_properties_save_as_overrides()
    {
        var vm = Details();
        await vm.LoadCommand.ExecuteAsync("sample:2");

        vm.InputIndex = 2; // Zapper
        vm.RunAheadIndex = 2; // Off
        await Task.Yield();

        var overrides = await _library.GetProfileOverridesAsync("sample:2");
        overrides!.Input.Should().Be(InputType.Zapper);
        overrides.RunAheadSafe.Should().BeFalse();

        vm.InputIndex = 0;
        vm.RunAheadIndex = 0;
        await Task.Yield();
        (await _library.GetProfileOverridesAsync("sample:2")).Should().BeNull();
    }

    [Fact]
    public async Task Play_and_timeline_loads_launch_the_player()
    {
        var vm = Details();
        await vm.LoadCommand.ExecuteAsync("sample:2");

        vm.PlayCommand.Execute(null);
        await vm.Timeline.Items[0].LoadCommand.ExecuteAsync(null);

        _navigation.History.Should().Equal(
            (AppPage.Player, new GameLaunch(@"C:\Games\NES\Crystal Caverns.nes", "sample:2")),
            (AppPage.Player, new GameLaunch(@"C:\Games\NES\Crystal Caverns.nes", "sample:2", "a")));
    }

    [Fact]
    public async Task Catalog_only_games_cannot_be_played()
    {
        var vm = Details();

        await vm.LoadCommand.ExecuteAsync("sample:6");

        vm.CanPlay.Should().BeFalse();
        vm.PlayHint.Should().Contain("Add a folder");
    }

    [Fact]
    public async Task Timeline_rename_pin_delete_and_undo()
    {
        var timeline = new TimelineViewModel(_timeline);
        await timeline.ShowAsync("sample:2");
        var item = timeline.Items[1];

        item.EditLabel = "Boss door";
        await item.RenameCommand.ExecuteAsync(null);
        await timeline.Items[1].TogglePinCommand.ExecuteAsync(null);
        timeline.Items[1].Title.Should().Be("Boss door");
        timeline.Items[1].IsPinned.Should().BeTrue();

        await timeline.Items[1].DeleteCommand.ExecuteAsync(null);
        timeline.Items.Should().HaveCount(3);
        timeline.UndoText.Should().Be("Deleted \"Boss door\"");

        await timeline.UndoCommand.ExecuteAsync(null);
        timeline.Items.Should().HaveCount(4);
        timeline.HasUndo.Should().BeFalse();
    }

    [Fact]
    public async Task Catalog_import_wizard_previews_imports_and_finishes()
    {
        _navigation.NavigateTo(AppPage.Library);
        var vm = new CatalogImportViewModel(_library, new SampleGameFilePicker(), _navigation);
        vm.IsChoosing.Should().BeTrue();

        await vm.ChooseFileCommand.ExecuteAsync(null);
        vm.IsPreviewing.Should().BeTrue();
        vm.PreviewText.Should().Be("\"Sample Catalog\" lists 677 games (652 licensed releases).");

        await vm.ImportCommand.ExecuteAsync(null);
        vm.IsDone.Should().BeTrue();
        vm.ResultText.Should().StartWith("Added 677 games");

        vm.FinishCommand.Execute(null);
        _navigation.CurrentPage.Should().Be(AppPage.Library);
    }

    [Fact]
    public async Task Library_settings_list_add_and_remove_folders()
    {
        var vm = new LibrarySettingsViewModel(new SampleLibraryService(empty: true), new SampleGameFilePicker());
        await vm.LoadCommand.ExecuteAsync(null);
        vm.HasNoFolders.Should().BeTrue();

        await vm.AddFolderCommand.ExecuteAsync(null);
        vm.Folders.Select(f => f.Path).Should().Equal(SampleGameFilePicker.SampleFolder);
        vm.Status.Should().StartWith("Found");

        await vm.Folders[0].RemoveCommand.ExecuteAsync(null);
        vm.HasNoFolders.Should().BeTrue();
    }
}
