using System.Buffers.Binary;
using GameVault.Core.PlayStation2;
using GameVault.Core.Snes;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.PlayStation2.Homebrew;

/// <summary>Runs one of GameVault's PS2 homebrew programs (tests/Ps2Homebrew) on the whole console.</summary>
internal static class Ps2HomebrewRunner
{
    /// <summary>The status block the programs write at physical $1FFF00 (tests/Ps2Homebrew/README.md).</summary>
    public const int StatusBlock = 0x1F_FF00;
    public const uint Signature = 0x42485647; // "GVHB"

    public static string ElfPath(string name) =>
        Path.Combine(TestRomLocator.RepoRoot, "tests", "Ps2Homebrew", "elf", name + ".elf");

    /// <summary>Runs until the status block is signed, then a few more frames; returns the console.</summary>
    public static Ps2Console Run(string name, int maxFrames = 120, int settleFrames = 4)
    {
        var console = new Ps2Console(Ps2Boot.FromElf(File.ReadAllBytes(ElfPath(name)), "host:" + name + ".elf"));
        var frames = 0;
        while (frames < maxFrames && Word(console, 0) != Signature)
        {
            console.RunFrame();
            frames++;
        }

        for (var i = 0; i < settleFrames; i++)
        {
            console.RunFrame();
        }

        return console;
    }

    public static uint Word(Ps2Console console, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(console.Rdram.AsSpan(StatusBlock + index * 4));

    /// <summary>The frame's pixel at (x, y) as 8-bit RGB (from the BGR555 frame, through the palette).</summary>
    public static (int R, int G, int B) Pixel(Ps2Console console, int x, int y)
    {
        var argb = SnesPpu.Palette[console.FrameBuffer[y * console.ScreenWidth + x]];
        return ((int)(argb >> 16) & 0xFF, (int)(argb >> 8) & 0xFF, (int)argb & 0xFF);
    }

    /// <summary>Writes the frame as a PNG (for looking at it while developing).</summary>
    public static void SavePng(Ps2Console console, string path) =>
        File.WriteAllBytes(path, PngWriter.Encode(PngWriter.ToArgb(console.FrameBuffer, SnesPpu.Palette), console.ScreenWidth, console.ScreenHeight));
}
