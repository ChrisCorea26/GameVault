using GameVault.Core.State;

namespace GameVault.Core.GameCube;

/// <summary>A GameCube controller's state as the serial interface reports it.</summary>
public struct GcPadState
{
    // Button bits as the controller sends them.
    public const ushort Left = 0x0001, Right = 0x0002, Down = 0x0004, Up = 0x0008, Z = 0x0010, R = 0x0020, L = 0x0040,
        A = 0x0100, B = 0x0200, X = 0x0400, Y = 0x0800, Start = 0x1000;

    public ushort Buttons;
    public byte StickX, StickY, CStickX, CStickY, TriggerL, TriggerR;

    public static GcPadState Centred => new() { StickX = 0x80, StickY = 0x80, CStickX = 0x80, CStickY = 0x80 };
}

/// <summary>
/// The GameCube's serial interface (phase-18.md, milestone 1) at $0C006400: four ports with standard controllers, the
/// command transfers through its 128-byte buffer (identify, origin, recalibrate, poll), the polled input buffers and
/// the transfer-complete interrupt.
/// </summary>
/// <remarks>
/// Per port: OUTBUF at +0, INBUFH at +4, INBUFL at +8 (twelve bytes a port); then POLL $30, COMCSR $34, SR $38, EXILK
/// $3C, and the buffer at $80. A polled controller's INBUFH is its buttons, then the stick's X and Y; INBUFL the
/// C-stick and the analog triggers (poll mode 3). Reference: YAGCD, "Serial Interface"; Dolphin's SI documentation.
/// </remarks>
public sealed class GcSerialInterface : IStateful
{
    private static readonly StateTag Tag = StateTag.From("GCSI");

    private readonly uint[] _outbuf = new uint[4];
    private readonly byte[] _buffer = new byte[0x80];
    private uint _poll;
    private uint _comcsr;
    private uint _sr;
    private uint _exiLock;

    /// <summary>Each port's controller (null for none); the console fills them in each frame.</summary>
    public GcPadState?[] Pads { get; } = [GcPadState.Centred, null, null, null];

    public bool Interrupt => (_comcsr & 0xC0000000) == 0xC0000000 || (_comcsr & 0x18000000) == 0x18000000;

    public void Reset()
    {
        Array.Clear(_outbuf);
        Array.Clear(_buffer);
        _poll = _comcsr = _sr = _exiLock = 0;
    }

    private static uint High(GcPadState pad) => ((uint)pad.Buttons << 16) | ((uint)pad.StickX << 8) | pad.StickY;

    private static uint Low(GcPadState pad) => ((uint)pad.CStickX << 24) | ((uint)pad.CStickY << 16) | ((uint)pad.TriggerL << 8) | pad.TriggerR;

    public uint Read32(int offset)
    {
        offset &= 0xFF;
        if (offset < 0x30)
        {
            var port = offset / 12;
            var pad = Pads[port];
            switch (offset % 12)
            {
                case 0: return _outbuf[port];
                case 4: return pad is { } p ? High(p) : 0x80000000; // ERRSTAT: nothing there
                default: return pad is { } q ? Low(q) : 0;
            }
        }

        return offset switch
        {
            0x30 => _poll,
            0x34 => _comcsr,
            0x38 => _sr,
            0x3C => _exiLock,
            >= 0x80 => (uint)((_buffer[offset - 0x80] << 24) | (_buffer[offset - 0x7F] << 16) | (_buffer[offset - 0x7E] << 8) | _buffer[offset - 0x7D]),
            _ => 0,
        };
    }

    public void Write32(int offset, uint value)
    {
        offset &= 0xFF;
        if (offset < 0x30)
        {
            if (offset % 12 == 0)
            {
                _outbuf[offset / 12] = value;
            }

            return;
        }

        switch (offset)
        {
            case 0x30:
                _poll = value;
                break;
            case 0x34:
                // TCINT and RDSTINT clear by writing 1; the masks and the transfer's fields are written as they are.
                var clear = value & 0x90000000;
                _comcsr = (_comcsr & ~clear & 0x90000000) | (value & 0x6F7F7F07);
                if ((value & 1) != 0)
                {
                    Transfer();
                }

                break;
            case 0x38:
                _sr &= ~(value & 0x0F0F0F0F); // error bits clear by writing 1
                break;
            case 0x3C:
                _exiLock = value;
                break;
            case >= 0x80:
                _buffer[offset - 0x80] = (byte)(value >> 24);
                _buffer[offset - 0x7F] = (byte)(value >> 16);
                _buffer[offset - 0x7E] = (byte)(value >> 8);
                _buffer[offset - 0x7D] = (byte)value;
                break;
        }
    }

    /// <summary>A command transfer: the command in the buffer's first byte, the controller's answer back into the buffer.</summary>
    private void Transfer()
    {
        var channel = (int)((_comcsr >> 25) & 3);
        var pad = Pads[channel];
        _comcsr &= ~0x20000001u; // TSTART done, COMERR clear
        if (pad is not { } p)
        {
            _comcsr |= 0x20000000; // COMERR: nobody answered
            _sr |= 0x08u << ((3 - channel) * 8); // NOREP
        }
        else
        {
            switch (_buffer[0])
            {
                case 0x00: // identify: a standard controller
                case 0xFF:
                    _buffer[0] = 0x09;
                    _buffer[1] = 0x00;
                    _buffer[2] = 0x00;
                    break;
                case 0x41: // origin
                case 0x42: // recalibrate
                {
                    var origin = GcPadState.Centred;
                    var high = High(origin);
                    var low = Low(origin);
                    _buffer[0] = (byte)(high >> 24);
                    _buffer[1] = (byte)(high >> 16);
                    _buffer[2] = (byte)(high >> 8);
                    _buffer[3] = (byte)high;
                    _buffer[4] = (byte)(low >> 24);
                    _buffer[5] = (byte)(low >> 16);
                    _buffer[6] = (byte)(low >> 8);
                    _buffer[7] = (byte)low;
                    _buffer[8] = _buffer[9] = 0;
                    break;
                }

                case 0x40: // poll
                {
                    var high = High(p);
                    var low = Low(p);
                    for (var i = 0; i < 4; i++)
                    {
                        _buffer[i] = (byte)(high >> (24 - i * 8));
                        _buffer[4 + i] = (byte)(low >> (24 - i * 8));
                    }

                    break;
                }
            }
        }

        _comcsr |= 0x80000000; // TCINT
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var o in _outbuf)
        {
            writer.WriteUInt32(o);
        }

        writer.WriteBytes(_buffer);
        writer.WriteUInt32(_poll);
        writer.WriteUInt32(_comcsr);
        writer.WriteUInt32(_sr);
        writer.WriteUInt32(_exiLock);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < 4; i++)
        {
            _outbuf[i] = reader.ReadUInt32();
        }

        reader.ReadBytes(_buffer);
        _poll = reader.ReadUInt32();
        _comcsr = reader.ReadUInt32();
        _sr = reader.ReadUInt32();
        _exiLock = reader.ReadUInt32();
        reader.EndChunk();
    }
}
