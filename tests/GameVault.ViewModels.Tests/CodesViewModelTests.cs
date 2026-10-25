using GameVault.ViewModels.SampleData;

namespace GameVault.ViewModels.Tests;

/// <summary>The Codes list (SPEC §7.8, Phase 8).</summary>
public class CodesViewModelTests
{
    private const string Game = "sha1:aa";
    private readonly SampleCheatService _service = new();
    private readonly SampleCodeFilePicker _picker = new();
    private readonly CodesViewModel _vm;

    public CodesViewModelTests() => _vm = new CodesViewModel(_service, _picker);

    [Fact]
    public async Task Shows_the_games_codes_and_only_suggestions_not_yet_added()
    {
        await _vm.ShowAsync(Game);

        _vm.Items.Select(i => i.Code).Should().Equal("SXIOPO", "0300:09");
        _vm.Items[0].Detail.Should().Be("$91D9 = $AD");
        _vm.Suggestions.Should().ContainSingle().Which.Code.Should().Be("AEKPIL");
    }

    [Theory]
    [InlineData("", false, "")]
    [InlineData("sxiopo", true, "Decodes to $91D9 = $AD")]
    [InlineData("075A:09", true, "Decodes to RAM $075A = $09")]
    [InlineData("SXIOP", false, "Game Genie codes have 6 or 8 letters.")]
    public void Codes_are_checked_as_they_are_typed(string text, bool valid, string message)
    {
        _vm.NewCode = text;

        _vm.IsNewCodeValid.Should().Be(valid);
        _vm.Validation.Should().Be(message);
        _vm.AddCommand.CanExecute(null).Should().Be(valid);
    }

    [Fact]
    public async Task Adding_toggling_and_deleting_codes_reach_the_service()
    {
        await _vm.ShowAsync(Game);
        _vm.NewCode = "aekp-il";
        _vm.NewDescription = "Jump higher";

        await _vm.AddCommand.ExecuteAsync(null);

        _vm.Items.Select(i => i.Code).Should().Equal("SXIOPO", "0300:09", "AEKPIL");
        _vm.NewCode.Should().BeEmpty();
        _vm.Suggestions.Should().BeEmpty("the suggestion is in the list now");

        _vm.Items[1].IsEnabled = true;
        await Task.Yield();
        _service.Applied[^1].Codes.Should().BeEquivalentTo(new[] { "SXIOPO", "0300:09", "AEKPIL" }.Select(GameVault.Core.Cheats.CheatCode.Parse));

        await _vm.AllOffCommand.ExecuteAsync(null);
        _vm.Items.Should().OnlyContain(i => !i.IsEnabled);
        _vm.AnyEnabled.Should().BeFalse();

        await _vm.Items[0].DeleteCommand.ExecuteAsync(null);
        _vm.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Imports_report_how_many_codes_were_added_or_why_none_were()
    {
        await _vm.ShowAsync(Game);
        _service.Files["good.txt"] = "AEKPIL Jump higher\nGXOOZTEY Eight letters";
        _service.Files["bad.txt"] = "AEKPIL ok\nNOPE";

        _picker.ImportPath = "bad.txt";
        await _vm.ImportCommand.ExecuteAsync(null);
        _vm.Message.Should().StartWith("Nothing was imported. Line 2:");
        _vm.Items.Should().HaveCount(2);

        _picker.ImportPath = "good.txt";
        await _vm.ImportCommand.ExecuteAsync(null);
        _vm.Message.Should().Be("Added 2 codes.");
        _vm.Items.Should().HaveCount(4);

        _picker.ImportPath = null; // cancelled
        await _vm.ImportCommand.ExecuteAsync(null);
        _vm.Message.Should().Be("Added 2 codes.");
    }

    [Fact]
    public async Task Exports_save_the_list()
    {
        await _vm.ShowAsync(Game);
        _picker.ExportPath = "Codes/mine.txt";

        await _vm.ExportCommand.ExecuteAsync(null);

        _service.Files["Codes/mine.txt"].Should().Contain("SXIOPO  Infinite lives");
        _vm.Message.Should().Be("Saved the codes to mine.txt.");
    }
}
