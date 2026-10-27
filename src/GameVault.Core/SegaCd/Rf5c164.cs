using GameVault.Core.State;

namespace GameVault.Core.SegaCd;

/// <summary>
/// The Sega CD's RF5C164 PCM chip (sega-cd-32x.md): eight channels playing 8-bit sign-magnitude samples from 64 KB of
/// wave RAM, each with a start address, a loop address, a 16-bit step (5.11 fixed point), an envelope and a pan, at
/// one sample every 384 sub-CPU clocks (32.55 kHz). A sample of $FF marks a loop: the channel jumps to its loop start.
/// </summary>
/// <remarks>
/// Registers (the sub CPU's $FF0001–$FF001F, odd bytes): 0 ENV, 1 PAN (left in bits 0–3, right in 4–7), 2–3 the
/// step, 4–5 the loop start, 6 the start address's high byte, 7 control (bit 7 sound on, bit 6 picks a channel in
/// bits 0–2 for registers 0–6, otherwise bits 0–3 pick the wave RAM bank shown at $FF2000), 8 channel off bits.
/// $FF0021–$FF003F read each channel's current address. Behaviour follows Genesis Plus GX's pcm.c, read for behaviour.
/// </remarks>
public sealed class Rf5c164 : IStateful
{
    private static readonly StateTag Tag = StateTag.From("PCM5");

    private readonly byte[] _ram = new byte[0x10000];
    private readonly byte[] _env = new byte[8];
    private readonly byte[] _pan = new byte[8];
    private readonly ushort[] _step = new ushort[8];
    private readonly ushort[] _loop = new ushort[8];
    private readonly byte[] _start = new byte[8];
    private readonly uint[] _address = new uint[8];
    private byte _control;
    private byte _off = 0xFF;
    private int _channel;
    private int _bank;

    public Span<byte> Ram => _ram;

    /// <summary>The last sample's left and right outputs (each a signed 16-bit range).</summary>
    public int Left { get; private set; }

    public int Right { get; private set; }

    public void Reset()
    {
        Array.Clear(_env);
        Array.Clear(_pan);
        Array.Clear(_step);
        Array.Clear(_loop);
        Array.Clear(_start);
        Array.Clear(_address);
        _control = 0;
        _off = 0xFF;
        _channel = _bank = 0;
        Left = Right = 0;
    }

    public void Power()
    {
        Array.Clear(_ram);
        Reset();
    }

    /// <summary>A register write: <paramref name="index"/> is the register number (the address's bits 1–5).</summary>
    public void WriteRegister(int index, byte value)
    {
        var channel = _channel;
        switch (index)
        {
            case 0:
                _env[channel] = value;
                break;
            case 1:
                _pan[channel] = value;
                break;
            case 2:
                _step[channel] = (ushort)((_step[channel] & 0xFF00) | value);
                break;
            case 3:
                _step[channel] = (ushort)((_step[channel] & 0x00FF) | (value << 8));
                break;
            case 4:
                _loop[channel] = (ushort)((_loop[channel] & 0xFF00) | value);
                break;
            case 5:
                _loop[channel] = (ushort)((_loop[channel] & 0x00FF) | (value << 8));
                break;
            case 6:
                _start[channel] = value;
                break;
            case 7:
                // Bit 7 sounds the chip; bit 6 set: bits 0–2 pick the channel, clear: bits 0–3 pick the wave RAM bank.
                _control = value;
                if ((value & 0x40) != 0)
                {
                    _channel = value & 7;
                }
                else
                {
                    _bank = value & 0x0F;
                }

                break;
            case 8:
                for (var n = 0; n < 8; n++)
                {
                    var bit = 1 << n;
                    if ((_off & bit) != 0 && (value & bit) == 0)
                    {
                        _address[n] = (uint)_start[n] << 19; // a channel switched on starts from its start address
                    }
                }

                _off = value;
                break;
        }
    }

    /// <summary>A read of $FF0021–$FF003F: a channel's current address (byte <paramref name="index"/> 0x10–0x1F).</summary>
    public byte ReadRegister(int index)
    {
        if (index is >= 0x10 and < 0x20)
        {
            var channel = (index - 0x10) >> 1;
            var address = _address[channel] >> 11;
            return (index & 1) == 0 ? (byte)address : (byte)(address >> 8);
        }

        return 0xFF;
    }

    /// <summary>The wave RAM window at $FF2000–$FF3FFF: 4 KB of the bank the control register picks.</summary>
    public byte ReadWindow(int offset) => _ram[(_bank << 12) | (offset & 0x0FFF)];

    public void WriteWindow(int offset, byte value) => _ram[(_bank << 12) | (offset & 0x0FFF)] = value;

    /// <summary>One output sample of every channel.</summary>
    public void Sample()
    {
        int left = 0, right = 0;
        if ((_control & 0x80) != 0)
        {
            for (var n = 0; n < 8; n++)
            {
                if ((_off & (1 << n)) != 0)
                {
                    continue;
                }

                var data = _ram[(_address[n] >> 11) & 0xFFFF];
                if (data == 0xFF)
                {
                    _address[n] = (uint)_loop[n] << 11;
                    data = _ram[_loop[n]];
                    if (data == 0xFF)
                    {
                        continue; // a loop onto another loop marker stays silent
                    }
                }

                // Sign-magnitude: bit 7 set is positive.
                var magnitude = data & 0x7F;
                var sample = (data & 0x80) != 0 ? magnitude : -magnitude;
                var scaled = sample * _env[n];
                left += (scaled * (_pan[n] & 0x0F)) >> 5;
                right += (scaled * (_pan[n] >> 4)) >> 5;
                _address[n] = (_address[n] + _step[n]) & 0x7FFFFFF;
            }
        }

        Left = Math.Clamp(left, -32768, 32767);
        Right = Math.Clamp(right, -32768, 32767);
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_ram);
        writer.WriteBytes(_env);
        writer.WriteBytes(_pan);
        writer.WriteBytes(_start);
        for (var n = 0; n < 8; n++)
        {
            writer.WriteUInt16(_step[n]);
            writer.WriteUInt16(_loop[n]);
            writer.WriteUInt32(_address[n]);
        }

        writer.WriteByte(_control);
        writer.WriteByte(_off);
        writer.WriteInt32(_bank);
        writer.WriteInt32(_channel);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_ram);
        reader.ReadBytes(_env);
        reader.ReadBytes(_pan);
        reader.ReadBytes(_start);
        for (var n = 0; n < 8; n++)
        {
            _step[n] = reader.ReadUInt16();
            _loop[n] = reader.ReadUInt16();
            _address[n] = reader.ReadUInt32() & 0x7FFFFFF;
        }

        _control = reader.ReadByte();
        _off = reader.ReadByte();
        _bank = reader.ReadInt32() & 0x0F;
        _channel = reader.ReadInt32() & 7;
        reader.EndChunk();
    }
}
