using System.Buffers.Binary;

namespace GameVault.Core.PlayStation2;

// UNPACK (ps2tek: "VIF UNPACK"). The write-cycle logic (CL/WL skipping and filling, the mask's rows) is ported from
// Play! (BSD-2-Clause): Source/ee/Vif.h, CVif::Unpack.
public sealed partial class Ps2Vif
{
    private const int MaskData = 0;
    private const int MaskRow = 1;
    private const int MaskColumn = 2;

    private bool ExecuteUnpack()
    {
        var cmd = Command;
        var format = (int)(cmd & 0xF);
        var components = ((format >> 2) & 3) + 1;
        var width = format & 3; // 0 = 32-bit, 1 = 16-bit, 2 = 8-bit, 3 = V4-5
        var elementBytes = width == 3 ? 2 : components * (4 >> width);
        var masked = (cmd & 0x10) != 0;
        var unsigned = (Immediate & 0x4000) != 0;

        uint cl = _cycle & 0xFF;
        uint wl = (_cycle >> 8) & 0xFF;
        if (wl == 0)
        {
            // WL = 0 writes without end and reads nothing (ps2autotests: dma/vif/stcycl, CL n, WL 0).
            wl = uint.MaxValue;
            cl = 0;
        }

        var skipping = cl >= wl;
        var memory = _vu.DataMemory;
        var addressMask = (uint)(memory.Length / 16) - 1;
        Span<uint> value = stackalloc uint[4];

        while (_num > 0)
        {
            bool read;
            bool write;
            if (skipping)
            {
                read = write = _readTick < wl;
            }
            else
            {
                read = _writeTick < cl;
                write = true;
            }

            if (read)
            {
                if ((_fifoCount * 4) - _byteOffset < elementBytes)
                {
                    return false;
                }

                Decode(components, width, elementBytes, unsigned, value);
                ConsumeBytes(elementBytes);
            }
            else
            {
                value.Clear();
            }

            if (write)
            {
                var row = (int)Math.Min(_writeTick, 3);
                var offset = (int)((_unpackAddress & addressMask) * 16);
                StoreVector(memory.AsSpan(offset, 16), value, masked ? (_mask >> (row * 8)) & 0xFF : 0, row);
                _num--;
            }

            _writeTick = Math.Min(_writeTick + 1, wl);
            _readTick = Math.Min(_readTick + 1, cl);
            if (skipping ? _readTick == cl : _writeTick == wl)
            {
                _readTick = 0;
                _writeTick = 0;
            }

            _unpackAddress++;
        }

        // The data ends on a 32-bit boundary.
        if (_byteOffset != 0)
        {
            Pop();
            _byteOffset = 0;
        }

        return true;
    }

    private void StoreVector(Span<byte> destination, ReadOnlySpan<uint> value, uint rowMask, int row)
    {
        for (var i = 0; i < 4; i++)
        {
            var field = destination.Slice(i * 4, 4);
            switch ((int)(rowMask >> (i * 2)) & 3)
            {
                case MaskData:
                {
                    var v = value[i];
                    switch (_mode)
                    {
                        case 1: // offset
                            v += _row[i];
                            break;
                        case 2: // difference
                            v += _row[i];
                            _row[i] = v;
                            break;
                        case 3: // undocumented: the data is written and also kept in the row (ps2autotests: stmod)
                            _row[i] = v;
                            break;
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(field, v);
                    break;
                }

                case MaskRow:
                    BinaryPrimitives.WriteUInt32LittleEndian(field, _row[i]);
                    break;
                case MaskColumn:
                    BinaryPrimitives.WriteUInt32LittleEndian(field, _col[row]);
                    break;
            }
        }
    }

    private void Decode(int components, int width, int elementBytes, bool unsigned, Span<uint> value)
    {
        if (width == 3)
        {
            // V4-5: RGBA 5:5:5:1, each field shifted to the top of a byte (ps2tek).
            var v = (uint)(PeekByte(0) | (PeekByte(1) << 8));
            value[0] = (v & 0x1F) << 3;
            value[1] = ((v >> 5) & 0x1F) << 3;
            value[2] = ((v >> 10) & 0x1F) << 3;
            value[3] = ((v >> 15) & 1) << 7;
            return;
        }

        var size = 4 >> width;
        for (var i = 0; i < components; i++)
        {
            value[i] = ReadComponent(i * size, size, unsigned);
        }

        switch (components)
        {
            case 1:
                // S: the scalar in all four fields.
                value[1] = value[2] = value[3] = value[0];
                break;
            case 2:
                // V2: z and w repeat x and y (ps2autotests: dma/vif/unpack).
                value[2] = value[0];
                value[3] = value[1];
                break;
            case 3:
                // V3: w is "indeterminate" (ps2tek): the hardware reads a whole vector's width, so w is whatever follows
                // in the stream (0 in ps2autotests' unpack). Taken from the FIFO when it's there, else 0.
                value[3] = (_fifoCount * 4) - _byteOffset >= elementBytes + size
                    ? ReadComponent(elementBytes, size, unsigned)
                    : 0;
                break;
        }
    }

    private uint ReadComponent(int at, int size, bool unsigned)
    {
        switch (size)
        {
            case 4:
                return (uint)(PeekByte(at) | (PeekByte(at + 1) << 8) | (PeekByte(at + 2) << 16) | (PeekByte(at + 3) << 24));
            case 2:
            {
                var v = (uint)(PeekByte(at) | (PeekByte(at + 1) << 8));
                return unsigned ? v : (uint)(short)v;
            }

            default:
            {
                var v = (uint)PeekByte(at);
                return unsigned ? v : (uint)(sbyte)v;
            }
        }
    }

    private int PeekByte(int index)
    {
        var at = _byteOffset + index;
        var word = _fifo[(_fifoHead + (at >> 2)) % _fifo.Length];
        return (int)((word >> ((at & 3) * 8)) & 0xFF);
    }

    private void ConsumeBytes(int count)
    {
        _byteOffset += count;
        while (_byteOffset >= 4)
        {
            Pop();
            _byteOffset -= 4;
        }
    }
}
