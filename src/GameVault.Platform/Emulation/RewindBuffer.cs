using System.IO.Compression;
using GameVault.Core;
using GameVault.Core.State;

namespace GameVault.Platform.Emulation;

/// <summary>
/// Rewind history (SPEC §7.2): a snapshot every <see cref="Interval"/> frames, compressed with Brotli. Every
/// <see cref="KeyframeEvery"/>th entry is stored whole; the ones between are XORed with their keyframe first,
/// which leaves mostly zeros and compresses to a few hundred bytes. The oldest group (keyframe plus its
/// deltas) is dropped when the memory cap is reached. Owned by the emulation thread.
/// </summary>
public sealed class RewindBuffer(long capacityBytes = RewindBuffer.DefaultCapacity)
{
    public const long DefaultCapacity = 256L * 1024 * 1024;
    public const int Interval = 2;
    public const int KeyframeEvery = 60;

    private readonly List<Entry> _entries = [];
    private readonly StateWriter _writer = new();
    private byte[] _keyframe = [];
    private int _keyframeIndex = -1;
    private byte[] _scratch = [];
    private byte[] _compressBuffer = [];
    private long _frames;

    public long CapacityBytes { get; set; } = capacityBytes;

    public int Count => _entries.Count;

    public long BytesUsed { get; private set; }

    /// <summary>How far back the history reaches.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(_entries.Count * Interval / EmulatorHost.NtscFrameRate);

    public void Clear()
    {
        _entries.Clear();
        BytesUsed = 0;
        _keyframeIndex = -1;
        _frames = 0;
    }

    /// <summary>Call once after every emulated frame; captures every <see cref="Interval"/> frames.</summary>
    public void OnFrame(NesConsole console)
    {
        if (_frames++ % Interval != 0)
        {
            return;
        }

        _writer.Reset();
        console.SaveSnapshot(_writer);
        var raw = _writer.WrittenSpan;

        var isKey = _entries.Count == 0 || _entries.Count - LastKeyIndex() >= KeyframeEvery || raw.Length != _keyframe.Length;
        ReadOnlySpan<byte> payload;
        if (isKey)
        {
            if (_keyframe.Length != raw.Length)
            {
                _keyframe = new byte[raw.Length];
            }

            raw.CopyTo(_keyframe);
            _keyframeIndex = _entries.Count;
            payload = raw;
        }
        else
        {
            EnsureSize(ref _scratch, raw.Length);
            for (var i = 0; i < raw.Length; i++)
            {
                _scratch[i] = (byte)(raw[i] ^ _keyframe[i]);
            }

            payload = _scratch.AsSpan(0, raw.Length);
        }

        EnsureSize(ref _compressBuffer, BrotliEncoder.GetMaxCompressedLength(payload.Length));
        if (!BrotliEncoder.TryCompress(payload, _compressBuffer, out var written, quality: 1, window: 22))
        {
            return; // can't happen with a buffer of the maximum size; skip this capture if it does
        }

        var data = _compressBuffer.AsSpan(0, written).ToArray();
        _entries.Add(new Entry(data, isKey, raw.Length));
        BytesUsed += data.Length;
        Evict();
    }

    /// <summary>
    /// Restores the newest entry and removes it, so holding rewind walks back in time. Returns false when the
    /// history is empty.
    /// </summary>
    public bool StepBack(NesConsole console)
    {
        if (_entries.Count == 0)
        {
            return false;
        }

        var index = _entries.Count - 1;
        var entry = _entries[index];
        var raw = Decode(index);
        _entries.RemoveAt(index);
        BytesUsed -= entry.Data.Length;
        if (entry.IsKey)
        {
            _keyframeIndex = -1; // the next older keyframe is found again when needed
        }

        console.LoadSnapshot(raw);
        _frames = 1; // the next capture happens Interval frames after this point
        return true;
    }

    private byte[] Decode(int index)
    {
        var entry = _entries[index];
        var raw = new byte[entry.Length];
        if (!BrotliDecoder.TryDecompress(entry.Data, raw, out _))
        {
            throw new InvalidDataException("Rewind history is damaged.");
        }

        if (entry.IsKey)
        {
            return raw;
        }

        var keyIndex = index;
        while (!_entries[keyIndex].IsKey)
        {
            keyIndex--;
        }

        var key = new byte[_entries[keyIndex].Length];
        BrotliDecoder.TryDecompress(_entries[keyIndex].Data, key, out _);
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] ^= key[i];
        }

        // Later captures delta against this group's keyframe again.
        _keyframe = key;
        _keyframeIndex = keyIndex;
        return raw;
    }

    private int LastKeyIndex()
    {
        if (_keyframeIndex >= 0 && _keyframeIndex < _entries.Count && _entries[_keyframeIndex].IsKey)
        {
            return _keyframeIndex;
        }

        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].IsKey)
            {
                _keyframeIndex = i;
                var key = new byte[_entries[i].Length];
                BrotliDecoder.TryDecompress(_entries[i].Data, key, out _);
                _keyframe = key;
                return i;
            }
        }

        return int.MinValue / 2; // none: forces a keyframe
    }

    private void Evict()
    {
        while (BytesUsed > CapacityBytes && _entries.Count > 1)
        {
            // Drop the oldest keyframe together with the deltas that depend on it.
            var end = 1;
            while (end < _entries.Count && !_entries[end].IsKey)
            {
                end++;
            }

            if (end >= _entries.Count)
            {
                break; // only one group left: keep it
            }

            for (var i = 0; i < end; i++)
            {
                BytesUsed -= _entries[i].Data.Length;
            }

            _entries.RemoveRange(0, end);
            _keyframeIndex -= end;
        }
    }

    private static void EnsureSize(ref byte[] buffer, int size)
    {
        if (buffer.Length < size)
        {
            buffer = new byte[size];
        }
    }

    private readonly record struct Entry(byte[] Data, bool IsKey, int Length);
}
