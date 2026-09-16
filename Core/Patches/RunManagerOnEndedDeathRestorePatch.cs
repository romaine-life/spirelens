using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace SpireLens.Core.Patches;

/// <summary>
/// Keeps what a death destroys so the restart action can undo it. A prefix,
/// because <c>RunManager.OnEnded</c> itself deletes the run save and records the
/// loss into progress and run history.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
public static class RunManagerOnEndedDeathRestorePatch
{
    [HarmonyPrefix]
    public static void Prefix(RunManager __instance, bool isVictory)
    {
        PatchGuard.Run(nameof(RunManagerOnEndedDeathRestorePatch), () =>
            RoomResetter.CaptureDeathRestorePoint(__instance, isVictory));
    }
}
