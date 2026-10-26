using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

// The kernel's SIF calls (ps2sdk's sifdma.h; Play!'s PS2OS.cpp): SIF DMA to the IOP through the DMAC's channel 6, the
// SIF0 receive chain on channel 5, and the SIF registers.
public sealed partial class Ps2Kernel
{
    private const uint D5Chcr = 0x1000_C000;
    private const uint D5Qwc = 0x1000_C020;
    private const uint D6Chcr = 0x1000_C400;
    private const uint D6Qwc = 0x1000_C420;
    private const uint D6Tadr = 0x1000_C430;
    private const uint ChcrStr = 0x100;

    // sceSifSetDma's attributes (ps2sdk: sifdma.h).
    private const uint SifDmaIntO = 0x04;
    private const uint SifDmaErt = 0x40;

    private readonly record struct SifTransfer(uint Source, uint Destination, uint Size, uint Attributes);

    /// <summary>SifSetDma requests waiting for channel 6 (the real kernel queues them the same way).</summary>
    private readonly List<(int Id, SifTransfer[] Transfers)> _sifQueue = [];

    private int _sifActive;
    private int _sifNextId = 1;
    private int _sifCompleted;

    /// <summary>
    /// SifSetDma (119/-119): queues the transfers (EE source, IOP destination, size in bytes, attributes) as one DMA chain
    /// on channel 6, each tag carrying the IOP's DMA tag (destination and flags, size in words) in its upper half for
    /// TTE; returns the request's id (never 0).
    /// </summary>
    private void SifSetDma()
    {
        var pointer = Arg(0);
        var count = (int)Math.Clamp(Arg(1), 1, 32);
        var transfers = new SifTransfer[count];
        for (var i = 0; i < count; i++)
        {
            var entry = pointer + (uint)(i * 16);
            transfers[i] = new SifTransfer(ReadWord(entry), ReadWord(entry + 4), ReadWord(entry + 8), ReadWord(entry + 12));
        }

        var id = _sifNextId;
        _sifNextId = _sifNextId == int.MaxValue ? 1 : _sifNextId + 1;
        _sifQueue.Add((id, transfers));
        Service();
        Return(id);
    }

    /// <summary>SifDmaStat (118/-118): negative when the transfer is done, 0 while it runs, 1 while it waits.</summary>
    private void SifDmaStat()
    {
        var id = (int)Arg(0);
        if (id == _sifActive)
        {
            Return(0);
        }
        else if (_sifQueue.Exists(r => r.Id == id))
        {
            Return(1);
        }
        else
        {
            Return(-1);
        }
    }

    /// <summary>SifSetDChain (120/-120): (re)starts channel 5's destination chain, which receives the IOP's packets.</summary>
    private void SifSetDChain()
    {
        var dmac = _console.Dmac;
        if ((dmac.ReadRegister(D5Chcr) & ChcrStr) == 0)
        {
            dmac.WriteRegister(D5Qwc, 0);
            dmac.WriteRegister(D5Chcr, 0x184); // chain mode, TIE, STR
        }

        Return(0);
    }

    /// <summary>
    /// SifSetReg (121): 1 MSCOM, 3 MSFLG (sets bits), 4 SMFLG (clears bits); $80000000+n the kernel's own registers.
    /// </summary>
    private void SifSetReg()
    {
        var register = Arg(0);
        var value = Arg(1);
        var sif = _console.Sif;
        switch (register)
        {
            case 1:
                sif.Mscom = value;
                break;
            case 3:
                sif.EeWriteMsflg(value);
                break;
            case 4:
                sif.EeWriteSmflg(value);
                break;
            case >= 0x8000_0000 and < 0x8000_0020:
                _sifSoftRegisters[register & 31] = value;
                break;
        }

        Return(0);
    }

    /// <summary>SifGetReg (122): 1 MSCOM, 2 SMCOM, 3 MSFLG, 4 SMFLG, $80000000+n the kernel's own registers.</summary>
    private void SifGetReg()
    {
        var register = Arg(0);
        var sif = _console.Sif;
        Return(register switch
        {
            1 => sif.Mscom,
            2 => sif.Smcom,
            3 => sif.Msflg,
            4 => sif.Smflg,
            >= 0x8000_0000 and < 0x8000_0020 => _sifSoftRegisters[register & 31],
            _ => 0,
        });
    }

    /// <summary>
    /// The kernel's background work, after each slice: when channel 6 is free, the next SifSetDma request goes out.
    /// </summary>
    internal void Service()
    {
        var dmac = _console.Dmac;
        if ((dmac.ReadRegister(D6Chcr) & ChcrStr) != 0)
        {
            return;
        }

        if (_sifActive != 0)
        {
            _sifCompleted = _sifActive;
            _sifActive = 0;
        }

        if (_sifQueue.Count == 0)
        {
            return;
        }

        var (id, transfers) = _sifQueue[0];
        _sifQueue.RemoveAt(0);
        _sifActive = id;

        // One REF tag per transfer, REFE for the last; the upper half is the IOP's tag (TTE sends it to the IOP).
        var tag = SifChainBase;
        for (var i = 0; i < transfers.Length; i++)
        {
            var t = transfers[i];
            var qwc = (t.Size + 15) / 16;
            var last = i == transfers.Length - 1;
            var source = (t.Source >> 28) == 7 ? 0x8000_0000 | (t.Source & 0x3FF0) : t.Source & 0x01FF_FFF0;
            var iopTag = (t.Destination & 0x00FF_FFFF)
                         | ((t.Attributes & SifDmaIntO) != 0 ? 0x4000_0000u : 0)
                         | ((t.Attributes & SifDmaErt) != 0 ? 0x8000_0000u : 0);
            WriteWord(tag, qwc | ((last ? 0u : 3u) << 28));
            WriteWord(tag + 4, source);
            WriteWord(tag + 8, iopTag);
            WriteWord(tag + 12, qwc * 4);
            tag += 16;
        }

        dmac.WriteRegister(D6Qwc, 0);
        dmac.WriteRegister(D6Tadr, SifChainBase);
        dmac.WriteRegister(D6Chcr, 0x145); // from memory, chain mode, TTE, STR
        _cpu.StopRequested = true;
        _console.UpdateInterruptLines();
    }

    private void SaveSif(StateWriter writer)
    {
        writer.WriteInt32(_sifActive);
        writer.WriteInt32(_sifNextId);
        writer.WriteInt32(_sifCompleted);
        writer.WriteInt32(_sifQueue.Count);
        foreach (var (id, transfers) in _sifQueue)
        {
            writer.WriteInt32(id);
            writer.WriteInt32(transfers.Length);
            foreach (var t in transfers)
            {
                writer.WriteUInt32(t.Source);
                writer.WriteUInt32(t.Destination);
                writer.WriteUInt32(t.Size);
                writer.WriteUInt32(t.Attributes);
            }
        }
    }

    private void LoadSif(StateReader reader)
    {
        _sifActive = reader.ReadInt32();
        _sifNextId = reader.ReadInt32();
        _sifCompleted = reader.ReadInt32();
        var count = reader.ReadInt32();
        if (count is < 0 or > 4096)
        {
            throw new StateFormatException("Too many SIF DMA requests.");
        }

        _sifQueue.Clear();
        for (var i = 0; i < count; i++)
        {
            var id = reader.ReadInt32();
            var length = reader.ReadInt32();
            if (length is < 1 or > 32)
            {
                throw new StateFormatException("Bad SIF DMA request.");
            }

            var transfers = new SifTransfer[length];
            for (var j = 0; j < length; j++)
            {
                transfers[j] = new SifTransfer(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
            }

            _sifQueue.Add((id, transfers));
        }
    }
}
