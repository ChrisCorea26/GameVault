using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>
/// The SNES sound module (SPEC §7.11, Phase 14): the SPC700 at 1.024 MHz with 64 KB of RAM, three timers, four ports
/// to the 65C816, the S-DSP behind $F2/$F3, and a boot program at $FFC0–$FFFF.
/// </summary>
/// <remarks>
/// <para>
/// The boot program is GameVault's own (Nintendo's IPL ROM is copyrighted). It follows the documented upload protocol
/// that games use from the 65C816 side: it shows $AA/$BB in ports 0/1 and waits for $CC in port 0; for each block it
/// takes the address from ports 2–3 and a flag from port 1 and echoes the kick value in port 0; a non-zero flag
/// means bytes follow, each sent as data in port 1 and its index in port 0 and echoed back; a kick ahead of the index
/// starts the next block, and a zero flag jumps to the address. It clears $01–$EF and sets SP to $EF first.
/// </para>
/// <para>References: anomie's "SPC700 doc" and "S-DSP doc"; fullsnes ("SNES APU").</para>
/// </remarks>
public sealed class SnesApu : ISpc700Bus, IStateful
{
    /// <summary>GameVault's boot program, assembled from the listing in the remarks above (64 bytes at $FFC0).</summary>
    internal static readonly byte[] BootProgram =
    [
        0xCD, 0xEF,             // FFC0 MOV X,#$EF
        0xBD,                   // FFC2 MOV SP,X
        0xE8, 0x00,             // FFC3 MOV A,#$00
        0xC6,                   // FFC5 MOV (X),A      ; clear $01–$EF
        0x1D,                   // FFC6 DEC X
        0xD0, 0xFC,             // FFC7 BNE $FFC5
        0x8F, 0xAA, 0xF4,       // FFC9 MOV $F4,#$AA   ; ready
        0x8F, 0xBB, 0xF5,       // FFCC MOV $F5,#$BB
        0x78, 0xCC, 0xF4,       // FFCF CMP $F4,#$CC   ; wait for the first kick
        0xD0, 0xFB,             // FFD2 BNE $FFCF
        0xBA, 0xF6,             // FFD4 MOVW YA,$F6    ; block: address from ports 2–3
        0xDA, 0x00,             // FFD6 MOVW $00,YA
        0xF8, 0xF5,             // FFD8 MOV X,$F5      ; flag
        0xE4, 0xF4,             // FFDA MOV A,$F4      ; kick
        0xC4, 0xF4,             // FFDC MOV $F4,A      ; echo it
        0xC8, 0x00,             // FFDE CMP X,#$00
        0xF0, 0x17,             // FFE0 BEQ $FFF9      ; zero flag: run the program
        0x8D, 0x00,             // FFE2 MOV Y,#$00
        0x7E, 0xF4,             // FFE4 CMP Y,$F4      ; wait for index Y
        0xF0, 0x04,             // FFE6 BEQ $FFEC
        0x10, 0xFA,             // FFE8 BPL $FFE4      ; behind: keep waiting
        0x2F, 0xE8,             // FFEA BRA $FFD4      ; ahead: the next block
        0xE4, 0xF5,             // FFEC MOV A,$F5      ; the byte
        0xCB, 0xF4,             // FFEE MOV $F4,Y      ; echo the index
        0xD7, 0x00,             // FFF0 MOV [$00]+Y,A
        0xFC,                   // FFF2 INC Y
        0xD0, 0xEF,             // FFF3 BNE $FFE4
        0xAB, 0x01,             // FFF5 INC $01
        0x2F, 0xEB,             // FFF7 BRA $FFE4
        0x1F, 0x00, 0x00,       // FFF9 JMP [$0000+X]  ; X is 0
        0x00, 0x00,             // FFFC (unused)
        0xC0, 0xFF,             // FFFE reset vector
    ];

    private static readonly StateTag Tag = StateTag.From("SAPU");

    private readonly byte[] _ram = new byte[0x10000];
    private readonly byte[] _fromCpu = new byte[4];
    private readonly byte[] _toCpu = new byte[4];
    private readonly Timer[] _timers = [new(128), new(128), new(16)];
    private byte _control = 0x80;
    private byte _dspAddress;
    private int _cycles;

    public SnesApu()
    {
        Cpu = new Spc700(this);
        Dsp = new SnesDsp(_ram);
    }

    public Spc700 Cpu { get; }

    public SnesDsp Dsp { get; }

    public ReadOnlySpan<byte> Ram => _ram;

    public void Power()
    {
        Array.Clear(_ram);
        Array.Clear(_fromCpu);
        Array.Clear(_toCpu);
        foreach (var timer in _timers)
        {
            timer.Reset();
        }

        _control = 0x80;
        _dspAddress = 0;
        _cycles = 0;
        Dsp.Reset();
        Cpu.Reset();
    }

    public void Reset() => Power();

    // ---- The 65C816's side ($2140–$217F) ----

    public byte CpuRead(int port) => _toCpu[port & 3];

    public void CpuWrite(int port, byte value) => _fromCpu[port & 3] = value;

    /// <summary>Runs the SPC700 (and the DSP every 32 cycles) for <paramref name="cycles"/> SPC700 cycles.</summary>
    public void Run(int cycles)
    {
        _cycles += cycles;
        while (_cycles > 0)
        {
            var spent = Cpu.Step();
            _cycles -= spent;
            foreach (var timer in _timers)
            {
                timer.Run(spent);
            }

            Dsp.Run(spent);
        }
    }

    // ---- The SPC700's side ----

    public byte Read(ushort address)
    {
        if (address >= 0xFFC0 && (_control & 0x80) != 0)
        {
            return BootProgram[address - 0xFFC0];
        }

        switch (address)
        {
            case 0xF2: return _dspAddress;
            case 0xF3: return Dsp.Read(_dspAddress);
            case >= 0xF4 and <= 0xF7: return _fromCpu[address - 0xF4];
            case 0xFD: return _timers[0].ReadOutput();
            case 0xFE: return _timers[1].ReadOutput();
            case 0xFF: return _timers[2].ReadOutput();
            case 0xF0 or 0xF1 or 0xFA or 0xFB or 0xFC: return 0;
            default: return _ram[address];
        }
    }

    public void Write(ushort address, byte value)
    {
        switch (address)
        {
            case 0xF1:
                for (var i = 0; i < 3; i++)
                {
                    _timers[i].Enable((value & (1 << i)) != 0);
                }

                if ((value & 0x10) != 0)
                {
                    _fromCpu[0] = _fromCpu[1] = 0;
                }

                if ((value & 0x20) != 0)
                {
                    _fromCpu[2] = _fromCpu[3] = 0;
                }

                _control = value;
                break;
            case 0xF2:
                _dspAddress = value;
                break;
            case 0xF3:
                if (_dspAddress < 0x80)
                {
                    Dsp.Write(_dspAddress, value);
                }

                break;
            case >= 0xF4 and <= 0xF7:
                _toCpu[address - 0xF4] = value;
                break;
            case 0xFA: _timers[0].Target = value; break;
            case 0xFB: _timers[1].Target = value; break;
            case 0xFC: _timers[2].Target = value; break;
        }

        // Writes always reach RAM, including under the boot program and the I/O registers.
        _ram[address] = value;
    }

    /// <summary>A timer: an 8 kHz (or 64 kHz) tick counted to a target, which steps a 4-bit output read at $FD–$FF.</summary>
    private sealed class Timer(int period)
    {
        private bool _enabled;
        private int _divider;
        private int _stage;
        private int _output;

        public byte Target { get; set; }

        public void Reset()
        {
            _enabled = false;
            _divider = _stage = _output = 0;
            Target = 0;
        }

        public void Enable(bool on)
        {
            if (on && !_enabled)
            {
                _stage = 0;
                _output = 0;
            }

            _enabled = on;
        }

        public void Run(int cycles)
        {
            _divider += cycles;
            while (_divider >= period)
            {
                _divider -= period;
                if (!_enabled)
                {
                    continue;
                }

                if (++_stage >= (Target == 0 ? 256 : Target))
                {
                    _stage = 0;
                    _output = (_output + 1) & 0x0F;
                }
            }
        }

        public byte ReadOutput()
        {
            var value = (byte)_output;
            _output = 0;
            return value;
        }

        public void Save(StateWriter writer)
        {
            writer.WriteBool(_enabled);
            writer.WriteInt32(_divider);
            writer.WriteInt32(_stage);
            writer.WriteInt32(_output);
            writer.WriteByte(Target);
        }

        public void Load(StateReader reader)
        {
            _enabled = reader.ReadBool();
            _divider = reader.ReadInt32();
            _stage = reader.ReadInt32();
            _output = reader.ReadInt32();
            Target = reader.ReadByte();
        }
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        Cpu.SaveState(writer);
        Dsp.SaveState(writer);
        writer.WriteBytes(_ram);
        writer.WriteBytes(_fromCpu);
        writer.WriteBytes(_toCpu);
        foreach (var timer in _timers)
        {
            timer.Save(writer);
        }

        writer.WriteByte(_control);
        writer.WriteByte(_dspAddress);
        writer.WriteInt32(_cycles);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Cpu.LoadState(reader);
        Dsp.LoadState(reader);
        reader.ReadBytes(_ram);
        reader.ReadBytes(_fromCpu);
        reader.ReadBytes(_toCpu);
        foreach (var timer in _timers)
        {
            timer.Load(reader);
        }

        _control = reader.ReadByte();
        _dspAddress = reader.ReadByte();
        _cycles = reader.ReadInt32();
        reader.EndChunk();
    }
}
