using System.Text.Json;
using GameVault.Library.Memory;

namespace GameVault.Library.Challenges;

/// <summary>
/// A challenge (SPEC §7.9): a start state, a goal and an optional time limit, shared as a <c>.gvchallenge</c>
/// file. The goal's named values are copied in, so the file works without the game's memory map.
/// </summary>
public sealed record ChallengeFile
{
    public int Version { get; init; } = 1;

    public required string Id { get; init; }

    /// <summary>The game's headerless SHA-1: the challenge only runs on the same game.</summary>
    public required string RomSha1 { get; init; }

    public string GameTitle { get; init; } = string.Empty;

    public required string Name { get; init; }

    public string? Note { get; init; }

    /// <summary>All must hold at the end of a frame for the challenge to be done.</summary>
    public required IReadOnlyList<MemoryCondition> Goal { get; init; }

    /// <summary>The named values the goal uses (copied from the memory map).</summary>
    public IReadOnlyDictionary<string, MemoryValue> Values { get; init; } = new Dictionary<string, MemoryValue>();

    /// <summary>Frames allowed (60 per second); null for no limit.</summary>
    public int? TimeLimitFrames { get; init; }

    /// <summary>The save state the attempt starts from (base64).</summary>
    public required string StartState { get; init; }

    public string? CreatedBy { get; init; }

    public DateTimeOffset Created { get; init; }

    /// <summary>A memory map holding just the goal's values, for evaluating it.</summary>
    public MemoryMap GoalMap() => new() { Values = Values };

    public bool GoalHolds(Func<int, byte> readRam)
    {
        var map = GoalMap();
        return Goal.All(c => c.Holds(map, readRam));
    }

    /// <summary>"bossHp = 0 and lives ≥ 3".</summary>
    public string DescribeGoal() => string.Join(" and ", Goal.Select(c => c.Describe()));
}

/// <summary>
/// An attempt's result, shared as a <c>.gvresult</c> file: every frame's buttons for players 1 and 2 from
/// the start state until the goal held. Checking it replays the inputs on a fresh console.
/// </summary>
public sealed record ChallengeResult
{
    public int Version { get; init; } = 1;

    public required string ChallengeId { get; init; }

    public required string RomSha1 { get; init; }

    public string Player { get; init; } = string.Empty;

    /// <summary>The frame count at which the goal first held.</summary>
    public required int Frames { get; init; }

    /// <summary>The inputs, run-length encoded (see <see cref="InputRecording"/>), base64.</summary>
    public required string Inputs { get; init; }

    public DateTimeOffset Finished { get; init; }
}

/// <summary>Reads and writes challenge and result files.</summary>
public static class ChallengeFiles
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Save(ChallengeFile challenge) => JsonSerializer.Serialize(challenge, Json);

    public static string Save(ChallengeResult result) => JsonSerializer.Serialize(result, Json);

    public static ChallengeFile LoadChallenge(string json) =>
        JsonSerializer.Deserialize<ChallengeFile>(json, Json) is { Version: 1 } file
            ? file
            : throw new FormatException("This isn't a GameVault challenge, or it's from a newer version.");

    public static ChallengeResult LoadResult(string json) =>
        JsonSerializer.Deserialize<ChallengeResult>(json, Json) is { Version: 1 } result
            ? result
            : throw new FormatException("This isn't a GameVault challenge result, or it's from a newer version.");
}

/// <summary>
/// Per-frame buttons for players 1 and 2, run-length encoded: repeated (count, player 1, player 2) triples,
/// the count as a variable-length number. Held buttons make long runs, so an attempt stays small.
/// </summary>
public static class InputRecording
{
    public static byte[] Encode(IReadOnlyList<(byte Player1, byte Player2)> frames)
    {
        using var stream = new MemoryStream();
        var i = 0;
        while (i < frames.Count)
        {
            var run = 1;
            while (i + run < frames.Count && frames[i + run] == frames[i])
            {
                run++;
            }

            for (var n = (uint)run; ; n >>= 7)
            {
                if (n < 0x80)
                {
                    stream.WriteByte((byte)n);
                    break;
                }

                stream.WriteByte((byte)(n & 0x7F | 0x80));
            }

            stream.WriteByte(frames[i].Player1);
            stream.WriteByte(frames[i].Player2);
            i += run;
        }

        return stream.ToArray();
    }

    public static List<(byte Player1, byte Player2)> Decode(ReadOnlySpan<byte> data)
    {
        var frames = new List<(byte, byte)>();
        var at = 0;
        while (at < data.Length)
        {
            uint run = 0;
            for (var shift = 0; ; shift += 7)
            {
                if (at >= data.Length || shift > 28)
                {
                    throw new FormatException("The recording is damaged.");
                }

                var b = data[at++];
                run |= (uint)(b & 0x7F) << shift;
                if (b < 0x80)
                {
                    break;
                }
            }

            if (at + 2 > data.Length || run == 0 || frames.Count + run > 10_000_000)
            {
                throw new FormatException("The recording is damaged.");
            }

            for (var n = 0; n < run; n++)
            {
                frames.Add((data[at], data[at + 1]));
            }

            at += 2;
        }

        return frames;
    }
}
