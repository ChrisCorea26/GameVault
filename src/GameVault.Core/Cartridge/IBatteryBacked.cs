namespace GameVault.Core.Cartridge;

/// <summary>
/// A cartridge with non-volatile memory (battery-backed PRG-RAM). The platform loads the bytes before power-on
/// and writes them to a <c>.sav</c> file; the core never touches files (SPEC §4.5).
/// </summary>
public interface IBatteryBacked
{
    /// <summary>True when the header says the board has a battery.</summary>
    bool HasBattery { get; }

    /// <summary>The memory to persist. Writes by the platform go straight into the cartridge.</summary>
    Span<byte> BatteryRam { get; }
}
