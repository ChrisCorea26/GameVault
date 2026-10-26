using System.Security.Cryptography;
using System.Text.Json;

namespace GameVault.Core.Tests.Harness;

/// <summary>
/// Golden-image tests (SPEC §11.2): hash a rendered frame and compare it with the hash committed in
/// <c>Golden/golden.json</c>. On a mismatch, the actual frame is saved as a PNG under
/// <c>TestResults/golden-actual/</c>. Set <c>GAMEVAULT_UPDATE_GOLDEN=1</c> to record new hashes, and review
/// the PNGs before committing them.
/// </summary>
public static class GoldenImages
{
    public const string UpdateVariable = "GAMEVAULT_UPDATE_GOLDEN";

    private static readonly Lock Gate = new();
    private static readonly string GoldenFile =
        Path.Combine(TestRomLocator.RepoRoot, "tests", "GameVault.Core.Tests", "Golden", "golden.json");
    private static readonly string ActualDirectory = Path.Combine(TestRomLocator.RepoRoot, "TestResults", "golden-actual");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static SortedDictionary<string, string>? _hashes;

    private static bool Updating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    public static string Hash(ReadOnlySpan<ushort> frame)
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame);
        return Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
    }

    public static void AssertMatches(
        string name, ReadOnlySpan<ushort> frame, int width = 256, int height = 240, ReadOnlySpan<uint> palette = default)
    {
        var actual = Hash(frame);
        string? expected;
        lock (Gate)
        {
            var hashes = Load();
            hashes.TryGetValue(name, out expected);
            if (Updating && expected != actual)
            {
                hashes[name] = actual;
                File.WriteAllText(GoldenFile, JsonSerializer.Serialize(hashes, JsonOptions) + Environment.NewLine);
                SavePng(name, frame, width, height, palette);
                return;
            }
        }

        if (expected == actual)
        {
            return;
        }

        var png = SavePng(name, frame, width, height, palette);
        Assert.Fail(expected is null
            ? $"No golden hash for '{name}'. Review {png}, then run with {UpdateVariable}=1 to record {actual}."
            : $"Frame for '{name}' changed: expected {expected}, got {actual}. Actual frame saved to {png}.");
    }

    private static SortedDictionary<string, string> Load()
    {
        if (_hashes is null)
        {
            _hashes = File.Exists(GoldenFile)
                ? JsonSerializer.Deserialize<SortedDictionary<string, string>>(File.ReadAllText(GoldenFile)) ?? []
                : [];
        }

        return _hashes;
    }

    private static string SavePng(string name, ReadOnlySpan<ushort> frame, int width, int height, ReadOnlySpan<uint> palette)
    {
        Directory.CreateDirectory(ActualDirectory);
        var path = Path.Combine(ActualDirectory, name.Replace('/', '_') + ".png");
        File.WriteAllBytes(path, PngWriter.Encode(PngWriter.ToArgb(frame, palette), width, height));
        return path;
    }
}
