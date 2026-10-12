using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using SpireLens.Core.Replay;

namespace SpireLens.Core.Patches;

/// <summary>
/// Run recording: snapshot every run save the game writes.
///
/// Targets the <c>SaveRun(SerializableRun save, bool isMultiplayer)</c>
/// overload, not the <c>SaveRun(AbstractRoom)</c> one
/// <see cref="RunSaveSnapshotPatch"/> uses: every write funnels through this
/// one — the gameplay saves (the room overload builds the
/// <c>SerializableRun</c> and calls it) plus the direct callers the room
/// overload misses, notably <c>SaveManager.IncrementNumReloads</c> on every
/// load. The prefix sees the exact object the game serializes next, so the
/// snapshot is the save itself rather than a re-derivation of it.
/// </summary>
[HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.SaveRun), new[] { typeof(SerializableRun), typeof(bool) })]
public static class ReplaySaveSnapshotPatch
{
    [HarmonyPrefix]
    public static void Prefix(SerializableRun save, bool isMultiplayer)
    {
        PatchGuard.Run(nameof(ReplaySaveSnapshotPatch), () =>
            ReplayRecorder.OnRunSaveWriting(save, isMultiplayer));
    }
}
