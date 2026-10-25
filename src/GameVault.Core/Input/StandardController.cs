using GameVault.Core.State;

namespace GameVault.Core.Input;

/// <summary>
/// The standard NES controller: a 4021 shift register. While strobe is high it keeps reloading the
/// buttons; after strobe goes low, each read shifts out one button, then 1s (NESdev Wiki: "Standard controller").
/// </summary>
public sealed class StandardController : IStateful
{
    private static readonly StateTag Tag = StateTag.From("JOY ");

    private bool _strobe;
    private byte _shift;

    /// <summary>The buttons currently held, set by the platform layer between frames.</summary>
    public ControllerState State { get; set; }

    /// <summary>A write to $4016 (bit 0 = strobe).</summary>
    public void Write(byte value)
    {
        _strobe = (value & 1) != 0;
        if (_strobe)
        {
            _shift = (byte)State;
        }
    }

    /// <summary>Returns the next button in bit 0.</summary>
    public byte Read()
    {
        if (_strobe)
        {
            return (byte)((byte)State & 1);
        }

        var bit = (byte)(_shift & 1);
        _shift = (byte)((_shift >> 1) | 0x80); // official controllers shift in 1s
        return bit;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteByte((byte)State);
        writer.WriteBool(_strobe);
        writer.WriteByte(_shift);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        State = (ControllerState)reader.ReadByte();
        _strobe = reader.ReadBool();
        _shift = reader.ReadByte();
        reader.EndChunk();
    }
}
