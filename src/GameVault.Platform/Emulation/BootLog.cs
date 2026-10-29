using System.Text;
using GameVault.Core.Input;
using GameVault.Core.PlayStation;
using GameVault.Core.PlayStation2;
using GameVault.Core.Systems;

namespace GameVault.Platform.Emulation;

/// <summary>
/// The first minute of a PlayStation or PlayStation 2 game, written to <c>logs\boot-ps1.log</c> or
/// <c>logs\boot-ps2.log</c> so a game that stops on the BIOS's screens can be diagnosed from the player's own BIOS and
/// disc (docs/bugs.md GV-023, GV-024). Once a second: where the processors are and the hardware registers they read most (a stuck
/// BIOS spins on one); the BIOS's own text output; on the PlayStation, every CD-ROM command and response. The probes
/// come off after the minute, so play runs at full speed. Emulation thread, apart from the watchdog: when no frame
/// finishes for a few seconds (the emulator itself is stuck, not the game), it writes from its own thread where the
/// processors are, the last registers each wrote, and the emulation thread's stack.
/// </summary>
internal sealed class BootLog : IDisposable
{
    private const int Seconds = 60;
    private const int FramesPerSecond = 60;
    private const int MaxCdLines = 4000;
    private const int TopRegisters = 6;
    private const int WriteHistory = 16;
    private const int MaxStallReports = 3;

    private readonly IEmulatedConsole _console;
    private readonly StreamWriter _writer;
    private readonly Dictionary<uint, int> _mainReads = [];
    private readonly Dictionary<uint, int> _iopReads = [];
    private readonly StringBuilder _text = new();
    private readonly object _gate = new();
    private readonly WriteRing _mainWrites = new();
    private readonly WriteRing _iopWrites = new();
    private readonly int _emulationThread = Environment.CurrentManagedThreadId;
    private readonly int _stallTicks;
    private readonly Timer _watchdog;
    private int _frames;
    private int _cdLines;
    private ControllerState _padsHeld;
    private int _padPolls;
    private readonly StringBuilder _packet = new();
    private string _lastPacket = "none";
    private Func<long>? _keyPresses;
    private long _keysAtLastSecond;
    private bool _closed;
    private int _framesAtLastTick;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan _lastSecond;
    private int _stalledTicks;
    private int _stallReports;

    private BootLog(IEmulatedConsole console, StreamWriter writer, TimeSpan tick, int stallTicks)
    {
        _console = console;
        _writer = writer;
        _stallTicks = stallTicks;
        _watchdog = new Timer(_ => Watch(), null, tick, tick);
    }

    /// <summary>Starts a log for a PlayStation, PlayStation 2 or Atari 5200 game; null for other systems or when the file can't be written.</summary>
    /// <param name="tick">How often the watchdog looks (tests shorten it); a second by default.</param>
    /// <param name="stallTicks">Ticks without a frame before it reports a stall.</param>
    /// <param name="keyPresses">The key presses the game picture has passed to the input service so far.</param>
    public static BootLog? Start(
        IEmulatedConsole console, string? logsDirectory, string game, TimeSpan? tick = null, int stallTicks = 3, Func<long>? keyPresses = null)
    {
        var name = console switch
        {
            PlayStationConsole => "boot-ps1.log",
            Ps2Console => "boot-ps2.log",
            GameVault.Core.Atari.Atari5200Console => "boot-5200.log",
            _ => null,
        };
        if (name is null || logsDirectory is null)
        {
            return null;
        }

        StreamWriter writer;
        try
        {
            Directory.CreateDirectory(logsDirectory);
            writer = new StreamWriter(Path.Combine(logsDirectory, name), append: false, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

#if DEBUG
        const string build = "Debug build (the emulation core is still optimized)";
#else
        const string build = "Release build";
#endif
        writer.WriteLine($"GameVault boot log, {DateTime.Now:yyyy-MM-dd HH:mm:ss}: {game}");
        writer.WriteLine($"{build}, .NET {Environment.Version}, {Environment.ProcessorCount} logical processors");
        var log = new BootLog(console, writer, tick ?? TimeSpan.FromSeconds(1), stallTicks) { _keyPresses = keyPresses };
        lock (log._gate)
        {
            log.Attach();
            writer.Flush();
        }

        return log;
    }

    private void Attach()
    {
        switch (_console)
        {
            case PlayStationConsole ps:
                _writer.WriteLine($"PlayStation, BIOS {(ps.HasBios ? "region " + ps.BiosRegion : "none")}, disc {(ps.CdRom.Disc is { } disc ? $"{disc.Tracks.Count} track(s), lead-out {disc.LeadOut}, licence region {ps.CdRom.DiscRegion() ?? "unknown"}" : "none")}");
                ps.IoReadProbe = address => Count(_mainReads, address);
                ps.TtyOutput = Print;
                ps.CdRom.Trace = CdLine;
                ps.Sio.Trace = PadByte;
                break;
            case Ps2Console ps2:
                _writer.WriteLine("PlayStation 2");
                ps2.RegisterReadProbe = address => Count(_mainReads, address);
                ps2.Iop.DeviceReadProbe = address => Count(_iopReads, address);
                ps2.RegisterWriteProbe = _mainWrites.Add;
                ps2.Iop.DeviceWriteProbe = _iopWrites.Add;
                ps2.DebugOutput = Print;
                break;
            case GameVault.Core.Atari.Atari5200Console a5200:
                var rom = a5200.Cartridge.Rom;
                var (low, high) = GameVault.Core.Atari.Atari5200Cartridge.CallTargets(rom);
                _writer.WriteLine($"Atari 5200, cartridge {rom.Length / 1024} KB, board {a5200.Cartridge.Board} (calls into $4000-$7FFF: {low}, into $8000-$BFFF: {high}), start ${rom[^1]:X2}{rom[^2]:X2}, $BFFD = {rom[^3]:X2}");
                a5200.IoReadProbe = address => Count(_mainReads, address);
                break;
        }
    }

    private void Detach()
    {
        switch (_console)
        {
            case PlayStationConsole ps:
                ps.IoReadProbe = null;
                ps.TtyOutput = null;
                ps.CdRom.Trace = null;
                ps.Sio.Trace = null;
                break;
            case Ps2Console ps2:
                ps2.RegisterReadProbe = null;
                ps2.Iop.DeviceReadProbe = null;
                ps2.RegisterWriteProbe = null;
                ps2.Iop.DeviceWriteProbe = null;
                ps2.DebugOutput = null;
                break;
            case GameVault.Core.Atari.Atari5200Console a5200:
                a5200.IoReadProbe = null;
                break;
        }
    }

    private static void Count(Dictionary<uint, int> reads, uint address)
    {
        reads.TryGetValue(address, out var count);
        reads[address] = count + 1;
    }

    /// <summary>A byte on the controller port: conversations are counted, and the last one is kept byte by byte.</summary>
    private void PadByte(bool first, byte sent, byte received, bool ack)
    {
        if (first)
        {
            if (_packet.Length > 0)
            {
                _lastPacket = _packet.ToString();
                _packet.Clear();
            }

            _padPolls++;
        }

        if (_packet.Length < 120)
        {
            _packet.Append($"{sent:X2}>{received:X2}{(ack ? "" : "!")} ");
        }
    }

    private void Print(char c)
    {
        lock (_gate)
        {
            _text.Append(c);
        }
    }

    private void CdLine(string line)
    {
        lock (_gate)
        {
            if (_cdLines++ < MaxCdLines)
            {
                FlushText();
                _writer.WriteLine("  cd " + line);
            }
        }
    }

    /// <summary>After each frame: once a second, a line of where the processors are; after a minute, the log closes.</summary>
    public void OnFrame()
    {
        if (!_closed && _console is PlayStationConsole held)
        {
            _padsHeld |= held.Sio.Pad(0).Input;
        }

        if (_closed || Interlocked.Increment(ref _frames) % FramesPerSecond != 0)
        {
            return;
        }

        lock (_gate)
        {
            WriteSecond();
        }
    }

    private void WriteSecond()
    {
        try
        {
            FlushText();
            var second = _frames / FramesPerSecond;
            var elapsed = _clock.Elapsed - _lastSecond;
            _lastSecond = _clock.Elapsed;
            var line = new StringBuilder($"[{second,2}s] ({elapsed.TotalSeconds:F2} s of real time for 60 frames) ");
            switch (_console)
            {
                case PlayStationConsole ps:
                    var (status, mask, dma) = ps.InterruptRegisters;
                    line.Append($"pc {ps.Cpu.Pc:X8}  I_STAT {status:X3} I_MASK {mask:X3} DICR {dma:X8}  reads {Top(_mainReads)}");
                    var keys = _keyPresses?.Invoke() ?? 0;
                    line.Append($"\n      keys pressed in the game picture: {keys - _keysAtLastSecond}  pad 1 held: {(_padsHeld == ControllerState.None ? "nothing" : _padsHeld.ToString())}  port conversations: {_padPolls}  last: {_lastPacket.TrimEnd()}");
                    _keysAtLastSecond = keys;
                    _padsHeld = ControllerState.None;
                    _padPolls = 0;
                    break;
                case GameVault.Core.Atari.Atari5200Console a5200:
                    var (dmactl, nmien, dlist) = a5200.Antic.Registers;
                    line.Append($"pc {a5200.Cpu.PC:X4}{(a5200.Cpu.IsJammed ? " JAMMED" : "")}  DMACTL {dmactl:X2} NMIEN {nmien:X2} DLIST {dlist:X4}  reads {Top(_mainReads)}");
                    var ram = a5200.Ram;
                    line.Append($"\n      shadows: SDLST {ram[6]:X2}{ram[5]:X2} SDMCTL {ram[7]:X2}  vectors $0200: {Convert.ToHexString(ram.Slice(0x200, 12))}");
                    break;
                case Ps2Console ps2:
                    line.Append($"EE pc {ps2.Cpu.Pc:X8}  IOP pc {ps2.Iop.Cpu.Pc:X8}  PMODE {ps2.Gs.ReadPrivileged(Ps2Gs.PrivPmode):X}  SMODE2 {ps2.Gs.ReadPrivileged(Ps2Gs.PrivSmode2):X}");
                    line.Append($"\n      EE reads {Top(_mainReads)}\n      IOP reads {Top(_iopReads)}");
                    break;
            }

            _writer.WriteLine(line.ToString());
            _mainReads.Clear();
            _iopReads.Clear();
            if (second >= Seconds)
            {
                _writer.WriteLine("(end of the boot log)");
                Dispose();
            }
            else
            {
                _writer.Flush();
            }
        }
        catch (IOException)
        {
            Dispose();
        }
    }

    /// <summary>The watchdog's tick (its own thread): counts ticks without a new frame and reports a stall.</summary>
    private void Watch()
    {
        lock (_gate)
        {
            if (_closed || _stallReports >= MaxStallReports)
            {
                return;
            }

            var frames = Volatile.Read(ref _frames);
            if (frames != _framesAtLastTick)
            {
                _framesAtLastTick = frames;
                _stalledTicks = 0;
                return;
            }

            if (++_stalledTicks % (_stallTicks * (_stallReports + 1)) != 0)
            {
                return;
            }

            try
            {
                ReportStall();
                _stallReports++;
                _stalledTicks = 0;
                _writer.Flush();
            }
            catch (IOException)
            {
                _stallReports = MaxStallReports;
            }
        }
    }

    private void ReportStall()
    {
        FlushText();
        _writer.WriteLine($"[stall] no frame has finished for {_stalledTicks} watchdog ticks, after frame {_frames}");
        switch (_console)
        {
            case PlayStationConsole ps:
                _writer.WriteLine($"      pc {ps.Cpu.Pc:X8}  cycles {ps.Cpu.Cycles}");
                break;
            case Ps2Console ps2:
                _writer.WriteLine($"      EE pc {ps2.Cpu.Pc:X8} cycles {ps2.Cpu.Cycles}  IOP pc {ps2.Iop.Cpu.Pc:X8} cycles {ps2.Iop.Cycles}");
                _writer.WriteLine($"      EE's last writes (oldest first): {_mainWrites}");
                _writer.WriteLine($"      IOP's last writes (oldest first): {_iopWrites}");
                break;
        }

        _writer.WriteLine("      emulation thread:");
        foreach (var frame in EmulationStack())
        {
            _writer.WriteLine("        " + frame);
        }
    }

    /// <summary>The emulation thread's managed stack, read from a snapshot of this process (ClrMD).</summary>
    private List<string> EmulationStack()
    {
        var frames = new List<string>();
        try
        {
            using var target = Microsoft.Diagnostics.Runtime.DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            // The runtime's own folder holds the DAC that matches it (mscordaccore.dll; libmscordaccore.so elsewhere).
            var folder = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var dac = new[] { "mscordaccore.dll", "libmscordaccore.so", "libmscordaccore.dylib" }
                .Select(name => Path.Combine(folder, name))
                .FirstOrDefault(File.Exists);
            using var runtime = dac is null ? target.ClrVersions[0].CreateRuntime() : target.ClrVersions[0].CreateRuntime(dac, ignoreMismatch: true);
            foreach (var thread in runtime.Threads)
            {
                if (thread.ManagedThreadId != _emulationThread)
                {
                    continue;
                }

                foreach (var frame in thread.EnumerateStackTrace())
                {
                    if (frame.Method is { } method)
                    {
                        frames.Add(method.Signature ?? method.Name ?? "?");
                    }

                    if (frames.Count >= 40)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            frames.Add($"(the stack couldn't be read: {ex.GetType().Name}: {ex.Message})");
        }

        if (frames.Count == 0)
        {
            frames.Add("(no managed frames found)");
        }

        return frames;
    }

    private void FlushText()
    {
        if (_text.Length == 0)
        {
            return;
        }

        foreach (var text in _text.ToString().Split('\n'))
        {
            if (text.Length > 0)
            {
                _writer.WriteLine("  tty " + text.TrimEnd('\r'));
            }
        }

        _text.Clear();
    }

    private static string Top(Dictionary<uint, int> reads)
    {
        if (reads.Count == 0)
        {
            return "none";
        }

        return string.Join("  ", reads.OrderByDescending(r => r.Value).Take(TopRegisters).Select(r => $"{r.Key:X8}×{r.Value}"));
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _watchdog.Dispose();
        lock (_gate)
        {
            _closed = true;
            Detach();
            try
            {
                FlushText();
                _writer.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The last register writes, kept in a ring; read racily by the watchdog, which only reports them.</summary>
    private sealed class WriteRing
    {
        private readonly uint[] _addresses = new uint[WriteHistory];
        private readonly uint[] _values = new uint[WriteHistory];
        private int _count;

        public void Add(uint address, uint value)
        {
            var slot = _count++ & (WriteHistory - 1);
            _addresses[slot] = address;
            _values[slot] = value;
        }

        public override string ToString()
        {
            var count = Math.Min(_count, WriteHistory);
            if (count == 0)
            {
                return "none";
            }

            var text = new StringBuilder();
            for (var i = _count - count; i < _count; i++)
            {
                var slot = i & (WriteHistory - 1);
                text.Append($"{_addresses[slot]:X8}={_values[slot]:X8} ");
            }

            return text.ToString().TrimEnd();
        }
    }
}
