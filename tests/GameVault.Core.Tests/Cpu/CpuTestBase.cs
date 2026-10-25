using GameVault.Core.Cpu;

namespace GameVault.Core.Tests.Cpu;

public abstract class CpuTestBase
{
    protected const ushort Origin = 0x8000;

    protected TestBus Bus { get; } = new();

    protected Cpu6502 Cpu { get; private set; } = null!;

    /// <summary>Loads a program at $8000, powers on (7-cycle reset) and clears the access log.</summary>
    protected void Boot(params byte[] program)
    {
        Bus.Load(Origin, program);
        Cpu = new Cpu6502(Bus);
        Cpu.Power();
        Bus.Log.Clear();
    }

    /// <summary>Executes one instruction and returns how many cycles it took.</summary>
    protected long Step()
    {
        var before = Cpu.CycleCount;
        Bus.Log.Clear();
        Cpu.Step();
        return Cpu.CycleCount - before;
    }

    protected void StepN(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Cpu.Step();
        }
    }
}
