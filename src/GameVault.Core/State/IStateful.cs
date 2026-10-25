namespace GameVault.Core.State;

/// <summary>
/// Implemented by every emulated component (CPU, PPU, APU, mappers, controllers, ...).
/// Save and load must be symmetric and fast (&lt; 1 ms for a whole console, SPEC §4.6),
/// because run-ahead and rewind call them every frame.
/// </summary>
public interface IStateful
{
    void SaveState(StateWriter writer);

    void LoadState(StateReader reader);
}
