using GameVault.Core.Apu;
using GameVault.Core.Audio;
using GameVault.Core.Bus;
using GameVault.Core.Cartridge;
using GameVault.Core.Cartridge.Fds;
using GameVault.Core.Cheats;
using GameVault.Core.Cpu;
using GameVault.Core.Input;
using GameVault.Core.Ppu;
using GameVault.Core.State;
using GameVault.Core.Systems;

namespace GameVault.Core;

/// <summary>
/// The whole console. Owns every component and is the only entry point the platform layer uses.
/// Deterministic: the same ROM and inputs from power-on always produce the same state.
/// </summary>
public sealed class NesConsole : IEmulatedConsole
{
    private static readonly StateTag Tag = StateTag.From("NES ");
    private static readonly StateTag SnapshotTag = StateTag.From("GVST");

    public NesConsole(INesRom rom)
        : this(rom, MapperFactory.Create(rom))
    {
    }

    private NesConsole(INesRom rom, IMapper mapper)
    {
        Rom = rom;
        Mapper = mapper;
        Ppu = new Ppu2C02(Mapper);
        Apu = new Apu2A03(pal: rom.TvSystem is TvSystem.Pal or TvSystem.Dendy) { ExpansionAudio = Mapper.ExpansionAudio };
        var clock = rom.TvSystem switch
        {
            TvSystem.Pal => ClockRatio.Pal,
            TvSystem.Dendy => ClockRatio.Dendy,
            _ => ClockRatio.Ntsc,
        };
        Bus = new NesBus(Ppu, Apu, Mapper, clock, Controller1, Controller2);
        Zapper = new Zapper(Ppu);
        Cpu = new Cpu6502(Bus) { DmcSource = Apu };
        Bus.AttachCpu(Cpu);
        Apu.CpuCycle = () => Cpu.CycleCount;
        RomHash = HashRom(rom, (mapper as FdsAdapter)?.Tracks);
    }

    /// <summary>FNV-1a over PRG and CHR. Identifies the game a snapshot belongs to (not a security hash).</summary>
    public ulong RomHash { get; }

    public INesRom Rom { get; }

    public Cpu6502 Cpu { get; }

    public NesBus Bus { get; }

    public Ppu2C02 Ppu { get; }

    public Apu2A03 Apu { get; }

    public IMapper Mapper { get; }

    /// <summary>Resampled audio, once <see cref="EnableAudio"/> has been called.</summary>
    public BandLimitedResampler? Audio { get; private set; }

    /// <summary>The CPU clock in Hz for this console's region (NESdev Wiki: "Cycle reference chart").</summary>
    public double CpuClockRate => Rom.TvSystem switch
    {
        TvSystem.Pal => 26601712.0 / 16,
        TvSystem.Dendy => 26601712.0 / 15,
        _ => 236250000.0 / 11 / 12,
    };

    public StandardController Controller1 { get; } = new();

    public StandardController Controller2 { get; } = new();

    /// <summary>Player 3, used only with the Four Score.</summary>
    public StandardController Controller3 { get; } = new();

    /// <summary>Player 4, used only with the Four Score.</summary>
    public StandardController Controller4 { get; } = new();

    /// <summary>The light gun; active on port 2 after <see cref="ConnectInput"/> with <see cref="InputSetup.Zapper"/>.</summary>
    public Zapper Zapper { get; }

    /// <summary>The Power Pad mat; active on port 2 after <see cref="ConnectInput"/> with <see cref="InputSetup.PowerPad"/>.</summary>
    public PowerPad PowerPad { get; } = new();

    /// <summary>The Arkanoid paddle; active on port 2 after <see cref="ConnectInput"/> with <see cref="InputSetup.ArkanoidPaddle"/>.</summary>
    public ArkanoidPaddle Paddle { get; } = new();

    public InputSetup InputSetup { get; private set; } = InputSetup.Controllers;

    /// <summary>
    /// Chooses what is plugged into the ports (SPEC §4.7): two controllers, a controller and a Zapper, or a
    /// Four Score with four controllers. Call before running, or between frames.
    /// </summary>
    public void ConnectInput(InputSetup setup)
    {
        InputSetup = setup;
        switch (setup)
        {
            case InputSetup.Zapper:
                Bus.SetPort(0, Controller1);
                Bus.SetPort(1, Zapper);
                break;
            case InputSetup.PowerPad:
                Bus.SetPort(0, Controller1);
                Bus.SetPort(1, PowerPad);
                break;
            case InputSetup.ArkanoidPaddle:
                Bus.SetPort(0, Controller1);
                Bus.SetPort(1, Paddle);
                break;
            case InputSetup.FourScore:
                Bus.SetPort(0, new FourScorePort(Controller1, Controller3, 0));
                Bus.SetPort(1, new FourScorePort(Controller2, Controller4, 1));
                break;
            default:
                Bus.SetPort(0, Controller1);
                Bus.SetPort(1, Controller2);
                break;
        }
    }

    public static NesConsole FromBytes(ReadOnlySpan<byte> romData) => new(INesRom.Parse(romData));

    /// <summary>
    /// A Famicom Disk System console (SPEC §4.5 Tier 4) from a disk image and the player's own 8 KB BIOS
    /// (<c>disksys.rom</c>), which is never shipped with GameVault. Side A of the first disk starts inserted.
    /// </summary>
    public static NesConsole FromFds(ReadOnlySpan<byte> diskImage, ReadOnlySpan<byte> bios)
    {
        var rom = FdsAdapter.BiosRom(bios);
        return new NesConsole(rom, new FdsAdapter(rom, FdsImage.Parse(diskImage)));
    }

    public GameSystem System => GameSystem.Nes;

    public ReadOnlySpan<ushort> FrameBuffer => Ppu.FrameBuffer;

    public ReadOnlySpan<uint> Palette => NtscPalette.Argb;

    public long FrameCount => Ppu.FrameCount;

    /// <summary>Four with the Four Score; the platform sends all four either way.</summary>
    public int ControllerPorts => 4;

    public IBatteryBacked? Battery => Mapper is IBatteryBacked { HasBattery: true } battery ? battery : null;

    public byte PeekMemory(int address) => Bus.Peek((ushort)address);

    /// <summary>The Disk System adapter when this console runs an FDS game.</summary>
    public FdsAdapter? Fds => Mapper as FdsAdapter;

    /// <summary>Starts producing audio samples at <paramref name="sampleRate"/>; read them from <see cref="Audio"/>.</summary>
    public BandLimitedResampler EnableAudio(int sampleRate = 48000)
    {
        Audio ??= new BandLimitedResampler(CpuClockRate, sampleRate);
        Apu.AudioSink = Audio;
        return Audio;
    }

    public void Power()
    {
        Bus.Power();
        Ppu.Power();
        Apu.Power();
        Cpu.Power();
    }

    /// <summary>The console's reset button.</summary>
    public void Reset()
    {
        Ppu.Reset();
        Apu.Reset();
        Cpu.Reset();
    }

    public void StepInstruction() => Cpu.Step();

    /// <summary>
    /// Sets the buttons for a player: 0 and 1 always, 2 and 3 for the Four Score's players 3 and 4. The
    /// platform layer calls this between frames with the merged state for each port (docs/design/input.md §2).
    /// </summary>
    public void SetControllerState(int port, ControllerState state)
    {
        var controller = port switch
        {
            0 => Controller1,
            1 => Controller2,
            2 => Controller3,
            3 => Controller4,
            _ => throw new ArgumentOutOfRangeException(nameof(port), port, "Ports 0-3 only."),
        };
        controller.State = state;

        // A disk change that came with the input (netplay, so both players' machines change disks on the same frame).
        if (Fds is { } drive && state.DiskCommand() is var command and not 0)
        {
            if (command == ControllerStick.EjectDisk)
            {
                drive.Eject();
            }
            else if (command - 1 < drive.SideCount)
            {
                drive.InsertSide(command - 1);
            }
        }
    }

    /// <summary>The active Game Genie and RAM codes (SPEC §7.8).</summary>
    public IReadOnlyList<CheatCode> Cheats => Bus.Cheats?.Codes ?? [];

    /// <summary>Replaces the active codes. Call between frames; an empty list turns codes off.</summary>
    public void SetCheats(IEnumerable<CheatCode> codes)
    {
        var list = codes.ToList();
        Bus.Cheats = list.Count == 0 ? null : new CheatEngine(list);
    }

    /// <summary>Runs until the PPU starts a new frame, then closes the frame's audio.</summary>
    public void RunFrame()
    {
        Bus.Cheats?.ApplyRam(Bus);
        var frame = Ppu.FrameCount;
        while (Ppu.FrameCount == frame)
        {
            Cpu.Step();
        }

        Apu.EndAudioFrame();
    }

    /// <summary>Runs a frame with audio output switched off (run-ahead's hidden frames, SPEC §5.5).</summary>
    public void RunFrameSilently()
    {
        var sink = Apu.AudioSink;
        Apu.AudioSink = null;
        try
        {
            RunFrame();
        }
        finally
        {
            Apu.AudioSink = sink;
        }
    }

    /// <summary>
    /// Writes a complete save state: a header that names the game, then every component (SPEC §4.6).
    /// Pass a reused <paramref name="writer"/> (after <see cref="StateWriter.Reset"/>) to avoid allocating.
    /// </summary>
    public void SaveSnapshot(StateWriter writer)
    {
        writer.BeginChunk(SnapshotTag, 1);
        writer.WriteUInt64(RomHash);
        SaveState(writer);
        writer.EndChunk();
    }

    public byte[] SaveSnapshot()
    {
        var writer = new StateWriter();
        SaveSnapshot(writer);
        return writer.ToArray();
    }

    /// <summary>Restores a snapshot made by <see cref="SaveSnapshot(StateWriter)"/> for this same game.</summary>
    /// <exception cref="StateFormatException">The data is corrupt or belongs to a different game.</exception>
    public void LoadSnapshot(ReadOnlyMemory<byte> data)
    {
        var reader = new StateReader(data);
        reader.BeginChunk(SnapshotTag);
        if (reader.ReadUInt64() != RomHash)
        {
            throw new StateFormatException("This save state belongs to a different game.");
        }

        LoadState(reader);
        reader.EndChunk();
    }

    private static ulong HashRom(INesRom rom, IReadOnlyList<byte[]>? diskTracks)
    {
        var hash = 14695981039346656037UL;
        foreach (var track in diskTracks ?? [])
        {
            foreach (var b in track)
            {
                hash = (hash ^ b) * 1099511628211UL;
            }
        }

        foreach (var b in rom.PrgRom)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        foreach (var b in rom.ChrRom)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>The next instruction in nestest.log format, with the PPU position as of the CPU's clock.</summary>
    public string Trace()
    {
        var scanline = Ppu.Scanline;
        var dot = Ppu.Dot + Bus.PendingPpuDots;
        if (dot >= Ppu2C02.DotsPerScanline)
        {
            dot -= Ppu2C02.DotsPerScanline;
            scanline = (scanline + 1) % Ppu2C02.ScanlinesPerFrame;
        }

        return CpuTracer.Format(Cpu, Bus.Peek, scanline, dot);
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 4);
        Cpu.SaveState(writer);
        Bus.SaveState(writer);
        Ppu.SaveState(writer);
        Apu.SaveState(writer);
        Mapper.SaveState(writer);
        Controller1.SaveState(writer);
        Controller2.SaveState(writer);
        writer.WriteByte((byte)InputSetup);
        Controller3.SaveState(writer);
        Controller4.SaveState(writer);
        Zapper.SaveState(writer);

        // Version 3: the codes that were on, so loading a state or rewinding restores them (SPEC §7.8).
        var cheats = Cheats;
        writer.WriteInt32(cheats.Count);
        foreach (var code in cheats)
        {
            writer.WriteByte((byte)code.Kind);
            writer.WriteUInt16(code.Address);
            writer.WriteByte(code.Value);
            writer.WriteBool(code.Compare.HasValue);
            writer.WriteByte(code.Compare ?? 0);
        }

        // Version 4: the Power Pad and the Arkanoid paddle (SPEC §4.7).
        PowerPad.SaveState(writer);
        Paddle.SaveState(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        var version = reader.BeginChunk(Tag);
        Cpu.LoadState(reader);
        Bus.LoadState(reader);
        Ppu.LoadState(reader);
        Apu.LoadState(reader);
        Mapper.LoadState(reader);
        Controller1.LoadState(reader);
        Controller2.LoadState(reader);
        if (version >= 2)
        {
            var setup = (InputSetup)reader.ReadByte();
            if (setup != InputSetup)
            {
                throw new StateFormatException("This save state was made with different controllers plugged in.");
            }

            Controller3.LoadState(reader);
            Controller4.LoadState(reader);
            Zapper.LoadState(reader);
        }

        var codes = new List<CheatCode>();
        if (version >= 3)
        {
            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var kind = (CheatKind)reader.ReadByte();
                var address = reader.ReadUInt16();
                var value = reader.ReadByte();
                var hasCompare = reader.ReadBool();
                var compare = reader.ReadByte();
                codes.Add(new CheatCode(kind, address, value, hasCompare ? compare : null));
            }
        }

        if (version >= 4)
        {
            PowerPad.LoadState(reader);
            Paddle.LoadState(reader);
        }

        if (!codes.SequenceEqual(Cheats))
        {
            SetCheats(codes);
        }

        reader.EndChunk();
    }
}
