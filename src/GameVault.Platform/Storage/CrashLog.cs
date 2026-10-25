namespace GameVault.Platform.Storage;

/// <summary>Appends unhandled exceptions to <see cref="AppPaths.CrashLogFile"/> so startup failures can be diagnosed.</summary>
public static class CrashLog
{
    public static void Write(AppPaths paths, Exception exception, string source)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.CrashLogFile)!);
            File.AppendAllText(
                paths.CrashLogFile,
                $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never turn a crash into a different crash.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
