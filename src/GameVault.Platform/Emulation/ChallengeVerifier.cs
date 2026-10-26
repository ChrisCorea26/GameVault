using System.Globalization;
using GameVault.Core;
using GameVault.Core.Input;
using GameVault.Core.State;
using GameVault.Core.Systems;
using GameVault.Library.Challenges;
using GameVault.Library.Hashing;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Emulation;

/// <summary>
/// Checks a challenge result (SPEC §7.9) by replaying it: a fresh console loads the challenge's start state
/// (with no codes), gets each recorded frame's buttons, and must reach the goal exactly at the reported frame,
/// not before and within the time limit. The deterministic core makes the replay match the original attempt.
/// </summary>
public static class ChallengeVerifier
{
    public static ChallengeCheck Verify(
        byte[] romImage, ChallengeFile challenge, ChallengeResult result, GameSystem system = GameSystem.Nes, PlayStationSetup? playStation = null)
    {
        if (result.ChallengeId != challenge.Id)
        {
            return new ChallengeCheck(false, "This result is for a different challenge.");
        }

        if (!string.Equals(RomHasher.Hash(romImage, system).Sha1, challenge.RomSha1, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(result.RomSha1, challenge.RomSha1, StringComparison.OrdinalIgnoreCase))
        {
            return new ChallengeCheck(false, "This result was played on a different game or version.");
        }

        List<(ushort Player1, ushort Player2)> inputs;
        try
        {
            inputs = InputRecording.Decode(Convert.FromBase64String(result.Inputs), result.Version);
        }
        catch (FormatException)
        {
            return new ChallengeCheck(false, "This result's recording is damaged.");
        }

        if (inputs.Count != result.Frames || result.Frames <= 0)
        {
            return new ChallengeCheck(false, "This result doesn't check out.");
        }

        if (challenge.TimeLimitFrames is { } limit && result.Frames > limit)
        {
            return new ChallengeCheck(false, "This result took longer than the time limit.");
        }

        IEmulatedConsole console;
        try
        {
            console = ConsoleFactory.Create(
                romImage, system, new GameVault.Library.Profiles.GameProfile { Sha1 = challenge.RomSha1 }, fdsBios: null, playStation: playStation);
        }
        catch (GameLoadException)
        {
            return new ChallengeCheck(false, "This game can't be checked.");
        }

        try
        {
            return Replay(console, challenge, result, inputs);
        }
        finally
        {
            // A PlayStation's replay opened its own copy of the disc.
            ((console as GameVault.Core.PlayStation.PlayStationConsole)?.CdRom.Disc as IDisposable)?.Dispose();
        }
    }

    private static ChallengeCheck Replay(
        IEmulatedConsole console, ChallengeFile challenge, ChallengeResult result, List<(ushort Player1, ushort Player2)> inputs)
    {
        console.Power();
        try
        {
            console.LoadSnapshot(Convert.FromBase64String(challenge.StartState));
        }
        catch (Exception ex) when (ex is StateFormatException or FormatException)
        {
            return new ChallengeCheck(false, "The challenge's starting point doesn't work with this game.");
        }

        (console as NesConsole)?.SetCheats([]);
        Func<int, byte> ram = console.PeekMemory;
        for (var frame = 0; frame < inputs.Count; frame++)
        {
            console.SetControllerState(0, (ControllerState)inputs[frame].Player1);
            console.SetControllerState(1, (ControllerState)inputs[frame].Player2);
            console.RunFrame();
            var reached = challenge.GoalHolds(ram);
            if (reached != (frame == inputs.Count - 1))
            {
                return new ChallengeCheck(false, "This result doesn't check out: replaying it doesn't reach the goal when it says.");
            }
        }

        return new ChallengeCheck(true, $"Verified: done in {Time(result.Frames)}" + (result.Player.Length > 0 ? $" by {result.Player}." : "."));
    }

    /// <summary>Frames as m:ss.cc at the NTSC frame rate.</summary>
    public static string Time(int frames)
    {
        var seconds = frames / 60.0988;
        return string.Create(CultureInfo.InvariantCulture, $"{(int)(seconds / 60)}:{seconds % 60:00.00}");
    }
}
