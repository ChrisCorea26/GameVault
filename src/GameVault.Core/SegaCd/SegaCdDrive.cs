using GameVault.Core.PlayStation;
using GameVault.Core.State;

namespace GameVault.Core.SegaCd;

/// <summary>
/// The Sega CD's drive controller, the CDD (sega-cd-32x.md): the sub CPU writes a ten-nibble command to $FF8042–$FF804B
/// and reads a ten-nibble status from $FF8038–$FF8041; seventy-five times a second (the level 4 interrupt) the drive
/// moves on a sector, handing data sectors to the CDC and audio sectors to the CD-DA output.
/// </summary>
/// <remarks>
/// Status nibbles: [0] the drive's state, [1] what the report holds, [2–7] a time as BCD minutes, seconds and frames
/// (or a track number), [8] a track number's low digit or flags, [9] the checksum (the inverted low nibble of the sum
/// of the others). Commands: 0 status, 1 stop, 2 read TOC (the report type in nibble 3, a track in 4–5), 3 play and 4
/// seek (an absolute time in nibbles 2–7), 6 pause, 7 resume, 8 fast forward, 9 rewind, C close and D open the tray.
/// Disc addresses are absolute times in frames (track 1 starts at 00:02:00, address 150), as in <see cref="ICdImage"/>.
/// Behaviour follows Genesis Plus GX's cdd.c, read for behaviour only.
/// </remarks>
public sealed class SegaCdDrive : IStateful
{
    // The drive's states (status nibble 0).
    public const int Stopped = 0x0, Playing = 0x1, Seeking = 0x2, Scanning = 0x3, Paused = 0x4, TrayOpen = 0x5;
    public const int StoppedAfterCommand = 0x9, NoDisc = 0xB, EndOfDisc = 0xC;

    private static readonly StateTag Tag = StateTag.From("CDD ");

    private readonly byte[] _status = new byte[10];
    private readonly byte[] _command = new byte[10];
    private readonly byte[] _sector = new byte[ICdImage.SectorSize];
    private readonly short[] _audio = new short[588 * 2];
    private readonly byte[] _subcode = new byte[ICdSubcode.Size];
    private int _state;
    private int _position;
    private int _seekTarget;
    private int _seekTicks;
    private bool _playAfterSeek;
    private int _report;
    private int _reportTrack;
    private int _scanDirection;
    private bool _audioValid;

    public SegaCdDrive(ICdImage? disc) => Disc = disc;

    public ICdImage? Disc { get; }

    /// <summary>The drive's state (status nibble 0).</summary>
    public int State => _state;

    /// <summary>The disc address the drive is at.</summary>
    public int Position => _position;

    /// <summary>The CD-DA sector last read: 588 stereo samples, or silence.</summary>
    public ReadOnlySpan<short> AudioSector => _audio;

    public bool AudioPlaying => _audioValid && _state == Playing;

    /// <summary>
    /// The last tick played a sector and the disc has subcode: <see cref="Subcode"/> holds its 96 symbols for the
    /// gate array's subcode buffer (genesis-gaps.md §4). Cleared at the start of each tick.
    /// </summary>
    public bool SubcodeReady { get; private set; }

    public ReadOnlySpan<byte> Subcode => _subcode;

    public Span<byte> Command => _command;

    public ReadOnlySpan<byte> Status => _status;

    public void Reset()
    {
        Array.Clear(_command);
        Array.Clear(_audio);
        _state = Disc is null ? NoDisc : Stopped;
        _position = 150;
        _seekTarget = 150;
        _seekTicks = 0;
        _playAfterSeek = false;
        _report = 0;
        _reportTrack = 1;
        _scanDirection = 0;
        _audioValid = false;
        Report();
    }

    /// <summary>The sub CPU finished writing a command (its write to $FF804B).</summary>
    public void ProcessCommand()
    {
        switch (_command[0] & 0x0F)
        {
            case 0x0: // status: the report stays as it is
                break;
            case 0x1:
                _state = Disc is null ? NoDisc : StoppedAfterCommand;
                _audioValid = false;
                _report = 0;
                break;
            case 0x2:
                _report = _command[3] & 0x0F;
                _reportTrack = ((_command[4] & 0x0F) * 10) + (_command[5] & 0x0F);
                break;
            case 0x3:
            case 0x4:
                if (Disc is null)
                {
                    break;
                }

                var target = FromBcd(_command[2], _command[3]) * 60 * 75 + FromBcd(_command[4], _command[5]) * 75 + FromBcd(_command[6], _command[7]);
                _seekTarget = Math.Clamp(target, 0, Disc.LeadOut);
                _seekTicks = 2 + Math.Abs(_seekTarget - _position) / 4500;
                _playAfterSeek = (_command[0] & 0x0F) == 0x3;
                _state = Seeking;
                _audioValid = false;
                _report = 0;
                break;
            case 0x6:
                if (Disc is not null)
                {
                    _state = Paused;
                    _audioValid = false;
                }

                break;
            case 0x7:
                if (Disc is not null)
                {
                    _state = Playing;
                }

                break;
            case 0x8:
            case 0x9:
                if (Disc is not null)
                {
                    _scanDirection = (_command[0] & 0x0F) == 0x8 ? 1 : -1;
                    _state = Scanning;
                }

                break;
            case 0xC:
                _state = Disc is null ? NoDisc : Stopped;
                break;
            case 0xD:
                _state = TrayOpen;
                break;
        }

        Report();
    }

    /// <summary>A 75 Hz tick: seeks progress, and while playing one sector goes to the CDC (data) or the CD-DA output (audio).</summary>
    public void Tick(Lc8951 cdc)
    {
        SubcodeReady = false;
        if (Disc is not { } disc)
        {
            return;
        }

        switch (_state)
        {
            case Seeking:
                if (--_seekTicks <= 0)
                {
                    _position = _seekTarget;
                    _state = _playAfterSeek ? Playing : Paused;
                }

                break;
            case Playing:
                if (_position >= disc.LeadOut)
                {
                    _state = EndOfDisc;
                    _audioValid = false;
                    break;
                }

                disc.ReadSector(_position, _sector);
                if (disc is ICdSubcode { HasSubcode: true } subcode)
                {
                    subcode.ReadSubcode(_position, _subcode);
                    SubcodeReady = true;
                }

                if (TrackAt(_position) is { Type: CdTrackType.Audio })
                {
                    for (var i = 0; i < _audio.Length; i++)
                    {
                        _audio[i] = (short)(_sector[i * 2] | (_sector[i * 2 + 1] << 8));
                    }

                    _audioValid = true;
                }
                else
                {
                    _audioValid = false;
                    cdc.Decode(_sector);
                }

                _position++;
                break;
            case Scanning:
                _position = Math.Clamp(_position + _scanDirection * 30, 150, disc.LeadOut);
                break;
        }

        Report();
    }

    private CdTrack? TrackAt(int address)
    {
        if (Disc is null)
        {
            return null;
        }

        CdTrack? found = null;
        foreach (var track in Disc.Tracks)
        {
            if (address >= track.Start - track.Pregap)
            {
                found = track;
            }
        }

        return found;
    }

    /// <summary>Fills the status nibbles from the drive's state and the report the last command asked for.</summary>
    private void Report()
    {
        Array.Clear(_status);
        _status[0] = (byte)_state;
        _status[1] = (byte)_report;
        if (Disc is { } disc)
        {
            switch (_report)
            {
                case 0:
                    PutTime(_position);
                    break;
                case 1:
                    PutTime(Math.Max(0, _position - (TrackAt(_position)?.Start ?? 150)));
                    break;
                case 2:
                    PutBcd(2, TrackAt(_position)?.Number ?? 1);
                    break;
                case 3:
                    PutTime(disc.LeadOut);
                    break;
                case 4:
                    PutBcd(2, 1);
                    PutBcd(4, disc.Tracks[^1].Number);
                    break;
                case 5:
                {
                    var track = _reportTrack >= 1 && _reportTrack <= disc.Tracks.Count ? disc.Tracks[_reportTrack - 1] : disc.Tracks[0];
                    PutTime(track.Start);
                    if (track.Type != CdTrackType.Audio)
                    {
                        _status[6] |= 0x08; // a data track
                    }

                    _status[8] = (byte)(track.Number % 10);
                    break;
                }
            }
        }

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += _status[i];
        }

        _status[9] = (byte)(~sum & 0x0F);
    }

    private void PutTime(int frames)
    {
        PutBcd(2, frames / 75 / 60);
        PutBcd(4, frames / 75 % 60);
        PutBcd(6, frames % 75);
    }

    private void PutBcd(int at, int value)
    {
        _status[at] = (byte)(value / 10 % 10);
        _status[at + 1] = (byte)(value % 10);
    }

    private static int FromBcd(byte tens, byte ones) => (tens & 0x0F) * 10 + (ones & 0x0F);

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_status);
        writer.WriteBytes(_command);
        writer.WriteInt32(_state);
        writer.WriteInt32(_position);
        writer.WriteInt32(_seekTarget);
        writer.WriteInt32(_seekTicks);
        writer.WriteBool(_playAfterSeek);
        writer.WriteInt32(_report);
        writer.WriteInt32(_reportTrack);
        writer.WriteInt32(_scanDirection);
        writer.WriteBool(_audioValid);
        foreach (var sample in _audio)
        {
            writer.WriteUInt16((ushort)sample);
        }

        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_status);
        reader.ReadBytes(_command);
        _state = reader.ReadInt32() & 0x0F;
        _position = Math.Max(0, reader.ReadInt32());
        _seekTarget = Math.Max(0, reader.ReadInt32());
        _seekTicks = reader.ReadInt32();
        _playAfterSeek = reader.ReadBool();
        _report = reader.ReadInt32() & 0x0F;
        _reportTrack = reader.ReadInt32();
        _scanDirection = Math.Sign(reader.ReadInt32());
        _audioValid = reader.ReadBool();
        for (var i = 0; i < _audio.Length; i++)
        {
            _audio[i] = (short)reader.ReadUInt16();
        }

        reader.EndChunk();
    }
}
