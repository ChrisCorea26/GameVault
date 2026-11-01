using System.Diagnostics;

namespace GameVault.App.Controls;

/// <summary>
/// Stops the render threads drawing and presenting while the window's presenter changes (full screen on or off).
/// Every crash on that change happened while a game was presenting frames, and inside
/// <c>AppWindow.SetPresenter</c> itself (docs/bugs.md GV-027); the same change with no game running never failed.
/// </summary>
internal static class PresentGate
{
    private static int _held;
    private static int _presenting;

    /// <summary>Called by a render thread before it draws; false means skip this frame.</summary>
    public static bool TryEnter()
    {
        Interlocked.Increment(ref _presenting);
        if (Volatile.Read(ref _held) != 0)
        {
            Interlocked.Decrement(ref _presenting);
            return false;
        }

        return true;
    }

    /// <summary>Called by a render thread once its frame is presented.</summary>
    public static void Exit() => Interlocked.Decrement(ref _presenting);

    /// <summary>Holds new frames back and waits (briefly) for one being presented to finish. UI thread.</summary>
    public static void Hold()
    {
        Volatile.Write(ref _held, 1);
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref _presenting) != 0 && clock.ElapsedMilliseconds < 250)
        {
            Thread.Sleep(1);
        }
    }

    public static void Release() => Volatile.Write(ref _held, 0);
}
