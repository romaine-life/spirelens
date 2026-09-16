using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace SpireLens.Core;

/// <summary>
/// What a restart would replay right now. Either <see cref="RoomNoun"/> names
/// what a restart would replay ("combat", "shop", "rest site"), or
/// <see cref="BlockedReason"/> says why nothing can be.
///
/// <see cref="ReplayNote"/> describes where the replay actually lands, because
/// that differs by room. Replaying a fight already won returns you to its
/// reward screen rather than to the top of the fight — a real destination, and
/// often the wanted one, so it is offered and described honestly rather than
/// refused for not being the start of the room.
/// </summary>
public readonly record struct RoomRestartAvailability(
    string? RoomNoun,
    string? BlockedReason,
    string? ReplayNote = null)
{
    public bool CanRestart => BlockedReason == null;
}

/// <summary>
/// Restarts the room the player is currently in, from the state it began with.
///
/// This needs no snapshot of its own. Slay the Spire 2's run save is already a
/// room-boundary snapshot: <c>RunManager.EnterMapPointInternal</c> writes
/// <c>SaveRun(null)</c> on map-node entry — before the room type is even rolled —
/// and nothing is written while the player is inside a fight, a shop, or an
/// event. So for the duration of a room, <c>current_run.save</c> on disk IS that
/// room's opening state, RNG included; restarting is just replaying the save the
/// game already wrote. Because the RNG is restored too, the re-rolled room is
/// the same room, with the same encounter, the same merchant stock, or the same
/// event.
///
/// The two exceptions are the only places the game rewrites the save with a
/// pre-finished room: <c>CombatManager</c> at combat victory, and
/// <c>EventRoom.OnEventStateChanged</c> when an Ancient event finishes. Past
/// either point the save is the room's aftermath rather than its opening, so a
/// replay lands on the won fight's reward screen or just after the Ancient
/// resolved — <see cref="Describe"/> still offers it and says so.
///
/// The load sequence mirrors the main menu's Continue button
/// (<c>NMainMenu</c>: FromSerializable → SetUpSavedSingleplayer → LoadRun),
/// with <c>RunManager.CleanUp()</c> inserted first because
/// <c>SetUpSavedSingleplayer</c> throws when <c>RunManager.State</c> is non-null.
/// <c>CleanUp</c> is the same teardown Save and Quit / Abandon use: it sets
/// <c>ShouldSave = false</c> (so the abandoned attempt can never overwrite the
/// save), calls <c>CombatManager.Reset(graceful)</c>, and nulls <c>State</c>.
/// It deliberately does NOT call <c>RunManager.OnEnded</c>, so no run outcome is
/// stamped and no <c>RunEnded</c> fires.
///
/// The run record rewinds with the game. A restarted <i>combat</i> would be fine
/// on its own — this is the Continue path, so <c>RunStarted</c> re-fires with
/// the same <c>_startTime</c>, <c>RunTracker.OnRunStarted</c> adopts the
/// existing record, and the adopt already discards <c>_pendingCombat</c>, so the
/// abandoned fight is never promoted. Shops and events get no such protection:
/// gold spent, cards bought, relics taken and HP traded are committed as they
/// happen, so replaying the room without rewinding the record would bank them
/// twice — in the exact stats the room exists to produce, per-relic attribution
/// included. <c>RunTracker.RollBackToRoomEntry</c> restores the snapshot taken
/// at the same point the save on disk describes: at room entry, and retaken
/// whenever the game rewrites the save with a pre-finished room — after the won
/// fight is promoted, or once the Ancient's choice has landed — so a reward
/// screen replay keeps the fight in the record just as the game keeps it.
/// The snapshot is mirrored to disk, so a hot reload mid-room keeps it; when
/// none exists <see cref="Describe"/> refuses — refusing beats quietly
/// inflating or erasing part of the run.
/// </summary>
public static class RoomResetter
{
    // The game's own transitions default to 0.8s each, which is right for a
    // deliberate main-menu Continue and far too slow for a retry the player
    // wants to feel instant. These are not zero because the run scene is torn
    // down and rebuilt in between: an uncovered swap shows a frame or two of
    // half-destroyed scene. Short enough to read as a blink, long enough to
    // hide the rebuild.
    private const float FadeOutSeconds = 0.12f;
    private const float FadeInSeconds = 0.18f;

    private static bool _restartInProgress;

    /// <summary>
    /// Whether a restart can run right now and what it would replay. Read on
    /// every menu open, so the row explains itself rather than silently doing
    /// nothing.
    /// </summary>
    public static RoomRestartAvailability Describe()
    {
        try
        {
            if (_restartInProgress) return Blocked("already restarting");

            var run = RunManager.Instance;
            if (run == null || !run.IsInProgress) return Blocked("no run in progress");
            if (run.IsCleaningUp) return Blocked("run is shutting down");

            // SetUpSavedSingleplayer is singleplayer-only; the multiplayer
            // equivalent needs a LoadRunLobby we have no way to rebuild here.
            if (run.NetService == null || run.NetService.Type != NetGameType.Singleplayer)
                return Blocked("singleplayer only");

            if (SaveManager.Instance?.HasRunSave != true) return Blocked("no run save on disk");

            // Without a rewind snapshot the run record cannot rewind with the
            // game, and a shop or event would bank what it did here twice.
            if (!RunTracker.HasRoomEntrySnapshot) return Blocked("no room-entry snapshot");

            var state = run.State;

            // The base of the room stack, not the top: an event that started a
            // fight has the combat room on top, but the save replays the map
            // point, so what actually comes back is the event.
            var room = state?.BaseRoom;
            if (state == null || room == null) return Blocked("between rooms");

            bool combatRunning = CombatManager.Instance?.IsInProgress == true;

            switch (room)
            {
                case CombatRoom:
                    // Victory rewrites the save with the room marked
                    // pre-finished, so replaying lands on the reward screen
                    // rather than at the top of the fight. That is a place
                    // worth going back to, so offer it and say where it goes.
                    return combatRunning
                        ? Available("combat", FromTheStart)
                        : Available("combat rewards", ToTheRewardScreen);

                case MerchantRoom:
                    // Merchants never save. The whole visit is replayable.
                    return Available("shop", FromTheStart);

                // Rest sites and treasure rooms write no save either, exactly
                // like merchants, so the same replay works for them.
                case RestSiteRoom:
                    return Available("rest site", FromTheStart);

                case TreasureRoom:
                    return Available("treasure room", FromTheStart);

                case EventRoom eventRoom:
                    // Ancient events are the one event kind that saves on
                    // completion (EventRoom.OnEventStateChanged), and an event
                    // option that started a fight leaves that fight's victory
                    // save behind. Both replay to just after the thing
                    // resolved rather than to the room's start.
                    if (eventRoom.IsPreFinished)
                        return Available("event", AfterItResolved);

                    if (FoughtAtCurrentMapPoint(state) && !combatRunning)
                        return Available("event rewards", ToTheRewardScreen);

                    return Available("event", FromTheStart);

                default:
                    // Every room type the game has is handled above, so this is
                    // the map itself, where there is no room to replay.
                    return Blocked("no room to restart");
            }
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"RoomResetter: availability check failed: {e}");
            return Blocked("unavailable");
        }
    }

    public static bool CanRestart => Describe().CanRestart;

    /// <summary>
    /// Fire-and-forget entry point. Returns false without touching the live run
    /// if a restart is not currently possible.
    /// </summary>
    public static bool Request(string source)
    {
        var availability = Describe();
        if (!availability.CanRestart)
        {
            CoreMain.Logger.Info(
                $"RoomResetter: restart refused ({availability.BlockedReason}, source={source})");
            return false;
        }

        _restartInProgress = true;
        TaskHelper.RunSafely(RestartAsync(availability.RoomNoun!, source));
        return true;
    }

    private const string FromTheStart =
        "replays it from the start, undoing everything you did here";
    private const string ToTheRewardScreen =
        "replays the reward screen, so you can pick again";
    private const string AfterItResolved =
        "replays from just after it resolved";

    private static RoomRestartAvailability Available(string roomNoun, string replayNote)
        => new(roomNoun, null, replayNote);

    private static RoomRestartAvailability Blocked(string reason) => new(null, reason);

    /// <summary>
    /// Whether a combat room has been entered at the map point the player is
    /// standing on. True for a plain fight, and for an event that pushed a
    /// combat room on top of itself.
    /// </summary>
    private static bool FoughtAtCurrentMapPoint(RunState state)
    {
        var here = state.CurrentMapPointHistoryEntry;
        if (here == null) return false;
        return here.HasRoomOfType(RoomType.Monster)
            || here.HasRoomOfType(RoomType.Elite)
            || here.HasRoomOfType(RoomType.Boss);
    }

    private static async Task RestartAsync(string roomNoun, string source)
    {
        try
        {
            // Read and deserialize BEFORE any teardown. A missing or corrupt
            // save must abort with the live run untouched, not after we have
            // already destroyed it.
            var read = SaveManager.Instance.LoadRunSave();
            if (!read.Success || read.SaveData == null)
            {
                CoreMain.Logger.Error(
                    $"RoomResetter: cannot read run save ({read.Status} {read.ErrorMessage}); live run left alone");
                return;
            }

            var save = read.SaveData;
            var runState = RunState.FromSerializable(save);

            var game = NGame.Instance;
            if (game == null)
            {
                CoreMain.Logger.Error("RoomResetter: NGame.Instance is null; live run left alone");
                return;
            }

            CoreMain.Logger.Info(
                $"RoomResetter: restarting {roomNoun} from run save (source={source}, " +
                $"floor={save.MapPointHistory?.Count}, pre_finished_room={save.PreFinishedRoom?.RoomType.ToString() ?? "none"})");

            // Per-phase timing, because the remaining cost after the fades is
            // the game's own reload work and it is worth knowing which part
            // dominates before trying to cut any of it.
            var total = Stopwatch.StartNew();
            var phase = Stopwatch.StartNew();

            NAudioManager.Instance?.StopMusic();
            await game.Transition.FadeOut(FadeOutSeconds);
            var fadeOutMs = phase.ElapsedMilliseconds; phase.Restart();

            // Rewind SpireLens' own record to the point the save describes —
            // room entry, or just after a won fight / finished Ancient when the
            // save holds a pre-finished room. Deliberately here: every path that can abort with the
            // live run untouched is above, and CleanUp below is the point of no
            // return, so the record and the game commit to the rewind together.
            if (!RunTracker.RollBackToRoomEntry($"{roomNoun} restart"))
            {
                CoreMain.Logger.Error(
                    "RoomResetter: run record rewind failed after the availability check passed; "
                    + "aborting so the record cannot double-count this room");
                await game.Transition.FadeIn(FadeInSeconds);
                return;
            }
            var rollBackMs = phase.ElapsedMilliseconds; phase.Restart();

            // Frees RunManager.State so SetUpSavedSingleplayer will accept the
            // reloaded state, and suppresses any further save of the abandoned
            // attempt on the way out.
            RunManager.Instance.CleanUp();
            var cleanUpMs = phase.ElapsedMilliseconds; phase.Restart();

            // Note: this awaits SaveManager.IncrementNumReloads, which writes
            // the run save to disk before returning.
            await RunManager.Instance.SetUpSavedSingleplayer(runState, save);
            var setUpMs = phase.ElapsedMilliseconds; phase.Restart();

            game.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());

            // Asset preload, NRun scene rebuild, map load, room entry and the
            // normal room intro all happen inside here.
            await game.LoadRun(runState, save.PreFinishedRoom);
            var loadRunMs = phase.ElapsedMilliseconds; phase.Restart();

            await game.Transition.FadeIn(FadeInSeconds);
            var fadeInMs = phase.ElapsedMilliseconds;

            CoreMain.Logger.Info(
                $"RoomResetter: {roomNoun} restart complete in {total.ElapsedMilliseconds}ms " +
                $"(fade_out={fadeOutMs}ms, roll_back={rollBackMs}ms, clean_up={cleanUpMs}ms, " +
                $"set_up_saved={setUpMs}ms, load_run={loadRunMs}ms, fade_in={fadeInMs}ms)");
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"RoomResetter: {roomNoun} restart failed: {e}");
        }
        finally
        {
            _restartInProgress = false;
        }
    }
}
