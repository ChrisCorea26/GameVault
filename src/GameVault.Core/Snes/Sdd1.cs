using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>
/// The S-DD1 (outstanding-work plan §5), Star Ocean's and Street Fighter Alpha 2's: a memory controller that maps
/// any megabyte of ROM into each of $C0–$CF, $D0–$DF, $E0–$EF and $F0–$FF ($4804–$4807), and a decompressor that
/// sits on the DMA. When a channel enabled in both $4800 and $4801 reads its source address in $C0–$FF, the bytes
/// come out of the decompressor instead of the ROM, until the channel's count runs out (whether the DMA's address
/// stays fixed or counts up).
/// </summary>
/// <remarks>
/// The decompressor is Andreas Naive's description of the chip (2003), as in every emulator since: a Golomb-code
/// decoder feeding eight run-length bit generators, a 33-state probability estimator over 32 contexts, and the
/// bitplane context model and output logic. Reference: the SNESdev Wiki "S-DD1" page.
/// </remarks>
public sealed class Sdd1 : ISnesCoprocessor
{
    private static readonly StateTag Tag = StateTag.From("SDD1");

    private readonly byte[] _rom;
    private readonly int[] _dmaAddress = new int[8];
    private readonly int[] _dmaSize = new int[8];
    private readonly int[] _dmaNext = new int[8];
    private readonly Decompressor _decompressor;
    private byte _r4800;
    private byte _r4801;
    private readonly byte[] _mmc = new byte[4];
    private bool _streaming;

    public Sdd1(byte[] rom)
    {
        _rom = rom;
        _decompressor = new Decompressor(this);
    }

    public string Name => "S-DD1";

    public bool Irq => false;

    public bool Claims(uint address)
    {
        var bank = address >> 16;
        return bank >= 0xC0 || ((bank & 0x40) == 0 && (address & 0xFFF8) == 0x4800);
    }

    public byte Read(uint address, byte openBus)
    {
        if (address >> 16 < 0xC0)
        {
            return (address & 7) switch
            {
                0 => _r4800,
                1 => _r4801,
                >= 4 and var n => _mmc[n - 4],
                _ => openBus,
            };
        }

        var enabled = _r4800 & _r4801;
        if (enabled != 0)
        {
            for (var n = 0; n < 8; n++)
            {
                // The channel's source address, or (for a DMA that counts up) the byte after its last.
                if ((enabled & (1 << n)) != 0 && (address == (uint)_dmaAddress[n] || (_streaming && address == (uint)_dmaNext[n])))
                {
                    if (!_streaming)
                    {
                        _decompressor.Init((int)address);
                        _streaming = true;
                    }

                    _dmaNext[n] = (int)((address & 0xFF0000) | ((address + 1) & 0xFFFF));

                    var value = _decompressor.Read();
                    _dmaSize[n] = (_dmaSize[n] - 1) & 0xFFFF;
                    if (_dmaSize[n] == 0)
                    {
                        _streaming = false;
                        _r4801 &= (byte)~(1 << n);
                    }

                    return value;
                }
            }
        }

        return MmcRead((int)address);
    }

    public void Write(uint address, byte value)
    {
        if (address >> 16 >= 0xC0)
        {
            return;
        }

        switch (address & 7)
        {
            case 0: _r4800 = value; break;
            case 1: _r4801 = value; break;
            case >= 4 and var n: _mmc[n - 4] = (byte)(value & 0x8F); break;
        }
    }

    /// <summary>The console shows the chip each write to the DMA registers ($43x2–$43x6): it watches the source and count.</summary>
    public void SnoopDma(int offset, byte value)
    {
        var channel = (offset >> 4) & 7;
        switch (offset & 0xF)
        {
            case 2: _dmaAddress[channel] = (_dmaAddress[channel] & 0xFFFF00) | value; break;
            case 3: _dmaAddress[channel] = (_dmaAddress[channel] & 0xFF00FF) | value << 8; break;
            case 4: _dmaAddress[channel] = (_dmaAddress[channel] & 0x00FFFF) | value << 16; break;
            case 5: _dmaSize[channel] = (_dmaSize[channel] & 0xFF00) | value; break;
            case 6: _dmaSize[channel] = (_dmaSize[channel] & 0x00FF) | value << 8; break;
        }
    }

    internal byte MmcRead(int address) =>
        _rom[(((_mmc[(address >> 20) & 3] & 0x0F) << 20) | (address & 0xFFFFF)) % _rom.Length];

    public void CatchUp(long clock)
    {
    }

    public void EndFrame(long frameClocks)
    {
    }

    public void Power() => Reset();

    public void Reset()
    {
        _r4800 = _r4801 = 0;
        for (var i = 0; i < 4; i++)
        {
            _mmc[i] = (byte)i;
        }

        Array.Clear(_dmaAddress);
        Array.Clear(_dmaSize);
        Array.Clear(_dmaNext);
        _streaming = false;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteByte(_r4800);
        writer.WriteByte(_r4801);
        writer.WriteBytes(_mmc);
        for (var i = 0; i < 8; i++)
        {
            writer.WriteInt32(_dmaAddress[i]);
            writer.WriteInt32(_dmaSize[i]);
            writer.WriteInt32(_dmaNext[i]);
        }

        writer.WriteBool(_streaming);
        _decompressor.Save(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        _r4800 = reader.ReadByte();
        _r4801 = reader.ReadByte();
        reader.ReadBytes(_mmc);
        for (var i = 0; i < 8; i++)
        {
            _dmaAddress[i] = reader.ReadInt32() & 0xFFFFFF;
            _dmaSize[i] = reader.ReadInt32() & 0xFFFF;
            _dmaNext[i] = reader.ReadInt32() & 0xFFFFFF;
        }

        _streaming = reader.ReadBool();
        _decompressor.Load(reader);
        reader.EndChunk();
    }

    /// <summary>The decompressor: input, Golomb decoding, bit generators, probability estimation, context model, output.</summary>
    internal sealed class Decompressor(Sdd1 chip)
    {
        // { code number, next state after an MPS run, next state after an LPS }
        private static readonly byte[] Evolution =
        [
            0, 25, 25, 0, 2, 1, 0, 3, 1, 0, 4, 2, 0, 5, 3, 1, 6, 4, 1, 7, 5, 1, 8, 6,
            1, 9, 7, 2, 10, 8, 2, 11, 9, 2, 12, 10, 2, 13, 11, 3, 14, 12, 3, 15, 13, 3, 16, 14,
            3, 17, 15, 4, 18, 16, 4, 19, 17, 5, 20, 18, 5, 21, 19, 6, 22, 20, 6, 23, 21, 7, 24, 22,
            7, 24, 23, 0, 26, 1, 1, 27, 2, 2, 28, 4, 3, 29, 8, 4, 30, 12, 5, 31, 16, 6, 32, 18,
            7, 24, 22,
        ];

        /// <summary>The MPS run length a Golomb code word means: its bits after the leading 1, reversed and inverted.</summary>
        private static readonly byte[] RunCount = BuildRunCount();

        // Input manager.
        private int _offset;
        private int _bitCount;

        // Bit generators.
        private readonly byte[] _mpsCount = new byte[8];
        private readonly bool[] _lpsIndex = new bool[8];

        // Probability estimation.
        private readonly byte[] _status = new byte[32];
        private readonly byte[] _mps = new byte[32];

        // Context model.
        private int _bitplanesInfo;
        private int _contextBitsInfo;
        private int _bitNumber;
        private int _currentBitplane;
        private readonly ushort[] _previousBits = new ushort[8];

        // Output logic.
        private byte _r0;
        private byte _r1;
        private byte _r2;

        private static byte[] BuildRunCount()
        {
            var table = new byte[256];
            for (var i = 1; i < 256; i++)
            {
                var n = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)i); // bits after the leading 1
                var reversed = 0;
                for (var b = 0; b < n; b++)
                {
                    reversed |= ((i >> b) & 1) << (n - 1 - b);
                }

                table[i] = (byte)((1 << n) - 1 - reversed);
            }

            return table;
        }

        public void Init(int offset)
        {
            _offset = offset;
            _bitCount = 4;
            Array.Clear(_mpsCount);
            Array.Clear(_lpsIndex);
            Array.Clear(_status);
            Array.Clear(_mps);
            var header = chip.MmcRead(offset);
            _bitplanesInfo = header & 0xC0;
            _contextBitsInfo = header & 0x30;
            _bitNumber = 0;
            Array.Clear(_previousBits);
            _currentBitplane = _bitplanesInfo switch
            {
                0x00 => 1,
                0x40 => 7,
                0x80 => 3,
                _ => 0,
            };
            _r0 = 0x01;
            _r1 = _r2 = 0;
        }

        public byte Read()
        {
            if (_bitplanesInfo == 0xC0)
            {
                _r1 = 0;
                for (var bit = 1; bit < 0x100; bit <<= 1)
                {
                    if (ContextBit())
                    {
                        _r1 |= (byte)bit;
                    }
                }

                return _r1;
            }

            if (_r0 == 0)
            {
                _r0 = 0xFF;
                return _r2;
            }

            _r1 = _r2 = 0;
            for (_r0 = 0x80; _r0 != 0; _r0 >>= 1)
            {
                if (ContextBit())
                {
                    _r1 |= _r0;
                }

                if (ContextBit())
                {
                    _r2 |= _r0;
                }
            }

            return _r1;
        }

        private bool ContextBit()
        {
            switch (_bitplanesInfo)
            {
                case 0x00:
                    _currentBitplane ^= 1;
                    break;
                case 0x40:
                    _currentBitplane ^= 1;
                    if ((_bitNumber & 0x7F) == 0)
                    {
                        _currentBitplane = (_currentBitplane + 2) & 7;
                    }

                    break;
                case 0x80:
                    _currentBitplane ^= 1;
                    if ((_bitNumber & 0x7F) == 0)
                    {
                        _currentBitplane ^= 2;
                    }

                    break;
                default:
                    _currentBitplane = _bitNumber & 7;
                    break;
            }

            ref var bits = ref _previousBits[_currentBitplane];
            var context = (_currentBitplane & 1) << 4;
            context |= _contextBitsInfo switch
            {
                0x00 => ((bits & 0x01C0) >> 5) | (bits & 1),
                0x10 => ((bits & 0x0180) >> 5) | (bits & 1),
                0x20 => ((bits & 0x00C0) >> 5) | (bits & 1),
                _ => ((bits & 0x0180) >> 5) | (bits & 3),
            };
            var bit = EstimatedBit(context);
            bits = (ushort)((bits << 1) | bit);
            _bitNumber++;
            return bit != 0;
        }

        private int EstimatedBit(int context)
        {
            var status = _status[context];
            var mps = _mps[context];
            var code = Evolution[status * 3];
            var bit = GeneratorBit(code, out var endOfRun);
            if (endOfRun)
            {
                if (bit != 0)
                {
                    if ((status & 0xFE) == 0)
                    {
                        _mps[context] ^= 1;
                    }

                    _status[context] = Evolution[status * 3 + 2];
                }
                else
                {
                    _status[context] = Evolution[status * 3 + 1];
                }
            }

            return bit ^ mps;
        }

        private int GeneratorBit(int code, out bool endOfRun)
        {
            if (_mpsCount[code] == 0 && !_lpsIndex[code])
            {
                var word = CodeWord(code);
                if ((word & 0x80) != 0)
                {
                    _lpsIndex[code] = true;
                    _mpsCount[code] = RunCount[word >> (code ^ 7)];
                }
                else
                {
                    _mpsCount[code] = (byte)(1 << code);
                }
            }

            int bit;
            if (_mpsCount[code] != 0)
            {
                bit = 0;
                _mpsCount[code]--;
            }
            else
            {
                bit = 1;
                _lpsIndex[code] = false;
            }

            endOfRun = _mpsCount[code] == 0 && !_lpsIndex[code];
            return bit;
        }

        private int CodeWord(int length)
        {
            var word = (chip.MmcRead(_offset) << _bitCount) & 0xFF;
            _bitCount++;
            if ((word & 0x80) != 0)
            {
                word |= chip.MmcRead((_offset + 1) & 0xFFFFFF) >> (9 - _bitCount);
                _bitCount += length;
            }

            if ((_bitCount & 8) != 0)
            {
                _offset = (_offset + 1) & 0xFFFFFF;
                _bitCount &= 7;
            }

            return word & 0xFF;
        }

        public void Save(StateWriter writer)
        {
            writer.WriteInt32(_offset);
            writer.WriteInt32(_bitCount);
            writer.WriteBytes(_mpsCount);
            for (var i = 0; i < 8; i++)
            {
                writer.WriteBool(_lpsIndex[i]);
                writer.WriteUInt16(_previousBits[i]);
            }

            writer.WriteBytes(_status);
            writer.WriteBytes(_mps);
            writer.WriteInt32(_bitplanesInfo);
            writer.WriteInt32(_contextBitsInfo);
            writer.WriteInt32(_bitNumber);
            writer.WriteInt32(_currentBitplane);
            writer.WriteByte(_r0);
            writer.WriteByte(_r1);
            writer.WriteByte(_r2);
        }

        public void Load(StateReader reader)
        {
            _offset = reader.ReadInt32() & 0xFFFFFF;
            _bitCount = reader.ReadInt32() & 7;
            reader.ReadBytes(_mpsCount);
            for (var i = 0; i < 8; i++)
            {
                _lpsIndex[i] = reader.ReadBool();
                _previousBits[i] = reader.ReadUInt16();
            }

            reader.ReadBytes(_status);
            for (var i = 0; i < 32; i++)
            {
                _status[i] = (byte)Math.Min((int)_status[i], 32);
            }

            reader.ReadBytes(_mps);
            _bitplanesInfo = reader.ReadInt32() & 0xC0;
            _contextBitsInfo = reader.ReadInt32() & 0x30;
            _bitNumber = reader.ReadInt32();
            _currentBitplane = reader.ReadInt32() & 7;
            _r0 = reader.ReadByte();
            _r1 = reader.ReadByte();
            _r2 = reader.ReadByte();
        }
    }
}
