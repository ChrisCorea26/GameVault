using System.Text;
using GameVault.Core.PlayStation;
using GameVault.Core.PlayStation2;
using GameVault.Core.Systems;

namespace GameVault.Platform.Emulation;

/// <summary>
/// The first minute of a PlayStation or PlayStation 2 game, written to <c>logs\boot-ps1.log</c> or
/// <c>logs\boot-ps2.log</c> so a game that stops on the BIOS's screens can be diagnosed from the player's own BIOS and
/// disc (docs/bugs.md GV-023, GV-024). Once a second: where the processors are and the hardware registers they read most (a stuck
/// BIOS spins on one); the BIOS's own text output; on the PlayStation, every CD-ROM command and response. The probes
/// come off after the minute, so play runs at full speed. Emulation thread.
/// </summary>
internal sealed class BootLog : IDisposable
{
    private const int Seconds = 60;
    private const int FramesPerSecond = 60;
    private const int MaxCdLines = 4000;
    private const int TopRegisters = 6;

    private readonly IEmulatedConsole _console;
    private readonly StreamWriter _writer;
    private readonly Dictionary<uint, int> _mainReads = [];
    private readonly Dictionary<uint, int> _iopReads = [];
    private readonly StringBuilder _text = new();
    private int _frames;
    private int _cdLines;
    private bool _closed;

    private BootLog(IEmulatedConsole console, StreamWriter writer)
    {
        _console = console;
        _writer = writer;
    }

    /// <summary>Starts a log for a PlayStation or PlayStation 2 game; null for other systems or when the file can't be written.</summary>
    public static BootLog? Start(IEmulatedConsole console, string? logsDirectory, string game)
    {
        var name = console switch
        {
            PlayStationConsole => "boot-ps1.log",
            Ps2Console => "boot-ps2.log",
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

        var log = new BootLog(console, writer);
        writer.WriteLine($"GameVault boot log, {DateTime.Now:yyyy-MM-dd HH:mm:ss}: {game}");
        log.Attach();
        writer.Flush();
        return log;
    }

    private void Attach()
    {
        switch (_console)
        {
            case PlayStationConsole ps:
                _writer.WriteLine($"PlayStation, BIOS {(ps.HasBios ? "region " + ps.BiosRegion : "none")}, disc {(ps.CdRom.Disc is { } disc ? $"{disc.Tracks.Count} track(s), lead-out {disc.LeadOut}, licence region {ps.CdRom.DiscRegion() ?? "unknown"}" : "none")}");
                ps.IoReadProbe = address => Count(_mainReads, address);
                ps.TtyOutput = c => _text.Append(c);
                ps.CdRom.Trace = CdLine;
                break;
            case Ps2Console ps2:
                _writer.WriteLine("PlayStation 2");
                ps2.RegisterReadProbe = address => Count(_mainReads, address);
                ps2.Iop.DeviceReadProbe = address => Count(_iopReads, address);
                ps2.DebugOutput = c => _text.Append(c);
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
                break;
            case Ps2Console ps2:
                ps2.RegisterReadProbe = null;
                ps2.Iop.DeviceReadProbe = null;
                ps2.DebugOutput = null;
                break;
        }
    }

    private static void Count(Dictionary<uint, int> reads, uint address)
    {
        reads.TryGetValue(address, out var count);
        reads[address] = count + 1;
    }

    private void CdLine(string line)
    {
        if (_cdLines++ < MaxCdLines)
        {
            FlushText();
            _writer.WriteLine("  cd " + line);
        }
    }

    /// <summary>After each frame: once a second, a line of where the processors are; after a minute, the log closes.</summary>
    public void OnFrame()
    {
        if (_closed || ++_frames % FramesPerSecond != 0)
        {
            return;
        }

        try
        {
            FlushText();
            var second = _frames / FramesPerSecond;
            var line = new StringBuilder($"[{second,2}s] ");
            switch (_console)
            {
                case PlayStationConsole ps:
                    line.Append($"pc {ps.Cpu.Pc:X8}  reads {Top(_mainReads)}");
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
