namespace GameVault.Library.Models;

public enum GameRegion
{
    Unknown,
    NorthAmerica,
    Europe,
    Japan,
    World,
}

public enum PlayStatus
{
    Unplayed,
    Playing,
    Beaten,
    Completed,
}

/// <summary>What the user owns for a game. Each part is a separate toggle (SPEC §6.3).</summary>
[Flags]
public enum Ownership
{
    None = 0,
    Cartridge = 1,
    Box = 2,
    Manual = 4,
    DigitalDump = 8,
}
