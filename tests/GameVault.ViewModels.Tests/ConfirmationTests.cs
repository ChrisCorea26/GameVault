using FluentAssertions;
using GameVault.ViewModels;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.Tests;

/// <summary>SPEC §9.3: destructive actions ask first (docs/bugs.md GV-005).</summary>
public sealed class ConfirmationTests
{
    [Fact]
    public async Task Removing_a_profile_asks_first_and_cancelling_keeps_it()
    {
        var family = new InMemoryFamilyService();
        var no = new FixedConfirmation(false);
        var vm = new FamilyViewModel(family, no) { PinEntry = "1234" }; // the sample PIN
        vm.UnlockCommand.Execute(null);
        var count = vm.Profiles.Count;
        var removable = vm.Profiles.First(p => p.CanRemove);

        await removable.RemoveCommand.ExecuteAsync(null);

        no.Asked.Should().ContainSingle().Which.Should().StartWith("Remove");
        vm.Profiles.Should().HaveCount(count);

        var yes = new FixedConfirmation(true);
        vm = new FamilyViewModel(family, yes) { PinEntry = "1234" };
        vm.UnlockCommand.Execute(null);
        await vm.Profiles.First(p => p.CanRemove).RemoveCommand.ExecuteAsync(null);
        vm.Profiles.Should().HaveCount(count - 1);
    }

    [Fact]
    public async Task Removing_a_library_folder_asks_first()
    {
        var library = new SampleLibraryService();
        var no = new FixedConfirmation(false);
        var vm = new LibrarySettingsViewModel(library, new SampleGameFilePicker(), no);
        await vm.LoadCommand.ExecuteAsync(null);
        var folders = vm.Folders.Count;
        folders.Should().BeGreaterThan(0);

        await vm.Folders[0].RemoveCommand.ExecuteAsync(null);

        no.Asked.Should().ContainSingle();
        vm.Folders.Should().HaveCount(folders);
        (await library.GetFoldersAsync()).Should().HaveCount(folders);
    }

    [Fact]
    public async Task Deleting_a_password_asks_first()
    {
        var service = new SamplePasswordService();
        var no = new FixedConfirmation(false);
        var vm = new PasswordsViewModel(service, new InMemoryClipboard(), no);
        await vm.ShowAsync("sample:2");
        var count = vm.Items.Count;

        await vm.Items[0].DeleteCommand.ExecuteAsync(null);

        no.Asked.Should().ContainSingle();
        await vm.ShowAsync("sample:2");
        vm.Items.Should().HaveCount(count);
    }
}
