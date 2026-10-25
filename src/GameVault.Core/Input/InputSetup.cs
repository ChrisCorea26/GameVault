namespace GameVault.Core.Input;

/// <summary>What is plugged into the two controller ports.</summary>
public enum InputSetup : byte
{
    /// <summary>Two standard controllers.</summary>
    Controllers,

    /// <summary>A controller on port 1 and a Zapper on port 2 (where Zapper games expect it).</summary>
    Zapper,

    /// <summary>A Four Score adapter with four controllers.</summary>
    FourScore,
}
