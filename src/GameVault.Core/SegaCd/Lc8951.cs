using GameVault.Core.State;

namespace GameVault.Core.SegaCd;

/// <summary>
/// The Sega CD's CDC, a Sanyo LC8951 (sega-cd-32x.md): it takes the sectors the drive reads, writes each one's header
/// and 2,048 data bytes into its 16 KB buffer, and hands bytes from the buffer to the host port or to DMA. Its
/// decoder and transfer-end interrupts are the sub CPU's level 5.
/// </summary>
/// <remarks>
/// Registers, picked by the gate array's register address (incremented after each access unless it's 0) — written:
/// 0 SBOUT, 1 IFCTRL, 2–3 DBC, 4–5 DAC, 6 DTTRG, 7 DTACK, 8–9 WA, 10 CTRL0, 11 CTRL1, 12–13 PT, 14 CTRL2, 15 RESET;
/// read: 0 COMIN, 1 IFSTAT, 2–3 DBC, 4–7 HEAD0–3, 8–9 PT, 10–11 WA, 12–15 STAT0–3. IFSTAT's flags are active low.
/// Behaviour follows the LC8951 data sheet as Genesis Plus GX's cdc.c applies it, read for behaviour only.
/// </remarks>
public sealed class Lc8951 : IStateful
{
    public const int BufferSize = 0x4000;

    // IFSTAT (active low) and IFCTRL bits.
    private const byte Cmdi = 0x80, Dtei = 0x40, Deci = 0x20, Dtbsy = 0x08, Douten = 0x02;

    // CTRL0 bits.
    private const byte Decen = 0x80, Wrrq = 0x04;

    private static readonly StateTag Tag = StateTag.From("CDC8");

    private readonly byte[] _ram = new byte[BufferSize];
    private readonly byte[] _head = new byte[4];
    private readonly byte[] _stat = new byte[4];
    private readonly byte[] _ctrl = new byte[2];
    private byte _ifstat = 0xFF;
    private byte _ifctrl;
    private int _dbc;
    private ushort _dac;
    private ushort _wa;
    private ushort _pt;
    private int _registerAddress;

    public Span<byte> Buffer => _ram;

    /// <summary>The transfer's destination (the gate array's DD bits): 2 the main CPU's host port, 3 the sub CPU's, 4 PCM RAM, 5 PRG-RAM, 7 Word RAM.</summary>
    public int Destination { get; set; }

    /// <summary>DSR: the host port has data.</summary>
    public bool DataReady { get; private set; }

    /// <summary>EDT: the transfer finished.</summary>
    public bool TransferEnded { get; private set; }

    /// <summary>A DMA transfer is running (destinations 4, 5 and 7).</summary>
    public bool DmaActive { get; private set; }

    /// <summary>The CDC's interrupt output (decoder or transfer end, as IFCTRL enables them).</summary>
    public bool Interrupt => (~_ifstat & _ifctrl & (Cmdi | Dtei | Deci)) != 0;

    public int RegisterAddress
    {
        get => _registerAddress & 0x0F;
        set => _registerAddress = value & 0x0F;
    }

    public void Reset()
    {
        _ifstat = 0xFF;
        _ifctrl = 0;
        _dbc = 0;
        _dac = _wa = _pt = 0;
        Array.Clear(_head);
        Array.Clear(_stat);
        Array.Clear(_ctrl);
        _stat[3] = 0x80;
        _registerAddress = 0;
        DataReady = TransferEnded = DmaActive = false;
    }

    public void Power()
    {
        Array.Clear(_ram);
        Destination = 0;
        Reset();
    }

    private void Advance()
    {
        if ((_registerAddress & 0x0F) != 0)
        {
            _registerAddress = (_registerAddress + 1) & 0x1F;
        }
    }

    public byte ReadRegister()
    {
        byte value;
        switch (_registerAddress & 0x0F)
        {
            case 0x00: value = 0xFF; break; // COMIN
            case 0x01: value = _ifstat; break;
            case 0x02: value = (byte)_dbc; break;
            case 0x03: value = (byte)((_dbc >> 8) & 0x0F); break;
            case 0x04: value = _head[0]; break;
            case 0x05: value = _head[1]; break;
            case 0x06: value = _head[2]; break;
            case 0x07: value = _head[3]; break;
            case 0x08: value = (byte)_pt; break;
            case 0x09: value = (byte)(_pt >> 8); break;
            case 0x0A: value = (byte)_wa; break;
            case 0x0B: value = (byte)(_wa >> 8); break;
            case 0x0C: value = _stat[0]; break;
            case 0x0D: value = _stat[1]; break;
            case 0x0E: value = _stat[2]; break;
            default:
                value = _stat[3];
                // Reading STAT3 acknowledges the decoder: no valid status until the next sector.
                _ifstat |= Deci;
                _stat[3] = 0x80;
                break;
        }

        Advance();
        return value;
    }

    public void WriteRegister(byte value)
    {
        switch (_registerAddress & 0x0F)
        {
            case 0x01:
                _ifctrl = value;
                if ((value & Douten) == 0)
                {
                    _ifstat |= Dtbsy | Dtei; // data output off: any transfer stops
                    DataReady = DmaActive = false;
                }

                break;
            case 0x02: _dbc = (_dbc & 0xF00) | value; break;
            case 0x03: _dbc = (_dbc & 0x0FF) | ((value & 0x0F) << 8); break;
            case 0x04: _dac = (ushort)((_dac & 0xFF00) | value); break;
            case 0x05: _dac = (ushort)((_dac & 0x00FF) | (value << 8)); break;
            case 0x06: StartTransfer(); break; // DTTRG
            case 0x07: _ifstat |= Dtei; break; // DTACK
            case 0x08: _wa = (ushort)((_wa & 0xFF00) | value); break;
            case 0x09: _wa = (ushort)((_wa & 0x00FF) | (value << 8)); break;
            case 0x0A: _ctrl[0] = value; break;
            case 0x0B: _ctrl[1] = value; break;
            case 0x0C: _pt = (ushort)((_pt & 0xFF00) | value); break;
            case 0x0D: _pt = (ushort)((_pt & 0x00FF) | (value << 8)); break;
            case 0x0F: Reset(); return;
        }

        Advance();
    }

    private void StartTransfer()
    {
        if ((_ifctrl & Douten) == 0)
        {
            return;
        }

        _ifstat &= unchecked((byte)~Dtbsy);
        TransferEnded = false;
        DataReady = Destination is 2 or 3;
        DmaActive = Destination is 4 or 5 or 7;
    }

    private void EndTransfer()
    {
        DataReady = DmaActive = false;
        TransferEnded = true;
        _ifstat |= Dtbsy;
        _ifstat &= unchecked((byte)~Dtei);
    }

    /// <summary>The next byte of the transfer; the transfer ends after DBC + 1 bytes.</summary>
    public byte NextByte()
    {
        var value = _ram[_dac & (BufferSize - 1)];
        _dac++;
        if (--_dbc < 0)
        {
            _dbc = 0x0FFF;
            EndTransfer();
        }

        return value;
    }

    /// <summary>A word from the host port (destinations 2 and 3).</summary>
    public ushort ReadHostWord()
    {
        if (!DataReady)
        {
            return 0;
        }

        var high = NextByte();
        var low = DataReady ? NextByte() : (byte)0;
        return (ushort)((high << 8) | low);
    }

    /// <summary>A sector from the drive: if decoding is on, its header goes to HEAD0–3 and, with WRRQ, it's written to the buffer.</summary>
    /// <param name="raw">The 2,352-byte sector: sync, header, then 2,048 bytes of mode 1 data.</param>
    public void Decode(ReadOnlySpan<byte> raw)
    {
        if ((_ctrl[0] & Decen) == 0)
        {
            return;
        }

        raw.Slice(12, 4).CopyTo(_head);
        _stat[0] = 0x80; // CRC OK
        _stat[1] = 0;
        _stat[2] = 0;
        _stat[3] = 0x00; // valid status
        _ifstat &= unchecked((byte)~Deci);
        if ((_ctrl[0] & Wrrq) != 0)
        {
            _pt = (ushort)(_pt + 2352);
            _wa = (ushort)(_wa + 2352);
            var offset = _pt;
            for (var i = 0; i < 4 + 2048; i++)
            {
                _ram[(offset + i) & (BufferSize - 1)] = raw[12 + i];
            }
        }
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_ram);
        writer.WriteBytes(_head);
        writer.WriteBytes(_stat);
        writer.WriteBytes(_ctrl);
        writer.WriteByte(_ifstat);
        writer.WriteByte(_ifctrl);
        writer.WriteInt32(_dbc);
        writer.WriteUInt16(_dac);
        writer.WriteUInt16(_wa);
        writer.WriteUInt16(_pt);
        writer.WriteInt32(_registerAddress);
        writer.WriteInt32(Destination);
        writer.WriteBool(DataReady);
        writer.WriteBool(TransferEnded);
        writer.WriteBool(DmaActive);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_ram);
        reader.ReadBytes(_head);
        reader.ReadBytes(_stat);
        reader.ReadBytes(_ctrl);
        _ifstat = reader.ReadByte();
        _ifctrl = reader.ReadByte();
        _dbc = Math.Clamp(reader.ReadInt32(), 0, 0x0FFF);
        _dac = reader.ReadUInt16();
        _wa = reader.ReadUInt16();
        _pt = reader.ReadUInt16();
        _registerAddress = reader.ReadInt32() & 0x1F;
        Destination = reader.ReadInt32() & 7;
        DataReady = reader.ReadBool();
        TransferEnded = reader.ReadBool();
        DmaActive = reader.ReadBool();
        reader.EndChunk();
    }
}
