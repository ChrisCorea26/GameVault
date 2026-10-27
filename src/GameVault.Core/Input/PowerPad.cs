using GameVault.Core.State;

namespace GameVault.Core.Input;

/// <summary>
/// The Power Pad (Family Trainer) mat on port 2 (NESdev Wiki: "Power Pad"). It holds two shift registers loaded while
/// strobe is high: reads return one bit of each, D3 from buttons 2, 1, 5, 9, 6, 10, 11, 7 and D4 from buttons 4, 3,
/// 12, 8 followed by four 1s; a pressed button reads as 1. After eight reads both lines read 1.
/// </summary>
/// <remarks>
/// Buttons are numbered as printed on side B of the mat, 1–12 in rows of four (side A's games read the same
/// positions). <see cref="Buttons"/> holds button N in bit N - 1 and is set between frames, so runs stay deterministic.
/// </remarks>
public sealed class PowerPad : IInputDevice
{
    private static readonly StateTag Tag = StateTag.From("PPAD");

    private bool _strobe;
    private byte _low;
    private byte _high;

    /// <summary>The buttons held: bit N - 1 for button N (1–12).</summary>
    public ushort Buttons { get; set; }

    public void Write(byte value)
    {
        _strobe = (value & 1) != 0;
        if (_strobe)
        {
            Reload();
        }
    }

    public byte Read()
    {
        if (_strobe)
        {
            Reload();
        }

        var value = (byte)(((_low & 1) << 3) | ((_high & 1) << 4));
        if (!_strobe)
        {
            _low = (byte)((_low >> 1) | 0x80);
            _high = (byte)((_high >> 1) | 0x80);
        }

        return value;
    }

    private void Reload()
    {
        int Bit(int button) => (Buttons >> (button - 1)) & 1;
        _low = (byte)(Bit(2) | (Bit(1) << 1) | (Bit(5) << 2) | (Bit(9) << 3) | (Bit(6) << 4) | (Bit(10) << 5) | (Bit(11) << 6) | (Bit(7) << 7));
        _high = (byte)(Bit(4) | (Bit(3) << 1) | (Bit(12) << 2) | (Bit(8) << 3) | 0xF0);
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteUInt16(Buttons);
        writer.WriteBool(_strobe);
        writer.WriteByte(_low);
        writer.WriteByte(_high);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Buttons = reader.ReadUInt16();
        _strobe = reader.ReadBool();
        _low = reader.ReadByte();
        _high = reader.ReadByte();
        reader.EndChunk();
    }
}
