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

    /// <summary>
    /// Which CIRAM page (0-1, or 0-3 with four-screen RAM) logical nametable <paramref name="slot"/> (0-3,
    /// for $2000/$2400/$2800/$2C00) uses. The default follows <see cref="Mirroring"/>; boards such as
    /// TxSROM route each slot themselves (NESdev Wiki: "Mirroring").
    /// </summary>
    int NametablePage(int slot) => NametableRouting.Page(Mirroring, slot);

    /// <summary>True for boards that supply nametable data themselves (MMC5 ExRAM and fill mode, Namco 163).</summary>
    bool HasNametableHooks => false;

    /// <summary>A PPU read of $2000-$3EFF when <see cref="HasNametableHooks"/>; <paramref name="ciram"/> is the console's 2 KB.</summary>
    byte ReadNametable(ushort address, byte[] ciram) => ciram[address & 0x7FF];

    void WriteNametable(ushort address, byte value, byte[] ciram) => ciram[address & 0x7FF] = value;

    /// <summary>True for boards that watch writes to the PPU registers (MMC5 snoops $2000 and $2001).</summary>
    bool SnoopsPpuRegisters => false;

    /// <summary>A CPU write to $2000-$3FFF (mirrored down to $2000-$2007), after the PPU has seen it.</summary>
    void OnPpuRegisterWrite(ushort register, byte value)
    {
    }

    /// <summary>The cartridge's sound chip, if it has one (VRC6, Sunsoft 5B, Namco 163, MMC5; SPEC §4.4).</summary>
    Audio.IExpansionAudio? ExpansionAudio => null;

    /// <summary>Called on each falling edge of PPU A12 (lets MMC3 filter out short low periods).</summary>
    void OnA12Fall()
    {
    }
}

/// <summary>The standard nametable layouts as CIRAM pages per slot (NESdev Wiki: "Mirroring").</summary>
public static class NametableRouting
{
    public static int Page(Mirroring mirroring, int slot) => mirroring switch
    {
        Mirroring.Vertical => slot & 1,
        Mirroring.Horizontal => slot >> 1,
        Mirroring.SingleScreenA => 0,
        Mirroring.SingleScreenB => 1,
        _ => slot,
    };
}
