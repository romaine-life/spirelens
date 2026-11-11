using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using SpireLens.Core.Pad;

namespace SpireLens.Core.Patches;

/// <summary>
/// Reads the right-stick click through GameInput in step with the game's own
/// per-frame pad read (<c>NControllerManager._Process</c> runs its Steam
/// Input or Godot strategy there): one GameInput reading per frame, the
/// cadence at which the game itself reads Steam Input.
/// </summary>
[HarmonyPatch(typeof(NControllerManager), nameof(NControllerManager._Process))]
public static class GameInputRightStickPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        try
        {
            if (GameInputPad.PollPress() && NGame.IsGameFocusedWindow())
                StatsVisibilityHotkeyPatch.OnGameInputRightStickPress();
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"GameInputRightStickPatch failed: {e.Message}");
        }
    }
}
