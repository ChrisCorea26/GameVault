using GameVault.Library.Family;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Storage;

/// <summary>
/// Family profiles over <see cref="FamilyStore"/> (SPEC §7.9). GameVault runs as one profile at a time; switching
/// restarts it with <c>--profile=&lt;id&gt;</c> through <paramref name="restart"/>, so every service opens that
/// profile's folders.
/// </summary>
public sealed class FamilyService(FamilyStore store, string currentProfileId, Action<string> restart, TimeProvider? clock = null)
    : IFamilyService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();

    public IReadOnlyList<FamilyProfile> Profiles
    {
        get
        {
            lock (_lock)
            {
                return store.Profiles;
            }
        }
    }

    /// <summary>The running profile; the main one if the requested profile was removed.</summary>
    public FamilyProfile Current
    {
        get
        {
            lock (_lock)
            {
                return store.Find(currentProfileId) ?? store.Find(FamilyProfile.MainId)!;
            }
        }
    }

    public bool HasPin
    {
        get
        {
            lock (_lock)
            {
                return store.HasPin;
            }
        }
    }

    public event EventHandler? Changed;

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public bool VerifyPin(string pin)
    {
        lock (_lock)
        {
            return store.VerifyPin(pin);
        }
    }

    public void SetPin(string? pin) => Change(() => store.SetPin(pin));

    public FamilyProfile Add(string name, int? dailyLimitMinutes, bool canChangeSettings)
    {
        FamilyProfile? added = null;
        Change(() => added = store.Add(name, dailyLimitMinutes, canChangeSettings));
        return added!;
    }

    public void Save(FamilyProfile profile) => Change(() => store.Save(profile));

    public void Remove(string profileId) => Change(() => store.Remove(profileId));

    public void SwitchTo(string profileId)
    {
        if (profileId != Current.Id && Profiles.Any(p => p.Id == profileId))
        {
            restart(profileId);
        }
    }

    public DailyPlay PlayToday()
    {
        lock (_lock)
        {
            return store.PlayToday(Current.Id, Today);
        }
    }

    public DailyPlay AddPlay(TimeSpan played)
    {
        lock (_lock)
        {
            return store.AddPlay(Current.Id, Today, played);
        }
    }

    public void LiftLimit()
    {
        lock (_lock)
        {
            store.LiftLimit(Current.Id, Today);
        }
    }

    private void Change(Action action)
    {
        lock (_lock)
        {
            action();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
