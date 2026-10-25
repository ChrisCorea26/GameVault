using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

public sealed class EmulatorHostTests : IDisposable
{
    private readonly FrameSink _frames = new();
    private readonly InputService _input = new(KeyboardMapping.CreateDefault());
    private readonly EmulatorHost _host;

    public EmulatorHostTests()
    {
        _host = new EmulatorHost(_frames, _input);
    }

    public void Dispose() => _host.Dispose();

    /// <summary>NROM image: reads controller 1 into $00 in a loop and copies each complete byte to $01.</summary>
    private static byte[] ControllerEchoRom()
    {
        byte[] program =
        [
            0xA9, 0x01, 0x8D, 0x16, 0x40, // LDA #1; STA $4016
            0xA9, 0x00, 0x8D, 0x16, 0x40, // LDA #0; STA $4016
            0xA2, 0x08,                   // LDX #8
            0xAD, 0x16, 0x40,             // loop: LDA $4016
            0x4A,                         // LSR A
            0x26, 0x00,                   // ROL $00
            0xCA,                         // DEX
            0xD0, 0xF7,                   // BNE loop
            0xA5, 0x00, 0x85, 0x01,       // LDA $00; STA $01
            0x4C, 0x00, 0xC0,             // JMP $C000
        ];
        var image = new byte[16 + 0x4000 + 0x2000];
        "NES\u001A"u8.CopyTo(image);
        image[4] = 1;
        image[5] = 1;
        program.CopyTo(image, 16);
        image[16 + 0x3FFC] = 0x00;
        image[16 + 0x3FFD] = 0xC0;
        return image;
    }

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Loading_starts_the_game_and_publishes_frames()
    {
        var statusChanges = 0;
        _host.StatusChanged += (_, _) => statusChanges++;

        _host.Load(ControllerEchoRom(), "Echo");

        _host.Status.Should().Be(EmulatorStatus.Running);
        _host.GameTitle.Should().Be("Echo");
        statusChanges.Should().Be(1);
        _frames.FrameReady.WaitOne(TimeSpan.FromSeconds(5)).Should().BeTrue();
        _frames.TryGetFrame(out var frame).Should().BeTrue();
        frame.Should().HaveCount(256 * 240);
    }

    [Fact]
    public void Frames_are_paced_near_60_per_second()
    {
        _host.Load(ControllerEchoRom(), "Echo");
        WaitFor(() => _host.FrameCount > 0);
        var start = _host.FrameCount;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Thread.Sleep(1000);

        // Pacing caps the rate near 60 fps. The lower bound only proves the game keeps running: a Debug
        // build on a busy CI runner (other test classes run in parallel) can fall well below real time.
        var fps = (_host.FrameCount - start) / clock.Elapsed.TotalSeconds;
        fps.Should().BeInRange(10, 75, "frames are paced near 60 per second, never run uncapped");
    }

    [Fact]
    public void Pause_stops_frames_and_resume_continues()
    {
        _host.Load(ControllerEchoRom(), "Echo");
        WaitFor(() => _host.FrameCount > 2);

        _host.Pause();
        _host.Status.Should().Be(EmulatorStatus.Paused);
        Thread.Sleep(50);
        var paused = _host.FrameCount;
        Thread.Sleep(100);
        _host.FrameCount.Should().Be(paused);

        _host.Resume();
        WaitFor(() => _host.FrameCount > paused);
    }

    [Fact]
    public void Stop_returns_to_idle()
    {
        _host.Load(ControllerEchoRom(), "Echo");

        _host.Stop();

        _host.Status.Should().Be(EmulatorStatus.Idle);
        _host.GameTitle.Should().BeNull();
    }

    [Fact]
    public void Bad_files_raise_plain_language_errors()
    {
        var notNes = () => _host.Load([1, 2, 3], "junk");
        notNes.Should().Throw<GameLoadException>().WithMessage("*isn't an NES game*");

        var rom = ControllerEchoRom();
        rom[6] = 0xF0; // mapper 239: no board is assigned to it
        rom[7] = (byte)((rom[7] & 0x0F) | 0xE0);
        var unsupported = () => _host.Load(rom, "unknown board");
        unsupported.Should().Throw<GameLoadException>().WithMessage("*doesn't support yet (mapper 239)*");

        _host.Status.Should().Be(EmulatorStatus.Idle);
    }

    [Fact]
    public async Task Missing_files_raise_a_plain_language_error()
    {
        var act = () => _host.LoadAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".nes"));

        await act.Should().ThrowAsync<GameLoadException>().WithMessage("*couldn't open*");
    }

    [Fact]
    public async Task Input_is_sampled_into_the_console_each_frame()
    {
        _host.Load(ControllerEchoRom(), "Echo");
        _input.KeyDown(VirtualKeys.X);
        _input.KeyDown(VirtualKeys.Left);
        var start = _host.FrameCount;
        WaitFor(() => _host.FrameCount > start + 2);

        // The ROM shifts the 8 buttons into a byte, so the first button read (A) ends up in bit 7.
        var echoed = await _host.InspectAsync(c => c.Bus.Peek(0x0001));

        echoed.Should().Be(0b1000_0010, "A is read first and Left seventh");
    }
}

public sealed class SampleEmulatorHostTests
{
    [Fact]
    public async Task Sample_host_draws_frames_without_a_rom()
    {
        var frames = new FrameSink();
        using var host = new SampleEmulatorHost(frames);

        await host.LoadAsync("anything.nes");

        host.Status.Should().Be(EmulatorStatus.Running);
        frames.FrameReady.WaitOne(TimeSpan.FromSeconds(5)).Should().BeTrue();
        frames.TryGetFrame(out var frame).Should().BeTrue();
        frame.Distinct().Count().Should().BeGreaterThan(10, "the pattern uses many palette entries");

        host.Stop();
        host.Status.Should().Be(EmulatorStatus.Idle);
    }
}
