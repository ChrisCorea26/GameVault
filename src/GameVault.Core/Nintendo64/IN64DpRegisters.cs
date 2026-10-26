namespace GameVault.Core.Nintendo64;

/// <summary>The RDP's command registers (DPC_*), which the CPU and the RSP's COP0 both reach.</summary>
public interface IN64DpRegisters
{
    uint ReadRegister(uint address);

    void WriteRegister(uint address, uint value);
}
