using System.Buffers.Binary;
using GameVault.Library.Discs;

namespace GameVault.Library.Tests.Discs;

/// <summary>
/// The generated discs and codec inputs the fixtures were made from (Fixtures/make_chd_fixtures.py and
/// make_codec_vectors.py compute the same bytes; the tests compare SHA-256s so the two can't drift).
/// </summary>
internal static class SyntheticDisc
{
    public const int Track1Sectors = 16;
    public const int Track2Frames = 32; // 10 stored pregap + 22
    public const int Track2Pregap = 10;
    public const int Track3Pregap = 5; // PREGAP: not in the file
    public const int Track3Sectors = 8;
    public const int CookedSectors = 20;
    public const int Track3Start = 150 + Track1Sectors + Track2Frames + Track3Pregap;

    public const string MixedCue =
        "FILE \"mixed disc.bin\" BINARY\n" +
        "  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n" +
        "  TRACK 02 AUDIO\n    INDEX 00 00:00:16\n    INDEX 01 00:00:26\n" +
        "  TRACK 03 MODE2/2352\n    PREGAP 00:00:05\n    INDEX 01 00:00:48\n";

    public const string CookedCue = "FILE \"cooked.bin\" BINARY\n  TRACK 01 MODE1/2048\n    INDEX 01 00:00:00\n";

    private sealed class Lcg(int seed)
    {
        private uint _x = (uint)seed;

        public int Next()
        {
            _x = (_x * 1103515245 + 12345) & 0x7FFFFFFF;
            return (int)(_x >> 8);
        }
    }

    public static byte[] UserData(int lba, int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++)
        {
            data[i] = (byte)(lba * 3 + (i >> 6));
        }

        return data;
    }

    /// <summary>A mode 1 sector with valid EDC and ECC.</summary>
    public static byte[] Mode1Raw(int lba)
    {
        var sector = new byte[CdSector.RawSize];
        CdSector.Build(UserData(lba, 2048), lba, mode2: false, sector);
        return sector;
    }

    public static byte[] Mode2Raw(int lba)
    {
        var s = new byte[CdSector.RawSize];
        CdSector.Sync.CopyTo(s);
        CdSector.WriteMsf(s.AsSpan(12), lba);
        s[15] = 2;
        s[18] = s[22] = 8;
        UserData(lba, 2328).CopyTo(s, 24);
        return s;
    }

    public static byte[] Audio(int n, bool silent)
    {
        var data = new byte[CdSector.RawSize];
        if (silent)
        {
            return data;
        }

        for (var k = 0; k < 588; k++)
        {
            var t = n * 588 + k;
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(k * 4), (short)(t * 37 % 2000 - 1000));
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(k * 4 + 2), (short)(700 - t * 23 % 1400));
        }

        return data;
    }

    /// <summary>The mixed disc's .bin: 16 mode 1 sectors, 32 audio frames (the last 16 silent), 8 mode 2 sectors.</summary>
    public static byte[] MixedBin()
    {
        var bin = new List<byte>();
        for (var i = 0; i < Track1Sectors; i++)
        {
            bin.AddRange(Mode1Raw(150 + i));
        }

        for (var n = 0; n < Track2Frames; n++)
        {
            bin.AddRange(Audio(n, silent: n >= 16));
        }

        for (var i = 0; i < Track3Sectors; i++)
        {
            bin.AddRange(Mode2Raw(Track3Start + i));
        }

        return [.. bin];
    }

    public static byte[] CookedBin()
    {
        var bin = new List<byte>();
        for (var i = 0; i < CookedSectors; i++)
        {
            bin.AddRange(UserData(150 + i, 2048));
        }

        return [.. bin];
    }

    /// <summary>The raw sector the mixed disc holds at disc address <paramref name="lba"/>.</summary>
    public static byte[] MixedSector(int lba)
    {
        if (lba is >= 150 and < 150 + Track1Sectors)
        {
            return Mode1Raw(lba);
        }

        var audioStart = 150 + Track1Sectors;
        if (lba >= audioStart && lba < audioStart + Track2Frames)
        {
            var n = lba - audioStart;
            return Audio(n, silent: n >= 16);
        }

        if (lba >= Track3Start && lba < Track3Start + Track3Sectors)
        {
            return Mode2Raw(lba);
        }

        return new byte[CdSector.RawSize];
    }

    // ---- Codec inputs ----

    public static byte[] LzmaInput()
    {
        var pattern = "GameVault reads CHD discs. "u8;
        var rng = new Lcg(1);
        var data = new byte[6000];
        for (var i = 0; i < data.Length; i++)
        {
            var r = rng.Next();
            data[i] = (r & 7) != 0 ? pattern[i % pattern.Length] : (byte)r;
        }

        return data;
    }

    public static byte[] FlacInput(string kind, int samples = 3000)
    {
        var rng = new Lcg(7);
        var data = new byte[samples * 4];
        for (var t = 0; t < samples; t++)
        {
            int left, right;
            switch (kind)
            {
                case "smooth":
                    left = t * 37 % 2000 - 1000 + (rng.Next() & 15);
                    right = 700 - t * 23 % 1400 + (rng.Next() & 7);
                    break;
                case "noise":
                    left = (rng.Next() & 0xFFFF) - 32768;
                    right = (rng.Next() & 0xFFFF) - 32768;
                    break;
                default:
                    left = right = t / 700 * 4096 - 8192;
                    break;
            }

            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(t * 4), (short)left);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(t * 4 + 2), (short)right);
        }

        return data;
    }
}

/// <summary>A temporary folder for disc files, deleted afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gv-discs-" + Guid.NewGuid().ToString("N"));

    public string Write(string name, byte[] data)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    public string Write(string name, string text) => Write(name, System.Text.Encoding.UTF8.GetBytes(text));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
