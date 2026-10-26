using GameVault.Core.Apple;
using GameVault.Platform.Storage;

namespace GameVault.Platform.Emulation;

/// <summary>
/// Apple II drives 1 and 2 (Phase 12): the Player's disk menu puts the player's own disk images in them, and
/// writes are saved as "Name - hash.a2save" patches (one per disk image), never into the image file.
/// </summary>
public sealed partial class EmulatorHost
{
    private readonly AppleDriveSlot?[] _appleDrives = new AppleDriveSlot?[2];
    private volatile string?[] _appleDriveNames = [null, null];

    /// <summary>The disk image in each Apple II drive (its file name), or null for an empty drive.</summary>
    public IReadOnlyList<string?> AppleDriveNames => _appleDriveNames;

    /// <summary>Reads a disk image and puts it in drive <paramref name="drive"/> (0 or 1) of the running Apple II.</summary>
    public async Task InsertAppleDiskAsync(int drive, string path, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(drive is 0 or 1, true, nameof(drive));
        byte[] data;
        try
        {
            data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RaiseNotice("GameVault couldn't open that disk image. " + ex.Message);
            return;
        }

        AppleDriveSlot slot;
        try
        {
            slot = OpenAppleDisk(data, Path.GetExtension(path), Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex) when (ex is GameVault.Core.Cartridge.RomFormatException or InvalidDataException)
        {
            RaiseNotice("That file isn't an Apple II disk image GameVault can read. " + ex.Message);
            return;
        }

        Post(() =>
        {
            if (_console is not AppleConsole apple)
            {
                return;
            }

            FlushAppleDisk(drive, background: true);
            apple.InsertDisk(drive, slot.Disk);
            _appleDrives[drive] = slot;
            PublishAppleDrives();
            RaiseNotice($"Drive {drive + 1}: {slot.Name}.");
        });
    }

    public void EjectAppleDisk(int drive) => Post(() =>
    {
        if (_console is AppleConsole apple && drive is 0 or 1 && _appleDrives[drive] is not null)
        {
            FlushAppleDisk(drive, background: true);
            apple.InsertDisk(drive, null);
            _appleDrives[drive] = null;
            PublishAppleDrives();
        }
    });

    /// <summary>A disk image with any saved writes put back on it.</summary>
    private AppleDriveSlot OpenAppleDisk(byte[] data, string extension, string name)
    {
        var disk = AppleDisk.Open(data, extension.Length > 0 ? extension : ".dsk");
        return WithSavedWrites(disk, AppleConsole.HashDisk(data), name);
    }

    private AppleDriveSlot WithSavedWrites(AppleDisk disk, ulong hash, string name)
    {
        var original = disk.SnapshotTracks();
        try
        {
            if (_appleDiskSaves?.TryRead(hash) is { } patch)
            {
                disk.ApplyPatch(patch);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            RaiseNotice($"GameVault couldn't read the saved progress on {name}, so the disk starts without it.");
        }

        return new AppleDriveSlot(disk, hash, name, original) { Saved = disk.SnapshotTracks() };
    }

    /// <summary>Called on the emulation thread when an Apple II game starts: drive 1 holds its disk, drive 2 is empty.</summary>
    private void StartAppleDrives(AppleDriveSlot? boot)
    {
        _appleDrives[0] = boot;
        _appleDrives[1] = null;
        PublishAppleDrives();
    }

    private void PublishAppleDrives()
    {
        _appleDriveNames = [_appleDrives[0]?.Name, _appleDrives[1]?.Name];
        Raise(() => DiskChanged?.Invoke(this, EventArgs.Empty));
    }

    private void FlushAppleDisks(bool background)
    {
        FlushAppleDisk(0, background);
        FlushAppleDisk(1, background);
    }

    /// <summary>Saves the drive's disk as a patch when the computer wrote to it. Runs on the emulation thread.</summary>
    private void FlushAppleDisk(int drive, bool background)
    {
        if (_appleDrives[drive] is not { Disk.IsModified: true } slot || _appleDiskSaves is null)
        {
            return;
        }

        var changed = false;
        for (var i = 0; i < slot.Disk.TrackData.Count && !changed; i++)
        {
            changed = !slot.Disk.TrackData[i].Bits.AsSpan().SequenceEqual(slot.Saved[i]);
        }

        if (!changed)
        {
            return;
        }

        slot.Saved = slot.Disk.SnapshotTracks();
        var patch = slot.Disk.CreatePatch(slot.Original);
        var store = _appleDiskSaves;

        void Write()
        {
            try
            {
                store.Save(slot.Name, slot.Hash, patch);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RaiseNotice($"GameVault couldn't save the progress on {slot.Name}. " + ex.Message);
            }
        }

        if (background)
        {
            _ = Task.Run(Write);
        }
        else
        {
            Write();
        }
    }

    private sealed record AppleDriveSlot(AppleDisk Disk, ulong Hash, string Name, byte[][] Original)
    {
        /// <summary>The tracks as last saved.</summary>
        public required byte[][] Saved { get; set; }
    }
}
