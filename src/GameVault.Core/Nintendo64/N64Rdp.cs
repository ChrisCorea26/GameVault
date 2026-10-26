using GameVault.Core.State;

namespace GameVault.Core.Nintendo64;

/// <summary>Placeholder until the RDP is merged.</summary>
public sealed class N64Rdp : IStateful, IN64DpRegisters
{
    public N64Rdp(IN64RcpHost host)
    {
        _ = host;
    }

    public uint ReadRegister(uint address) => 0;

    public void WriteRegister(uint address, uint value)
    {
    }

    public void SaveState(StateWriter writer)
    {
    }

    public void LoadState(StateReader reader)
    {
    }
}
