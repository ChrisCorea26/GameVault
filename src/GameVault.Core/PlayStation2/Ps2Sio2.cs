using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// SIO2, the PlayStation 2's serial port for controllers and memory cards (ps2tek: "SIO2 Registers"), at $1F808200.
/// SEND3 holds up to sixteen commands (port and length); their bytes arrive through the in FIFO (by the CPU or IOP DMA
/// channel 11) and each command runs as soon as its bytes are in, so the replies are waiting in the out FIFO (read by
/// the CPU or channel 12) by the time the CPU starts the transfer through CTRL, which raises the SIO2 interrupt
/// (IOP IRQ 17). The first byte of a command picks the device: 01h a pad, 21h a multitap, 61h infrared, 81h a memory
/// card.
/// </summary>
/// <remarks>
/// RECV1 reports which devices answered: 1100h for a memory card that is there, 1D100h for none; pad commands build
/// it from the ports used (100h for one port, 200h for two) with 1000h always set and 1D000h/2D000h for a missing
/// pad. RECV2 always reads 0Fh. When a transfer comes by DMA, each command takes one DMA block of the in FIFO and its
/// reply is padded to the block size, as SIO2MAN expects. (The facts follow PCSX2's SIO2, read for behaviour only.)
/// </remarks>
public sealed class Ps2Sio2 : IStateful
{
    public const uint Base = 0x1F808200;

    private const uint RecvDisconnected = 0x1D100;
    private const uint RecvConnected = 0x1100;
    private const uint RecvNoDevicesMissing = 0x1000;
    private const uint RecvOnePort = 0x100;
    private const uint RecvTwoPorts = 0x200;
    private const uint RecvPort1Missing = 0x1D000;
    private const uint RecvPort2Missing = 0x2D000;

    private static readonly StateTag Tag = StateTag.From("SIO2");

    private readonly uint[] _send3 = new uint[16];
    private readonly uint[] _send1 = new uint[4];
    private readonly uint[] _send2 = new uint[4];
    private readonly List<byte> _in = [];
    private readonly List<byte> _out = [];
    private int _outRead;

    private uint _ctrl = 0x3BC;
    private uint _recv1 = RecvDisconnected;
    private uint _recv3;
    private uint _unknown278;
    private uint _unknown27C;
    private uint _istat;

    private bool _commandLoaded;
    private int _queuePosition;
    private int _commandLength;
    private int _port;
    private bool _queueComplete;
    private int _dmaBlockSize;

    public Ps2Sio2()
    {
        Pads = [new Ps2Pad(), new Ps2Pad()];
    }

    /// <summary>The DualShock 2 on each port.</summary>
    public Ps2Pad[] Pads { get; }

    /// <summary>Whether a pad is plugged into each port.</summary>
    public bool[] PadConnected { get; } = [true, true];

    /// <summary>The memory card in slot 1.</summary>
    public Ps2MemoryCard? MemoryCard { get; set; }

    /// <summary>The memory card in slot 2, or null for an empty slot (outstanding-work plan §12).</summary>
    public Ps2MemoryCard? MemoryCard2 { get; set; }

    /// <summary>Raised when a transfer completes (IOP IRQ 17).</summary>
    public Action? Interrupt { get; set; }

    public void Reset()
    {
        Array.Clear(_send3);
        Array.Clear(_send1);
        Array.Clear(_send2);
        _ctrl = 0x3BC;
        _recv1 = RecvDisconnected;
        _recv3 = 0;
        _unknown278 = _unknown27C = 0;
        _istat = 0;
        _out.Clear();
        _outRead = 0;
        SoftReset();
        foreach (var pad in Pads)
        {
            pad.Reset();
        }

        MemoryCard?.Reset();
        MemoryCard2?.Reset();
    }

    /// <summary>Clears the command queue and the in FIFO (writing SEND3's first entry does this).</summary>
    private void SoftReset()
    {
        _commandLoaded = false;
        _queuePosition = 0;
        _commandLength = 0;
        _queueComplete = false;
        _dmaBlockSize = 0;
        _in.Clear();
        _recv1 = 0;
    }

    /// <summary>A register read; <paramref name="offset"/> is from $1F808200.</summary>
    public uint Read(uint offset)
    {
        switch (offset)
        {
            case < 0x40:
                return _send3[offset >> 2];
            case < 0x60:
                return (offset & 4) == 0 ? _send1[(offset - 0x40) >> 3] : _send2[(offset - 0x40) >> 3];
            case 0x64:
                return ReadFifo();
            case 0x68:
                return _ctrl;
            case 0x6C:
                return _recv1;
            case 0x70:
                return 0xF;
            case 0x74:
                return _recv3;
            case 0x78:
                return _unknown278;
            case 0x7C:
                return _unknown27C;
            case 0x80:
                return _istat;
            default:
                return 0;
        }
    }

    public void Write(uint offset, uint value)
    {
        switch (offset)
        {
            case < 0x40:
                _send3[offset >> 2] = value;
                if (offset >> 2 == 0)
                {
                    SoftReset();
                }

                break;
            case < 0x60:
                if ((offset & 4) == 0)
                {
                    _send1[(offset - 0x40) >> 3] = value;
                }
                else
                {
                    _send2[(offset - 0x40) >> 3] = value;
                }

                break;
            case 0x60:
                WriteFifo((byte)value);
                break;
            case 0x68:
                _ctrl = value;
                if ((value & 1) != 0)
                {
                    // Start: the replies are already in the out FIFO, so the transfer is done.
                    if (_istat == 0)
                    {
                        Interrupt?.Invoke();
                    }

                    _istat |= 1;
                }
                else if ((value & 0xC) != 0)
                {
                    // Bits 2 and 3 reset SIO2 for the next transfer.
                    _out.Clear();
                    _outRead = 0;
                    SoftReset();
                }

                break;
            case 0x78:
                _unknown278 = value;
                break;
            case 0x7C:
                _unknown27C = value;
                break;
            case 0x80:
                _istat &= ~value;
                break;
        }
    }

    /// <summary>IOP DMA channel 11 starts: each command takes one block of <paramref name="blockBytes"/> bytes.</summary>
    public void BeginDmaIn(int blockBytes) => _dmaBlockSize = blockBytes;

    /// <summary>A byte into the in FIFO (SIO2_FIFOIN or DMA channel 11).</summary>
    public void WriteFifo(byte data)
    {
        if (!_commandLoaded)
        {
            if (_queuePosition >= _send3.Length)
            {
                return;
            }

            var command = _send3[_queuePosition];
            _port = (int)(command & 1);
            _commandLength = (int)((command >> 8) & 0x1FF);
            _commandLoaded = true;
            _queueComplete = _commandLength == 0;
            _in.Clear();
        }

        if (_queueComplete)
        {
            return;
        }

        _in.Add(data);
        if ((_dmaBlockSize == 0 && _in.Count == _commandLength) || (_dmaBlockSize != 0 && _in.Count == _dmaBlockSize))
        {
            _commandLoaded = false;
            _queuePosition++;
            RunCommand();
        }
    }

    /// <summary>A byte from the out FIFO (SIO2_FIFOOUT or DMA channel 12); FFh when it is empty.</summary>
    public byte ReadFifo()
    {
        if (_outRead >= _out.Count)
        {
            return 0xFF;
        }

        var value = _out[_outRead++];
        if (_outRead == _out.Count)
        {
            _out.Clear();
            _outRead = 0;
        }

        return value;
    }

    private void RunCommand()
    {
        var length = _dmaBlockSize != 0 ? Math.Min(_commandLength, _in.Count) : _in.Count;
        var bytes = _in.Count > 0 ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_in)[..Math.Max(1, length)] : default;
        var start = _out.Count - _outRead;
        var device = bytes.IsEmpty ? (byte)0 : bytes[0];
        switch (device)
        {
            case 0x01:
                RunPad(bytes);
                break;
            case 0x81 when (_port == 0 ? MemoryCard : MemoryCard2) is { } card:
                _recv1 = RecvConnected;
                card.Exchange(bytes[1..], _out);
                break;
            default:
                // A multitap, infrared, an empty card slot or nothing at all: no device answers.
                _recv1 = device == 0x01 ? _recv1 : RecvDisconnected;
                for (var i = 0; i < bytes.Length; i++)
                {
                    _out.Add(0xFF);
                }

                break;
        }

        _in.Clear();

        if (_dmaBlockSize > 0)
        {
            var written = _out.Count - _outRead - start;
            while (written % _dmaBlockSize != 0)
            {
                _out.Add(0x00);
                written++;
            }
        }
    }

    private void RunPad(ReadOnlySpan<byte> bytes)
    {
        _recv1 = (_recv1 & RecvOnePort) != 0 ? (_recv1 & ~RecvOnePort) | RecvTwoPorts : _recv1 | RecvOnePort;
        _recv1 |= RecvNoDevicesMissing;
        if (!PadConnected[_port])
        {
            _recv1 |= _port == 0 ? RecvPort1Missing : RecvPort2Missing;
            for (var i = 0; i < bytes.Length; i++)
            {
                _out.Add(0xFF);
            }

            return;
        }

        _out.Add(0xFF);
        Pads[_port].Exchange(bytes[1..], _out);
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 2);
        foreach (var v in _send3)
        {
            writer.WriteUInt32(v);
        }

        for (var i = 0; i < 4; i++)
        {
            writer.WriteUInt32(_send1[i]);
            writer.WriteUInt32(_send2[i]);
        }

        writer.WriteInt32(_in.Count);
        foreach (var b in _in)
        {
            writer.WriteByte(b);
        }

        writer.WriteInt32(_out.Count - _outRead);
        for (var i = _outRead; i < _out.Count; i++)
        {
            writer.WriteByte(_out[i]);
        }

        writer.WriteUInt32(_ctrl);
        writer.WriteUInt32(_recv1);
        writer.WriteUInt32(_recv3);
        writer.WriteUInt32(_unknown278);
        writer.WriteUInt32(_unknown27C);
        writer.WriteUInt32(_istat);
        writer.WriteBool(_commandLoaded);
        writer.WriteInt32(_queuePosition);
        writer.WriteInt32(_commandLength);
        writer.WriteInt32(_port);
        writer.WriteBool(_queueComplete);
        writer.WriteInt32(_dmaBlockSize);
        foreach (var pad in Pads)
        {
            pad.SaveState(writer);
        }

        writer.WriteBool(PadConnected[0]);
        writer.WriteBool(PadConnected[1]);
        writer.WriteBool(MemoryCard is not null);
        MemoryCard?.SaveState(writer);
        writer.WriteBool(MemoryCard2 is not null);
        MemoryCard2?.SaveState(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        var version = reader.BeginChunk(Tag);
        if (version is not (1 or 2))
        {
            throw new StateFormatException("Unsupported SIO2 state version.");
        }

        for (var i = 0; i < _send3.Length; i++)
        {
            _send3[i] = reader.ReadUInt32();
        }

        for (var i = 0; i < 4; i++)
        {
            _send1[i] = reader.ReadUInt32();
            _send2[i] = reader.ReadUInt32();
        }

        _in.Clear();
        var count = ReadCount(reader);
        for (var i = 0; i < count; i++)
        {
            _in.Add(reader.ReadByte());
        }

        _out.Clear();
        _outRead = 0;
        count = ReadCount(reader);
        for (var i = 0; i < count; i++)
        {
            _out.Add(reader.ReadByte());
        }

        _ctrl = reader.ReadUInt32();
        _recv1 = reader.ReadUInt32();
        _recv3 = reader.ReadUInt32();
        _unknown278 = reader.ReadUInt32();
        _unknown27C = reader.ReadUInt32();
        _istat = reader.ReadUInt32();
        _commandLoaded = reader.ReadBool();
        _queuePosition = Math.Clamp(reader.ReadInt32(), 0, _send3.Length);
        _commandLength = reader.ReadInt32() & 0x1FF;
        _port = reader.ReadInt32() & 1;
        _queueComplete = reader.ReadBool();
        _dmaBlockSize = Math.Max(0, reader.ReadInt32());
        foreach (var pad in Pads)
        {
            pad.LoadState(reader);
        }

        PadConnected[0] = reader.ReadBool();
        PadConnected[1] = reader.ReadBool();
        if (reader.ReadBool())
        {
            // The card's contents travel with the state, so saves rewind with everything else.
            MemoryCard ??= new Ps2MemoryCard();
            MemoryCard.LoadState(reader);
        }

        // Version 1 (before slot 2) leaves slot 2's card as it is.
        if (version >= 2 && reader.ReadBool())
        {
            MemoryCard2 ??= new Ps2MemoryCard();
            MemoryCard2.LoadState(reader);
        }

        reader.EndChunk();
    }

    private static int ReadCount(StateReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 1 << 20)
        {
            throw new StateFormatException("SIO2 FIFO count out of range.");
        }

        return count;
    }
}
