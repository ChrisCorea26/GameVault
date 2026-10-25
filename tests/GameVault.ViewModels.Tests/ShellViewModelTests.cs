using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.Tests;

public class ShellViewModelTests
{
    [Fact]
    public void Start_opens_the_library()
    {
        var nav = new FakeNavigationService();
        var vm = new ShellViewModel(nav, new AppEnvironment(IsSampleData: false));

        vm.StartCommand.Execute(null);

        nav.History.Select(h => h.Page).Should().Equal(AppPage.Library);
        vm.CurrentPage.Should().Be(AppPage.Library);
    }

    [Fact]
    public void Navigating_to_the_current_page_does_nothing()
    {
        var nav = new FakeNavigationService();
        var vm = new ShellViewModel(nav, new AppEnvironment(false));
        vm.StartCommand.Execute(null);

        vm.NavigateCommand.Execute(AppPage.Library);
        vm.NavigateCommand.Execute(AppPage.Settings);

        nav.History.Select(h => h.Page).Should().Equal(AppPage.Library, AppPage.Settings);
        vm.CurrentPage.Should().Be(AppPage.Settings);
    }

    [Fact]
    public void Page_argument_opens_the_player_over_the_library()
    {
        var nav = new FakeNavigationService();
        var env = AppEnvironment.FromCommandLine(["--sample-data", "--page=player"]);
        var vm = new ShellViewModel(nav, env);

        vm.StartCommand.Execute(null);

        nav.History.Should().Equal((AppPage.Library, null), (AppPage.Player, SampleData.SampleGameFilePicker.SamplePath));
        nav.CanGoBack.Should().BeTrue();
    }

    [Theory]
    [InlineData("--page=settings", AppPage.Settings)]
    [InlineData("--page=PLAYER", AppPage.Player)]
    [InlineData("--page=nonsense", AppPage.Library)]
    public void Page_argument_is_parsed_case_insensitively(string arg, AppPage expected)
    {
        AppEnvironment.FromCommandLine([arg]).StartPage.Should().Be(expected);
    }

    [Theory]
    [InlineData(new[] { "--sample-data" }, true)]
    [InlineData(new[] { "GameVault.exe", "--SAMPLE-DATA" }, true)]
    [InlineData(new[] { "GameVault.exe" }, false)]
    [InlineData(new string[0], false)]
    public void Sample_data_flag_is_read_from_the_command_line(string[] args, bool expected)
    {
        var env = AppEnvironment.FromCommandLine(args);

        env.IsSampleData.Should().Be(expected);
        new ShellViewModel(new FakeNavigationService(), env).IsSampleData.Should().Be(expected);
    }

    [Fact]
    public void Empty_sample_data_and_new_pages_are_recognized()
    {
        var env = AppEnvironment.FromCommandLine(["--sample-data=empty", "--page=gamedetails"]);

        env.IsSampleData.Should().BeTrue();
        env.IsEmptySampleLibrary.Should().BeTrue();
        env.StartPage.Should().Be(AppPage.GameDetails);

        var nav = new FakeNavigationService();
        new ShellViewModel(nav, env).StartCommand.Execute(null);
        nav.History.Should().Equal((AppPage.Library, null), (AppPage.GameDetails, "sample:2"));
    }
}
