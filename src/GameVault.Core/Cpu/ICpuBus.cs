namespace GameVault.Core.Cpu;

/// <summary>
/// The CPU's view of the address space. Each call is one CPU cycle: the bus advances the rest of the
/// system (PPU, mappers) by one CPU cycle before it performs the access.
/// </summary>
public interface ICpuBus
{
    byte Read(ushort address);

    void Write(ushort address, byte value);
}
