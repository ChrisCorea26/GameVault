using GameVault.Core.State;

namespace GameVault.Core.GameCube;

/// <summary>
/// The GameCube's DSP interface (phase-18.md, milestone 1) at $0C005000: the two mailboxes, the control register
/// (DSPCR), the 16 MB of audio RAM (ARAM) with its DMA, and the audio DMA's registers. The DSP itself isn't emulated
/// yet: it answers the OS's boot handshake (the mail $8071FEED when it's let out of halt, as Nintendo's DSP ROM does)
/// and takes mail as soon as it's sent.
/// </summary>
/// <remarks>
/// Registers (16-bit): $00/$02 the CPU-to-DSP mailbox (bit 15 of $00: mail waiting), $04/$06 the DSP-to-CPU mailbox
/// (reading $06 takes the mail), $0A DSPCR (RES 0x1, PIINT 0x2, HALT 0x4, AIINT 0x8 and its mask 0x10, ARINT 0x20 and
/// its mask 0x40, DSPINT 0x80 and its mask 0x100, DSP DMA 0x200, DSP reset 0x800; the interrupt flags clear by writing
/// 1), $20 the ARAM DMA's main-memory address, $24 its ARAM address, $28 its length (bit 31: ARAM to main memory),
/// $30–$36 the audio DMA's start, length and remaining blocks. Reference: YAGCD; libogc's dsp.c and arqueue.c.
/// </remarks>
public sealed class GcDspInterface : IStateful
{
    public const int AramSize = 16 * 1024 * 1024;

    private const ushort Res = 0x0001, Halt = 0x0004, AiInt = 0x0008, AiMask = 0x0010, ArInt = 0x0020, ArMask = 0x0040,
        DspInt = 0x0080, DspMask = 0x0100;

    private static readonly StateTag Tag = StateTag.From("GCDS");

    private readonly byte[] _aram = new byte[AramSize];
    private uint _mailToDsp;
    private uint _mailFromDsp;
    private ushort _control = Halt;
    private uint _aramMain;
    private uint _aramAddress;
    private uint _aramLength;
    private uint _aiStart;
    private ushort _aiControl;

    /// <summary>Main RAM, for ARAM DMA (the console sets it).</summary>
    public byte[]? Ram { get; set; }

    public Span<byte> Aram => _aram;

    public bool Interrupt =>
        ((_control & AiInt) != 0 && (_control & AiMask) != 0)
        || ((_control & ArInt) != 0 && (_control & ArMask) != 0)
        || ((_control & DspInt) != 0 && (_control & DspMask) != 0);

    public void Reset()
    {
        _mailToDsp = _mailFromDsp = 0;
        _control = Halt;
        _aramMain = _aramAddress = _aramLength = 0;
        _aiStart = 0;
        _aiControl = 0;
    }

    public void Power()
    {
        Array.Clear(_aram);
        Reset();
    }

    public ushort Read16(int offset)
    {
        switch (offset & 0x3E)
        {
            case 0x00: return (ushort)(_mailToDsp >> 16);
            case 0x02: return (ushort)_mailToDsp;
            case 0x04: return (ushort)(_mailFromDsp >> 16);
            case 0x06:
            {
                var low = (ushort)_mailFromDsp;
                _mailFromDsp &= 0x7FFFFFFF; // read: the mailbox is empty
                return low;
            }

            case 0x0A: return _control;
            case 0x20: return (ushort)(_aramMain >> 16);
            case 0x22: return (ushort)_aramMain;
            case 0x24: return (ushort)(_aramAddress >> 16);
            case 0x26: return (ushort)_aramAddress;
            case 0x28: return (ushort)(_aramLength >> 16);
            case 0x2A: return 0; // the DMA's remaining length: always done
            case 0x30: return (ushort)(_aiStart >> 16);
            case 0x32: return (ushort)_aiStart;
            case 0x36: return _aiControl;
            case 0x3A: return 0;
            default: return 0;
        }
    }

    public void Write16(int offset, ushort value)
    {
        switch (offset & 0x3E)
        {
            case 0x00:
                _mailToDsp = (_mailToDsp & 0xFFFF) | ((uint)value << 16);
                break;
            case 0x02:
                // The low half completes a mail; the DSP takes it at once (its "mail waiting" bit clears).
                _mailToDsp = ((_mailToDsp & 0xFFFF0000) | value) & 0x7FFFFFFF;
                break;
            case 0x0A:
                WriteControl(value);
                break;
            case 0x20: _aramMain = (_aramMain & 0xFFFF) | ((uint)(value & 0x03FF) << 16); break;
            case 0x22: _aramMain = (_aramMain & 0xFFFF0000) | (value & 0xFFE0u); break;
            case 0x24: _aramAddress = (_aramAddress & 0xFFFF) | ((uint)(value & 0x03FF) << 16); break;
            case 0x26: _aramAddress = (_aramAddress & 0xFFFF0000) | (value & 0xFFE0u); break;
            case 0x28: _aramLength = (_aramLength & 0xFFFF) | ((uint)value << 16); break;
            case 0x2A:
                _aramLength = (_aramLength & 0xFFFF0000) | (value & 0xFFE0u);
                AramDma();
                break;
            case 0x30: _aiStart = (_aiStart & 0xFFFF) | ((uint)value << 16); break;
            case 0x32: _aiStart = (_aiStart & 0xFFFF0000) | value; break;
            case 0x36: _aiControl = value; break;
        }
    }

    private void WriteControl(ushort value)
    {
        var wasHalted = (_control & Halt) != 0;

        // The interrupt flags clear by writing 1; RES finishes at once.
        var flags = (ushort)(_control & (AiInt | ArInt | DspInt) & ~value);
        _control = (ushort)((value & ~(AiInt | ArInt | DspInt | Res)) | flags);
        if ((value & Res) != 0)
        {
            _mailToDsp = _mailFromDsp = 0;
        }

        if (wasHalted && (_control & Halt) == 0)
        {
            // Out of halt, the DSP's boot ROM greets the CPU.
            _mailFromDsp = 0x8071FEED;
        }
    }

    /// <summary>The ARAM DMA runs at once when its length's low half is written, and raises ARINT.</summary>
    private void AramDma()
    {
        var length = (int)(_aramLength & 0x7FFFFFFF);
        var toMain = (_aramLength & 0x80000000) != 0;
        if (Ram is { } ram)
        {
            for (var i = 0; i < length; i++)
            {
                var main = (int)((_aramMain + i) & 0x01FFFFFF);
                var aram = (int)((_aramAddress + i) & (AramSize - 1));
                if (main >= ram.Length)
                {
                    break;
                }

                if (toMain)
                {
                    ram[main] = _aram[aram];
                }
                else
                {
                    _aram[aram] = ram[main];
                }
            }
        }

        _control |= ArInt;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_aram);
        writer.WriteUInt32(_mailToDsp);
        writer.WriteUInt32(_mailFromDsp);
        writer.WriteUInt16(_control);
        writer.WriteUInt32(_aramMain);
        writer.WriteUInt32(_aramAddress);
        writer.WriteUInt32(_aramLength);
        writer.WriteUInt32(_aiStart);
        writer.WriteUInt16(_aiControl);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_aram);
        _mailToDsp = reader.ReadUInt32();
        _mailFromDsp = reader.ReadUInt32();
        _control = reader.ReadUInt16();
        _aramMain = reader.ReadUInt32();
        _aramAddress = reader.ReadUInt32();
        _aramLength = reader.ReadUInt32();
        _aiStart = reader.ReadUInt32();
        _aiControl = reader.ReadUInt16();
        reader.EndChunk();
    }
}
