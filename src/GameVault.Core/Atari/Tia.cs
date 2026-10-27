using System.Runtime.CompilerServices;
using GameVault.Core.Audio;
using GameVault.Core.State;

namespace GameVault.Core.Atari;

/// <summary>
/// The TIA (phase-19.md): the 2600's picture, drawn one colour clock at a time from its registers at that moment, its
/// collision latches, the fire buttons, and its two sound channels. A line is 228 colour clocks: 68 of horizontal
/// blank, then 160 visible pixels. The CPU runs one cycle every three colour clocks.
/// </summary>
/// <remarks>
/// References: Steve Wright's "Stella Programmer's Guide" (1979) for the registers, the playfield and object layout
/// and the priorities; Andrew Towers' "TIA Hardware Notes" for object positioning after RESPx and HMOVE; Ron Fries'
/// TIASound for the audio counters. Positions follow the documented rules (RESPx during horizontal blank puts a
/// player at pixel 3 and a missile or the ball at 2; otherwise 5 and 4 pixels after the beam), and HMOVE moves the
/// objects at once and blanks the line's first 8 pixels when it comes during horizontal blank.
/// </remarks>
public sealed class Tia : IStateful
{
    public const int ClocksPerLine = 228;
    public const int HBlankClocks = 68;
    public const int Width = 160;

    /// <summary>Lines shown of an NTSC frame, and of a PAL one (more than 290 lines).</summary>
    public const int NtscHeight = 210;
    public const int PalHeight = 250;
    public const int MaxHeight = PalHeight;

    /// <summary>A frame that never ends VSYNC is cut off after this many lines.</summary>
    public const int MaxLines = 400;

    private static readonly StateTag Tag = StateTag.From("TIA ");

    // Copies of a player or missile (NUSIZ bits 0–2), as pixel offsets from its position.
    private static readonly int[][] Copies = [[0], [0, 16], [0, 32], [0, 16, 32], [0, 64], [0], [0, 32, 64], [0]];

    private readonly byte[] _frame = new byte[Width * MaxHeight];

    // Registers.
    private byte _vsync;
    private byte _vblank;
    private readonly byte[] _nusiz = new byte[2];
    private readonly byte[] _colup = new byte[2];
    private byte _colupf;
    private byte _colubk;
    private byte _ctrlpf;
    private readonly bool[] _refp = new bool[2];
    private byte _pf0;
    private byte _pf1;
    private byte _pf2;
    private readonly byte[] _grp = new byte[2];
    private readonly byte[] _grpOld = new byte[2];
    private readonly bool[] _enam = new bool[2];
    private bool _enabl;
    private bool _enablOld;
    private readonly sbyte[] _hmp = new sbyte[2];
    private readonly sbyte[] _hmm = new sbyte[2];
    private sbyte _hmbl;
    private readonly bool[] _vdelp = new bool[2];
    private bool _vdelbl;
    private readonly bool[] _resmp = new bool[2];

    // Object positions (the visible pixel each starts at).
    private readonly int[] _posP = new int[2];
    private readonly int[] _posM = new int[2];
    private int _posBl;

    private ushort _collisions;
    private bool _hmoveBlank;

    // The beam.
    private int _clock;
    private int _line;
    private bool _sawVsync;

    // Where the picture starts (the first line with VBLANK off), measured on one frame and used on the next.
    private int _top = 37;
    private int _topSeen = -1;
    private int _height = NtscHeight;

    // Inputs: INPT4/5 (fire buttons, low when pressed) and their latches (VBLANK bit 6).
    private readonly bool[] _fire = new bool[2];
    private readonly bool[] _fireLatched = new bool[2];

    // Sound.
    private readonly AudioChannel[] _channels = [new AudioChannel(), new AudioChannel()];
    private float _lastLevel;

    /// <summary>The frame: <see cref="Width"/> × <see cref="Height"/> palette indices (0–127).</summary>
    public ReadOnlySpan<byte> Frame => _frame.AsSpan(0, Width * _height);

    public int Height => _height;

    /// <summary>The last frame had more than 290 lines (a PAL game).</summary>
    public bool Pal => _height == PalHeight;

    /// <summary>The CPU is held until the line's end (WSYNC).</summary>
    public bool WaitingForSync { get; private set; }

    /// <summary>The game ended VSYNC: the frame is complete and the next line starts a new one.</summary>
    public bool FrameDone { get; private set; }

    /// <summary>The line being drawn, counted from the end of VSYNC.</summary>
    public int Line => _line;

    public int Clock => _clock;

    /// <summary>Where audio changes go, timed in CPU cycles since the frame began; null while silent.</summary>
    public IAudioSink? AudioSink { get; set; }

    /// <summary>CPU cycles since the audio frame began (the console counts them).</summary>
    public int AudioClock { get; set; }

    public void Power()
    {
        Array.Clear(_frame);
        _vsync = _vblank = _colupf = _colubk = _ctrlpf = _pf0 = _pf1 = _pf2 = 0;
        Array.Clear(_nusiz);
        Array.Clear(_colup);
        Array.Clear(_refp);
        Array.Clear(_grp);
        Array.Clear(_grpOld);
        Array.Clear(_enam);
        _enabl = _enablOld = false;
        Array.Clear(_hmp);
        Array.Clear(_hmm);
        _hmbl = 0;
        Array.Clear(_vdelp);
        _vdelbl = false;
        Array.Clear(_resmp);
        Array.Clear(_posP);
        Array.Clear(_posM);
        _posBl = 0;
        _collisions = 0;
        _hmoveBlank = false;
        _clock = _line = 0;
        _sawVsync = false;
        _top = 37;
        _topSeen = -1;
        _height = NtscHeight;
        WaitingForSync = false;
        FrameDone = false;
        Array.Clear(_fireLatched);
        foreach (var channel in _channels)
        {
            channel.Reset();
        }

        _lastLevel = 0;
    }

    /// <summary>Starts a new frame's bookkeeping (the console calls it after it has used the finished one).</summary>
    public void BeginFrame() => FrameDone = false;

    public void SetFire(int player, bool pressed) => _fire[player] = pressed;

    // ---- The beam ----

    /// <summary>One CPU cycle: three colour clocks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CpuCycle()
    {
        ColourClock();
        ColourClock();
        ColourClock();
    }

    private void ColourClock()
    {
        if (_clock >= HBlankClocks)
        {
            DrawPixel(_clock - HBlankClocks);
        }

        if (_clock is 9 or 123)
        {
            AudioTick(); // the sound counters step twice a line (about 31.4 kHz)
        }

        if (++_clock == ClocksPerLine)
        {
            EndLine();
        }
    }

    private void EndLine()
    {
        _clock = 0;
        _hmoveBlank = false;
        WaitingForSync = false;
        _line++;
        if (_line >= MaxLines)
        {
            EndFrame(); // the game never ended VSYNC: show what there is
        }
    }

    private void EndFrame()
    {
        // The picture's top for the next frame: where the game turned VBLANK off this time (or as it was).
        if (_topSeen is >= 0 and < 80)
        {
            _top = _topSeen;
        }

        _height = _line > 290 ? PalHeight : NtscHeight;
        _topSeen = -1;
        _line = 0;
        FrameDone = true;
    }

    private void DrawPixel(int x)
    {
        if ((_vblank & 0x02) == 0 && _topSeen < 0)
        {
            _topSeen = _line;
        }

        var row = _line - _top;
        var visible = (uint)row < (uint)_height;

        var pf = Playfield(x);
        var bl = Ball(x);
        var p0 = Player(0, x);
        var p1 = Player(1, x);
        var m0 = Missile(0, x);
        var m1 = Missile(1, x);
        Collide(p0, p1, m0, m1, bl, pf);

        if (!visible)
        {
            return;
        }

        byte colour;
        if ((_vblank & 0x02) != 0 || (_hmoveBlank && x < 8))
        {
            colour = 0;
        }
        else
        {
            colour = _colubk;
            var playfieldColour = (_ctrlpf & 0x02) != 0 ? _colup[x < Width / 2 ? 0 : 1] : _colupf; // score mode
            if ((_ctrlpf & 0x04) != 0)
            {
                // Playfield and ball in front of the players.
                if (p1 || m1)
                {
                    colour = _colup[1];
                }

                if (p0 || m0)
                {
                    colour = _colup[0];
                }

                if (pf)
                {
                    colour = playfieldColour;
                }

                if (bl)
                {
                    colour = _colupf;
                }
            }
            else
            {
                if (pf)
                {
                    colour = playfieldColour;
                }

                if (bl)
                {
                    colour = _colupf;
                }

                if (p1 || m1)
                {
                    colour = _colup[1];
                }

                if (p0 || m0)
                {
                    colour = _colup[0];
                }
            }

            colour >>= 1;
        }

        _frame[row * Width + x] = colour;
    }

    private bool Playfield(int x)
    {
        var index = x >> 2;
        if (index >= 20)
        {
            index -= 20;
            if ((_ctrlpf & 0x01) != 0)
            {
                index = 19 - index; // reflected
            }
        }

        return index switch
        {
            < 4 => ((_pf0 >> (4 + index)) & 1) != 0,
            < 12 => ((_pf1 >> (11 - index)) & 1) != 0,
            _ => ((_pf2 >> (index - 12)) & 1) != 0,
        };
    }

    private bool Ball(int x)
    {
        var enabled = _vdelbl ? _enablOld : _enabl;
        if (!enabled)
        {
            return false;
        }

        var size = 1 << ((_ctrlpf >> 4) & 3);
        return (x - _posBl + Width) % Width < size;
    }

    private bool Player(int player, int x)
    {
        var graphics = _vdelp[player] ? _grpOld[player] : _grp[player];
        if (graphics == 0)
        {
            return false;
        }

        var mode = _nusiz[player] & 7;
        var scale = mode == 5 ? 2 : mode == 7 ? 4 : 1;
        var distance = (x - _posP[player] + Width) % Width;
        foreach (var offset in Copies[mode])
        {
            var dot = distance - offset;
            if ((uint)dot < (uint)(8 * scale))
            {
                var bit = dot / scale;
                return ((graphics >> (_refp[player] ? bit : 7 - bit)) & 1) != 0;
            }
        }

        return false;
    }

    private bool Missile(int missile, int x)
    {
        if (!_enam[missile] || _resmp[missile])
        {
            return false;
        }

        var size = 1 << ((_nusiz[missile] >> 4) & 3);
        var distance = (x - _posM[missile] + Width) % Width;
        foreach (var offset in Copies[_nusiz[missile] & 7])
        {
            if ((uint)(distance - offset) < (uint)size)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Sets the collision latches of every pair of objects that both draw this pixel.</summary>
    private void Collide(bool p0, bool p1, bool m0, bool m1, bool bl, bool pf)
    {
        // Bit order: CXM0P (M0-P1 7, M0-P0 6), CXM1P (M1-P0 7, M1-P1 6), CXP0FB (P0-PF 7, P0-BL 6),
        // CXP1FB (P1-PF, P1-BL), CXM0FB (M0-PF, M0-BL), CXM1FB (M1-PF, M1-BL), CXBLPF (BL-PF 7), CXPPMM (P0-P1 7, M0-M1 6).
        ushort set = 0;
        if (m0 && p1) set |= 1 << 0;
        if (m0 && p0) set |= 1 << 1;
        if (m1 && p0) set |= 1 << 2;
        if (m1 && p1) set |= 1 << 3;
        if (p0 && pf) set |= 1 << 4;
        if (p0 && bl) set |= 1 << 5;
        if (p1 && pf) set |= 1 << 6;
        if (p1 && bl) set |= 1 << 7;
        if (m0 && pf) set |= 1 << 8;
        if (m0 && bl) set |= 1 << 9;
        if (m1 && pf) set |= 1 << 10;
        if (m1 && bl) set |= 1 << 11;
        if (bl && pf) set |= 1 << 12;
        if (p0 && p1) set |= 1 << 13;
        if (m0 && m1) set |= 1 << 14;
        _collisions |= set;
    }

    // ---- Registers ----

    /// <summary>A read of $00–$0D (the address's low four bits): the collision latches and the inputs, in bits 7 and 6.</summary>
    public byte Read(int address)
    {
        var register = address & 0x0F;
        if (register < 8)
        {
            var bits = (_collisions >> (register * 2)) & 3;
            return (byte)(((bits & 1) << 7) | ((bits & 2) << 5));
        }

        return register switch
        {
            0x0C => Input(0),
            0x0D => Input(1),
            _ => 0x80, // INPT0–3: the paddles' capacitors charged (no paddles)
        };
    }

    private byte Input(int player)
    {
        var low = (_vblank & 0x40) != 0 ? _fireLatched[player] || _fire[player] : _fire[player];
        if ((_vblank & 0x40) != 0 && _fire[player])
        {
            _fireLatched[player] = true;
        }

        return low ? (byte)0x00 : (byte)0x80;
    }

    public void Write(int address, byte value)
    {
        switch (address & 0x3F)
        {
            case 0x00: // VSYNC
                if ((value & 0x02) != 0)
                {
                    _sawVsync = true;
                }
                else if ((_vsync & 0x02) != 0 && _sawVsync)
                {
                    _sawVsync = false;
                    EndFrame(); // the line after VSYNC ends is the new frame's first
                }

                _vsync = value;
                break;
            case 0x01: // VBLANK
                if ((value & 0x40) == 0)
                {
                    Array.Clear(_fireLatched);
                }

                _vblank = value;
                break;
            case 0x02: // WSYNC
                WaitingForSync = true;
                break;
            case 0x03: // RSYNC
                _clock = ClocksPerLine - 3;
                break;
            case 0x04: _nusiz[0] = value; break;
            case 0x05: _nusiz[1] = value; break;
            case 0x06: _colup[0] = value; break;
            case 0x07: _colup[1] = value; break;
            case 0x08: _colupf = value; break;
            case 0x09: _colubk = value; break;
            case 0x0A: _ctrlpf = value; break;
            case 0x0B: _refp[0] = (value & 0x08) != 0; break;
            case 0x0C: _refp[1] = (value & 0x08) != 0; break;
            case 0x0D: _pf0 = value; break;
            case 0x0E: _pf1 = value; break;
            case 0x0F: _pf2 = value; break;
            case 0x10: _posP[0] = ResetPosition(5, 3); break;
            case 0x11: _posP[1] = ResetPosition(5, 3); break;
            case 0x12: _posM[0] = ResetPosition(4, 2); break;
            case 0x13: _posM[1] = ResetPosition(4, 2); break;
            case 0x14: _posBl = ResetPosition(4, 2); break;
            case 0x15: _channels[0].SetControl(value); break;
            case 0x16: _channels[1].SetControl(value); break;
            case 0x17: _channels[0].SetFrequency(value); break;
            case 0x18: _channels[1].SetFrequency(value); break;
            case 0x19: _channels[0].SetVolume(value); break;
            case 0x1A: _channels[1].SetVolume(value); break;
            case 0x1B: // GRP0: player 1's delayed copy takes its new graphics
                _grp[0] = value;
                _grpOld[1] = _grp[1];
                break;
            case 0x1C: // GRP1: player 0's delayed copy and the ball's do
                _grp[1] = value;
                _grpOld[0] = _grp[0];
                _enablOld = _enabl;
                break;
            case 0x1D: _enam[0] = (value & 0x02) != 0; break;
            case 0x1E: _enam[1] = (value & 0x02) != 0; break;
            case 0x1F: _enabl = (value & 0x02) != 0; break;
            case 0x20: _hmp[0] = Motion(value); break;
            case 0x21: _hmp[1] = Motion(value); break;
            case 0x22: _hmm[0] = Motion(value); break;
            case 0x23: _hmm[1] = Motion(value); break;
            case 0x24: _hmbl = Motion(value); break;
            case 0x25: _vdelp[0] = (value & 0x01) != 0; break;
            case 0x26: _vdelp[1] = (value & 0x01) != 0; break;
            case 0x27: _vdelbl = (value & 0x01) != 0; break;
            case 0x28: SetResmp(0, value); break;
            case 0x29: SetResmp(1, value); break;
            case 0x2A: Hmove(); break;
            case 0x2B: // HMCLR
                Array.Clear(_hmp);
                Array.Clear(_hmm);
                _hmbl = 0;
                break;
            case 0x2C: _collisions = 0; break;
        }
    }

    /// <summary>HMxx's top nibble as a signed motion: positive moves the object left.</summary>
    private static sbyte Motion(byte value) => (sbyte)((sbyte)value >> 4);

    /// <summary>The position RESPx (or RESMx, RESBL) gives: <paramref name="delay"/> pixels after the beam, or <paramref name="blank"/> during horizontal blank.</summary>
    private int ResetPosition(int delay, int blank) => _clock < HBlankClocks ? blank : (_clock - HBlankClocks + delay) % Width;

    private void SetResmp(int missile, byte value)
    {
        var locked = (value & 0x02) != 0;
        if (_resmp[missile] && !locked)
        {
            // Released from the player: the missile sits at the player's centre.
            var mode = _nusiz[missile] & 7;
            var centre = mode == 5 ? 6 : mode == 7 ? 10 : 3;
            _posM[missile] = (_posP[missile] + centre) % Width;
        }

        _resmp[missile] = locked;
    }

    private void Hmove()
    {
        for (var i = 0; i < 2; i++)
        {
            _posP[i] = Move(_posP[i], _hmp[i]);
            _posM[i] = Move(_posM[i], _hmm[i]);
        }

        _posBl = Move(_posBl, _hmbl);
        if (_clock < HBlankClocks)
        {
            _hmoveBlank = true; // the black bar at the line's start
        }
    }

    private static int Move(int position, int motion) => (position - motion + Width * 2) % Width;

    // ---- Sound ----

    private void AudioTick()
    {
        _channels[0].Tick();
        _channels[1].Tick();
        if (AudioSink is { } sink)
        {
            var level = (_channels[0].Output + _channels[1].Output) / 30f;
            if (level != _lastLevel)
            {
                sink.AddDelta(AudioClock, level - _lastLevel);
                _lastLevel = level;
            }
        }
    }

    /// <summary>
    /// One sound channel, as Ron Fries' TIASound runs it: a divider of AUDF + 1 (three times as long in AUDC's
    /// "divide by 3" modes) clocks the 4-, 5- and 9-bit polynomial counters, and AUDC picks which one, or a plain
    /// square wave, decides the output, gated by the 5-bit counter or its divide-by-31 in the modes that say so.
    /// </summary>
    private sealed class AudioChannel
    {
        private static readonly bool[] Poly4 = Lfsr(4, 3, 15);
        private static readonly bool[] Poly5 = Lfsr(5, 3, 31);
        private static readonly bool[] Poly9 = Lfsr(9, 5, 511);

        private byte _control;
        private byte _frequency;
        private byte _volume;
        private int _divider;
        private int _dividerMax = 1;
        private int _p4;
        private int _p5;
        private int _p9;

        public int Output { get; private set; }

        public void Reset()
        {
            _control = _frequency = _volume = 0;
            _divider = 0;
            _dividerMax = 1;
            _p4 = _p5 = _p9 = 0;
            Output = 0;
            Update();
        }

        public void SetControl(byte value)
        {
            _control = (byte)(value & 0x0F);
            Update();
        }

        public void SetFrequency(byte value)
        {
            _frequency = (byte)(value & 0x1F);
            Update();
        }

        public void SetVolume(byte value)
        {
            _volume = (byte)(value & 0x0F);
            Update();
        }

        private void Update()
        {
            int max;
            if (_control is 0x00 or 0x0B)
            {
                max = 0;
                Output = _volume; // held high: games play samples by changing the volume
            }
            else
            {
                max = _frequency + 1;
                if ((_control & 0x0C) == 0x0C)
                {
                    max *= 3;
                }
            }

            if (max != _dividerMax)
            {
                _dividerMax = max;
                if (_divider == 0 || max == 0)
                {
                    _divider = max;
                }
            }
        }

        public void Tick()
        {
            if (_divider > 1)
            {
                _divider--;
                return;
            }

            if (_divider != 1)
            {
                return;
            }

            _divider = _dividerMax;
            _p5 = (_p5 + 1) % 31;
            var clocked = (_control & 0x02) == 0
                || ((_control & 0x01) == 0 && _p5 == 0) // divide by 31
                || ((_control & 0x01) != 0 && Poly5[_p5]);
            if (!clocked)
            {
                return;
            }

            if ((_control & 0x04) != 0)
            {
                Output = Output != 0 ? 0 : _volume; // a square wave
            }
            else if ((_control & 0x08) != 0)
            {
                if (_control == 0x08)
                {
                    _p9 = (_p9 + 1) % 511;
                    Output = Poly9[_p9] ? _volume : 0;
                }
                else
                {
                    Output = Poly5[_p5] ? _volume : 0;
                }
            }
            else
            {
                _p4 = (_p4 + 1) % 15;
                Output = Poly4[_p4] ? _volume : 0;
            }
        }

        /// <summary>A maximal-length shift register's output sequence (taps at <paramref name="bits"/> and <paramref name="tap"/>).</summary>
        private static bool[] Lfsr(int bits, int tap, int length)
        {
            var sequence = new bool[length];
            var value = (1 << bits) - 1;
            for (var i = 0; i < length; i++)
            {
                sequence[i] = (value & 1) != 0;
                var feedback = ((value >> 0) ^ (value >> (bits - tap))) & 1;
                value = (value >> 1) | (feedback << (bits - 1));
            }

            return sequence;
        }

        public void Save(StateWriter writer)
        {
            writer.WriteByte(_control);
            writer.WriteByte(_frequency);
            writer.WriteByte(_volume);
            writer.WriteInt32(_divider);
            writer.WriteInt32(_dividerMax);
            writer.WriteInt32(_p4);
            writer.WriteInt32(_p5);
            writer.WriteInt32(_p9);
            writer.WriteInt32(Output);
        }

        public void Load(StateReader reader)
        {
            _control = (byte)(reader.ReadByte() & 0x0F);
            _frequency = (byte)(reader.ReadByte() & 0x1F);
            _volume = (byte)(reader.ReadByte() & 0x0F);
            _divider = Math.Clamp(reader.ReadInt32(), 0, 96);
            _dividerMax = Math.Clamp(reader.ReadInt32(), 0, 96);
            _p4 = reader.ReadInt32() % 15;
            _p5 = reader.ReadInt32() % 31;
            _p9 = reader.ReadInt32() % 511;
            Output = reader.ReadInt32() & 0x0F;
        }
    }

    // ---- Save states ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_frame);
        writer.WriteByte(_vsync);
        writer.WriteByte(_vblank);
        writer.WriteBytes(_nusiz);
        writer.WriteBytes(_colup);
        writer.WriteByte(_colupf);
        writer.WriteByte(_colubk);
        writer.WriteByte(_ctrlpf);
        writer.WriteBool(_refp[0]);
        writer.WriteBool(_refp[1]);
        writer.WriteByte(_pf0);
        writer.WriteByte(_pf1);
        writer.WriteByte(_pf2);
        writer.WriteBytes(_grp);
        writer.WriteBytes(_grpOld);
        writer.WriteBool(_enam[0]);
        writer.WriteBool(_enam[1]);
        writer.WriteBool(_enabl);
        writer.WriteBool(_enablOld);
        for (var i = 0; i < 2; i++)
        {
            writer.WriteByte((byte)_hmp[i]);
            writer.WriteByte((byte)_hmm[i]);
            writer.WriteBool(_vdelp[i]);
            writer.WriteBool(_resmp[i]);
            writer.WriteInt32(_posP[i]);
            writer.WriteInt32(_posM[i]);
            writer.WriteBool(_fireLatched[i]);
        }

        writer.WriteByte((byte)_hmbl);
        writer.WriteBool(_vdelbl);
        writer.WriteInt32(_posBl);
        writer.WriteUInt16(_collisions);
        writer.WriteBool(_hmoveBlank);
        writer.WriteInt32(_clock);
        writer.WriteInt32(_line);
        writer.WriteBool(_sawVsync);
        writer.WriteInt32(_top);
        writer.WriteInt32(_topSeen);
        writer.WriteInt32(_height);
        writer.WriteBool(WaitingForSync);
        writer.WriteBool(FrameDone);
        _channels[0].Save(writer);
        _channels[1].Save(writer);
        writer.WriteInt32(BitConverter.SingleToInt32Bits(_lastLevel));
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_frame);
        _vsync = reader.ReadByte();
        _vblank = reader.ReadByte();
        reader.ReadBytes(_nusiz);
        reader.ReadBytes(_colup);
        _colupf = reader.ReadByte();
        _colubk = reader.ReadByte();
        _ctrlpf = reader.ReadByte();
        _refp[0] = reader.ReadBool();
        _refp[1] = reader.ReadBool();
        _pf0 = reader.ReadByte();
        _pf1 = reader.ReadByte();
        _pf2 = reader.ReadByte();
        reader.ReadBytes(_grp);
        reader.ReadBytes(_grpOld);
        _enam[0] = reader.ReadBool();
        _enam[1] = reader.ReadBool();
        _enabl = reader.ReadBool();
        _enablOld = reader.ReadBool();
        for (var i = 0; i < 2; i++)
        {
            _hmp[i] = (sbyte)reader.ReadByte();
            _hmm[i] = (sbyte)reader.ReadByte();
            _vdelp[i] = reader.ReadBool();
            _resmp[i] = reader.ReadBool();
            _posP[i] = Math.Abs(reader.ReadInt32()) % Width;
            _posM[i] = Math.Abs(reader.ReadInt32()) % Width;
            _fireLatched[i] = reader.ReadBool();
        }

        _hmbl = (sbyte)reader.ReadByte();
        _vdelbl = reader.ReadBool();
        _posBl = Math.Abs(reader.ReadInt32()) % Width;
        _collisions = (ushort)(reader.ReadUInt16() & 0x7FFF);
        _hmoveBlank = reader.ReadBool();
        _clock = Math.Clamp(reader.ReadInt32(), 0, ClocksPerLine - 1);
        _line = Math.Clamp(reader.ReadInt32(), 0, MaxLines - 1);
        _sawVsync = reader.ReadBool();
        _top = Math.Clamp(reader.ReadInt32(), 0, 79);
        _topSeen = Math.Clamp(reader.ReadInt32(), -1, MaxLines);
        _height = reader.ReadInt32() == PalHeight ? PalHeight : NtscHeight;
        WaitingForSync = reader.ReadBool();
        FrameDone = reader.ReadBool();
        _channels[0].Load(reader);
        _channels[1].Load(reader);
        _lastLevel = BitConverter.Int32BitsToSingle(reader.ReadInt32());
        reader.EndChunk();
    }
}
