namespace GameVault.Core.Cpu;

/// <summary>The APU's DMC memory reader, which the CPU serves by DMA (NESdev Wiki: "DMA").</summary>
public interface IDmcDmaSource
{
    ushort DmcDmaAddress { get; }

    void OnDmcDmaRead(byte value);
}
