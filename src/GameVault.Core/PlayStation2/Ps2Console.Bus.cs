using System.Buffers.Binary;
using GameVault.Core.Cpu.R5900;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

// The EE's physical bus (ps2tek: "EE Physical Memory Map", "EE Map") and the vector units' host.
public sealed partial class Ps2Console : IR5900Bus, IPs2VuHost
{
    private const uint IopRamBase = 0x1C00_0000;
    private const uint BiosBase = 0x1FC0_0000;

    // The RDRAM controller (ps2tek: "EE RDRAM initialization"): the BIOS probes the two RDRAM devices through these.
    private uint _mchRicm;
    private uint _mchDrd;
    private int _rdramDeviceId;

    // EE registers that only hold what was written (the SIO's, the unknown ones in $1000F000–$1000FFFF).
    private readonly uint[] _miscRegisters = new uint[0x1000 / 4];

    byte[] IR5900Bus.Rdram => _rdram;

    byte[]? IR5900Bus.Bios => _bios;

    private void ResetIo()
    {
        _mchRicm = _mchDrd = 0;
        _rdramDeviceId = 0;
        Array.Clear(_miscRegisters);
    }

    // ---- Reads ----

    public byte Read8(uint address)
    {
        if (address < RdramSize)
        {
            return _rdram[address];
        }

        if (TryMemory(address, out var memory, out var offset))
        {
            return memory[offset];
        }

        return (byte)(Read32(address & ~3u) >> (int)((address & 3) * 8));
    }

    public ushort Read16(uint address)
    {
        if (address < RdramSize)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(_rdram.AsSpan((int)address));
        }

        if (TryMemory(address, out var memory, out var offset))
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(memory.AsSpan(offset));
        }

        return (ushort)(Read32(address & ~3u) >> (int)((address & 2) * 8));
    }

    public uint Read32(uint address)
    {
        if (address < RdramSize)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(_rdram.AsSpan((int)address));
        }

        if (TryMemory(address, out var memory, out var offset))
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan(offset));
        }

        return ReadRegister32(address);
    }

    public ulong Read64(uint address)
    {
        if (address < RdramSize)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(_rdram.AsSpan((int)address));
        }

        if (TryMemory(address, out var memory, out var offset))
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(memory.AsSpan(offset));
        }

        if (address - 0x1200_0000 < 0x2000)
        {
            RegisterReadProbe?.Invoke(address);
            return Gs.ReadPrivileged(address - 0x1200_0000);
        }

        if (address - 0x1000_2000 < 0x1000)
        {
            return Ipu.ReadRegister(address - 0x1000_2000);
        }

        return ReadRegister32(address) | ((ulong)ReadRegister32(address + 4) << 32);
    }

    public UInt128 Read128(uint address)
    {
        address &= ~15u;
        if (address < RdramSize)
        {
            return BinaryPrimitives.ReadUInt128LittleEndian(_rdram.AsSpan((int)address));
        }

        if (TryMemory(address, out var memory, out var offset))
        {
            return BinaryPrimitives.ReadUInt128LittleEndian(memory.AsSpan(offset));
        }

        switch (address)
        {
            case 0x1000_5000: // VIF1's FIFO, read for local-to-host transfers
            {
                Span<UInt128> one = stackalloc UInt128[1];
                return Vif1.Read(one) == 1 ? one[0] : 0;
            }

            case 0x1000_7000: // the IPU's output FIFO
                return Ipu.ReadFifo();
        }

        return new UInt128(Read64(address + 8), Read64(address));
    }

    // ---- Writes ----

    public void Write8(uint address, byte value)
    {
        if (address < RdramSize)
        {
            _rdram[address] = value;
            return;
        }

        if (TryWritableMemory(address, out var memory, out var offset))
        {
            memory[offset] = value;
            return;
        }

        if (address == 0x1000_F180)
        {
            DebugOutput?.Invoke((char)value); // KPUTCHAR, the EE's debug UART
            return;
        }

        WriteRegister32(address & ~3u, (uint)value << (int)((address & 3) * 8), 8);
    }

    public void Write16(uint address, ushort value)
    {
        if (address < RdramSize)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_rdram.AsSpan((int)address), value);
            return;
        }

        if (TryWritableMemory(address, out var memory, out var offset))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(memory.AsSpan(offset), value);
            return;
        }

        WriteRegister32(address & ~3u, (uint)value << (int)((address & 2) * 8), 16);
    }

    public void Write32(uint address, uint value)
    {
        if (address < RdramSize)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_rdram.AsSpan((int)address), value);
            return;
        }

        if (TryWritableMemory(address, out var memory, out var offset))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(offset), value);
            return;
        }

        WriteRegister32(address, value, 32);
    }

    public void Write64(uint address, ulong value)
    {
        if (address < RdramSize)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_rdram.AsSpan((int)address), value);
            return;
        }

        if (TryWritableMemory(address, out var memory, out var offset))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(memory.AsSpan(offset), value);
            return;
        }

        if (address - 0x1200_0000 < 0x2000)
        {
            RegisterWriteProbe?.Invoke(address, (uint)value);
            Gs.WritePrivileged(address - 0x1200_0000, value);
            UpdateInterruptLines();
            return;
        }

        if (address - 0x1000_2000 < 0x1000)
        {
            Ipu.WriteRegister(address - 0x1000_2000, value);
            UpdateInterruptLines();
            return;
        }

        WriteRegister32(address, (uint)value, 32);
        WriteRegister32(address + 4, (uint)(value >> 32), 32);
    }

    public void Write128(uint address, UInt128 value)
    {
        address &= ~15u;
        if (address < RdramSize)
        {
            BinaryPrimitives.WriteUInt128LittleEndian(_rdram.AsSpan((int)address), value);
            return;
        }

        if (TryWritableMemory(address, out var memory, out var offset))
        {
            BinaryPrimitives.WriteUInt128LittleEndian(memory.AsSpan(offset), value);
            return;
        }

        // The FIFOs take whole quadwords (ps2tek: "EE Map"); a full FIFO drops the write, as the hardware stalls instead.
        RegisterWriteProbe?.Invoke(address, (uint)value);
        switch (address)
        {
            case 0x1000_4000:
                Vif0.Write([value]);
                UpdateInterruptLines();
                return;
            case 0x1000_5000:
                Vif1.Write([value]);
                UpdateInterruptLines();
                return;
            case 0x1000_6000:
                Gif.WritePath3([value]);
                UpdateInterruptLines();
                return;
            case 0x1000_7010:
                Ipu.WriteFifo(value);
                UpdateInterruptLines();
                return;
        }

        Write64(address, (ulong)value);
        Write64(address + 8, (ulong)(value >> 64));
    }

    /// <summary>
    /// RDRAM's mirrors, the VU memories ($11000000: VU0 micro and data at $0000/$4000, VU1's at $8000/$C000), the IOP's
    /// RAM ($1C000000) and the BIOS ($1FC00000), as an array and offset.
    /// </summary>
    private bool TryMemory(uint address, out byte[] memory, out int offset)
    {
        switch (address >> 24)
        {
            case 0x11:
            {
                var block = (address >> 14) & 3;
                var unit = block < 2 ? Vu0 : Vu1;
                memory = (block & 1) == 0 ? unit.MicroMemory : unit.DataMemory;
                offset = (int)(address & (uint)(memory.Length - 1));
                return true;
            }

            case 0x1C:
                memory = Iop.Ram;
                offset = (int)((address - IopRamBase) & (Ps2Iop.RamSize - 1));
                return true;
            case 0x1F when address >= BiosBase:
                if (_bios is { } bios)
                {
                    memory = bios;
                    offset = (int)((address - BiosBase) & (BiosSize - 1));
                    return true;
                }

                break;
        }

        memory = [];
        offset = 0;
        return false;
    }

    /// <summary>As <see cref="TryMemory"/>, without the BIOS (writes to ROM are ignored).</summary>
    private bool TryWritableMemory(uint address, out byte[] memory, out int offset)
    {
        if (address >= BiosBase && address < BiosBase + BiosSize)
        {
            memory = _miscScratch;
            offset = 0;
            return true;
        }

        return TryMemory(address, out memory, out offset);
    }

    private readonly byte[] _miscScratch = new byte[16];

    // ---- Registers ----

    private uint ReadRegister32(uint address)
    {
        RegisterReadProbe?.Invoke(address);
        switch (address >> 12)
        {
            case 0x1000_0 or 0x1000_1:
                return Timers.Read(address);
            case 0x1000_2:
                return Ipu.ReadRegister32(address - 0x1000_2000);
            case 0x1000_3:
                if (address < 0x1000_3800)
                {
                    return Gif.ReadRegister(address - 0x1000_3000);
                }

                return address < 0x1000_3C00 ? Vif0.ReadRegister(address - 0x1000_3800) : Vif1.ReadRegister(address - 0x1000_3C00);
            case >= 0x1000_8 and <= 0x1000_E:
                return Dmac.ReadRegister(address);
            case 0x1000_F:
                return ReadMiscRegister(address);
            case 0x1200_0 or 0x1200_1:
                return Gs.ReadPrivileged32(address - 0x1200_0000);
            default:
                return 0;
        }
    }

    private uint ReadMiscRegister(uint address)
    {
        switch (address)
        {
            case Ps2Intc.StatAddress:
            case Ps2Intc.MaskAddress:
                return Intc.Read(address);
            case 0x1000_F200:
                return Sif.Mscom;
            case 0x1000_F210:
                return Sif.Smcom;
            case 0x1000_F220:
                return Sif.Msflg;
            case 0x1000_F230:
                return Sif.Smflg;
            case 0x1000_F240:
                return Sif.EeReadCtrl();
            case 0x1000_F260:
                return Sif.Bd6;
            case 0x1000_F130: // SIO_ISR: nothing to receive, and the transmitter is never busy, so KPUTCHAR never waits
            case 0x1000_F410: // the BIOS waits for 0 here after "Initialize memory" (PCSX2's hwRead32 returns 0)
            case 0x1000_F430:
                return 0;
            case 0x1000_F440:
                return ReadRicm();
            case 0x1000_F520:
                return Dmac.ReadRegister(address);
            default:
                return _miscRegisters[(address & 0xFFF) >> 2];
        }
    }

    /// <summary>
    /// MCH_RICM's read-back as the BIOS's RDRAM initialization expects it (ps2tek: "EE RDRAM initialization"): two
    /// RDRAM devices answer the device-ID probe, then the serial reads give their fixed values.
    /// </summary>
    private uint ReadRicm()
    {
        var sop = (_mchRicm >> 6) & 0xF;
        var sa = (_mchRicm >> 16) & 0xFFF;
        if (sop != 0)
        {
            return 0;
        }

        switch (sa)
        {
            case 0x21:
                if (_rdramDeviceId < 2)
                {
                    _rdramDeviceId++;
                    return 0x1F;
                }

                return 0;
            case 0x23:
                return 0x0D0D;
            case 0x24:
                return 0x0090;
            case 0x40:
                return _mchRicm & 0x1F;
            default:
                return 0;
        }
    }

    private void WriteRegister32(uint address, uint value, int width)
    {
        RegisterWriteProbe?.Invoke(address, value);
        switch (address >> 12)
        {
            case 0x1000_0 or 0x1000_1:
                Timers.Write(address, value);
                break;
            case 0x1000_2:
                Ipu.WriteRegister32(address - 0x1000_2000, value);

                break;
            case 0x1000_3:
                if (address < 0x1000_3800)
                {
                    Gif.WriteRegister(address - 0x1000_3000, value);
                }
                else if (address < 0x1000_3C00)
                {
                    Vif0.WriteRegister(address - 0x1000_3800, value);
                }
                else
                {
                    Vif1.WriteRegister(address - 0x1000_3C00, value);
                }

                break;
            case >= 0x1000_8 and <= 0x1000_E:
                Dmac.WriteRegister(address, value);
                Cpu.StopRequested = true; // a transfer may have started: the DMAC runs right away, as it would beside the EE
                break;
            case 0x1000_F:
                WriteMiscRegister(address, value, width);
                break;
            case 0x1200_0 or 0x1200_1:
                Gs.WritePrivileged32(address - 0x1200_0000, value);
                break;
            default:
                return; // unmapped: ignored
        }

        UpdateInterruptLines();
    }

    private void WriteMiscRegister(uint address, uint value, int width)
    {
        switch (address)
        {
            case Ps2Intc.StatAddress:
            case Ps2Intc.MaskAddress:
                Intc.Write(address, value);
                break;
            case 0x1000_F180:
                DebugOutput?.Invoke((char)(byte)value);
                break;
            case 0x1000_F200:
                Sif.Mscom = value;
                break;
            case 0x1000_F210:
                break; // SMCOM is the IOP's
            case 0x1000_F220:
                Sif.EeWriteMsflg(value);
                break;
            case 0x1000_F230:
                Sif.EeWriteSmflg(value);
                break;
            case 0x1000_F240:
                Sif.EeWriteCtrl(value);
                break;
            case 0x1000_F260:
                Sif.Bd6 = value;
                break;
            case 0x1000_F430:
            {
                var sa = (value >> 16) & 0xFFF;
                var sbc = (value >> 6) & 0xF;
                if (sa == 0x21 && sbc == 1 && ((_mchDrd >> 7) & 1) == 0)
                {
                    _rdramDeviceId = 0;
                }

                _mchRicm = value & ~0x8000_0000u;
                break;
            }

            case 0x1000_F440:
                _mchDrd = value;
                break;
            case 0x1000_F590:
                Dmac.WriteRegister(address, value);
                break;
            default:
                if (width == 32)
                {
                    _miscRegisters[(address & 0xFFF) >> 2] = value;
                }
                else
                {
                    _miscRegisters[(address & 0xFFF) >> 2] |= value;
                }

                break;
        }
    }

    // ---- The vector units' host ----

    uint IPs2VuHost.ReadTop(int unit) => Vif1.Top;

    uint IPs2VuHost.ReadItop(int unit) => unit == 0 ? Vif0.Itop : Vif1.Itop;

    void IPs2VuHost.XgKick(uint address) => Gif.KickPath1(Vu1.DataMemory, address);

    void IPs2VuHost.RaiseInterrupt(int unit) => Intc.Raise(unit == 0 ? Ps2Interrupt.Vu0 : Ps2Interrupt.Vu1);

    IPs2VectorUnit IPs2VuHost.Vu1 => Vu1;

    // ---- State of the bus's own registers ----

    private void SaveIo(StateWriter writer)
    {
        writer.WriteUInt32(_mchRicm);
        writer.WriteUInt32(_mchDrd);
        writer.WriteInt32(_rdramDeviceId);
        foreach (var register in _miscRegisters)
        {
            writer.WriteUInt32(register);
        }
    }

    private void LoadIo(StateReader reader)
    {
        _mchRicm = reader.ReadUInt32();
        _mchDrd = reader.ReadUInt32();
        _rdramDeviceId = reader.ReadInt32();
        for (var i = 0; i < _miscRegisters.Length; i++)
        {
            _miscRegisters[i] = reader.ReadUInt32();
        }
    }
}
