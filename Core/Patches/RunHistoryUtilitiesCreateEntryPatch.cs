using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace SpireLens.Core.Patches;

/// <summary>
/// Universal run-end hook. Supersedes the earlier attempt on
/// RunManager.OnEnded, which only covered in-game death and in-game victory —
/// missing the main-menu abandon path (NMainMenu.AbandonRun operates directly
/// on the save file without going through RunManager).
///
/// <c>RunHistoryUtilities.CreateRunHistoryEntry</c> is the single
/// funnel that all three terminal paths go through:
///   - Victory:       RunManager.OnEnded -> CreateRunHistoryEntry(..., victory=true, ...)
///   - Death:         CreatureCmd.Kill (all players) -> RunManager.OnEnded -> CreateRunHistoryEntry(..., victory=false, isAbandoned=false, ...)
///   - Main-menu abandon: NMainMenu.AbandonRun -> CreateRunHistoryEntry(..., victory=false, isAbandoned=true, ...)
///
/// The parameter names on the public method give us outcome directly:
///   abandoned   isAbandoned == true (highest priority)
///   win         victory == true
///   loss        neither
///
/// Called as a Prefix with no return-value manipulation so we don't affect
/// the game's own run history creation. Runs before the game's own side
/// effects (saves, achievements upload), which is fine — the call is
/// guaranteed to proceed regardless.
/// </summary>
[HarmonyPatch(typeof(RunHistoryUtilities), nameof(RunHistoryUtilities.CreateRunHistoryEntry))]
public static class RunHistoryUtilitiesCreateEntryPatch
{
    [HarmonyPostfix]
    public static void Postfix(SerializableRun run, bool victory, bool isAbandoned)
    {
        // Guard the terminal run-end funnel: an unhandled throw here would
        // propagate into RunManager.OnEnded / NMainMenu.AbandonRun and could
        // skip the game's own run cleanup. PatchGuard logs at always-on Error
        // level (not the debug-gated LogDebug) so a broken run-end is visible
        // without a debug flag.
        PatchGuard.Run(nameof(RunHistoryUtilitiesCreateEntryPatch), () =>
        {
            string outcome;
            if (isAbandoned) outcome = "abandoned";
            else if (victory) outcome = "win";
            else outcome = "loss";

            RunTracker.OnRunEnded(outcome);
        });

        // Run recording: separate guard so a recording failure can never
        // cost the stats record its outcome (or the reverse).
        PatchGuard.Run(nameof(RunHistoryUtilitiesCreateEntryPatch) + ".Recording", () =>
        {
            string outcome = isAbandoned ? "abandoned" : victory ? "win" : "loss";
            if (run != null)
                Replay.ReplayRecorder.OnRunEnded(run.StartTime, outcome, run.FloorReached);
        });
    }
}
