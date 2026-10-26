using GameVault.Library.Models;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.Tests;

public class LibraryViewModelTests
{
    private readonly FakeNavigationService _navigation = new();

    private LibraryViewModel Create(ILibraryService library, IGameFilePicker? picker = null) =>
        new(library, picker ?? new SampleGameFilePicker(), _navigation);

    private async Task<LibraryViewModel> LoadedSampleLibrary()
    {
        var vm = Create(new SampleLibraryService());
        await vm.LoadCommand.ExecuteAsync(null);
        vm.ShowChecklist = true; // every sample game, owned or not
        return vm;
    }

    [Fact]
    public async Task Load_shows_every_game_sorted_by_title()
    {
        var vm = await LoadedSampleLibrary();

        vm.Games.Should().HaveCount(SampleLibraryService.Games.Count);
        vm.Games.Select(g => g.Title).Should().BeInAscendingOrder(StringComparer.CurrentCultureIgnoreCase);
        vm.IsLoading.Should().BeFalse();
        vm.IsEmptyLibrary.Should().BeFalse();
    }

    [Fact]
    public async Task Load_computes_the_collection_summary()
    {
        var vm = await LoadedSampleLibrary();

        var expected = CollectionSummary.Compute(SampleLibraryService.Games.Where(g => g.IsCataloged));
        vm.SummaryText.Should().Be(expected.ToString());
        vm.CompletionFraction.Should().Be(expected.Fraction);
    }

    [Fact]
    public async Task Search_filters_by_title_publisher_or_genre_case_insensitively()
    {
        var vm = await LoadedSampleLibrary();

        vm.SearchText = "crystal";
        vm.Games.Select(g => g.Title).Should().Equal("Crystal Caverns");

        vm.SearchText = "HOSHI";
        vm.Games.Should().HaveCount(2);

        vm.SearchText = "rpg";
        vm.Games.Should().OnlyContain(g => g.Game.Genre == "RPG");

        vm.SearchText = "";
        vm.Games.Should().HaveCount(SampleLibraryService.Games.Count);
    }

    [Fact]
    public async Task No_matches_is_distinct_from_an_empty_library()
    {
        var vm = await LoadedSampleLibrary();

        vm.SearchText = "no such game";

        vm.Games.Should().BeEmpty();
        vm.HasNoMatches.Should().BeTrue();
        vm.IsEmptyLibrary.Should().BeFalse();
    }

    [Fact]
    public async Task Owned_only_filter_hides_unowned_games()
    {
        var vm = await LoadedSampleLibrary();

        vm.ShowOwnedOnly = true;

        vm.Games.Should().NotBeEmpty().And.OnlyContain(g => g.IsOwned);
        vm.Games.Should().HaveCount(SampleLibraryService.Games.Count(g => g.IsOwned));
    }

    [Fact]
    public async Task Empty_library_reports_empty_state()
    {
        var vm = Create(new SampleLibraryService(empty: true));

        await vm.LoadCommand.ExecuteAsync(null);

        vm.IsEmptyLibrary.Should().BeTrue();
        vm.HasNoMatches.Should().BeFalse();
        vm.SummaryText.Should().Be("0 / 0 licensed releases owned");
    }

    [Fact]
    public void Toggle_view_switches_between_grid_and_list()
    {
        var vm = Create(new SampleLibraryService(empty: true));
        vm.IsGridView.Should().BeTrue();

        vm.ToggleViewCommand.Execute(null);

        vm.IsGridView.Should().BeFalse();
        vm.IsListView.Should().BeTrue();
    }

    [Fact]
    public async Task Open_game_file_navigates_to_the_player_with_the_picked_path()
    {
        var vm = Create(new SampleLibraryService(empty: true));

        await vm.OpenGameFileCommand.ExecuteAsync(null);

        _navigation.History.Should().Equal((AppPage.Player, SampleGameFilePicker.SamplePath));
    }

    [Fact]
    public async Task Cancelling_the_picker_stays_on_the_library()
    {
        var vm = Create(new SampleLibraryService(empty: true), new CancellingPicker());

        await vm.OpenGameFileCommand.ExecuteAsync(null);

        _navigation.History.Should().BeEmpty();
    }

    [Fact]
    public async Task Play_opens_games_that_have_a_rom_and_ignores_the_rest()
    {
        var vm = await LoadedSampleLibrary();
        var playable = vm.Games.First(g => g.IsPlayable);
        var catalogOnly = vm.Games.First(g => !g.IsPlayable);

        vm.PlayCommand.Execute(catalogOnly);
        vm.PlayCommand.Execute(playable);

        _navigation.History.Should().Equal((AppPage.Player, new GameLaunch(playable.Game.RomPath!, playable.Game.Key)));
    }

    private sealed class CancellingPicker : IGameFilePicker
    {
        public Task<string?> PickGameFileAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickCatalogFileAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickImageAsync() => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task My_games_shows_only_owned_or_playable_games_and_the_checklist_shows_all()
    {
        var vm = Create(new SampleLibraryService());
        await vm.LoadCommand.ExecuteAsync(null);

        vm.Games.Should().OnlyContain(g => g.IsOwned || g.IsPlayable);
        vm.Games.Count.Should().BeLessThan(SampleLibraryService.Games.Count);

        vm.ShowChecklist = true;
        vm.Games.Should().HaveCount(SampleLibraryService.Games.Count);
    }

    [Fact]
    public async Task Filters_by_region_genre_publisher_year_and_status()
    {
        var vm = await LoadedSampleLibrary();

        vm.Regions.Should().StartWith(LibraryViewModel.Any).And.Contain("Japan");
        vm.SelectedRegion = "Japan";
        vm.Games.Select(g => g.Title).Should().BeEquivalentTo("Rocket Rally", "Dragon Lantern", "Orbit Squadron");

        vm.SelectedRegion = LibraryViewModel.Any;
        vm.SelectedGenre = "Shooter";
        vm.SelectedPublisher = "Kitefox";
        vm.Games.Select(g => g.Title).Should().Equal("Sky Duelist");

        vm.SelectedGenre = vm.SelectedPublisher = LibraryViewModel.Any;
        vm.SelectedYear = "1988";
        vm.SelectedStatus = "Playing";
        vm.Games.Select(g => g.Title).Should().Equal("Crystal Caverns");
    }

    [Fact]
    public async Task Filters_by_system_and_shows_each_games_system()
    {
        var vm = await LoadedSampleLibrary();

        vm.HasSeveralSystems.Should().BeTrue();
        vm.Systems.Should().Equal(LibraryViewModel.Any, "Apple II", "Genesis", "Master System", "NES", "SG-1000");
        vm.SelectedSystem = "Master System";
        vm.Games.Should().ContainSingle().Which.Title.Should().Be("Coral Coast");
        vm.Games[0].SystemText.Should().Be("Master System");

        vm.SelectedSystem = "SG-1000";
        vm.Games.Select(g => g.Title).Should().Equal("Orbit Squadron");
    }

    [Fact]
    public async Task Adding_a_folder_scans_and_fills_an_empty_library()
    {
        var vm = Create(new SampleLibraryService(empty: true));
        await vm.LoadCommand.ExecuteAsync(null);
        vm.IsEmptyLibrary.Should().BeTrue();

        await vm.AddFolderCommand.ExecuteAsync(null);

        vm.IsScanning.Should().BeFalse();
        vm.ScanStatus.Should().Be($"Found {SampleLibraryService.Games.Count} games");
        vm.IsEmptyLibrary.Should().BeFalse();
        vm.Games.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Opening_details_navigates_with_the_game_key()
    {
        var vm = await LoadedSampleLibrary();

        vm.OpenDetailsCommand.Execute(vm.Games[0]);

        _navigation.History.Should().Equal((AppPage.GameDetails, vm.Games[0].Game.Key));
    }

    [Fact]
    public async Task Changing_search_raises_HasNoMatches_change_notification()
    {
        var vm = await LoadedSampleLibrary();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SearchText = "zzz";

        changed.Should().Contain(nameof(LibraryViewModel.HasNoMatches));
    }
}
