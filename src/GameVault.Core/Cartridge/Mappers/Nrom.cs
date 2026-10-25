using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Mapper 0 (NESdev Wiki: "NROM"): 16 or 32 KB PRG-ROM, 8 KB CHR-ROM or CHR-RAM, no banking.
/// 8 KB of PRG-RAM at $6000 is always present; blargg's test ROMs report results there.
/// </summary>
public sealed class Nrom : IMapper
{
    private static readonly StateTag Tag = StateTag.From("NROM");

    private readonly byte[] _prgRom;
    private readonly byte[] _chr;
    private readonly bool _chrIsRam;
    private readonly byte[] _prgRam = new byte[0x2000];
    private readonly int _prgMask;

    public Nrom(INesRom rom)
    {
        _prgRom = rom.PrgRom;
        _prgMask = rom.PrgRom.Length > 0x4000 ? 0x7FFF : 0x3FFF;
        _chrIsRam = rom.ChrRom.Length == 0;
        _chr = _chrIsRam ? new byte[0x2000] : rom.ChrRom;
        Mirroring = rom.Mirroring;
    }

    public Mirroring Mirroring { get; }

    public bool IrqPending => false;

    public byte CpuRead(ushort address, byte openBus) => address switch
    {
        >= 0x8000 => _prgRom[address & _prgMask],
        >= 0x6000 => _prgRam[address & 0x1FFF],
        _ => openBus,
    };

    public byte CpuPeek(ushort address) => CpuRead(address, 0xFF);

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            _prgRam[address & 0x1FFF] = value;
        }
    }

    public byte PpuRead(ushort address) => _chr[address & 0x1FFF];

    public void PpuWrite(ushort address, byte value)
    {
        if (_chrIsRam)
        {
            _chr[address & 0x1FFF] = value;
        }
    }

    public void OnCpuCycle()
    {
    }

    public void OnA12Rise()
    {
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_prgRam);
        if (_chrIsRam)
        {
            writer.WriteBytes(_chr);
        }

        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_prgRam);
        if (_chrIsRam)
        {
            reader.ReadBytes(_chr);
        }

        reader.EndChunk();
    }
}
