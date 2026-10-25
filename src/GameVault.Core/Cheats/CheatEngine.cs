using System.Runtime.CompilerServices;
using GameVault.Core.Bus;

namespace GameVault.Core.Cheats;

/// <summary>
/// Applies the active codes (SPEC §7.8). ROM codes act on CPU reads of $8000-$FFFF like the Game Genie: the
/// replacement is returned instead of the real byte, and with a compare value only when the real byte
/// matches, which keeps a code to the bank it was made for. RAM codes are written before every frame. The
/// set changes only between frames, so the console stays deterministic.
/// </summary>
public sealed class CheatEngine
{
    // For each address in $8000-$FFFF, 1 + the index of its code in _rom (0 for none). The first code for an
    // address wins, as on the real device.
    private readonly ushort[] _romIndex = new ushort[0x8000];
    private readonly CheatCode[] _rom;
    private readonly CheatCode[] _ram;

    public CheatEngine(IEnumerable<CheatCode> codes)
    {
        Codes = [.. codes];
        _rom = [.. Codes.Where(c => c.Kind == CheatKind.Rom)];
        _ram = [.. Codes.Where(c => c.Kind == CheatKind.Ram)];
        for (var i = 0; i < _rom.Length; i++)
        {
            ref var slot = ref _romIndex[_rom[i].Address & 0x7FFF];
            if (slot == 0)
            {
                slot = (ushort)(i + 1);
            }
        }
    }

    public IReadOnlyList<CheatCode> Codes { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadRom(ushort address, byte value)
    {
        var index = _romIndex[address & 0x7FFF];
        if (index == 0)
        {
            return value;
        }

        var code = _rom[index - 1];
        return code.Compare is not { } compare || compare == value ? code.Value : value;
    }

    /// <summary>Writes the RAM codes; called at the start of each frame.</summary>
    public void ApplyRam(NesBus bus)
    {
        foreach (var code in _ram)
        {
            if (code.Compare is not { } compare || bus.Peek(code.Address) == compare)
            {
                bus.PokeRam(code.Address, code.Value);
            }
        }
    }
}
