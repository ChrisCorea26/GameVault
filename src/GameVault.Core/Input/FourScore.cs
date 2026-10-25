using GameVault.Core.State;

namespace GameVault.Core.Input;

/// <summary>
/// One port of the Four Score adapter (NESdev Wiki: "Four Score"). After a strobe, reads return 8 bits
/// for the first controller, 8 for the second (players 3 or 4), then an 8-bit signature that tells games the
/// adapter is present, then 1s. Port 1 carries players 1 and 3; port 2 carries players 2 and 4.
/// </summary>
public sealed class FourScorePort(StandardController first, StandardController second, int port) : IInputDevice
{
    private static readonly StateTag Tag = StateTag.From("4SCR");

    // Signature bits, in read order: $4016 reads 0,0,0,1,0,0,0,0 and $4017 reads 0,0,1,0,0,0,0,0.
    private readonly uint _signature = port == 0 ? 0x08u : 0x04u;
    private bool _strobe;
    private uint _shift;
    private int _count;

    public void Write(byte value)
    {
        _strobe = (value & 1) != 0;
        if (_strobe)
        {
            Latch();
        }
    }

    public byte Read()
    {
        if (_strobe)
        {
            Latch();
            return (byte)(_shift & 1);
        }

        if (_count >= 24)
        {
            return 1;
        }

        var bit = (byte)(_shift & 1);
        _shift >>= 1;
        _count++;
        return bit;
    }

    private void Latch()
    {
        _shift = (byte)first.State | ((uint)(byte)second.State << 8) | (_signature << 16);
        _count = 0;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBool(_strobe);
        writer.WriteUInt32(_shift);
        writer.WriteInt32(_count);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        _strobe = reader.ReadBool();
        _shift = reader.ReadUInt32();
        _count = reader.ReadInt32();
        reader.EndChunk();
    }
}
