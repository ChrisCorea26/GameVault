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
}
