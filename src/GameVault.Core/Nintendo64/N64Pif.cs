using GameVault.Core.Cartridge;
using GameVault.Core.State;

namespace GameVault.Core.Nintendo64;

/// <summary>An N64 controller's buttons, in the order the joybus sends them (n64brew: "Joybus Controller").</summary>
[Flags]
public enum N64Buttons : ushort
{
    None = 0,
    CRight = 1 << 0,
    CLeft = 1 << 1,
    CDown = 1 << 2,
    CUp = 1 << 3,
    R = 1 << 4,
    L = 1 << 5,
    Right = 1 << 8,
    Left = 1 << 9,
    Down = 1 << 10,
    Up = 1 << 11,
    Start = 1 << 12,
    Z = 1 << 13,
    B = 1 << 14,
    A = 1 << 15,
}

/// <summary>The Controller Pak: 32 KB of battery-backed memory that plugs into a controller.</summary>
public sealed class N64ControllerPak : IBatteryBacked
{
    public const int Size = 32 * 1024;

    public N64ControllerPak()
    {
        Format(Memory);
    }

    public byte[] Memory { get; } = new byte[Size];

    public bool HasBattery => true;

    public Span<byte> BatteryRam => Memory;

    /// <summary>
    /// Writes an empty, formatted file system (n64brew: "Controller Pak"): the ID block and its three copies on page
    /// 0, the index table on pages 1 and 2 with every page free, and an empty note table.
    /// </summary>
    public static void Format(Span<byte> memory)
    {
        memory.Clear();
        Span<byte> id = stackalloc byte[32];
        id.Fill(0xFF);
        id[4] = 0x05;
        id[5] = 0x1A;
        id[6] = 0x5F;
        id[7] = 0x13;
        id[8..16].Clear();
        id[0x18] = 0x00;
        id[0x19] = 0x01; // device id
        id[0x1A] = 0x01; // one bank
        id[0x1B] = 0x00;
        var sum = 0;
        for (var i = 0; i < 28; i += 2)
        {
            sum += (id[i] << 8) | id[i + 1];
        }

        var check = (ushort)sum;
        var inverse = (ushort)(0xFFF2 - check);
        id[0x1C] = (byte)(check >> 8);
        id[0x1D] = (byte)check;
        id[0x1E] = (byte)(inverse >> 8);
        id[0x1F] = (byte)inverse;
        for (var i = 0; i < 32; i++)
        {
            memory[i] = (byte)(0x81 + (i == 0 ? 0 : i - 0x80)); // the label area: 81 01 02 … 1F
        }

        foreach (var at in new[] { 0x20, 0x60, 0x80, 0xC0 })
        {
            id.CopyTo(memory[at..]);
        }

        // The index tables: entries 5–127 free (0x0003), entry 0's second byte the checksum of entries 5–127.
        for (var page = 1; page <= 2; page++)
        {
            var table = memory.Slice(page * 256, 256);
            var checksum = 0;
            for (var entry = 5; entry < 128; entry++)
            {
                table[entry * 2 + 1] = 0x03;
                checksum += 0x03;
            }

            table[1] = (byte)checksum;
        }
    }
}

/// <summary>
/// The PIF (SPEC §7.11, Phase 16): its 64 bytes of RAM, through which the CPU talks to the controllers, the
/// Controller Pak and the cartridge's EEPROM over the joybus; and the CIC-NUS-6105 challenge.
/// </summary>
/// <remarks>
/// The CPU fills PIF RAM with commands (through SI DMA) and sets bit 0 of the last byte; the PIF runs them and
/// leaves the replies in place (n64brew: "PIF-NUS", "Joybus Protocol"). Each command block is a transmit length, a
/// receive length, the transmitted bytes and room for the reply, one block per channel: channels 0–3 are the
/// controller ports and channel 4 the cartridge.
/// </remarks>
public sealed class N64Pif : IStateful
{
    public const int RamSize = 64;

    private static readonly StateTag Tag = StateTag.From("PIF ");

    // CIC-NUS-6105 challenge tables (n64brew: "CIC-NUS-6105").
    private static readonly byte[] ChallengeTable0 = [0x4, 0x7, 0xA, 0x7, 0xE, 0x5, 0xE, 0x1, 0xC, 0xF, 0x8, 0xF, 0x6, 0x3, 0x6, 0x9];
    private static readonly byte[] ChallengeTable1 = [0x4, 0x1, 0xA, 0x7, 0xE, 0x5, 0xE, 0x1, 0xC, 0x9, 0x8, 0x5, 0x6, 0x3, 0xC, 0x9];

    private readonly N64Cartridge? _cartridge;
    private readonly N64Buttons[] _buttons = new N64Buttons[4];
    private readonly sbyte[] _stickX = new sbyte[4];
    private readonly sbyte[] _stickY = new sbyte[4];

    public N64Pif(N64Cartridge? cartridge, N64ControllerPak? pak)
    {
        _cartridge = cartridge;
        Pak = pak;
    }

    public byte[] Ram { get; } = new byte[RamSize];

    /// <summary>The Controller Pak in the first controller, or null for none.</summary>
    public N64ControllerPak? Pak { get; }

    /// <summary>Controllers plugged in (the first <see cref="ConnectedPads"/> ports).</summary>
    public int ConnectedPads { get; set; } = 4;

    public void Reset() => Array.Clear(Ram);

    public void SetPad(int port, N64Buttons buttons, sbyte stickX, sbyte stickY)
    {
        _buttons[port] = buttons;
        _stickX[port] = stickX;
        _stickY[port] = stickY;
    }

    /// <summary>Called after the CPU writes PIF RAM: runs what the control byte asks for.</summary>
    public void Written()
    {
        var control = Ram[63];
        if ((control & 0x01) != 0)
        {
            RunJoybus();
            Ram[63] &= 0xFE;
        }

        if ((control & 0x02) != 0)
        {
            Challenge();
        }

        if ((control & 0x08) != 0)
        {
            Ram[63] &= 0xF7; // the boot code's "done" bit
        }
    }

    // ---- The joybus ----

    private void RunJoybus()
    {
        var ram = Ram;
        var channel = 0;
        var i = 0;
        while (i < 63 && channel < 5)
        {
            var tx = ram[i];
            if (tx == 0xFE)
            {
                break;
            }

            if (tx is 0xFF or 0xFD)
            {
                i++;
                continue;
            }

            if (tx == 0)
            {
                channel++;
                i++;
                continue;
            }

            var rxIndex = i + 1;
            if (rxIndex >= 63 || ram[rxIndex] == 0xFE)
            {
                break;
            }

            var txLength = tx & 0x3F;
            var rxLength = ram[rxIndex] & 0x3F;
            var txStart = rxIndex + 1;
            var rxStart = txStart + txLength;
            if (rxStart + rxLength > 63)
            {
                break;
            }

            var present = channel < 4
                ? RunController(channel, ram.AsSpan(txStart, txLength), ram.AsSpan(rxStart, rxLength))
                : RunCartridge(ram.AsSpan(txStart, txLength), ram.AsSpan(rxStart, rxLength));
            if (!present)
            {
                ram[rxIndex] |= 0x80;
            }

            i = rxStart + rxLength;
            channel++;
        }
    }

    private bool RunController(int port, ReadOnlySpan<byte> tx, Span<byte> rx)
    {
        if (port >= ConnectedPads || tx.Length == 0)
        {
            return false;
        }

        switch (tx[0])
        {
            case 0x00 or 0xFF: // info and reset
                if (rx.Length >= 3)
                {
                    rx[0] = 0x05;
                    rx[1] = 0x00;
                    rx[2] = port == 0 && Pak is not null ? (byte)0x01 : (byte)0x02;
                }

                return true;
            case 0x01: // read buttons and stick
                if (rx.Length >= 4)
                {
                    var buttons = (ushort)_buttons[port];
                    rx[0] = (byte)(buttons >> 8);
                    rx[1] = (byte)buttons;
                    rx[2] = (byte)_stickX[port];
                    rx[3] = (byte)_stickY[port];
                }

                return true;
            case 0x02: // read the pak: a 16-bit address (its low 5 bits a CRC), then 32 bytes and their CRC
                if (tx.Length >= 3 && rx.Length >= 33)
                {
                    var address = ((tx[1] << 8) | tx[2]) & 0xFFE0;
                    var data = rx[..32];
                    if (port == 0 && Pak is not null && address < N64ControllerPak.Size)
                    {
                        Pak.Memory.AsSpan(address, 32).CopyTo(data);
                    }
                    else
                    {
                        data.Clear(); // the accessory area ($8000 up): no rumble pak or transfer pak
                    }

                    rx[32] = port == 0 && Pak is not null ? DataCrc(data) : (byte)(DataCrc(data) ^ 0xFF);
                }

                return true;
            case 0x03: // write the pak
                if (tx.Length >= 35 && rx.Length >= 1)
                {
                    var address = ((tx[1] << 8) | tx[2]) & 0xFFE0;
                    var data = tx.Slice(3, 32);
                    if (port == 0 && Pak is not null && address < N64ControllerPak.Size)
                    {
                        data.CopyTo(Pak.Memory.AsSpan(address));
                    }

                    rx[0] = port == 0 && Pak is not null ? DataCrc(data) : (byte)(DataCrc(data) ^ 0xFF);
                }

                return true;
            default:
                return true;
        }
    }

    /// <summary>The cartridge's joybus device: the EEPROM, 8-byte blocks (n64brew: "EEPROM").</summary>
    private bool RunCartridge(ReadOnlySpan<byte> tx, Span<byte> rx)
    {
        var type = _cartridge?.SaveType ?? N64SaveType.None;
        if (type is not (N64SaveType.Eeprom4K or N64SaveType.Eeprom16K) || tx.Length == 0)
        {
            return false;
        }

        var eeprom = _cartridge!.Save;
        switch (tx[0])
        {
            case 0x00 or 0xFF:
                if (rx.Length >= 3)
                {
                    rx[0] = 0x00;
                    rx[1] = type == N64SaveType.Eeprom16K ? (byte)0xC0 : (byte)0x80;
                    rx[2] = 0x00;
                }

                return true;
            case 0x04: // read a block
                if (tx.Length >= 2 && rx.Length >= 8)
                {
                    var at = tx[1] * 8 % eeprom.Length;
                    eeprom.AsSpan(at, 8).CopyTo(rx);
                }

                return true;
            case 0x05: // write a block
                if (tx.Length >= 10)
                {
                    var at = tx[1] * 8 % eeprom.Length;
                    tx.Slice(2, 8).CopyTo(eeprom.AsSpan(at));
                    if (rx.Length >= 1)
                    {
                        rx[0] = 0x00;
                    }
                }

                return true;
            default:
                return false; // no real-time clock
        }
    }

    /// <summary>The CRC of a pak's 32 data bytes (polynomial $85; n64brew: "Joybus Protocol").</summary>
    public static byte DataCrc(ReadOnlySpan<byte> data)
    {
        var crc = 0;
        for (var i = 0; i <= 32; i++)
        {
            for (var mask = 0x80; mask >= 1; mask >>= 1)
            {
                var tap = (crc & 0x80) != 0 ? 0x85 : 0x00;
                crc = (crc << 1) & 0xFF;
                if (i != 32 && (data[i] & mask) != 0)
                {
                    crc |= 1;
                }

                crc ^= tap;
            }
        }

        return (byte)crc;
    }

    /// <summary>
    /// The CIC-NUS-6105's challenge: the 30 nibbles at $30 are turned into the response the game checks
    /// (n64brew: "CIC-NUS-6105").
    /// </summary>
    private void Challenge()
    {
        Span<byte> challenge = stackalloc byte[30];
        for (var i = 0; i < 15; i++)
        {
            challenge[i * 2] = (byte)(Ram[48 + i] >> 4);
            challenge[i * 2 + 1] = (byte)(Ram[48 + i] & 0x0F);
        }

        Span<byte> response = stackalloc byte[30];
        var key = 0xB;
        var table = ChallengeTable0;
        for (var i = 0; i < 30; i++)
        {
            response[i] = (byte)((key + 5 * challenge[i]) & 0xF);
            key = table[response[i]];
            var sign = (response[i] >> 3) & 1;
            var magnitude = (sign == 1 ? ~response[i] : response[i]) & 7;
            var mod = magnitude % 3 == 1 ? sign : 1 - sign;
            if (table == ChallengeTable1 && response[i] is 0x1 or 0x9)
            {
                mod = 1;
            }

            if (table == ChallengeTable1 && response[i] is 0xB or 0xE)
            {
                mod = 0;
            }

            table = mod == 1 ? ChallengeTable1 : ChallengeTable0;
        }

        Ram[46] = 0;
        Ram[47] = 0;
        for (var i = 0; i < 15; i++)
        {
            Ram[48 + i] = (byte)((response[i * 2] << 4) | response[i * 2 + 1]);
        }

        Ram[63] = 0;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(Ram);
        for (var i = 0; i < 4; i++)
        {
            writer.WriteUInt16((ushort)_buttons[i]);
            writer.WriteByte((byte)_stickX[i]);
            writer.WriteByte((byte)_stickY[i]);
        }

        if (Pak is not null)
        {
            writer.WriteBytes(Pak.Memory);
        }

        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(Ram);
        for (var i = 0; i < 4; i++)
        {
            _buttons[i] = (N64Buttons)reader.ReadUInt16();
            _stickX[i] = (sbyte)reader.ReadByte();
            _stickY[i] = (sbyte)reader.ReadByte();
        }

        if (Pak is not null)
        {
            reader.ReadBytes(Pak.Memory);
        }

        reader.EndChunk();
    }
}
