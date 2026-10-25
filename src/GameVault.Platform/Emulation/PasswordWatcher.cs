using GameVault.Core;
using GameVault.Library.Passwords;

namespace GameVault.Platform.Emulation;

/// <summary>
/// Watches for a game's password screen (SPEC §7.3): when every detection condition has held for the
/// definition's number of frames, the password is read once and reported; it is read again only after the
/// screen goes away. Runs on the emulation thread after each real frame; reads only a few bytes.
/// </summary>
public sealed class PasswordWatcher
{
    private PasswordDefinition? _definition;
    private int _stableFrames;
    private bool _captured;

    public PasswordDefinition? Definition
    {
        get => _definition;
        set
        {
            _definition = value;
            _stableFrames = 0;
            _captured = false;
        }
    }

    /// <summary>Returns the password when it has just appeared, otherwise null.</summary>
    public string? OnFrame(NesConsole console)
    {
        if (_definition is not { } definition)
        {
            return null;
        }

        if (!definition.IsPasswordScreen(a => console.Bus.Peek((ushort)a)))
        {
            _stableFrames = 0;
            _captured = false;
            return null;
        }

        if (_captured || ++_stableFrames < Math.Max(1, definition.StableFrames))
        {
            return null;
        }

        _captured = true;
        return definition.Decode(a => console.Bus.Peek((ushort)a), console.Ppu.PeekNametable);
    }
}
