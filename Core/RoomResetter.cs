using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using MegaCrit.Sts2.Core.Saves.Validation;

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
    string? ReplayNote = null,
    bool UndoesDeath = false)
{
    public bool CanRestart => BlockedReason == null;
}

/// <summary>
/// What dying destroys, kept so the death can be undone. Written by
/// <see cref="RoomResetter.CaptureDeathRestorePoint"/> immediately before the
/// game processes a loss.
/// </summary>
public sealed class DeathRestorePoint
{
    public int ProfileId { get; set; }

    /// <summary>The game's run identity, <c>SerializableRun.StartTime</c>, which also names its <c>{StartTime}.run</c> history entry.</summary>
    public long GameStartTime { get; set; }

    /// <summary>The SpireLens run record whose room-entry snapshot rewinds with the game.</summary>
    public string RunId { get; set; } = "";

    /// <summary><c>current_run.save</c> as it stood when the player died, in the game's own JSON.</summary>
    public string RunSave { get; set; } = "";

    /// <summary>The progress save as it stood just before the loss was recorded into it.</summary>
    public string Progress { get; set; } = "";

    public string RoomNoun { get; set; } = "room";
    public string ReplayNote { get; set; } = "";
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
/// replay would undo nothing — <see cref="Describe"/> refuses instead.
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
/// when the room opened, which is the same instant the run save was written.
/// The snapshot is in memory, so a hot reload mid-room drops it and
/// <see cref="Describe"/> refuses until the next room re-arms it — refusing
/// beats quietly inflating the run.
///
/// Death is the one room ending that destroys the save a restart replays:
/// <c>RunManager.OnEnded</c> deletes <c>current_run.save</c> on a loss. It also
/// records the loss everywhere the game keeps score — <c>progress.save</c>
/// (losses, the win streak reset, playtime, per-card and per-encounter losses,
/// and the score the game-over screen banks) and a <c>{StartTime}.run</c>
/// history entry — and SpireLens stamps its record <c>outcome=loss</c>.
/// <see cref="CaptureDeathRestorePoint"/> runs just before all of that and
/// keeps the run save and the untouched progress, so undoing a death replays
/// the room and reverses every one of those records instead of leaving behind
/// a loss that never happened. The only things it cannot reverse are the ones
/// that already left the machine: the game's run metrics upload, and any Steam
/// achievement unlocked on the way out.
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

            // Dead, whether still on the game-over screen or already back on
            // the main menu: all that is left to replay is what was kept
            // before the game deleted the save.
            if (run == null || !run.IsInProgress || run.IsGameOver)
            {
                var death = FindDeathRestorePoint(run, out var deathBlocked);
                return death == null
                    ? Blocked(deathBlocked)
                    : new RoomRestartAvailability(death.RoomNoun, null, death.ReplayNote, UndoesDeath: true);
            }

            if (run.IsCleaningUp) return Blocked("run is shutting down");

            // SetUpSavedSingleplayer is singleplayer-only; the multiplayer
            // equivalent needs a LoadRunLobby we have no way to rebuild here.
            if (run.NetService == null || run.NetService.Type != NetGameType.Singleplayer)
                return Blocked("singleplayer only");

            if (SaveManager.Instance?.HasRunSave != true) return Blocked("no run save on disk");

            // Without a room-entry snapshot the run record cannot rewind with
            // the game, and a shop or event would bank what it did here twice.
            // The snapshot is in-memory, so a hot reload mid-room drops it; the
            // next room re-arms it.
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
        TaskHelper.RunSafely(RestartAsync(availability.RoomNoun!, availability.UndoesDeath, source));
        return true;
    }

    /// <summary>
    /// Keep what a loss is about to destroy. Called from the
    /// <c>RunManager.OnEnded</c> prefix, which is ahead of every one of its
    /// side effects: the progress update, the history entry (and SpireLens'
    /// <see cref="RunTracker.OnRunEnded"/>, which hangs off it), and the save
    /// deletion.
    /// </summary>
    public static void CaptureDeathRestorePoint(RunManager run, bool isVictory)
    {
        // OnEnded does its bookkeeping once per run; a repeat call changes
        // nothing and must not replace the real pre-death capture with a
        // post-death one.
        if (isVictory || run._runHistoryWasUploaded) return;
        if (!run.ShouldSave || run.IsAbandoned) return;
        if (run.NetService == null || run.NetService.Type != NetGameType.Singleplayer) return;

        var state = run.State;
        if (state == null) return;

        // Without a rewind target for the run record, restoring the game would
        // leave SpireLens holding a finished run and minting a second record
        // for the replay. Refuse up front, as the live restart does.
        var runId = RunTracker.RunIdWithRoomEntrySnapshot;
        if (runId == null)
        {
            CoreMain.Logger.Info("RoomResetter: death not undoable (no room-entry snapshot)");
            return;
        }

        var read = SaveManager.Instance.LoadRunSave();
        if (!read.Success || read.SaveData == null)
        {
            CoreMain.Logger.Info(
                $"RoomResetter: death not undoable (run save unreadable: {read.Status} {read.ErrorMessage})");
            return;
        }

        var save = read.SaveData;
        var progress = SaveManager.Instance.Progress.ToSerializable();
        progress.SchemaVersion = SaveManager.Instance.GetLatestSchemaVersion<SerializableProgress>();
        var (noun, note) = DescribeReplayAfterDeath(state, save);

        var point = new DeathRestorePoint
        {
            ProfileId = SaveManager.Instance.CurrentProfileId,
            GameStartTime = save.StartTime,
            RunId = runId,
            RunSave = SaveManager.ToJson(save),
            Progress = SaveManager.ToJson(progress),
            RoomNoun = noun,
            ReplayNote = note,
        };
        RunStorage.SaveDeathRestorePoint(point);
        _deathRestorePoint = point;
        _deathRestorePointLoaded = true;

        CoreMain.Logger.Info(
            $"RoomResetter: kept pre-death save for {noun} (run={runId}, game_start_time={save.StartTime})");
    }

    private static DeathRestorePoint? _deathRestorePoint;
    private static bool _deathRestorePointLoaded;

    /// <summary>
    /// The kept pre-death state, if it still describes the most recent thing
    /// that happened. A later run (a run save on disk, or a newer history
    /// entry) or another profile makes it stale, and a stale one is deleted:
    /// restoring it would roll progress back over runs played since.
    /// </summary>
    private static DeathRestorePoint? FindDeathRestorePoint(RunManager? run, out string blocked)
    {
        blocked = run?.IsGameOver == true ? "nothing kept from before this death" : "no run in progress";
        if (run?.IsCleaningUp == true)
        {
            blocked = "run is shutting down";
            return null;
        }

        if (!_deathRestorePointLoaded)
        {
            _deathRestorePoint = RunStorage.LoadDeathRestorePoint();
            _deathRestorePointLoaded = true;
        }

        var point = _deathRestorePoint;
        if (point == null) return null;

        var saves = SaveManager.Instance;
        if (saves.CurrentProfileId != point.ProfileId) return null;

        // On the game-over screen the dead run is still loaded, and it has to
        // be the run this point was kept for.
        if (run?.IsInProgress == true && run._startTime != point.GameStartTime) return null;

        bool superseded = saves.HasRunSave
            || saves.GetAllRunHistoryNames().Any(name =>
                long.TryParse(Path.GetFileNameWithoutExtension(name), out var startTime)
                && startTime > point.GameStartTime);
        if (superseded)
        {
            ForgetDeathRestorePoint();
            return null;
        }

        return point;
    }

    private static void ForgetDeathRestorePoint()
    {
        RunStorage.DeleteDeathRestorePoint();
        _deathRestorePoint = null;
        _deathRestorePointLoaded = true;
    }

    /// <summary>
    /// What replaying <paramref name="save"/> brings back, judged from the room
    /// the player died in. A death has no live combat to consult, so where the
    /// replay lands comes from the save itself.
    /// </summary>
    private static (string Noun, string Note) DescribeReplayAfterDeath(RunState state, SerializableRun save)
    {
        bool savedAfterAFight = save.PreFinishedRoom != null;
        return state.BaseRoom switch
        {
            CombatRoom => savedAfterAFight
                ? ("combat rewards", ToTheRewardScreen)
                : ("combat", FromTheStart),
            EventRoom { IsPreFinished: true } => ("event", AfterItResolved),
            EventRoom => savedAfterAFight
                ? ("event rewards", ToTheRewardScreen)
                : ("event", FromTheStart),
            _ => ("room", FromTheStart),
        };
    }

    /// <summary>
    /// Reverse the game's own record of the death: put the progress save back
    /// as it was before the loss was counted, and remove the history entry the
    /// loss wrote. The run writes that entry again when it really ends.
    /// </summary>
    private static void UndoRecordedDeath(DeathRestorePoint point, ProgressState progressBeforeDeath)
    {
        var saves = SaveManager.Instance;
        saves.Progress = progressBeforeDeath;
        saves.SaveProgressFile();

        var history = saves._runHistorySaveManager;
        var entry = Path.Combine(history.HistoryPath, $"{point.GameStartTime}.run");
        if (history._saveStore.FileExists(entry))
            history._saveStore.DeleteFile(entry);
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

    private static async Task RestartAsync(string roomNoun, bool undoesDeath, string source)
    {
        try
        {
            // Read and deserialize BEFORE any teardown. A missing or corrupt
            // save must abort with the live run untouched, not after we have
            // already destroyed it.
            SerializableRun save;
            DeathRestorePoint? death = null;
            ProgressState? progressBeforeDeath = null;
            if (undoesDeath)
            {
                death = FindDeathRestorePoint(RunManager.Instance, out var blocked);
                if (death == null)
                {
                    CoreMain.Logger.Error($"RoomResetter: pre-death save gone ({blocked}); nothing changed");
                    return;
                }

                var keptRun = SaveManager.FromJson<SerializableRun>(death.RunSave);
                var keptProgress = SaveManager.FromJson<SerializableProgress>(death.Progress);
                if (!keptRun.Success || keptRun.SaveData == null
                    || !keptProgress.Success || keptProgress.SaveData == null)
                {
                    CoreMain.Logger.Error(
                        $"RoomResetter: pre-death save unreadable (run={keptRun.Status}, progress={keptProgress.Status}); nothing changed");
                    return;
                }

                save = keptRun.SaveData;
                progressBeforeDeath = ProgressState.FromSerializable(keptProgress.SaveData, new DeserializationContext());
            }
            else
            {
                var read = SaveManager.Instance.LoadRunSave();
                if (!read.Success || read.SaveData == null)
                {
                    CoreMain.Logger.Error(
                        $"RoomResetter: cannot read run save ({read.Status} {read.ErrorMessage}); live run left alone");
                    return;
                }

                save = read.SaveData;
            }

            var runState = RunState.FromSerializable(save);

            var game = NGame.Instance;
            if (game == null)
            {
                CoreMain.Logger.Error("RoomResetter: NGame.Instance is null; live run left alone");
                return;
            }

            CoreMain.Logger.Info(
                $"RoomResetter: restarting {roomNoun} from {(death != null ? "pre-death save" : "run save")} (source={source}, " +
                $"floor={save.MapPointHistory?.Count}, pre_finished_room={save.PreFinishedRoom?.RoomType.ToString() ?? "none"})");

            // Per-phase timing, because the remaining cost after the fades is
            // the game's own reload work and it is worth knowing which part
            // dominates before trying to cut any of it.
            var total = Stopwatch.StartNew();
            var phase = Stopwatch.StartNew();

            NAudioManager.Instance?.StopMusic();
            await game.Transition.FadeOut(FadeOutSeconds);
            var fadeOutMs = phase.ElapsedMilliseconds; phase.Restart();

            // Rewind SpireLens' own record to the same instant the save was
            // written. Deliberately here: every path that can abort with the
            // live run untouched is above, and CleanUp below is the point of no
            // return, so the record and the game commit to the rewind together.
            bool rewound = death != null
                ? RunTracker.RollBackEndedRunToRoomEntry(death.RunId, $"{roomNoun} restart after death")
                : RunTracker.RollBackToRoomEntry($"{roomNoun} restart");
            if (!rewound)
            {
                CoreMain.Logger.Error(
                    "RoomResetter: run record rewind failed after the availability check passed; "
                    + "aborting so the record cannot double-count this room");
                await game.Transition.FadeIn(FadeInSeconds);
                return;
            }
            // The game's own records of the death go back at the same commit
            // point as the run record.
            if (death != null)
                UndoRecordedDeath(death, progressBeforeDeath!);
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

            // SetUpSavedSingleplayer has written the run save back to disk, so
            // the death is undone for good; any later restart is the ordinary
            // kind.
            if (death != null)
                ForgetDeathRestorePoint();

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
