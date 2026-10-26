using System.Text;
using GameVault.Core.PlayStation;

namespace GameVault.Core.Tests.PlayStation;

/// <summary>
/// Builds small PlayStation discs for tests: an ISO-9660 volume of mode 2 form 1 sectors with the licence text in
/// sector 4, SYSTEM.CNF and the given files in the root directory, optionally followed by an audio track.
/// </summary>
internal static class TestDisc
{
    public const string BootName = "SLUS_000.01";

    public static MemoryCdImage Create(byte[] exe, string licence = "Licensed by Sony Computer Entertainment Amer  ica", int audioSectors = 0, Dictionary<string, byte[]>? extraFiles = null)
    {
        var files = new List<(string Name, byte[] Data)>
        {
            ("SYSTEM.CNF", Encoding.ASCII.GetBytes($"BOOT = cdrom:\\{BootName};1\r\nTCB = 4\r\nEVENT = 10\r\nSTACK = 801FFFF0\r\n")),
            (BootName, exe),
        };
        if (extraFiles is not null)
        {
            files.AddRange(extraFiles.Select(f => (f.Key, f.Value)));
        }

        // Layout (sectors of the data track): 0–15 system area (licence in 4), 16 PVD, 17 terminator, 18 root
        // directory, then the files.
        var data = new List<byte[]>();
        for (var i = 0; i < 16; i++)
        {
            data.Add(new byte[2048]);
        }

        Encoding.ASCII.GetBytes(licence).CopyTo(data[4], 0);

        var pvd = new byte[2048];
        var terminator = new byte[2048];
        var root = new byte[2048];
        data.Add(pvd);
        data.Add(terminator);
        data.Add(root);

        var next = 19;
        var entries = new List<byte[]> { DirectoryRecord("\0", 18, 2048, directory: true), DirectoryRecord("\u0001", 18, 2048, directory: true) };
        foreach (var (name, content) in files)
        {
            entries.Add(DirectoryRecord(name + ";1", next, content.Length, directory: false));
            for (var offset = 0; offset < Math.Max(1, content.Length); offset += 2048)
            {
                var sector = new byte[2048];
                content.AsSpan(offset, Math.Min(2048, content.Length - offset)).CopyTo(sector);
                data.Add(sector);
                next++;
            }
        }

        var position = 0;
        foreach (var entry in entries)
        {
            entry.CopyTo(root, position);
            position += entry.Length;
        }

        pvd[0] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(pvd, 1);
        pvd[6] = 1;
        Encoding.ASCII.GetBytes("PLAYSTATION                     ").CopyTo(pvd, 8);
        DirectoryRecord("\0", 18, 2048, directory: true).CopyTo(pvd, 156);
        terminator[0] = 0xFF;
        Encoding.ASCII.GetBytes("CD001").CopyTo(terminator, 1);

        var raw = new byte[(data.Count + audioSectors) * ICdImage.SectorSize];
        for (var i = 0; i < data.Count; i++)
        {
            WriteMode2Sector(raw.AsSpan(i * ICdImage.SectorSize, ICdImage.SectorSize), 150 + i, data[i]);
        }

        // An audio track: a square wave, left at +4000 and right at −4000 for the first half of each sector.
        for (var s = 0; s < audioSectors; s++)
        {
            var sector = raw.AsSpan((data.Count + s) * ICdImage.SectorSize, ICdImage.SectorSize);
            for (var i = 0; i < 588; i++)
            {
                short left = i < 294 ? (short)4000 : (short)-4000;
                BitConverter.TryWriteBytes(sector[(i * 4)..], left);
                BitConverter.TryWriteBytes(sector[(i * 4 + 2)..], (short)-left);
            }
        }

        if (audioSectors == 0)
        {
            return new MemoryCdImage(raw);
        }

        return new MemoryCdImage(raw,
        [
            new CdTrack(1, CdTrackType.Mode2, 150, data.Count, 150),
            new CdTrack(2, CdTrackType.Audio, 150 + data.Count, audioSectors),
        ]);
    }

    private static byte[] DirectoryRecord(string name, int lba, int size, bool directory)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var length = 33 + nameBytes.Length + (nameBytes.Length % 2 == 0 ? 1 : 0);
        var record = new byte[length];
        record[0] = (byte)length;
        BitConverter.GetBytes(lba).CopyTo(record, 2);
        BitConverter.GetBytes(size).CopyTo(record, 10);
        record[25] = directory ? (byte)2 : (byte)0;
        record[32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(record, 33);
        return record;
    }

    /// <summary>A raw mode 2 form 1 sector: sync, BCD address, mode 2, the subheader twice, then 2,048 bytes of data.</summary>
    public static void WriteMode2Sector(Span<byte> sector, int lba, ReadOnlySpan<byte> data, byte submode = 0x08, byte file = 0, byte channel = 0, byte coding = 0)
    {
        sector.Clear();
        sector[0] = 0x00;
        sector[1..11].Fill(0xFF);
        sector[11] = 0x00;
        sector[12] = Bcd(lba / 4500);
        sector[13] = Bcd(lba / 75 % 60);
        sector[14] = Bcd(lba % 75);
        sector[15] = 2;
        sector[16] = sector[20] = file;
        sector[17] = sector[21] = channel;
        sector[18] = sector[22] = submode;
        sector[19] = sector[23] = coding;
        data[..Math.Min(data.Length, 2324)].CopyTo(sector[24..]);
    }

    private static byte Bcd(int value) => (byte)(value / 10 << 4 | value % 10);
}
