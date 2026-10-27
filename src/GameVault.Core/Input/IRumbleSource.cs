namespace GameVault.Core.Input;

/// <summary>A console whose controllers can rumble (the N64's Rumble Pak). The host passes it on to the player's pad.</summary>
public interface IRumbleSource
{
    /// <summary>How hard controller <paramref name="port"/>'s motor runs now, 0 (still) to 1.</summary>
    float RumbleLevel(int port);
}
