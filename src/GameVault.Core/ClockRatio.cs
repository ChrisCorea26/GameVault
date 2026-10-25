namespace GameVault.Core;

/// <summary>
/// CPU and PPU clock dividers from the master clock (NESdev Wiki: "Cycle reference chart").
/// NTSC gives exactly 3 PPU dots per CPU cycle; PAL gives 3.2, so the bus schedules by master clock.
/// </summary>
public readonly record struct ClockRatio(int CpuDivider, int PpuDivider)
{
    public static ClockRatio Ntsc { get; } = new(12, 4);

    public static ClockRatio Pal { get; } = new(16, 5);

    public static ClockRatio Dendy { get; } = new(15, 5);
}
