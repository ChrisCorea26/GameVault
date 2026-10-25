using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// The IRQ counter shared by Konami's VRC4, VRC6 and VRC7 (NESdev Wiki: "VRC IRQ"). In scanline mode a
/// prescaler divides CPU cycles by 113.667 (341 / 3) so the 8-bit counter ticks once per scanline; in cycle
/// mode it ticks every CPU cycle. When it overflows from $FF it reloads from the latch and raises /IRQ.
/// </summary>
public sealed class VrcIrq
{
    private int _prescaler = 341;

    public byte Latch { get; set; }

    public bool Pending { get; private set; }

    private byte _counter;
    private bool _enabled;
    private bool _enableAfterAck;
    private bool _cycleMode;

    /// <summary>$F001-style control: bit 0 = enable after acknowledge, bit 1 = enable, bit 2 = cycle mode.</summary>
    public void WriteControl(byte value)
    {
        _enableAfterAck = (value & 1) != 0;
        _enabled = (value & 2) != 0;
        _cycleMode = (value & 4) != 0;
        Pending = false;
        if (_enabled)
        {
            _counter = Latch;
            _prescaler = 341;
        }
    }

    public void Acknowledge()
    {
        Pending = false;
        _enabled = _enableAfterAck;
    }

    public void Clock()
    {
        if (!_enabled)
        {
            return;
        }

        if (!_cycleMode)
        {
            _prescaler -= 3;
            if (_prescaler > 0)
            {
                return;
            }

            _prescaler += 341;
        }

        if (_counter == 0xFF)
        {
            _counter = Latch;
            Pending = true;
        }
        else
        {
            _counter++;
        }
    }

    public void Save(StateWriter writer)
    {
        writer.WriteInt32(_prescaler);
        writer.WriteByte(Latch);
        writer.WriteBool(Pending);
        writer.WriteByte(_counter);
        writer.WriteBool(_enabled);
        writer.WriteBool(_enableAfterAck);
        writer.WriteBool(_cycleMode);
    }

    public void Load(StateReader reader)
    {
        _prescaler = reader.ReadInt32();
        Latch = reader.ReadByte();
        Pending = reader.ReadBool();
        _counter = reader.ReadByte();
        _enabled = reader.ReadBool();
        _enableAfterAck = reader.ReadBool();
        _cycleMode = reader.ReadBool();
    }
}
