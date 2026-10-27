using GameVault.Core.Audio;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu;
using GameVault.Core.Input;
using GameVault.Core.State;
using GameVault.Core.Systems;

namespace GameVault.Core.Atari;

/// <summary>
/// The Atari 5200 SuperSystem (SPEC §7.11, phase 19, milestone 2): a 6502C at 1.79 MHz with 16 KB of RAM, ANTIC,
/// GTIA, POKEY, a cartridge of up to 32 KB and the player's own 2 KB BIOS. Up to four controllers, each with an analog
/// stick, two fire buttons and a twelve-key keypad with Start, Pause and Reset.
/// </summary>
/// <remarks>
/// The bus: RAM at $0000–$3FFF, the cartridge at $4000–$BFFF, GTIA at $C000–$CFFF, ANTIC at $D400–$D4FF, POKEY at
/// $E800–$EFFF and the BIOS at $F800–$FFFF, each chip repeating through its block. ANTIC halts the CPU (the 6502C's
/// HALT line) for the cycles its DMA takes and for WSYNC.
/// </remarks>
public sealed class Atari5200Console : IEmulatedConsole, ICpuBus, IAnticBus
{
    /// <summary>The NTSC machine clock: the colour clock (3.579545 MHz) over two.</summary>
    public const double NtscCpuClock = 3_579_545.0 / 2;

    public const int BiosSize = 2048;

    // Pot readings for a stick pushed fully left (or up), centred and fully right (or down).
    private const int PotLow = 6;
    private const int PotCentre = 114;
    private const int PotHigh = 220;

    private static readonly StateTag Tag = StateTag.From("A52 ");
    private static readonly StateTag SnapshotTag = StateTag.From("GVA5");

    private readonly byte[] _ram = new byte[0x4000];
    private readonly byte[] _bios;
    private readonly ushort[] _frame = new ushort[Gtia.Width * Gtia.Height];
    private int _frameCycles;

    /// <exception cref="RomFormatException">The BIOS isn't 2 KB.</exception>
    public Atari5200Console(Atari5200Cartridge cartridge, byte[] bios)
    {
        if (bios.Length != BiosSize)
        {
            throw new RomFormatException("The Atari 5200 BIOS file should be 2 KB (2,048 bytes).");
        }

        Cartridge = cartridge;
        _bios = bios;
        Cpu = new Cpu6502(this, CpuVariant.Nmos6502);
        Antic = new Antic(this, Gtia);
        RomHash = HashRom(cartridge.Rom);
    }

    public Atari5200Cartridge Cartridge { get; }

    public Cpu6502 Cpu { get; }

    public Gtia Gtia { get; } = new();

    public Antic Antic { get; }

    public Pokey Pokey { get; } = new();

    public GameSystem System => GameSystem.Atari5200;

    public ulong RomHash { get; }

    public int ScreenWidth => Gtia.Width;

    public int ScreenHeight => Gtia.Height;

    /// <summary>A hi-res pixel (half a colour clock) is about 0.86 times as wide as a line is tall.</summary>
    public double PixelAspect => 6.0 / 7.0;

    public ReadOnlySpan<ushort> FrameBuffer => _frame;

    public ReadOnlySpan<uint> Palette => Atari5200Palette.Argb;

    public long FrameCount { get; private set; }

    public double CpuClockRate => NtscCpuClock;

    public BandLimitedResampler? Audio { get; private set; }

    public int ControllerPorts => 4;

    public IBatteryBacked? Battery => null;

    public Span<byte> Ram => _ram;

    public BandLimitedResampler EnableAudio(int sampleRate = 48000)
    {
        Audio ??= new BandLimitedResampler(CpuClockRate, sampleRate);
        Pokey.AudioSink = Audio;
        return Audio;
    }

    public void Power()
    {
        Array.Clear(_ram);
        Array.Clear(_frame);
        Gtia.Power();
        Pokey.Power();
        Antic.Power();
        _frameCycles = 0;
        FrameCount = 0;
        Cpu.Reset();
    }

    /// <summary>The 5200 has no reset button on the console (the keypad's Reset key is a game input); this resets the CPU.</summary>
    public void Reset() => Cpu.Reset();

    public void RunFrame()
    {
        Antic.FrameDone = false;
        while (!Antic.FrameDone)
        {
            Cpu.Step();
        }

        Gtia.Frame.CopyTo(_frame);
        Audio?.EndFrame(_frameCycles);
        _frameCycles = 0;
        FrameCount++;
    }

    public void RunFrameSilently()
    {
        var sink = Pokey.AudioSink;
        Pokey.AudioSink = null;
        try
        {
            RunFrame();
        }
        finally
        {
            Pokey.AudioSink = sink;
        }
    }

    /// <summary>
    /// Controller 1–4: the stick (analog, or the D-pad pushed fully), B the bottom fire button and A the top one, Start
    /// and Select the keypad's Start and Pause, and the keypad's keys from <see cref="Atari5200Keypad"/>.
    /// </summary>
    public void SetControllerState(int port, ControllerState state)
    {
        if ((uint)port >= 4)
        {
            return;
        }

        Gtia.SetTrigger(port, (state & ControllerState.B) != 0);
        Pokey.TopButtons[port] = (state & ControllerState.A) != 0;
        Pokey.Keys[port] = Atari5200Keypad.PokeyCode(state);

        int x = state.StickX(), y = state.StickY();
        var potX = x != 0 ? PotCentre + x * (PotHigh - PotCentre) / 127
            : (state & ControllerState.Left) != 0 ? PotLow
            : (state & ControllerState.Right) != 0 ? PotHigh
            : PotCentre;
        var potY = y != 0 ? PotCentre - y * (PotHigh - PotCentre) / 127
            : (state & ControllerState.Up) != 0 ? PotLow
            : (state & ControllerState.Down) != 0 ? PotHigh
            : PotCentre;
        Pokey.PotInputs[port * 2] = (byte)Math.Clamp(potX, 1, Pokey.PotMax - 1);
        Pokey.PotInputs[port * 2 + 1] = (byte)Math.Clamp(potY, 1, Pokey.PotMax - 1);
    }

    // ---- The bus ----

    private void Cycle()
    {
        Pokey.AudioClock = _frameCycles;
        Antic.Tick();
        Pokey.Tick();
        if (Antic.NmiRequest)
        {
            Antic.NmiRequest = false;
            Cpu.TriggerNmi();
        }

        if (Pokey.IrqAsserted)
        {
            Cpu.SetIrq(IrqSource.External);
        }
        else
        {
            Cpu.ClearIrq(IrqSource.External);
        }

        _frameCycles++;
    }

    /// <summary>One CPU bus cycle, after any cycles ANTIC holds the CPU for.</summary>
    private void BusCycle()
    {
        while (Antic.Halted)
        {
            Cycle();
        }

        Cycle();
    }

    public byte Read(ushort address)
    {
        BusCycle();
        return Peek(address);
    }

    private byte Peek(ushort address)
    {
        switch (address >> 12)
        {
            case < 0x4:
                return _ram[address];
            case < 0xC:
                return Cartridge.Read(address);
            case 0xC:
                Gtia.RenderTo(Antic.Cycle * 2);
                return Gtia.Read(address);
            case 0xD:
                return (address & 0x0F00) == 0x0400 ? Antic.Read(address) : (byte)0xFF;
            case 0xE:
                return address >= 0xE800 ? Pokey.Read(address) : (byte)0xFF;
            default:
                return address >= 0xF800 ? _bios[address & 0x07FF] : (byte)0xFF;
        }
    }

    public void Write(ushort address, byte value)
    {
        BusCycle();
        switch (address >> 12)
        {
            case < 0x4:
                _ram[address] = value;
                break;
            case 0xC:
                Gtia.RenderTo(Antic.Cycle * 2);
                Gtia.Write(address, value);
                if ((address & 0x1F) == 0x1F)
                {
                    Pokey.SelectedController = value & 3; // CONSOL picks the controller POKEY scans
                }

                break;
            case 0xD:
                if ((address & 0x0F00) == 0x0400)
                {
                    Antic.Write(address, value);
                }

                break;
            case 0xE:
                if (address >= 0xE800)
                {
                    Pokey.Write(address, value);
                }

                break;
        }
    }

    /// <summary>ANTIC's DMA reads (no side effects, no bus cycle of the CPU's).</summary>
    public byte DmaRead(ushort address) => address switch
    {
        < 0x4000 => _ram[address],
        < 0xC000 => Cartridge.Read(address),
        >= 0xF800 => _bios[address & 0x07FF],
        _ => 0xFF,
    };

    /// <summary>The 16 KB of RAM.</summary>
    public byte PeekMemory(int address) => (uint)address < (uint)_ram.Length ? _ram[address] : (byte)0;

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
        Antic.SaveState(writer);
        Gtia.SaveState(writer);
        Pokey.SaveState(writer);
        Cartridge.SaveState(writer);
        writer.WriteBytes(_ram);
        writer.WriteInt32(_frameCycles);
        writer.WriteInt64(FrameCount);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Cpu.LoadState(reader);
        Antic.LoadState(reader);
        Gtia.LoadState(reader);
        Pokey.LoadState(reader);
        Cartridge.LoadState(reader);
        reader.ReadBytes(_ram);
        _frameCycles = Math.Max(0, reader.ReadInt32());
        FrameCount = reader.ReadInt64();
        reader.EndChunk();
        Gtia.Frame.CopyTo(_frame);
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
