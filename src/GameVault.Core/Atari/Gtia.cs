using GameVault.Core.State;

namespace GameVault.Core.Atari;

/// <summary>
/// GTIA, the Atari 5200's (and 400/800's) colour chip (phase-19.md, milestone 2): it turns ANTIC's playfield into
/// colours, draws the four players and four missiles over or under it by PRIOR's rules, records the sixty collisions,
/// reads the controllers' bottom fire buttons, and on the 5200 its CONSOL register picks the controller POKEY scans.
/// </summary>
/// <remarks>
/// The picture is drawn a colour clock at a time with the registers as they are when the beam gets there (the console
/// catches the line up before each GTIA write), so colours changed mid-line land where they should. The priority logic
/// is GTIA's own equations (Atari's GTIA data sheet, as the Altirra Hardware Reference Manual gives them); where two
/// objects both win, their colours are ORed, as on the hardware.
/// </remarks>
public sealed class Gtia : IStateful
{
    /// <summary>The first colour clock shown: 168 clocks (336 hi-res pixels) from here.</summary>
    public const int FirstClock = 44;

    public const int Width = 336;
    public const int Height = Antic.VblankLine - Antic.FirstDisplayLine;

    private static readonly StateTag Tag = StateTag.From("GTIA");

    // Which colour registers show (bit 0–3 COLPM0–3, 4–7 COLPF0–3, 8 COLBK), by PRIOR's low four bits and bit 5
    // (multicolour players), for each mix of players (bits 0–3) and playfields (bits 4–7).
    private static readonly ushort[] Priority = BuildPriority();

    // Object width shifts by SIZE value: normal, double, normal, quadruple.
    private static readonly int[] SizeShift = [0, 1, 0, 2];

    private readonly ushort[] _frame = new ushort[Width * Height];
    private readonly byte[] _hposp = new byte[4];
    private readonly byte[] _hposm = new byte[4];
    private readonly byte[] _sizep = new byte[4];
    private readonly byte[] _grafp = new byte[4];
    private readonly byte[] _colpm = new byte[4];
    private readonly byte[] _colpf = new byte[4];
    private readonly byte[] _mpf = new byte[4];
    private readonly byte[] _ppf = new byte[4];
    private readonly byte[] _mpl = new byte[4];
    private readonly byte[] _ppl = new byte[4];
    private readonly bool[] _trigger = new bool[4];
    private readonly bool[] _latched = new bool[4];
    private byte _sizem;
    private byte _grafm;
    private byte _colbk;
    private byte _prior;
    private byte _vdelay;
    private byte _gractl;
    private byte _consol;
    private int _y = -1;
    private int _x;
    private Antic? _antic;

    public ReadOnlySpan<ushort> Frame => _frame;

    /// <summary>The value last written to CONSOL: on the 5200, bits 0–1 pick the controller POKEY scans, bit 2 powers the pots.</summary>
    public byte Consol => _consol;

    public void Power()
    {
        Array.Clear(_frame);
        Array.Clear(_hposp);
        Array.Clear(_hposm);
        Array.Clear(_sizep);
        Array.Clear(_grafp);
        Array.Clear(_colpm);
        Array.Clear(_colpf);
        ClearCollisions();
        Array.Clear(_latched);
        _sizem = _grafm = _colbk = _prior = _vdelay = _gractl = _consol = 0;
        _y = -1;
        _x = 0;
    }

    /// <summary>The bottom fire button on controller <paramref name="port"/> (TRIG0–3).</summary>
    public void SetTrigger(int port, bool pressed)
    {
        _trigger[port] = pressed;
        if (pressed && (_gractl & 4) != 0)
        {
            _latched[port] = true;
        }
    }

    // ---- ANTIC's side ----

    /// <summary>A line starts: <paramref name="y"/> is its row of the picture, or -1 outside it.</summary>
    public void BeginLine(int y, Antic antic)
    {
        _y = y;
        _x = 0;
        _antic = antic;
    }

    public void EndLine() => RenderTo(Antic.Clocks);

    public void DmaMissiles(byte value, bool singleLine, bool odd)
    {
        if ((_gractl & 1) == 0)
        {
            return;
        }

        // VDELAY holds an object's double-line graphics back a line: it takes DMA only on odd lines.
        var keep = !singleLine && !odd ? _vdelay & 0x0F : 0;
        var mask = 0;
        for (var n = 0; n < 4; n++)
        {
            if ((keep & (1 << n)) != 0)
            {
                mask |= 3 << (n * 2);
            }
        }

        _grafm = (byte)((_grafm & mask) | (value & ~mask));
    }

    public void DmaPlayer(int n, byte value, bool singleLine, bool odd)
    {
        if ((_gractl & 2) == 0 || (!singleLine && !odd && (_vdelay & (0x10 << n)) != 0))
        {
            return;
        }

        _grafp[n] = value;
    }

    // ---- The CPU's side ----

    public byte Read(int address)
    {
        var a = address & 0x1F;
        return a switch
        {
            < 0x04 => _mpf[a],
            < 0x08 => _ppf[a - 4],
            < 0x0C => _mpl[a - 8],
            < 0x10 => _ppl[a - 12],
            < 0x14 => _trigger[a - 0x10] || _latched[a - 0x10] ? (byte)0 : (byte)1,
            0x14 => 0x0F, // PAL: an NTSC machine
            _ => 0x0F,
        };
    }

    public void Write(int address, byte value)
    {
        var a = address & 0x1F;
        switch (a)
        {
            case < 0x04:
                _hposp[a] = value;
                break;
            case < 0x08:
                _hposm[a - 4] = value;
                break;
            case < 0x0C:
                _sizep[a - 8] = (byte)(value & 3);
                break;
            case 0x0C:
                _sizem = value;
                break;
            case < 0x11:
                _grafp[a - 0x0D] = value;
                break;
            case 0x11:
                _grafm = value;
                break;
            case < 0x16:
                _colpm[a - 0x12] = value;
                break;
            case < 0x1A:
                _colpf[a - 0x16] = value;
                break;
            case 0x1A:
                _colbk = value;
                break;
            case 0x1B:
                _prior = value;
                break;
            case 0x1C:
                _vdelay = value;
                break;
            case 0x1D:
                _gractl = value;
                if ((value & 4) == 0)
                {
                    Array.Clear(_latched);
                }

                break;
            case 0x1E:
                ClearCollisions(); // HITCLR
                break;
            case 0x1F:
                _consol = value;
                break;
        }
    }

    private void ClearCollisions()
    {
        Array.Clear(_mpf);
        Array.Clear(_ppf);
        Array.Clear(_mpl);
        Array.Clear(_ppl);
    }

    // ---- Drawing ----

    /// <summary>Draws the current line up to colour clock <paramref name="clock"/>.</summary>
    public void RenderTo(int clock)
    {
        var end = Math.Min(clock, FirstClock + Width / 2);
        if (_y < 0 || _antic is null)
        {
            _x = Math.Max(_x, end);
            return;
        }

        var x = Math.Max(_x, FirstClock);
        if (x >= end)
        {
            return;
        }

        var playfield = _antic.Playfield;
        var hires = _antic.Hires;
        var hiresLine = _antic.HiresLine;
        var gtiaMode = hiresLine ? _prior >> 6 : 0;
        var table = ((_prior & 0x0F) | ((_prior & 0x20) >> 1)) << 8;
        var fifth = (_prior & 0x10) != 0;
        var row = _y * Width;
        for (; x < end; x++)
        {
            int players = 0, missiles = 0;
            for (var n = 0; n < 4; n++)
            {
                var g = _grafp[n];
                if (g != 0)
                {
                    var offset = (x - _hposp[n]) >> SizeShift[_sizep[n]];
                    if ((uint)offset < 8 && x >= _hposp[n] && (g & (0x80 >> offset)) != 0)
                    {
                        players |= 1 << n;
                    }
                }

                var m = (_grafm >> (n * 2)) & 3;
                if (m != 0)
                {
                    var offset = (x - _hposm[n]) >> SizeShift[(_sizem >> (n * 2)) & 3];
                    if ((uint)offset < 2 && x >= _hposm[n] && (m & (2 >> offset)) != 0)
                    {
                        missiles |= 1 << n;
                    }
                }
            }

            var code = playfield[x];
            var lit = hiresLine && code == 3 ? hires[x] : 3;
            var pf = code == 0 || gtiaMode != 0 ? 0 : 1 << (code - 1);
            var hitPf = hiresLine && code == 3 ? (lit != 0 && gtiaMode == 0 ? 4 : 0) : pf;
            if ((players | missiles) != 0)
            {
                for (var n = 0; n < 4; n++)
                {
                    if ((missiles & (1 << n)) != 0)
                    {
                        _mpf[n] |= (byte)hitPf;
                        _mpl[n] |= (byte)players;
                    }

                    if ((players & (1 << n)) != 0)
                    {
                        _ppf[n] |= (byte)hitPf;
                        _ppl[n] |= (byte)(players & ~(1 << n));
                    }
                }
            }

            var objects = players;
            if (fifth)
            {
                pf |= missiles != 0 ? 8 : 0; // the fifth player: missiles drawn as PF3
            }
            else
            {
                objects |= missiles;
            }

            var select = Priority[table | objects | (pf << 4)];
            var colour = Colour(select);
            int left = colour, right = colour;
            if (gtiaMode != 0 && code == 3 && objects == 0)
            {
                var pair = x & ~1;
                var nibble = (hires[pair] << 2) | hires[pair + 1];
                left = right = GtiaColour(gtiaMode, nibble);
            }
            else if (hiresLine && code == 3 && (select & 0x40) != 0)
            {
                // A lit half-clock pixel takes PF1's luminance on PF2's (or the winning object's) hue.
                var lum = (colour & 0xF0) | (_colpf[1] & 0x0E);
                left = (lit & 2) != 0 ? lum : colour;
                right = (lit & 1) != 0 ? lum : colour;
            }

            var i = row + (x - FirstClock) * 2;
            _frame[i] = (ushort)left;
            _frame[i + 1] = (ushort)right;
        }

        _x = end;
    }

    private int Colour(ushort select)
    {
        var c = 0;
        if (select == 0x100)
        {
            return _colbk;
        }

        for (var n = 0; n < 4; n++)
        {
            if ((select & (1 << n)) != 0)
            {
                c |= _colpm[n];
            }

            if ((select & (0x10 << n)) != 0)
            {
                c |= _colpf[n];
            }
        }

        return (select & 0x100) != 0 ? c | _colbk : c;
    }

    /// <summary>GTIA's modes 9 (sixteen luminances), 10 (nine colour registers) and 11 (sixteen hues).</summary>
    private int GtiaColour(int mode, int nibble) => mode switch
    {
        1 => (_colbk & 0xF0) | nibble,
        2 => nibble switch
        {
            < 4 => _colpm[nibble],
            < 8 => _colpf[nibble - 4],
            < 12 => _colbk,
            _ => _colpf[nibble - 12],
        },
        _ => nibble == 0 ? _colbk & 0xF0 : (nibble << 4) | (_colbk & 0x0F),
    };

    private static ushort[] BuildPriority()
    {
        var table = new ushort[32 * 256];
        for (var variant = 0; variant < 32; variant++)
        {
            bool pri0 = (variant & 1) != 0, pri1 = (variant & 2) != 0, pri2 = (variant & 4) != 0, pri3 = (variant & 8) != 0;
            var multi = (variant & 16) != 0;
            bool pri01 = pri0 | pri1, pri12 = pri1 | pri2, pri23 = pri2 | pri3, pri03 = pri0 | pri3;
            for (var mix = 0; mix < 256; mix++)
            {
                bool p0 = (mix & 1) != 0, p1 = (mix & 2) != 0, p2 = (mix & 4) != 0, p3 = (mix & 8) != 0;
                bool pf0 = (mix & 16) != 0, pf1 = (mix & 32) != 0, pf2 = (mix & 64) != 0, pf3 = (mix & 128) != 0;
                bool p01 = p0 | p1, p23 = p2 | p3, pf01 = pf0 | pf1, pf23 = pf2 | pf3;
                var sp0 = p0 && !(pf01 && pri23) && !(pri2 && pf23);
                var sp1 = p1 && !(pf01 && pri23) && !(pri2 && pf23) && (!p0 || multi);
                var sp2 = p2 && !p01 && !(pf23 && pri12) && !(pf01 && !pri0);
                var sp3 = p3 && !p01 && !(pf23 && pri12) && !(pf01 && !pri0) && (!p2 || multi);
                var sf3 = pf3 && !(p23 && pri03) && !(p01 && !pri2);
                var sf0 = pf0 && !(p23 && pri0) && !(p01 && pri01) && !sf3;
                var sf1 = pf1 && !(p23 && pri0) && !(p01 && pri01) && !sf3;
                var sf2 = pf2 && !(p23 && pri03) && !(p01 && !pri2) && !sf3;
                var sb = !p01 && !p23 && !pf01 && !pf23;
                var select = (sp0 ? 1 : 0) | (sp1 ? 2 : 0) | (sp2 ? 4 : 0) | (sp3 ? 8 : 0)
                    | (sf0 ? 16 : 0) | (sf1 ? 32 : 0) | (sf2 ? 64 : 0) | (sf3 ? 128 : 0) | (sb ? 256 : 0);
                table[(variant << 8) | mix] = (ushort)select;
            }
        }

        return table;
    }

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_hposp);
        writer.WriteBytes(_hposm);
        writer.WriteBytes(_sizep);
        writer.WriteBytes(_grafp);
        writer.WriteBytes(_colpm);
        writer.WriteBytes(_colpf);
        writer.WriteBytes(_mpf);
        writer.WriteBytes(_ppf);
        writer.WriteBytes(_mpl);
        writer.WriteBytes(_ppl);
        for (var n = 0; n < 4; n++)
        {
            writer.WriteBool(_latched[n]);
        }

        writer.WriteByte(_sizem);
        writer.WriteByte(_grafm);
        writer.WriteByte(_colbk);
        writer.WriteByte(_prior);
        writer.WriteByte(_vdelay);
        writer.WriteByte(_gractl);
        writer.WriteByte(_consol);
        writer.WriteInt32(_y);
        writer.WriteInt32(_x);
        for (var i = 0; i < _frame.Length; i++)
        {
            writer.WriteByte((byte)_frame[i]);
        }

        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_hposp);
        reader.ReadBytes(_hposm);
        reader.ReadBytes(_sizep);
        for (var n = 0; n < 4; n++)
        {
            _sizep[n] &= 3;
        }

        reader.ReadBytes(_grafp);
        reader.ReadBytes(_colpm);
        reader.ReadBytes(_colpf);
        reader.ReadBytes(_mpf);
        reader.ReadBytes(_ppf);
        reader.ReadBytes(_mpl);
        reader.ReadBytes(_ppl);
        for (var n = 0; n < 4; n++)
        {
            _latched[n] = reader.ReadBool();
        }

        _sizem = reader.ReadByte();
        _grafm = reader.ReadByte();
        _colbk = reader.ReadByte();
        _prior = reader.ReadByte();
        _vdelay = reader.ReadByte();
        _gractl = reader.ReadByte();
        _consol = reader.ReadByte();
        _y = Math.Clamp(reader.ReadInt32(), -1, Height - 1);
        _x = Math.Clamp(reader.ReadInt32(), 0, Antic.Clocks);
        for (var i = 0; i < _frame.Length; i++)
        {
            _frame[i] = reader.ReadByte();
        }

        reader.EndChunk();
    }
}
