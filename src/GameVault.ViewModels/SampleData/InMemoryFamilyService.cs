using GameVault.Library.Family;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>Family profiles kept in memory (tests and <c>--sample-data</c>). The sample PIN is 1234.</summary>
public sealed class InMemoryFamilyService : IFamilyService
{
    private readonly List<FamilyProfile> _profiles;
    private readonly TimeProvider _clock;
    private string? _pin;
    private DailyPlay _play;

    public InMemoryFamilyService(TimeProvider? clock = null, bool withSamples = true)
    {
        _clock = clock ?? TimeProvider.System;
        _profiles = [new FamilyProfile { Id = FamilyProfile.MainId, Name = withSamples ? "Alex" : "Player 1" }];
        if (withSamples)
        {
            _profiles.Add(new FamilyProfile { Id = "sample-kid", Name = "Sam", Icon = "", DailyLimitMinutes = 60, CanChangeSettings = false });
            _pin = "1234";
        }

        Current = _profiles[0];
        _play = new DailyPlay { Date = Today, Seconds = withSamples ? 25 * 60 : 0 };
    }

    public IReadOnlyList<FamilyProfile> Profiles => _profiles;

    public FamilyProfile Current { get; private set; }

    public bool HasPin => _pin is not null;

    /// <summary>The profile <see cref="SwitchTo"/> was last asked for (a real app restarts here).</summary>
    public string? SwitchedTo { get; private set; }

    public event EventHandler? Changed;

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public bool VerifyPin(string pin) => _pin is null || _pin == pin;

    public void SetPin(string? pin)
    {
        if (pin is not null && !FamilyStore.IsValidPin(pin))
        {
            throw new ArgumentException("A PIN is 4 to 12 digits.", nameof(pin));
        }

        _pin = pin;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public FamilyProfile Add(string name, int? dailyLimitMinutes, bool canChangeSettings)
    {
        var profile = new FamilyProfile
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = name.Trim(),
            DailyLimitMinutes = dailyLimitMinutes,
            CanChangeSettings = canChangeSettings,
        };
        _profiles.Add(profile);
        Changed?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    public void Save(FamilyProfile profile)
    {
        var index = _profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            _profiles[index] = profile;
        }
        else
        {
            _profiles.Add(profile);
        }

        if (Current.Id == profile.Id)
        {
            Current = profile;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string profileId)
    {
        if (profileId == FamilyProfile.MainId)
        {
            throw new InvalidOperationException("The first profile can't be removed.");
        }

        _profiles.RemoveAll(p => p.Id == profileId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tests and previews switch in place; the real service restarts GameVault.</summary>
    public void SwitchTo(string profileId)
    {
        SwitchedTo = profileId;
        Current = _profiles.First(p => p.Id == profileId);
        _play = new DailyPlay { Date = Today };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public DailyPlay PlayToday() => _play.Date == Today ? _play : _play = new DailyPlay { Date = Today };

    public DailyPlay AddPlay(TimeSpan played)
    {
        var play = PlayToday();
        return _play = play with { Seconds = play.Seconds + played.TotalSeconds };
    }

    public void LiftLimit() => _play = PlayToday() with { LimitLifted = true };
}
