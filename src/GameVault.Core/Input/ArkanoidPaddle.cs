using GameVault.Core.State;

namespace GameVault.Core.Input;

/// <summary>
/// The Arkanoid "Vaus" paddle on port 2 (NESdev Wiki: "Arkanoid controller"). Strobing latches the knob's 8-bit
/// position into a shift register; each read returns its next bit, most significant first and inverted, on D4, and the
/// fire button on D3 (1 while pressed).
/// </summary>
/// <remarks>
/// The NES paddle's knob reads from about 98 to 242 across its travel (NESdev Wiki), so <see cref="Position"/> is kept in
/// that range. It is set between frames, so runs stay deterministic.
/// </remarks>
public sealed class ArkanoidPaddle : IInputDevice
{
    private static readonly StateTag Tag = StateTag.From("VAUS");

    /// <summary>The knob turned all the way left.</summary>
    public const byte MinPosition = 98;

    /// <summary>The knob turned all the way right.</summary>
    public const byte MaxPosition = 242;

    private bool _strobe;
    private byte _shift;
    private byte _position = (MinPosition + MaxPosition) / 2;

    /// <summary>The knob, from <see cref="MinPosition"/> (left) to <see cref="MaxPosition"/> (right).</summary>
    public byte Position
    {
        get => _position;
        set => _position = Math.Clamp(value, MinPosition, MaxPosition);
    }

    public bool Fire { get; set; }

    /// <summary>Sets the knob from a fraction of its travel: 0 is all the way left, 1 all the way right.</summary>
    public void SetFraction(double fraction) =>
        Position = (byte)Math.Round(MinPosition + Math.Clamp(fraction, 0, 1) * (MaxPosition - MinPosition));

    public void Write(byte value)
    {
        _strobe = (value & 1) != 0;
        if (_strobe)
        {
            _shift = _position;
        }
    }

    public byte Read()
    {
        if (_strobe)
        {
            _shift = _position;
        }

        var value = (byte)((((~_shift) >> 7) & 1) << 4 | (Fire ? 0x08 : 0));
        if (!_strobe)
        {
            _shift <<= 1;
        }

        return value;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteByte(_position);
        writer.WriteBool(Fire);
        writer.WriteBool(_strobe);
        writer.WriteByte(_shift);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Position = reader.ReadByte();
        Fire = reader.ReadBool();
        _strobe = reader.ReadBool();
        _shift = reader.ReadByte();
        reader.EndChunk();
    }
}
