using GameVault.Core.State;

namespace GameVault.Core.SegaCd;

/// <summary>
/// The Sega CD's graphics ASIC (sega-cd-32x.md): it draws a rotated and scaled view of a stamp map into Word RAM's
/// image buffer, one line per trace-table entry, and raises the sub CPU's level 1 interrupt when it's done.
/// </summary>
/// <remarks>
/// Registers (the sub CPU's $FF8058–$FF8067): $58 stamp data size (bit 0 repeat, bit 1 32×32 stamps, bit 2 a 16×16-
/// screen map; bit 15 reads busy), $5A the stamp map's base, $5C the image buffer's height in cells less one, $5E its
/// start, $60 its dot offset (H in bits 0–2, V in bits 3–5), $62 its width in dots, $64 its height in dots, $66 the
/// trace table's base (writing it starts the job). Each trace entry is X and Y (13.3 fixed point) and their steps per
/// dot (5.11). A stamp map entry holds the stamp number (in 128-byte units), the rotation (bits 13–14) and a
/// horizontal flip (bit 15); stamps and the image buffer are 4-bit dots in 8×8 cells, stored column by column.
/// Behaviour follows Genesis Plus GX's gfx.c, read for behaviour only; the rotations' direction is unchecked on
/// hardware.
/// </remarks>
public sealed class SegaCdGraphics : IStateful
{
    // Sub CPU clocks a dot takes (the job's length decides when the interrupt comes).
    private const int ClocksPerDot = 3;

    private static readonly StateTag Tag = StateTag.From("CDGF");

    public ushort StampSize { get; set; }

    public ushort StampMap { get; set; }

    public ushort ImageCells { get; set; }

    public ushort ImageStart { get; set; }

    public ushort ImageOffset { get; set; }

    public ushort ImageWidth { get; set; }

    public ushort ImageHeight { get; set; }

    public ushort TraceTable { get; private set; }

    /// <summary>Sub CPU clocks until the job finishes; 0 when idle.</summary>
    public long BusyClocks { get; set; }

    public bool Busy => BusyClocks > 0;

    public void Reset()
    {
        StampSize = StampMap = ImageCells = ImageStart = ImageOffset = ImageWidth = ImageHeight = TraceTable = 0;
        BusyClocks = 0;
    }

    /// <summary>Starts a job: draws every line now, and stays busy for as long as the hardware would take.</summary>
    /// <param name="wordRam">The 256 KB Word RAM (2M layout), with the stamps, the map, the trace table and the image buffer.</param>
    /// <param name="write">Writes a dot (byte address in Word RAM, nibble high or low, value), applying the priority mode.</param>
    public void Start(ushort traceTable, byte[] wordRam, Action<int, bool, int> write)
    {
        TraceTable = traceTable;
        var big = (StampSize & 2) != 0;
        var stampDots = big ? 32 : 16;
        var mapDots = (StampSize & 4) != 0 ? 4096 : 256;
        var mapStamps = mapDots / stampDots;
        var repeat = (StampSize & 1) != 0;
        var mapBase = (StampMap << 2) & 0x3FFFF;
        var imageBase = (ImageStart << 2) & 0x3FFFF;
        var cellsHigh = (ImageCells & 0x1F) + 1;
        var width = ImageWidth & 0x1FF;
        var height = ImageHeight & 0xFF;
        var offsetX = ImageOffset & 7;
        var offsetY = (ImageOffset >> 3) & 7;
        var trace = (traceTable << 2) & 0x3FFFF;

        for (var line = 0; line < height; line++)
        {
            var entry = trace + line * 8;
            int x = (short)Word(wordRam, entry) << 8; // 13.3 to 13.11
            int y = (short)Word(wordRam, entry + 2) << 8;
            int dx = (short)Word(wordRam, entry + 4);
            int dy = (short)Word(wordRam, entry + 6);
            for (var dot = 0; dot < width; dot++, x += dx, y += dy)
            {
                var px = (x >> 11) & 0x1FFF;
                var py = (y >> 11) & 0x1FFF;
                var pixel = 0;
                if (repeat || (px < mapDots && py < mapDots))
                {
                    px &= mapDots - 1;
                    py &= mapDots - 1;
                    var entryAddress = mapBase + ((py / stampDots) * mapStamps + px / stampDots) * 2;
                    var stamp = Word(wordRam, entryAddress);
                    var number = stamp & 0x7FF;
                    if (big)
                    {
                        number &= ~3;
                    }

                    if (number != 0)
                    {
                        pixel = StampDot(wordRam, number, stamp, px % stampDots, py % stampDots, stampDots);
                    }
                }

                // The image buffer's dot (column-major 8×8 cells).
                var ix = dot + offsetX;
                var iy = line + offsetY;
                var cell = (ix >> 3) * cellsHigh + (iy >> 3);
                var address = imageBase + cell * 32 + (iy & 7) * 4 + ((ix & 7) >> 1);
                write(address & 0x3FFFF, (ix & 1) == 0, pixel);
            }
        }

        BusyClocks = Math.Max(1, (long)width * height * ClocksPerDot);
    }

    private static int StampDot(byte[] ram, int number, int entry, int sx, int sy, int size)
    {
        if ((entry & 0x8000) != 0)
        {
            sx = size - 1 - sx; // horizontal flip
        }

        int u, v;
        switch ((entry >> 13) & 3)
        {
            case 0: (u, v) = (sx, sy); break;
            case 1: (u, v) = (sy, size - 1 - sx); break;
            case 2: (u, v) = (size - 1 - sx, size - 1 - sy); break;
            default: (u, v) = (size - 1 - sy, sx); break;
        }

        var cellsHigh = size / 8;
        var cell = (u >> 3) * cellsHigh + (v >> 3);
        var address = number * 128 + cell * 32 + (v & 7) * 4 + ((u & 7) >> 1);
        var value = ram[address & 0x3FFFF];
        return (u & 1) == 0 ? value >> 4 : value & 0x0F;
    }

    private static int Word(byte[] ram, int address) => (ram[address & 0x3FFFF] << 8) | ram[(address + 1) & 0x3FFFF];

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteUInt16(StampSize);
        writer.WriteUInt16(StampMap);
        writer.WriteUInt16(ImageCells);
        writer.WriteUInt16(ImageStart);
        writer.WriteUInt16(ImageOffset);
        writer.WriteUInt16(ImageWidth);
        writer.WriteUInt16(ImageHeight);
        writer.WriteUInt16(TraceTable);
        writer.WriteInt64(BusyClocks);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        StampSize = reader.ReadUInt16();
        StampMap = reader.ReadUInt16();
        ImageCells = reader.ReadUInt16();
        ImageStart = reader.ReadUInt16();
        ImageOffset = reader.ReadUInt16();
        ImageWidth = reader.ReadUInt16();
        ImageHeight = reader.ReadUInt16();
        TraceTable = reader.ReadUInt16();
        BusyClocks = Math.Max(0, reader.ReadInt64());
        reader.EndChunk();
    }
}
