using System.IO.Compression;
using System.Text;
using GameVault.Library.Data;
using GameVault.Library.Hashing;

namespace GameVault.Library.Tests.Fixtures;

/// <summary>A temporary folder and in-memory database, with helpers for synthetic (non-game) ROM files.</summary>
public sealed class LibraryFixture : IDisposable
{
    public LibraryFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "GameVaultLibraryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Database = new LibraryDatabase(":memory:");
        Repository = new LibraryRepository(Database);
    }

    public string Root { get; }

    public LibraryDatabase Database { get; }

    public LibraryRepository Repository { get; }

    /// <summary>A tiny iNES image whose PRG is filled with <paramref name="seed"/>; not a real game.</summary>
    public static byte[] Rom(byte seed, bool trainer = false)
    {
        var prg = new byte[0x4000];
        Array.Fill(prg, seed);
        var header = new byte[16];
        "NES\u001A"u8.CopyTo(header);
        header[4] = 1;
        header[6] = (byte)(trainer ? 0x04 : 0);
        return [.. header, .. (trainer ? new byte[512] : []), .. prg];
    }

    public static RomHashes Hashes(byte seed) => RomHasher.Hash(Rom(seed));

    public string WriteRom(string relativePath, byte seed)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Rom(seed));
        return path;
    }

    public string WriteZip(string relativePath, params (string Name, byte Seed)[] entries)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, seed) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Rom(seed));
        }

        return path;
    }

    /// <summary>A Logiqx DAT of fictional games for the given seeds.</summary>
    public static Stream Dat(params (string Name, byte Seed)[] games)
    {
        var xml = new StringBuilder("""<?xml version="1.0"?><datafile><header><name>Test DAT</name></header>""");
        foreach (var (name, seed) in games)
        {
            var h = Hashes(seed);
            xml.Append($"""<game name="{name}"><description>{name}</description><rom name="{name}.nes" size="16384" crc="{h.Crc32.ToUpperInvariant()}" sha1="{h.Sha1.ToUpperInvariant()}"/></game>""");
        }

        xml.Append("</datafile>");
        return new MemoryStream(Encoding.UTF8.GetBytes(xml.ToString()));
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
