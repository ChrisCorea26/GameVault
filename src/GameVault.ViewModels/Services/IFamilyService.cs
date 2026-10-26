using GameVault.Library.Family;

namespace GameVault.ViewModels.Services;

/// <summary>Family profiles, the parent PIN and daily limits (SPEC §7.9).</summary>
public interface IFamilyService
{
    IReadOnlyList<FamilyProfile> Profiles { get; }

    /// <summary>The profile GameVault is running as.</summary>
    FamilyProfile Current { get; }

    bool HasPin { get; }

    /// <summary>True when the PIN matches, or when there's no PIN.</summary>
    bool VerifyPin(string pin);

    /// <summary>Sets the PIN (4 to 12 digits); null removes it.</summary>
    void SetPin(string? pin);

    FamilyProfile Add(string name, int? dailyLimitMinutes, bool canChangeSettings);

    void Save(FamilyProfile profile);

    void Remove(string profileId);

    /// <summary>Closes the game and restarts GameVault as another profile.</summary>
    void SwitchTo(string profileId);

    /// <summary>The current profile's play today.</summary>
    DailyPlay PlayToday();

    /// <summary>Adds play to the current profile's day.</summary>
    DailyPlay AddPlay(TimeSpan played);

    /// <summary>A parent entered the PIN at the limit: no more stops today.</summary>
    void LiftLimit();

    event EventHandler? Changed;
}
