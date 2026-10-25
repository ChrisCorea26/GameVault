using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>
/// Practice mode (SPEC §7.6): pin a practice point, retry it instantly (F6), and time segments with splits
/// (F7). Times are counted in emulated frames, so they don't depend on the PC's speed; personal bests are
/// kept per segment.
/// </summary>
public sealed partial class PracticeViewModel(IEmulatorService emulator, IPracticeRecords records) : ObservableObject
{
    public const double FramesPerSecond = 39375000.0 / 655171.0;

    private byte[]? _point;
    private long _segmentStart;
    private string? _gameKey;

    public ObservableCollection<SegmentViewModel> Segments { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand), nameof(SplitCommand))]
    public partial bool HasPoint { get; private set; }

    /// <summary>Time in the current segment, refreshed by <see cref="Tick"/>.</summary>
    [ObservableProperty]
    public partial string CurrentTime { get; private set; } = Format(0);

    [ObservableProperty]
    public partial int CurrentSegment { get; private set; } = 1;

    public string BestForCurrent => _gameKey is not null && records.Bests(_gameKey).TryGetValue(CurrentSegment, out var best)
        ? $"Best {Format(best)}"
        : "No best yet";

    [RelayCommand]
    private async Task SetPointAsync()
    {
        _point = await emulator.CaptureStateAsync();
        _gameKey = emulator.GameKey;
        HasPoint = _point is not null;
        StartRun();
    }

    [RelayCommand(CanExecute = nameof(HasPoint))]
    private async Task RetryAsync()
    {
        if (_point is not null && await emulator.RestoreStateAsync(_point))
        {
            StartRun();
        }
    }

    [RelayCommand(CanExecute = nameof(HasPoint))]
    private void Split()
    {
        var frames = emulator.FrameCount - _segmentStart;
        var isBest = _gameKey is not null && records.Record(_gameKey, CurrentSegment, frames);
        Segments.Add(new SegmentViewModel(CurrentSegment, Format(frames), isBest));
        _segmentStart = emulator.FrameCount;
        CurrentSegment++;
        OnPropertyChanged(nameof(BestForCurrent));
    }

    /// <summary>Refreshes <see cref="CurrentTime"/>; the page calls this a few times a second.</summary>
    public void Tick()
    {
        if (HasPoint)
        {
            CurrentTime = Format(emulator.FrameCount - _segmentStart);
        }
    }

    public static string Format(long frames)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, frames) / FramesPerSecond);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss\.ff") : time.ToString(@"m\:ss\.ff");
    }

    private void StartRun()
    {
        Segments.Clear();
        CurrentSegment = 1;
        _segmentStart = emulator.FrameCount;
        CurrentTime = Format(0);
        OnPropertyChanged(nameof(BestForCurrent));
    }
}

public sealed record SegmentViewModel(int Number, string Time, bool IsPersonalBest)
{
    public string Title => $"Segment {Number}";

    public string Badge => IsPersonalBest ? "New best" : string.Empty;
}
