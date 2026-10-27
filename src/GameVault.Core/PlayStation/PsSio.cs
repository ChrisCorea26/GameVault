using GameVault.Core.Cartridge;
using GameVault.Core.Input;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation;

/// <summary>
/// A PlayStation memory card: 128 KB in 1,024 frames of 128 bytes, battery-free flash the platform saves as a raw
/// <c>.mcd</c> image (the format other emulators read).
/// </summary>
public sealed class PsMemoryCard : IBatteryBacked
{
    public const int Size = 128 * 1024;

    private readonly byte[] _data = new byte[Size];

    public PsMemoryCard()
    {
        Format(_data);
    }

    public bool HasBattery => true;

    public Span<byte> BatteryRam => _data;

    /// <summary>
    /// Writes an empty, formatted card: the "MC" header, fifteen free directory entries, an empty broken-sector list,
    /// and the write-test frame (psx-spx: "Memory Card Data Format").
    /// </summary>
    public static void Format(Span<byte> card)
    {
        card.Clear();
        Span<byte> frame = stackalloc byte[128];
        frame[0] = (byte)'M';
        frame[1] = (byte)'C';
        WriteFrame(card, 0, frame);
        WriteFrame(card, 63, frame);

        for (var i = 1; i < 16; i++)
        {
            frame.Clear();
            frame[0] = 0xA0; // free, freshly formatted
            frame[8] = frame[9] = 0xFF; // no next block
            WriteFrame(card, i, frame);
        }

        for (var i = 16; i < 36; i++)
        {
            frame.Clear();
            frame[..4].Fill(0xFF); // no broken sector
            frame[8] = frame[9] = 0xFF;
            WriteFrame(card, i, frame);
        }

        for (var i = 36; i < 63; i++)
        {
            card.Slice(i * 128, 128).Fill(0xFF);
        }
    }

    private static void WriteFrame(Span<byte> card, int index, Span<byte> frame)
    {
        byte checksum = 0;
        for (var i = 0; i < 127; i++)
        {
            checksum ^= frame[i];
        }

        frame[127] = checksum;
        frame.CopyTo(card.Slice(index * 128, 128));
    }
}

/// <summary>
/// SIO0, the controller and memory card port at $1F801040 (SPEC §7.11, Phase 15): DualShock pads (outstanding-work
/// plan §11; digital until a game or the player switches them to analog) and memory cards in both slots, with the transfer time from the baud rate and the /ACK pulse that raises IRQ 7.
/// </summary>
/// <remarks>Reference: psx-spx "Controllers and Memory Cards", "Controller and Memory Card I/O Ports".</remarks>
public sealed class PsSio : IStateful
{
    private const int AckDelay = 340;

    private static readonly StateTag Tag = StateTag.From("PSIO");
    private static readonly byte[] DiscardBuffer = new byte[PsMemoryCard.Size];

    private readonly ControllerState[] _pads = new ControllerState[2];
    private readonly PsDualShock[] _dualShocks = [new PsDualShock(), new PsDualShock()];
    private readonly PsMemoryCard?[] _cards = new PsMemoryCard?[2];
    private readonly byte[] _cardFlag = [0x08, 0x08];

    private ushort _mode;
    private ushort _control;
    private ushort _baud = 0x88;
    private byte _rxData = 0xFF;
    private bool _rxReady;
    private bool _irq;
    private bool _ackLow;
    private long _transferDoneAt = long.MaxValue;
    private long _ackAt = long.MaxValue;
    private byte _txData;
    private bool _pendingAck;

    // The device being talked to in this selection, and where it is in its sequence.
    private Device _device;
    private int _step;
    private byte _cardCommand;
    private int _cardAddress;
    private byte _cardChecksum;
    private byte _cardPrevious;

    private enum Device
    {
        None,
        Pad,
        Card,
        Ignored,
    }

    public PsSio(PsMemoryCard? slot1 = null, PsMemoryCard? slot2 = null)
    {
        _cards[0] = slot1;
        _cards[1] = slot2;
    }

    /// <summary>IRQ 7, raised on /ACK when enabled.</summary>
    public Action? Interrupt { get; set; }

    public PsMemoryCard? Card(int slot) => _cards[slot];

    public void SetPad(int port, ControllerState state)
    {
        _pads[port] = state;
        _dualShocks[port].Input = state;
    }

    /// <summary>The DualShock in a port.</summary>
    public PsDualShock Pad(int port) => _dualShocks[port];

    public void Reset()
    {
        _mode = _control = 0;
        _baud = 0x88;
        _rxData = 0xFF;
        _rxReady = _irq = _ackLow = false;
        _transferDoneAt = _ackAt = long.MaxValue;
        _device = Device.None;
        foreach (var pad in _dualShocks)
        {
            pad.Reset();
        }
    }

    public long NextEvent => Math.Min(_transferDoneAt, _ackAt);

    public void Advance(long now)
    {
        if (_transferDoneAt <= now)
        {
            _transferDoneAt = long.MaxValue;
            _rxReady = true;
            if (_pendingAck)
            {
                _ackAt = now + AckDelay;
            }
        }

        if (_ackAt <= now)
        {
            _ackAt = long.MaxValue;
            _ackLow = true;
            if ((_control & 0x1000) != 0 && !_irq)
            {
                _irq = true;
                Interrupt?.Invoke();
            }
        }
    }

    // ---- Registers (offsets from $1F801040) ----

    public uint Read(int offset, long now)
    {
        Advance(now);
        switch (offset)
        {
            case 0x0:
            {
                var value = _rxReady ? _rxData : (byte)0xFF;
                _rxReady = false;
                return value;
            }

            case 0x4:
            {
                var status = 0x01u | 0x04u; // TX ready
                status |= _rxReady ? 0x02u : 0;
                status |= _ackLow ? 0x80u : 0;
                status |= _irq ? 0x200u : 0;
                _ackLow = false; // the /ACK pulse is short
                return status;
            }

            case 0x8:
                return _mode;
            case 0xA:
                return _control;
            case 0xE:
                return _baud;
            default:
                return 0;
        }
    }

    public void Write(int offset, ushort value, long now)
    {
        Advance(now);
        switch (offset)
        {
            case 0x0:
                StartTransfer((byte)value, now);
                break;
            case 0x8:
                _mode = value;
                break;
            case 0xA:
                if ((value & 0x40) != 0)
                {
                    Reset();
                    return;
                }

                if ((value & 0x10) != 0)
                {
                    _irq = false; // acknowledge
                }

                // Deselecting (bit 1 cleared) ends the conversation with the device.
                if ((value & 0x02) == 0 || ((value ^ _control) & 0x2000) != 0)
                {
                    _device = Device.None;
                }

                _control = (ushort)(value & ~0x50);
                break;
            case 0xE:
                _baud = value;
                break;
        }
    }

    private void StartTransfer(byte data, long now)
    {
        _txData = data;
        _rxData = Exchange(data, out _pendingAck);
        var factor = (_mode & 3) switch { 2 => 16, 3 => 64, _ => 1 };
        _transferDoneAt = now + Math.Max(64, _baud * factor * 8 / 2);
    }

    /// <summary>One byte each way with the selected device; <paramref name="ack"/> says whether it answers with /ACK.</summary>
    private byte Exchange(byte data, out bool ack)
    {
        ack = false;
        if ((_control & 0x02) == 0)
        {
            return 0xFF;
        }

        var port = (_control & 0x2000) != 0 ? 1 : 0;
        if (_device == Device.None)
        {
            _step = 0;
            _device = data switch
            {
                0x01 => Device.Pad,
                0x81 when _cards[port] is not null => Device.Card,
                _ => Device.Ignored,
            };
            if (_device == Device.Ignored)
            {
                return 0xFF;
            }

            ack = true;
            return 0xFF;
        }

        return _device switch
        {
            Device.Pad => PadExchange(port, data, out ack),
            Device.Card => CardExchange(port, data, out ack),
            _ => 0xFF,
        };
    }

    // ---- The pad ----

    private byte PadExchange(int port, byte data, out bool ack) => _dualShocks[port].Exchange(_step++, data, out ack);

    // ---- Memory card ----

    private byte CardExchange(int port, byte data, out bool ack)
    {
        var card = _cards[port]!.BatteryRam;
        var step = _step++;
        ack = true;
        if (step == 0)
        {
            _cardCommand = data;
            return _cardFlag[port];
        }

        switch (_cardCommand)
        {
            case 0x52: // 'R': read a frame
                switch (step)
                {
                    case 1: return 0x5A;
                    case 2: return 0x5D;
                    case 3:
                        _cardAddress = data << 8;
                        _cardPrevious = data;
                        return 0x00;
                    case 4:
                        _cardAddress |= data;
                        var previous = _cardPrevious;
                        _cardPrevious = data;
                        return previous;
                    case 5: return 0x5C;
                    case 6: return 0x5D;
                    case 7:
                        _cardAddress &= 0x3FF;
                        _cardChecksum = (byte)((_cardAddress >> 8) ^ (_cardAddress & 0xFF));
                        return (byte)(_cardAddress >> 8);
                    case 8: return (byte)_cardAddress;
                    case < 9 + 128:
                    {
                        var value = card[_cardAddress * 128 + step - 9];
                        _cardChecksum ^= value;
                        return value;
                    }

                    case 9 + 128: return _cardChecksum;
                    default:
                        ack = false;
                        return 0x47; // 'G': good
                }

            case 0x57: // 'W': write a frame
                switch (step)
                {
                    case 1: return 0x5A;
                    case 2: return 0x5D;
                    case 3:
                        _cardAddress = data << 8;
                        _cardPrevious = data;
                        return 0x00;
                    case 4:
                        _cardAddress |= data;
                        _cardChecksum = (byte)((_cardAddress >> 8) ^ data);
                        var previous = _cardPrevious;
                        _cardPrevious = data;
                        return previous;
                    case < 5 + 128:
                    {
                        if (_cardAddress < 0x400)
                        {
                            card[_cardAddress * 128 + step - 5] = data;
                        }

                        _cardChecksum ^= data;
                        var echo = _cardPrevious;
                        _cardPrevious = data;
                        return echo;
                    }

                    case 5 + 128:
                        _cardChecksum ^= data; // zero when the sender's checksum matched
                        return _cardPrevious;
                    case 6 + 128: return 0x5C;
                    case 7 + 128: return 0x5D;
                    default:
                        ack = false;
                        _cardFlag[port] &= unchecked((byte)~0x08); // the "not written yet" flag clears
                        return _cardAddress >= 0x400 ? (byte)0xFF : _cardChecksum == 0 ? (byte)0x47 : (byte)0x4E;
                }

            case 0x53: // 'S': card ID
                ReadOnlySpan<byte> id = [0x5A, 0x5D, 0x5C, 0x5D, 0x04, 0x00, 0x00, 0x80];
                if (step - 1 < id.Length)
                {
                    ack = step - 1 < id.Length - 1;
                    return id[step - 1];
                }

                ack = false;
                return 0xFF;
            default:
                ack = false;
                return 0xFF;
        }
    }

    // ---- Save state ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 2);
        writer.WriteUInt16(_mode);
        writer.WriteUInt16(_control);
        writer.WriteUInt16(_baud);
        writer.WriteByte(_rxData);
        writer.WriteBool(_rxReady);
        writer.WriteBool(_irq);
        writer.WriteBool(_ackLow);
        writer.WriteInt64(_transferDoneAt);
        writer.WriteInt64(_ackAt);
        writer.WriteByte(_txData);
        writer.WriteBool(_pendingAck);
        writer.WriteInt32((int)_device);
        writer.WriteInt32(_step);
        writer.WriteByte(_cardCommand);
        writer.WriteInt32(_cardAddress);
        writer.WriteByte(_cardChecksum);
        writer.WriteByte(_cardPrevious);
        writer.WriteBytes(_cardFlag);
        for (var i = 0; i < 2; i++)
        {
            writer.WriteUInt16((ushort)_pads[i]);
            writer.WriteBool(_cards[i] is not null);
            if (_cards[i] is { } card)
            {
                writer.WriteBytes(card.BatteryRam);
            }
        }

        foreach (var pad in _dualShocks)
        {
            pad.SaveState(writer);
        }

        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        var version = reader.BeginChunk(Tag);
        _mode = reader.ReadUInt16();
        _control = reader.ReadUInt16();
        _baud = reader.ReadUInt16();
        _rxData = reader.ReadByte();
        _rxReady = reader.ReadBool();
        _irq = reader.ReadBool();
        _ackLow = reader.ReadBool();
        _transferDoneAt = reader.ReadInt64();
        _ackAt = reader.ReadInt64();
        _txData = reader.ReadByte();
        _pendingAck = reader.ReadBool();
        _device = (Device)reader.ReadInt32();
        _step = reader.ReadInt32();
        _cardCommand = reader.ReadByte();
        _cardAddress = reader.ReadInt32();
        _cardChecksum = reader.ReadByte();
        _cardPrevious = reader.ReadByte();
        reader.ReadBytes(_cardFlag);
        for (var i = 0; i < 2; i++)
        {
            _pads[i] = (ControllerState)reader.ReadUInt16();
            if (reader.ReadBool())
            {
                // A card in the state replaces the card's contents, so saves rewind with everything else.
                reader.ReadBytes(_cards[i] is { } card ? card.BatteryRam : DiscardBuffer);
            }
        }

        // Version 1 had digital pads only, which is how a DualShock starts.
        foreach (var pad in _dualShocks)
        {
            if (version >= 2)
            {
                pad.LoadState(reader);
            }
            else
            {
                pad.Reset();
            }
        }

        reader.EndChunk();
    }
}
