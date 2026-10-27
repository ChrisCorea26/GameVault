using GameVault.Core.State;

namespace GameVault.Core.PlayStation;

/// <summary>What is plugged into a controller port besides the DualShock (Game Details → Controls).</summary>
public enum PsPortDevice
{
    DualShock,

    /// <summary>Namco's GunCon (NPC-103): aimed with the PC mouse.</summary>
    GunCon,

    /// <summary>The PlayStation Mouse (SCPH-1090).</summary>
    Mouse,
}

/// <summary>
/// The GunCon (outstanding-work plan §11): ID 63h, then its buttons and the beam's position as the gun latched it — X in
/// dot-clock units (77 to 461 across the picture) and Y in scanlines (25 to 248), or 1 and 10 when it points off the
/// screen (psx-spx: "Controllers - Lightguns - Namco (GunCon)"). Games calibrate the range themselves.
/// </summary>
public sealed class PsGunCon
{
    private const int Trigger = 1 << 13;
    private const int ButtonA = 1 << 3;
    private const int ButtonB = 1 << 14;

    private int _buttons;
    private int _x = 1;
    private int _y = 10;

    /// <summary>
    /// Aims the gun at (<paramref name="x"/>, <paramref name="y"/>) as fractions of the picture (0 to 1), or off the
    /// screen when null, with the trigger and the A and B buttons.
    /// </summary>
    public void Set((double X, double Y)? aim, bool trigger, bool a, bool b)
    {
        _buttons = (trigger ? Trigger : 0) | (a ? ButtonA : 0) | (b ? ButtonB : 0);
        if (aim is { } p && p.X is >= 0 and < 1 && p.Y is >= 0 and < 1)
        {
            _x = 77 + (int)(p.X * 384);
            _y = 25 + (int)(p.Y * 224);
        }
        else
        {
            _x = 1;
            _y = 10;
        }
    }

    public byte Exchange(int step, out bool ack)
    {
        ack = step < 7;
        var buttons = ~_buttons;
        return step switch
        {
            0 => 0x63,
            1 => 0x5A,
            2 => (byte)buttons,
            3 => (byte)(buttons >> 8),
            4 => (byte)_x,
            5 => (byte)(_x >> 8),
            6 => (byte)_y,
            7 => (byte)(_y >> 8),
            _ => 0xFF,
        };
    }

    public void SaveState(StateWriter writer)
    {
        writer.WriteInt32(_buttons);
        writer.WriteInt32(_x);
        writer.WriteInt32(_y);
    }

    public void LoadState(StateReader reader)
    {
        _buttons = reader.ReadInt32();
        _x = reader.ReadInt32() & 0xFFFF;
        _y = reader.ReadInt32() & 0xFFFF;
    }
}

/// <summary>
/// The PlayStation Mouse (outstanding-work plan §11): ID 12h, then its two buttons and how far it moved since it was
/// last read, as signed bytes (psx-spx: "Controllers - Mouse").
/// </summary>
public sealed class PsMouse
{
    private int _dx;
    private int _dy;
    private bool _left;
    private bool _right;

    /// <summary>Adds the frame's movement (in mouse counts) and sets the buttons.</summary>
    public void Move(int dx, int dy, bool left, bool right)
    {
        _dx = Math.Clamp(_dx + dx, -1024, 1024);
        _dy = Math.Clamp(_dy + dy, -1024, 1024);
        _left = left;
        _right = right;
    }

    public byte Exchange(int step, out bool ack)
    {
        ack = step < 5;
        switch (step)
        {
            case 0: return 0x12;
            case 1: return 0x5A;
            case 2: return 0xFF;
            case 3: return (byte)(0xFC & ~(_right ? 0x04 : 0) & ~(_left ? 0x08 : 0));
            case 4:
            {
                var dx = Math.Clamp(_dx, -128, 127);
                _dx -= dx;
                return (byte)(sbyte)dx;
            }

            case 5:
            {
                var dy = Math.Clamp(_dy, -128, 127);
                _dy -= dy;
                return (byte)(sbyte)dy;
            }

            default: return 0xFF;
        }
    }

    public void SaveState(StateWriter writer)
    {
        writer.WriteInt32(_dx);
        writer.WriteInt32(_dy);
        writer.WriteBool(_left);
        writer.WriteBool(_right);
    }

    public void LoadState(StateReader reader)
    {
        _dx = Math.Clamp(reader.ReadInt32(), -1024, 1024);
        _dy = Math.Clamp(reader.ReadInt32(), -1024, 1024);
        _left = reader.ReadBool();
        _right = reader.ReadBool();
    }
}
