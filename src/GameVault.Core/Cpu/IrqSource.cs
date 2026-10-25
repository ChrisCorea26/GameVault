namespace GameVault.Core.Cpu;

/// <summary>Devices that can hold the shared, level-triggered /IRQ line low.</summary>
[Flags]
public enum IrqSource : byte
{
    None = 0,
    External = 1,
    FrameCounter = 2,
    Dmc = 4,
    Mapper = 8,
}
