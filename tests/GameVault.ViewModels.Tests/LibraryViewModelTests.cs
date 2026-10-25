using GameVault.Library.Models;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.Tests;

public class LibraryViewModelTests
{
    private static async Task<LibraryViewModel> LoadedSampleLibrary()
    {
        var vm = new LibraryViewModel(new SampleLibraryService());
        await vm.LoadCommand.ExecuteAsync(null);
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

        var expected = CollectionSummary.Compute(SampleLibraryService.Games);
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
        var vm = new LibraryViewModel(new EmptyLibraryService());

        await vm.LoadCommand.ExecuteAsync(null);

        vm.IsEmptyLibrary.Should().BeTrue();
        vm.HasNoMatches.Should().BeFalse();
        vm.SummaryText.Should().Be("0 / 0 licensed releases owned");
    }

    [Fact]
    public void Toggle_view_switches_between_grid_and_list()
    {
        var vm = new LibraryViewModel(new EmptyLibraryService());
        vm.IsGridView.Should().BeTrue();

        vm.ToggleViewCommand.Execute(null);

        vm.IsGridView.Should().BeFalse();
        vm.IsListView.Should().BeTrue();
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
