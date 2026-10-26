using FluentAssertions;
using GameVault.Core.Genesis;
using GameVault.Core.Systems;
using GameVault.Library.Hashing;
using GameVault.Library.Scanning;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Video;

namespace GameVault.Platform.Tests;

/// <summary>Genesis games in the emulator host and the library (SPEC §7.11, Phase 13).</summary>
public sealed class GenesisHostTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-genesis-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>A ROM that counts V-interrupts in RAM ($FF0000) with the display on (H40, a blue background).</summary>
    internal static byte[] CountingRom()
    {
        ushort[] program =
        [
            0x33FC, 0x8C81, 0x00C0, 0x0004,                 // H40
            0x23FC, 0xC000, 0x0000, 0x00C0, 0x0004,         // CRAM 0
            0x33FC, 0x0E00, 0x00C0, 0x0000,                 // blue
            0x33FC, 0x8164, 0x00C0, 0x0004,                 // display, V-int, mode 5
            0x46FC, 0x2000,                                 // interrupts on
            0x60FE,
        ];
        var rom = new byte[0x10000];
        void Long(int at, uint value)
        {
            rom[at] = (byte)(value >> 24);
            rom[at + 1] = (byte)(value >> 16);
            rom[at + 2] = (byte)(value >> 8);
            rom[at + 3] = (byte)value;
        }

        Long(0, 0xFFFFFE00);
        Long(4, 0x200);
        Long(30 * 4, 0x300); // level 6 autovector
        "SEGA MEGA DRIVE "u8.CopyTo(rom.AsSpan(0x100));
        "JUE"u8.CopyTo(rom.AsSpan(0x1F0));
        for (var i = 0; i < program.Length; i++)
        {
            rom[0x200 + i * 2] = (byte)(program[i] >> 8);
            rom[0x200 + i * 2 + 1] = (byte)program[i];
        }

        // ADDQ.W #1,$FF0000; RTE
        byte[] handler = [0x52, 0x79, 0x00, 0xFF, 0x00, 0x00, 0x4E, 0x73];
        handler.CopyTo(rom, 0x300);
        return rom;
    }

    [Fact]
    public async Task A_genesis_file_loads_and_runs_at_320_by_224_with_the_six_button_layout()
    {
        var path = Path.Combine(_root.FullName, "Counter.md");
        await File.WriteAllBytesAsync(path, CountingRom());
        var frames = new FrameSink();
        var input = new InputService(KeyboardMapping.CreateDefault());
        using var host = new EmulatorHost(frames, input);

        await host.LoadAsync(path);

        host.System.Should().Be(GameSystem.Genesis);
        input.SixButtonLayout.Should().BeTrue();
        host.AchievementsHash.Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(CountingRom())));
        await WaitFor(async () => await host.InspectAsync(c => c.PeekMemory(1)) > 30);
        uint color = 0;
        await WaitFor(() =>
        {
            // Each frame can be taken once: check its size and colour while it's in hand.
            if (frames.TryGetVideoFrame(out var frame) && frame.Width == 320 && frame.Height == 224)
            {
                color = frames.Palette[frame.Span[100]];
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        });
        color.Should().Be(0xFF0000FFu, "a blue background");

        var state = await host.CaptureStateAsync();
        (await host.RestoreAsync(state!)).Should().BeTrue();

        host.Stop();
        input.SixButtonLayout.Should().BeFalse();
    }

    [Fact]
    public void The_library_finds_genesis_files_hashes_smd_images_like_plain_roms_and_skips_other_bins()
    {
        var rom = CountingRom();
        var smd = new byte[0x200 + rom.Length];
        smd[8] = 0xAA;
        smd[9] = 0xBB;
        for (var block = 0; block < rom.Length / 0x4000; block++)
        {
            for (var i = 0; i < 0x2000; i++)
            {
                smd[0x200 + block * 0x4000 + i] = rom[block * 0x4000 + i * 2 + 1];
                smd[0x200 + block * 0x4000 + 0x2000 + i] = rom[block * 0x4000 + i * 2];
            }
        }

        RomHasher.Hash(smd, GameSystem.Genesis).Should().Be(RomHasher.Hash(rom, GameSystem.Genesis));
        RomFiles.SystemOf("Game.gen").Should().Be(GameSystem.Genesis);
        RomFiles.SystemOf("Game.smd").Should().Be(GameSystem.Genesis);

        var genesisBin = Path.Combine(_root.FullName, "Game.bin");
        File.WriteAllBytes(genesisBin, rom);
        var otherBin = Path.Combine(_root.FullName, "Other.bin");
        File.WriteAllBytes(otherBin, new byte[0x1000]);
        RomFiles.Read(genesisBin)!.System.Should().Be(GameSystem.Genesis);
        RomFiles.Read(otherBin).Should().BeNull("a .bin without a Genesis header isn't taken");
    }

    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(20);
        }
    }
}
