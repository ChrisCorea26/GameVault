using GameVault.Core.State;

namespace GameVault.Core.Apple;

/// <summary>
/// The Disk II controller in slot 6 with two drives (SPEC §7.11, Phase 12). Bits pass under the head every 4 CPU
/// cycles while the motor runs. Reads shift them into the data latch: a byte is complete when its high bit is set,
/// and it stays readable for two bit times while the next bits gather (as the controller's sequencer holds it), so
/// self-sync bytes and copy-protected tracks behave as on the real drive. Writes shift the data register out a bit
/// at a time, so 40-cycle sync bytes become 10-bit ones.
/// </summary>
/// <remarks>
/// References: Jim Sather, "Understanding the Apple II", chapter 9; "Beneath Apple DOS", chapter 6.
/// </remarks>
public sealed class DiskII : IStateful
{
    private const int CyclesPerBit = 4;
    private static readonly StateTag Tag = StateTag.From("DSK2");

    private readonly Drive[] _drives = [new(), new()];
    private int _selected;
    private bool _motorOn;
    private bool _q6;
    private bool _q7;
    private byte _latch;
    private byte _pending;
    private int _holdBits;
    private byte _writeRegister;
    private long _lastCycle;
    private int _bitCycles;

    /// <summary>The drive (0 or 1) a disk goes in.</summary>
    public void Insert(int drive, AppleDisk? disk)
    {
        _drives[drive].Disk = disk;
        _drives[drive].BitPosition = 0;
    }

    public AppleDisk? DiskIn(int drive) => _drives[drive].Disk;

    public bool MotorOn => _motorOn;

    /// <summary>The selected drive's head position in quarter tracks.</summary>
    public int QuarterTrack => _drives[_selected].HalfTrack * 2;

    public int SelectedDrive => _selected;

    public void Reset()
    {
        _motorOn = false;
        _q6 = _q7 = false;
        foreach (var drive in _drives)
        {
            drive.Phases = 0;
        }
    }

    /// <summary>An access to $C0E0–$C0EF at CPU cycle <paramref name="cycle"/>. Returns the data bus value for reads.</summary>
    public byte Access(int offset, bool write, byte value, long cycle)
    {
        Advance(cycle);
        var drive = _drives[_selected];
        switch (offset & 0x0F)
        {
            case <= 7:
            {
                var phase = (offset >> 1) & 3;
                drive.Phases = (offset & 1) != 0 ? drive.Phases | (1 << phase) : drive.Phases & ~(1 << phase);
                drive.Step();
                break;
            }

            case 8:
                _motorOn = false;
                break;
            case 9:
                _motorOn = true;
                break;
            case 0xA:
                _selected = 0;
                break;
            case 0xB:
                _selected = 1;
                break;
            case 0xC:
                _q6 = false;
                break;
            case 0xD:
                _q6 = true;
                break;
            case 0xE:
                _q7 = false;
                break;
            case 0xF:
                _q7 = true;
                break;
        }

        if (write && _q7 && _q6)
        {
            _writeRegister = value; // the data register loads; bits go out from the next bit time
        }

        if ((offset & 1) != 0)
        {
            return 0;
        }

        // Q6 on, Q7 off: the write-protect sense shows on bit 7.
        return _q6 && !_q7 ? (byte)((drive.Disk?.WriteProtected ?? false ? 0x80 : 0) | (_latch & 0x7F)) : _latch;
    }

    /// <summary>Moves the disk under the head up to <paramref name="cycle"/>.</summary>
    public void Advance(long cycle)
    {
        var elapsed = cycle - _lastCycle;
        _lastCycle = cycle;
        if (!_motorOn || elapsed <= 0)
        {
            return;
        }

        var drive = _drives[_selected];
        var track = drive.Disk?.TrackAt(drive.HalfTrack * 2);
        _bitCycles += (int)Math.Min(elapsed, 1_000_000);
        while (_bitCycles >= CyclesPerBit)
        {
            _bitCycles -= CyclesPerBit;
            if (track is null)
            {
                Shift(false); // no disk or no track: nothing but zeros
                continue;
            }

            var position = drive.BitPosition % track.BitCount;
            drive.BitPosition = position + 1;
            if (_q7)
            {
                if (!drive.Disk!.WriteProtected)
                {
                    track[position] = (_writeRegister & 0x80) != 0;
                    drive.Disk.IsModified = true;
                }

                _writeRegister <<= 1;
            }
            else
            {
                Shift(track[position]);
            }
        }
    }

    private void Shift(bool bit)
    {
        if ((_latch & 0x80) != 0 && _holdBits > 0)
        {
            // A complete byte stays visible for two bit times while the next bits gather.
            _pending = (byte)((_pending << 1) | (bit ? 1 : 0));
            if (--_holdBits == 0)
            {
                _latch = _pending;
                _pending = 0;
            }

            return;
        }

        if ((_latch & 0x80) != 0)
        {
            // Zero bits after a complete byte are dropped (self-sync); a one starts the next byte.
            if (!bit)
            {
                return;
            }

            _latch = 1;
            return;
        }

        _latch = (byte)((_latch << 1) | (bit ? 1 : 0));
        if ((_latch & 0x80) != 0)
        {
            _holdBits = 2;
            _pending = 0;
        }
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var drive in _drives)
        {
            writer.WriteInt32(drive.HalfTrack);
            writer.WriteInt32(drive.Phases);
            writer.WriteInt32(drive.BitPosition);
        }

        writer.WriteByte((byte)_selected);
        writer.WriteBool(_motorOn);
        writer.WriteBool(_q6);
        writer.WriteBool(_q7);
        writer.WriteByte(_latch);
        writer.WriteByte(_pending);
        writer.WriteByte((byte)_holdBits);
        writer.WriteByte(_writeRegister);
        writer.WriteInt64(_lastCycle);
        writer.WriteInt32(_bitCycles);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        foreach (var drive in _drives)
        {
            drive.HalfTrack = reader.ReadInt32();
            drive.Phases = reader.ReadInt32();
            drive.BitPosition = reader.ReadInt32();
        }

        _selected = reader.ReadByte();
        _motorOn = reader.ReadBool();
        _q6 = reader.ReadBool();
        _q7 = reader.ReadBool();
        _latch = reader.ReadByte();
        _pending = reader.ReadByte();
        _holdBits = reader.ReadByte();
        _writeRegister = reader.ReadByte();
        _lastCycle = reader.ReadInt64();
        _bitCycles = reader.ReadInt32();
        reader.EndChunk();
    }

    private sealed class Drive
    {
        public AppleDisk? Disk { get; set; }

        /// <summary>The head, in half tracks (0–79).</summary>
        public int HalfTrack { get; set; }

        /// <summary>Energized stepper phases, one bit each.</summary>
        public int Phases { get; set; }

        public int BitPosition { get; set; }

        /// <summary>The head moves half a track toward an energized phase next to its own.</summary>
        public void Step()
        {
            var current = HalfTrack & 3;
            if ((Phases & (1 << ((current + 1) & 3))) != 0)
            {
                HalfTrack = Math.Min(79, HalfTrack + 1);
            }
            else if ((Phases & (1 << ((current + 3) & 3))) != 0)
            {
                HalfTrack = Math.Max(0, HalfTrack - 1);
            }
        }
    }
}
