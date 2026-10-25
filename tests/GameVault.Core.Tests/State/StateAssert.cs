using GameVault.Core.State;

namespace GameVault.Core.Tests.State;

/// <summary>
/// Shared assertions for <see cref="IStateful"/> components. Every component and every mapper
/// gets a round-trip test built on these helpers (SPEC §11.1).
/// </summary>
public static class StateAssert
{
    /// <summary>
    /// Builds a component, mutates it, saves it, loads the bytes into a fresh instance, and checks
    /// that saving the fresh instance produces identical bytes.
    /// </summary>
    public static void RoundTrips<T>(Func<T> create, Action<T> mutate)
        where T : IStateful
    {
        var original = create();
        mutate(original);
        var saved = Save(original);

        var restored = create();
        restored.LoadState(new StateReader(saved));
        var resaved = Save(restored);

        resaved.Should().Equal(saved, "loading a saved state and saving again must produce identical bytes");
    }

    public static byte[] Save(IStateful component)
    {
        var writer = new StateWriter();
        component.SaveState(writer);
        return writer.ToArray();
    }
}
