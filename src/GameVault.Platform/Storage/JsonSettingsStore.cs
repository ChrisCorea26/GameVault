using System.Text.Json;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Storage;

/// <summary>Reads and writes <see cref="AppSettings"/> as JSON at <see cref="AppPaths.SettingsFile"/>.</summary>
public sealed class JsonSettingsStore(AppPaths paths) : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(paths.SettingsFile);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
                ?? new AppSettings();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // First launch, or a corrupt file: fall back to defaults (zero-config, SPEC §9.3).
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(paths.Root);

            // Write to a temporary file, then swap it in, so a crash never leaves a half-written file.
            var tempFile = paths.SettingsFile + ".tmp";
            await using (var stream = File.Create(tempFile))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
            }

            File.Move(tempFile, paths.SettingsFile, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
