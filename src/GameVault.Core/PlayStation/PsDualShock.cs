using GameVault.Core.Input;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation;

/// <summary>
/// The DualShock (SCPH-1200) on SIO0, one byte at a time (outstanding-work plan §11): digital mode (ID 41h), analog mode
/// with both sticks (73h) and the configuration mode (F3h) games use to switch and lock the mode and to map the two
/// motors (psx-spx: "Controllers - Configuration Commands", "Controllers - Vibration/Rumble Control").
/// </summary>
/// <remarks>
/// In digital mode the left stick also works the D-pad, as the player expects of a pad whose stick a digital game
/// can't see. In analog mode the sticks are reported as they are and the D-pad is only the D-pad.
/// </remarks>
public sealed class PsDualShock
{
    private const byte ModeDigital = 0x41;
    private const byte ModeAnalog = 0x73;
    private const byte ModeConfig = 0xF3;

    /// <summary>How far the left stick must be pushed to press the D-pad in digital mode.</summary>
    private const int DigitalThreshold = 64;

    private readonly byte[] _motorMap = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
    private readonly byte[] _motorData = new byte[6];

    private bool _analog;
    private bool _config;
    private bool _locked;
    private byte _command;
    private byte _parameter;
    private bool _startAnalog;
    private bool _pendingConfig;

    /// <summary>The player's input, set each frame.</summary>
    public ControllerState Input { get; set; }

    /// <summary>The small motor (on or off) and the large one (0–255), as the game last set them.</summary>
    public bool SmallMotor { get; private set; }

    public byte LargeMotor { get; private set; }

    /// <summary>In analog mode (the red LED on).</summary>
    public bool Analog => _analog;

    /// <summary>
    /// Start in analog mode, as if the player had pressed the Analog button (Settings → PlayStation → Analog mode): for
    /// games that support the sticks but don't switch the mode themselves.
    /// </summary>
    public bool StartAnalog
    {
        get => _startAnalog;
        set
        {
            _startAnalog = value;
            if (!_locked)
            {
                _analog = value;
            }
        }
    }

    private byte Id => _config ? ModeConfig : _analog ? ModeAnalog : ModeDigital;

    public void Reset()
    {
        _analog = _startAnalog;
        _config = false;
        _locked = false;
        _motorMap.AsSpan().Fill(0xFF);
        SmallMotor = false;
        LargeMotor = 0;
    }

    /// <summary>
    /// One byte of a packet after the 01h select byte. <paramref name="step"/> 0 is the command byte; the reply is the
    /// ID, then 5Ah, then the payload. <paramref name="ack"/> is false on the packet's last byte.
    /// </summary>
    public byte Exchange(int step, byte data, out bool ack)
    {
        if (step == 0)
        {
            _command = _config || data == 0x43 ? data : (byte)0x42; // outside config mode only poll and 43h work
            ack = true;
            return Id;
        }

        var length = PayloadLength();
        ack = step < length + 1;
        if (step == 1)
        {
            return 0x5A;
        }

        var index = step - 2;
        if (index >= length)
        {
            ack = false;
            return 0xFF;
        }

        if (index == 0)
        {
            _parameter = data;
        }

        switch (_command)
        {
            case 0x42:
                if (index < _motorData.Length)
                {
                    _motorData[index] = data;
                }

                if (index == length - 1)
                {
                    ApplyMotors();
                }

                return PollByte(index);
            case 0x43: // enter (1) or leave (0) config mode, decided by the first data byte
                var reply = _config ? (byte)0x00 : PollByte(index);
                if (index == 0)
                {
                    _pendingConfig = data == 1;
                }

                if (index == length - 1)
                {
                    _config = _pendingConfig;
                }

                return reply;
            case 0x44: // set the mode: data 0 digital / 1 analog, then 3 to lock it
                if (index == 0 && data is 0 or 1)
                {
                    _analog = data == 1;
                }
                else if (index == 1)
                {
                    _locked = data == 3;
                }

                return 0x00;
            case 0x45: // status: DualShock, the LED, two motors
                return index switch
                {
                    0 => 0x01,
                    1 => 0x02,
                    2 => (byte)(_analog ? 0x01 : 0x00),
                    3 => 0x02,
                    4 => 0x01,
                    _ => 0x00,
                };
            case 0x46:
                return index switch
                {
                    2 => 0x01,
                    3 => (byte)(_parameter == 0 ? 0x02 : 0x01),
                    4 => (byte)(_parameter == 0 ? 0x00 : 0x01),
                    5 => (byte)(_parameter == 0 ? 0x0A : 0x14),
                    _ => 0x00,
                };
            case 0x47:
                return index switch
                {
                    2 => 0x02,
                    4 => 0x01,
                    _ => 0x00,
                };
            case 0x4C:
                return index == 3 ? (byte)(_parameter == 0 ? 0x04 : 0x07) : (byte)0x00;
            case 0x4D: // map the motors to the poll's data bytes: 00h the small one, 01h the large one, FFh none
            {
                var old = _motorMap[index];
                _motorMap[index] = data;
                return old;
            }

            default:
                return 0x00;
        }
    }

    private int PayloadLength() => _command == 0x42 || (_command == 0x43 && !_config) ? (_analog ? 6 : 2) : 6;

    /// <summary>The poll's payload: the buttons (active low), then in analog mode the right and left sticks.</summary>
    private byte PollByte(int index)
    {
        var state = Input;
        var buttons = (ushort)~Buttons(state);
        return index switch
        {
            0 => (byte)buttons,
            1 => (byte)(buttons >> 8),
            2 => Axis(state.RightStickX()),
            3 => Axis((sbyte)-Math.Max(state.RightStickY(), (sbyte)-127)),
            4 => Axis(state.StickX()),
            5 => Axis((sbyte)-Math.Max(state.StickY(), (sbyte)-127)),
            _ => 0xFF,
        };
    }

    /// <summary>A signed axis (positive right or up) as the pad's byte: 00h at the left or top, 80h centred, FFh at the other end.</summary>
    private static byte Axis(sbyte value) => (byte)(value + 0x80);

    /// <summary>The pad's button bits (active high here): Select, L3, R3, Start, D-pad, then L2, R2, L1, R1, △, ○, ×, □.</summary>
    private int Buttons(ControllerState state)
    {
        var bits = 0;
        bits |= state.HasFlag(ControllerState.Select) ? 1 << 0 : 0;
        bits |= _analog && state.HasFlag(ControllerState.L3) ? 1 << 1 : 0;
        bits |= _analog && state.HasFlag(ControllerState.R3) ? 1 << 2 : 0;
        bits |= state.HasFlag(ControllerState.Start) ? 1 << 3 : 0;
        var up = state.HasFlag(ControllerState.Up);
        var right = state.HasFlag(ControllerState.Right);
        var down = state.HasFlag(ControllerState.Down);
        var left = state.HasFlag(ControllerState.Left);
        if (!_analog)
        {
            up |= state.StickY() >= DigitalThreshold;
            down |= state.StickY() <= -DigitalThreshold;
            right |= state.StickX() >= DigitalThreshold;
            left |= state.StickX() <= -DigitalThreshold;
        }

        bits |= up ? 1 << 4 : 0;
        bits |= right ? 1 << 5 : 0;
        bits |= down ? 1 << 6 : 0;
        bits |= left ? 1 << 7 : 0;
        bits |= state.HasFlag(ControllerState.L2) ? 1 << 8 : 0;
        bits |= state.HasFlag(ControllerState.R2) ? 1 << 9 : 0;
        bits |= state.HasFlag(ControllerState.L) ? 1 << 10 : 0;
        bits |= state.HasFlag(ControllerState.R) ? 1 << 11 : 0;
        bits |= state.HasFlag(ControllerState.X) ? 1 << 12 : 0; // Triangle (north)
        bits |= state.HasFlag(ControllerState.A) ? 1 << 13 : 0; // Circle (east)
        bits |= state.HasFlag(ControllerState.B) ? 1 << 14 : 0; // Cross (south)
        bits |= state.HasFlag(ControllerState.Y) ? 1 << 15 : 0; // Square (west)
        return bits;
    }

    /// <summary>The poll's data bytes drive the motors the game mapped to them (01h and up spins the small one).</summary>
    private void ApplyMotors()
    {
        var small = false;
        byte large = 0;
        for (var i = 0; i < _motorMap.Length; i++)
        {
            switch (_motorMap[i])
            {
                case 0x00:
                    small = (_motorData[i] & 1) != 0;
                    break;
                case 0x01:
                    large = _motorData[i];
                    break;
            }
        }

        SmallMotor = small;
        LargeMotor = large;
    }

    public void SaveState(StateWriter writer)
    {
        writer.WriteBool(_analog);
        writer.WriteBool(_config);
        writer.WriteBool(_locked);
        writer.WriteByte(_command);
        writer.WriteByte(_parameter);
        writer.WriteBool(_pendingConfig);
        writer.WriteBytes(_motorMap);
        writer.WriteBytes(_motorData);
        writer.WriteBool(SmallMotor);
        writer.WriteByte(LargeMotor);
    }

    public void LoadState(StateReader reader)
    {
        _analog = reader.ReadBool();
        _config = reader.ReadBool();
        _locked = reader.ReadBool();
        _command = reader.ReadByte();
        _parameter = reader.ReadByte();
        _pendingConfig = reader.ReadBool();
        reader.ReadBytes(_motorMap);
        reader.ReadBytes(_motorData);
        SmallMotor = reader.ReadBool();
        LargeMotor = reader.ReadByte();
    }
}
