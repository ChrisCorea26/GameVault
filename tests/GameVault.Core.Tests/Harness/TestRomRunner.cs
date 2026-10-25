using System.Text;

namespace GameVault.Core.Tests.Harness;

/// <summary>
/// Runs a blargg-style test ROM (SPEC §11.2). These ROMs write a signature (DE B0 61) to $6001-$6003,
/// keep $6000 at $80 while running, set $81 when they need the reset button pressed, and finally write
/// a result code to $6000 (0 = pass) with a zero-terminated message at $6004.
/// </summary>
public static class TestRomRunner
{
    private const byte Running = 0x80;
    private const byte NeedsReset = 0x81;

    public static TestRomResult Run(byte[] rom, int maxFrames = 60 * 60) => Run(rom, out _, maxFrames);

    public static TestRomResult Run(byte[] rom, out NesConsole console, int maxFrames = 60 * 60)
    {
        console = NesConsole.FromBytes(rom);
        console.Power();

        int? resetAtFrame = null;

        // After a reset, $6000 still reads $81 until the ROM's reset handler gets going. Ignore that stale
        // request; otherwise a second reset lands while the ROM is in the middle of its next step.
        var waitingForRestart = false;
        for (var frame = 0; frame < maxFrames; frame++)
        {
            console.RunFrame();

            if (console.Cpu.IsJammed)
            {
                return new TestRomResult(TestRomStatus.Jammed, -1, ReadMessage(console), frame);
            }

            if (!HasSignature(console))
            {
                continue;
            }

            var status = console.Bus.Peek(0x6000);
            if (waitingForRestart)
            {
                if (status == NeedsReset)
                {
                    continue;
                }

                waitingForRestart = false;
            }

            switch (status)
            {
                case Running:
                    continue;
                case NeedsReset:
                    // The ROM asks for the reset button after a short delay (at least 100 ms).
                    resetAtFrame ??= frame + 10;
                    if (frame >= resetAtFrame)
                    {
                        console.Reset();
                        resetAtFrame = null;
                        waitingForRestart = true;
                    }

                    continue;
                default:
                    return new TestRomResult(status == 0 ? TestRomStatus.Passed : TestRomStatus.Failed, status, ReadMessage(console), frame);
            }
        }

        return new TestRomResult(TestRomStatus.TimedOut, -1, ReadMessage(console), maxFrames);
    }

    /// <summary>
    /// Runs an older (2005) blargg ROM for a fixed number of frames. These store a result code in zero
    /// page (1 = passed, 2+ = which check failed) and print it on screen. They also use that byte as scratch
    /// while running, and they have no "done" flag, so the code is read after a fixed time. All of them
    /// settle within 110 frames; the default allows more than twice that.
    /// </summary>
    public static TestRomResult RunWithZeroPageResult(byte[] rom, ushort resultAddress, out NesConsole console, int frames = 240)
    {
        console = NesConsole.FromBytes(rom);
        console.Power();
        for (var frame = 0; frame < frames; frame++)
        {
            console.RunFrame();
        }

        var code = console.Bus.Peek(resultAddress);
        return new TestRomResult(code == 1 ? TestRomStatus.Passed : TestRomStatus.Failed, code, $"result code {code}", frames);
    }

    private static bool HasSignature(NesConsole console) =>
        console.Bus.Peek(0x6001) == 0xDE && console.Bus.Peek(0x6002) == 0xB0 && console.Bus.Peek(0x6003) == 0x61;

    private static string ReadMessage(NesConsole console)
    {
        var text = new StringBuilder();
        for (ushort address = 0x6004; address < 0x8000; address++)
        {
            var b = console.Bus.Peek(address);
            if (b == 0)
            {
                break;
            }

            text.Append((char)b);
        }

        return text.ToString().Trim();
    }
}

public enum TestRomStatus
{
    Passed,
    Failed,
    TimedOut,
    Jammed,
}

public sealed record TestRomResult(TestRomStatus Status, int Code, string Message, int Frames)
{
    public override string ToString() => $"{Status} (code {Code}) after {Frames} frames: {Message}";
}
