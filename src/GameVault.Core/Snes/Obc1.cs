using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>
/// The OBC1 (outstanding-work plan §5), Metal Combat: Falcon's Revenge's sprite helper. It sits in front of the 8 KB
/// SRAM at $6000–$7FFF and keeps an OAM-shaped table there: $7FF0–$7FF3 are the four bytes of sprite <i>n</i>
/// ($7FF6), $7FF4 its two high bits, and $7FF5 bit 0 picks which of two tables.
/// </summary>
/// <remarks>Reference: the SNESdev Wiki "OBC1" page.</remarks>
public sealed class Obc1 : ISnesCoprocessor
{
    private static readonly StateTag Tag = StateTag.From("OBC1");

    private readonly byte[] _ram = new byte[0x2000];

    public string Name => "OBC1";

    /// <summary>The battery-backed SRAM.</summary>
    public Span<byte> Ram => _ram;

    public bool Irq => false;

    private int Base => (_ram[0x1FF5] & 1) != 0 ? 0x1800 : 0x1C00;

    private int Index => _ram[0x1FF6] & 0x7F;

    public bool Claims(uint address) => ((address >> 16) & 0x40) == 0 && (address & 0xE000) == 0x6000;

    public byte Read(uint address, byte openBus) => (address & 0x1FFF) switch
    {
        >= 0x1FF0 and <= 0x1FF3 and var a => _ram[Base + (Index << 2) + (a & 3)],
        0x1FF4 => _ram[Base + (Index >> 2) + 0x200],
        var a => _ram[a],
    };

    public void Write(uint address, byte value)
    {
        switch (address & 0x1FFF)
        {
            case >= 0x1FF0 and <= 0x1FF3 and var a:
                _ram[Base + (Index << 2) + (a & 3)] = value;
                break;
            case 0x1FF4:
            {
                ref var high = ref _ram[Base + (Index >> 2) + 0x200];
                var shift = (Index & 3) << 1;
                high = (byte)((high & ~(3 << shift)) | ((value & 3) << shift));
                break;
            }

            case var a:
                _ram[a] = value;
                break;
        }
    }

    public void CatchUp(long clock)
    {
    }

    public void EndFrame(long frameClocks)
    {
    }

    public void Power()
    {
    }

    public void Reset()
    {
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_ram);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_ram);
        reader.EndChunk();
    }
}
