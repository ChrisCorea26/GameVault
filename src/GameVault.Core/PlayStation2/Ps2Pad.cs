using GameVault.Core.Input;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// What a DualShock 2 reports: its sixteen buttons as the pad sends them (ps2tek: "SIO2 Command Sequence"; bit set =
/// pressed here, the pad inverts them on the wire) and both sticks as signed values (up and right positive).
/// </summary>
/// <remarks>
/// GameVault's <see cref="ControllerState"/> carries the buttons and the left stick; the right stick and L3/R3 come
/// separately until it grows to 64 bits (Phase 17's input step), so this struct is the one place to plug them in.
/// </remarks>
public readonly record struct Ps2PadInput(ushort Buttons, sbyte LeftX, sbyte LeftY, sbyte RightX, sbyte RightY)
{
    public const ushort Select = 1 << 0;
    public const ushort L3 = 1 << 1;
    public const ushort R3 = 1 << 2;
    public const ushort Start = 1 << 3;
    public const ushort Up = 1 << 4;
    public const ushort Right = 1 << 5;
    public const ushort Down = 1 << 6;
    public const ushort Left = 1 << 7;
    public const ushort L2 = 1 << 8;
    public const ushort R2 = 1 << 9;
    public const ushort L1 = 1 << 10;
    public const ushort R1 = 1 << 11;
    public const ushort Triangle = 1 << 12;
    public const ushort Circle = 1 << 13;
    public const ushort Cross = 1 << 14;
    public const ushort Square = 1 << 15;

    /// <summary>
    /// The buttons and left stick of <paramref name="state"/>, mapped by position as the PlayStation maps them
    /// (B = Cross, A = Circle, Y = Square, X = Triangle, L/R = L1/R1), with the right stick and L3/R3 given separately.
    /// </summary>
    public static Ps2PadInput From(ControllerState state, sbyte rightX = 0, sbyte rightY = 0, bool l3 = false, bool r3 = false)
    {
        var bits = 0;
        bits |= state.HasFlag(ControllerState.Select) ? Select : 0;
        bits |= l3 ? L3 : 0;
        bits |= r3 ? R3 : 0;
        bits |= state.HasFlag(ControllerState.Start) ? Start : 0;
        bits |= state.HasFlag(ControllerState.Up) ? Up : 0;
        bits |= state.HasFlag(ControllerState.Right) ? Right : 0;
        bits |= state.HasFlag(ControllerState.Down) ? Down : 0;
        bits |= state.HasFlag(ControllerState.Left) ? Left : 0;
        bits |= state.HasFlag(ControllerState.L2) ? L2 : 0;
        bits |= state.HasFlag(ControllerState.R2) ? R2 : 0;
        bits |= state.HasFlag(ControllerState.L) ? L1 : 0;
        bits |= state.HasFlag(ControllerState.R) ? R1 : 0;
        bits |= state.HasFlag(ControllerState.X) ? Triangle : 0; // north
        bits |= state.HasFlag(ControllerState.A) ? Circle : 0; // east
        bits |= state.HasFlag(ControllerState.B) ? Cross : 0; // south
        bits |= state.HasFlag(ControllerState.Y) ? Square : 0; // west
        return new Ps2PadInput((ushort)bits, state.StickX(), state.StickY(), rightX, rightY);
    }
}

/// <summary>
/// The DualShock 2 on SIO2 (ps2tek: "SIO2 Controller Commands"): digital mode (ID 41h), analog mode with both sticks
/// (73h) and the DualShock 2 mode with twelve pressure bytes (79h), and the configuration commands 40h–4Fh the pad
/// libraries send in config mode (F3h). Pressure bytes report full pressure for pressed buttons. The rumble motors'
/// settings are kept but drive nothing.
/// </summary>
public sealed class Ps2Pad : IStateful
{
    private const byte ModeDigital = 0x41;
    private const byte ModeAnalog = 0x73;
    private const byte ModeDualShock2 = 0x79;
    private const byte ModeConfig = 0xF3;

    private static readonly StateTag Tag = StateTag.From("PPAD");

    private readonly byte[] _motorMap = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    private byte _mode = ModeDigital;
    private bool _config;
    private bool _analogLocked;
    private uint _responseMask;

    /// <summary>The pad's current input, set by the console from the player's controller.</summary>
    public Ps2PadInput Input { get; set; }

    /// <summary>The ID byte the pad answers the command byte with.</summary>
    public byte ModeId => _config ? ModeConfig : _mode;

    public void Reset()
    {
        _mode = ModeDigital;
        _config = false;
        _analogLocked = false;
        _responseMask = 0;
        _motorMap.AsSpan().Fill(0xFF);
    }

    /// <summary>
    /// Runs one packet: <paramref name="command"/> holds the bytes after the 01h device byte (the command byte, 00h,
    /// then the data); one reply byte per command byte goes to <paramref name="reply"/>.
    /// </summary>
    internal void Exchange(ReadOnlySpan<byte> command, List<byte> reply)
    {
        if (command.IsEmpty)
        {
            return;
        }

        var cmd = command[0];
        Span<byte> data = stackalloc byte[Math.Max(command.Length, 3)];
        data.Clear();
        data[0] = ModeId;
        data[1] = 0x5A;
        var payload = data[2..];

        // Outside config mode only 42h (poll) and 43h (enter config) work; others reply like a poll.
        if (!_config && cmd != 0x43)
        {
            cmd = 0x42;
        }

        switch (cmd)
        {
            case 0x42: // read data (also sets the motors)
                Poll(payload);
                break;
            case 0x43: // enter or leave config mode
                if (_config)
                {
                    // Inside config mode it only leaves it; zeros follow.
                    if (command.Length > 2 && command[2] == 0)
                    {
                        _config = false;
                    }
                }
                else
                {
                    Poll(payload, pressure: false);
                    if (command.Length > 2 && command[2] == 1)
                    {
                        _config = true;
                    }
                }

                break;
            case 0x40: // unknown, constant
                Set(payload, 2, 0x02);
                Set(payload, 5, 0x5A);
                break;
            case 0x41: // which buttons polls include
                if (_mode != ModeDigital)
                {
                    Set(payload, 0, 0xFF);
                    Set(payload, 1, 0xFF);
                    Set(payload, 2, 0x03);
                    Set(payload, 5, 0x5A);
                }

                break;
            case 0x44: // set digital or analog mode, and whether the analog button can change it
                if (command.Length > 2)
                {
                    _mode = command[2] != 0 ? ModeAnalog : ModeDigital;
                    _responseMask = command[2] != 0 ? 0x3Fu : 0;
                }

                if (command.Length > 3)
                {
                    _analogLocked = command[3] == 0x03;
                }

                break;
            case 0x45: // model and mode: a DualShock 2, analog on or off
                Set(payload, 0, 0x03);
                Set(payload, 1, 0x02);
                Set(payload, 2, (byte)(_mode == ModeDigital ? 0 : 1));
                Set(payload, 3, 0x02);
                Set(payload, 4, 0x01);
                break;
            case 0x46: // constant, in two halves selected by the first byte
            {
                var second = command.Length > 2 && command[2] != 0;
                Set(payload, 2, 0x01);
                Set(payload, 3, (byte)(second ? 0x01 : 0x02));
                Set(payload, 4, (byte)(second ? 0x01 : 0x00));
                Set(payload, 5, (byte)(second ? 0x14 : 0x0A));
                break;
            }

            case 0x47: // constant
                Set(payload, 2, 0x02);
                Set(payload, 4, 0x01);
                break;
            case 0x4C: // constant, selected by the first byte
                Set(payload, 3, (byte)(command.Length > 2 && command[2] != 0 ? 0x07 : 0x04));
                break;
            case 0x4D: // map the rumble motors: replies with the old mapping
                for (var i = 0; i < 6; i++)
                {
                    Set(payload, i, _motorMap[i]);
                    if (command.Length > 2 + i)
                    {
                        _motorMap[i] = command[2 + i];
                    }
                }

                break;
            case 0x4F: // which bytes polls return: 3Fh analog, 3FFFFh with the pressures
            {
                uint mask = 0;
                for (var i = 0; i < 3 && command.Length > 2 + i; i++)
                {
                    mask |= (uint)command[2 + i] << (8 * i);
                }

                _responseMask = mask;
                _mode = mask switch
                {
                    0x3F => ModeAnalog,
                    0x3FFFF => ModeDualShock2,
                    _ => ModeDigital,
                };
                Set(payload, 5, 0x5A);
                break;
            }

            default: // 48h–4Bh, 4Eh: unknown; zeros
                break;
        }

        for (var i = 0; i < command.Length; i++)
        {
            reply.Add(data[i]);
        }
    }

    private static void Set(Span<byte> payload, int index, byte value)
    {
        if (index < payload.Length)
        {
            payload[index] = value;
        }
    }

    /// <summary>
    /// The poll reply after the header: the buttons (active low), then in analog modes the right and left sticks
    /// (X then Y, 00h up/left, 80h centred), then in DualShock 2 mode the pressures of right, left, up, down,
    /// triangle, circle, cross, square, L1, R1, L2 and R2.
    /// </summary>
    private void Poll(Span<byte> payload, bool pressure = true)
    {
        var input = Input;
        var buttons = (ushort)~input.Buttons;
        Set(payload, 0, (byte)buttons);
        Set(payload, 1, (byte)(buttons >> 8));
        if (_mode == ModeDigital)
        {
            return;
        }

        Set(payload, 2, Axis(input.RightX));
        Set(payload, 3, Axis((sbyte)-Math.Max(input.RightY, (sbyte)-127)));
        Set(payload, 4, Axis(input.LeftX));
        Set(payload, 5, Axis((sbyte)-Math.Max(input.LeftY, (sbyte)-127)));
        if (_mode != ModeDualShock2 || !pressure)
        {
            return;
        }

        ReadOnlySpan<ushort> order =
        [
            Ps2PadInput.Right, Ps2PadInput.Left, Ps2PadInput.Up, Ps2PadInput.Down,
            Ps2PadInput.Triangle, Ps2PadInput.Circle, Ps2PadInput.Cross, Ps2PadInput.Square,
            Ps2PadInput.L1, Ps2PadInput.R1, Ps2PadInput.L2, Ps2PadInput.R2,
        ];
        for (var i = 0; i < order.Length; i++)
        {
            Set(payload, 6 + i, (byte)((input.Buttons & order[i]) != 0 ? 0xFF : 0x00));
        }
    }

    /// <summary>A signed axis (−128…127, positive right or down) as the pad's byte: 00h at one end, 80h centred, FFh at the other.</summary>
    private static byte Axis(sbyte value) => (byte)(value + 0x80);

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteByte(_mode);
        writer.WriteBool(_config);
        writer.WriteBool(_analogLocked);
        writer.WriteUInt32(_responseMask);
        writer.WriteBytes(_motorMap);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        if (reader.BeginChunk(Tag) != 1)
        {
            throw new StateFormatException("Unsupported PS2 pad state version.");
        }

        _mode = reader.ReadByte();
        _config = reader.ReadBool();
        _analogLocked = reader.ReadBool();
        _responseMask = reader.ReadUInt32();
        reader.ReadBytes(_motorMap);
        reader.EndChunk();
    }
}
