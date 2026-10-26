using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.Library.Challenges;
using GameVault.Library.Memory;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>
/// The Player's Challenges flyout (SPEC §7.9): make a challenge from this moment, take one a friend sent,
/// save the result, and check a friend's result by replaying it.
/// </summary>
public sealed partial class ChallengesViewModel(IEmulatorService emulator, IChallengeFiles files) : ObservableObject
{
    private static readonly string[] OpSymbols = ["==", "!=", "<", "<=", ">", ">="];
    private ChallengeResult? _lastResult;
    private bool _attached;

    // ---------------------------------------------------------------- making a challenge

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>The memory map's value names to build the goal from.</summary>
    public IReadOnlyList<string> GoalValues => emulator.MemoryValueNames;

    public bool CanMakeChallenges => GoalValues.Count > 0;

    [ObservableProperty]
    public partial int GoalValueIndex { get; set; }

    public IReadOnlyList<string> Ops { get; } = ["=", "≠", "<", "≤", ">", "≥"];

    [ObservableProperty]
    public partial int OpIndex { get; set; }

    [ObservableProperty]
    public partial string Target { get; set; } = "0";

    /// <summary>Seconds allowed; empty for no limit.</summary>
    [ObservableProperty]
    public partial string TimeLimitSeconds { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Message { get; private set; }

    // ---------------------------------------------------------------- taking one

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChallenge), nameof(ChallengeSummary))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CheckResultCommand))]
    public partial ChallengeFile? Challenge { get; private set; }

    public bool HasChallenge => Challenge is not null;

    /// <summary>"Press A — goal: RAM $0000 = 128 — 1:00 allowed."</summary>
    public string ChallengeSummary => Challenge is not { } c
        ? string.Empty
        : $"{c.Name}: {c.DescribeGoal()}" + (c.TimeLimitFrames is { } limit ? $", within {Format(limit)}" : string.Empty)
            + (c.Note is { } note ? $". {note}" : string.Empty);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(GiveUpCommand))]
    public partial bool IsAttempting { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveResultCommand))]
    public partial bool HasResult { get; private set; }

    public void Attach()
    {
        if (!_attached)
        {
            emulator.ChallengeFinished += OnFinished;
            _attached = true;
        }

        OnPropertyChanged(nameof(GoalValues));
        OnPropertyChanged(nameof(CanMakeChallenges));
    }

    public void Detach()
    {
        if (_attached)
        {
            emulator.ChallengeFinished -= OnFinished;
            _attached = false;
        }
    }

    [RelayCommand]
    private async Task MakeAsync()
    {
        Message = null;
        if (!CanMakeChallenges || GoalValueIndex < 0 || GoalValueIndex >= GoalValues.Count)
        {
            Message = "This game needs a memory map before goals can be set.";
            return;
        }

        if (!int.TryParse(Target, NumberStyles.Integer, CultureInfo.CurrentCulture, out var target))
        {
            Message = "The goal's number must be a whole number.";
            return;
        }

        int? limit = null;
        if (!string.IsNullOrWhiteSpace(TimeLimitSeconds))
        {
            if (!double.TryParse(TimeLimitSeconds, NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds) || seconds <= 0)
            {
                Message = "The time limit is in seconds, or leave it empty for none.";
                return;
            }

            limit = (int)Math.Round(seconds * 60.0988);
        }

        try
        {
            var goal = MemoryCondition.Named(GoalValues[GoalValueIndex], OpSymbols[Math.Clamp(OpIndex, 0, 5)], target);
            var challenge = await emulator.CreateChallengeAsync(Name, [goal], limit, null);
            if (await files.SaveAsync(ChallengeFileKind.Challenge, challenge.Name, ChallengeFiles.Save(challenge)))
            {
                Message = $"Saved \"{challenge.Name}\". Send the file to a friend; they open it here to take the challenge.";
            }
        }
        catch (ChallengeException ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (await files.OpenAsync(ChallengeFileKind.Challenge) is not { } text)
        {
            return;
        }

        try
        {
            Challenge = ChallengeFiles.LoadChallenge(text);
            HasResult = false;
            Message = null;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            Message = "That file isn't a GameVault challenge.";
        }
    }

    private bool CanStart() => Challenge is not null && !IsAttempting;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        try
        {
            await emulator.StartChallengeAsync(Challenge!);
            IsAttempting = true;
            HasResult = false;
            Message = "Go! Rewind, loading states and codes are off until the attempt ends.";
        }
        catch (ChallengeException ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(IsAttempting))]
    private void GiveUp() => emulator.CancelChallenge();

    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task SaveResultAsync()
    {
        if (_lastResult is { } result && Challenge is { } challenge
            && await files.SaveAsync(ChallengeFileKind.Result, $"{challenge.Name} result", ChallengeFiles.Save(result)))
        {
            Message = "Result saved. Send it back to whoever made the challenge so they can check it.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasChallenge))]
    private async Task CheckResultAsync()
    {
        if (Challenge is not { } challenge || await files.OpenAsync(ChallengeFileKind.Result) is not { } text)
        {
            return;
        }

        try
        {
            var check = await emulator.VerifyChallengeAsync(challenge, ChallengeFiles.LoadResult(text));
            Message = check.Message;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            Message = "That file isn't a GameVault challenge result.";
        }
    }

    private void OnFinished(object? sender, ChallengeOutcome outcome)
    {
        IsAttempting = false;
        _lastResult = outcome.Result;
        HasResult = outcome.Result is not null;
        Message = outcome.Kind switch
        {
            ChallengeOutcomeKind.Done => $"Done in {Format(outcome.Frames)}! Save your result to share it.",
            ChallengeOutcomeKind.TimeUp => $"Time's up after {Format(outcome.Frames)}. Try again?",
            _ => "Attempt stopped.",
        };
    }

    /// <summary>Frames as m:ss.cc at the NTSC frame rate.</summary>
    public static string Format(int frames)
    {
        var seconds = frames / 60.0988;
        return string.Create(CultureInfo.InvariantCulture, $"{(int)(seconds / 60)}:{seconds % 60:00.00}");
    }
}
