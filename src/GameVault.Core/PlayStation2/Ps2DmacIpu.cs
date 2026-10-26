namespace GameVault.Core.PlayStation2;

/// <summary>
/// The IPU's two DMA channels as the DMAC drives them (ps2tek: "DMAC I/O"): IPU_TO (channel 4) fills the IPU's input
/// FIFO, IPU_FROM (channel 3) drains its output FIFO. Set on <see cref="Ps2Dmac.Ipu"/>; without one, both channels wait.
/// </summary>
public interface IPs2DmaIpu
{
    /// <summary>IPU_TO: quadwords into the input FIFO; returns how many the IPU took (fewer when its FIFO is full).</summary>
    int WriteIn(ReadOnlySpan<UInt128> data);

    /// <summary>IPU_FROM: quadwords out of the output FIFO; returns how many the IPU had ready.</summary>
    int ReadOut(Span<UInt128> destination);
}
