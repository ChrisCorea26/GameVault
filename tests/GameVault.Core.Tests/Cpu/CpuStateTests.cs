using GameVault.Core.Cpu;
using GameVault.Core.Tests.State;

namespace GameVault.Core.Tests.Cpu;

public class CpuStateTests
{
    [Fact]
    public void Cpu_state_round_trips_including_interrupt_latches()
    {
        StateAssert.RoundTrips(
            () => new Cpu6502(new TestBus()),
            cpu =>
            {
                cpu.Power();
                cpu.A = 1;
                cpu.X = 2;
                cpu.Y = 3;
                cpu.SP = 0x80;
                cpu.PC = 0x1234;
                cpu.Status = 0xC3;
                cpu.NmiLine = true;
                cpu.SetIrq(IrqSource.Mapper | IrqSource.Dmc);
                cpu.RequestOamDma(0x02);
                cpu.Step();
            });
    }
}
