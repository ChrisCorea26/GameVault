using FluentAssertions;
using GameVault.ViewModels;
using GameVault.ViewModels.SampleData;

namespace GameVault.ViewModels.Tests;

/// <summary>Settings → Family and the profile switcher (SPEC §7.9).</summary>
public sealed class FamilyViewModelTests
{
    [Fact]
    public void Without_a_pin_anyone_can_add_profiles_then_setting_one_locks_them()
    {
        var family = new InMemoryFamilyService(withSamples: false);
        var vm = new FamilyViewModel(family);
        vm.HasOtherProfiles.Should().BeFalse();
        vm.CanManage.Should().BeTrue();

        vm.NewName = "Sam";
        vm.NewLimitMinutes = "90";
        vm.AddProfileCommand.Execute(null);
        vm.Profiles.Should().HaveCount(2);
        vm.Profiles[1].Details.Should().Be("1 hour 30 minutes a day · Settings locked");
        vm.HasOtherProfiles.Should().BeTrue();

        vm.NewPin = "12";
        vm.SetPinCommand.Execute(null);
        vm.Message.Should().Be("A PIN is 4 to 12 digits.");
        vm.NewPin = "2468";
        vm.SetPinCommand.Execute(null);
        vm.HasPin.Should().BeTrue();
        vm.CanManage.Should().BeTrue("whoever set the PIN stays unlocked");

        vm.LockCommand.Execute(null);
        vm.CanManage.Should().BeFalse();
        vm.NewName = "Robin";
        vm.AddProfileCommand.Execute(null);
        vm.Profiles.Should().HaveCount(2);

        vm.PinEntry = "0000";
        vm.UnlockCommand.Execute(null);
        vm.Message.Should().Be("That PIN isn't right.");
        vm.PinEntry = "2468";
        vm.UnlockCommand.Execute(null);
        vm.CanManage.Should().BeTrue();
    }

    [Fact]
    public void Limits_must_be_sensible()
    {
        var vm = new FamilyViewModel(new InMemoryFamilyService(withSamples: false)) { NewName = "Sam", NewLimitMinutes = "2" };
        vm.AddProfileCommand.Execute(null);
        vm.Message.Should().StartWith("A daily limit is 5 to 1440 minutes");
        vm.Profiles.Should().ContainSingle();
    }

    [Fact]
    public void A_locked_profile_needs_the_pin_for_settings_and_switching_goes_through_the_service()
    {
        var family = new InMemoryFamilyService();
        var vm = new FamilyViewModel(family);
        vm.CanChangeSettings.Should().BeTrue("the main sample profile may change settings");

        vm.Profiles[1].SwitchCommand.Execute(null);
        family.SwitchedTo.Should().Be("sample-kid");
        vm.CurrentName.Should().Be("Sam");
        vm.SettingsLocked.Should().BeTrue();
        vm.Profiles[1].CanRemove.Should().BeFalse("it's the one playing");

        vm.PinEntry = "1234";
        vm.UnlockCommand.Execute(null);
        vm.CanChangeSettings.Should().BeTrue();
    }

    [Theory]
    [InlineData(45, "45 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(61, "1 hour 1 minute")]
    [InlineData(150, "2 hours 30 minutes")]
    public void Minutes_read_naturally(int minutes, string expected) => FamilyViewModel.Minutes(minutes).Should().Be(expected);
}
