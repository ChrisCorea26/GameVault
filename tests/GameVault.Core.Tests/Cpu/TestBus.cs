using GameVault.Core.Cpu;

namespace GameVault.Core.Tests.Cpu;

/// <summary>A flat 64 KB RAM bus that records every access, for cycle-level CPU tests.</summary>
public sealed class TestBus : ICpuBus
{
    public byte[] Memory { get; } = new byte[0x10000];

    public List<BusAccess> Log { get; } = [];

    /// <summary>Called before each access, so tests can raise interrupt lines on a specific cycle.</summary>
    public Action<int>? BeforeAccess { get; set; }

    /// <summary>Called after each write, like a device register reacting to it.</summary>
    public Action<ushort, byte>? AfterWrite { get; set; }

    public byte Read(ushort address)
    {
        BeforeAccess?.Invoke(Log.Count);
        var value = Memory[address];
        Log.Add(new BusAccess(address, value, IsWrite: false));
        return value;
    }

    public void Write(ushort address, byte value)
    {
        BeforeAccess?.Invoke(Log.Count);
        Memory[address] = value;
        Log.Add(new BusAccess(address, value, IsWrite: true));
        AfterWrite?.Invoke(address, value);
    }

    /// <summary>Places a program at <paramref name="origin"/> and points the reset vector at it.</summary>
    public void Load(ushort origin, params byte[] program)
    {
        program.CopyTo(Memory, origin);
        Memory[0xFFFC] = (byte)origin;
        Memory[0xFFFD] = (byte)(origin >> 8);
    }

    public void SetVector(ushort vector, ushort target)
    {
        Memory[vector] = (byte)target;
        Memory[vector + 1] = (byte)(target >> 8);
    }
}

public readonly record struct BusAccess(ushort Address, byte Value, bool IsWrite)
{
    public override string ToString() => $"{(IsWrite ? "W" : "R")} {Address:X4}={Value:X2}";
}
