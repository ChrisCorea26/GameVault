using System.Buffers.Binary;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The Image Processing Unit (ps2tek: "Image Processing Unit (IPU)"): the EE's MPEG-1/MPEG-2 macroblock decoder.
/// Registers at $10002000 (IPU_CMD, IPU_CTRL, IPU_BP, IPU_TOP); the output FIFO reads at $10007000, the input FIFO
/// writes at $10007010, and DMA channels 4 (IPU_TO) and 3 (IPU_FROM) move quadwords through <see cref="IPs2DmaIpu"/>.
/// </summary>
/// <remarks>
/// <para>
/// Commands (IPU_CMD bits 28–31): BCLR, IDEC (slice decode to RGB), BDEC (macroblock decode to RAW16), VDEC (one VLC
/// symbol), FDEC (32 fixed-length bits), SETIQ, SETVQ, CSC (YCbCr → RGB), PACK (RGB32 → RGB16/INDX4) and SETTH. A
/// command runs as a resumable state machine: each step decodes one whole symbol or none, so when the input FIFO runs
/// dry (or the output FIFO is full) the command simply stops with BUSY set and carries on, from the same point, when
/// the DMA or the EE moves more data (<see cref="WriteIn"/>, <see cref="ReadOut"/>, <see cref="WriteFifo"/>,
/// <see cref="ReadFifo"/>) or the console calls <see cref="Step"/>. A finished command clears BUSY and raises
/// <see cref="Ps2Interrupt.Ipu"/>.
/// </para>
/// <para>
/// The bitstream comes from the 8-quadword input FIFO through a two-quadword internal buffer (IPU_BP.FP), read MSB
/// first in memory byte order from bit IPU_BP.BP. Decoding, dequantisation, the inverse scans and the
/// colour conversion are ported in structure from Play! (BSD-2-Clause, Jean-Philip Desjardins: Source/ee/IPU.cpp);
/// the studio-range CSC coefficients, the dither matrix, the RGB16 alpha rule and the VQ search follow the behaviour
/// documented for the hardware by ps2tek and the PCSX2 project (facts only).
/// </para>
/// </remarks>
public sealed partial class Ps2Ipu : IPs2DmaIpu, IStateful
{
    public const uint BaseAddress = 0x10002000;
    public const uint OutFifoAddress = 0x10007000;
    public const uint InFifoAddress = 0x10007010;

    public const uint RegCmd = 0x00;
    public const uint RegCtrl = 0x10;
    public const uint RegBp = 0x20;
    public const uint RegTop = 0x30;

    /// <summary>Depth of each FIFO in quadwords.</summary>
    public const int FifoDepth = 8;

    public const int CmdBclr = 0;
    public const int CmdIdec = 1;
    public const int CmdBdec = 2;
    public const int CmdVdec = 3;
    public const int CmdFdec = 4;
    public const int CmdSetiq = 5;
    public const int CmdSetvq = 6;
    public const int CmdCsc = 7;
    public const int CmdPack = 8;
    public const int CmdSetth = 9;

    // IPU_CTRL bits.
    private const uint CtrlEcd = 1u << 14;
    private const uint CtrlScd = 1u << 15;
    private const uint CtrlRst = 1u << 30;
    private const uint CtrlBusy = 1u << 31;
    private const uint CtrlWritable = 0x07F30000; // IDP, AS, IVF, QST, MP1, PCT
    private const uint CtrlCbpMask = 0x3Fu << 8;

    // Command phases.
    private const int PhIdle = 0;
    private const int PhSkip = 1;
    private const int PhIdecMbType = 2;
    private const int PhMacroblock = 3;
    private const int PhIdecConvert = 4;
    private const int PhFlush = 5;
    private const int PhIdecMba = 6;
    private const int PhStartCode = 7;
    private const int PhStartCodeScan = 8;
    private const int PhTop = 9;
    private const int PhVdec = 10;
    private const int PhFdec = 11;
    private const int PhReadBytes = 12;
    private const int PhBdecOutput = 13;

    private static readonly StateTag Tag = StateTag.From("IPU ");

    private readonly Ps2Intc _intc;

    // Registers.
    private uint _cmdData;
    private uint _top;
    private uint _ctrl;
    private bool _busy;
    private int _cmd = -1;
    private int _lastCmd = -1;
    private uint _cmdWord;
    private int _phase;

    // Input FIFO and the bitstream's internal buffer. _q0/_q1 hold the buffered quadwords byte-swapped, so the next
    // stream bit is bit 127 - _bp of _q0; bits past the buffered data are zero.
    private readonly UInt128[] _inFifo = new UInt128[FifoDepth];
    private int _inHead;
    private int _inCount;
    private UInt128 _q0;
    private UInt128 _q1;
    private int _fp;
    private int _bp;

    // Output FIFO and the result being moved into it.
    private readonly UInt128[] _outFifo = new UInt128[FifoDepth];
    private int _outHead;
    private int _outCount;
    private readonly byte[] _stage = new byte[1024];
    private int _stageLength;
    private int _stagePosition;

    // Tables set by commands.
    private readonly byte[] _intraMatrix = new byte[64];
    private readonly byte[] _nonIntraMatrix = new byte[64];
    private readonly ushort[] _vqClut = new ushort[16];
    private int _th0;
    private int _th1;

    // Byte input of SETIQ, SETVQ, CSC and PACK; the macroblock count of CSC and PACK.
    private readonly byte[] _input = new byte[1024];
    private int _inputLength;
    private int _inputPosition;
    private int _remaining;

    public Ps2Ipu(Ps2Intc intc)
    {
        _intc = intc;
        Reset();
    }

    /// <summary>A command is running (IPU_CTRL.BUSY).</summary>
    public bool Busy => _busy;

    /// <summary>Quadwords in the input FIFO (IPU_CTRL.IFC).</summary>
    public int InputCount => _inCount;

    /// <summary>Quadwords in the output FIFO (IPU_CTRL.OFC).</summary>
    public int OutputCount => _outCount;

    /// <summary>Power-on state: idle, FIFOs empty, the default quantiser matrices.</summary>
    public void Reset()
    {
        _cmdData = 0;
        _top = 0;
        _ctrl = 0;
        _busy = false;
        _cmd = -1;
        _lastCmd = -1;
        _cmdWord = 0;
        _phase = PhIdle;
        ClearInput();
        ClearOutput();
        Ps2IpuTables.DefaultIntraMatrix.CopyTo(_intraMatrix, 0);
        _nonIntraMatrix.AsSpan().Fill(16);
        Array.Clear(_vqClut);
        _th0 = 0;
        _th1 = 0;
        _inputLength = 0;
        _inputPosition = 0;
        _remaining = 0;
        ResetDecoder();
        ResetDcPredictors();
    }

    // ---- Registers ----

    /// <summary>Reads a 64-bit register; <paramref name="offset"/> is relative to $10002000.</summary>
    public ulong ReadRegister(uint offset)
    {
        switch (offset & 0xF0)
        {
            case RegCmd:
                // With no VDEC/FDEC result, IPU_CMD shows the next 32 bits of the stream (ps2tek: "IPU_CMD").
                if (_lastCmd is not (CmdVdec or CmdFdec) && TryPeekLive32(out uint next))
                {
                    _cmdData = next;
                }

                return _cmdData | (_busy ? 1UL << 63 : 0);

            case RegCtrl:
                return ReadCtrl();

            case RegBp:
                return (uint)(_bp & 0x7F) | ((uint)_inCount << 8) | ((uint)_fp << 16);

            case RegTop:
            {
                bool enough = TryPeekLive32(out uint bits);
                if (!_busy && enough)
                {
                    _top = bits;
                }

                // Bit 63: busy, or fewer than 32 bits in the FIFO (ps2tek: "IPU_TOP").
                return _top | (_busy || !enough ? 1UL << 63 : 0);
            }

            default:
                return 0;
        }
    }

    /// <summary>Reads a 32-bit half of a register (offset bit 2 picks the upper half).</summary>
    public uint ReadRegister32(uint offset)
    {
        ulong value = ReadRegister(offset & 0xF0);
        return (offset & 0xC) switch
        {
            0 => (uint)value,
            4 => (uint)(value >> 32),
            _ => 0,
        };
    }

    /// <summary>Writes a 64-bit register: IPU_CMD starts a command, IPU_CTRL sets the decoder modes or resets.</summary>
    public void WriteRegister(uint offset, ulong value)
    {
        switch (offset & 0xF0)
        {
            case RegCmd:
                StartCommand((uint)value);
                break;
            case RegCtrl:
                WriteCtrl((uint)value);
                break;
        }
    }

    /// <summary>Writes a 32-bit register (writes to upper halves are ignored).</summary>
    public void WriteRegister32(uint offset, uint value)
    {
        if ((offset & 0xC) == 0)
        {
            WriteRegister(offset & 0xF0, value);
        }
    }

    private uint ReadCtrl() =>
        (_ctrl & (CtrlWritable | CtrlEcd | CtrlScd | CtrlCbpMask))
        | (uint)_inCount
        | ((uint)_outCount << 4)
        | (_busy ? CtrlBusy : 0);

    private void WriteCtrl(uint value)
    {
        _ctrl = (_ctrl & ~CtrlWritable) | (value & CtrlWritable);
        if ((value & CtrlRst) == 0)
        {
            return;
        }

        // RST: the whole IPU resets; an interrupted command raises the IPU interrupt (ps2tek: "IPU_CTRL").
        bool wasBusy = _busy;
        ClearInput();
        ClearOutput();
        _ctrl &= CtrlWritable;
        _th0 = 0;
        _th1 = 0;
        _top = 0;
        _cmdData = 0;
        _busy = false;
        _cmd = -1;
        _lastCmd = -1;
        _phase = PhIdle;
        ResetDecoder();
        ResetDcPredictors();
        if (wasBusy)
        {
            _intc.Raise(Ps2Interrupt.Ipu);
        }
    }

    private int IntraDcPrecision => (int)(_ctrl >> 16) & 3;
    private bool AlternateScan => (_ctrl & (1u << 20)) != 0;
    private bool IntraVlcFormat => (_ctrl & (1u << 21)) != 0;
    private bool NonLinearQuantiser => (_ctrl & (1u << 22)) != 0;
    private bool Mpeg1 => (_ctrl & (1u << 23)) != 0;
    private int PictureType => (int)(_ctrl >> 24) & 7;

    // ---- FIFOs ----

    /// <summary>Reads a quadword of the output FIFO ($10007000); 0 when it is empty.</summary>
    public UInt128 ReadFifo()
    {
        if (_outCount == 0)
        {
            Step();
            if (_outCount == 0)
            {
                return 0;
            }
        }

        UInt128 value = DequeueOutput();
        Step();
        return value;
    }

    /// <summary>Writes a quadword to the input FIFO ($10007010); dropped when it is full.</summary>
    public void WriteFifo(UInt128 value)
    {
        if (_inCount < FifoDepth)
        {
            _inFifo[(_inHead + _inCount) & (FifoDepth - 1)] = value;
            _inCount++;
        }

        Step();
    }

    /// <inheritdoc/>
    public int WriteIn(ReadOnlySpan<UInt128> data)
    {
        int taken = 0;
        while (taken < data.Length && _inCount < FifoDepth)
        {
            int n = Math.Min(FifoDepth - _inCount, data.Length - taken);
            for (int i = 0; i < n; i++)
            {
                _inFifo[(_inHead + _inCount) & (FifoDepth - 1)] = data[taken + i];
                _inCount++;
            }

            taken += n;
            Step();
        }

        return taken;
    }

    /// <inheritdoc/>
    public int ReadOut(Span<UInt128> destination)
    {
        int count = 0;
        while (count < destination.Length)
        {
            if (_outCount == 0)
            {
                Step();
                if (_outCount == 0)
                {
                    break;
                }
            }

            destination[count++] = DequeueOutput();
        }

        Step();
        return count;
    }

    private UInt128 DequeueOutput()
    {
        UInt128 value = _outFifo[_outHead];
        _outHead = (_outHead + 1) & (FifoDepth - 1);
        _outCount--;
        return value;
    }

    private void ClearInput()
    {
        Array.Clear(_inFifo);
        _inHead = 0;
        _inCount = 0;
        _q0 = 0;
        _q1 = 0;
        _fp = 0;
        _bp = 0;
    }

    private void ClearOutput()
    {
        Array.Clear(_outFifo);
        _outHead = 0;
        _outCount = 0;
        _stageLength = 0;
        _stagePosition = 0;
    }

    // ---- Bitstream ----

    /// <summary>Moves quadwords from the input FIFO into the internal buffer until <paramref name="bits"/> bits from BP are buffered; false if the FIFO runs out first.</summary>
    private bool Fill(int bits)
    {
        while (_fp * 128 < _bp + bits)
        {
            if (_inCount == 0 || _fp == 2)
            {
                return false;
            }

            UInt128 q = BinaryPrimitives.ReverseEndianness(_inFifo[_inHead]);
            _inHead = (_inHead + 1) & (FifoDepth - 1);
            _inCount--;
            if (_fp == 0)
            {
                _q0 = q;
            }
            else
            {
                _q1 = q;
            }

            _fp++;
        }

        return true;
    }

    /// <summary>Buffers up to <paramref name="bits"/> bits and returns how many are available from BP.</summary>
    private int Available(int bits)
    {
        Fill(bits);
        return _fp * 128 - _bp;
    }

    /// <summary>The next 64 bits of the buffered stream (MSB first), zero past the buffered data.</summary>
    private ulong Window()
    {
        UInt128 w = _bp == 0 ? _q0 : (_q0 << _bp) | (_q1 >> (128 - _bp));
        return (ulong)(w >> 64);
    }

    /// <summary>Consumes <paramref name="bits"/> buffered bits (at most 128 - BP + 128).</summary>
    private void Advance(int bits)
    {
        _bp += bits;
        if (_bp >= 128)
        {
            _bp -= 128;
            _q0 = _q1;
            _q1 = 0;
            _fp--;
        }
    }

    /// <summary>The next 32 stream bits across the internal buffer and the FIFO, without consuming anything.</summary>
    private bool TryPeekLive32(out uint value)
    {
        if ((_fp + _inCount) * 128 - _bp < 32)
        {
            value = 0;
            return false;
        }

        UInt128 a = LiveQuadword(0);
        UInt128 w = _bp == 0 ? a : (a << _bp) | (LiveQuadword(1) >> (128 - _bp));
        value = (uint)(w >> 96);
        return true;
    }

    private UInt128 LiveQuadword(int index)
    {
        if (index < _fp)
        {
            return index == 0 ? _q0 : _q1;
        }

        int fifoIndex = index - _fp;
        return fifoIndex < _inCount
            ? BinaryPrimitives.ReverseEndianness(_inFifo[(_inHead + fifoIndex) & (FifoDepth - 1)])
            : 0;
    }

    // ---- Commands ----

    private void StartCommand(uint value)
    {
        // A new command replaces one still running.
        _ctrl &= ~(CtrlEcd | CtrlScd);
        _cmdWord = value;
        _cmd = (int)(value >> 28);
        _lastCmd = _cmd;
        _busy = true;

        switch (_cmd)
        {
            case CmdBclr:
                // Clears the input FIFO; the stream restarts at bit BP of the next quadword.
                ClearInput();
                _bp = (int)(value & 0x7F);
                Complete();
                return;

            case CmdSetth:
                _th0 = (int)(value & 0x1FF);
                _th1 = (int)((value >> 16) & 0x1FF);
                Complete();
                return;

            case CmdIdec:
            case CmdBdec:
            case CmdVdec:
            case CmdFdec:
            case CmdSetiq:
                _phase = PhSkip;
                break;

            case CmdSetvq:
                BeginRead(32);
                break;

            case CmdCsc:
            case CmdPack:
                _remaining = (int)(value & 0x7FF);
                if (_remaining == 0)
                {
                    Complete();
                    return;
                }

                BeginRead(_cmd == CmdCsc ? 384 : 1024);
                break;

            default:
                // Codes 10–15 do nothing.
                Complete();
                return;
        }

        Step();
    }

    /// <summary>
    /// Runs the current command as far as the FIFOs allow. FIFO traffic calls it; the console may also call it each
    /// slice. Does nothing when idle or stalled.
    /// </summary>
    public void Step()
    {
        if (_busy && Execute())
        {
            Complete();
        }
    }

    private void Complete()
    {
        _busy = false;
        _cmd = -1;
        _phase = PhIdle;
        _intc.Raise(Ps2Interrupt.Ipu);
    }

    private void Error()
    {
        _ctrl |= CtrlEcd;
    }

    private void BeginRead(int length)
    {
        _inputLength = length;
        _inputPosition = 0;
        _phase = PhReadBytes;
    }

    /// <summary>Runs the command; true when it has finished, false when it waits for data or output space.</summary>
    private bool Execute()
    {
        while (true)
        {
            switch (_phase)
            {
                case PhSkip:
                {
                    // FB: bits skipped before the command starts.
                    int fb = (int)(_cmdWord & 0x3F);
                    if (!Fill(fb))
                    {
                        return false;
                    }

                    Advance(fb);
                    switch (_cmd)
                    {
                        case CmdIdec:
                            ResetDcPredictors();
                            _mbQsc = (int)(_cmdWord >> 16) & 0x1F;
                            _phase = PhIdecMbType;
                            break;
                        case CmdBdec:
                            if ((_cmdWord & (1u << 26)) != 0)
                            {
                                ResetDcPredictors();
                            }

                            BeginMacroblock((_cmdWord & (1u << 27)) != 0, (_cmdWord & (1u << 25)) != 0,
                                (int)(_cmdWord >> 16) & 0x1F);
                            break;
                        case CmdVdec:
                            _phase = PhVdec;
                            break;
                        case CmdFdec:
                            _phase = PhFdec;
                            break;
                        default:
                            BeginRead(64);
                            break;
                    }

                    break;
                }

                case PhIdecMbType:
                {
                    int r = IdecMacroblockType();
                    if (r == StepStall)
                    {
                        return false;
                    }

                    if (r == StepError)
                    {
                        Error();
                        return true;
                    }

                    break;
                }

                case PhMacroblock:
                {
                    int r = MacroblockStep();
                    if (r == StepStall)
                    {
                        return false;
                    }

                    if (r == StepError)
                    {
                        Error();
                        return true;
                    }

                    if (r == StepDone)
                    {
                        _ctrl = (_ctrl & ~CtrlCbpMask) | ((uint)_cbp << 8);
                        _phase = _cmd == CmdIdec ? PhIdecConvert : PhBdecOutput;
                    }

                    break;
                }

                case PhIdecConvert:
                {
                    Span<byte> ycc = _input.AsSpan(0, 384);
                    for (int i = 0; i < 384; i++)
                    {
                        ycc[i] = (byte)Math.Clamp((int)_mb[i], 0, 255);
                    }

                    ConvertYcc(ycc, (_cmdWord & (1u << 27)) != 0, (_cmdWord & (1u << 26)) != 0,
                        (_cmdWord & (1u << 25)) != 0);
                    _phase = PhFlush;
                    break;
                }

                case PhBdecOutput:
                {
                    // RAW16: Y as a 16×16 block, then Cb and Cr 8×8, each sample a little-endian halfword.
                    for (int i = 0; i < 384; i++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(_stage.AsSpan(i * 2), _mb[i]);
                    }

                    _stageLength = 768;
                    _stagePosition = 0;
                    _phase = PhFlush;
                    break;
                }

                case PhFlush:
                {
                    while (_stagePosition < _stageLength)
                    {
                        if (_outCount == FifoDepth)
                        {
                            return false;
                        }

                        _outFifo[(_outHead + _outCount) & (FifoDepth - 1)] =
                            BinaryPrimitives.ReadUInt128LittleEndian(_stage.AsSpan(_stagePosition));
                        _outCount++;
                        _stagePosition += 16;
                    }

                    switch (_cmd)
                    {
                        case CmdIdec:
                            _mba = 0;
                            _phase = PhIdecMba;
                            break;
                        case CmdBdec:
                            _phase = PhStartCode;
                            break;
                        default:
                            if (--_remaining == 0)
                            {
                                return true;
                            }

                            BeginRead(_cmd == CmdCsc ? 384 : 1024);
                            break;
                    }

                    break;
                }

                case PhIdecMba:
                {
                    int r = IdecAddressIncrement();
                    if (r == StepStall)
                    {
                        return false;
                    }

                    break;
                }

                case PhStartCode:
                {
                    // Eight zero bits after the data: byte-align and look for a start code (00 00 01).
                    if (Available(8) < 8)
                    {
                        return false;
                    }

                    if ((Window() >> 56) != 0)
                    {
                        _phase = PhTop;
                        break;
                    }

                    Advance((8 - (_bp & 7)) & 7);
                    _phase = PhStartCodeScan;
                    break;
                }

                case PhStartCodeScan:
                {
                    if (Available(24) < 24)
                    {
                        return false;
                    }

                    uint code = (uint)(Window() >> 40);
                    if (code == 0)
                    {
                        Advance(8);
                        break;
                    }

                    _ctrl |= code == 1 ? CtrlScd : CtrlEcd;
                    _phase = PhTop;
                    break;
                }

                case PhTop:
                    if (Available(32) < 32)
                    {
                        return false;
                    }

                    _top = (uint)(Window() >> 32);
                    return true;

                case PhVdec:
                {
                    int r = DecodeVlcSymbol();
                    if (r == StepStall)
                    {
                        return false;
                    }

                    _phase = PhTop;
                    break;
                }

                case PhFdec:
                    if (Available(32) < 32)
                    {
                        return false;
                    }

                    _cmdData = (uint)(Window() >> 32);
                    _top = _cmdData;
                    return true;

                case PhReadBytes:
                {
                    while (_inputPosition < _inputLength)
                    {
                        if (Available(32) < 32)
                        {
                            return false;
                        }

                        BinaryPrimitives.WriteUInt32BigEndian(_input.AsSpan(_inputPosition), (uint)(Window() >> 32));
                        Advance(32);
                        _inputPosition += 4;
                    }

                    switch (_cmd)
                    {
                        case CmdSetiq:
                            _input.AsSpan(0, 64).CopyTo((_cmdWord & (1u << 27)) != 0 ? _nonIntraMatrix : _intraMatrix);
                            return true;
                        case CmdSetvq:
                            for (int i = 0; i < 16; i++)
                            {
                                _vqClut[i] = BinaryPrimitives.ReadUInt16LittleEndian(_input.AsSpan(i * 2));
                            }

                            return true;
                        case CmdCsc:
                            ConvertYcc(_input.AsSpan(0, 384), (_cmdWord & (1u << 27)) != 0,
                                (_cmdWord & (1u << 26)) != 0, false);
                            _phase = PhFlush;
                            break;
                        default:
                            Pack(_input, (_cmdWord & (1u << 27)) != 0, (_cmdWord & (1u << 26)) != 0);
                            _phase = PhFlush;
                            break;
                    }

                    break;
                }

                default:
                    return true;
            }
        }
    }

    /// <summary>IDEC: macroblock_type (I-picture), dct_type when DTD, quantiser_scale_code when the type says so.</summary>
    private int IdecMacroblockType()
    {
        int available = Available(8);
        if (available < 1)
        {
            return StepStall;
        }

        ulong w = Window();
        int used;
        bool quant;
        if ((w >> 63) != 0)
        {
            used = 1;
            quant = false;
        }
        else
        {
            if (available < 2)
            {
                return StepStall;
            }

            if (((w >> 62) & 1) == 0)
            {
                return StepError;
            }

            used = 2;
            quant = true;
        }

        bool field = false;
        if ((_cmdWord & (1u << 24)) != 0)
        {
            field = ((w << used) >> 63) != 0;
            used++;
        }

        int qsc = _mbQsc;
        if (quant)
        {
            qsc = (int)((w << used) >> 59);
            used += 5;
        }

        if (used > available)
        {
            return StepStall;
        }

        Advance(used);
        BeginMacroblock(true, field, qsc);
        return StepContinue;
    }

    /// <summary>IDEC: the macroblock_address_increment before the next macroblock, or the end of the slice.</summary>
    private int IdecAddressIncrement()
    {
        int available = Available(Ps2IpuTables.MbaBits);
        ulong w = Window();
        int code11 = (int)(w >> (64 - Ps2IpuTables.MbaBits));
        int entry = Ps2IpuTables.Mba[code11];
        int length = entry & 31;
        if (length != 0 && length <= available)
        {
            Advance(length);
            _mba += entry >> 5;

            // A skipped macroblock resets the DC predictors (ISO/IEC 13818-2 §7.2.1).
            if (_mba > 1)
            {
                ResetDcPredictors();
            }

            _phase = PhIdecMbType;
            return StepContinue;
        }

        if (available < Ps2IpuTables.MbaBits)
        {
            return StepStall;
        }

        if (code11 == Ps2IpuTables.MbaEscape)
        {
            Advance(11);
            _mba += 33;
            return StepContinue;
        }

        if (code11 == Ps2IpuTables.MbaStuffing && Mpeg1)
        {
            Advance(11);
            return StepContinue;
        }

        // No increment: the slice ends.
        _phase = PhStartCode;
        return StepContinue;
    }

    /// <summary>VDEC: one symbol of the table IPU_CMD.TBL picks; the result is the value with the code length in bits 16 up.</summary>
    private int DecodeVlcSymbol()
    {
        int table = (int)(_cmdWord >> 26) & 3;
        int[] lut;
        int bits;
        switch (table)
        {
            case 0:
                lut = Ps2IpuTables.Mba;
                bits = Ps2IpuTables.MbaBits;
                break;
            case 1:
                // Picture type 0 decodes as I (games leave it unset; PCSX2 notes Kaiketsu Zorro).
                lut = PictureType switch
                {
                    2 => Ps2IpuTables.MbTypeP,
                    3 => Ps2IpuTables.MbTypeB,
                    4 => Ps2IpuTables.MbTypeD,
                    _ => Ps2IpuTables.MbTypeI,
                };
                bits = Ps2IpuTables.MbTypeBits;
                break;
            case 2:
                lut = Ps2IpuTables.Motion;
                bits = Ps2IpuTables.MotionBits;
                break;
            default:
                lut = Ps2IpuTables.Dmv;
                bits = Ps2IpuTables.DmvBits;
                break;
        }

        int available = Available(bits);
        ulong w = Window();
        int code = (int)(w >> (64 - bits));
        int entry = lut[code];
        int length = entry & 31;
        if (length != 0 && length <= available)
        {
            Advance(length);
            _cmdData = ((uint)length << 16) | ((uint)(entry >> 5) & 0xFFFF);
            return StepContinue;
        }

        if (available < bits)
        {
            return StepStall;
        }

        if (table == 0 && code == Ps2IpuTables.MbaEscape)
        {
            Advance(11);
            _cmdData = (11u << 16) | 0x23;
            return StepContinue;
        }

        if (table == 0 && code == Ps2IpuTables.MbaStuffing && Mpeg1)
        {
            Advance(11);
            _cmdData = (11u << 16) | 0x22;
            return StepContinue;
        }

        // No such code: the result is 0 and ECD is set.
        _cmdData = 0;
        Error();
        return StepContinue;
    }

    // ---- Save states ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteUInt32(_cmdData);
        writer.WriteUInt32(_top);
        writer.WriteUInt32(_ctrl);
        writer.WriteBool(_busy);
        writer.WriteInt32(_cmd);
        writer.WriteInt32(_lastCmd);
        writer.WriteUInt32(_cmdWord);
        writer.WriteInt32(_phase);

        WriteQuadwords(writer, _inFifo);
        writer.WriteInt32(_inHead);
        writer.WriteInt32(_inCount);
        WriteQuadword(writer, _q0);
        WriteQuadword(writer, _q1);
        writer.WriteInt32(_fp);
        writer.WriteInt32(_bp);

        WriteQuadwords(writer, _outFifo);
        writer.WriteInt32(_outHead);
        writer.WriteInt32(_outCount);
        writer.WriteBytes(_stage);
        writer.WriteInt32(_stageLength);
        writer.WriteInt32(_stagePosition);

        writer.WriteBytes(_intraMatrix);
        writer.WriteBytes(_nonIntraMatrix);
        foreach (ushort entry in _vqClut)
        {
            writer.WriteUInt16(entry);
        }

        writer.WriteInt32(_th0);
        writer.WriteInt32(_th1);
        writer.WriteBytes(_input);
        writer.WriteInt32(_inputLength);
        writer.WriteInt32(_inputPosition);
        writer.WriteInt32(_remaining);

        SaveDecoder(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        if (reader.BeginChunk(Tag) != 1)
        {
            throw new StateFormatException("Unsupported IPU state version.");
        }

        _cmdData = reader.ReadUInt32();
        _top = reader.ReadUInt32();
        _ctrl = reader.ReadUInt32();
        _busy = reader.ReadBool();
        _cmd = reader.ReadInt32();
        _lastCmd = reader.ReadInt32();
        _cmdWord = reader.ReadUInt32();
        _phase = reader.ReadInt32();

        ReadQuadwords(reader, _inFifo);
        _inHead = reader.ReadInt32() & (FifoDepth - 1);
        _inCount = Math.Clamp(reader.ReadInt32(), 0, FifoDepth);
        _q0 = ReadQuadword(reader);
        _q1 = ReadQuadword(reader);
        _fp = Math.Clamp(reader.ReadInt32(), 0, 2);
        _bp = reader.ReadInt32() & 0x7F;

        ReadQuadwords(reader, _outFifo);
        _outHead = reader.ReadInt32() & (FifoDepth - 1);
        _outCount = Math.Clamp(reader.ReadInt32(), 0, FifoDepth);
        reader.ReadBytes(_stage);
        _stageLength = Math.Clamp(reader.ReadInt32(), 0, _stage.Length) & ~15;
        _stagePosition = Math.Clamp(reader.ReadInt32(), 0, _stageLength) & ~15;

        reader.ReadBytes(_intraMatrix);
        reader.ReadBytes(_nonIntraMatrix);
        for (int i = 0; i < _vqClut.Length; i++)
        {
            _vqClut[i] = reader.ReadUInt16();
        }

        _th0 = reader.ReadInt32() & 0x1FF;
        _th1 = reader.ReadInt32() & 0x1FF;
        reader.ReadBytes(_input);
        _inputLength = Math.Clamp(reader.ReadInt32(), 0, _input.Length) & ~3;
        _inputPosition = Math.Clamp(reader.ReadInt32(), 0, _inputLength) & ~3;
        _remaining = reader.ReadInt32();

        LoadDecoder(reader);
        reader.EndChunk();
    }

    private static void WriteQuadword(StateWriter writer, UInt128 value)
    {
        writer.WriteUInt64((ulong)value);
        writer.WriteUInt64((ulong)(value >> 64));
    }

    private static UInt128 ReadQuadword(StateReader reader)
    {
        ulong lo = reader.ReadUInt64();
        ulong hi = reader.ReadUInt64();
        return ((UInt128)hi << 64) | lo;
    }

    private static void WriteQuadwords(StateWriter writer, UInt128[] values)
    {
        foreach (UInt128 value in values)
        {
            WriteQuadword(writer, value);
        }
    }

    private static void ReadQuadwords(StateReader reader, UInt128[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = ReadQuadword(reader);
        }
    }
}
