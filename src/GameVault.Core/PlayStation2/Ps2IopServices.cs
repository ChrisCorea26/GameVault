using System.Buffers.Binary;
using System.Text;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The IOP's side of the SIF, answered in C# when there is no BIOS (Phase 17 plan §7): the SIF command layer (the
/// init handshake, SET_SREG, RPC bind/call/end and other-data requests) and the <c>fileio</c> RPC server, whose writes to
/// stdout go to <see cref="Ps2Console.DebugOutput"/>. The IOP's CPU doesn't run.
/// </summary>
/// <remarks>
/// <para>
/// It works through the real hardware path: the EE's SifSetDma sends each command through the DMAC's channel 6 into
/// the SIF1 FIFO, preceded by the IOP's DMA tag (destination and size); the services take the words out of the FIFO
/// into the IOP's RAM, and answer through the SIF0 FIFO with the EE's destination DMAtags inline, which the EE's channel
/// 5 (a destination chain) writes to the EE's receive buffer, raising its interrupt.
/// </para>
/// <para>
/// Protocol (ps2tek: "SIF RPC Basics", "SIF RPC Structs and Definitions", "SIF RPC Commands"; ps2sdk's sifcmd and
/// sifrpc, AFL 2.0; Play!'s Source/ee/SIF.cpp, BSD-2-Clause): at boot the IOP's command buffer address is in SMCOM and
/// SMFLG has SIF DMA, SIFCMD and boot-end set. Every server ID binds: <c>fileio</c> (0x80000001) answers open, close
/// and write (stdout, stderr and <c>tty:</c> files print; other files don't exist) and returns 0 for its other calls;
/// other servers return zeros.
/// </para>
/// </remarks>
public sealed class Ps2IopServices : IStateful
{
    /// <summary>The IOP's SIF command receive buffer (SMCOM).</summary>
    public const uint CommandBuffer = 0x0000_1000;

    /// <summary>The servers' fake server-data structures, one per bound server ID.</summary>
    private const uint ServerDataBase = 0x0000_2000;

    /// <summary>The servers' receive buffers (where a call's arguments land), 64 KB each.</summary>
    private const uint ServerBufferBase = 0x0004_0000;
    private const uint ServerBufferSize = 0x1_0000;
    private const int MaxServers = 24;

    public const uint FileioServer = 0x8000_0001;

    // SIF command IDs (ps2sdk: sifcmd-common.h).
    private const uint CmdChangeSaddr = 0x8000_0000;
    private const uint CmdSetSreg = 0x8000_0001;
    private const uint CmdInit = 0x8000_0002;
    private const uint CmdReset = 0x8000_0003;
    private const uint CmdRpcEnd = 0x8000_0008;
    private const uint CmdRpcBind = 0x8000_0009;
    private const uint CmdRpcCall = 0x8000_000A;
    private const uint CmdRpcRdata = 0x8000_000C;

    private const int RpcPacketSize = 0x40;

    private static readonly StateTag Tag = StateTag.From("IOPS");

    private readonly Ps2Console _console;
    private readonly Ps2Sif _sif;
    private readonly byte[] _iopRam;

    // The SIF1 transfer being received: its IOP tag (address, flags) and the words still to come.
    private bool _haveTag;
    private uint _address;
    private uint _start;
    private uint _wordsLeft;
    private uint _pendingTagWord;
    private bool _havePendingTagWord;

    /// <summary>The words waiting to go out through SIF0 (EE destination DMAtags and data, in whole quadwords).</summary>
    private readonly Queue<uint> _outgoing = new();

    /// <summary>The EE's SIF command receive buffer (from SIF_CMD_INIT_CMD or CHANGE_SADDR).</summary>
    private uint _eeReceiveBuffer;

    /// <summary>The server IDs bound so far, by index (server-data and buffer addresses follow the index).</summary>
    private readonly List<uint> _servers = [];

    /// <summary>fileio's open files: true for the ones that print (stdout-like).</summary>
    private readonly Dictionary<int, bool> _files = [];

    public Ps2IopServices(Ps2Console console)
    {
        _console = console;
        _sif = console.Sif;
        _iopRam = console.Iop.Ram;
    }

    /// <summary>Called with each command received (tracing).</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>The IOP has "booted": its command buffer in SMCOM and the boot flags in SMFLG.</summary>
    public void Reset()
    {
        _haveTag = false;
        _havePendingTagWord = false;
        _address = _start = _wordsLeft = _pendingTagWord = 0;
        _outgoing.Clear();
        _eeReceiveBuffer = 0;
        _servers.Clear();
        _files.Clear();
        _sif.Smcom = CommandBuffer;
        _sif.IopWriteSmflg(Ps2Sif.FlagDmaReady | Ps2Sif.FlagCmdReady | Ps2Sif.FlagIopBooted);
    }

    /// <summary>Takes what the EE sent through SIF1, answers the commands, and feeds SIF0 (called after each slice).</summary>
    public void Service()
    {
        Receive();
        while (_outgoing.Count >= 4 && _sif.Sif0.Free >= 4)
        {
            for (var i = 0; i < 4; i++)
            {
                _sif.Sif0.TryPush(_outgoing.Dequeue());
            }
        }
    }

    // ---- SIF1: from the EE ----

    private void Receive()
    {
        var fifo = _sif.Sif1;
        while (fifo.Count > 0)
        {
            if (!_haveTag)
            {
                // The IOP's tag: the destination with its flags, then the size in words.
                if (!_havePendingTagWord)
                {
                    fifo.TryPop(out _pendingTagWord);
                    _havePendingTagWord = true;
                    continue;
                }

                fifo.TryPop(out uint size);
                _havePendingTagWord = false;
                _address = _start = _pendingTagWord & 0x00FF_FFFC;
                _wordsLeft = size;
                _haveTag = true;
                if (_wordsLeft == 0)
                {
                    TransferDone();
                }

                continue;
            }

            fifo.TryPop(out uint word);
            BinaryPrimitives.WriteUInt32LittleEndian(_iopRam.AsSpan((int)(_address & (Ps2Iop.RamSize - 4))), word);
            _address += 4;
            if (--_wordsLeft == 0)
            {
                TransferDone();
            }
        }
    }

    private void TransferDone()
    {
        _haveTag = false;
        if (_start == CommandBuffer)
        {
            HandleCommand();
        }
    }

    private uint IopWord(uint address) => BinaryPrimitives.ReadUInt32LittleEndian(_iopRam.AsSpan((int)(address & (Ps2Iop.RamSize - 4))));

    private void HandleCommand()
    {
        var p = CommandBuffer;
        var cid = IopWord(p + 8);
        var opt = IopWord(p + 12);
        Trace?.Invoke($"iop cmd {cid:X8} opt={opt:X} w0={IopWord(p):X} w4={IopWord(p + 16):X} w5={IopWord(p + 20):X}");
        switch (cid)
        {
            case CmdChangeSaddr:
                _eeReceiveBuffer = IopWord(p + 16);
                break;
            case CmdInit:
                if (opt == 0)
                {
                    _eeReceiveBuffer = IopWord(p + 16); // SIFCMD's init: the EE's receive buffer
                }
                else
                {
                    // SifInitRpc's init: the IOP answers by setting the EE's RPCINIT software register.
                    var packet = new byte[0x18];
                    WriteHeader(packet, CmdSetSreg);
                    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), 0); // SIF_SREG_RPCINIT
                    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), 1);
                    SendPacket(packet);
                }

                break;
            case CmdReset:
                // SifIopReset: the IOP reboots at once, its flags set again.
                _servers.Clear();
                _files.Clear();
                _sif.IopWriteSmflg(Ps2Sif.FlagDmaReady | Ps2Sif.FlagCmdReady | Ps2Sif.FlagIopBooted);
                break;
            case CmdRpcBind:
                Bind(p);
                break;
            case CmdRpcCall:
                Call(p);
                break;
            case CmdRpcRdata:
                OtherData(p);
                break;
        }
    }

    // ---- RPC ----

    private void Bind(uint p)
    {
        var sid = IopWord(p + 0x20);
        var index = _servers.IndexOf(sid);
        if (index < 0 && _servers.Count < MaxServers)
        {
            _servers.Add(sid);
            index = _servers.Count - 1;
        }

        var reply = RequestEnd(p, CmdRpcBind);
        if (index >= 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(0x24), ServerDataBase + (uint)index * 0x40); // sd
            BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(0x28), ServerBufferBase + (uint)index * ServerBufferSize); // buf
            BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(0x2C), 0); // cbuf
        }

        SendPacket(reply);
    }

    private void Call(uint p)
    {
        var function = IopWord(p + 0x20);
        var sendSize = IopWord(p + 0x24);
        var receive = IopWord(p + 0x28);
        var receiveSize = IopWord(p + 0x2C);
        var mode = IopWord(p + 0x30);
        var sd = IopWord(p + 0x34);
        var index = (int)((sd - ServerDataBase) / 0x40);
        var sid = index >= 0 && index < _servers.Count ? _servers[index] : 0;
        var buffer = ServerBufferBase + (uint)Math.Max(index, 0) * ServerBufferSize;

        var result = new byte[(Math.Min(receiveSize, 0x1000u) + 15) & ~15u];
        if (sid == FileioServer)
        {
            Fileio(function, buffer, sendSize, result);
        }

        if (receive != 0 && receiveSize > 0)
        {
            SendData(receive, result, irq: false);
        }

        if (mode != 0)
        {
            SendPacket(RequestEnd(p, CmdRpcCall));
        }
    }

    /// <summary>SIF_CMD_RPC_RDATA: the EE asks for data from IOP memory.</summary>
    private void OtherData(uint p)
    {
        var receiveData = IopWord(p + 0x1C);
        var source = IopWord(p + 0x20);
        var destination = IopWord(p + 0x24);
        var size = IopWord(p + 0x28);
        var data = new byte[(Math.Min(size, 0x10000u) + 15) & ~15u];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = _iopRam[(source + (uint)i) & (Ps2Iop.RamSize - 1)];
        }

        if (size > 0)
        {
            SendData(destination, data, irq: false);
        }

        var reply = RequestEnd(p, CmdRpcRdata);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(0x1C), receiveData);
        SendPacket(reply);
    }

    /// <summary>An RPC_END packet answering the request at <paramref name="p"/> (rec_id, pkt_addr, rpc_id, cd, cid).</summary>
    private byte[] RequestEnd(uint p, uint requestCid)
    {
        var packet = new byte[RpcPacketSize];
        WriteHeader(packet, CmdRpcEnd);
        for (uint offset = 0x10; offset < 0x20; offset += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan((int)offset), IopWord(p + offset));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x20), requestCid);
        return packet;
    }

    private static void WriteHeader(Span<byte> packet, uint cid)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length & 0xFF); // psize; no extra data
        BinaryPrimitives.WriteUInt32LittleEndian(packet[4..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[8..], cid);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[12..], 0);
    }

    // ---- fileio (server 0x80000001) ----

    private const int FioOpen = 0;
    private const int FioClose = 1;
    private const int FioWrite = 3;
    private const int ENoEnt = 2;

    private void Fileio(uint function, uint buffer, uint sendSize, byte[] result)
    {
        var value = 0;
        switch ((int)function)
        {
            case FioOpen:
            {
                var name = IopString(buffer + 4, 256);
                if (name.StartsWith("tty", StringComparison.OrdinalIgnoreCase))
                {
                    var fd = 3;
                    while (_files.ContainsKey(fd))
                    {
                        fd++;
                    }

                    _files[fd] = true;
                    value = fd;
                }
                else
                {
                    value = -ENoEnt;
                }

                break;
            }

            case FioClose:
                value = _files.Remove((int)IopWord(buffer)) ? 0 : -9; // EBADF
                break;
            case FioWrite:
                value = Write((int)IopWord(buffer), IopWord(buffer + 4), IopWord(buffer + 8), IopWord(buffer + 12), buffer + 16);
                break;
        }

        if (result.Length >= 4)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result, value);
        }
    }

    /// <summary>fioWrite: the first <paramref name="misaligned"/> bytes came with the arguments, the rest are in EE memory.</summary>
    private int Write(int fd, uint pointer, uint size, uint misaligned, uint alignedBytes)
    {
        var prints = fd is 1 or 2 || (_files.TryGetValue(fd, out var tty) && tty);
        if (!prints)
        {
            return fd == 0 ? -9 : (int)size;
        }

        misaligned = Math.Min(misaligned, Math.Min(size, 16u));
        var output = _console.DebugOutput;
        for (uint i = 0; i < size; i++)
        {
            var b = i < misaligned ? _iopRam[(alignedBytes + i) & (Ps2Iop.RamSize - 1)] : EeByte(pointer + i);
            output?.Invoke((char)b);
        }

        return (int)size;
    }

    private byte EeByte(uint address) => (address >> 28) == 7
        ? _console.Cpu.Scratchpad[address & 0x3FFF]
        : _console.Rdram[address & (Ps2Console.RdramSize - 1)];

    private string IopString(uint address, int max)
    {
        var text = new StringBuilder();
        for (var i = 0; i < max; i++)
        {
            var b = _iopRam[(address + (uint)i) & (Ps2Iop.RamSize - 1)];
            if (b == 0)
            {
                break;
            }

            text.Append((char)b);
        }

        return text.ToString();
    }

    // ---- SIF0: to the EE ----

    /// <summary>A command packet to the EE's receive buffer, ending the EE's chain with an interrupt.</summary>
    private void SendPacket(byte[] packet) => SendData(_eeReceiveBuffer, packet, irq: true);

    /// <summary>
    /// Queues <paramref name="data"/> for EE address <paramref name="address"/>: a destination DMAtag (CNT, or END with
    /// IRQ for the last part of a command) as a whole quadword, then the data in quadwords.
    /// </summary>
    private void SendData(uint address, byte[] data, bool irq)
    {
        var qwc = (uint)(data.Length + 15) / 16;
        var tag = qwc | (irq ? (7u << 28) | 0x8000_0000u : 1u << 28);
        _outgoing.Enqueue(tag);
        _outgoing.Enqueue(address & 0x01FF_FFF0);
        _outgoing.Enqueue(0);
        _outgoing.Enqueue(0);
        for (var i = 0; i < qwc * 16; i += 4)
        {
            _outgoing.Enqueue(i + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) : 0);
        }
    }

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBool(_haveTag);
        writer.WriteUInt32(_address);
        writer.WriteUInt32(_start);
        writer.WriteUInt32(_wordsLeft);
        writer.WriteUInt32(_pendingTagWord);
        writer.WriteBool(_havePendingTagWord);
        writer.WriteUInt32(_eeReceiveBuffer);
        writer.WriteInt32(_outgoing.Count);
        foreach (var word in _outgoing)
        {
            writer.WriteUInt32(word);
        }

        writer.WriteInt32(_servers.Count);
        foreach (var sid in _servers)
        {
            writer.WriteUInt32(sid);
        }

        writer.WriteInt32(_files.Count);
        foreach (var (fd, prints) in _files)
        {
            writer.WriteInt32(fd);
            writer.WriteBool(prints);
        }

        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        if (reader.BeginChunk(Tag) != 1)
        {
            throw new StateFormatException("Unsupported IOP services state version.");
        }

        _haveTag = reader.ReadBool();
        _address = reader.ReadUInt32();
        _start = reader.ReadUInt32();
        _wordsLeft = reader.ReadUInt32();
        _pendingTagWord = reader.ReadUInt32();
        _havePendingTagWord = reader.ReadBool();
        _eeReceiveBuffer = reader.ReadUInt32();
        var count = reader.ReadInt32();
        if (count is < 0 or > 1 << 20)
        {
            throw new StateFormatException("SIF0 queue too long.");
        }

        _outgoing.Clear();
        for (var i = 0; i < count; i++)
        {
            _outgoing.Enqueue(reader.ReadUInt32());
        }

        var servers = reader.ReadInt32();
        if (servers is < 0 or > MaxServers)
        {
            throw new StateFormatException("Too many RPC servers.");
        }

        _servers.Clear();
        for (var i = 0; i < servers; i++)
        {
            _servers.Add(reader.ReadUInt32());
        }

        var files = reader.ReadInt32();
        if (files is < 0 or > 1024)
        {
            throw new StateFormatException("Too many open files.");
        }

        _files.Clear();
        for (var i = 0; i < files; i++)
        {
            var fd = reader.ReadInt32();
            _files[fd] = reader.ReadBool();
        }

        reader.EndChunk();
    }
}
