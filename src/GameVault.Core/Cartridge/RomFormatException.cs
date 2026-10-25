namespace GameVault.Core.Cartridge;

/// <summary>Thrown when ROM data is not a valid iNES / NES 2.0 image.</summary>
public sealed class RomFormatException(string message) : Exception(message);
