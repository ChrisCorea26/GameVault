using GameVault.Core.State;

namespace GameVault.Core.Atari;

/// <summary>
/// The 6532 RIOT (phase-19.md): 128 bytes of RAM, port A for the joysticks (SWCHA, with its direction register), port B
/// for the console switches (SWCHB), and the interval timer.
/// </summary>
/// <remarks>
/// The timer counts down once every 1, 8, 64 or 1024 cycles (TIM1T–T1024T); after it passes zero it sets its flag and
/// counts down every cycle, as games' waits expect. Reading INTIM clears the flag. Reference: the Stella Programmer's
/// Guide, and the 6532 data sheet.
/// </remarks>
public sealed class Riot : IStateful
{
    private static readonly StateTag Tag = StateTag.From("RIOT");
    private static readonly int[] Intervals = [1, 8, 64, 1024];

    private readonly byte[] _ram = new byte[128];
    private byte _portAOut;
    private byte _portADirection;
    private byte _portBOut;
    private byte _portBDirection;
    private byte _timer;
    private int _interval = 1024;
    private int _prescaler = 1024;
    private bool _flag;

    public Span<byte> Ram => _ram;

    /// <summary>The joystick lines (SWCHA as the joysticks drive it, low when pushed): player 1 in bits 4–7, player 2 in 0–3.</summary>
    public byte Joysticks { get; set; } = 0xFF;

    /// <summary>The console switches (SWCHB): bit 0 Reset and bit 1 Select (low when pressed), bit 3 colour, bits 6 and 7 the difficulties.</summary>
    public byte Switches { get; set; } = 0x0B;

    public void Power()
    {
        Array.Clear(_ram);
        _portAOut = _portADirection = _portBOut = _portBDirection = 0;
        _timer = 0;
        _interval = _prescaler = 1024;
        _flag = false;
    }

    /// <summary>One CPU cycle.</summary>
    public void Tick()
    {
        if (--_prescaler > 0)
        {
            return;
        }

        _prescaler = _flag ? 1 : _interval;
        if (_timer-- == 0)
        {
            _flag = true; // passed zero: count every cycle from now on
            _prescaler = 1;
        }
    }

    /// <summary>A read in $80–$FF (RAM) or $280–$29F (ports and timer).</summary>
    public byte Read(int address)
    {
        if ((address & 0x200) == 0)
        {
            return _ram[address & 0x7F];
        }

        if ((address & 0x04) == 0)
        {
            return (address & 3) switch
            {
                0 => (byte)((Joysticks & ~_portADirection) | (_portAOut & _portADirection)),
                1 => _portADirection,
                2 => (byte)((Switches & ~_portBDirection) | (_portBOut & _portBDirection)),
                _ => _portBDirection,
            };
        }

        if ((address & 0x01) == 0)
        {
            _flag = false; // INTIM: reading it clears the flag
            return _timer;
        }

        return (byte)(_flag ? 0x80 : 0x00); // TIMINT
    }

    public void Write(int address, byte value)
    {
        if ((address & 0x200) == 0)
        {
            _ram[address & 0x7F] = value;
            return;
        }

        if ((address & 0x04) == 0)
        {
            switch (address & 3)
            {
                case 0: _portAOut = value; break;
                case 1: _portADirection = value; break;
                case 2: _portBOut = value; break;
                default: _portBDirection = value; break;
            }

            return;
        }

        if ((address & 0x10) != 0)
        {
            // TIM1T, TIM8T, TIM64T, T1024T: load the timer and its interval.
            _timer = value;
            _interval = Intervals[address & 3];
            _prescaler = _interval;
            _flag = false;
        }
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_ram);
        writer.WriteByte(_portAOut);
        writer.WriteByte(_portADirection);
        writer.WriteByte(_portBOut);
        writer.WriteByte(_portBDirection);
        writer.WriteByte(_timer);
        writer.WriteInt32(_interval);
        writer.WriteInt32(_prescaler);
        writer.WriteBool(_flag);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_ram);
        _portAOut = reader.ReadByte();
        _portADirection = reader.ReadByte();
        _portBOut = reader.ReadByte();
        _portBDirection = reader.ReadByte();
        _timer = reader.ReadByte();
        var interval = reader.ReadInt32();
        _interval = Array.IndexOf(Intervals, interval) >= 0 ? interval : 1024;
        _prescaler = Math.Clamp(reader.ReadInt32(), 1, 1024);
        _flag = reader.ReadBool();
        reader.EndChunk();
    }
}
