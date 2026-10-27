using GameVault.Core.Audio;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu;
using GameVault.Core.Input;
using GameVault.Core.State;
using GameVault.Core.Systems;

namespace GameVault.Core.Atari;

/// <summary>
/// The Atari 2600 (SPEC §7.11, phase 19): a 6507 (GameVault's NMOS 6502 on a 13-bit bus) at 1.19 MHz, the TIA and
/// the RIOT. Each CPU cycle runs the TIA three colour clocks and the RIOT one cycle; WSYNC holds the CPU until the
/// line's end. A frame is whatever the game draws between two VSYNCs.
/// </summary>
/// <remarks>
/// The bus (Stella Programmer's Guide): A12 set is the cartridge; otherwise A7 clear is the TIA (writes to $00–$3F,
/// reads of $00–$0D), and A7 set is the RIOT: its RAM with A9 clear, its ports and timer with A9 set.
/// </remarks>
public sealed class Atari2600Console : IEmulatedConsole, ICpuBus
{
    /// <summary>The NTSC CPU clock: the colour clock (3.579545 MHz) over three.</summary>
    public const double NtscCpuClock = 3_579_545.0 / 3;

    private static readonly StateTag Tag = StateTag.From("A26 ");
    private static readonly StateTag SnapshotTag = StateTag.From("GVA2");

    private readonly ushort[] _frame = new ushort[Tia.Width * Tia.MaxHeight];
    private readonly ControllerState[] _pads = new ControllerState[2];
    private int _frameCycles;

    public Atari2600Console(Atari2600Cartridge cartridge)
    {
        Cartridge = cartridge;
        Cpu = new Cpu6502(this, CpuVariant.Nmos6502);
        RomHash = HashRom(cartridge.Rom);
    }

    public Atari2600Cartridge Cartridge { get; }

    public Cpu6502 Cpu { get; }

    public Tia Tia { get; } = new();

    public Riot Riot { get; } = new();

    public GameSystem System => GameSystem.Atari2600;

    public ulong RomHash { get; }

    public int ScreenWidth => Tia.Width;

    public int ScreenHeight => Tia.Height;

    /// <summary>A pixel is about 1.9 times as wide as tall: 160 of them across a 4:3 picture of about 228 lines.</summary>
    public double PixelAspect => 4.0 / 3.0 / (Tia.Width / 228.0);

    public ReadOnlySpan<ushort> FrameBuffer => _frame.AsSpan(0, Tia.Width * Tia.Height);

    public ReadOnlySpan<uint> Palette => Atari2600Palette.Argb;

    public long FrameCount { get; private set; }

    public double CpuClockRate => NtscCpuClock;

    public BandLimitedResampler? Audio { get; private set; }

    public int ControllerPorts => 2;

    public IBatteryBacked? Battery => null;

    public BandLimitedResampler EnableAudio(int sampleRate = 48000)
    {
        Audio ??= new BandLimitedResampler(CpuClockRate, sampleRate);
        Tia.AudioSink = Audio;
        return Audio;
    }

    public void Power()
    {
        Tia.Power();
        Riot.Power();
        Cartridge.Power();
        Array.Clear(_frame);
        _frameCycles = 0;
        FrameCount = 0;
        Cpu.Reset();
    }

    /// <summary>The console's Reset switch is a game input; this is the power button's reset (the CPU's).</summary>
    public void Reset() => Cpu.Reset();

    public void RunFrame()
    {
        Tia.BeginFrame();
        while (!Tia.FrameDone)
        {
            Cpu.Step();
            while (Tia.WaitingForSync && !Tia.FrameDone)
            {
                Cycle(); // RDY: the CPU waits for the line's end
            }
        }

        // Keep the picture as the frame left it; the TIA's next frame starts drawing over it.
        for (var i = 0; i < Tia.Width * Tia.Height; i++)
        {
            _frame[i] = Tia.Frame[i];
        }

        Audio?.EndFrame(_frameCycles);
        _frameCycles = 0;
        Tia.AudioClock = 0;
        FrameCount++;
    }

    public void RunFrameSilently()
    {
        var sink = Tia.AudioSink;
        Tia.AudioSink = null;
        try
        {
            RunFrame();
        }
        finally
        {
            Tia.AudioSink = sink;
        }
    }

    /// <summary>
    /// The joystick on port 1 or 2 (the directions; B or A is the fire button). On port 1, Start is the console's Reset
    /// switch and Select its Select switch.
    /// </summary>
    public void SetControllerState(int port, ControllerState state)
    {
        if ((uint)port >= 2)
        {
            return;
        }

        _pads[port] = state;
        var nibble = 0x0F;
        nibble &= (state & ControllerState.Up) != 0 ? ~0x01 : 0x0F;
        nibble &= (state & ControllerState.Down) != 0 ? ~0x02 : 0x0F;
        nibble &= (state & ControllerState.Left) != 0 ? ~0x04 : 0x0F;
        nibble &= (state & ControllerState.Right) != 0 ? ~0x08 : 0x0F;
        Riot.Joysticks = port == 0
            ? (byte)((Riot.Joysticks & 0x0F) | (nibble << 4))
            : (byte)((Riot.Joysticks & 0xF0) | nibble);
        Tia.SetFire(port, (state & (ControllerState.B | ControllerState.A)) != 0);
        if (port == 0)
        {
            var switches = Riot.Switches | 0x03;
            switches &= (state & ControllerState.Start) != 0 ? ~0x01 : 0xFF;
            switches &= (state & ControllerState.Select) != 0 ? ~0x02 : 0xFF;
            Riot.Switches = (byte)switches;
        }
    }

    // ---- The bus ----

    private void Cycle()
    {
        Tia.AudioClock = _frameCycles;
        Tia.CpuCycle();
        Riot.Tick();
        _frameCycles++;
    }

    public byte Read(ushort address)
    {
        Cycle();
        var a = address & 0x1FFF;
        byte value;
        if ((a & 0x1000) != 0)
        {
            value = Cartridge.Read(a);
        }
        else if ((a & 0x80) == 0)
        {
            value = Tia.Read(a);
        }
        else
        {
            value = Riot.Read(a);
        }

        Cartridge.Snoop(a, value, write: false);
        return value;
    }

    public void Write(ushort address, byte value)
    {
        Cycle();
        var a = address & 0x1FFF;
        if ((a & 0x1000) != 0)
        {
            Cartridge.Write(a, value);
        }
        else if ((a & 0x80) == 0)
        {
            Tia.Write(a, value);
        }
        else
        {
            Riot.Write(a, value);
        }

        Cartridge.Snoop(a, value, write: true);
    }

    /// <summary>The RIOT's RAM at 0–$7F (RetroAchievements' layout for the 2600, console 25), then the cartridge's RAM.</summary>
    public byte PeekMemory(int address)
    {
        if ((uint)address < 128)
        {
            return Riot.Ram[address];
        }

        var cartridge = address - 128;
        return (uint)cartridge < (uint)Cartridge.Ram.Length ? Cartridge.Ram[cartridge] : (byte)0;
    }

    // ---- State ----

    public void SaveSnapshot(StateWriter writer)
    {
        writer.BeginChunk(SnapshotTag, 1);
        writer.WriteUInt64(RomHash);
        SaveState(writer);
        writer.EndChunk();
    }

    public byte[] SaveSnapshot()
    {
        var writer = new StateWriter();
        SaveSnapshot(writer);
        return writer.ToArray();
    }

    public void LoadSnapshot(ReadOnlyMemory<byte> data)
    {
        var reader = new StateReader(data);
        reader.BeginChunk(SnapshotTag);
        if (reader.ReadUInt64() != RomHash)
        {
            throw new StateFormatException("This save state belongs to a different game.");
        }

        LoadState(reader);
        reader.EndChunk();
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        Cpu.SaveState(writer);
        Tia.SaveState(writer);
        Riot.SaveState(writer);
        Cartridge.SaveState(writer);
        writer.WriteInt32(_frameCycles);
        writer.WriteInt64(FrameCount);
        writer.WriteByte(Riot.Joysticks);
        writer.WriteByte(Riot.Switches);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Cpu.LoadState(reader);
        Tia.LoadState(reader);
        Riot.LoadState(reader);
        Cartridge.LoadState(reader);
        _frameCycles = Math.Max(0, reader.ReadInt32());
        FrameCount = reader.ReadInt64();
        Riot.Joysticks = reader.ReadByte();
        Riot.Switches = reader.ReadByte();
        reader.EndChunk();
        for (var i = 0; i < Tia.Width * Tia.Height; i++)
        {
            _frame[i] = Tia.Frame[i];
        }
    }

    private static ulong HashRom(ReadOnlySpan<byte> rom)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in rom)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }
}
