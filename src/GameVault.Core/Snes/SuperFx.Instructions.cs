namespace GameVault.Core.Snes;

// The GSU's instructions (fullsnes "SNES Cart GSU-n CPU Instructions"). ALT1/ALT2 (set by the ALT prefixes) pick an
// instruction's variant; TO/FROM/WITH pick the destination and source registers (Dreg/Sreg, R0 by default). Every
// instruction but the prefixes and the branches clears them afterwards.
public sealed partial class SuperFx
{
    private ushort Source => _r[_sreg];

    /// <summary>Writes a register; R14 refills the ROM buffer and R15 is a jump (the pipeline's byte still runs first).</summary>
    private void SetRegister(int n, int value)
    {
        _r[n] = (ushort)value;
        if (n == 14)
        {
            _r14Modified = true;
        }
        else if (n == 15)
        {
            _r15Modified = true;
        }
    }

    private void SetDest(int value) => SetRegister(_dreg, value);

    private void SetSignZero(ushort value)
    {
        _s = (value & 0x8000) != 0;
        _z = value == 0;
    }

    /// <summary>Ends an instruction: the prefixes and register selections are used up.</summary>
    private void Done()
    {
        _b = false;
        _alt1 = false;
        _alt2 = false;
        _sreg = 0;
        _dreg = 0;
    }

    private void Execute(byte op)
    {
        var n = op & 15;
        switch (op >> 4)
        {
            case 0x0:
                ExecuteControl(op);
                return;
            case 0x1: // TO Rn / MOVE
                if (!_b)
                {
                    _dreg = n;
                    return;
                }

                SetRegister(n, Source);
                Done();
                return;
            case 0x2: // WITH Rn
                _sreg = _dreg = n;
                _b = true;
                return;
            case 0x3:
                if (n <= 11) // STW (Rn) / STB (Rn)
                {
                    _ramAddress = _r[n];
                    WriteRam(_ramAddress, (byte)Source);
                    if (!_alt1)
                    {
                        WriteRam((ushort)(_ramAddress ^ 1), (byte)(Source >> 8));
                    }

                    Done();
                    return;
                }

                switch (n)
                {
                    case 12: // LOOP
                        _r[12]--;
                        SetSignZero(_r[12]);
                        if (!_z)
                        {
                            SetRegister(15, _r[13]);
                        }

                        Done();
                        return;
                    case 13: // ALT1
                        _b = false;
                        _alt1 = true;
                        return;
                    case 14: // ALT2
                        _b = false;
                        _alt2 = true;
                        return;
                    default: // ALT3
                        _b = false;
                        _alt1 = _alt2 = true;
                        return;
                }

            case 0x4:
                if (n <= 11) // LDW (Rn) / LDB (Rn)
                {
                    _ramAddress = _r[n];
                    if (!_alt1)
                    {
                        var low = ReadRam(_ramAddress);
                        SetDest(low | (ReadRam((ushort)(_ramAddress ^ 1)) << 8));
                    }
                    else
                    {
                        SetDest(ReadRam(_ramAddress));
                    }

                    Done();
                    return;
                }

                switch (n)
                {
                    case 12:
                        if (!_alt1) // PLOT
                        {
                            Plot((byte)_r[1], (byte)_r[2]);
                            _r[1]++;
                        }
                        else // RPIX
                        {
                            var pixel = ReadPixel((byte)_r[1], (byte)_r[2]);
                            SetDest(pixel);
                            SetSignZero(pixel);
                        }

                        Done();
                        return;
                    case 13: // SWAP
                    {
                        var value = (ushort)((Source >> 8) | (Source << 8));
                        SetDest(value);
                        SetSignZero(value);
                        Done();
                        return;
                    }

                    case 14:
                        if (!_alt1) // COLOR
                        {
                            _colr = Color((byte)Source);
                        }
                        else // CMODE
                        {
                            _por = (byte)Source;
                        }

                        Done();
                        return;
                    default: // NOT
                    {
                        var value = (ushort)~Source;
                        SetDest(value);
                        SetSignZero(value);
                        Done();
                        return;
                    }
                }

            case 0x5: // ADD / ADC / ADD #n / ADC #n
            {
                var operand = _alt2 ? n : _r[n];
                var source = Source;
                var r = source + operand + (_alt1 && _cy ? 1 : 0);
                _ov = (~(source ^ operand) & (operand ^ r) & 0x8000) != 0;
                _s = (r & 0x8000) != 0;
                _cy = r >= 0x10000;
                _z = (ushort)r == 0;
                SetDest(r);
                Done();
                return;
            }

            case 0x6: // SUB / SBC / SUB #n / CMP
            {
                var operand = _alt2 && !_alt1 ? n : _r[n];
                var source = Source;
                var r = source - operand - (!_alt2 && _alt1 && !_cy ? 1 : 0);
                _ov = ((source ^ operand) & (source ^ r) & 0x8000) != 0;
                _s = (r & 0x8000) != 0;
                _cy = r >= 0;
                _z = (ushort)r == 0;
                if (!(_alt1 && _alt2))
                {
                    SetDest(r);
                }

                Done();
                return;
            }

            case 0x7:
                if (n == 0) // MERGE
                {
                    var value = (ushort)((_r[7] & 0xFF00) | (_r[8] >> 8));
                    SetDest(value);
                    _ov = (value & 0xC0C0) != 0;
                    _s = (value & 0x8080) != 0;
                    _cy = (value & 0xE0E0) != 0;
                    _z = (value & 0xF0F0) != 0;
                    Done();
                    return;
                }
                else // AND / BIC / AND #n / BIC #n
                {
                    var operand = _alt2 ? n : _r[n];
                    var value = (ushort)(Source & (_alt1 ? ~operand : operand));
                    SetDest(value);
                    SetSignZero(value);
                    Done();
                    return;
                }

            case 0x8: // MULT / UMULT / MULT #n / UMULT #n
            {
                var operand = _alt2 ? n : _r[n];
                var value = (ushort)(_alt1 ? (byte)Source * (byte)operand : (sbyte)Source * (sbyte)operand);
                SetDest(value);
                SetSignZero(value);
                Done();
                if ((_cfgr & 0x20) == 0)
                {
                    Step(1, 2); // the slow multiplier
                }

                return;
            }

            case 0x9:
                ExecuteGroup9(n);
                return;
            case 0xA:
                if (!_alt1 && !_alt2) // IBT Rn, #pp
                {
                    SetRegister(n, (sbyte)Operand());
                }
                else if (_alt1 && !_alt2) // LMS Rn, (yy)
                {
                    _ramAddress = (ushort)(Operand() << 1);
                    var low = ReadRam(_ramAddress);
                    SetRegister(n, low | (ReadRam((ushort)(_ramAddress ^ 1)) << 8));
                }
                else // SMS (yy), Rn
                {
                    _ramAddress = (ushort)(Operand() << 1);
                    WriteRam(_ramAddress, (byte)_r[n]);
                    WriteRam((ushort)(_ramAddress ^ 1), (byte)(_r[n] >> 8));
                }

                Done();
                return;
            case 0xB: // FROM Rn / MOVES
                if (!_b)
                {
                    _sreg = n;
                    return;
                }

                {
                    var value = _r[n];
                    SetDest(value);
                    _ov = (value & 0x80) != 0;
                    SetSignZero(value);
                    Done();
                    return;
                }

            case 0xC:
                if (n == 0) // HIB
                {
                    var value = (ushort)(Source >> 8);
                    SetDest(value);
                    _s = (value & 0x80) != 0;
                    _z = value == 0;
                }
                else // OR / XOR / OR #n / XOR #n
                {
                    var operand = _alt2 ? n : _r[n];
                    var value = (ushort)(_alt1 ? Source ^ operand : Source | operand);
                    SetDest(value);
                    SetSignZero(value);
                }

                Done();
                return;
            case 0xD:
                if (n < 15) // INC Rn
                {
                    SetRegister(n, _r[n] + 1);
                    SetSignZero(_r[n]);
                }
                else if (!_alt2) // GETC
                {
                    _colr = Color(_romBuffer);
                }
                else if (!_alt1) // RAMB
                {
                    _rambr = (byte)(Source & 1);
                }
                else // ROMB
                {
                    _rombr = (byte)(Source & 0x7F);
                }

                Done();
                return;
            case 0xE:
                if (n < 15) // DEC Rn
                {
                    SetRegister(n, _r[n] - 1);
                    SetSignZero(_r[n]);
                }
                else // GETB / GETBH / GETBL / GETBS
                {
                    var value = (_alt1, _alt2) switch
                    {
                        (false, false) => _romBuffer,
                        (true, false) => (_romBuffer << 8) | (Source & 0xFF),
                        (false, true) => (Source & 0xFF00) | _romBuffer,
                        _ => (sbyte)_romBuffer,
                    };
                    SetDest(value);
                }

                Done();
                return;
            default:
                if (!_alt1 && !_alt2) // IWT Rn, #xx
                {
                    var low = Operand();
                    SetRegister(n, low | (Operand() << 8));
                }
                else if (_alt1 && !_alt2) // LM Rn, (xx)
                {
                    var low = Operand();
                    _ramAddress = (ushort)(low | (Operand() << 8));
                    var value = ReadRam(_ramAddress);
                    SetRegister(n, value | (ReadRam((ushort)(_ramAddress ^ 1)) << 8));
                }
                else // SM (xx), Rn
                {
                    var low = Operand();
                    _ramAddress = (ushort)(low | (Operand() << 8));
                    WriteRam(_ramAddress, (byte)_r[n]);
                    WriteRam((ushort)(_ramAddress ^ 1), (byte)(_r[n] >> 8));
                }

                Done();
                return;
        }
    }

    /// <summary>$00–$0F: STOP, NOP, CACHE, LSR, ROL and the branches.</summary>
    private void ExecuteControl(byte op)
    {
        switch (op)
        {
            case 0x00: // STOP
                if ((_cfgr & 0x80) == 0)
                {
                    _irq = true; // unless CFGR masks it
                }

                _go = false;
                _pipeline = 0x01; // a restart runs a NOP first
                Done();
                return;
            case 0x01: // NOP
                Done();
                return;
            case 0x02: // CACHE
                if (_cbr != (_r[15] & 0xFFF0))
                {
                    _cbr = (ushort)(_r[15] & 0xFFF0);
                    FlushCache();
                }

                Done();
                return;
            case 0x03: // LSR
            {
                _cy = (Source & 1) != 0;
                var value = (ushort)(Source >> 1);
                SetDest(value);
                SetSignZero(value);
                Done();
                return;
            }

            case 0x04: // ROL
            {
                var carry = (Source & 0x8000) != 0;
                var value = (ushort)((Source << 1) | (_cy ? 1 : 0));
                SetDest(value);
                SetSignZero(value);
                _cy = carry;
                Done();
                return;
            }

            default:
            {
                var displacement = (sbyte)Operand();
                var take = op switch
                {
                    0x05 => true, // BRA
                    0x06 => _s == _ov, // BGE
                    0x07 => _s != _ov, // BLT
                    0x08 => !_z, // BNE
                    0x09 => _z, // BEQ
                    0x0A => !_s, // BPL
                    0x0B => _s, // BMI
                    0x0C => !_cy, // BCC
                    0x0D => _cy, // BCS
                    0x0E => !_ov, // BVC
                    _ => _ov, // BVS
                };
                if (take)
                {
                    SetRegister(15, _r[15] + displacement);
                }

                return;
            }
        }
    }

    /// <summary>$90–$9F: SBK, LINK, SEX, ASR/DIV2, ROR, JMP/LJMP, LOB and FMULT/LMULT.</summary>
    private void ExecuteGroup9(int n)
    {
        switch (n)
        {
            case 0: // SBK
                WriteRam(_ramAddress, (byte)Source);
                WriteRam((ushort)(_ramAddress ^ 1), (byte)(Source >> 8));
                break;
            case 1 or 2 or 3 or 4: // LINK #n
                SetRegister(11, _r[15] + n);
                break;
            case 5: // SEX
            {
                var value = (ushort)(sbyte)Source;
                SetDest(value);
                SetSignZero(value);
                break;
            }

            case 6: // ASR / DIV2 (DIV2 of −1 is 0)
            {
                _cy = (Source & 1) != 0;
                var value = (ushort)(((short)Source >> 1) + (_alt1 ? (Source + 1) >> 16 : 0));
                SetDest(value);
                SetSignZero(value);
                break;
            }

            case 7: // ROR
            {
                var carry = (Source & 1) != 0;
                var value = (ushort)(((_cy ? 1 : 0) << 15) | (Source >> 1));
                SetDest(value);
                SetSignZero(value);
                _cy = carry;
                break;
            }

            case >= 8 and <= 13:
                if (!_alt1) // JMP Rn
                {
                    SetRegister(15, _r[n]);
                }
                else // LJMP Rn: bank from Rn, address from Sreg
                {
                    _pbr = (byte)(_r[n] & 0x7F);
                    SetRegister(15, Source);
                    _cbr = (ushort)(_r[15] & 0xFFF0);
                    FlushCache();
                }

                break;
            case 14: // LOB
            {
                var value = (ushort)(Source & 0xFF);
                SetDest(value);
                _s = (value & 0x80) != 0;
                _z = value == 0;
                break;
            }

            default: // FMULT / LMULT
            {
                var result = (short)Source * (short)_r[6];
                if (_alt1)
                {
                    SetRegister(4, result);
                }

                var high = (ushort)(result >> 16);
                SetDest(high);
                _s = (result & 0x80000000) != 0;
                _cy = (result & 0x8000) != 0;
                _z = high == 0;
                var cycles = (_cfgr & 0x20) != 0 ? 3 : 7; // the fast or slow multiplier
                Step(cycles, cycles * 2);
                break;
            }
        }

        Done();
    }

    // ---- Pixels ----

    /// <summary>COLOR and GETC through CMODE's high-nibble and freeze-high options.</summary>
    private byte Color(byte source)
    {
        if ((_por & 0x04) != 0)
        {
            return (byte)((_colr & 0xF0) | (source >> 4));
        }

        if ((_por & 0x08) != 0)
        {
            return (byte)((_colr & 0xF0) | (source & 0x0F));
        }

        return source;
    }

    private int ColorDepthMode => _scmr & 3;

    /// <summary>Bits per pixel: 2, 4 or 8 (mode 2 is 4 bpp too).</summary>
    private int BitsPerPixel => 2 << (ColorDepthMode - (ColorDepthMode >> 1));

    private void Plot(byte x, byte y)
    {
        var color = _colr;
        var mode = ColorDepthMode;
        if ((_por & 0x02) != 0 && mode != 3)
        {
            // Dither: odd pixels take the high nibble.
            if (((x ^ y) & 1) != 0)
            {
                color >>= 4;
            }

            color &= 0x0F;
        }

        if ((_por & 0x01) == 0)
        {
            // Transparent pixels (colour 0, or 0 in the low nibble) aren't drawn.
            var visible = mode == 3 ? ((_por & 0x08) != 0 ? (color & 0x0F) != 0 : color != 0) : (color & 0x0F) != 0;
            if (!visible)
            {
                return;
            }
        }

        var offset = (ushort)((y << 5) + (x >> 3));
        var current = _pixels[0];
        if (offset != current.Offset)
        {
            FlushPixels(_pixels[1]);
            _pixels[1].CopyFrom(current);
            current.Pending = 0;
            current.Offset = offset;
        }

        var bit = (x & 7) ^ 7;
        current.Data[bit] = color;
        current.Pending |= (byte)(1 << bit);
        if (current.Pending == 0xFF)
        {
            FlushPixels(_pixels[1]);
            _pixels[1].CopyFrom(current);
            current.Pending = 0;
        }
    }

    private byte ReadPixel(byte x, byte y)
    {
        FlushPixels(_pixels[1]);
        FlushPixels(_pixels[0]);
        var address = CharacterRowAddress(x, y);
        var bit = (x & 7) ^ 7;
        var value = 0;
        for (var plane = 0; plane < BitsPerPixel; plane++)
        {
            Step(5, 6);
            value |= ((_ram[(address + PlaneOffset(plane)) & _ramMask] >> bit) & 1) << plane;
        }

        return (byte)value;
    }

    private void FlushPixels(PixelCache cache)
    {
        if (cache.Pending == 0)
        {
            return;
        }

        var x = (byte)(cache.Offset << 3);
        var y = (byte)(cache.Offset >> 5);
        var address = CharacterRowAddress(x, y);
        for (var plane = 0; plane < BitsPerPixel; plane++)
        {
            var at = (address + PlaneOffset(plane)) & _ramMask;
            var data = 0;
            for (var i = 0; i < 8; i++)
            {
                data |= ((cache.Data[i] >> plane) & 1) << i;
            }

            if (cache.Pending != 0xFF)
            {
                Step(5, 6);
                data = (data & cache.Pending) | (_ram[at] & ~cache.Pending);
            }

            Step(5, 6);
            _ram[at] = (byte)data;
        }

        cache.Pending = 0;
    }

    /// <summary>Bit planes come in pairs 16 bytes apart: planes 0–1 at +0/+1, 2–3 at +16/+17, and so on.</summary>
    private static int PlaneOffset(int plane) => ((plane >> 1) << 4) + (plane & 1);

    /// <summary>The RAM address of a pixel's character row: the character from SCMR's height (or OBJ mode), then SCBR.</summary>
    private int CharacterRowAddress(byte x, byte y)
    {
        var height = (_por & 0x10) != 0 ? 3 : ((_scmr >> 2) & 1) | ((_scmr >> 4) & 2);
        var character = height switch
        {
            0 => ((x & 0xF8) << 1) + ((y & 0xF8) >> 3), // 128 lines
            1 => ((x & 0xF8) << 1) + ((x & 0xF8) >> 1) + ((y & 0xF8) >> 3), // 160
            2 => ((x & 0xF8) << 1) + (x & 0xF8) + ((y & 0xF8) >> 3), // 192
            _ => ((y & 0x80) << 2) + ((x & 0x80) << 1) + ((y & 0x78) << 1) + ((x & 0x78) >> 3), // OBJ
        };
        return character * (BitsPerPixel << 3) + (_scbr << 10) + ((y & 7) * 2);
    }
}
