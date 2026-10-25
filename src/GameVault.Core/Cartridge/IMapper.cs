using GameVault.Core.State;

namespace GameVault.Core.Cartridge;

/// <summary>Cartridge hardware: PRG/CHR banking, PRG-RAM, mirroring and IRQs (SPEC §4.5).</summary>
public interface IMapper : IStateful
{
    Mirroring Mirroring { get; }

    /// <summary>True while the mapper holds /IRQ low.</summary>
    bool IrqPending { get; }

    /// <summary>
    /// False for boards with no per-cycle logic or IRQ (NROM, UxROM, ...), so the bus can skip
    /// <see cref="OnCpuCycle"/> and <see cref="IrqPending"/> on every cycle.
    /// </summary>
    bool HasCycleHooks => true;

    /// <summary>CPU read from $4020-$FFFF. Returns <paramref name="openBus"/> for unmapped addresses.</summary>
    byte CpuRead(ushort address, byte openBus);

    /// <summary>Side-effect-free read for debuggers and tracers.</summary>
    byte CpuPeek(ushort address);

    void CpuWrite(ushort address, byte value);

    /// <summary>PPU read from pattern tables ($0000-$1FFF).</summary>
    byte PpuRead(ushort address);

    void PpuWrite(ushort address, byte value);

    /// <summary>Called once per CPU cycle, for mappers with cycle-based IRQ counters.</summary>
    void OnCpuCycle();

    /// <summary>Called on each rising edge of PPU address line A12 (MMC3 scanline counter).</summary>
    void OnA12Rise();

    /// <summary>Called on each falling edge of PPU A12 (lets MMC3 filter out short low periods).</summary>
    void OnA12Fall()
    {
    }
}
