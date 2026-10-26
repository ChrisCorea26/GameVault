using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The subsystem interface between the EE and the IOP (ps2tek: "SIF Registers", "SIF DMA"): the mailbox registers and
/// the SIF0 (IOP→EE) and SIF1 (EE→IOP) FIFOs that the two DMA controllers fill and drain. SIF0 carries the EE's
/// destination DMAtags inline, ahead of their data, as the IOP's channel 9 sends them.
/// </summary>
/// <remarks>
/// The FIFOs hold 32-bit words. Their capacity is larger than the hardware's so that the two processors, which run in
/// slices, don't have to hand over every few words; transfers still stop when a FIFO is full or empty, as they do on
/// hardware, so the order of events is the same.
/// </remarks>
public sealed class Ps2Sif : IStateful
{
    public const int FifoWords = 128;

    // The mailbox flags (ps2tek): 0x10000 SIF DMA initialized, 0x20000 SIFCMD initialized, 0x40000 IOP booted.
    public const uint FlagDmaReady = 0x10000;
    public const uint FlagCmdReady = 0x20000;
    public const uint FlagIopBooted = 0x40000;

    private static readonly StateTag Tag = StateTag.From("SIF ");

    /// <summary>Data from the IOP to the EE.</summary>
    public WordFifo Sif0 { get; } = new(FifoWords);

    /// <summary>Data from the EE to the IOP.</summary>
    public WordFifo Sif1 { get; } = new(FifoWords);

    /// <summary>SIF_MSCOM ($1000F200 / $1D000000): written by the EE (its SIF0 receive address, by convention).</summary>
    public uint Mscom { get; set; }

    /// <summary>SIF_SMCOM ($1000F210 / $1D000010): written by the IOP.</summary>
    public uint Smcom { get; set; }

    /// <summary>SIF_MSFLG ($1000F220 / $1D000020): the EE sets bits, the IOP clears them by writing them.</summary>
    public uint Msflg { get; private set; }

    /// <summary>SIF_SMFLG ($1000F230 / $1D000030): the IOP sets bits, the EE clears them by writing them.</summary>
    public uint Smflg { get; private set; }

    /// <summary>SIF_CTRL ($1000F240 / $1D000040), as each side reads it.</summary>
    public uint Ctrl { get; private set; }

    /// <summary>SIF_BD6 ($1000F260 / $1D000060).</summary>
    public uint Bd6 { get; set; }

    public void EeWriteMsflg(uint value) => Msflg |= value;

    public void IopWriteMsflg(uint value) => Msflg &= ~value;

    public void IopWriteSmflg(uint value) => Smflg |= value;

    public void EeWriteSmflg(uint value) => Smflg &= ~value;

    /// <summary>The EE's view of SIF_CTRL: bit 8 reads as 1 on the EE's side.</summary>
    public uint EeReadCtrl() => Ctrl | 0xF000_0102;

    /// <summary>The IOP's view of SIF_CTRL: bit 8 reads as 0 on the IOP's side.</summary>
    public uint IopReadCtrl() => (Ctrl | 0xF000_0002) & ~0x100u;

    public void EeWriteCtrl(uint value) => Ctrl = value;

    public void IopWriteCtrl(uint value) => Ctrl = value;

    public void Reset()
    {
        Sif0.Clear();
        Sif1.Clear();
        Mscom = Smcom = Msflg = Smflg = Ctrl = Bd6 = 0;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        Sif0.SaveState(writer);
        Sif1.SaveState(writer);
        writer.WriteUInt32(Mscom);
        writer.WriteUInt32(Smcom);
        writer.WriteUInt32(Msflg);
        writer.WriteUInt32(Smflg);
        writer.WriteUInt32(Ctrl);
        writer.WriteUInt32(Bd6);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        if (reader.BeginChunk(Tag) != 1)
        {
            throw new StateFormatException("Unsupported SIF state version.");
        }

        Sif0.LoadState(reader);
        Sif1.LoadState(reader);
        Mscom = reader.ReadUInt32();
        Smcom = reader.ReadUInt32();
        Msflg = reader.ReadUInt32();
        Smflg = reader.ReadUInt32();
        Ctrl = reader.ReadUInt32();
        Bd6 = reader.ReadUInt32();
        reader.EndChunk();
    }
}

/// <summary>A bounded FIFO of 32-bit words.</summary>
public sealed class WordFifo(int capacity)
{
    private readonly uint[] _words = new uint[capacity];
    private int _head;

    public int Capacity => _words.Length;

    public int Count { get; private set; }

    public int Free => _words.Length - Count;

    public bool TryPush(uint word)
    {
        if (Count == _words.Length)
        {
            return false;
        }

        _words[(_head + Count) % _words.Length] = word;
        Count++;
        return true;
    }

    /// <summary>Pushes a quadword as four words, low word first, if there is room for all four.</summary>
    public bool TryPush(UInt128 quadword)
    {
        if (Free < 4)
        {
            return false;
        }

        TryPush((uint)quadword);
        TryPush((uint)(quadword >> 32));
        TryPush((uint)(quadword >> 64));
        TryPush((uint)(quadword >> 96));
        return true;
    }

    public bool TryPop(out uint word)
    {
        if (Count == 0)
        {
            word = 0;
            return false;
        }

        word = _words[_head];
        _head = (_head + 1) % _words.Length;
        Count--;
        return true;
    }

    /// <summary>Pops four words as a quadword (low word first), if four are there.</summary>
    public bool TryPop(out UInt128 quadword)
    {
        if (Count < 4)
        {
            quadword = 0;
            return false;
        }

        TryPop(out uint w0);
        TryPop(out uint w1);
        TryPop(out uint w2);
        TryPop(out uint w3);
        quadword = ((UInt128)w3 << 96) | ((UInt128)w2 << 64) | ((UInt128)w1 << 32) | w0;
        return true;
    }

    public void Clear()
    {
        _head = 0;
        Count = 0;
    }

    public void SaveState(StateWriter writer)
    {
        writer.WriteInt32(Count);
        for (var i = 0; i < Count; i++)
        {
            writer.WriteUInt32(_words[(_head + i) % _words.Length]);
        }
    }

    public void LoadState(StateReader reader)
    {
        Clear();
        var count = reader.ReadInt32();
        if (count < 0 || count > _words.Length)
        {
            throw new StateFormatException("SIF FIFO count out of range.");
        }

        for (var i = 0; i < count; i++)
        {
            TryPush(reader.ReadUInt32());
        }
    }
}
