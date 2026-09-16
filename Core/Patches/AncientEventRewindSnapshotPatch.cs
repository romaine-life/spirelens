using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;

namespace SpireLens.Core.Patches;

/// <summary>
/// Keeps RoomResetter's rewind target in step with the save a finished Ancient
/// event writes.
///
/// <c>EventRoom.OnEventStateChanged</c> is the only event-side save: once every
/// <c>AncientEventModel</c> in the room is finished it calls
/// <c>MarkPreFinished()</c> and <c>SaveRun(this)</c>, after the chosen option
/// has already granted its relic. From then on a restart replays the finished
/// event, so the record must rewind to this point rather than to room entry.
///
/// The prefix/postfix pair detects the not-finished → finished transition, so
/// ordinary events (which return early) and repeat state changes never retake
/// the snapshot. The method is reached through the <c>StateChanged</c>
/// delegate, never a direct call site.
/// </summary>
[HarmonyPatch(typeof(EventRoom), nameof(EventRoom.OnEventStateChanged))]
public static class AncientEventRewindSnapshotPatch
{
    [HarmonyPrefix]
    public static void Prefix(EventRoom __instance, out bool __state)
    {
        __state = __instance.IsPreFinished;
    }

    [HarmonyPostfix]
    public static void Postfix(EventRoom __instance, EventModel eventModel, bool __state)
    {
        try
        {
            if (__state || !__instance.IsPreFinished) return;
            if (!RunTracker.GameWritesRunSave()) return;

            RunTracker.CaptureRewindSnapshotAtPreFinishedSave(
                $"ancient event finished: {eventModel?.Id}");
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"AncientEventRewindSnapshotPatch failed: {e.Message}");
        }
    }
}
