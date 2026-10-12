using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using SpireLens.Core.Replay;

namespace SpireLens.Core.Patches;

/// <summary>
/// Run recording: keep a copy of the game's own per-combat replay.
///
/// <c>RunManager.WriteReplay(bool stopRecording)</c> writes the built-in
/// <c>CombatReplayWriter</c> buffer to <c>&lt;profile&gt;/replays/latest.mcr</c>,
/// overwriting it, at every combat end, on <c>CleanUp</c> (Save and Quit,
/// abandon, room restart) and for the bug-report console command. The prefix
/// reads how many events the buffer holds (a <c>stopRecording</c> write clears
/// it), so CleanUp writes from non-combat rooms — zero events — are not copied.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.WriteReplay))]
public static class ReplayCombatReplayCopyPatch
{
    [HarmonyPrefix]
    public static void Prefix(RunManager __instance, out int __state)
    {
        int count = 0;
        PatchGuard.Run(nameof(ReplayCombatReplayCopyPatch) + ".Prefix", () =>
            count = ReplayRecorder.PendingCombatReplayEventCount(__instance));
        __state = count;
    }

    [HarmonyPostfix]
    public static void Postfix(bool stopRecording, int __state)
    {
        PatchGuard.Run(nameof(ReplayCombatReplayCopyPatch) + ".Postfix", () =>
            ReplayRecorder.OnCombatReplayWritten(stopRecording, __state));
    }
}
