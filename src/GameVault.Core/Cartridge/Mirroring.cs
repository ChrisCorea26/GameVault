namespace GameVault.Core.Cartridge;

/// <summary>How the PPU's two physical nametables map onto its four logical ones.</summary>
public enum Mirroring : byte
{
    Horizontal,
    Vertical,
    SingleScreenA,
    SingleScreenB,
    FourScreen,
}
