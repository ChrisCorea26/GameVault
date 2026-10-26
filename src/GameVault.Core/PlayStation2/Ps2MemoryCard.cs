using System.Buffers.Binary;
using System.Text;
using GameVault.Core.Cartridge;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The 8 MB PlayStation 2 memory card (SPEC §7.11, Phase 17): flash memory of 16,384 pages, each 512 data bytes and 16
/// spare bytes that hold the page's ECC, erased in blocks of 16 pages, spoken to through SIO2 with the PS2 card command
/// set (ps2tek: "SIO2 PS2 Memcards"). <see cref="Data"/> is the raw image (8,650,752 bytes, the <c>.ps2</c> format
/// other emulators use).
/// </summary>
/// <remarks>
/// The replies follow the byte layout emulators give the BIOS's MCMAN (two leading bytes for the device and command
/// bytes, then 2Bh and the terminator at the end), and the authentication commands SECRMAN sends (F0h, F3h, F7h) are
/// answered as emulators answer them: F0h's long forms reply with the XOR of the eight bytes they carry, the rest
/// with 2Bh and the terminator.
/// </remarks>
public sealed class Ps2MemoryCard : IBatteryBacked, IStateful
{
    public const int PageSize = 512;
    public const int SpareSize = 16;
    public const int RawPageSize = PageSize + SpareSize;
    public const int PageCount = 16384;
    public const int PagesPerBlock = 16;
    public const int PagesPerCluster = 2;
    public const int Size = RawPageSize * PageCount;

    /// <summary>The terminator byte the card ends replies with after a reset (ps2tek: Probe).</summary>
    public const byte DefaultTerminator = 0x55;

    private static readonly StateTag Tag = StateTag.From("PMCD");

    /// <summary>Each byte's ECC contribution: bit 7 its parity, bits 0–6 the column parities (see <see cref="ComputeEcc"/>).</summary>
    internal static readonly byte[] EccTable = BuildEccTable();

    // Pages whose data area a write sequence touched without writing their spare area: their ECC is computed when
    // the sequence ends.
    private readonly HashSet<int> _pagesNeedingEcc = [];
    private readonly HashSet<int> _pagesWithSpareWritten = [];

    private uint _sector;
    private int _transferAddress;

    public Ps2MemoryCard()
        : this(new byte[Size])
    {
        Data.AsSpan().Fill(0xFF);
    }

    /// <param name="data">A raw card image of <see cref="Size"/> bytes, used in place.</param>
    public Ps2MemoryCard(byte[] data)
    {
        if (data.Length != Size)
        {
            throw new ArgumentException($"A PS2 memory card image is {Size} bytes.", nameof(data));
        }

        Data = data;
    }

    /// <summary>The raw image: pages of 512 data bytes followed by 16 spare (ECC) bytes.</summary>
    public byte[] Data { get; }

    /// <summary>Set when the card is written or erased; the platform clears it after saving the image.</summary>
    public bool Dirty { get; set; }

    /// <summary>The byte ending each reply (MCMAN changes it with command 27h).</summary>
    public byte Terminator { get; set; } = DefaultTerminator;

    public bool HasBattery => true;

    public Span<byte> BatteryRam => Data;

    /// <summary>
    /// A card formatted as the BIOS's MCMAN formats one (ps2tek: "SIO2 PS2 Memcard Filesystem"; the published "PS2
    /// memory card file system" description): the superblock "Sony PS2 Memory Card Format 1.2.0.0" in page 0, 1 KB
    /// clusters of two pages, the indirect FAT cluster at cluster 8, the 32 FAT clusters from 9, the allocatable
    /// clusters from 41 with the root directory ("." and "..") in the first of them, the two backup erase blocks at
    /// the end (1023 and 1022), and every page's ECC.
    /// </summary>
    /// <param name="created">The root directory's creation time; a fixed date by default so the image is deterministic.</param>
    public static Ps2MemoryCard CreateFormatted(DateTime? created = null)
    {
        var card = new Ps2MemoryCard();
        Format(card.Data, created ?? new DateTime(2000, 1, 1));
        card.Dirty = false;
        return card;
    }

    /// <summary>Formats a raw image in place (see <see cref="CreateFormatted"/>).</summary>
    public static void Format(Span<byte> image, DateTime created)
    {
        const int clusterSize = PageSize * PagesPerCluster;
        const int clustersPerCard = PageCount / PagesPerCluster; // 8192
        const int clustersPerBlock = PagesPerBlock / PagesPerCluster; // 8
        const int entriesPerCluster = clusterSize / 4; // 256
        const int fatClusters = (clustersPerCard * 4 - 1) / clusterSize + 1; // 32
        const int indirectCluster = 8;
        const int firstFatCluster = indirectCluster + 1; // 9
        const int allocOffset = firstFatCluster + fatClusters; // 41
        const int backupBlock1 = clustersPerCard / clustersPerBlock - 1; // 1023
        const int backupBlock2 = backupBlock1 - 1; // 1022
        const int allocEnd = backupBlock2 * clustersPerBlock - allocOffset; // 8135

        // Build the card's contents cluster by cluster (512 × 2 bytes of data each), then lay them out as raw pages.
        var clusters = new byte[clustersPerCard * clusterSize];
        clusters.AsSpan().Fill(0xFF);

        // The superblock (mcman's MCDevInfo, as written by its format routine).
        var sb = clusters.AsSpan(0, clusterSize);
        sb[..0x180].Clear();
        Encoding.ASCII.GetBytes("Sony PS2 Memory Card Format 1.2.0.0").CopyTo(sb);
        BinaryPrimitives.WriteUInt16LittleEndian(sb[0x28..], PageSize);
        BinaryPrimitives.WriteUInt16LittleEndian(sb[0x2A..], PagesPerCluster);
        BinaryPrimitives.WriteUInt16LittleEndian(sb[0x2C..], PagesPerBlock);
        BinaryPrimitives.WriteUInt16LittleEndian(sb[0x2E..], 0xFF00);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x30..], clustersPerCard);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x34..], allocOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x38..], allocEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x3C..], 0); // root directory cluster
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x40..], backupBlock1);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x44..], backupBlock2);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x50..], indirectCluster);
        for (var i = 0; i < 32; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(sb[(0xD0 + i * 4)..], -1); // no bad blocks
        }

        sb[0x150] = 2; // a PS2 card
        sb[0x151] = 0x52; // ECC, erased bits read as ones (ps2tek: card flags)

        // The rest of mcman's 384-byte MCDevInfo, which its format routine writes along with the superblock.
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x154..], clusterSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x158..], entriesPerCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x15C..], clustersPerBlock);
        BinaryPrimitives.WriteInt32LittleEndian(sb[0x160..], -1); // cardform while formatting
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x170..], 8001); // max_allocatable_clusters: clusters / 1000 × 1000 + 1
        BinaryPrimitives.WriteInt32LittleEndian(sb[0x17C..], -1);

        // The indirect FAT cluster lists the FAT clusters; the FAT marks the root directory's cluster as the end of
        // its chain and the rest of the allocatable clusters free (0x7FFFFFFF).
        var ifc = clusters.AsSpan(indirectCluster * clusterSize, clusterSize);
        for (var i = 0; i < fatClusters; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(ifc[(i * 4)..], (uint)(firstFatCluster + i));
        }

        for (var entry = 0; entry < allocEnd; entry++)
        {
            var fatCluster = firstFatCluster + entry / entriesPerCluster;
            var offset = fatCluster * clusterSize + entry % entriesPerCluster * 4;
            BinaryPrimitives.WriteUInt32LittleEndian(clusters.AsSpan(offset), entry == 0 ? 0xFFFFFFFF : 0x7FFFFFFF);
        }

        // The root directory: "." (with the directory's length of two entries) and "..".
        var root = clusters.AsSpan(allocOffset * clusterSize, clusterSize);
        root.Clear();
        WriteDirEntry(root[..512], 0x8427, 2, created, ".");
        WriteDirEntry(root[512..], 0xA426, 0, created, "..");

        image[..Size].Fill(0xFF);
        for (var page = 0; page < PageCount; page++)
        {
            var raw = image.Slice(page * RawPageSize, RawPageSize);
            clusters.AsSpan(page * PageSize, PageSize).CopyTo(raw);
            WriteEcc(raw);
        }
    }

    private static void WriteDirEntry(Span<byte> entry, ushort mode, uint length, DateTime time, string name)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(entry, mode);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], length);
        WriteTime(entry[8..], time); // created
        BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], 0); // cluster
        BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], 0); // dir_entry
        WriteTime(entry[24..], time); // modified
        Encoding.ASCII.GetBytes(name).CopyTo(entry[64..]);
    }

    /// <summary>sceMcStDateTime: reserved, seconds, minutes, hours, day, month, then the 16-bit year.</summary>
    private static void WriteTime(Span<byte> destination, DateTime time)
    {
        destination[0] = 0;
        destination[1] = (byte)time.Second;
        destination[2] = (byte)time.Minute;
        destination[3] = (byte)time.Hour;
        destination[4] = (byte)time.Day;
        destination[5] = (byte)time.Month;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)time.Year);
    }

    /// <summary>Writes a raw page's spare area: three ECC bytes for each 128-byte quarter of its data, then four zeros.</summary>
    public static void WriteEcc(Span<byte> rawPage)
    {
        for (var i = 0; i < 4; i++)
        {
            ComputeEcc(rawPage.Slice(i * 128, 128), rawPage.Slice(PageSize + i * 3, 3));
        }

        rawPage.Slice(PageSize + 12, 4).Clear();
    }

    /// <summary>
    /// The card's ECC of 128 data bytes: a column parity byte and two line parity bytes, inverted
    /// (PCSX2's FolderMemoryCard and mymc document the algorithm; the table is derived from its definition here).
    /// </summary>
    public static void ComputeEcc(ReadOnlySpan<byte> data, Span<byte> ecc)
    {
        byte column = 0, line0 = 0, line1 = 0;
        for (var i = 0; i < 128; i++)
        {
            var c = EccTable[data[i]];
            column ^= c;
            if ((c & 0x80) != 0)
            {
                line0 ^= (byte)~i;
                line1 ^= (byte)i;
            }
        }

        ecc[0] = (byte)(~column & 0x77);
        ecc[1] = (byte)(~line0 & 0x7F);
        ecc[2] = (byte)(~line1 & 0x7F);
    }

    private static byte[] BuildEccTable()
    {
        ReadOnlySpan<byte> masks = [0x55, 0x33, 0x0F, 0x00, 0xAA, 0xCC, 0xF0];
        var table = new byte[256];
        for (var b = 0; b < 256; b++)
        {
            var value = Parity(b) << 7;
            for (var i = 0; i < masks.Length; i++)
            {
                value |= Parity(b & masks[i]) << i;
            }

            table[b] = (byte)value;
        }

        return table;

        static int Parity(int v) => System.Numerics.BitOperations.PopCount((uint)v) & 1;
    }

    // ---- The SIO2 protocol ----

    /// <summary>
    /// Runs one SIO2 command: <paramref name="command"/> holds the bytes after the 81h device byte (the command byte
    /// first); the reply, including the two bytes answering the device and command bytes, goes to <paramref name="reply"/>.
    /// </summary>
    internal void Exchange(ReadOnlySpan<byte> command, List<byte> reply)
    {
        var start = reply.Count;
        reply.Add(0x00);
        reply.Add(0x00);
        if (command.IsEmpty)
        {
            return;
        }

        var args = command[1..];
        switch (command[0])
        {
            case 0x11: // Probe
            case 0x12: // end of a write or erase sequence
            case 0x81: // end of a read or write
                if (command[0] != 0x11)
                {
                    FinishWrites();
                }

                TwoBTerminator(reply, start, 4);
                break;
            case 0x21: // set the erase sector
            case 0x22: // set the write sector
            case 0x23: // set the read sector
                _sector = args.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(args) : 0;
                _transferAddress = (int)Math.Min((long)_sector * RawPageSize, Size);
                TwoBTerminator(reply, start, 9);
                break;
            case 0x26: // Get Specs: 512-byte pages, 16-page erase blocks, 16,384 pages, and their XOR
            {
                ReadOnlySpan<byte> specs = [0x00, 0x02, 0x10, 0x00, 0x00, 0x40, 0x00, 0x00];
                reply.Add(0x2B);
                byte xor = 0;
                foreach (var b in specs)
                {
                    reply.Add(b);
                    xor ^= b;
                }

                reply.Add(xor);
                reply.Add(Terminator);
                break;
            }

            case 0x27: // Set Terminator
                Terminator = args.Length > 0 ? args[0] : Terminator;
                reply.Add(0x00);
                reply.Add(0x2B);
                reply.Add(Terminator);
                break;
            case 0x28: // Get Terminator
                reply.Add(0x2B);
                reply.Add(Terminator);
                reply.Add(Terminator);
                break;
            case 0x42: // Write Data
            {
                reply.Add(0x00);
                reply.Add(0x2B);
                var length = args.Length > 0 ? Math.Min((int)args[0], args.Length - 1) : 0;
                byte xor = 0;
                for (var i = 0; i < length; i++)
                {
                    var b = args[1 + i];
                    xor ^= b;
                    WriteByte(b);
                    reply.Add(0x00);
                }

                reply.Add(xor);
                reply.Add(Terminator);
                Dirty |= length > 0;
                break;
            }

            case 0x43: // Read Data
            {
                var length = args.Length > 0 ? args[0] : 0;
                reply.Add(0x00);
                reply.Add(0x2B);
                byte xor = 0;
                for (var i = 0; i < length; i++)
                {
                    var b = _transferAddress < Size ? Data[_transferAddress] : (byte)0xFF;
                    _transferAddress++;
                    xor ^= b;
                    reply.Add(b);
                }

                reply.Add(xor);
                reply.Add(Terminator);
                break;
            }

            case 0x82: // Erase Block: the 16 pages from the erase sector read as ones
            {
                var first = (long)_sector * RawPageSize;
                if (first < Size)
                {
                    var length = (int)Math.Min(RawPageSize * PagesPerBlock, Size - first);
                    Data.AsSpan((int)first, length).Fill(0xFF);
                    Dirty = true;
                }

                TwoBTerminator(reply, start, 4);
                break;
            }

            case 0xBF: // used while detecting the card
            case 0xF7: // SECRMAN
                TwoBTerminator(reply, start, 5);
                break;
            case 0xF3: // SECRMAN: resets the terminator
                Terminator = DefaultTerminator;
                TwoBTerminator(reply, start, 5);
                break;
            case 0xF0: // SECRMAN's XOR authentication
                AuthXor(args, reply, start);
                break;
            default:
                TwoBTerminator(reply, start, command.Length + 1);
                break;
        }
    }

    private void AuthXor(ReadOnlySpan<byte> args, List<byte> reply, int start)
    {
        var mode = args.Length > 0 ? args[0] : (byte)0;
        switch (mode)
        {
            case 0x01 or 0x02 or 0x04 or 0x0F or 0x11 or 0x13:
            {
                // Eight bytes come in; the card answers with their XOR.
                reply.Add(0x00);
                reply.Add(0x2B);
                byte xor = 0;
                for (var i = 0; i < 8; i++)
                {
                    xor ^= args.Length > 1 + i ? args[1 + i] : (byte)0;
                    reply.Add(0x00);
                }

                reply.Add(xor);
                reply.Add(Terminator);
                break;
            }

            case 0x06 or 0x07 or 0x0B:
                TwoBTerminator(reply, start, 14);
                break;
            default:
                TwoBTerminator(reply, start, 5);
                break;
        }
    }

    /// <summary>Pads the reply with zeros to <paramref name="length"/> − 2 bytes, then ends it with 2Bh and the terminator.</summary>
    private void TwoBTerminator(List<byte> reply, int start, int length)
    {
        while (reply.Count - start < length - 2)
        {
            reply.Add(0x00);
        }

        reply.Add(0x2B);
        reply.Add(Terminator);
    }

    private void WriteByte(byte value)
    {
        if (_transferAddress >= Size)
        {
            return;
        }

        var page = _transferAddress / RawPageSize;
        if (_transferAddress % RawPageSize < PageSize)
        {
            _pagesNeedingEcc.Add(page);
        }
        else
        {
            _pagesWithSpareWritten.Add(page);
        }

        Data[_transferAddress++] = value;
    }

    /// <summary>
    /// Ends a write sequence: pages whose data was written without their spare area get their ECC computed, as the
    /// card's ECC must always match its data for MCMAN to accept it.
    /// </summary>
    private void FinishWrites()
    {
        foreach (var page in _pagesNeedingEcc)
        {
            if (!_pagesWithSpareWritten.Contains(page))
            {
                WriteEcc(Data.AsSpan(page * RawPageSize, RawPageSize));
            }
        }

        _pagesNeedingEcc.Clear();
        _pagesWithSpareWritten.Clear();
    }

    /// <summary>Resets the protocol state (the terminator and the sector); the contents stay.</summary>
    public void Reset()
    {
        FinishWrites();
        Terminator = DefaultTerminator;
        _sector = 0;
        _transferAddress = 0;
    }

    public void SaveState(StateWriter writer)
    {
        FinishWrites();
        writer.BeginChunk(Tag, 1);
        writer.WriteByte(Terminator);
        writer.WriteUInt32(_sector);
        writer.WriteInt32(_transferAddress);
        writer.WriteBytes(Data);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        if (reader.BeginChunk(Tag) != 1)
        {
            throw new StateFormatException("Unsupported PS2 memory card state version.");
        }

        _pagesNeedingEcc.Clear();
        _pagesWithSpareWritten.Clear();
        Terminator = reader.ReadByte();
        _sector = reader.ReadUInt32();
        _transferAddress = Math.Clamp(reader.ReadInt32(), 0, Size);
        reader.ReadBytes(Data);
        reader.EndChunk();
    }
}
