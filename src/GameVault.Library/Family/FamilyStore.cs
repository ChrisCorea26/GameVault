using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameVault.Library.Family;

/// <summary>One person's profile (SPEC §7.9): their own library, saves and settings, and an optional daily limit.</summary>
public sealed record FamilyProfile
{
    /// <summary>The first profile is "main": it uses GameVault's own folder, so nothing moves.</summary>
    public const string MainId = "main";

    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>A Segoe Fluent Icons glyph, for example "" (contact).</summary>
    public string Icon { get; init; } = "";

    /// <summary>Minutes of play a day; null for no limit.</summary>
    public int? DailyLimitMinutes { get; init; }

    /// <summary>False for children: Settings need the parent PIN.</summary>
    public bool CanChangeSettings { get; init; } = true;
}

/// <summary>Today's play for one profile, and whether a parent let them keep going.</summary>
public sealed record DailyPlay
{
    public DateOnly Date { get; init; }

    public double Seconds { get; init; }

    /// <summary>A parent entered the PIN after the limit: no more stops today.</summary>
    public bool LimitLifted { get; init; }
}

/// <summary>
/// <c>profiles.json</c> in GameVault's root folder: the profiles, the parent PIN (a salted PBKDF2 hash, never
/// the PIN itself) and each profile's play today. Every method reads and writes the file, so the store can be
/// shared; it is not thread-safe.
/// </summary>
public sealed class FamilyStore(string root)
{
    private const int PinIterations = 100_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Root { get; } = root;

    public string FilePath => Path.Combine(Root, "profiles.json");

    /// <summary>The profiles; always at least the main one.</summary>
    public IReadOnlyList<FamilyProfile> Profiles => Read().Profiles;

    public bool HasPin => Read().Pin is not null;

    /// <summary>Where a profile keeps its data: the root for "main", <c>Profiles/&lt;id&gt;</c> for the rest.</summary>
    public string DataRootFor(string profileId) =>
        profileId == FamilyProfile.MainId ? Root : Path.Combine(Root, "Profiles", profileId);

    public FamilyProfile? Find(string profileId) => Profiles.FirstOrDefault(p => p.Id == profileId);

    /// <summary>Adds or replaces a profile (matched by id).</summary>
    public void Save(FamilyProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);
        var file = Read();
        var list = file.Profiles.ToList();
        var index = list.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            list[index] = profile;
        }
        else
        {
            list.Add(profile);
        }

        Write(file with { Profiles = list });
    }

    /// <summary>A new profile with a fresh id (its folder is made when it's first used).</summary>
    public FamilyProfile Add(string name, int? dailyLimitMinutes, bool canChangeSettings)
    {
        var profile = new FamilyProfile
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = name.Trim(),
            DailyLimitMinutes = dailyLimitMinutes,
            CanChangeSettings = canChangeSettings,
        };
        Save(profile);
        return profile;
    }

    /// <summary>Removes a profile from the list. Its folder is kept, so nothing is lost by mistake.</summary>
    public void Remove(string profileId)
    {
        if (profileId == FamilyProfile.MainId)
        {
            throw new InvalidOperationException("The first profile can't be removed.");
        }

        var file = Read();
        Write(file with
        {
            Profiles = file.Profiles.Where(p => p.Id != profileId).ToList(),
            Play = file.Play.Where(kv => kv.Key != profileId).ToDictionary(),
        });
    }

    // ---------------------------------------------------------------- the parent PIN

    /// <summary>Sets the PIN (4 to 12 digits). Null removes it.</summary>
    public void SetPin(string? pin)
    {
        if (pin is not null && !IsValidPin(pin))
        {
            throw new ArgumentException("A PIN is 4 to 12 digits.", nameof(pin));
        }

        Write(Read() with { Pin = pin is null ? null : PinHash.Create(pin) });
    }

    /// <summary>True when <paramref name="pin"/> matches, or when no PIN is set.</summary>
    public bool VerifyPin(string pin) => Read().Pin is not { } hash || hash.Matches(pin);

    public static bool IsValidPin(string pin) => pin.Length is >= 4 and <= 12 && pin.All(char.IsAsciiDigit);

    // ---------------------------------------------------------------- play today

    public DailyPlay PlayToday(string profileId, DateOnly today) =>
        Read().Play.TryGetValue(profileId, out var play) && play.Date == today ? play : new DailyPlay { Date = today };

    /// <summary>Adds play time to today's total (a new day starts at zero).</summary>
    public DailyPlay AddPlay(string profileId, DateOnly today, TimeSpan played)
    {
        var play = PlayToday(profileId, today);
        play = play with { Seconds = play.Seconds + played.TotalSeconds };
        SetPlay(profileId, play);
        return play;
    }

    /// <summary>A parent entered the PIN at the limit: no more stops for this profile today.</summary>
    public void LiftLimit(string profileId, DateOnly today) =>
        SetPlay(profileId, PlayToday(profileId, today) with { LimitLifted = true });

    private void SetPlay(string profileId, DailyPlay play)
    {
        var file = Read();
        var plays = new Dictionary<string, DailyPlay>(file.Play) { [profileId] = play };
        Write(file with { Play = plays });
    }

    // ---------------------------------------------------------------- the file

    private FamilyFile Read()
    {
        FamilyFile? file = null;
        try
        {
            if (File.Exists(FilePath))
            {
                file = JsonSerializer.Deserialize<FamilyFile>(File.ReadAllText(FilePath), Json);
            }
        }
        catch (JsonException)
        {
            // A damaged file falls back to the main profile alone; it's rewritten on the next change.
        }

        file ??= new FamilyFile();
        if (!file.Profiles.Any(p => p.Id == FamilyProfile.MainId))
        {
            file = file with { Profiles = [new FamilyProfile { Id = FamilyProfile.MainId, Name = "Player 1" }, .. file.Profiles] };
        }

        return file;
    }

    private void Write(FamilyFile file)
    {
        Directory.CreateDirectory(Root);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
        File.Move(temp, FilePath, overwrite: true);
    }

    private sealed record FamilyFile
    {
        public int Version { get; init; } = 1;

        public IReadOnlyList<FamilyProfile> Profiles { get; init; } = [];

        public PinHash? Pin { get; init; }

        public IReadOnlyDictionary<string, DailyPlay> Play { get; init; } = new Dictionary<string, DailyPlay>();
    }

    /// <summary>PBKDF2-SHA256 with a random salt; compared in constant time.</summary>
    private sealed record PinHash(string Salt, string Hash, int Iterations)
    {
        public static PinHash Create(string pin)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            return new PinHash(Convert.ToBase64String(salt), Convert.ToBase64String(Derive(pin, salt, PinIterations)), PinIterations);
        }

        public bool Matches(string pin)
        {
            try
            {
                var expected = Convert.FromBase64String(Hash);
                return CryptographicOperations.FixedTimeEquals(Derive(pin, Convert.FromBase64String(Salt), Iterations), expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static byte[] Derive(string pin, byte[] salt, int iterations) =>
            Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, 32);
    }
}
