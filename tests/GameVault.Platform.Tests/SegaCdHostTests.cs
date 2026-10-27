using GameVault.Core.Systems;
using GameVault.Library.Discs;
using GameVault.Library.Hashing;
using GameVault.Library.Scanning;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>Sega CD discs (sega-cd-32x.md) through the library's file reader and the host.</summary>
public sealed class SegaCdHostTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-segacd-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>A one-track mode 1 disc whose system area starts "SEGADISCSYSTEM", as a .cue and .bin.</summary>
    private string Disc(string header = "SEGADISCSYSTEM  ", char region = 'U')
    {
        var sectors = new byte[16 * 2352];
        for (var i = 0; i < 16; i++)
        {
            var at = i * 2352;
            sectors.AsSpan(at + 1, 10).Fill(0xFF);
            sectors[at + 15] = 0x01;
        }

        System.Text.Encoding.ASCII.GetBytes(header).CopyTo(sectors, 16);
        sectors[16 + 0x1F0] = (byte)region;
        File.WriteAllBytes(Path.Combine(_root.FullName, "Game.bin"), sectors);
        var cue = Path.Combine(_root.FullName, "Game.cue");
        File.WriteAllText(cue, "FILE \"Game.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");
        return cue;
    }

    /// <summary>A stand-in BIOS: a stack, a reset vector and a loop.</summary>
    private static byte[] Bios()
    {
        var bios = new byte[0x20000];
        byte[] start = [0xFF, 0xFF, 0xFE, 0x00, 0x00, 0x00, 0x02, 0x00];
        start.CopyTo(bios, 0);
        bios[0x200] = 0x60;
        bios[0x201] = 0xFE;
        return bios;
    }

    [Fact]
    public void A_disc_with_the_sega_cd_system_area_is_a_sega_cd_game()
    {
        var cue = Disc(region: 'J');

        var file = RomFiles.Read(cue);

        file!.System.Should().Be(GameSystem.SegaCd);
        SegaCdDiscs.TryGetPath(file.Data, out var path).Should().BeTrue();
        path.Should().Be(Path.GetFullPath(cue));
        var disc = DiscImages.Open(cue);
        SegaCdDiscs.Region(disc).Should().Be(GameVault.Core.Genesis.GenesisRegion.Japan);
        (disc as IDisposable)?.Dispose();
        RomHasher.Hash(file.Data, GameSystem.SegaCd).Sha1.Should().HaveLength(40);
    }

    [Fact]
    public void Other_cd_images_stay_playstation_discs()
    {
        var cue = Disc(header: "NOT A SEGA DISC ");

        RomFiles.Read(cue)?.System.Should().NotBe(GameSystem.SegaCd);
    }

    [Fact]
    public void The_host_plays_a_disc_with_the_players_bios()
    {
        var cue = Disc();
        var frames = new FrameSink();
        using var host = new EmulatorHost(frames, new InputService(KeyboardMapping.CreateDefault()));

        host.Load(SegaCdDiscs.Reference(cue), "Game", system: GameSystem.SegaCd, segaCdBios: Bios());

        host.System.Should().Be(GameSystem.SegaCd);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        VideoFrame frame = null!;
        while (!frames.TryGetVideoFrame(out frame) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        frame.Should().NotBeNull();
        host.Stop();
    }

    [Fact]
    public async Task Without_a_bios_the_player_is_told_where_to_choose_one()
    {
        var cue = Disc();
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));

        var act = () => host.LoadAsync(cue);

        (await act.Should().ThrowAsync<GameLoadException>()).Which.Message.Should().Contain("Settings → Sega CD");
    }
}
