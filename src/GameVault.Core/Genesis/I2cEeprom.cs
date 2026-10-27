using GameVault.Core.State;

namespace GameVault.Core.Genesis;

/// <summary>How an I²C EEPROM is addressed.</summary>
public enum I2cEepromKind
{
    /// <summary>The X24C01 (128 bytes): after START, 7 address bits and the read/write bit; no device byte.</summary>
    X24C01,

    /// <summary>The 24C01–24C16: a device byte (1010, three block bits, read/write), then one address byte.</summary>
    Standard,

    /// <summary>The 24C32–24C512: a device byte, then two address bytes.</summary>
    TwoByteAddress,
}

/// <summary>
/// A serial EEPROM on a two-wire (I²C) bus, as Genesis cartridges use for saves (outstanding-work plan §7): the game
/// drives the clock (SCL) and data (SDA) lines through cartridge addresses and reads SDA back. START is SDA falling
/// while SCL is high, STOP SDA rising; bits are sampled on SCL's rising edge, MSB first; every byte is acknowledged on
/// a ninth clock. Writes go through a page buffer that wraps within the page; reads run on until the game stops
/// acknowledging. Deterministic and part of the save state.
/// </summary>
/// <remarks>
/// References: the Xicor X24C01 and Microchip 24Cxx data sheets; Genesis Plus GX's eeprom_i2c.c for the Genesis
/// boards' wiring. The X24C01's own bit order differs from the others' in its data sheet; a game reads back what it
/// wrote either way, so saves are unaffected.
/// </remarks>
public sealed class I2cEeprom : IStateful
{
    private static readonly StateTag Tag = StateTag.From("I2CE");

    private readonly byte[] _data;
    private readonly int _pageMask;

    private bool _scl = true;
    private bool _sda = true;
    private bool _sdaOut = true;
    private bool _receiving;
    private bool _sending;
    private bool _acknowledging;
    private int _bit;
    private int _shift;
    private int _stage;
    private int _address;
    private int _block;

    /// <param name="size">Bytes: 128 for the X24C01 and 24C01, up to 64 KB.</param>
    /// <param name="pageSize">Bytes one write can fill before wrapping: 4 (X24C01), 8 or 16 for most others.</param>
    public I2cEeprom(I2cEepromKind kind, int size, int pageSize)
    {
        Kind = kind;
        _data = new byte[size];
        _pageMask = pageSize - 1;
        Array.Fill(_data, (byte)0xFF);
    }

    public I2cEepromKind Kind { get; }

    /// <summary>The EEPROM's contents (the game's save).</summary>
    public Span<byte> Data => _data;

    /// <summary>The SDA line as the game reads it: low when either side pulls it low.</summary>
    public bool Sda => _sda && _sdaOut;

    /// <summary>The game sets both lines (open drain: true releases a line high).</summary>
    public void Drive(bool scl, bool sda)
    {
        if (_scl && scl && sda != _sda)
        {
            if (!sda)
            {
                Start();
            }
            else
            {
                Stop();
            }
        }
        else if (!_scl && scl)
        {
            Rising(sda);
        }
        else if (_scl && !scl)
        {
            Falling();
        }

        _scl = scl;
        _sda = sda;
    }

    private void Start()
    {
        _receiving = true;
        _sending = false;
        _acknowledging = false;
        _bit = 0;
        _shift = 0;
        _stage = 0;
        _sdaOut = true;
    }

    private void Stop()
    {
        _receiving = _sending = _acknowledging = false;
        _sdaOut = true;
    }

    private void Rising(bool sda)
    {
        if (_receiving)
        {
            if (_bit < 8)
            {
                _shift = (_shift << 1) | (sda ? 1 : 0);
                if (++_bit == 8)
                {
                    _acknowledging = Received((byte)_shift);
                    if (!_acknowledging)
                    {
                        _receiving = false; // not for us: wait for the next START
                    }
                }
            }
            else
            {
                // The ninth clock: our acknowledge was read. A read starts sending now.
                _bit = 0;
                _shift = 0;
                _acknowledging = false;
                if (_stage < 0)
                {
                    _receiving = false;
                    _sending = true;
                }
            }

            return;
        }

        if (_sending)
        {
            if (_bit < 8)
            {
                _bit++;
                return;
            }

            // The game's acknowledge: low asks for the next byte, high ends the read.
            _bit = 0;
            if (!sda)
            {
                _address = (_address + 1) % _data.Length;
            }
            else
            {
                _sending = false;
            }
        }
    }

    private void Falling()
    {
        if (_receiving)
        {
            _sdaOut = !(_bit == 8 && _acknowledging);
        }
        else if (_sending)
        {
            _sdaOut = _bit < 8 ? ((_data[_address] >> (7 - _bit)) & 1) != 0 : true;
        }
        else
        {
            _sdaOut = true;
        }
    }

    /// <summary>A whole byte arrived. Returns whether it's acknowledged; a negative stage means a read follows.</summary>
    private bool Received(byte value)
    {
        switch (Kind)
        {
            case I2cEepromKind.X24C01:
                if (_stage == 0)
                {
                    _address = (value >> 1) % _data.Length;
                    _stage = (value & 1) != 0 ? -1 : 1;
                    return true;
                }

                Store(value);
                return true;

            default:
                if (_stage == 0)
                {
                    if ((value & 0xF0) != 0xA0)
                    {
                        return false;
                    }

                    _block = (value >> 1) & 7;
                    if ((value & 1) != 0)
                    {
                        _stage = -1; // current-address read
                        return true;
                    }

                    _stage = 1;
                    return true;
                }

                if (_stage == 1)
                {
                    _address = Kind == I2cEepromKind.Standard ? ((_block << 8) | value) % _data.Length : value << 8;
                    _stage = Kind == I2cEepromKind.Standard ? 3 : 2;
                    return true;
                }

                if (_stage == 2)
                {
                    _address = (_address | value) % _data.Length;
                    _stage = 3;
                    return true;
                }

                Store(value);
                return true;
        }
    }

    private void Store(byte value)
    {
        _data[_address] = value;
        _address = (_address & ~_pageMask) | ((_address + 1) & _pageMask);
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_data);
        writer.WriteBool(_scl);
        writer.WriteBool(_sda);
        writer.WriteBool(_sdaOut);
        writer.WriteBool(_receiving);
        writer.WriteBool(_sending);
        writer.WriteBool(_acknowledging);
        writer.WriteInt32(_bit);
        writer.WriteInt32(_shift);
        writer.WriteInt32(_stage);
        writer.WriteInt32(_address);
        writer.WriteInt32(_block);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_data);
        _scl = reader.ReadBool();
        _sda = reader.ReadBool();
        _sdaOut = reader.ReadBool();
        _receiving = reader.ReadBool();
        _sending = reader.ReadBool();
        _acknowledging = reader.ReadBool();
        _bit = reader.ReadInt32() & 15;
        _shift = reader.ReadInt32() & 0xFF;
        _stage = reader.ReadInt32();
        _address = (int)((uint)reader.ReadInt32() % (uint)_data.Length);
        _block = reader.ReadInt32() & 7;
        reader.EndChunk();
    }
}
