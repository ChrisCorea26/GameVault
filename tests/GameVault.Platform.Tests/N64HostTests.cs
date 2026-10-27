using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using GameVault.Core.Input;
using GameVault.Core.Nintendo64;
using GameVault.Core.Systems;
using GameVault.Library.Hashing;
using GameVault.Library.Profiles;
using GameVault.Library.Scanning;
using GameVault.Platform.Achievements;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Storage;
using GameVault.Platform.Video;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>Nintendo 64 games in the emulator host, the library and the input layer (SPEC §7.11, Phase 16).</summary>
public sealed class N64HostTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-n64-");

    public void Dispose() => _root.Delete(recursive: true);

    private static uint I(int opcode, int rt, int rs, int immediate) => (uint)((opcode << 26) | (rs << 21) | (rt << 16) | (immediate & 0xFFFF));

    private static uint Lui(int rt, int immediate) => I(0x0F, rt, 0, immediate);

    private static uint Ori(int rt, int rs, int immediate) => I(0x0D, rt, rs, immediate);

    private static uint Sw(int rt, int rs, int offset) => I(0x2B, rt, rs, offset);

    private static uint Lw(int rt, int rs, int offset) => I(0x23, rt, rs, offset);

    private static uint Addiu(int rt, int rs, int immediate) => I(0x09, rt, rs, immediate);

    private static uint Bne(int rs, int rt, int offset) => I(0x05, rt, rs, offset);

    private static uint Beq(int rs, int rt, int offset) => I(0x04, rt, rs, offset);

    private static uint Addu(int rd, int rs, int rt) => (uint)((rs << 21) | (rt << 16) | (rd << 11) | 0x21);

    /// <summary>
    /// A cartridge whose boot code (run from DMEM by the simulated PIF) shows a red 320×240 16-bit framebuffer and
    /// then counts in RDRAM at $10.
    /// </summary>
    internal static byte[] RedScreenRom(string gameCode = "NGVE")
    {
        uint[] program =
        [
            Lui(8, 0xA440),
            Ori(9, 0, 0x3002), Sw(9, 8, 0x00), // 16-bit
            Lui(9, 0x0020), Sw(9, 8, 0x04), // origin $200000
            Ori(9, 0, 320), Sw(9, 8, 0x08),
            Ori(9, 0, 0x20D), Sw(9, 8, 0x18),
            Ori(9, 0, 0xC15), Sw(9, 8, 0x1C),
            Lui(9, 0x006C), Ori(9, 9, 0x02EC), Sw(9, 8, 0x24),
            Lui(9, 0x0025), Ori(9, 9, 0x01FF), Sw(9, 8, 0x28),
            Ori(9, 0, 0x200), Sw(9, 8, 0x30),
            Ori(9, 0, 0x400), Sw(9, 8, 0x34),
            Lui(10, 0xA020), // the framebuffer
            Lui(11, 0xF801), Ori(11, 11, 0xF801), // two red pixels
            Lui(12, 0x0002), Ori(12, 12, 0x5800), Addu(12, 12, 10),
            Sw(11, 10, 0), Addiu(10, 10, 4), Bne(10, 12, -3), 0,
            Lui(13, 0xA000),
            Lw(14, 13, 0x10), Addiu(14, 14, 1), Sw(14, 13, 0x10), Beq(0, 0, -4), 0,
        ];
        var rom = new byte[0x10_0000];
        BinaryPrimitives.WriteUInt32BigEndian(rom, 0x8037_1240);
        Encoding.ASCII.GetBytes("GAMEVAULT TEST      ").CopyTo(rom, 0x20);
        Encoding.ASCII.GetBytes(gameCode).CopyTo(rom, 0x3B);
        for (var i = 0; i < program.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(0x40 + i * 4), program[i]);
        }

        return rom;
    }

    /// <summary>
    /// A cartridge whose boot code shows a 320×240 16-bit framebuffer that the RDP fills green (a command list in
    /// RDRAM at $100000), then counts in RDRAM at $10.
    /// </summary>
    internal static byte[] GreenRdpRom()
    {
        uint[] commands =
        [
            0x3F10013F, 0x00200000, // Set Color Image: RGBA16, 320 wide, at $200000
            0x2D000000, 0x005003C0, // Set Scissor: 0, 0 to 320, 240
            0x2F300000, 0x00000000, // Set Other Modes: fill
            0x37000000, 0x07C107C1, // Set Fill Color: green
            0x364FC3BC, 0x00000000, // Fill Rectangle: 0, 0 to 319, 239
            0x29000000, 0x00000000, // Sync Full
        ];
        List<uint> program =
        [
            Lui(8, 0xA440),
            Ori(9, 0, 0x3002), Sw(9, 8, 0x00), // 16-bit
            Lui(9, 0x0020), Sw(9, 8, 0x04), // origin $200000
            Ori(9, 0, 320), Sw(9, 8, 0x08),
            Ori(9, 0, 0x20D), Sw(9, 8, 0x18),
            Ori(9, 0, 0xC15), Sw(9, 8, 0x1C),
            Lui(9, 0x006C), Ori(9, 9, 0x02EC), Sw(9, 8, 0x24),
            Lui(9, 0x0025), Ori(9, 9, 0x01FF), Sw(9, 8, 0x28),
            Ori(9, 0, 0x200), Sw(9, 8, 0x30),
            Ori(9, 0, 0x400), Sw(9, 8, 0x34),
            Lui(10, 0xA010), // the command list
        ];
        for (var i = 0; i < commands.Length; i++)
        {
            program.AddRange([Lui(9, (int)(commands[i] >> 16)), Ori(9, 9, (int)(commands[i] & 0xFFFF)), Sw(9, 10, i * 4)]);
        }

        program.AddRange(
        [
            Lui(8, 0xA410), // DPC_START and DPC_END
            Lui(9, 0x0010), Sw(9, 8, 0x00),
            Ori(9, 9, commands.Length * 4), Sw(9, 8, 0x04),
            Lui(13, 0xA000),
            Lw(14, 13, 0x10), Addiu(14, 14, 1), Sw(14, 13, 0x10), Beq(0, 0, -4), 0,
        ]);
        var rom = new byte[0x10_0000];
        BinaryPrimitives.WriteUInt32BigEndian(rom, 0x8037_1240);
        Encoding.ASCII.GetBytes("GAMEVAULT RDP TEST  ").CopyTo(rom, 0x20);
        Encoding.ASCII.GetBytes("NGRE").CopyTo(rom, 0x3B);
        for (var i = 0; i < program.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(0x40 + i * 4), program[i]);
        }

        return rom;
    }

    /// <summary>The same ROM in the .v64 (byte-swapped) order.</summary>
    private static byte[] ByteSwapped(byte[] rom)
    {
        var swapped = rom.ToArray();
        for (var i = 0; i < swapped.Length; i += 2)
        {
            (swapped[i], swapped[i + 1]) = (swapped[i + 1], swapped[i]);
        }

        return swapped;
    }

    [Fact]
    public async Task An_n64_file_loads_runs_and_shows_its_framebuffer_with_the_n64_pad()
    {
        var path = Path.Combine(_root.FullName, "Red.v64");
        await File.WriteAllBytesAsync(path, ByteSwapped(RedScreenRom()));
        var frames = new FrameSink();
        var input = new InputService(KeyboardMapping.CreateDefault());
        using var host = new EmulatorHost(frames, input);

        await host.LoadAsync(path);

        host.System.Should().Be(GameSystem.Nintendo64);
        input.Layout.Should().Be(PadLayout.Nintendo64);
        await WaitFor(async () => await host.InspectAsync(c => c.PeekMemory(0x13)) > 0);
        uint color = 0;
        await WaitFor(() =>
        {
            if (frames.TryGetVideoFrame(out var frame) && frame.Width == 320 && frame.Height == 237)
            {
                color = frames.Palette[frame.Span[1000]];
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        });
        color.Should().Be(0xFFFF0000u, "a red framebuffer");

        host.Stop();
        input.Layout.Should().Be(PadLayout.Nes);
    }

    [Fact]
    public async Task At_2x_the_gpu_renderer_draws_the_picture_on_windows_and_the_software_rdp_elsewhere()
    {
        var path = Path.Combine(_root.FullName, "Green.z64");
        await File.WriteAllBytesAsync(path, GreenRdpRom());
        var frames = new FrameSink();
        var settings = new InMemorySettingsStore(new AppSettings { N64InternalResolution = 2 });
        using var host = new EmulatorHost(frames, new InputService(KeyboardMapping.CreateDefault()), settings);

        await host.LoadAsync(path);

        // Direct3D only exists on Windows (CI's Windows runner has WARP); elsewhere the software picture stays. The
        // software picture shows first, until the GPU renderer is ready.
        var (width, height) = OperatingSystem.IsWindows() ? (640, 474) : (320, 237);
        uint color = 0;
        await WaitFor(() =>
        {
            if (frames.TryGetVideoFrame(out var frame) && frame.Width == width && frame.Height == height)
            {
                var argb = new uint[width * height];
                frame.ToArgb(frames.Palette, argb);
                color = argb[width * (height / 2) + width / 2];
                return Task.FromResult(color != 0xFF000000);
            }

            return Task.FromResult(false);
        }, seconds: 600); // the renderer is made in the background, and the first start compiles its kernel (minutes on WARP)
        color.Should().Be(0xFF00FF00u, "the RDP filled the screen green");
        host.Stop();
    }

    [Fact]
    public async Task The_controller_pak_is_saved_as_a_raw_mpk_image()
    {
        var path = Path.Combine(_root.FullName, "Red.z64");
        await File.WriteAllBytesAsync(path, RedScreenRom());
        var paths = new AppPaths(Path.Combine(_root.FullName, "data"));
        using (var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()), paths: paths))
        {
            await host.LoadAsync(path);
            await WaitFor(async () => await host.InspectAsync(c => c.PeekMemory(0x13)) > 0);
            await host.InspectAsync(c => ((N64Console)c).ControllerPak.Memory[0x500] = 0x42);
            host.Stop();
        }

        var pak = Directory.GetFiles(paths.SavesDirectory, "*.mpk").Should().ContainSingle().Which;
        File.ReadAllBytes(pak)[0x500].Should().Be(0x42);
        new FileInfo(pak).Length.Should().Be(N64ControllerPak.Size);
    }

    [Fact]
    public async Task The_save_type_list_picks_the_cartridges_save_memory()
    {
        var path = Path.Combine(_root.FullName, "Flash.z64");
        await File.WriteAllBytesAsync(path, RedScreenRom("NZSE"));
        var list = Path.Combine(_root.FullName, "saves.json");
        await File.WriteAllTextAsync(list, """{ "ZS": "flash" }""");
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()))
        {
            N64SaveTypes = new N64SaveTypes(list).For,
        };

        await host.LoadAsync(path);

        (await host.InspectAsync(c => ((N64Console)c).Cartridge.SaveType)).Should().Be(N64SaveType.FlashRam);
        (await host.InspectAsync(c => ((N64Console)c).Rdram.Length)).Should().Be(8 * 1024 * 1024, "the Expansion Pak is on by default");
        host.Stop();
    }

    [Fact]
    public void The_library_finds_n64_files_in_every_byte_order_and_hashes_them_alike()
    {
        var rom = RedScreenRom();
        var v64 = ByteSwapped(rom);
        var n64 = rom.ToArray();
        for (var i = 0; i < n64.Length; i += 4)
        {
            Array.Reverse(n64, i, 4);
        }

        foreach (var extension in new[] { ".z64", ".v64", ".n64" })
        {
            RomFiles.SystemOf("Game" + extension).Should().Be(GameSystem.Nintendo64);
        }

        RomHasher.Hash(v64, GameSystem.Nintendo64).Should().Be(RomHasher.Hash(rom, GameSystem.Nintendo64));
        RomHasher.Hash(n64, GameSystem.Nintendo64).Should().Be(RomHasher.Hash(rom, GameSystem.Nintendo64));
        AchievementsHashing.ForSystem(v64, GameSystem.Nintendo64).Should().Be(
            Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(rom)), "rcheevos hashes the big-endian ROM");
    }

    [Fact]
    public void The_save_type_list_and_profiles_name_save_memories()
    {
        N64SaveTypes.Parse("eeprom16k").Should().Be(N64SaveType.Eeprom16K);
        N64SaveTypes.Parse("FLASH").Should().Be(N64SaveType.FlashRam);
        N64SaveTypes.Parse("cassette").Should().BeNull();
        var bundled = new N64SaveTypes(Path.Combine(FindRepoRoot(), "data", "gamedb", "n64", "saves.json"));
        bundled.Error.Should().BeNull();
        bundled.For("NSME").Should().Be(N64SaveType.Eeprom4K);
        bundled.For("NXXE").Should().BeNull();
        new GameProfile { Save = "sram" }.Merge(new GameProfile { Save = "flash" }).Save.Should().Be("flash");
    }

    [Fact]
    public void On_the_n64_pad_the_arrows_push_the_stick_and_other_keys_are_the_d_pad_and_c_buttons()
    {
        var input = new InputService(KeyboardMapping.CreateDefault()) { Layout = PadLayout.Nintendo64 };

        input.KeyDown(VirtualKeys.Up);
        input.KeyDown(VirtualKeys.Left);
        var state = input.Sample(0);
        state.StickX().Should().Be(-InputService.StickRange);
        state.StickY().Should().Be(InputService.StickRange);
        state.Buttons().Should().Be(ControllerState.None, "the arrows aren't the D-pad here");
        input.KeyUp(VirtualKeys.Up);
        input.KeyUp(VirtualKeys.Left);

        input.KeyDown(VirtualKeys.T); // D-pad up
        input.KeyDown(VirtualKeys.I); // C-up
        input.KeyDown(VirtualKeys.Z); // A
        input.Sample(0).Should().Be(ControllerState.Up | ControllerState.C | ControllerState.B);
        input.KeyUp(VirtualKeys.T);
        input.KeyUp(VirtualKeys.I);
        input.KeyUp(VirtualKeys.Z);

        // A game controller's stick, scaled to the N64's range past a small dead zone (+Y is down in SDL).
        input.SetStick(1, 0, short.MaxValue, 0);
        input.Sample(0).StickX().Should().Be(InputService.StickRange);
        input.SetStick(1, 0, 1000, -1000);
        input.Sample(0).Should().Be(ControllerState.None, "inside the dead zone");
        input.SetStick(1, 0, 0, short.MinValue);
        input.Sample(0).StickY().Should().Be(InputService.StickRange);

        // Other systems never see the stick or the N64-only keys.
        input.Layout = PadLayout.Snes;
        input.KeyDown(VirtualKeys.I);
        input.Sample(0).Should().Be(ControllerState.None);
    }

    [Fact]
    public void On_the_ps2_pad_the_arrows_push_the_left_stick_and_ijkl_the_right_one()
    {
        var input = new InputService(KeyboardMapping.CreateDefault()) { Layout = PadLayout.PlayStation2 };

        input.KeyDown(VirtualKeys.Up);
        input.KeyDown(VirtualKeys.Left);
        input.KeyDown(VirtualKeys.L); // right stick right
        var state = input.Sample(0);
        state.StickX().Should().Be(-InputService.DualShockStickRange);
        state.StickY().Should().Be(InputService.DualShockStickRange);
        state.RightStickX().Should().Be(InputService.DualShockStickRange);
        state.RightStickY().Should().Be(0);
        state.Buttons().Should().Be(ControllerState.None, "neither the arrows nor I/J/K/L are the D-pad here");
        input.KeyUp(VirtualKeys.Up);
        input.KeyUp(VirtualKeys.Left);
        input.KeyUp(VirtualKeys.L);
        input.Sample(0).Should().Be(ControllerState.None);

        // The PlayStation's keys, T/F/G/H as the D-pad and 5/6 as L3/R3.
        input.KeyDown(VirtualKeys.T);
        input.KeyDown(VirtualKeys.Z); // Cross
        input.KeyDown(VirtualKeys.A); // Square (no turbo)
        input.KeyDown(VirtualKeys.Digit4); // R2
        input.KeyDown(VirtualKeys.Digit5); // L3
        input.KeyDown(VirtualKeys.Digit6); // R3
        input.Sample(0, frame: 2).Should().Be(ControllerState.Up | ControllerState.B | ControllerState.Y | ControllerState.R2 | ControllerState.L3 | ControllerState.R3);
        foreach (var key in (int[])[VirtualKeys.T, VirtualKeys.Z, VirtualKeys.A, VirtualKeys.Digit4, VirtualKeys.Digit5, VirtualKeys.Digit6])
        {
            input.KeyUp(key);
        }

        // A pad's right stick pushed further than the keys wins, scaled to the DualShock's full byte.
        input.KeyDown(VirtualKeys.K); // right stick down
        input.Sample(0).RightStickY().Should().Be(-InputService.DualShockStickRange);
        input.SetRightStick(1, 0, short.MaxValue, 0);
        input.Sample(0).RightStickY().Should().Be(-InputService.DualShockStickRange, "the key is all the way, as far as the pad");
        input.KeyUp(VirtualKeys.K);
        input.Sample(0).RightStickX().Should().Be(InputService.DualShockStickRange);
        input.SetRightStick(1, 0, 1000, 1000);
        input.Sample(0).Should().Be(ControllerState.None, "inside the dead zone");

        // The N64 sees the same keys as its C buttons and never a right stick.
        input.Layout = PadLayout.Nintendo64;
        input.SetRightStick(1, 0, short.MaxValue, 0);
        input.KeyDown(VirtualKeys.Digit5);
        input.Sample(0).Should().Be(ControllerState.None, "5 isn't an N64 key, and the N64 reads no right stick");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GameVault.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root wasn't found.");
    }

    private static async Task WaitFor(Func<Task<bool>> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
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
