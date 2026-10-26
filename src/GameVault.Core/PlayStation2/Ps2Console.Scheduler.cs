using System.Runtime.InteropServices;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

// The scheduler: the devices catch up with the EE, and the video timing (ps2tek: "GS", SMODE1; the NTSC and PAL
// line rates) drives H-blank and V-blank.
public sealed partial class Ps2Console
{
    // Line timing in EE cycles: NTSC 15,734.25 lines a second (59.94 fields of 262.5 lines), PAL 15,625 (50 × 312.5).
    private const int NtscLineCycles = 18743;
    private const int PalLineCycles = 18874;

    /// <summary>H-blank's length: about 10.9 µs of each line.</summary>
    private const int HBlankCycles = 3216;

    private const int NtscVisibleLines = 240;
    private const int PalVisibleLines = 288;

    private long _lastSync;
    private long _busRemainder;
    private long _vu0Time;
    private long _vu1Time;

    // Video timing: the line within the field, the field's length, the next event (H-blank's start, or the line's end).
    private int _line;
    private int _fieldLines = 263;
    private bool _oddField;
    private bool _pal;
    private long _lineStart;
    private long _nextVideoEvent;
    private bool _inHBlank;

    private int LineCycles => _pal ? PalLineCycles : NtscLineCycles;

    private int VisibleLines => _pal ? PalVisibleLines : NtscVisibleLines;

    private void ResetScheduler()
    {
        _lastSync = 0;
        _busRemainder = 0;
        _vu0Time = _vu1Time = 0;
        _line = 0;
        _oddField = false;
        _pal = Gs.PalMode;
        _fieldLines = _pal ? 313 : 263;
        _lineStart = 0;
        _inHBlank = false;
        _nextVideoEvent = LineCycles - HBlankCycles;
    }

    /// <summary>The next cycle the EE should stop at: a video event, a timer's interrupt, or the end of a slice.</summary>
    private long NextEvent()
    {
        var now = Cpu.Cycles;
        var next = Math.Min(_nextVideoEvent, now + MaxSlice);
        var timer = Timers.CyclesUntilNextEvent();
        if (timer != long.MaxValue)
        {
            next = Math.Min(next, now + Math.Max(1, timer * 2));
        }

        return Math.Max(next, now + 1);
    }

    /// <summary>Brings every device up to the EE's time and handles the video events that are due.</summary>
    private void Sync()
    {
        var now = Cpu.Cycles;
        var delta = now - _lastSync;
        if (delta > 0)
        {
            _lastSync = now;
            _busRemainder += delta;
            var bus = _busRemainder >> 1;
            _busRemainder &= 1;
            RunVectorUnits(now);
            Timers.Advance(bus);
            Dmac.Advance(bus);
            Ipu.Step();
        }

        Kernel?.Service();
        IopServices?.Service();
        CatchUpIop(now);

        // A VIF waiting for its VU or the GIF goes on once they are free.
        if (Vif0.Stalled || Vif0.FifoCount > 0)
        {
            Vif0.Resume();
        }

        if (Vif1.Stalled || Vif1.FifoCount > 0)
        {
            Vif1.Resume();
        }

        while (now >= _nextVideoEvent)
        {
            VideoEvent();
        }

        UpdateInterruptLines();
    }

    /// <summary>VU1 (and VU0's micro programs) run alongside the EE, at its clock.</summary>
    private void RunVectorUnits(long now)
    {
        if (Vu1.Running)
        {
            var budget = now - _vu1Time;
            if (budget > 0)
            {
                _vu1Time += Vu1.Run(budget);
            }

            if (!Vu1.Running)
            {
                _vu1Time = now;
            }
        }
        else
        {
            _vu1Time = now;
        }

        if (Vu0.Running)
        {
            var budget = now - _vu0Time;
            if (budget > 0)
            {
                _vu0Time += Vu0.Run(budget);
            }

            if (!Vu0.Running)
            {
                _vu0Time = now;
            }
        }
        else
        {
            _vu0Time = now;
        }
    }

    /// <summary>Runs the IOP (at an eighth of the EE's clock) up to the EE's time.</summary>
    private void CatchUpIop(long now)
    {
        if (!Iop.Halted)
        {
            Iop.ServiceSif();
        }

        Iop.Run(now / 8);
    }

    /// <summary>The EE's interrupt lines and CPCOND0 follow the INTC and the DMAC.</summary>
    internal void UpdateInterruptLines()
    {
        var int0 = Intc.Int0;
        if (Cpu.Int0 != int0)
        {
            Cpu.Int0 = int0;
        }

        var int1 = Dmac.Int1;
        if (Cpu.Int1 != int1)
        {
            Cpu.Int1 = int1;
        }

        Cpu.Cpcond0 = Dmac.Cpcond0;
    }

    /// <summary>The next video event: H-blank starts, or the line ends (H-blank ends, and V-blank starts or ends).</summary>
    private void VideoEvent()
    {
        if (!_inHBlank)
        {
            _inHBlank = true;
            Gs.HBlankStart();
            Timers.HBlankStart();
            Iop.SetHBlank(true);
            _nextVideoEvent = _lineStart + LineCycles;
            return;
        }

        _inHBlank = false;
        Timers.HBlankEnd();
        Iop.SetHBlank(false);
        _lineStart += LineCycles;
        _line++;
        if (_line == VisibleLines)
        {
            VBlankStart();
        }
        else if (_line >= _fieldLines)
        {
            VBlankEnd();
        }

        _nextVideoEvent = _lineStart + LineCycles - HBlankCycles;
    }

    private void VBlankStart()
    {
        CaptureFrame();
        Gs.VBlankStart();
        Timers.VBlankStart();
        Intc.Raise(Ps2Interrupt.VBlankStart);
        Iop.SetVBlank(true);
        Kernel?.VBlankStart();
    }

    /// <summary>The field ends: the next one starts, NTSC alternating 263 and 262 lines (PAL 313 and 312).</summary>
    private void VBlankEnd()
    {
        _line = 0;
        _oddField = !_oddField;
        _pal = Gs.PalMode;
        _fieldLines = (_pal ? 313 : 263) - (_oddField ? 1 : 0);
        Gs.VBlankEnd();
        Timers.VBlankEnd();
        Intc.Raise(Ps2Interrupt.VBlankEnd);
        Iop.SetVBlank(false);
    }

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_rdram);
        writer.WriteBytes(MemoryMarshal.AsBytes(_frame.AsSpan()));
        writer.WriteInt64(FrameCount);
        writer.WriteInt32(_frameWidth);
        writer.WriteInt32(_frameHeight);
        writer.WriteBool(Exited);
        writer.WriteInt32(ExitCode);
        writer.WriteInt64(_lastSync);
        writer.WriteInt64(_busRemainder);
        writer.WriteInt64(_vu0Time);
        writer.WriteInt64(_vu1Time);
        writer.WriteInt32(_line);
        writer.WriteInt32(_fieldLines);
        writer.WriteBool(_oddField);
        writer.WriteBool(_pal);
        writer.WriteInt64(_lineStart);
        writer.WriteInt64(_nextVideoEvent);
        writer.WriteBool(_inHBlank);
        SaveIo(writer);
        writer.EndChunk();
        Cpu.SaveState(writer);
        Vu0.SaveState(writer);
        Vu1.SaveState(writer);
        Gs.SaveState(writer);
        Gif.SaveState(writer);
        Vif0.SaveState(writer);
        Vif1.SaveState(writer);
        Dmac.SaveState(writer);
        Intc.SaveState(writer);
        Timers.SaveState(writer);
        Sif.SaveState(writer);
        Iop.SaveState(writer);
        Ipu.SaveState(writer);
        writer.WriteBool(Kernel is not null);
        Kernel?.SaveState(writer);
        IopServices?.SaveState(writer);
    }

    /// <remarks>The disc isn't part of the state: the caller keeps the same disc in the drive.</remarks>
    public void LoadState(StateReader reader)
    {
        if (reader.BeginChunk(Tag) != 1)
        {
            throw new StateFormatException("Unsupported PlayStation 2 state version.");
        }

        reader.ReadBytes(_rdram);
        reader.ReadBytes(MemoryMarshal.AsBytes(_frame.AsSpan()));
        FrameCount = reader.ReadInt64();
        _frameWidth = reader.ReadInt32();
        _frameHeight = reader.ReadInt32();
        Exited = reader.ReadBool();
        ExitCode = reader.ReadInt32();
        _lastSync = reader.ReadInt64();
        _busRemainder = reader.ReadInt64();
        _vu0Time = reader.ReadInt64();
        _vu1Time = reader.ReadInt64();
        _line = reader.ReadInt32();
        _fieldLines = reader.ReadInt32();
        _oddField = reader.ReadBool();
        _pal = reader.ReadBool();
        _lineStart = reader.ReadInt64();
        _nextVideoEvent = reader.ReadInt64();
        _inHBlank = reader.ReadBool();
        LoadIo(reader);
        reader.EndChunk();
        Cpu.LoadState(reader);
        Vu0.LoadState(reader);
        Vu1.LoadState(reader);
        Gs.LoadState(reader);
        Gif.LoadState(reader);
        Vif0.LoadState(reader);
        Vif1.LoadState(reader);
        Dmac.LoadState(reader);
        Intc.LoadState(reader);
        Timers.LoadState(reader);
        Sif.LoadState(reader);
        Iop.LoadState(reader);
        Ipu.LoadState(reader);

        if (reader.ReadBool() != (Kernel is not null))
        {
            throw new StateFormatException("The state was saved with a different boot (BIOS or GameVault's kernel).");
        }

        Kernel?.LoadState(reader);
        IopServices?.LoadState(reader);
        UpdateInterruptLines();
    }
}
