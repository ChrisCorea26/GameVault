namespace GameVault.Core.State;

/// <summary>Thrown when save-state data is truncated, corrupt, or from an incompatible layout.</summary>
public sealed class StateFormatException(string message) : Exception(message);
