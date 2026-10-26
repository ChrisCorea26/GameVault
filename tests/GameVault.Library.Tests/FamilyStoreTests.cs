using FluentAssertions;
using GameVault.Library.Family;

namespace GameVault.Library.Tests;

/// <summary>Family profiles (SPEC §7.9): the file, the PIN, folders and daily play.</summary>
public sealed class FamilyStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-family-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void The_main_profile_is_always_there_and_uses_the_existing_folder()
    {
        var store = new FamilyStore(_root.FullName);

        store.Profiles.Should().ContainSingle().Which.Id.Should().Be(FamilyProfile.MainId);
        store.DataRootFor(FamilyProfile.MainId).Should().Be(_root.FullName);

        var kid = store.Add("Sam", 60, canChangeSettings: false);
        store.DataRootFor(kid.Id).Should().Be(Path.Combine(_root.FullName, "Profiles", kid.Id));
        new FamilyStore(_root.FullName).Find(kid.Id).Should().Be(kid);

        store.Invoking(s => s.Remove(FamilyProfile.MainId)).Should().Throw<InvalidOperationException>();
        store.Remove(kid.Id);
        store.Profiles.Should().ContainSingle();
    }

    [Fact]
    public void The_pin_is_stored_as_a_salted_hash()
    {
        var store = new FamilyStore(_root.FullName);
        store.VerifyPin("anything").Should().BeTrue("no PIN set yet");

        store.SetPin("482913");
        File.ReadAllText(store.FilePath).Should().NotContain("482913");
        store.HasPin.Should().BeTrue();
        store.VerifyPin("482913").Should().BeTrue();
        store.VerifyPin("482914").Should().BeFalse();

        var first = File.ReadAllText(store.FilePath);
        store.SetPin("482913");
        File.ReadAllText(store.FilePath).Should().NotBe(first, "every PIN gets a fresh salt");

        store.Invoking(s => s.SetPin("12a4")).Should().Throw<ArgumentException>();
        store.Invoking(s => s.SetPin("123")).Should().Throw<ArgumentException>();
        store.SetPin(null);
        store.HasPin.Should().BeFalse();
    }

    [Fact]
    public void Play_adds_up_through_the_day_and_starts_over_the_next()
    {
        var store = new FamilyStore(_root.FullName);
        var monday = new DateOnly(2026, 10, 26);

        store.AddPlay("main", monday, TimeSpan.FromMinutes(20));
        store.AddPlay("main", monday, TimeSpan.FromMinutes(15)).Seconds.Should().Be(35 * 60);
        store.LiftLimit("main", monday);
        store.PlayToday("main", monday).LimitLifted.Should().BeTrue();

        var tuesday = monday.AddDays(1);
        store.PlayToday("main", tuesday).Should().Be(new DailyPlay { Date = tuesday });
        store.AddPlay("main", tuesday, TimeSpan.FromMinutes(1)).LimitLifted.Should().BeFalse();
    }

    [Fact]
    public void A_damaged_file_falls_back_to_the_main_profile()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "profiles.json"), "{ not json");
        new FamilyStore(_root.FullName).Profiles.Should().ContainSingle().Which.Id.Should().Be(FamilyProfile.MainId);
    }
}
