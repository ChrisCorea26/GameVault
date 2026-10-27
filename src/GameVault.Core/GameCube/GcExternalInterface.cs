using System.Buffers.Binary;
using GameVault.Core.State;

namespace GameVault.Core.GameCube;

/// <summary>
/// The GameCube's EXI bus (phase-18.md, milestone 1) at $0C006800: three channels, each with its status, DMA address,
/// length, control and data registers. Channel 0's device 1 is the RTC and SRAM chip (the OS's clock and settings); no
/// memory card or broadband adapter is attached yet, so the other devices read as absent.
/// </summary>
/// <remarks>
/// A transfer starts when the control register's TSTART is written: immediate transfers move one to four bytes through
/// the data register, DMA moves LENGTH bytes to or from main RAM; either sets TCINT at once. On the RTC/SRAM chip the
/// first word written after selecting it is a command: $20000000 reads (or, with bit 31, writes) the RTC, $20000100
/// the 64-byte SRAM; anything else reads the boot ROM, which isn't here (zeros). Reference: YAGCD, "EXI".
/// </remarks>
public sealed class GcExternalInterface : IStateful
{
    private static readonly StateTag Tag = StateTag.From("GCEX");

    private readonly uint[] _csr = new uint[3];
    private readonly uint[] _mar = new uint[3];
    private readonly uint[] _length = new uint[3];
    private readonly uint[] _cr = new uint[3];
    private readonly uint[] _data = new uint[3];
    private readonly byte[] _sram = DefaultSram();
    private uint _command;
    private bool _haveCommand;
    private int _offset;
    private uint _rtc;

    /// <summary>Main RAM, for DMA (the console sets it).</summary>
    public byte[]? Ram { get; set; }

    public bool Interrupt
    {
        get
        {
            for (var c = 0; c < 3; c++)
            {
                var csr = _csr[c];
                if (((csr & 0x2) != 0 && (csr & 0x1) != 0) || ((csr & 0x8) != 0 && (csr & 0x4) != 0) || ((csr & 0x800) != 0 && (csr & 0x400) != 0))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public ReadOnlySpan<byte> Sram => _sram;

    /// <summary>The SRAM a fresh console has: NTSC, English, stereo, with valid checksums.</summary>
    private static byte[] DefaultSram()
    {
        var sram = new byte[64];
        sram[0x12] = 0x00; // language: English
        sram[0x13] = 0x2C; // flags: stereo, progressive off, the OS's "set up" bit
        ushort sum = 0, inverse = 0;
        for (var i = 0x0C; i < 0x14; i += 2)
        {
            var word = BinaryPrimitives.ReadUInt16BigEndian(sram.AsSpan(i));
            sum += word;
            inverse += (ushort)~word;
        }

        BinaryPrimitives.WriteUInt16BigEndian(sram, sum);
        BinaryPrimitives.WriteUInt16BigEndian(sram.AsSpan(2), inverse);
        return sram;
    }

    public void Reset()
    {
        Array.Clear(_csr);
        Array.Clear(_mar);
        Array.Clear(_length);
        Array.Clear(_cr);
        Array.Clear(_data);
        _command = 0;
        _haveCommand = false;
        _offset = 0;
    }

    public uint Read32(int offset)
    {
        var channel = Math.Min((offset & 0xFF) / 0x14, 2);
        return ((offset & 0xFF) % 0x14) switch
        {
            0x00 => _csr[channel] | (channel == 0 ? 0 : 0u), // EXT stays clear: no memory card in either slot
            0x04 => _mar[channel],
            0x08 => _length[channel],
            0x0C => _cr[channel],
            _ => _data[channel],
        };
    }

    public void Write32(int offset, uint value)
    {
        var channel = Math.Min((offset & 0xFF) / 0x14, 2);
        switch ((offset & 0xFF) % 0x14)
        {
            case 0x00:
            {
                // The interrupt flags clear by writing 1; selecting a device starts a new command.
                var old = _csr[channel];
                var selected = (value >> 7) & 7;
                if (selected != ((old >> 7) & 7))
                {
                    _haveCommand = false;
                    _offset = 0;
                }

                var flags = old & 0x80A & ~value;
                _csr[channel] = (value & ~0x80Au & 0x3FFF) | flags;
                break;
            }

            case 0x04: _mar[channel] = value & 0x03FFFFE0; break;
            case 0x08: _length[channel] = value & 0x03FFFFE0; break;
            case 0x0C:
                _cr[channel] = value & 0x3F;
                if ((value & 1) != 0)
                {
                    Transfer(channel);
                }

                break;
            default:
                _data[channel] = value;
                break;
        }
    }

    private bool RtcSramSelected(int channel) => channel == 0 && ((_csr[0] >> 7) & 7) == 2;

    private void Transfer(int channel)
    {
        var cr = _cr[channel];
        var dma = (cr & 2) != 0;
        var write = ((cr >> 2) & 3) == 1;
        var device = RtcSramSelected(channel);
        if (!dma)
        {
            var count = (int)((cr >> 4) & 3) + 1;
            if (write)
            {
                for (var i = 0; i < count; i++)
                {
                    WriteByte(device, (byte)(_data[channel] >> (24 - i * 8)));
                }
            }
            else
            {
                uint value = 0;
                for (var i = 0; i < count; i++)
                {
                    value |= (uint)ReadByte(device) << (24 - i * 8);
                }

                _data[channel] = value;
            }
        }
        else if (Ram is { } ram)
        {
            var address = (int)_mar[channel];
            for (var i = 0; i < _length[channel] && address + i < ram.Length; i++)
            {
                if (write)
                {
                    WriteByte(device, ram[address + i]);
                }
                else
                {
                    ram[address + i] = ReadByte(device);
                }
            }
        }

        _cr[channel] &= ~1u;
        _csr[channel] |= 0x08; // TCINT
    }

    private void WriteByte(bool device, byte value)
    {
        if (!device)
        {
            return;
        }

        if (!_haveCommand)
        {
            _command = (_command << 8) | value;
            if (++_offset == 4)
            {
                _haveCommand = true;
                _offset = 0;
            }

            return;
        }

        var region = _command & 0x7FFFFF00;
        if ((_command & 0x80000000) == 0)
        {
            return;
        }

        if (region == 0x20000100 && _offset < _sram.Length)
        {
            _sram[_offset] = value;
        }
        else if (region == 0x20000000 && _offset < 4)
        {
            _rtc = (_rtc & ~(0xFF000000u >> (_offset * 8))) | ((uint)value << (24 - _offset * 8));
        }

        _offset++;
    }

    private byte ReadByte(bool device)
    {
        if (!device || !_haveCommand)
        {
            return 0;
        }

        var region = _command & 0x7FFFFF00;
        var at = _offset++;
        return region switch
        {
            0x20000100 => at < _sram.Length ? _sram[at] : (byte)0,
            0x20000000 => at < 4 ? (byte)(_rtc >> (24 - at * 8)) : (byte)0,
            _ => 0, // the boot ROM isn't here
        };
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        for (var c = 0; c < 3; c++)
        {
            writer.WriteUInt32(_csr[c]);
            writer.WriteUInt32(_mar[c]);
            writer.WriteUInt32(_length[c]);
            writer.WriteUInt32(_cr[c]);
            writer.WriteUInt32(_data[c]);
        }

        writer.WriteBytes(_sram);
        writer.WriteUInt32(_command);
        writer.WriteBool(_haveCommand);
        writer.WriteInt32(_offset);
        writer.WriteUInt32(_rtc);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var c = 0; c < 3; c++)
        {
            _csr[c] = reader.ReadUInt32();
            _mar[c] = reader.ReadUInt32();
            _length[c] = reader.ReadUInt32();
            _cr[c] = reader.ReadUInt32();
            _data[c] = reader.ReadUInt32();
        }

        reader.ReadBytes(_sram);
        _command = reader.ReadUInt32();
        _haveCommand = reader.ReadBool();
        _offset = Math.Max(0, reader.ReadInt32());
        _rtc = reader.ReadUInt32();
        reader.EndChunk();
    }
}
