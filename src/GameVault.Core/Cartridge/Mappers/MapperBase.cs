using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Shared banking for the Tier 1 boards: PRG mapped in 8 KB slots at $8000-$FFFF, CHR in 1 KB slots,
/// optional PRG-RAM at $6000-$7FFF. Bank switches just recompute offsets, so each access is one lookup.
/// </summary>
public abstract class MapperBase : IMapper, IBatteryBacked
{
    private readonly int[] _prgOffsets = new int[4];
    private readonly int[] _chrOffsets = new int[8];
    private readonly StateTag _tag;
    private readonly int _chrRamStart = int.MaxValue;

    /// <param name="extraChrRam">
    /// CHR-RAM placed after the CHR-ROM in the same bank space, for boards with both (TQROM). Its 1 KB banks
    /// are numbered after the ROM's (see <see cref="ChrRamFirstBank"/>).
    /// </param>
    protected MapperBase(INesRom rom, string stateTag, int extraChrRam = 0)
    {
        _tag = StateTag.From(stateTag);
        PrgRom = rom.PrgRom;
        ChrIsRam = rom.ChrRom.Length == 0;
        if (ChrIsRam)
        {
            Chr = new byte[Math.Max(rom.ChrRamSize, 0x2000)];
        }
        else if (extraChrRam > 0)
        {
            Chr = new byte[rom.ChrRom.Length + extraChrRam];
            rom.ChrRom.CopyTo(Chr, 0);
            _chrRamStart = rom.ChrRom.Length;
        }
        else
        {
            Chr = rom.ChrRom;
        }

        PrgRam = new byte[Math.Max(rom.PrgRamSize, 0x2000)];
        HasBattery = rom.HasBattery;
        Submapper = rom.Submapper;
        HeaderMirroring = rom.Mirroring;
        Mirroring = rom.Mirroring;
        SetPrg32K(0);
        SetChr8K(0);
    }

    protected byte[] PrgRom { get; }

    protected byte[] Chr { get; }

    protected bool ChrIsRam { get; }

    /// <summary>The first 1 KB bank number of the extra CHR-RAM (only with <c>extraChrRam</c>).</summary>
    protected int ChrRamFirstBank => _chrRamStart == int.MaxValue ? 0 : _chrRamStart >> 10;

    protected byte[] PrgRam { get; }

    protected int Submapper { get; }

    protected Mirroring HeaderMirroring { get; }

    /// <summary>Byte offset of the 8 KB PRG-RAM bank at $6000.</summary>
    protected int PrgRamOffset { get; set; }

    protected bool PrgRamReadable { get; set; } = true;

    protected bool PrgRamWritable { get; set; } = true;

    public Mirroring Mirroring { get; protected set; }

    public bool HasBattery { get; }

    public virtual Span<byte> BatteryRam => PrgRam;

    public virtual bool IrqPending => false;

    public virtual bool HasCycleHooks => false;

    public virtual Audio.IExpansionAudio? ExpansionAudio => null;

    public virtual int NametablePage(int slot) => NametableRouting.Page(Mirroring, slot);

    public virtual bool HasNametableHooks => false;

    public virtual bool SnoopsPpuRegisters => false;

    public virtual void OnPpuRegisterWrite(ushort register, byte value)
    {
    }

    public virtual byte ReadNametable(ushort address, byte[] ciram) => ciram[(NametablePage((address >> 10) & 3) << 10 | (address & 0x3FF)) & 0x7FF];

    public virtual void WriteNametable(ushort address, byte value, byte[] ciram) =>
        ciram[(NametablePage((address >> 10) & 3) << 10 | (address & 0x3FF)) & 0x7FF] = value;

    protected int PrgBanks8K => PrgRom.Length >> 13;

    // ------------------------------------------------------------------ banking

    protected void SetPrg8K(int slot, int bank) => _prgOffsets[slot] = Wrap(bank, Math.Max(1, PrgBanks8K)) << 13;

    protected void SetPrg16K(int slot, int bank)
    {
        // Boards with a single 16 KB bank (NROM-128 style) mirror it.
        var banks16 = Math.Max(1, PrgRom.Length >> 14);
        var b = Wrap(bank, banks16);
        _prgOffsets[slot * 2] = (b << 14) % PrgRom.Length;
        _prgOffsets[slot * 2 + 1] = ((b << 14) + 0x2000) % PrgRom.Length;
    }

    protected void SetPrg32K(int bank)
    {
        SetPrg16K(0, bank * 2);
        SetPrg16K(1, bank * 2 + 1);
    }

    protected void SetChr1K(int slot, int bank) => _chrOffsets[slot] = Wrap(bank, Math.Max(1, Chr.Length >> 10)) << 10;

    protected void SetChr2K(int slot, int bank)
    {
        SetChr1K(slot * 2, bank * 2);
        SetChr1K(slot * 2 + 1, bank * 2 + 1);
    }

    protected void SetChr4K(int slot, int bank)
    {
        for (var i = 0; i < 4; i++)
        {
            SetChr1K(slot * 4 + i, bank * 4 + i);
        }
    }

    protected void SetChr8K(int bank)
    {
        for (var i = 0; i < 8; i++)
        {
            SetChr1K(i, bank * 8 + i);
        }
    }

    private static int Wrap(int bank, int count) => ((bank % count) + count) % count;

    // ------------------------------------------------------------------ bus

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected byte ReadPrgRom(ushort address) => PrgRom[_prgOffsets[(address >> 13) & 3] + (address & 0x1FFF)];

    public virtual byte CpuRead(ushort address, byte openBus)
    {
        if (address >= 0x8000)
        {
            return ReadPrgRom(address);
        }

        if (address >= 0x6000 && PrgRamReadable)
        {
            return PrgRam[PrgRamOffset + (address & 0x1FFF)];
        }

        return openBus;
    }

    public byte CpuPeek(ushort address) => CpuRead(address, 0xFF);

    public virtual void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x8000)
        {
            WriteRegister(address, value);
        }
        else if (address >= 0x6000 && PrgRamWritable)
        {
            PrgRam[PrgRamOffset + (address & 0x1FFF)] = value;
        }
    }

    /// <summary>A write to $8000-$FFFF.</summary>
    protected abstract void WriteRegister(ushort address, byte value);

    /// <summary>
    /// Boards without a write-enable on the ROM see the ROM drive the bus during register writes, so the
    /// written value is ANDed with the ROM byte (NESdev Wiki: "Bus conflict").
    /// </summary>
    protected byte WithBusConflict(ushort address, byte value) => (byte)(value & ReadPrgRom(address));

    public virtual byte PpuRead(ushort address) => Chr[_chrOffsets[(address >> 10) & 7] + (address & 0x03FF)];

    public void PpuWrite(ushort address, byte value)
    {
        var offset = _chrOffsets[(address >> 10) & 7] + (address & 0x03FF);
        if (ChrIsRam || offset >= _chrRamStart)
        {
            Chr[offset] = value;
        }
    }

    public virtual void OnCpuCycle()
    {
    }

    public virtual void OnA12Rise()
    {
    }

    public virtual void OnA12Fall()
    {
    }

    // ------------------------------------------------------------------ state

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(_tag, 1);
        writer.WriteBytes(PrgRam);
        if (ChrIsRam)
        {
            writer.WriteBytes(Chr);
        }
        else if (_chrRamStart != int.MaxValue)
        {
            writer.WriteBytes(Chr.AsSpan(_chrRamStart));
        }

        SaveRegisters(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(_tag);
        reader.ReadBytes(PrgRam);
        if (ChrIsRam)
        {
            reader.ReadBytes(Chr);
        }
        else if (_chrRamStart != int.MaxValue)
        {
            reader.ReadBytes(Chr.AsSpan(_chrRamStart));
        }

        LoadRegisters(reader);
        reader.EndChunk();
    }

    /// <summary>Writes the board's registers. Bank offsets are derived, so <see cref="LoadRegisters"/> recomputes them.</summary>
    protected abstract void SaveRegisters(StateWriter writer);

    protected abstract void LoadRegisters(StateReader reader);
}
