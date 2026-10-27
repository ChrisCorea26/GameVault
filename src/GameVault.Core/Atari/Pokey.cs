using GameVault.Core.Audio;
using GameVault.Core.State;

namespace GameVault.Core.Atari;

/// <summary>
/// POKEY, the Atari 5200's (and 400/800's) sound and input chip (phase-19.md, milestone 2): four sound channels with
/// their polynomial-counter distortions, 16-bit pairs and high-pass filters; the timer interrupts; the keypad scan;
/// the eight pot counters that read the 5200's analog sticks; and RANDOM.
/// </summary>
/// <remarks>
/// Behaviour follows the Altirra Hardware Reference Manual's POKEY chapter: the 64 kHz and 15 kHz base clocks divide
/// the 1.79 MHz machine clock by 28 and 114; a channel's divider underflows every AUDF + 1 base clocks (AUDF + 4 machine
/// cycles at 1.79 MHz, a 16-bit pair's AUDF + 7); each underflow updates its output from a pure tone, the 4-bit poly or
/// the 17-bit (or 9-bit) poly, gated by the 5-bit poly unless AUDC's bit 7 says not to. The polys step every cycle
/// except while SKCTL holds POKEY in its initialisation state.
/// </remarks>
public sealed class Pokey : IStateful
{
    /// <summary>No key held on the keypad.</summary>
    public const int NoKey = -1;

    /// <summary>The pot counter's top: a pot that hasn't latched by here reads this.</summary>
    public const int PotMax = 228;

    private static readonly StateTag Tag = StateTag.From("POKY");

    private readonly byte[] _audf = new byte[4];
    private readonly byte[] _audc = new byte[4];
    private readonly int[] _count = new int[4];
    private readonly bool[] _output = new bool[4];
    private readonly byte[] _pot = new byte[8];
    private byte _audctl;
    private byte _irqEnable;
    private byte _irqPending;
    private byte _skctl;
    private byte _kbcode;
    private byte _allpot;
    private int _div64;
    private int _div15;
    private int _potDivider;
    private int _potCounter;
    private int _poly4;
    private int _poly5;
    private int _poly9;
    private int _poly17;
    private bool _highPass1;
    private bool _highPass2;
    private int _keyHeld = NoKey;
    private bool _topHeld;
    private bool _dirty;
    private float _lastLevel;

    /// <summary>The keypad codes held on each controller (0–15, or <see cref="NoKey"/>); the console sets them.</summary>
    public int[] Keys { get; } = [NoKey, NoKey, NoKey, NoKey];

    /// <summary>Each controller's top fire button; the console sets them.</summary>
    public bool[] TopButtons { get; } = new bool[4];

    /// <summary>The eight pots' readings (0–228): two per controller, its stick's X and Y; the console sets them.</summary>
    public byte[] PotInputs { get; } = [PotMax, PotMax, PotMax, PotMax, PotMax, PotMax, PotMax, PotMax];

    /// <summary>The controller the keypad scan and SKSTAT read (GTIA's CONSOL bits 0–1 on the 5200).</summary>
    public int SelectedController { get; set; }

    public IAudioSink? AudioSink { get; set; }

    /// <summary>The machine cycle within the frame, for the audio sink.</summary>
    public int AudioClock { get; set; }

    /// <summary>An enabled interrupt is pending (POKEY's IRQ output).</summary>
    public bool IrqAsserted => (_irqPending & _irqEnable) != 0;

    public void Power()
    {
        Array.Clear(_audf);
        Array.Clear(_audc);
        Array.Clear(_output);
        Array.Clear(_pot);
        _audctl = _irqEnable = _irqPending = _skctl = 0;
        _kbcode = 0xFF;
        _allpot = 0xFF;
        _div64 = 28;
        _div15 = 114;
        _potDivider = 114;
        _potCounter = PotMax;
        ResetPolys();
        _highPass1 = _highPass2 = false;
        _keyHeld = NoKey;
        _topHeld = false;
        for (var n = 0; n < 4; n++)
        {
            _count[n] = Period(n);
        }

        _dirty = true;
    }

    private void ResetPolys()
    {
        _poly4 = 0x0F;
        _poly5 = 0x1F;
        _poly9 = 0x1FF;
        _poly17 = 0x1FFFF;
    }

    /// <summary>One machine cycle (1.79 MHz).</summary>
    public void Tick()
    {
        bool base64 = false, base15 = false;
        if ((_skctl & 3) != 0)
        {
            // The polys: x^4+x^3+1, x^5+x^3+1, x^9+x^5+1 and x^17+x^14+1, shifting left.
            _poly4 = ((_poly4 << 1) | (((_poly4 >> 3) ^ (_poly4 >> 2)) & 1)) & 0x0F;
            _poly5 = ((_poly5 << 1) | (((_poly5 >> 4) ^ (_poly5 >> 2)) & 1)) & 0x1F;
            _poly9 = ((_poly9 << 1) | (((_poly9 >> 8) ^ (_poly9 >> 4)) & 1)) & 0x1FF;
            _poly17 = ((_poly17 << 1) | (((_poly17 >> 16) ^ (_poly17 >> 13)) & 1)) & 0x1FFFF;
            if (--_div64 == 0)
            {
                _div64 = 28;
                base64 = true;
            }

            if (--_div15 == 0)
            {
                _div15 = 114;
                base15 = true;
            }
        }

        var baseClock = (_audctl & 1) != 0 ? base15 : base64;
        var clock1 = (_audctl & 0x40) != 0 || baseClock;
        var clock3 = (_audctl & 0x20) != 0 || baseClock;
        bool under1 = false, under2 = false, under3 = false, under4 = false;
        if ((_audctl & 0x10) != 0)
        {
            if (clock1 && --_count[1] <= 0)
            {
                _count[1] = Period16(0);
                under2 = true;
            }
        }
        else
        {
            if (clock1 && --_count[0] <= 0)
            {
                _count[0] = Period(0);
                under1 = true;
            }

            if (baseClock && --_count[1] <= 0)
            {
                _count[1] = Period(1);
                under2 = true;
            }
        }

        if ((_audctl & 0x08) != 0)
        {
            if (clock3 && --_count[3] <= 0)
            {
                _count[3] = Period16(2);
                under4 = true;
            }
        }
        else
        {
            if (clock3 && --_count[2] <= 0)
            {
                _count[2] = Period(2);
                under3 = true;
            }

            if (baseClock && --_count[3] <= 0)
            {
                _count[3] = Period(3);
                under4 = true;
            }
        }

        if (under1)
        {
            Underflow(0);
            Interrupt(0x01);
        }

        if (under2)
        {
            Underflow(1);
            Interrupt(0x02);
        }

        if (under3)
        {
            Underflow(2);
            if ((_audctl & 0x04) != 0)
            {
                _highPass1 = _output[0];
                _dirty = true;
            }
        }

        if (under4)
        {
            Underflow(3);
            Interrupt(0x04);
            if ((_audctl & 0x02) != 0)
            {
                _highPass2 = _output[1];
                _dirty = true;
            }
        }

        // The pot counters count lines, or machine cycles in SKCTL's fast-scan mode; the keypad scan runs by lines.
        if (--_potDivider == 0)
        {
            _potDivider = 114;
            ScanKeypad();
            if ((_skctl & 4) == 0)
            {
                CountPots();
            }
        }

        if ((_skctl & 4) != 0)
        {
            CountPots();
        }

        if (_dirty)
        {
            Mix();
        }
    }

    private int Period(int n)
    {
        var fast = (n == 0 && (_audctl & 0x40) != 0) || (n == 2 && (_audctl & 0x20) != 0);
        return _audf[n] + (fast ? 4 : 1);
    }

    private int Period16(int low)
    {
        var fast = (low == 0 && (_audctl & 0x40) != 0) || (low == 2 && (_audctl & 0x20) != 0);
        return (_audf[low] | (_audf[low + 1] << 8)) + (fast ? 7 : 1);
    }

    private void Underflow(int n)
    {
        var control = _audc[n];
        if ((control & 0x80) == 0 && (_poly5 & 1) == 0)
        {
            return; // the 5-bit poly gates the clock
        }

        var old = _output[n];
        _output[n] = (control & 0x20) != 0 ? !old
            : (control & 0x40) != 0 ? (_poly4 & 1) != 0
            : (((_audctl & 0x80) != 0 ? _poly9 : _poly17) & 1) != 0;
        _dirty |= old != _output[n];
    }

    private void Interrupt(byte bit)
    {
        if ((_irqEnable & bit) != 0)
        {
            _irqPending |= bit;
        }
    }

    private int Level(int n, bool output)
    {
        var control = _audc[n];
        var volume = control & 0x0F;
        return (control & 0x10) != 0 || output ? volume : 0;
    }

    private void Mix()
    {
        _dirty = false;
        var one = (_audctl & 0x04) != 0 ? _output[0] ^ _highPass1 : _output[0];
        var two = (_audctl & 0x02) != 0 ? _output[1] ^ _highPass2 : _output[1];
        var level = (Level(0, one) + Level(1, two) + Level(2, _output[2]) + Level(3, _output[3])) / 60f;
        if (level != _lastLevel)
        {
            AudioSink?.AddDelta(AudioClock, level - _lastLevel);
            _lastLevel = level;
        }
    }

    private void CountPots()
    {
        if (_potCounter >= PotMax)
        {
            return;
        }

        _potCounter++;
        LatchPots();
    }

    private void LatchPots()
    {
        for (var i = 0; i < 8; i++)
        {
            var bit = 1 << i;
            if ((_allpot & bit) != 0 && _potCounter >= PotInputs[i])
            {
                _pot[i] = (byte)Math.Min((int)PotInputs[i], PotMax);
                _allpot &= (byte)~bit;
            }
        }
    }

    /// <summary>
    /// The keypad scan (SKCTL bit 1): a newly pressed key latches its code in KBCODE (bits 1–4) and raises the keyboard
    /// interrupt; the top fire button, wired as the scan's BREAK key, raises the break interrupt when pressed.
    /// </summary>
    private void ScanKeypad()
    {
        var controller = SelectedController & 3;
        var top = TopButtons[controller];
        if (top && !_topHeld)
        {
            Interrupt(0x80);
        }

        _topHeld = top;
        if ((_skctl & 2) == 0)
        {
            return;
        }

        var key = Keys[controller];
        if (key != NoKey && key != _keyHeld)
        {
            _kbcode = (byte)((key & 0x0F) << 1);
            Interrupt(0x40);
        }

        _keyHeld = key;
    }

    public byte Read(int address)
    {
        var a = address & 0x0F;
        switch (a)
        {
            case < 8:
                return (_allpot & (1 << a)) != 0 ? (byte)_potCounter : _pot[a];
            case 0x08:
                return _allpot;
            case 0x09:
                return _kbcode;
            case 0x0A:
                // RANDOM: the top eight bits of the 17-bit poly (or the 9-bit one), inverted.
                return (_audctl & 0x80) != 0 ? (byte)~_poly9 : (byte)~(_poly17 >> 9);
            case 0x0E:
                return (byte)~_irqPending;
            case 0x0F:
            {
                // SKSTAT: bit 3 the top fire button (the scan's SHIFT line), bit 2 a key held; low when active.
                var value = 0xFF;
                var controller = SelectedController & 3;
                if (TopButtons[controller])
                {
                    value &= ~0x08;
                }

                if ((_skctl & 2) != 0 && Keys[controller] != NoKey)
                {
                    value &= ~0x04;
                }

                return (byte)value;
            }

            default:
                return 0xFF;
        }
    }

    public void Write(int address, byte value)
    {
        var a = address & 0x0F;
        switch (a)
        {
            case 0x00:
            case 0x02:
            case 0x04:
            case 0x06:
                _audf[a >> 1] = value;
                break;
            case 0x01:
            case 0x03:
            case 0x05:
            case 0x07:
                _audc[a >> 1] = value;
                _dirty = true;
                break;
            case 0x08:
                _audctl = value;
                _dirty = true;
                break;
            case 0x09:
                // STIMER: every divider starts its period again.
                _count[0] = Period(0);
                _count[2] = Period(2);
                _count[1] = (_audctl & 0x10) != 0 ? Period16(0) : Period(1);
                _count[3] = (_audctl & 0x08) != 0 ? Period16(2) : Period(3);
                break;
            case 0x0B:
                // POTGO: the counters start from zero.
                _potCounter = 0;
                _allpot = 0xFF;
                LatchPots();
                break;
            case 0x0E:
                _irqEnable = value;
                _irqPending &= value;
                break;
            case 0x0F:
                if ((value & 3) == 0 && (_skctl & 3) != 0)
                {
                    ResetPolys();
                    _div64 = 28;
                    _div15 = 114;
                }

                _skctl = value;
                break;
        }
    }

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_audf);
        writer.WriteBytes(_audc);
        for (var n = 0; n < 4; n++)
        {
            writer.WriteInt32(_count[n]);
            writer.WriteBool(_output[n]);
        }

        writer.WriteBytes(_pot);
        writer.WriteByte(_audctl);
        writer.WriteByte(_irqEnable);
        writer.WriteByte(_irqPending);
        writer.WriteByte(_skctl);
        writer.WriteByte(_kbcode);
        writer.WriteByte(_allpot);
        writer.WriteInt32(_div64);
        writer.WriteInt32(_div15);
        writer.WriteInt32(_potDivider);
        writer.WriteInt32(_potCounter);
        writer.WriteInt32(_poly4);
        writer.WriteInt32(_poly5);
        writer.WriteInt32(_poly9);
        writer.WriteInt32(_poly17);
        writer.WriteBool(_highPass1);
        writer.WriteBool(_highPass2);
        writer.WriteInt32(_keyHeld);
        writer.WriteBool(_topHeld);
        writer.WriteInt32(SelectedController);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_audf);
        reader.ReadBytes(_audc);
        for (var n = 0; n < 4; n++)
        {
            _count[n] = Math.Clamp(reader.ReadInt32(), 1, 0x10007);
            _output[n] = reader.ReadBool();
        }

        reader.ReadBytes(_pot);
        _audctl = reader.ReadByte();
        _irqEnable = reader.ReadByte();
        _irqPending = reader.ReadByte();
        _skctl = reader.ReadByte();
        _kbcode = reader.ReadByte();
        _allpot = reader.ReadByte();
        _div64 = Math.Clamp(reader.ReadInt32(), 1, 28);
        _div15 = Math.Clamp(reader.ReadInt32(), 1, 114);
        _potDivider = Math.Clamp(reader.ReadInt32(), 1, 114);
        _potCounter = Math.Clamp(reader.ReadInt32(), 0, PotMax);
        _poly4 = reader.ReadInt32() & 0x0F;
        _poly5 = reader.ReadInt32() & 0x1F;
        _poly9 = reader.ReadInt32() & 0x1FF;
        _poly17 = reader.ReadInt32() & 0x1FFFF;
        _highPass1 = reader.ReadBool();
        _highPass2 = reader.ReadBool();
        _keyHeld = Math.Clamp(reader.ReadInt32(), NoKey, 15);
        _topHeld = reader.ReadBool();
        SelectedController = reader.ReadInt32() & 3;
        reader.EndChunk();
        _dirty = true;
    }
}
