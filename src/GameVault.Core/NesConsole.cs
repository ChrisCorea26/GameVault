using GameVault.Core.Apu;
using GameVault.Core.Bus;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu;
using GameVault.Core.Input;
using GameVault.Core.Ppu;
using GameVault.Core.State;

namespace GameVault.Core;

/// <summary>
/// The whole console. Owns every component and is the only entry point the platform layer uses.
/// Deterministic: the same ROM and inputs from power-on always produce the same state.
/// </summary>
public sealed class NesConsole : IStateful
{
    private static readonly StateTag Tag = StateTag.From("NES ");

    public NesConsole(INesRom rom)
    {
        Rom = rom;
        Mapper = MapperFactory.Create(rom);
        Ppu = new Ppu2C02(Mapper);
        Apu = new Apu2A03(pal: rom.TvSystem is TvSystem.Pal or TvSystem.Dendy);
        var clock = rom.TvSystem switch
        {
            TvSystem.Pal => ClockRatio.Pal,
            TvSystem.Dendy => ClockRatio.Dendy,
            _ => ClockRatio.Ntsc,
        };
        Bus = new NesBus(Ppu, Apu, Mapper, clock, Controller1, Controller2);
        Cpu = new Cpu6502(Bus) { DmcSource = Apu };
        Bus.AttachCpu(Cpu);
        Apu.CpuCycle = () => Cpu.CycleCount;
    }

    public INesRom Rom { get; }

    public Cpu6502 Cpu { get; }

    public NesBus Bus { get; }

    public Ppu2C02 Ppu { get; }

    public Apu2A03 Apu { get; }

    public IMapper Mapper { get; }

    public StandardController Controller1 { get; } = new();

    public StandardController Controller2 { get; } = new();

    public static NesConsole FromBytes(ReadOnlySpan<byte> romData) => new(INesRom.Parse(romData));

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
    /// Sets the buttons for port 0 (Player 1) or 1 (Player 2). The platform layer calls this between frames
    /// with the merged state for each port (docs/design/input.md §2).
    /// </summary>
    public void SetControllerState(int port, ControllerState state)
    {
        switch (port)
        {
            case 0:
                Controller1.State = state;
                break;
            case 1:
                Controller2.State = state;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(port), port, "Only ports 0 and 1 exist without a Four Score.");
        }
    }

    /// <summary>Runs until the PPU starts a new frame, then closes the frame's audio.</summary>
    public void RunFrame()
    {
        var frame = Ppu.FrameCount;
        while (Ppu.FrameCount == frame)
        {
            Cpu.Step();
        }

        Apu.EndAudioFrame();
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
        writer.BeginChunk(Tag, 1);
        Cpu.SaveState(writer);
        Bus.SaveState(writer);
        Ppu.SaveState(writer);
        Apu.SaveState(writer);
        Mapper.SaveState(writer);
        Controller1.SaveState(writer);
        Controller2.SaveState(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Cpu.LoadState(reader);
        Bus.LoadState(reader);
        Ppu.LoadState(reader);
        Apu.LoadState(reader);
        Mapper.LoadState(reader);
        Controller1.LoadState(reader);
        Controller2.LoadState(reader);
        reader.EndChunk();
    }
}
