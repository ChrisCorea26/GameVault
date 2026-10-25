using GameVault.Core.State;

namespace GameVault.Core.Ppu;

/// <summary>
/// Phase 1 stand-in for the 2C02: models only dot/scanline timing, the VBlank flag, NMI output and
/// the register I/O latch, so CPU test ROMs that wait for VBlank can run. Phase 2 replaces it.
/// </summary>
public sealed class PpuTimingStub : IStateful
{
    public const int DotsPerScanline = 341;
    public const int ScanlinesPerFrame = 262;
    private const int VBlankScanline = 241;
    private const int PreRenderScanline = 261;

    private static readonly StateTag Tag = StateTag.From("PPUS");

    private bool _vblank;
    private bool _nmiEnabled;
    private bool _renderingEnabled;
    private bool _oddFrame;
    private byte _latch;

    public int Scanline { get; private set; }

    public int Dot { get; private set; }

    public long FrameCount { get; private set; }

    /// <summary>Level of the PPU's /NMI output: VBlank flag AND the NMI enable bit ($2000 bit 7).</summary>
    public bool NmiOutput => _vblank && _nmiEnabled;

    public void Power()
    {
        Scanline = Dot = 0;
        FrameCount = 0;
        _vblank = _nmiEnabled = _renderingEnabled = _oddFrame = false;
        _latch = 0;
    }

    /// <summary>Advances one PPU dot.</summary>
    public void Tick()
    {
        // NESdev Wiki "PPU frame timing": VBlank starts at dot 1 of line 241 and ends at dot 1 of the pre-render line.
        if (Dot == 1)
        {
            if (Scanline == VBlankScanline)
            {
                _vblank = true;
            }
            else if (Scanline == PreRenderScanline)
            {
                _vblank = false;
            }
        }

        Dot++;

        // With rendering on, odd frames skip the last dot of the pre-render line.
        if (Scanline == PreRenderScanline && Dot == DotsPerScanline - 1 && _oddFrame && _renderingEnabled)
        {
            Dot++;
        }

        if (Dot == DotsPerScanline)
        {
            Dot = 0;
            if (++Scanline == ScanlinesPerFrame)
            {
                Scanline = 0;
                FrameCount++;
                _oddFrame = !_oddFrame;
            }
        }
    }

    /// <summary>CPU read of $2000-$3FFF (mirrored every 8 bytes).</summary>
    public byte ReadRegister(ushort address)
    {
        if ((address & 7) == 2)
        {
            _latch = (byte)((_vblank ? 0x80 : 0) | (_latch & 0x1F));
            _vblank = false;
        }

        return _latch;
    }

    public void WriteRegister(ushort address, byte value)
    {
        _latch = value;
        switch (address & 7)
        {
            case 0:
                _nmiEnabled = (value & 0x80) != 0;
                break;
            case 1:
                _renderingEnabled = (value & 0x18) != 0;
                break;
        }
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteInt32(Scanline);
        writer.WriteInt32(Dot);
        writer.WriteInt64(FrameCount);
        writer.WriteBool(_vblank);
        writer.WriteBool(_nmiEnabled);
        writer.WriteBool(_renderingEnabled);
        writer.WriteBool(_oddFrame);
        writer.WriteByte(_latch);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Scanline = reader.ReadInt32();
        Dot = reader.ReadInt32();
        FrameCount = reader.ReadInt64();
        _vblank = reader.ReadBool();
        _nmiEnabled = reader.ReadBool();
        _renderingEnabled = reader.ReadBool();
        _oddFrame = reader.ReadBool();
        _latch = reader.ReadByte();
        reader.EndChunk();
    }
}
