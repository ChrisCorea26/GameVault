using GameVault.Core.Systems;
using System.Diagnostics;
using GameVault.Core;
using GameVault.Core.State;

namespace GameVault.Platform.Emulation;

/// <summary>
/// Run-ahead (SPEC §5.5): run the real frame, save, run N more frames silently, show the last one, then
/// load the saved state. Games react N frames sooner without changing what the console really does.
/// Allocation-free after the first frame. Owned by the emulation thread.
/// </summary>
public sealed class RunAhead
{
    /// <summary>Turn off after this many frames in a row over budget (about half a second).</summary>
    public const int OverBudgetLimit = 30;

    private readonly StateWriter _writer = new();
    private byte[] _snapshot = [];
    private int _overBudgetStreak;

    /// <summary>How many frames to run ahead, 0–3.</summary>
    public int Frames { get; set; }

    /// <summary>
    /// False for a system whose states are too large to save and load every frame (the PlayStation 2,
    /// <see cref="SystemBudgets"/>): run-ahead stays off whatever <see cref="Frames"/> says.
    /// </summary>
    public bool Allowed { get; set; } = true;

    /// <summary>The time one host frame may take before run-ahead counts as too slow.</summary>
    public TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(1 / EmulatorHost.NtscFrameRate * 0.9);

    /// <summary>True once run-ahead switched itself off because this PC couldn't keep up.</summary>
    public bool AutoDisabled { get; private set; }

    /// <summary>Clears the automatic switch-off (for example when a new game starts).</summary>
    public void ResetAutoDisable()
    {
        AutoDisabled = false;
        _overBudgetStreak = 0;
    }

    /// <summary>
    /// Advances the console by one real frame and calls <paramref name="present"/> with the frame to show.
    /// Returns true if run-ahead just switched itself off.
    /// </summary>
    public bool RunFrame(IEmulatedConsole console, Action<IEmulatedConsole> present)
    {
        var frames = AutoDisabled || !Allowed ? 0 : Math.Clamp(Frames, 0, 3);
        if (frames == 0)
        {
            console.RunFrame();
            present(console);
            return false;
        }

        var start = Stopwatch.GetTimestamp();
        console.RunFrame();

        _writer.Reset();
        console.SaveSnapshot(_writer);
        var length = _writer.Length;
        if (_snapshot.Length < length)
        {
            _snapshot = new byte[length * 2];
        }

        _writer.WrittenSpan.CopyTo(_snapshot);

        for (var i = 0; i < frames; i++)
        {
            console.RunFrameSilently();
        }

        present(console);
        console.LoadSnapshot(_snapshot.AsMemory(0, length));

        if (Stopwatch.GetElapsedTime(start) > Budget)
        {
            if (++_overBudgetStreak >= OverBudgetLimit)
            {
                AutoDisabled = true;
                return true;
            }
        }
        else
        {
            _overBudgetStreak = 0;
        }

        return false;
    }
}
