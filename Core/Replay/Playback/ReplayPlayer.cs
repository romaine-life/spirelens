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
/// Full-run replay: SpireLens drives the real game through a recorded run
/// (docs/replay-player.md). Starts from the recording's first snapshot,
/// feeds the canonical timeline's player inputs through
/// <see cref="ReplayInputFeeder"/> at a watchable pace, checks every game save
/// point against the recording's key, and resyncs from the recording's
/// snapshot on divergence.
///
/// The game runs with its own switches set so it writes nothing: the run is
/// set up like <c>RunManager.SetUpSavedSingleplayer</c> but with
/// <c>shouldSave: false</c>, no <c>IncrementNumReloads</c> and no daily time.
/// <see cref="ReplayDataGuard"/> backs up and restores everything else, and
/// <see cref="ReplayPlaybackGuard"/> keeps SpireLens's own tracking,
/// recording and import away from the replayed run.
///
/// No new Harmony target: save points arrive through the existing
/// <c>RunSaveSnapshotPatch</c> prefix, checksums through the public
/// <c>ChecksumTracker.ChecksumGenerated</c> event, selections through the
/// game's <c>CardSelectCmd</c> local-selector seam, input through the existing
/// <c>StatsVisibilityHotkeyPatch</c> input postfix.
/// </summary>
public static class ReplayPlayer
{
    private enum Request { None, RestartFloor, NextFloor, PreviousFloor, Stop }

    internal static readonly double[] Speeds = { 0.5, 1, 2, 4, double.PositiveInfinity };
    private const int DefaultSpeedIndex = 1;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(90);

    private static bool _active;
    private static bool _stopping;
    private static int _generation;
    private static ReplayPlaybackPlan? _plan;
    private static string _dir = "";
    private static long _gameStartTime;
    private static int _pos;
    private static int _speedIndex = DefaultSpeedIndex;
    private static bool _paused;
    private static bool _stepOnce;
    private static Request _request;
    private static string _status = "";
    private static string? _note;
    private static DateTime _noteUntil;
    private static bool _finished;
    private static int _lastResyncKeyframe = -1;
    private static int _resyncRepeats;
    private static int _divergences;

    private static ReplayCardSelector? _selector;
    private static IDisposable? _selectorScope;
    private static ChecksumTracker? _checksumSource;
    private static readonly Queue<string> ObservedSaves = new();
    private static readonly Queue<uint> ExpectedChecksums = new();
    private static readonly Queue<(uint Value, string Context)> LiveChecksums = new();
    private static bool _checksumMismatchLogged;

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
            if (rm != null && rm.IsInProgress && rm._startTime == ReplayDataGuard.MarkerGameStartTime())
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

    public static void Start(ReplayListing listing) => TaskHelper.RunSafely(StartAsync(listing));

    private static async Task StartAsync(ReplayListing listing)
    {
        var blocked = StartBlockedReason();
        if (blocked != null)
        {
            CoreMain.Logger.Warn($"Replay: not starting ({blocked})");
            return;
        }
        if (!listing.Playable)
        {
            CoreMain.Logger.Warn($"Replay: recording not playable ({listing.DisabledReason})");
            return;
        }

        ReplayPlaybackPlan plan;
        SerializableRun genesis;
        try
        {
            plan = ReplayPlaybackPlan.Build(File.ReadLines(Path.Combine(listing.Directory, "events.jsonl")));
            int start = plan.StartKeyframeIndex;
            if (start < 0) throw new InvalidOperationException("recording has no save point to start from");
            genesis = ReadSnapshot(listing.Directory, plan.Steps[start].Snapshot)
                      ?? throw new InvalidOperationException("start snapshot unreadable");
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"Replay: cannot prepare {listing.Directory}: {e.Message}");
            return;
        }

        // Interrupted replay data still waiting? Restore it before taking a new backup.
        if (ReplayDataGuard.MarkerExists) ReplayDataGuard.Restore("before starting a new replay");
        try
        {
            ReplayDataGuard.Backup(listing.GameStartTime);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"Replay: data backup failed, not starting: {e.Message}");
            return;
        }

        _active = true;
        _stopping = false;
        _finished = false;
        _generation++;
        _plan = plan;
        _dir = listing.Directory;
        _gameStartTime = listing.GameStartTime;
        _speedIndex = DefaultSpeedIndex;
        _paused = false;
        _stepOnce = false;
        _request = Request.None;
        _divergences = 0;
        _lastResyncKeyframe = -1;
        _resyncRepeats = 0;
        ReplayPlaybackGuard.SetPlayerActive(true);
        ReplayPlaybackGuard.SetMarkerPresent(true);
        RunTracker.SuspendForPlayback();

        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null) ReplayPlaybackBar.Build(tree);
        ApplySpeed();

        int startIndex = plan.StartKeyframeIndex;
        CoreMain.Logger.Info(
            $"Replay: starting {listing.GameStartTime} ({plan.InputCount} inputs, {plan.Steps.Count(s => s.Kind == PlaybackStepKind.Keyframe)} keyframes, "
            + $"{plan.Branches.Count} abandoned branches, {plan.SkippedConsoleCommands} console commands skipped)");
        if (listing.StartedMidRun)
            ShowNote($"Recording began mid-run: starting at floor {plan.Steps[startIndex].RoomFloor}");

        int generation = _generation;
        try
        {
            await LoadIntoGameAsync(genesis);
            _pos = startIndex + 1;
            await RunLoopAsync(generation, tree);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"Replay: playback failed: {e}");
            if (generation == _generation) await StopAsync("playback error");
        }
    }

    /// <summary>Exit playback: back to the main menu, user data restored.</summary>
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
        CoreMain.Logger.Info($"Replay: stopping ({reason}); {_divergences} divergence(s)");

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
        ReplayPlaybackGuard.SetPlayerActive(false);
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

    // -------- Loading a snapshot --------

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
        ExpectedChecksums.Clear();
        LiveChecksums.Clear();
        _checksumMismatchLogged = false;
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
        rm.InitializeRunLobby(net, state, new[]
        {
            new RunLobbyPlayer { id = net.NetId, isModded = net.LocalVersion.IsModded() },
        });
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
        await LoadIntoGameAsync(save);
        _pos = index + 1;
    }

    // -------- The driver loop --------

    private static async Task RunLoopAsync(int generation, SceneTree? tree)
    {
        var plan = _plan!;
        var wait = new Stopwatch();
        var paceUntil = DateTime.MinValue;

        while (generation == _generation)
        {
            if (tree != null) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            else await Task.Delay(16);
            if (generation != _generation) return;

            if (await HandleRequestAsync()) { wait.Reset(); continue; }
            UpdateBar();

            if (_pos >= plan.Steps.Count || _finished)
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
                        ExpectedChecksums.Clear();
                        LiveChecksums.Clear();
                        _checksumMismatchLogged = false;
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
    }

    private static void Finish(string why)
    {
        if (_finished) return;
        _finished = true;
        _status = "finished";
        ShowNote($"Replay finished ({why}). Esc exits.", TimeSpan.FromDays(1));
        CoreMain.Logger.Info($"Replay: finished ({why}); {_divergences} divergence(s)");
        if (_request == Request.Stop) TaskHelper.RunSafely(StopAsync("finished"));
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
        var plan = _plan!;

        switch (request)
        {
            case Request.Stop:
                await StopAsync("stopped by user");
                return true;

            case Request.RestartFloor:
            {
                int idx = plan.CurrentRoomStartIndex(_pos);
                if (idx < 0) { ShowNote("No floor start to restart from"); return true; }
                _finished = false;
                await LoadKeyframeAsync(idx, "restart floor");
                ShowNote($"Restarted floor {plan.Steps[idx].RoomFloor}");
                return true;
            }

            case Request.NextFloor:
            case Request.PreviousFloor:
            {
                int current = Math.Max(1, CurrentFloor());
                int target = request == Request.NextFloor ? current + 1 : current - 1;
                int idx = target >= 1 ? plan.RoomStartKeyframeIndex(target) : -1;
                if (idx < 0) { ShowNote($"No recorded start for floor {target}"); return true; }
                _finished = false;
                await LoadKeyframeAsync(idx, $"jump to floor {target}");
                ShowNote($"Jumped to floor {target}");
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

    /// <summary>Early desync signal: compare live and recorded checksums in order. Log only.</summary>
    private static void CompareChecksums()
    {
        while (ExpectedChecksums.Count > 0 && LiveChecksums.Count > 0)
        {
            uint expected = ExpectedChecksums.Dequeue();
            var (live, context) = LiveChecksums.Dequeue();
            if (expected != live && !_checksumMismatchLogged)
            {
                _checksumMismatchLogged = true;
                CoreMain.Logger.Warn($"Replay: combat checksum differs at '{context}' (recorded {expected}, replay {live}); the next save point decides");
            }
        }
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
    public static void RequestNextFloor() => _request = Request.NextFloor;
    public static void RequestPreviousFloor() => _request = Request.PreviousFloor;

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
        int floorTotal = plan.MaxRoomFloor;
        string line = $"REPLAY  {state}  {speedText}   floor {CurrentFloor()}/{floorTotal}   "
                      + $"input {plan.InputsBefore(_pos)}/{plan.InputCount}";
        if (_divergences > 0) line += $"   resyncs {_divergences}";
        string? note = _note != null && DateTime.UtcNow < _noteUntil ? _note : null;
        ReplayPlaybackBar.Update(line, note);
    }

    // -------- Input (from StatsVisibilityHotkeyPatch's existing _Input postfix) --------

    /// <summary>
    /// Playback hotkeys, and blocking of every other input that could act on
    /// the replayed run. Returns true when the event is consumed. Left Shift /
    /// R3 (SpireLens menu), F5 (hot reload) and mouse motion (hover) pass.
    /// </summary>
    public static bool HandleInput(InputEvent evt)
    {
        if (!_active) return false;
        if (SpireLensOptionsMenu.IsOpen || ReplayPickerMenu.IsOpen) return false;

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
                    case Key.Pagedown: RequestNextFloor(); break;
                    case Key.Pageup: RequestPreviousFloor(); break;
                    case Key.Escape: RequestStop(); break;
                }
                return true;
            }

            case InputEventJoypadButton button:
            {
                if (button.ButtonIndex == JoyButton.RightStick) return false;
                if (!button.Pressed) return true;
                switch (button.ButtonIndex)
                {
                    case JoyButton.Back: TogglePause(); break;
                    case JoyButton.DpadRight: StepOnce(); break;
                    case JoyButton.RightShoulder: ChangeSpeed(+1); break;
                    case JoyButton.LeftShoulder: ChangeSpeed(-1); break;
                    case JoyButton.Y: RequestRestartFloor(); break;
                    case JoyButton.DpadUp: RequestNextFloor(); break;
                    case JoyButton.DpadDown: RequestPreviousFloor(); break;
                }
                return true;
            }

            default:
                // Mouse buttons, joypad motion, touch, actions: consumed so the
                // watcher cannot act on the replayed run.
                return true;
        }
    }
}
