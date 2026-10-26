using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace GameVault.Platform.Achievements;

/// <summary>Encrypts the RetroAchievements token at rest.</summary>
public interface ITokenProtector
{
    byte[] Protect(byte[] data);

    /// <summary>Null when the data can't be decrypted (another Windows user, or damaged).</summary>
    byte[]? Unprotect(byte[] data);
}

/// <summary>
/// Windows DPAPI for the current user (<c>CryptProtectData</c>): only this Windows account on this PC can
/// read the token back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class DpapiTokenProtector : ITokenProtector
{
    private const int UiForbidden = 0x1;
    private static readonly byte[] Entropy = "GameVault RetroAchievements"u8.ToArray();

    public byte[] Protect(byte[] data) => Crypt(data, protect: true) ?? throw new InvalidOperationException("Windows couldn't encrypt the sign-in.");

    public byte[]? Unprotect(byte[] data) => Crypt(data, protect: false);

    private static unsafe byte[]? Crypt(byte[] data, bool protect)
    {
        fixed (byte* input = data)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new DataBlob { Size = data.Length, Data = input };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropy };
            var ok = protect
                ? CryptProtectData(&inBlob, null, &entropyBlob, 0, 0, UiForbidden, out var outBlob)
                : CryptUnprotectData(&inBlob, 0, &entropyBlob, 0, 0, UiForbidden, out outBlob);
            if (!ok)
            {
                return null;
            }

            try
            {
                return new ReadOnlySpan<byte>(outBlob.Data, outBlob.Size).ToArray();
            }
            finally
            {
                LocalFree((nint)outBlob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DataBlob
    {
        public int Size;
        public byte* Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptProtectData(
        DataBlob* dataIn, char* description, DataBlob* entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptUnprotectData(
        DataBlob* dataIn, nint description, DataBlob* entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}

/// <summary>
/// <c>retroachievements.json</c> in the profile's folder: the username and the encrypted login token. The
/// password is never stored.
/// </summary>
public sealed class AchievementsCredentials(string path, ITokenProtector protector)
{
    public (string UserName, string Token)? Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var file = JsonSerializer.Deserialize<CredentialsFile>(File.ReadAllText(path), JsonSerializerOptions.Web);
            if (file is null || string.IsNullOrEmpty(file.UserName) || string.IsNullOrEmpty(file.Token))
            {
                return null;
            }

            return protector.Unprotect(Convert.FromBase64String(file.Token)) is { } token
                ? (file.UserName, Encoding.UTF8.GetString(token))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string userName, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new CredentialsFile(userName, Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(token))));
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonSerializerOptions.Web));
    }

    public void Delete()
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Signing out still works for this session; the file is retried next time.
        }
    }

    private sealed record CredentialsFile(string UserName, string Token);
}
