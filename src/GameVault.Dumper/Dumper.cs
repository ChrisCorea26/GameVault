using System.Diagnostics;
using GameVault.Library.Hashing;

namespace GameVault.Dumper;

/// <summary>What to dump: the board (iNES mapper number) and its ROM sizes.</summary>
public sealed record DumpRequest(int Mapper, int PrgKilobytes, int ChrKilobytes, string OutputPath, string? ExpectedSha1 = null);

public enum DumpStatus
{
    /// <summary>The dump matches the catalog's hash.</summary>
    Verified,

    /// <summary>Two reads agreed, but the catalog doesn't know this hash (a revision or homebrew, or no catalog).</summary>
    Consistent,

    /// <summary>Reads kept differing or never matched the catalog: dirty contacts, a loose cartridge.</summary>
    BadRead,

    /// <summary>The dumper isn't installed, isn't connected, or doesn't support the board.</summary>
    Failed,
}

public sealed record DumpResult(DumpStatus Status, string Message, int Attempts, RomHashes? Hashes = null, string? Path = null)
{
    public bool Succeeded => Status is DumpStatus.Verified or DumpStatus.Consistent;
}

/// <summary>A cartridge dumper (SPEC §8).</summary>
public interface IDumper
{
    string Name { get; }

    /// <summary>The boards the dumper's scripts can read, by iNES mapper number.</summary>
    IReadOnlyCollection<int> SupportedMappers { get; }

    Task<DumpResult> DumpAsync(DumpRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>Runs a command-line tool. Faked in tests.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await output + await error);
    }
}

/// <summary>
/// INL Retro-Prog (SPEC §8: supported first). Shells out to its open-source command-line tool,
/// <c>inlretro</c>, with the board script for the mapper, then reads again until two dumps agree (or one
/// matches the catalog), up to <see cref="MaxAttempts"/> times.
/// </summary>
public sealed class InlRetroDumper(IProcessRunner runner, string executablePath) : IDumper
{
    public const int MaxAttempts = 3;

    /// <summary>iNES mapper → the name the INL Retro-Prog NES script uses for that board.</summary>
    public static readonly IReadOnlyDictionary<int, string> BoardNames = new Dictionary<int, string>
    {
        [0] = "nrom",
        [1] = "mmc1",
        [2] = "unrom",
        [3] = "cnrom",
        [4] = "mmc3",
        [7] = "anrom",
        [9] = "mmc2",
        [10] = "mmc4",
        [11] = "cdream",
        [34] = "bnrom",
        [66] = "gxrom",
        [69] = "fme7",
    };

    public string Name => "INL Retro-Prog";

    public IReadOnlyCollection<int> SupportedMappers => (IReadOnlyCollection<int>)BoardNames.Keys;

    /// <summary>The command line for one read (exposed for tests and troubleshooting).</summary>
    public static IReadOnlyList<string> Arguments(DumpRequest request, string outputPath) =>
    [
        "-s", Path.Combine("scripts", "inlretro2.lua"),
        "-c", "NES",
        "-m", BoardNames[request.Mapper],
        "-x", request.PrgKilobytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-y", request.ChrKilobytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-d", outputPath,
    ];

    public async Task<DumpResult> DumpAsync(DumpRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executablePath))
        {
            return new DumpResult(DumpStatus.Failed, "GameVault can't find the INL Retro-Prog software. Install it and choose inlretro.exe in Settings.", 0);
        }

        if (!BoardNames.ContainsKey(request.Mapper))
        {
            return new DumpResult(DumpStatus.Failed, "The INL Retro-Prog can't read this kind of cartridge yet.", 0);
        }

        var workingDirectory = Path.GetDirectoryName(executablePath)!;
        var temp = Path.Combine(Path.GetTempPath(), "GameVaultDump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            RomHashes? previous = null;
            string? previousPath = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                progress?.Report(attempt == 1 ? "Reading the cartridge…" : $"Reading again to check (try {attempt} of {MaxAttempts})…");
                var path = Path.Combine(temp, $"read{attempt}.nes");
                var result = await runner.RunAsync(executablePath, Arguments(request, path), workingDirectory, cancellationToken);
                if (DeviceProblem(result) is { } problem)
                {
                    return new DumpResult(DumpStatus.Failed, problem, attempt);
                }

                if (result.ExitCode != 0 || !File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    previous = null;
                    continue; // a failed read counts as a bad read; try again
                }

                var hashes = RomHasher.Hash(await File.ReadAllBytesAsync(path, cancellationToken));
                if (request.ExpectedSha1 is { } expected && string.Equals(expected, hashes.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    return Finish(DumpStatus.Verified, "The dump matches the catalog.", attempt, hashes, path, request.OutputPath);
                }

                if (previous is { } earlier && earlier == hashes && request.ExpectedSha1 is null)
                {
                    return Finish(DumpStatus.Consistent, "Two reads matched.", attempt, hashes, previousPath!, request.OutputPath);
                }

                if (previous is { } same && same == hashes)
                {
                    // Two identical reads that don't match the catalog: a different revision, most likely.
                    return Finish(DumpStatus.Consistent, "Two reads matched, but not the catalog entry you picked. It may be a different revision.", attempt, hashes, path, request.OutputPath);
                }

                previous = hashes;
                previousPath = path;
            }

            return new DumpResult(DumpStatus.BadRead, "The reads didn't match. Clean the cartridge contacts and try again.", MaxAttempts);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static DumpResult Finish(DumpStatus status, string message, int attempts, RomHashes hashes, string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
        File.Copy(from, to, overwrite: true);
        return new DumpResult(status, message, attempts, hashes, to);
    }

    /// <summary>Problems another read can't fix, in plain language.</summary>
    private static string? DeviceProblem(ProcessResult result)
    {
        var output = result.Output.ToLowerInvariant();
        if (output.Contains("could not find") || output.Contains("not found") && output.Contains("device") || output.Contains("unable to open usb"))
        {
            return "GameVault can't find the INL Retro-Prog. Check that it's plugged in, then try again.";
        }

        if (output.Contains("access denied") || output.Contains("libusb_error_access"))
        {
            return "Windows didn't let GameVault use the dumper. Install the INL Retro-Prog USB driver, then try again.";
        }

        return null;
    }
}
