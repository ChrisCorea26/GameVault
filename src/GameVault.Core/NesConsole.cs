using GameVault.Core.Bus;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu;
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
        Ppu = new PpuTimingStub();
        var clock = rom.TvSystem switch
        {
            TvSystem.Pal => ClockRatio.Pal,
            TvSystem.Dendy => ClockRatio.Dendy,
            _ => ClockRatio.Ntsc,
        };
        Bus = new NesBus(Ppu, Mapper, clock);
        Cpu = new Cpu6502(Bus);
        Bus.AttachCpu(Cpu);
    }

    public INesRom Rom { get; }

    public Cpu6502 Cpu { get; }

    public NesBus Bus { get; }

    public PpuTimingStub Ppu { get; }

    public IMapper Mapper { get; }

    public static NesConsole FromBytes(ReadOnlySpan<byte> romData) => new(INesRom.Parse(romData));

    public void Power()
    {
        Bus.Power();
        Ppu.Power();
        Cpu.Power();
    }

    /// <summary>The console's reset button.</summary>
    public void Reset() => Cpu.Reset();

    public void StepInstruction() => Cpu.Step();

    /// <summary>Runs until the PPU starts a new frame.</summary>
    public void RunFrame()
    {
        var frame = Ppu.FrameCount;
        while (Ppu.FrameCount == frame)
        {
            Cpu.Step();
        }
    }

    /// <summary>The next instruction in nestest.log format.</summary>
    public string Trace() => CpuTracer.Format(Cpu, Bus.Peek, Ppu.Scanline, Ppu.Dot);

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        Cpu.SaveState(writer);
        Bus.SaveState(writer);
        Ppu.SaveState(writer);
        Mapper.SaveState(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Cpu.LoadState(reader);
        Bus.LoadState(reader);
        Ppu.LoadState(reader);
        Mapper.LoadState(reader);
        reader.EndChunk();
    }
}
