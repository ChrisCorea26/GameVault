using GameVault.Library.Profiles;
using GameVault.Library.Tests.Fixtures;
using GameVault.Library.Timeline;

namespace GameVault.Library.Tests;

public sealed class ProfilesAndTimelineTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly LibraryFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Profiles_load_single_and_array_files_and_skip_bad_ones()
    {
        var folder = Path.Combine(_fx.Root, "gamedb");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "one.json"), $$"""{ "sha1": "{{Sha}}", "input": "Zapper", "runAheadSafe": false, "header": { "mapper": 4 } }""");
        File.WriteAllText(Path.Combine(folder, "many.json"), """[ { "sha1": "ffffffffffffffffffffffffffffffffffffffff", "removeSpriteLimit": true } ]""");
        File.WriteAllText(Path.Combine(folder, "broken.json"), "{ not json");

        var profiles = new GameProfiles(folder);

        profiles.Count.Should().Be(2);
        profiles.Errors.Should().ContainSingle().Which.Should().StartWith("broken.json");
        var profile = profiles.Resolve(Sha, null);
        profile.Input.Should().Be(InputType.Zapper);
        profile.RunAheadSafe.Should().BeFalse();
        profile.Header!.Mapper.Should().Be(4);
        profiles.Resolve("unknown", null).Input.Should().BeNull();
    }

    [Fact]
    public void User_overrides_win_over_the_bundled_profile()
    {
        var bundled = new GameProfile { Sha1 = Sha, Input = InputType.Zapper, RunAheadSafe = true };
        var overrides = GameProfile.FromJson(new GameProfile { Input = InputType.Controller }.ToJson());

        var merged = bundled.Merge(overrides);

        merged.Input.Should().Be(InputType.Controller);
        merged.RunAheadSafe.Should().BeTrue();
    }

    [Fact]
    public void Timeline_lists_renames_pins_and_undoes_deletes()
    {
        var store = new TimelineStore(Path.Combine(_fx.Root, "States"));
        var t0 = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var first = store.Add("sha1:" + Sha, [1], [0x89], TimeSpan.FromMinutes(5), TimelineKind.Manual, t0);
        var second = store.Add("sha1:" + Sha, [2], [0x89], TimeSpan.FromMinutes(9), TimelineKind.Quick, t0.AddMinutes(4));

        store.Rename("sha1:" + Sha, first.Id, "  Before the boss ");
        store.SetPinned("sha1:" + Sha, first.Id, true);
        store.Delete("sha1:" + Sha, second.Id);

        var list = store.List("sha1:" + Sha);
        list.Should().ContainSingle();
        list[0].Label.Should().Be("Before the boss");
        list[0].Pinned.Should().BeTrue();
        store.LoadState("sha1:" + Sha, first.Id).Should().Equal(1);
        File.Exists(store.ThumbnailPath("sha1:" + Sha, first.Id)).Should().BeTrue();

        store.Undelete("sha1:" + Sha, second.Id);
        store.Latest("sha1:" + Sha)!.Id.Should().Be(second.Id);

        store.Delete("sha1:" + Sha, second.Id);
        store.PurgeDeleted();
        store.LoadState("sha1:" + Sha, second.Id).Should().BeNull();
        store.List("sha1:" + Sha).Should().ContainSingle();
    }

    [Fact]
    public void Only_the_newest_auto_exit_state_is_kept()
    {
        var store = new TimelineStore(Path.Combine(_fx.Root, "States"));
        var t0 = DateTimeOffset.UnixEpoch;
        var old = store.Add("g", [1], [], TimeSpan.Zero, TimelineKind.AutoExit, t0);
        store.Add("g", [2], [], TimeSpan.Zero, TimelineKind.Manual, t0.AddSeconds(1));
        var newest = store.Add("g", [3], [], TimeSpan.Zero, TimelineKind.AutoExit, t0.AddSeconds(2));

        store.List("g").Should().HaveCount(2);
        store.Latest("g", TimelineKind.AutoExit)!.Id.Should().Be(newest.Id);
        store.LoadState("g", old.Id).Should().BeNull();
    }
}
