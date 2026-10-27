using System.Buffers.Binary;
using GameVault.Core.Systems;
using GameVault.Library.Scanning;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>GameCube programs (phase-18.md, milestone 1) through the library's file reader and the host.</summary>
public sealed class GameCubeHostTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-gc-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>A DOL that loops at its entry point.</summary>
    private static byte[] Dol()
    {
        var dol = new byte[0x104];
        BinaryPrimitives.WriteUInt32BigEndian(dol, 0x100);
        BinaryPrimitives.WriteUInt32BigEndian(dol.AsSpan(0x48), 0x80003100);
        BinaryPrimitives.WriteUInt32BigEndian(dol.AsSpan(0x90), 4);
        BinaryPrimitives.WriteUInt32BigEndian(dol.AsSpan(0xE0), 0x80003100);
        BinaryPrimitives.WriteUInt32BigEndian(dol.AsSpan(0x100), 0x48000000); // b .
        return dol;
    }

    [Fact]
    public void A_dol_plays_as_a_gamecube_program()
    {
        var path = Path.Combine(_root.FullName, "Demo.dol");
        File.WriteAllBytes(path, Dol());
        RomFiles.Read(path)!.System.Should().Be(GameSystem.GameCube);
        var frames = new FrameSink();
        using var host = new EmulatorHost(frames, new InputService(KeyboardMapping.CreateDefault()));

        host.Load(File.ReadAllBytes(path), "Demo", system: GameSystem.GameCube);

        host.System.Should().Be(GameSystem.GameCube);
        VideoFrame frame = null!;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!(frames.TryGetVideoFrame(out frame) && frame.Width == 640) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        frame.Width.Should().Be(640);
        frame.Height.Should().Be(480);
        host.Stop();
    }

    [Fact]
    public void A_powerpc_elf_is_a_gamecube_program_and_a_file_of_neither_kind_isnt()
    {
        var elf = new byte[0x40];
        elf[0] = 0x7F;
        "ELF"u8.CopyTo(elf.AsSpan(1));
        elf[4] = 1;
        elf[5] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(elf.AsSpan(0x12), 20);
        var elfPath = Path.Combine(_root.FullName, "Demo.elf");
        File.WriteAllBytes(elfPath, elf);
        var junk = Path.Combine(_root.FullName, "Junk.dol");
        File.WriteAllBytes(junk, new byte[0x200]);

        RomFiles.Read(elfPath)!.System.Should().Be(GameSystem.GameCube);
        RomFiles.Read(junk).Should().BeNull();
    }
}
