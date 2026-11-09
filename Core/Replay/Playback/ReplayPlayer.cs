using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace SpireLens.Core.Replay.Playback;

/// <summary>
/// Full-run replay: SpireLens drives the real game through recorded runs
/// (docs/replay-player.md). Plays a queue of runs — one for a single replay,
/// many for a playlist — each from its first snapshot, feeding the canonical
/// timeline's player inputs through <see cref="ReplayInputFeeder"/> at a
/// watchable pace, checking every game save point against the recording's
/// key, and resyncing from the recording's snapshot on divergence.
///
/// The game runs with its own switches set so it writes nothing: the run is
/// set up like <c>RunManager.SetUpSavedSingleplayer</c> but with
/// <c>shouldSave: false</c>, no <c>IncrementNumReloads</c> and no daily time.
/// <see cref="ReplayDataGuard"/> backs up once before the first run and
/// restores once when the whole queue ends or is exited, and
/// <see cref="ReplayPlaybackGuard"/> keeps SpireLens's own tracking,
/// recording and import away from the replayed runs.
///
/// Save points arrive through the existing <c>RunSaveSnapshotPatch</c>
/// prefix, checksums through the public
/// <c>ChecksumTracker.ChecksumGenerated</c> event, selections through the
/// game's <c>CardSelectCmd</c> local-selector seam, input through the existing
/// <c>StatsVisibilityHotkeyPatch</c> input postfix. Floor jumps go through the
/// game's map (<see cref="ReplayMapFloorPicker"/>, whose one patch acts only
/// while it is open).
/// </summary>
public static class ReplayPlayer
{
    private enum Request { None, RestartFloor, JumpToRoom, NextAct, PreviousAct, NextRun, PreviousRun, RestartRun, Stop }

    internal static readonly double[] Speeds = { 0.5, 1, 2, 4, double.PositiveInfinity };
    private const int DefaultSpeedIndex = 1;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan BetweenRuns = TimeSpan.FromSeconds(4);

    private sealed record QueuedRun(PlaylistRunRef Ref, ReplayListing? Listing)
    {
        public string Label => !string.IsNullOrWhiteSpace(Ref.Label) ? Ref.Label! : Listing?.Label ?? Ref.GameStartTime.ToString();
    }

    private static bool _active;
    private static bool _stopping;
    private static int _generation;

    // Queue (playlist) state.
    private static List<QueuedRun> _queue = new();
    private static int _runIndex = -1;
    private static string? _playlistName;
    private static DateTime? _advanceAt;

    // Current run state.
    private static ReplayPlaybackPlan? _plan;
    private static string _dir = "";
    private static int _pos;
    private static bool _finished;
    private static int _lastResyncKeyframe = -1;
    private static int _resyncRepeats;
    private static int _divergences;

    private static int _speedIndex = DefaultSpeedIndex;
    private static bool _paused;
    private static bool _stepOnce;
    private static Request _request;
    private static RecordedRoom? _jumpTarget;
    private static bool _reopenPickerAfterLoad;
    private static string? _note;
    private static DateTime _noteUntil;

    private static ReplayCardSelector? _selector;
    private static IDisposable? _selectorScope;
    private static ChecksumTracker? _checksumSource;
    private static readonly Queue<string> ObservedSaves = new();
    private static readonly Queue<uint> ExpectedChecksums = new();
    private static readonly Queue<(uint Value, string Context)> LiveChecksums = new();
    private static int _checksumMismatches;

    public static bool IsActive => _active;

    // -------- Lifecycle --------

    /// <summary>
    /// CoreMain.Initialize: if a replay was interrupted (crash, game closed,
    /// Core hot reload mid-replay) its data backup is still there. Keep
    /// SpireLens tracking suppressed, leave the orphaned replay run if one is
    /// still live, and restore the user's data.
    /// </summary>
    public static void OnCoreInitialize()
    {
        try
        {
            if (!ReplayDataGuard.MarkerExists) return;
            ReplayPlaybackGuard.SetMarkerPresent(true);
            var rm = RunManager.Instance;
            // A playback run never saves; a run the user is playing does. A
            // playlist's live run need not be the run the marker names.
            if (rm != null && rm.IsInProgress && (!rm.ShouldSave || rm._startTime == ReplayDataGuard.MarkerGameStartTime()))
            {
                CoreMain.Logger.Warn("Replay: a replay was still running across a Core reload; returning to the main menu and restoring data");
                TaskHelper.RunSafely(LeaveOrphanedReplayAsync());
            }
            else if (rm == null || !rm.IsInProgress)
            {
                ReplayDataGuard.Restore("interrupted replay found at Core load");
            }
            else
            {
                CoreMain.Logger.Warn("Replay: data backup present but a different run is live; restore deferred to the main menu");
            }
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"Replay: interrupted-replay recovery failed: {e}");
        }
    }

    private static async Task LeaveOrphanedReplayAsync()
    {
        try { await NGame.Instance.ReturnToMainMenu(); }
        catch (Exception e) { CoreMain.Logger.Error($"Replay: leaving orphaned replay failed: {e.Message}"); }
        ReplayDataGuard.Restore("orphaned replay after Core reload");
    }

    /// <summary>
    /// CoreMain.Shutdown: stop feeding and release everything the game holds
    /// back to this assembly (the selector on the game's stack, event
    /// subscriptions, UI, time scale). The data backup stays: the reloaded
    /// Core's <see cref="OnCoreInitialize"/> leaves the replay and restores.
    /// </summary>
    public static void ShutdownForReload()
    {
        if (!_active) return;
        _generation++;
        _active = false;
        ReplayMapFloorPicker.Close();
        ReleaseGameHooks();
        ReplayPlaybackBar.Destroy();
        ReplayPlaybackGuard.SetPlayerActive(false);
        ReplayPlaybackGuard.SetMarkerPresent(ReplayDataGuard.MarkerExists);
        CoreMain.Logger.Warn("Replay: Core shutting down mid-replay; the reloaded Core will exit it and restore data");
    }

    /// <summary>Why a replay can't start right now, or null.</summary>
    public static string? StartBlockedReason()
    {
        if (_active) return "a replay is already playing";
        if (RunManager.Instance?.IsInProgress == true) return "return to the main menu first";
        if (NGame.Instance == null) return "game not ready";
        return null;
    }

    /// <summary>Play one recording.</summary>
    public static void Start(ReplayListing listing) =>
        TaskHelper.RunSafely(StartQueueAsync(null, new List<QueuedRun>
        {
            new(new PlaylistRunRef(listing.GameStartTime, null), listing),
        }));

    /// <summary>Play a saved playlist's runs back to back.</summary>
    public static void StartPlaylist(ReplayPlaylist playlist) => StartRuns(playlist.Name, playlist.Runs);

    /// <summary>Play these runs back to back (a playlist, or an inline play-now list).</summary>
    public static void StartRuns(string? name, IEnumerable<PlaylistRunRef> runs) =>
        TaskHelper.RunSafely(StartQueueAsync(
            name,
            ReplayPlaylistLibrary.Resolve(runs).Select(r => new QueuedRun(r.Ref, r.Listing)).ToList()));

    /// <summary>
    /// Start a queue. The data guard covers the whole queue: one backup now,
    /// one restore when the queue ends or is exited. Nothing between runs
    /// writes user data — every run is a playback run under the same guard —
    /// so a backup per run would only re-copy identical files, and restoring
    /// between runs would need a trip through the main menu for nothing.
    /// </summary>
    private static async Task StartQueueAsync(string? name, List<QueuedRun> queue)
    {
        var blocked = StartBlockedReason();
        if (blocked != null)
        {
            CoreMain.Logger.Warn($"Replay: not starting ({blocked})");
            Toast($"Replay not started: {blocked}");
            return;
        }
        var first = queue.FindIndex(q => q.Listing is { Playable: true });
        if (first < 0)
        {
            CoreMain.Logger.Warn($"Replay: nothing playable in {(name ?? "the request")} ({queue.Count} run(s))");
            Toast($"Nothing playable in {(name ?? "that list")}");
            return;
        }

        if (ReplayDataGuard.MarkerExists) ReplayDataGuard.Restore("before starting a new replay");
        try
        {
            ReplayDataGuard.Backup(queue[first].Ref.GameStartTime);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"Replay: data backup failed, not starting: {e.Message}");
            Toast("Replay not started: data backup failed (see log)");
            return;
        }

        _active = true;
        _stopping = false;
        _generation++;
        _queue = queue;
        _playlistName = name;
        _runIndex = -1;
        _advanceAt = null;
        _speedIndex = DefaultSpeedIndex;
        _paused = false;
        _stepOnce = false;
        _request = Request.None;
        _jumpTarget = null;
        _reopenPickerAfterLoad = false;
        ReplayPlaybackGuard.SetPlayerActive(true);
        ReplayPlaybackGuard.SetMarkerPresent(true);
        RunTracker.SuspendForPlayback();

        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null) ReplayPlaybackBar.Build(tree);
        ApplySpeed();
        CoreMain.Logger.Info($"Replay: starting {(name != null ? $"playlist '{name}'" : "replay")} with {queue.Count} run(s)");

        int generation = _generation;
        try
        {
            if (!await BeginRunAsync(first, +1))
            {
                await StopAsync("no run could be loaded");
                return;
            }
            await RunLoopAsync(generation, tree);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"Replay: playback failed: {e}");
            if (generation == _generation) await StopAsync("playback error");
        }
    }

    /// <summary>
    /// Load the queue's run at <paramref name="index"/>, or the nearest
    /// playable one in <paramref name="direction"/>. Missing, other-build and
    /// unreadable recordings are skipped with a log line and a note.
    /// </summary>
    private static async Task<bool> BeginRunAsync(int index, int direction)
    {
        for (int i = index; i >= 0 && i < _queue.Count; i += direction)
        {
            var run = _queue[i];
            if (run.Listing == null)
            {
                Skip(i, "recording missing");
                continue;
            }
            if (!run.Listing.Playable)
            {
                Skip(i, run.Listing.DisabledReason ?? "not playable");
                continue;
            }

            ReplayPlaybackPlan plan;
            SerializableRun genesis;
            try
            {
                plan = ReplayPlaybackPlan.Build(ReadEventLines(Path.Combine(run.Listing.Directory, "events.jsonl")));
                int start = plan.StartKeyframeIndex;
                if (start < 0) throw new InvalidOperationException("recording has no save point to start from");
                genesis = ReadSnapshot(run.Listing.Directory, plan.Steps[start].Snapshot)
                          ?? throw new InvalidOperationException("start snapshot unreadable");
            }
            catch (Exception e)
            {
                Skip(i, e.Message);
                continue;
            }

            _runIndex = i;
            _plan = plan;
            _dir = run.Listing.Directory;
            _finished = false;
            _advanceAt = null;
            _divergences = 0;
            _lastResyncKeyframe = -1;
            _resyncRepeats = 0;

            int startIndex = plan.StartKeyframeIndex;
            CoreMain.Logger.Info(
                $"Replay: run {i + 1}/{_queue.Count} {run.Ref.GameStartTime} ({plan.InputCount} inputs, "
                + $"{plan.Steps.Count(s => s.Kind == PlaybackStepKind.Keyframe)} keyframes, {plan.Branches.Count} abandoned branches, "
                + $"{plan.SkippedConsoleCommands} console commands skipped)");
            if (run.Listing.StartedMidRun)
                ShowNote($"Recording began mid-run: starting at floor {plan.Steps[startIndex].RoomFloor}");

            await LoadIntoGameAsync(genesis);
            _pos = startIndex + 1;
            return true;
        }
        return false;
    }

    private static void Skip(int index, string why)
    {
        var run = _queue[index];
        CoreMain.Logger.Warn($"Replay: skipping run {index + 1}/{_queue.Count} ({run.Ref.GameStartTime}): {why}");
        ShowNote($"Skipped run {index + 1}/{_queue.Count}: {why}");
    }

    /// <summary>Exit playback (the whole queue): back to the main menu, user data restored.</summary>
    public static void RequestStop()
    {
        if (!_active) return;
        _request = Request.Stop;
    }

    private static async Task StopAsync(string reason)
    {
        if (!_active || _stopping) return;
        _stopping = true;
        _generation++;
        CoreMain.Logger.Info($"Replay: stopping ({reason})");

        ReplayMapFloorPicker.Close();
        ReleaseGameHooks();
        ReplayPlaybackBar.Destroy();

        try
        {
            if (RunManager.Instance.IsInProgress)
            {
                NAudioManager.Instance?.StopMusic();
                await NGame.Instance.ReturnToMainMenu();
            }
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"Replay: returning to the main menu failed: {e.Message}");
        }

        ReplayDataGuard.Restore($"replay ended: {reason}");
        _active = false;
        _stopping = false;
        _plan = null;
        _queue = new List<QueuedRun>();
        _runIndex = -1;
        ReplayPlaybackGuard.SetPlayerActive(false);
        // A play-now request that arrived during this replay can start now.
        ReplayPlayNowWatcher.OnReplayEnded();
    }

    private static void ReleaseGameHooks()
    {
        try { _selector?.Abandon(); } catch { }
        try { _selectorScope?.Dispose(); } catch { }
        _selectorScope = null;
        _selector = null;
        try { if (_checksumSource != null) _checksumSource.ChecksumGenerated -= OnChecksum; } catch { }
        _checksumSource = null;
        try { Engine.TimeScale = 1.0; } catch { }
    }

    private static void Toast(string message)
    {
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree) HotReloadToast.Show(tree, message);
        }
        catch { }
    }

    // -------- Loading a snapshot --------

    // The recorder keeps the run's events.jsonl open for append until the next
    // run starts, so the run that just ended must be read with a share mode
    // that tolerates that writer; File.ReadLines refuses it.
    private static IEnumerable<string> ReadEventLines(string path)
    {
        using var file = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static SerializableRun? ReadSnapshot(string dir, string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return null;
        string path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var read = SaveManager.FromJson<SerializableRun>(reader.ReadToEnd());
        return read.Success ? read.SaveData : null;
    }

    /// <summary>
    /// Put the game at a recorded save: the main menu's Continue sequence
    /// (as RoomResetter uses it), except the run is set up for playback —
    /// <see cref="SetUpForPlayback"/> instead of SetUpSavedSingleplayer.
    /// </summary>
    private static async Task LoadIntoGameAsync(SerializableRun save)
    {
        var game = NGame.Instance ?? throw new InvalidOperationException("NGame not ready");
        ReplayMapFloorPicker.Close();
        // Release a waiting selection and take our selector off the game's
        // stack before CleanUp resets that stack; it is reinstalled after load.
        _selector?.Abandon();
        try { _selectorScope?.Dispose(); } catch { }
        _selectorScope = null;
        NAudioManager.Instance?.StopMusic();
        await game.Transition.FadeOut(0.15f);

        var runState = RunState.FromSerializable(save);
        if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
        SetUpForPlayback(runState, save);
        game.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
        await game.LoadRun(runState, save.PreFinishedRoom);

        // CleanUp resets CardSelectCmd's selector stacks; (re)install ours.
        InstallGameHooks();
        ObservedSaves.Clear();
        ResetChecksums();
        await game.Transition.FadeIn(0.2f);
    }

    /// <summary>
    /// <c>RunManager.SetUpSavedSingleplayer</c> without its writes: the same
    /// InitializeShared / InitializeRunLobby / InitializeSavedRun sequence, but
    /// <c>shouldSave: false</c> (the game then writes no run save, no run
    /// history, no end-of-run progress, stats, metrics or leaderboard), no
    /// <c>SaveManager.IncrementNumReloads</c> (which would rewrite
    /// current_run.save), and no daily time (no daily score upload).
    /// </summary>
    private static void SetUpForPlayback(RunState state, SerializableRun save)
    {
        var rm = RunManager.Instance;
        if (rm.State != null) throw new InvalidOperationException("RunManager state is already set");
        rm.State = state;
        var net = new NetSingleplayerGameService();
        rm.InitializeShared(net, new PeerInputSynchronizer(net), shouldSave: false, dailyTime: null,
            save.StartTime, save.RunTime, save.WinTime, save.NumReloads);
        // The installed game's RunLobbyPlayer carries an isModded flag that the
        // checked-in reference sts2.dll (what CI compiles against) predates,
        // so set it reflectively. SpireLens is itself a mod, so the running
        // game is always modded. The struct is boxed so SetValue sticks.
        object lobbyPlayer = new RunLobbyPlayer { id = net.NetId };
        typeof(RunLobbyPlayer).GetField("isModded")?.SetValue(lobbyPlayer, true);
        rm.InitializeRunLobby(net, state, new[] { (RunLobbyPlayer)lobbyPlayer });
        rm.InitializeSavedRun(save);
        if (rm.ShouldSave) throw new InvalidOperationException("playback run would save; refusing");
    }

    private static void InstallGameHooks()
    {
        try { _selectorScope?.Dispose(); } catch { }
        _selector = new ReplayCardSelector();
        _selectorScope = CardSelectCmd.PushSelector(_selector, localOnly: true);

        try { if (_checksumSource != null) _checksumSource.ChecksumGenerated -= OnChecksum; } catch { }
        _checksumSource = RunManager.Instance.ChecksumTracker;
        if (_checksumSource != null) _checksumSource.ChecksumGenerated += OnChecksum;
    }

    private static async Task LoadKeyframeAsync(int index, string why)
    {
        var plan = _plan!;
        var step = plan.Steps[index];
        var save = ReadSnapshot(_dir, step.Snapshot)
                   ?? throw new InvalidOperationException($"snapshot {step.Snapshot} unreadable");
        CoreMain.Logger.Info($"Replay: loading keyframe seq {step.Seq} (floor {step.RoomFloor}, {step.Snapshot}): {why}");
        _finished = false;
        _advanceAt = null;
        await LoadIntoGameAsync(save);
        _pos = index + 1;
    }

    // -------- The driver loop --------

    private static async Task RunLoopAsync(int generation, SceneTree? tree)
    {
        var wait = new Stopwatch();
        var paceUntil = DateTime.MinValue;

        while (generation == _generation)
        {
            if (tree != null) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            else await Task.Delay(16);
            if (generation != _generation) return;

            try
            {
                if (await HandleRequestAsync()) { wait.Reset(); continue; }
                if (generation != _generation) return;
                UpdateBar();

                // The map is open for picking a floor: nothing is fed and no
                // wait runs down until it closes.
                if (ReplayMapFloorPicker.IsOpen) { wait.Reset(); continue; }

                var plan = _plan;
                if (plan == null) continue;

                if (_finished)
                {
                    // Between runs of a queue: advance after a short pause (held while paused).
                    if (_advanceAt.HasValue && !_paused && DateTime.UtcNow >= _advanceAt.Value)
                    {
                        _advanceAt = null;
                        await AdvanceRunAsync(+1, "next run");
                        wait.Reset();
                    }
                    continue;
                }

                if (_pos >= plan.Steps.Count)
                {
                    Finish("end of recording");
                    continue;
                }

                var step = plan.Steps[_pos];
                switch (step.Kind)
                {
                    case PlaybackStepKind.Checksum:
                        if (TryGetChecksum(step, out uint expected)) ExpectedChecksums.Enqueue(expected);
                        CompareChecksums();
                        _pos++;
                        continue;

                    case PlaybackStepKind.RunEnd:
                        Finish("the recorded run ended here");
                        continue;

                    case PlaybackStepKind.Keyframe:
                    {
                        if (ObservedSaves.Count == 0)
                        {
                            if (!wait.IsRunning) wait.Restart();
                            if (wait.Elapsed < StepTimeout) continue;
                            wait.Reset();
                            await DivergeAsync(_pos, $"the game never reached save point seq {step.Seq} (floor {step.RoomFloor})");
                            continue;
                        }
                        wait.Reset();
                        string live = ObservedSaves.Dequeue();
                        if (live == step.Key)
                        {
                            ResetChecksums();
                            _pos++;
                            continue;
                        }
                        await DivergeAsync(_pos, $"save point seq {step.Seq} differs: recorded {step.Key}, replay {live}");
                        continue;
                    }

                    case PlaybackStepKind.Input:
                    {
                        if (_paused && !_stepOnce) { wait.Reset(); continue; }
                        if (DateTime.UtcNow < paceUntil) continue;

                        ReplayInputFeeder.FeedResult result;
                        try
                        {
                            result = _selector == null
                                ? ReplayInputFeeder.FeedResult.NotReady
                                : ReplayInputFeeder.TryFeed(step, _selector);
                        }
                        catch (Exception e)
                        {
                            CoreMain.Logger.Warn($"Replay: feeding seq {step.Seq} threw: {e.Message}");
                            result = ReplayInputFeeder.FeedResult.Impossible;
                        }
                        if (result == ReplayInputFeeder.FeedResult.Fed)
                        {
                            wait.Reset();
                            _stepOnce = false;
                            _pos++;
                            paceUntil = DateTime.UtcNow + PaceAfter(step.InputKind);
                            continue;
                        }

                        if (result == ReplayInputFeeder.FeedResult.NotReady)
                        {
                            if (!wait.IsRunning) wait.Restart();
                            if (wait.Elapsed < StepTimeout) continue;
                        }
                        wait.Reset();
                        int next = plan.NextKeyframeIndex(_pos);
                        await DivergeAsync(next,
                            $"input seq {step.Seq} ({step.InputKind}) could not be fed: {ReplayInputFeeder.LastReason}");
                        continue;
                    }
                }
            }
            catch (Exception e)
            {
                // A run that can't continue (an unreadable snapshot, a load that
                // throws) ends; a queue moves on to its next run.
                CoreMain.Logger.Error($"Replay: run {_runIndex + 1}/{_queue.Count} failed: {e}");
                ShowNote("This run could not continue");
                Finish("error");
                wait.Reset();
            }
        }
    }

    /// <summary>The current run is over (end of recording, run end, or unrecoverable).</summary>
    private static void Finish(string why)
    {
        if (_finished) return;
        _finished = true;
        CoreMain.Logger.Info($"Replay: run {_runIndex + 1}/{_queue.Count} finished ({why}); {_divergences} divergence(s)");

        bool more = _queue.Skip(_runIndex + 1).Any(q => q.Listing is { Playable: true });
        if (more)
        {
            double speed = Speeds[_speedIndex];
            var delay = double.IsInfinity(speed) ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(BetweenRuns.TotalSeconds / Math.Max(1, speed));
            _advanceAt = DateTime.UtcNow + delay;
            ShowNote($"Run {_runIndex + 1}/{_queue.Count} finished ({why}); next run shortly. N skips ahead, Esc exits.");
        }
        else
        {
            _advanceAt = null;
            ShowNote(_queue.Count > 1
                    ? $"Playlist finished ({_queue.Count} runs). Esc exits."
                    : $"Replay finished ({why}). Esc exits.",
                TimeSpan.FromDays(1));
        }
    }

    /// <summary>Move to the next / previous playable run of the queue; at either end, stay.</summary>
    private static async Task AdvanceRunAsync(int direction, string why)
    {
        int target = _runIndex + direction;
        bool exists = direction > 0
            ? _queue.Skip(target).Any(q => q.Listing is { Playable: true })
            : _queue.Take(Math.Max(0, target + 1)).Any(q => q.Listing is { Playable: true });
        if (!exists)
        {
            ShowNote(direction > 0 ? "No later run in this list" : "No earlier run in this list");
            return;
        }
        CoreMain.Logger.Info($"Replay: {why}");
        if (!await BeginRunAsync(target, direction))
            ShowNote("No playable run there");
    }

    /// <summary>
    /// Divergence: log it, reload the recording's snapshot at keyframe
    /// <paramref name="keyframeIndex"/> and continue after it. A keyframe that
    /// diverges twice in a row is stepped over to the next one.
    /// </summary>
    private static async Task DivergeAsync(int keyframeIndex, string detail)
    {
        _divergences++;
        CoreMain.Logger.Warn($"Replay: divergence #{_divergences}: {detail}");
        var plan = _plan!;

        if (keyframeIndex == _lastResyncKeyframe && ++_resyncRepeats >= 2)
            keyframeIndex = plan.NextKeyframeIndex(keyframeIndex + 1);
        else if (keyframeIndex != _lastResyncKeyframe)
            _resyncRepeats = 0;

        if (keyframeIndex < 0)
        {
            ShowNote("Replay diverged and no later save point exists to resync from.");
            Finish("diverged past the last save point");
            return;
        }

        _lastResyncKeyframe = keyframeIndex;
        ShowNote($"Desync at floor {plan.Steps[keyframeIndex].RoomFloor}: resynced from the recording");
        await LoadKeyframeAsync(keyframeIndex, "resync after divergence");
    }

    private static async Task<bool> HandleRequestAsync()
    {
        var request = _request;
        if (request == Request.None) return false;
        _request = Request.None;
        var plan = _plan;

        switch (request)
        {
            case Request.Stop:
                await StopAsync("stopped by user");
                return true;

            case Request.NextRun:
                await AdvanceRunAsync(+1, "next run requested");
                return true;

            case Request.PreviousRun:
                await AdvanceRunAsync(-1, "previous run requested");
                return true;

            case Request.RestartRun:
                if (_runIndex < 0) return true;
                if (await BeginRunAsync(_runIndex, +1)) ShowNote($"Restarted run {_runIndex + 1}/{_queue.Count}");
                return true;

            case Request.RestartFloor:
            {
                if (plan == null) return true;
                int idx = plan.CurrentRoomStartIndex(_pos);
                if (idx < 0) { ShowNote("No floor start to restart from"); return true; }
                await LoadKeyframeAsync(idx, "restart floor");
                ShowNote($"Restarted floor {plan.Steps[idx].RoomFloor}");
                return true;
            }

            case Request.JumpToRoom:
            {
                var room = _jumpTarget;
                _jumpTarget = null;
                if (plan == null || room == null) return true;
                await LoadKeyframeAsync(room.KeyframeIndex, $"jump to floor {room.RoomFloor}");
                ShowNote($"Jumped to floor {room.RoomFloor}");
                return true;
            }

            case Request.NextAct:
            case Request.PreviousAct:
            {
                bool reopen = _reopenPickerAfterLoad;
                _reopenPickerAfterLoad = false;
                if (plan == null) return true;
                int direction = request == Request.NextAct ? +1 : -1;
                int? act = plan.AdjacentAct(CurrentAct(), direction);
                if (act is not { } target)
                {
                    ShowNote(direction > 0 ? "No later act in this recording" : "No earlier act in this recording");
                    if (reopen) OpenFloorPicker();
                    return true;
                }
                var first = plan.RoomsInAct(target)[0];
                await LoadKeyframeAsync(first.KeyframeIndex, $"jump to act {target + 1}");
                ShowNote($"Act {target + 1}, floor {first.RoomFloor}");
                if (reopen) OpenFloorPicker();
                return true;
            }
        }
        return false;
    }

    private static int CurrentFloor()
    {
        try { return RunManager.Instance.State?.TotalFloor ?? 0; }
        catch { return 0; }
    }

    private static int CurrentAct()
    {
        try { return RunManager.Instance.State?.CurrentActIndex ?? 0; }
        catch { return 0; }
    }

    // -------- Save points and checksums from the game --------

    /// <summary>
    /// From the existing RunSaveSnapshotPatch prefix on
    /// <c>RunSaveManager.SaveRun(AbstractRoom)</c>, which the game reaches at
    /// every save point even with ShouldSave off (the write itself is skipped
    /// inside). Computes the key of the save the game would have written.
    /// </summary>
    public static void OnGameSavePoint(AbstractRoom? preFinishedRoom)
    {
        if (!_active || _stopping) return;
        try
        {
            var save = RunManager.Instance.ToSave(preFinishedRoom);
            ObservedSaves.Enqueue(ReplayRecorder.SavePointKey(save));
        }
        catch (Exception e)
        {
            CoreMain.Logger.Warn($"Replay: computing the live save-point key failed: {e.Message}");
        }
    }

    private static void OnChecksum(NetChecksumData checksum, string context, NetFullCombatState state)
    {
        if (!_active) return;
        LiveChecksums.Enqueue((checksum.checksum, context));
        CompareChecksums();
    }

    private static bool TryGetChecksum(PlaybackStep step, out uint value)
    {
        value = 0;
        try
        {
            using var doc = JsonDocument.Parse(step.Data);
            return doc.RootElement.TryGetProperty("value", out var v) && v.TryGetUInt32(out value);
        }
        catch { return false; }
    }

    /// <summary>
    /// Compare live and recorded combat checksums in order. Observed live:
    /// they differ on many card plays while every keyframe matches, so this is
    /// noise for now — the first difference since the last save point is
    /// logged at Info, and the count when the segment ends.
    /// </summary>
    private static void CompareChecksums()
    {
        while (ExpectedChecksums.Count > 0 && LiveChecksums.Count > 0)
        {
            uint expected = ExpectedChecksums.Dequeue();
            var (live, context) = LiveChecksums.Dequeue();
            if (expected == live) continue;
            if (_checksumMismatches++ == 0)
                CoreMain.Logger.Info($"Replay: combat checksum differs at '{context}' (recorded {expected}, replay {live}); keyframes decide");
        }
    }

    private static void ResetChecksums()
    {
        if (_checksumMismatches > 0)
            CoreMain.Logger.Info($"Replay: {_checksumMismatches} combat checksum difference(s) since the last save point");
        _checksumMismatches = 0;
        ExpectedChecksums.Clear();
        LiveChecksums.Clear();
    }

    // -------- Controls --------

    public static void TogglePause()
    {
        _paused = !_paused;
        _stepOnce = false;
        UpdateBar();
    }

    public static void StepOnce()
    {
        if (!_paused) return;
        _stepOnce = true;
    }

    public static void ChangeSpeed(int delta)
    {
        _speedIndex = Math.Clamp(_speedIndex + delta, 0, Speeds.Length - 1);
        ApplySpeed();
        UpdateBar();
    }

    public static void RequestRestartFloor() => _request = Request.RestartFloor;

    /// <summary>From the floor picker: jump to the start of that recorded room.</summary>
    internal static void RequestJumpToRoom(RecordedRoom room)
    {
        _jumpTarget = room;
        _request = Request.JumpToRoom;
    }

    /// <summary>
    /// Step one act (+1 / -1). From the floor picker, the picker closes for
    /// the load and reopens on the new act's map.
    /// </summary>
    private static void RequestAct(int direction)
    {
        _reopenPickerAfterLoad = ReplayMapFloorPicker.IsOpen;
        if (_reopenPickerAfterLoad) ReplayMapFloorPicker.Close();
        _request = direction > 0 ? Request.NextAct : Request.PreviousAct;
    }

    private static void ToggleFloorPicker()
    {
        if (ReplayMapFloorPicker.IsOpen) ReplayMapFloorPicker.Close();
        else OpenFloorPicker();
    }

    private static void OpenFloorPicker()
    {
        var plan = _plan;
        if (plan == null) return;
        string? why = ReplayMapFloorPicker.Open(plan, plan.CurrentRoomStartIndex(_pos));
        if (why != null) ShowNote($"Can't open the map: {why}");
        UpdateBar();
    }

    /// <summary>The picker closed (a choice, M / Esc / B, or the map's own back button).</summary>
    internal static void OnFloorPickerClosed() => UpdateBar();

    private static void ToggleControls()
    {
        ReplayPlaybackBar.ControlsVisible = !ReplayPlaybackBar.ControlsVisible;
        UpdateBar();
    }
    public static void RequestNextRun() => _request = Request.NextRun;
    public static void RequestPreviousRun() => _request = Request.PreviousRun;
    public static void RequestRestartRun() => _request = Request.RestartRun;

    private static void ApplySpeed()
    {
        double speed = Speeds[_speedIndex];
        try { Engine.TimeScale = double.IsInfinity(speed) ? 4.0 : speed; } catch { }
    }

    private static TimeSpan PaceAfter(string inputKind)
    {
        double speed = Speeds[_speedIndex];
        if (double.IsInfinity(speed)) return TimeSpan.Zero;
        double baseMs = inputKind switch
        {
            "play_card" or "use_potion" or "discard_potion" or "end_turn" or "undo_end_turn" => 650,
            "choice" => 900,
            _ => 1100,
        };
        return TimeSpan.FromMilliseconds(baseMs / speed);
    }

    private static void ShowNote(string note, TimeSpan? duration = null)
    {
        _note = note;
        _noteUntil = DateTime.UtcNow + (duration ?? TimeSpan.FromSeconds(6));
        UpdateBar();
    }

    private static void UpdateBar()
    {
        var plan = _plan;
        if (plan == null) return;
        double speed = Speeds[_speedIndex];
        string speedText = double.IsInfinity(speed) ? "max" : $"{speed:0.#}x";
        string state = _finished ? "■ finished" : _paused ? "❚❚ paused" : "▶ playing";
        string line = $"REPLAY  {state}  {speedText}   floor {CurrentFloor()}/{plan.MaxRoomFloor}   "
                      + $"input {plan.InputsBefore(_pos)}/{plan.InputCount}";
        if (_divergences > 0) line += $"   resyncs {_divergences}";
        if (_runIndex >= 0 && _runIndex < _queue.Count && (_queue.Count > 1 || _playlistName != null))
        {
            string title = _playlistName != null ? $"{_playlistName}: " : "";
            line = $"{title}run {_runIndex + 1}/{_queue.Count} — {_queue[_runIndex].Label}\n{line}";
        }
        string? note = _note != null && DateTime.UtcNow < _noteUntil ? _note : null;
        ReplayPlaybackBar.Update(line, note, ReplayMapFloorPicker.IsOpen);
    }

    // -------- Input (from StatsVisibilityHotkeyPatch's existing _Input postfix) --------

    // Pad buttons are matched by the game's controller actions, not raw
    // JoyButton values. Under Steam Input (the game's preferred path) a pad
    // press reaches Godot only as an InputEventAction the game parses
    // (SteamControllerInputStrategy), never as InputEventJoypadButton; with
    // Steam Input off, Godot's joypad events match the same actions through
    // the InputMap. Steam Input's action set has no right-stick click, so R3
    // (the SpireLens menu) is matched raw and only exists with Steam Input off.
    private static readonly StringName[] PadActions =
    {
        Controller.selectButton, Controller.dPadUp, Controller.dPadDown, Controller.dPadRight,
        Controller.leftBumper, Controller.rightBumper, Controller.leftTrigger, Controller.rightTrigger,
        Controller.faceButtonNorth, Controller.faceButtonSouth, Controller.faceButtonEast, Controller.faceButtonWest,
        Controller.lStickPress,
    };

    // While the floor picker is open these pass to the game so its map can
    // move focus and choose a room: the pad's d-pad, left stick and A, and
    // the menu actions the game makes from them.
    private static readonly StringName[] PickerPassActions =
    {
        Controller.dPadUp, Controller.dPadDown, Controller.dPadLeft, Controller.dPadRight,
        Controller.lStickUp, Controller.lStickDown, Controller.lStickLeft, Controller.lStickRight,
        Controller.faceButtonSouth,
        MegaInput.up, MegaInput.down, MegaInput.left, MegaInput.right, MegaInput.select,
        "ui_accept",
    };

    /// <summary>
    /// Pad actions whose press a replay control consumed and whose release has
    /// not come yet. A press acts once (triggers are axes that repeat), and its
    /// release is consumed too: the A that opens the map must not release onto
    /// it as a click.
    /// </summary>
    private static readonly HashSet<string> PadHeld = new();

    private static StringName? PadAction(InputEvent evt, StringName[] actions)
    {
        if (evt is not (InputEventAction or InputEventJoypadButton or InputEventJoypadMotion)) return null;
        foreach (var action in actions)
        {
            // IsAction on an InputMap action that doesn't exist logs an error
            // for every event; a parsed InputEventAction just compares names.
            if (evt is not InputEventAction && !InputMap.HasAction(action)) continue;
            if (evt.IsAction(action)) return action;
        }
        return null;
    }

    /// <summary>True on the press edge of a pad control; tracks its release.</summary>
    private static bool PadPress(InputEvent evt, StringName action)
    {
        if (evt.IsPressed()) return PadHeld.Add(action.ToString());
        PadHeld.Remove(action.ToString());
        return false;
    }

    /// <summary>
    /// Playback hotkeys, and blocking of every other input that could act on
    /// the replayed run. Returns true when the event is consumed. Left Shift /
    /// R3 (SpireLens menu), F5 (hot reload) and mouse motion (hover) pass.
    /// While the floor picker is open, see <see cref="HandlePickerInput"/>.
    /// </summary>
    public static bool HandleInput(InputEvent evt)
    {
        if (!_active) return false;
        if (SpireLensOptionsMenu.IsOpen || ReplayPickerMenu.IsOpen) return false;
        if (ReplayMapFloorPicker.IsOpen) return HandlePickerInput(evt);

        switch (evt)
        {
            case InputEventMouseMotion:
                return false;

            case InputEventKey key:
            {
                if (key.Keycode is Key.Shift or Key.F5 || key.PhysicalKeycode is Key.Shift or Key.F5) return false;
                if (!key.Pressed || key.Echo) return true;
                var k = key.Keycode != Key.None ? key.Keycode : key.PhysicalKeycode;
                switch (k)
                {
                    case Key.Space: TogglePause(); break;
                    case Key.Period:
                    case Key.Right: StepOnce(); break;
                    case Key.Equal:
                    case Key.Plus:
                    case Key.KpAdd:
                    case Key.Bracketright: ChangeSpeed(+1); break;
                    case Key.Minus:
                    case Key.KpSubtract:
                    case Key.Bracketleft: ChangeSpeed(-1); break;
                    case Key.R: RequestRestartFloor(); break;
                    case Key.M: ToggleFloorPicker(); break;
                    case Key.Pagedown: RequestAct(+1); break;
                    case Key.Pageup: RequestAct(-1); break;
                    case Key.N: RequestNextRun(); break;
                    case Key.B: RequestPreviousRun(); break;
                    case Key.Home: RequestRestartRun(); break;
                    case Key.H: ToggleControls(); break;
                    case Key.Escape: RequestStop(); break;
                }
                return true;
            }

            case InputEventJoypadButton { ButtonIndex: JoyButton.RightStick }:
                return false;
        }

        if (PadAction(evt, PadActions) is { } a && PadPress(evt, a))
        {
            if (a == Controller.selectButton) TogglePause();
            else if (a == Controller.dPadRight) StepOnce();
            else if (a == Controller.rightBumper) ChangeSpeed(+1);
            else if (a == Controller.leftBumper) ChangeSpeed(-1);
            else if (a == Controller.faceButtonNorth) RequestRestartFloor();
            else if (a == Controller.faceButtonWest) RequestRestartRun();
            else if (a == Controller.faceButtonSouth) ToggleFloorPicker();
            else if (a == Controller.dPadUp) RequestAct(+1);
            else if (a == Controller.dPadDown) RequestAct(-1);
            else if (a == Controller.rightTrigger) RequestNextRun();
            else if (a == Controller.leftTrigger) RequestPreviousRun();
            else if (a == Controller.lStickPress) ToggleControls();
        }
        // Everything else (mouse buttons, other pad input, the game's own
        // actions) is consumed so the watcher cannot act on the replayed run.
        return true;
    }

    /// <summary>
    /// Input while the floor picker has the map open. The map gets what it
    /// needs to move between rooms and choose one: arrow keys and Enter, the
    /// pad's d-pad / left stick / A, the mouse wheel, and left clicks on the
    /// map itself (not the top bar over it, where potions are). M or Esc / B
    /// close the map, Page Up / Page Down / LB / RB change act, H / L3 toggle
    /// the controls. Everything else stays blocked.
    /// </summary>
    private static bool HandlePickerInput(InputEvent evt)
    {
        switch (evt)
        {
            case InputEventMouseMotion:
                return false;

            case InputEventMouseButton mouse:
                if (mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown) return false;
                return mouse.ButtonIndex != MouseButton.Left || (mouse.Pressed && !IsOverMap());

            case InputEventKey key:
            {
                if (key.Keycode is Key.Shift or Key.F5 || key.PhysicalKeycode is Key.Shift or Key.F5) return false;
                var k = key.Keycode != Key.None ? key.Keycode : key.PhysicalKeycode;
                if (k is Key.Up or Key.Down or Key.Left or Key.Right or Key.Enter or Key.KpEnter) return false;
                if (!key.Pressed || key.Echo) return true;
                switch (k)
                {
                    case Key.M:
                    case Key.Escape: ReplayMapFloorPicker.Close(); break;
                    case Key.Pagedown: RequestAct(+1); break;
                    case Key.Pageup: RequestAct(-1); break;
                    case Key.H: ToggleControls(); break;
                }
                return true;
            }

            case InputEventJoypadButton { ButtonIndex: JoyButton.RightStick }:
                return false;
        }

        if (PadAction(evt, PadActions) is { } a)
        {
            // The release of a press a control consumed (the A that opened
            // the map included) is consumed with it.
            if (!evt.IsPressed() && PadHeld.Remove(a.ToString())) return true;

            bool pickerControl = a == Controller.faceButtonEast || a == Controller.rightBumper
                                 || a == Controller.leftBumper || a == Controller.lStickPress;
            if (pickerControl)
            {
                if (PadPress(evt, a))
                {
                    if (a == Controller.faceButtonEast) ReplayMapFloorPicker.Close();
                    else if (a == Controller.rightBumper) RequestAct(+1);
                    else if (a == Controller.leftBumper) RequestAct(-1);
                    else ToggleControls();
                }
                return true;
            }
        }

        return PadAction(evt, PickerPassActions) == null;
    }

    private static bool IsOverMap()
    {
        try
        {
            var screen = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance;
            var hovered = screen?.GetViewport()?.GuiGetHoveredControl();
            return screen != null && hovered != null && (hovered == screen || screen.IsAncestorOf(hovered));
        }
        catch
        {
            return false;
        }
    }
}
