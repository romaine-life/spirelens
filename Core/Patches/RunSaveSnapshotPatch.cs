using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace SpireLens.Core.Patches;

/// <summary>
/// Takes the run record's rewind target at the instant the game writes
/// <c>current_run.save</c>.
///
/// This overload is where every gameplay save is actually serialized: map-node
/// entry (<c>EnterMapPointInternal</c>), combat victory and a finished Ancient
/// event all reach it through <c>SaveManager.SaveRun</c>, which first awaits
/// any save still in flight. The prefix runs in the same synchronous segment as
/// the method's own <c>RunManager.ToSave</c>, so the snapshot and the save
/// describe the same moment. The method's write condition is mirrored, because
/// a save it skips leaves the previous one on disk and the snapshot must keep
/// matching that one.
///
/// Whatever the game replays from this save — room creation, room-entry hooks,
/// the visit itself — then happens after the snapshot, so rewinding to it on a
/// room restart or a main-menu Continue cannot count any of it twice.
/// </summary>
[HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.SaveRun), new[] { typeof(AbstractRoom) })]
public static class RunSaveSnapshotPatch
{
    [HarmonyPrefix]
    public static void Prefix(AbstractRoom? preFinishedRoom)
    {
        try
        {
            if (!RunTracker.GameWritesRunSave()) return;
            RunTracker.OnRunSaveWriting(preFinishedRoom);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"RunSaveSnapshotPatch failed: {e.Message}");
        }
    }
}
