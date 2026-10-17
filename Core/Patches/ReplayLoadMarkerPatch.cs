using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireLens.Core.Replay;

namespace SpireLens.Core.Patches;

/// <summary>
/// Run recording: mark every resume-from-save. Main-menu Continue,
/// <see cref="RoomResetter"/> room restarts and death undos all go through
/// <c>RunManager.SetUpSavedSingleplayer(RunState state, SerializableRun save)</c>.
/// The prefix runs before its <c>IncrementNumReloads</c> re-save, so the load
/// marker precedes that re-save's save point in the log.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpSavedSingleplayer))]
public static class ReplayLoadMarkerSingleplayerPatch
{
    [HarmonyPrefix]
    public static void Prefix(SerializableRun save)
    {
        PatchGuard.Run(nameof(ReplayLoadMarkerSingleplayerPatch), () =>
            ReplayRecorder.OnLoadingSave(save, nameof(RunManager.SetUpSavedSingleplayer)));
    }
}

/// <summary>
/// Multiplayer counterpart: <c>RunManager.SetUpSavedMultiplayer(RunState state, LoadRunLobby lobby)</c>,
/// whose save is <c>lobby.Run</c>. Multiplayer recording is best-effort.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpSavedMultiplayer))]
public static class ReplayLoadMarkerMultiplayerPatch
{
    [HarmonyPrefix]
    public static void Prefix(LoadRunLobby lobby)
    {
        PatchGuard.Run(nameof(ReplayLoadMarkerMultiplayerPatch), () =>
        {
            if (lobby?.Run != null)
                ReplayRecorder.OnLoadingSave(lobby.Run, nameof(RunManager.SetUpSavedMultiplayer));
        });
    }
}
