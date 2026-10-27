using System.Security.Cryptography;
using System.Text;
using GameVault.Core.PlayStation2;
using GameVault.Core.Systems;
using GameVault.Library.Discs;
using GameVault.Library.Scanning;
using GameVault.Library.Tests.Discs;
using GameVault.Platform.Achievements;
using GameVault.Platform.Emulation;
using GameVault.Platform.Storage;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>The PlayStation 2's platform side (Phase 17): RetroAchievements hashing, memory cards and the BIOS setting.</summary>
public sealed class PlayStation2PlatformTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameVaultTests", Guid.NewGuid().ToString("N"));

    public PlayStation2PlatformTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string name, byte[] data)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static string Md5(string name, byte[] file)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(Encoding.ASCII.GetBytes(name));
        md5.AppendData(file);
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    [Fact]
    public void Rc_hash_ps2_is_the_boot_executables_name_then_its_contents()
    {
        var iso = RomFiles.Read(Write("game.iso", Ps2TestImages.Iso()))!;
        Write("game.bin", Ps2TestImages.Raw(Ps2TestImages.Iso()));
        File.WriteAllText(Path.Combine(_root, "game.cue"), Ps2TestImages.Cue("game.bin"));
        var cue = RomFiles.Read(Path.Combine(_root, "game.cue"))!;
        var expected = Md5(Ps2TestImages.Serial, Ps2TestImages.Elf());

        AchievementsHashing.ForSystem(iso.Data, GameSystem.PlayStation2).Should().Be(expected);
        AchievementsHashing.ForSystem(cue.Data, GameSystem.PlayStation2).Should().Be(expected, "a CD hashes the same way through its data track");
    }

    [Fact]
    public void Rc_hash_ps2_follows_a_boot_path_into_a_subdirectory_and_an_elf_hashes_whole()
    {
        var body = Encoding.ASCII.GetBytes(new string('e', 3000));
        var disc = new MemoryPs2Disc(Ps2TestImages.Iso(Ps2TestImages.SystemCnf(@"cdrom0:\DATA\MAIN.ELF;1"), new() { [@"DATA\MAIN.ELF"] = body }));

        AchievementsHashing.PlayStation2Disc(disc).Should().Be(Md5(@"DATA\MAIN.ELF", body));
        AchievementsHashing.PlayStation2Disc(new MemoryPs2Disc(Ps2TestImages.Iso(ps2: false))).Should().BeEmpty("there's no SYSTEM.CNF");

        var elf = Ps2TestImages.Elf();
        AchievementsHashing.ForSystem(elf, GameSystem.PlayStation2).Should().Be(Convert.ToHexStringLower(MD5.HashData(elf)));
        AchievementsHashing.ConsoleId(GameSystem.PlayStation2).Should().Be(21);
        AchievementsHashing.ConsoleId(GameSystem.PlayStation).Should().Be(12);
        AchievementsHashing.PlayStation2MemoryLimit.Should().Be(32 * 1024 * 1024 + 16 * 1024 - 1, "32 MB of RDRAM, then the 16 KB scratchpad");
    }

    [Fact]
    public void Each_game_gets_an_8_mb_ps2_card_saved_next_to_the_other_saves()
    {
        var paths = new AppPaths(_root);
        var store = new Ps2MemoryCardStore(paths);
        store.TryLoad(0x1234).Should().BeNull("no card yet: the core creates a formatted one");

        var card = new byte[Ps2MemoryCardStore.Size];
        card[0] = 0x53;
        card[^1] = 0x77;
        store.Save("Skyline: Courier", 0x1234, card);

        Ps2MemoryCardStore.Size.Should().Be(8_650_752);
        var file = Directory.GetFiles(paths.SavesDirectory, "*.ps2").Should().ContainSingle().Which;
        Path.GetFileName(file).Should().Be("Skyline_ Courier - 0000000000001234.ps2");
        store.TryLoad(0x1234).Should().Equal(card);
        FluentActions.Invoking(() => store.Save("Game", 1, new byte[100])).Should().Throw<ArgumentException>();

        File.WriteAllBytes(store.PathFor("Other", 0x99), new byte[128 * 1024]);
        FluentActions.Invoking(() => store.TryLoad(0x99)).Should().Throw<InvalidDataException>().WithMessage("*8 MB*");
    }

    [Fact]
    public void Slot_2_has_one_card_shared_by_every_game()
    {
        var store = new Ps2MemoryCardStore(new AppPaths(_root));
        store.TryLoadShared().Should().BeNull();

        var card = new byte[Ps2MemoryCardStore.Size];
        card[5] = 0x42;
        store.SaveShared(card);

        Path.GetFileName(store.SharedPath).Should().Be("Memory card (slot 2).ps2");
        store.TryLoadShared().Should().Equal(card);
        store.TryLoad(0x1234).Should().BeNull("a game's own card is separate");
        File.WriteAllBytes(store.SharedPath, new byte[10]);
        FluentActions.Invoking(store.TryLoadShared).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task The_bios_from_settings_must_be_4_mb()
    {
        var none = await PlayStation2Setup.LoadAsync(new AppSettings { PlayStation2FastBoot = true });
        none.Bios.Should().BeNull();
        none.FastBoot.Should().BeTrue();

        var bios = Write("scph39001.bin", new byte[4 * 1024 * 1024]);
        (await PlayStation2Setup.LoadAsync(new AppSettings { PlayStation2BiosPath = bios })).Bios.Should().HaveCount(4 * 1024 * 1024);

        var small = Write("small.bin", new byte[512 * 1024]);
        await FluentActions.Awaiting(() => PlayStation2Setup.LoadAsync(new AppSettings { PlayStation2BiosPath = small }))
            .Should().ThrowAsync<GameLoadException>().WithMessage("*4 MB*");
        await FluentActions.Awaiting(() => PlayStation2Setup.LoadAsync(new AppSettings { PlayStation2BiosPath = Path.Combine(_root, "missing.bin") }))
            .Should().ThrowAsync<GameLoadException>().WithMessage("*couldn't open*");
    }
}
