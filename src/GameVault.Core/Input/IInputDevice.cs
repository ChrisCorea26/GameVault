using GameVault.Core.State;

namespace GameVault.Core.Input;

/// <summary>Something plugged into a controller port: a standard controller, a Zapper, or half a Four Score.</summary>
public interface IInputDevice : IStateful
{
    /// <summary>A write to $4016 (bit 0 = strobe, shared by both ports).</summary>
    void Write(byte value);

    /// <summary>A read of this port's register; bits 0-4 are driven by the device, the rest is open bus.</summary>
    byte Read();
}
