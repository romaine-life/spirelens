using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace SpireLens.Core.Replay;

/// <summary>
/// Records every run to disk in a form a future replay driver / database
/// importer can consume: exact game save snapshots at every save point, an
/// ordered input log (each game action in both the game's own packet form and
/// a readable form), copies of the game's own per-combat replays, and
/// save/load markers that let a reader rebuild the canonical timeline across
/// Continue, room restarts and death undos. Format: <c>docs/run-recording.md</c>.
///
/// Capture only — nothing here changes game behavior, and every entry point
/// swallows its own failures (a recording bug must never cost the player a
/// run). Capture sources:
///   * public game events, subscribed per run (no Harmony):
///     <c>ActionQueueSet.ActionEnqueued/ActionResumed</c>,
///     <c>PlayerChoiceSynchronizer.PlayerChoiceReceived</c>,
///     <c>ChecksumTracker.ChecksumGenerated</c>,
///     <c>ActionExecutor.AfterActionExecuted</c>;
///   * <c>SaveManager.Saved</c> (raised after every run-save write) for save
///     points and snapshots;
///   * Harmony prefixes in <c>Core/Patches/Replay*Patch.cs</c> for loads,
///     combat-replay copies and the non-action decisions
///     (event / rest site / reward / shop / treasure chest).
///
/// Hot reload: <see cref="InitializeHooks"/> re-attaches to a run already in
/// progress; <see cref="TeardownHooks"/> unsubscribes everything, closes the
/// log and waits (bounded) for queued file writes.
/// </summary>
public static class ReplayRecorder
{
    /// <summary>Set to 1/true to turn run recording off entirely.</summary>
    public const string DisableEnvVar = "SPIRELENS_DISABLE_RUN_RECORDING";

    private static readonly object _lock = new();
    private static ReplaySession? _session;
    private static bool _hooked;
    private static SaveManager? _savedSource;

    // Per-run game objects we are subscribed to (they are recreated for every run).
    private static ActionQueueSet? _subQueue;
    private static PlayerChoiceSynchronizer? _subChoices;
    private static ChecksumTracker? _subChecksums;
    private static ActionExecutor? _subExecutor;

    private static string? _pendingLoadReason;
    private static long? _loadedStartTime;

    private static bool _disabled;
    private static bool _active;

    /// <summary>Read once per Core load (env var), so a hot reload picks up a change.</summary>
    public static bool IsDisabled => _disabled;

    /// <summary>True while this Core load is capturing (enabled and not torn down).</summary>
    public static bool IsRecording => !_disabled && _active;

    private static bool ReadDisabledFromEnvironment()
    {
        var v = Environment.GetEnvironmentVariable(DisableEnvVar);
        return !string.IsNullOrEmpty(v) && v != "0"
            && !v.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    // -------- Lifecycle --------

    public static void InitializeHooks()
    {
        _disabled = ReadDisabledFromEnvironment();
        if (_disabled)
        {
            CoreMain.Logger.Info($"Run recording disabled by {DisableEnvVar}.");
            return;
        }

        RunManager.Instance.RunStarted += OnRunStarted;
        _savedSource = SaveManager.Instance;
        _savedSource.Saved += OnRunSaved;
        _hooked = true;
        _active = true;

        // Core hot reload mid-run: the game is not replaying anything, just
        // re-attach to the live run and mark the gap.
        Guard("InitializeHooks.attach", () =>
        {
            var rm = RunManager.Instance;
            if (!rm.IsInProgress || rm.NetService?.Type == NetGameType.Replay) return;
            lock (_lock)
            {
                var session = EnsureSessionLocked(rm._startTime, () => HeaderFromRunState(rm.State!), midRunIfCreated: true, out _);
                session?.Append(ReplayFormat.EvCoreReloaded, Context(), BuildStamp());
                SubscribeRunObjectsLocked(rm);
            }
        });
        CoreMain.Logger.Info("Run recording hooks wired.");
    }

    public static void TeardownHooks()
    {
        // First: any callback still reaching this (soon orphaned) assembly —
        // e.g. a per-action BeforeCancelled subscription — must become a no-op
        // instead of reopening a session behind the reloaded Core's back.
        _active = false;
        if (_hooked)
        {
            try { RunManager.Instance.RunStarted -= OnRunStarted; } catch { }
            try { if (_savedSource != null) _savedSource.Saved -= OnRunSaved; } catch { }
            _savedSource = null;
            _hooked = false;
        }
        lock (_lock)
        {
            UnsubscribeRunObjectsLocked();
            UnsubscribeUiLocked();
            _pendingSelection = null;
            _session?.Dispose();
            _session = null;
        }
        if (!ReplayStorage.WaitForPendingWrites(TimeSpan.FromSeconds(2)))
            CoreMain.Logger.Warn("Run recording: queued writes still pending at shutdown.");
    }

    /// <summary>
    /// Set by <see cref="RoomResetter"/> immediately before it reloads the run
    /// save, so the resulting <c>load</c> event says why. Consumed by the next load.
    /// </summary>
    public static void SetPendingLoadReason(string reason)
    {
        lock (_lock) _pendingLoadReason = reason;
    }

    private static void OnRunStarted(RunState state) => Guard(nameof(OnRunStarted), () =>
    {
        var rm = RunManager.Instance;
        lock (_lock)
        {
            if (rm.NetService?.Type == NetGameType.Replay)
            {
                // The game's own debug combat-replay player: never record a replay of a replay.
                UnsubscribeRunObjectsLocked();
                _session?.Dispose();
                _session = null;
                return;
            }

            long startTime = rm._startTime;
            bool continued = _loadedStartTime == startTime;
            _loadedStartTime = null;
            _pendingLoadReason = null;

            var session = EnsureSessionLocked(startTime, () => HeaderFromRunState(state), midRunIfCreated: continued, out _);
            if (session == null) return;

            if (session.Header.SpirelensRunId == null && RunTracker.Current?.RunId is { } runId)
                session.UpdateHeader(h => h.SpirelensRunId = runId);

            var started = BuildStamp();
            started["continued"] = continued;
            started["net_type"] = rm.NetService?.Type.ToString();
            session.Append(ReplayFormat.EvRunStarted, Context(), started);
            SubscribeRunObjectsLocked(rm);
        }
    });

    /// <summary>From <c>RunHistoryUtilitiesCreateEntryPatch</c>: every terminal path (win, loss, abandon incl. main-menu abandon).</summary>
    public static void OnRunEnded(long gameStartTime, string outcome, int floor)
    {
        RecordRunEnd(gameStartTime, outcome, floor);
        // Outside the recording guard: the game's history file and the stats
        // record are worth importing even with recording disabled. Queued
        // behind the run-end writes RecordRunEnd just queued.
        RunDatabaseImporter.RequestAfterRunEnd();
    }

    private static void RecordRunEnd(long gameStartTime, string outcome, int floor) => Guard(nameof(OnRunEnded), () =>
    {
        lock (_lock)
        {
            // Never create a recording at run end: a run we did not record stays unrecorded.
            var session = EnsureSessionLocked(gameStartTime, null, midRunIfCreated: false, out _);
            if (session == null) return;
            bool inCombat = CombatManager.Instance?.IsInProgress == true;
            session.Append(ReplayFormat.EvRunEnd, Context(), new Dictionary<string, object?>
            {
                ["outcome"] = outcome,
                ["floor"] = floor,
                ["in_combat"] = inCombat,
            });
            string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            string? runId = RunTracker.Current?.RunId;
            session.UpdateHeader(h =>
            {
                h.Outcome = outcome;
                h.EndedAtUtc = now;
                h.FinalFloor = floor;
                h.SpirelensRunId ??= runId;
            });
            session.CloseDecisionSegment();
        }
    });

    // -------- Save points (RunSaveManager.Saved) and loads (Harmony prefix) --------

    /// <summary>
    /// Handler for <c>SaveManager.Saved</c> (forwarding <c>RunSaveManager.Saved</c>),
    /// which <c>RunSaveManager.SaveRun(SerializableRun, bool)</c> raises right
    /// after writing the run save. That method is the one every run-save write
    /// goes through: map-node entry, combat victory, finished Ancient, run
    /// creation, and the IncrementNumReloads re-save on every load.
    ///
    /// Deliberately an event, not a Harmony prefix on that method: its only
    /// callers (the <c>SaveRun(AbstractRoom)</c> overload and
    /// <c>SaveManager.IncrementNumReloads</c>) are compiled long before a hot
    /// reload adds a detour, and live they bypassed one (see the primer,
    /// "Newly Added Harmony Targets Need One Full Restart"). The event works
    /// from the moment it is subscribed. The handler reads back the file the
    /// game just wrote, so the snapshot is byte-for-byte <c>current_run.save</c>.
    /// </summary>
    private static void OnRunSaved() => Guard(nameof(OnRunSaved), () =>
    {
        if (RunManager.Instance.NetService?.Type == NetGameType.Replay) return;
        if (!TryReadLatestRunSave(out byte[] json, out bool isMultiplayer)) return;

        var save = JsonSerializer.Deserialize(json, JsonSerializationUtility.GetTypeInfo<SerializableRun>());
        if (save == null) return;
        string key = SavePointKey(save);

        lock (_lock)
        {
            var session = EnsureSessionLocked(
                save.StartTime,
                () => HeaderFromSave(save),
                midRunIfCreated: save.FloorReached > 0 || save.NumReloads > 0,
                out _);
            if (session == null) return;

            var (rel, reused) = session.ReserveSnapshot(key);
            if (!reused)
            {
                string abs = session.AbsolutePath(rel);
                ReplayStorage.Enqueue("snapshot write", () => ReplayStorage.WriteGzip(abs, json));
            }

            long savePointSeq = session.Append(ReplayFormat.EvSavePoint, Context(), new Dictionary<string, object?>
            {
                ["key"] = key,
                ["snapshot"] = rel,
                ["reused"] = reused,
                ["num_reloads"] = save.NumReloads,
                ["floor"] = save.FloorReached,
                ["act"] = save.CurrentActIndex,
                ["pre_finished_room"] = save.PreFinishedRoom?.RoomType.ToString(),
                ["is_multiplayer"] = isMultiplayer,
                ["bytes"] = json.Length,
            });
            session.OnSavePointAppended(savePointSeq);

            if (session.Header.SpirelensRunId == null && RunTracker.Current?.RunId is { } runId)
                session.UpdateHeader(h => h.SpirelensRunId = runId);
        }
    });

    /// <summary>
    /// Prefix on <c>RunManager.SetUpSavedSingleplayer</c> / <c>SetUpSavedMultiplayer</c>:
    /// the game is about to resume from <paramref name="save"/>. Everything
    /// recorded after the matching save point is now an abandoned branch.
    /// </summary>
    public static void OnLoadingSave(SerializableRun save, string setUpMethod) => Guard(nameof(OnLoadingSave), () =>
    {
        if (save == null) return;
        string key = SavePointKey(save);
        lock (_lock)
        {
            string reason = _pendingLoadReason ?? ReplayFormat.LoadReasonContinue;
            _pendingLoadReason = null;
            _loadedStartTime = save.StartTime;

            var session = EnsureSessionLocked(save.StartTime, () => HeaderFromSave(save), midRunIfCreated: true, out _);
            if (session == null) return;

            session.Append(ReplayFormat.EvLoad, null, new Dictionary<string, object?>
            {
                ["key"] = key,
                ["reason"] = reason,
                ["num_reloads_before"] = save.NumReloads,
                ["floor"] = save.FloorReached,
                ["act"] = save.CurrentActIndex,
                ["pre_finished_room"] = save.PreFinishedRoom?.RoomType.ToString(),
                ["set_up"] = setUpMethod,
            });

            if (session.Header.Outcome != "in_progress")
            {
                // A death undo (or any reload after an ended run) un-ends it.
                session.UpdateHeader(h =>
                {
                    h.Outcome = "in_progress";
                    h.EndedAtUtc = null;
                    h.FinalFloor = null;
                });
            }
        }
    });

    /// <summary>
    /// The run save file the game most recently wrote. <c>Saved</c> does not
    /// say which file, and <c>RunManager.NetService</c> is not yet the new
    /// run's while <c>SetUpSaved*</c> re-saves, so pick the newer of the
    /// singleplayer and multiplayer saves.
    /// </summary>
    private static bool TryReadLatestRunSave(out byte[] bytes, out bool isMultiplayer)
    {
        bytes = Array.Empty<byte>();
        isMultiplayer = false;
        var rsm = SaveManager.Instance._runSaveManager;
        string sp = Godot.ProjectSettings.GlobalizePath(rsm._saveStore.GetFullPath(rsm.CurrentRunSavePath));
        string mp = Godot.ProjectSettings.GlobalizePath(rsm._saveStore.GetFullPath(rsm.CurrentMultiplayerRunSavePath));
        bool spExists = File.Exists(sp), mpExists = File.Exists(mp);
        if (!spExists && !mpExists) return false;
        isMultiplayer = mpExists && (!spExists || File.GetLastWriteTimeUtc(mp) > File.GetLastWriteTimeUtc(sp));
        bytes = File.ReadAllBytes(isMultiplayer ? mp : sp);
        return bytes.Length > 0;
    }

    // -------- Combat replay copies (Harmony prefix + postfix on RunManager.WriteReplay) --------

    /// <summary>Events in the game's in-memory combat replay, or 0 if there is nothing worth copying.</summary>
    public static int PendingCombatReplayEventCount(RunManager rm)
    {
        try
        {
            if (_disabled || !_active || rm.NetService?.Type == NetGameType.Replay) return 0;
            var writer = rm.CombatReplayWriter;
            if (writer == null || !writer.IsEnabled) return 0;
            return writer._replay?.events.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Copy the <c>replays/latest.mcr</c> the game just wrote. The game
    /// overwrites that single file at every combat end (and on CleanUp with
    /// an empty non-combat replay, which the event count filters out), so the
    /// copy is the only way to keep one per combat. The bytes are read
    /// synchronously — the next WriteReplay could otherwise replace them —
    /// and written to our directory off-thread.
    /// </summary>
    public static void OnCombatReplayWritten(bool stopRecording, int eventCount) => Guard(nameof(OnCombatReplayWritten), () =>
    {
        if (eventCount <= 0) return;
        string src = Godot.ProjectSettings.GlobalizePath(
            SaveManager.Instance.GetProfileScopedPath("replays/latest.mcr"));
        if (!File.Exists(src)) return;
        byte[] bytes = File.ReadAllBytes(src);

        lock (_lock)
        {
            var session = LiveSessionLocked();
            if (session == null) return;
            int floor = RunManager.Instance.State?.TotalFloor ?? 0;
            string rel = session.ReserveCombatFile(floor);
            string abs = session.AbsolutePath(rel);
            ReplayStorage.Enqueue("combat replay copy", () => ReplayStorage.WriteBytesAtomic(abs, bytes));
            session.Append(ReplayFormat.EvCombatReplayFile, Context(), new Dictionary<string, object?>
            {
                ["file"] = rel,
                ["bytes"] = bytes.Length,
                ["events"] = eventCount,
                ["stop_recording"] = stopRecording,
            });
        }
    });

    // -------- Non-action decisions (Harmony prefixes) --------

    /// <summary>
    /// Log a non-action decision. With <paramref name="training"/>, also write
    /// its decision record (state + options + choice), linked both ways.
    /// </summary>
    internal static void RecordDecision(string kind, Dictionary<string, object?> data, TrainingSpec? training = null) =>
        Guard("RecordDecision." + kind, () =>
        {
            data["kind"] = kind;
            lock (_lock)
            {
                var session = LiveSessionLocked();
                if (session == null) return;
                EnsureUiSubscriptionsLocked();
                var player = training == null ? null : DecisionCapture.LocalPlayer(RunManager.Instance.State);
                if (training == null || player == null)
                {
                    session.Append(ReplayFormat.EvDecision, Context(), data);
                    return;
                }
                EmitWithDecisionRecordLocked(session, ReplayFormat.EvDecision, data, kind, player,
                    training.Chosen ?? data, training.Options, training.ChosenIndex, null,
                    optionsComplete: true, selection: null, notes: null);
            }
        });

    /// <summary>Options and choice for one non-action decision's training record.</summary>
    internal sealed record TrainingSpec(List<DecisionCapture.Option> Options, int? ChosenIndex, object? Chosen = null);

    public static void OnEventOptionChosen(EventSynchronizer sync, int index)
    {
        if (!IsRecording) return;
        var data = new Dictionary<string, object?> { ["index"] = index };
        Try(() => data["page"] = sync._pageIndex);
        Try(() => data["shared"] = sync._canonicalEvent?.IsShared);
        Try(() =>
        {
            var ev = sync.GetLocalEvent();
            data["event"] = ev.Id.ToString();
            var options = ev.CurrentOptions;
            if (index >= 0 && index < options.Count)
            {
                data["option"] = options[index].TextKey;
                data["is_proceed"] = options[index].IsProceed;
            }
        });
        var offered = new List<DecisionCapture.Option>();
        Try(() =>
        {
            var options = sync.GetLocalEvent().CurrentOptions;
            for (int i = 0; i < options.Count; i++)
            {
                var o = options[i];
                var detail = new Dictionary<string, object?>();
                Try(() => detail["locked"] = o.IsLocked);
                Try(() => detail["proceed"] = o.IsProceed);
                Try(() => detail["relic"] = o.Relic?.Id.ToString());
                offered.Add(new DecisionCapture.Option(new LegalOption("event_option", Index: i, Id: o.TextKey), detail));
            }
        });
        RecordDecision("event_option", data, new TrainingSpec(offered, index >= 0 && index < offered.Count ? index : null));
    }

    public static void OnRestSiteOptionChosen(RestSiteSynchronizer sync, int index)
    {
        if (!IsRecording) return;
        var data = new Dictionary<string, object?> { ["index"] = index };
        Try(() =>
        {
            int slot = sync._playerCollection.GetPlayerSlotIndex(sync.LocalPlayer);
            var options = sync._restSites[slot].options;
            if (index >= 0 && index < options.Count) data["option_id"] = options[index].OptionId;
        });
        var offered = new List<DecisionCapture.Option>();
        Try(() =>
        {
            int slot = sync._playerCollection.GetPlayerSlotIndex(sync.LocalPlayer);
            var options = sync._restSites[slot].options;
            for (int i = 0; i < options.Count; i++)
            {
                var detail = new Dictionary<string, object?>();
                var o = options[i];
                Try(() => detail["enabled"] = o.IsEnabled);
                offered.Add(new DecisionCapture.Option(new LegalOption("rest_option", Index: i, Id: o.OptionId), detail));
            }
        });
        RecordDecision("rest_option", data, new TrainingSpec(offered, index >= 0 && index < offered.Count ? index : null));
    }

    public static void OnRewardSelected(RewardsSetSynchronizer sync, Reward reward)
    {
        if (!IsRecording) return;
        var data = new Dictionary<string, object?> { ["reward_type"] = reward?.GetType().Name };
        Try(() =>
        {
            var set = sync.GetRewardStateForPlayer(sync.LocalPlayer).rewardsStack.Last().set;
            data["set_id"] = set.Id;
            data["index"] = reward == null ? null : set.Rewards.IndexOf(reward);
        });
        Try(() => data["reward"] = reward?.ToString());
        TrainingSpec? training = null;
        Try(() =>
        {
            var set = sync.GetRewardStateForPlayer(sync.LocalPlayer).rewardsStack.Last().set;
            int idx = reward == null ? -1 : set.Rewards.IndexOf(reward);
            training = new TrainingSpec(DecisionCapture.RewardOptions(set), idx >= 0 ? idx : null);
        });
        RecordDecision("reward_take", data, training);
    }

    public static void OnRewardsSkipped(RewardsSetSynchronizer sync)
    {
        if (!IsRecording) return;
        var data = new Dictionary<string, object?>();
        Try(() => data["set_id"] = sync.GetRewardStateForPlayer(sync.LocalPlayer).rewardsStack.Last().set.Id);
        TrainingSpec? training = null;
        Try(() =>
        {
            var set = sync.GetRewardStateForPlayer(sync.LocalPlayer).rewardsStack.Last().set;
            training = new TrainingSpec(DecisionCapture.RewardOptions(set), set.Rewards.Count);
        });
        RecordDecision("reward_skip", data, training);
    }

    public static void OnMerchantPurchaseAttempt(MerchantEntry entry, MerchantInventory? inventory, bool ignoreCost, bool? cancelable)
    {
        if (!IsRecording) return;
        var data = new Dictionary<string, object?>
        {
            // ignoreCost purchases are made by game logic (Lord's Parasol), not
            // the player; a replay driver must not re-issue them.
            ["auto"] = ignoreCost,
            ["cancelable"] = cancelable,
        };
        Try(() => data["cost"] = entry.Cost);
        Try(() =>
        {
            inventory ??= (RunManager.Instance.State?.CurrentRoom as MerchantRoom)?.GetLocalInventory();
            switch (entry)
            {
                case MerchantCardRemovalEntry:
                    data["category"] = "card_removal";
                    break;
                case MerchantCardEntry card:
                    data["model"] = card.CreationResult?.Card?.Id.ToString();
                    int ci = inventory?.CharacterCardEntries.ToList().IndexOf(card) ?? -1;
                    if (ci >= 0) { data["category"] = "character_card"; data["slot"] = ci; }
                    else
                    {
                        int cc = inventory?.ColorlessCardEntries.ToList().IndexOf(card) ?? -1;
                        data["category"] = "colorless_card";
                        data["slot"] = cc >= 0 ? cc : null;
                    }
                    break;
                case MerchantRelicEntry relic:
                    data["category"] = "relic";
                    data["model"] = relic.Model?.Id.ToString();
                    data["slot"] = IndexOrNull(inventory?.RelicEntries, relic);
                    break;
                case MerchantPotionEntry potion:
                    data["category"] = "potion";
                    data["model"] = potion.Model?.Id.ToString();
                    data["slot"] = IndexOrNull(inventory?.PotionEntries, potion);
                    break;
                default:
                    data["category"] = entry.GetType().Name;
                    break;
            }
        });
        TrainingSpec? training = null;
        if (!ignoreCost)
        {
            Try(() =>
            {
                inventory ??= (RunManager.Instance.State?.CurrentRoom as MerchantRoom)?.GetLocalInventory();
                var offered = DecisionCapture.MerchantOptions(inventory);
                int idx = offered.FindIndex(o => ReferenceEquals(o.Ref, entry));
                training = new TrainingSpec(offered, idx >= 0 ? idx : null);
            });
        }
        RecordDecision(entry is MerchantCardRemovalEntry ? "shop_remove_card" : "shop_buy", data, training);
    }

    private static int? IndexOrNull<T>(IReadOnlyList<T>? list, T item) where T : class
    {
        if (list == null) return null;
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item)) return i;
        return null;
    }

    // -------- Per-run game event subscriptions --------

    private static void SubscribeRunObjectsLocked(RunManager rm)
    {
        if (ReferenceEquals(_subQueue, rm.ActionQueueSet)
            && ReferenceEquals(_subChoices, rm.PlayerChoiceSynchronizer)
            && ReferenceEquals(_subChecksums, rm.ChecksumTracker)
            && ReferenceEquals(_subExecutor, rm.ActionExecutor))
            return;

        UnsubscribeRunObjectsLocked();

        _subQueue = rm.ActionQueueSet;
        _subChoices = rm.PlayerChoiceSynchronizer;
        _subChecksums = rm.ChecksumTracker;
        _subExecutor = rm.ActionExecutor;

        if (_subQueue != null)
        {
            _subQueue.ActionEnqueued += OnActionEnqueued;
            _subQueue.ActionResumed += OnActionResumed;
        }
        if (_subChoices != null) _subChoices.PlayerChoiceReceived += OnPlayerChoice;
        if (_subChecksums != null) _subChecksums.ChecksumGenerated += OnChecksum;
        if (_subExecutor != null) _subExecutor.AfterActionExecuted += OnActionExecuted;
    }

    private static void UnsubscribeRunObjectsLocked()
    {
        if (_subQueue != null)
        {
            _subQueue.ActionEnqueued -= OnActionEnqueued;
            _subQueue.ActionResumed -= OnActionResumed;
        }
        if (_subChoices != null) _subChoices.PlayerChoiceReceived -= OnPlayerChoice;
        if (_subChecksums != null) _subChecksums.ChecksumGenerated -= OnChecksum;
        if (_subExecutor != null) _subExecutor.AfterActionExecuted -= OnActionExecuted;
        _subQueue = null;
        _subChoices = null;
        _subChecksums = null;
        _subExecutor = null;
    }

    private static void OnActionEnqueued(GameAction action) => Guard(nameof(OnActionEnqueued), () =>
    {
        lock (_lock)
        {
            var session = LiveSessionLocked();
            if (session == null) return;

            if (action is GenericHookGameAction hook)
            {
                session.Append(ReplayFormat.EvHookAction, Context(), new Dictionary<string, object?>
                {
                    ["action_id"] = action.Id,
                    ["player"] = action.OwnerId,
                    ["hook_id"] = hook.HookId,
                    ["action_type"] = hook.ActionType.ToString(),
                    ["net"] = NetForm(new CombatReplayEvent
                    {
                        eventType = CombatReplayEventType.HookAction,
                        playerId = action.OwnerId,
                        hookId = hook.HookId,
                        gameActionType = hook.ActionType,
                    }),
                });
                return;
            }

            bool playerDriven = ActionQueueSet.IsGameActionPlayerDriven(action);
            var data = new Dictionary<string, object?>
            {
                ["action_id"] = action.Id,
                ["player"] = action.OwnerId,
                ["class"] = action.GetType().Name,
                ["action_type"] = action.ActionType.ToString(),
                ["player_driven"] = playerDriven,
            };
            Try(() => data["executing_action_id"] = RunManager.Instance.ActionExecutor?.CurrentlyRunningAction?.Id);

            INetAction? net = null;
            try { net = action.ToNetAction(); }
            catch (Exception e) { data["net_error"] = e.Message; }

            if (net != null)
            {
                Try(() => data["net_type_id"] = net.ToId());
                data["net"] = NetForm(new CombatReplayEvent
                {
                    eventType = CombatReplayEventType.GameAction,
                    playerId = action.OwnerId,
                    action = net,
                });
                data["sem"] = DescribeNetAction(net, action.OwnerId);
            }

            EnsureUiSubscriptionsLocked();
            if (!TryEmitActionDecisionLocked(session, action, net, data))
                session.Append(ReplayFormat.EvAction, Context(), data);

            if (playerDriven) action.BeforeCancelled += OnActionCancelled;
        }
    });

    private static void OnActionExecuted(GameAction action) => Guard(nameof(OnActionExecuted), () =>
    {
        if (action is GenericHookGameAction || !ActionQueueSet.IsGameActionPlayerDriven(action)) return;
        lock (_lock)
        {
            LiveSessionLocked()?.Append(ReplayFormat.EvActionResult, Context(), new Dictionary<string, object?>
            {
                ["action_id"] = action.Id,
                ["state"] = action.State.ToString(),
            });
        }
    });

    private static void OnActionCancelled(GameAction action) => Guard(nameof(OnActionCancelled), () =>
    {
        action.BeforeCancelled -= OnActionCancelled;
        lock (_lock)
        {
            LiveSessionLocked()?.Append(ReplayFormat.EvActionResult, Context(), new Dictionary<string, object?>
            {
                ["action_id"] = action.Id,
                ["state"] = "Canceled",
            });
        }
    });

    private static void OnActionResumed(uint actionId) => Guard(nameof(OnActionResumed), () =>
    {
        lock (_lock)
        {
            LiveSessionLocked()?.Append(ReplayFormat.EvActionResumed, Context(), new Dictionary<string, object?>
            {
                ["action_id"] = actionId,
                ["net"] = NetForm(new CombatReplayEvent
                {
                    eventType = CombatReplayEventType.ResumeAction,
                    actionId = actionId,
                }),
            });
        }
    });

    private static void OnPlayerChoice(Player player, uint choiceId, NetPlayerChoiceResult result) => Guard(nameof(OnPlayerChoice), () =>
    {
        lock (_lock)
        {
            var session = LiveSessionLocked();
            if (session == null) return;
            var data = new Dictionary<string, object?>
            {
                ["player"] = player?.NetId,
                ["choice_id"] = choiceId,
                ["choice_type"] = result.type.ToString(),
                ["net"] = NetForm(new CombatReplayEvent
                {
                    eventType = CombatReplayEventType.PlayerChoice,
                    playerId = player?.NetId,
                    choiceId = choiceId,
                    playerChoiceResult = result,
                }),
                ["sem"] = DescribeChoice(player, result),
            };

            // The screen that offered this choice was snapshot when it opened
            // (it has usually closed by the time the choice syncs).
            var selection = _pendingSelection;
            _pendingSelection = null;

            if (player == null || !LocalContext.IsMe(player))
            {
                session.Append(ReplayFormat.EvChoice, Context(), data);
                return;
            }

            var options = selection?.Options ?? new List<DecisionCapture.Option>();
            List<int> chosen;
            if (selection != null) chosen = DecisionCapture.ChosenIndexes(selection, result, player);
            else chosen = result.indexes?.ToList() ?? new List<int>();
            var notes = new List<string>();
            if (selection == null) notes.Add("options_unavailable: no selection screen snapshot preceded this choice");
            else if (!selection.FilterKnown) notes.Add("options_filter_unknown");
            string kind = selection?.Screen == "card_reward" ? "card_reward" : "card_select";
            data["selection_screen"] = selection?.Screen;

            EmitWithDecisionRecordLocked(session, ReplayFormat.EvChoice, data, kind, player,
                chosen: data["sem"],
                options: options,
                chosenIndex: chosen.Count == 1 ? chosen[0] : null,
                chosenIndexes: chosen,
                optionsComplete: selection != null && selection.FilterKnown,
                selection: selection?.Describe(),
                notes: notes);
        }
    });

    private static void OnChecksum(NetChecksumData checksum, string context, NetFullCombatState fullState) => Guard(nameof(OnChecksum), () =>
    {
        // The full state dump stays in the game's own .mcr; the log keeps only
        // the value, which is what a replay compares against.
        lock (_lock)
        {
            EnsureUiSubscriptionsLocked();
            LiveSessionLocked()?.Append(ReplayFormat.EvChecksum, null, new Dictionary<string, object?>
            {
                ["id"] = checksum.id,
                ["value"] = checksum.checksum,
                ["context"] = context,
            });
        }
    });

    // -------- Decision records (training data) --------

    private static readonly HashSet<string> TrainableActionKinds = new(StringComparer.Ordinal)
    {
        "play_card", "use_potion", "discard_potion", "end_turn", "undo_end_turn",
        "map_vote", "pick_relic", "vote_next_act",
    };

    /// <summary>
    /// A local player's decision action: capture state + legal options
    /// (before the action executes; ActionEnqueued fires before execution)
    /// and log the event with its decision record. False when the action is
    /// not a local decision; the caller logs it plainly.
    /// </summary>
    private static bool TryEmitActionDecisionLocked(ReplaySession session, GameAction action, INetAction? net, Dictionary<string, object?> data)
    {
        if (net == null || !data.TryGetValue("sem", out var semObj) || semObj is not Dictionary<string, object?> sem) return false;
        if (!sem.TryGetValue("kind", out var k) || k is not string kind || !TrainableActionKinds.Contains(kind)) return false;
        if (LocalContext.NetId.HasValue && action.OwnerId != LocalContext.NetId.Value) return false;

        var rm = RunManager.Instance;
        var state = rm.State;
        var player = state?.Players.FirstOrDefault(p => p.NetId == action.OwnerId);
        if (state == null || player == null) return false;

        List<DecisionCapture.Option> options = new();
        LegalOption? chosen = null;
        Try(() =>
        {
            switch (net)
            {
                case NetPlayCardAction p:
                    options = DecisionCapture.ActionOptions(player);
                    chosen = new LegalOption("play_card", CombatCardIndex: p.card.CombatCardIndex, TargetCombatId: p.targetId);
                    break;
                case NetUsePotionAction u:
                    options = DecisionCapture.ActionOptions(player);
                    chosen = new LegalOption("use_potion", TargetCombatId: u.targetId, TargetPlayerId: u.targetPlayerId, PotionSlot: (int)u.potionIndex);
                    break;
                case NetDiscardPotionGameAction dp:
                    options = DecisionCapture.ActionOptions(player);
                    chosen = new LegalOption("discard_potion", PotionSlot: (int)dp.potionSlotIndex);
                    break;
                case NetEndPlayerTurnAction:
                    options = DecisionCapture.ActionOptions(player);
                    chosen = new LegalOption("end_turn");
                    break;
                case NetUndoEndPlayerTurnAction:
                    chosen = new LegalOption("undo_end_turn");
                    options = new() { new DecisionCapture.Option(chosen, new()) };
                    break;
                case NetVoteForMapCoordAction v:
                    options = DecisionCapture.MapOptions(state);
                    chosen = new LegalOption("map_vote",
                        Id: v.destination.HasValue ? $"{v.destination.Value.coord.col},{v.destination.Value.coord.row}" : null);
                    break;
                case NetPickRelicAction r:
                    options = DecisionCapture.TreasureRelicOptions(rm.TreasureRoomRelicSynchronizer?.CurrentRelics);
                    chosen = new LegalOption("pick_relic", Index: r.relicIndex ?? -1);
                    break;
                case NetVoteToMoveToNextActAction:
                    chosen = new LegalOption("vote_next_act");
                    options = new() { new DecisionCapture.Option(chosen, new()) };
                    break;
            }
        });
        if (chosen == null) return false;

        int? chosenIndex = DecisionFormat.FindChosenIndex(options.Select(o => o.Identity).ToList(), chosen);
        var notes = new List<string>();
        if (chosenIndex == null) notes.Add("chosen_not_in_enumerated_options");
        EmitWithDecisionRecordLocked(session, ReplayFormat.EvAction, data, kind, player,
            chosen: sem, options: options, chosenIndex: chosenIndex, chosenIndexes: null,
            optionsComplete: chosenIndex != null, selection: null, notes: notes);
        return true;
    }

    /// <summary>Opening the treasure chest: a one-option decision, recorded for its state.</summary>
    public static void OnTreasureChestOpened()
    {
        if (!IsRecording) return;
        var open = new LegalOption("treasure_chest_open");
        RecordDecision("treasure_chest_open", new Dictionary<string, object?>(),
            new TrainingSpec(new List<DecisionCapture.Option> { new(open, new()) }, 0));
    }

    /// <summary>
    /// Capture state synchronously (before the decision mutates anything),
    /// append the event carrying <c>dseq</c>, and queue the record (linked
    /// back by <c>event_seq</c>) for encoding and writing off the game thread.
    /// </summary>
    private static void EmitWithDecisionRecordLocked(
        ReplaySession session,
        string eventType,
        Dictionary<string, object?> eventData,
        string kind,
        Player player,
        object? chosen,
        List<DecisionCapture.Option> options,
        int? chosenIndex,
        IReadOnlyList<int>? chosenIndexes,
        bool optionsComplete,
        object? selection,
        List<string>? notes)
    {
        var state = RunManager.Instance.State;
        DecisionCapture.Capture? capture = null;
        if (state != null)
        {
            try { capture = DecisionCapture.CaptureState(state, player); }
            catch (Exception e) { (notes ??= new()).Add("state_capture_failed: " + e.Message); }
        }

        long dseq = session.NextDecisionSeq();
        eventData["dseq"] = dseq;
        var context = Context();
        long eventSeq = session.Append(eventType, context, eventData);

        if (session.Header.DecisionsFormatVersion == null)
            session.UpdateHeader(h => h.DecisionsFormatVersion = DecisionFormat.FormatVersion);

        long anchor = session.LastSavePointSeq;
        var utc = DateTime.UtcNow;
        var optionJson = options.Select(o => (object?)o.ToJson()).ToList();
        string phase = capture?.Phase ?? DecisionFormat.PhaseRun;
        var stateJson = capture?.State;
        var hidden = capture?.Hidden is { Count: > 0 } h ? h : null;
        string? form = capture?.NetForm;
        var snapshot = capture?.NetSnapshot;
        ulong playerId = player.NetId;

        // Encoding the detached snapshot and JSON-serializing the record run
        // on the background chain, in order with the segment's other writes.
        session.QueueDecisionRecord(() =>
        {
            string? b64 = null;
            if (snapshot != null)
            {
                try
                {
                    var writer = new PacketWriter { WarnOnGrow = false };
                    writer.Write(snapshot);
                    b64 = Convert.ToBase64String(writer.Buffer, 0, writer.BytePosition);
                }
                catch (Exception e)
                {
                    CoreMain.LogDebug($"Run recording: net state encode failed: {e.Message}");
                }
            }
            return DecisionFormat.FormatRecord(new DecisionRecordParts
            {
                DecisionSeq = dseq,
                EventSeq = eventSeq,
                EventType = eventType,
                AnchorSaveSeq = anchor,
                Utc = utc,
                Kind = kind,
                Phase = phase,
                Player = playerId,
                Context = context,
                Chosen = chosen,
                ChosenIndex = chosenIndex,
                ChosenIndexes = chosenIndexes,
                Options = optionJson,
                OptionsComplete = optionsComplete,
                Selection = selection,
                State = stateJson,
                Hidden = hidden,
                NetStateForm = b64 == null ? null : form,
                NetStateBase64 = b64,
                Notes = notes,
            });
        });
    }

    // -------- Selection-screen snapshots (NOverlayStack.Changed / NPlayerHand.ModeChanged) --------

    private static DecisionCapture.Selection? _pendingSelection;
    private static NOverlayStack? _overlay;
    private static NPlayerHand? _hand;

    /// <summary>
    /// Both UI objects are recreated per run / per combat, so subscriptions
    /// are refreshed lazily from frequent capture callbacks. Plain C# events
    /// (the Godot signal raise invokes the delegate); no Harmony involved.
    /// </summary>
    private static void EnsureUiSubscriptionsLocked()
    {
        try
        {
            var overlay = NOverlayStack.Instance;
            if (!ReferenceEquals(overlay, _overlay))
            {
                if (_overlay != null) { try { _overlay.Changed -= OnOverlayChanged; } catch { } }
                _overlay = overlay;
                if (overlay != null) overlay.Changed += OnOverlayChanged;
            }
        }
        catch (Exception e) { CoreMain.LogDebug($"Run recording: overlay subscribe failed: {e.Message}"); }

        try
        {
            var hand = NPlayerHand.Instance;
            if (!ReferenceEquals(hand, _hand))
            {
                if (_hand != null) { try { _hand.ModeChanged -= OnHandModeChanged; } catch { } }
                _hand = hand;
                if (hand != null) hand.ModeChanged += OnHandModeChanged;
            }
        }
        catch (Exception e) { CoreMain.LogDebug($"Run recording: hand subscribe failed: {e.Message}"); }
    }

    private static void UnsubscribeUiLocked()
    {
        if (_overlay != null) { try { _overlay.Changed -= OnOverlayChanged; } catch { } }
        if (_hand != null) { try { _hand.ModeChanged -= OnHandModeChanged; } catch { } }
        _overlay = null;
        _hand = null;
    }

    private static void OnOverlayChanged() => Guard(nameof(OnOverlayChanged), () =>
    {
        var selection = DecisionCapture.SnapshotOverlay(NOverlayStack.Instance?.Peek());
        if (selection == null) return;
        lock (_lock) _pendingSelection = selection;
    });

    private static void OnHandModeChanged() => Guard(nameof(OnHandModeChanged), () =>
    {
        var hand = _hand;
        if (hand == null) return;
        // ModeChanged fires from the CurrentMode setter, before SelectCards
        // stores the prefs and filter; read them once the frame settles.
        Godot.Callable.From(() => Guard("OnHandModeChanged.deferred", () =>
        {
            var selection = DecisionCapture.SnapshotHand(hand);
            if (selection == null) return;
            lock (_lock) _pendingSelection = selection;
        })).CallDeferred();
    });

    // -------- Readable forms --------

    private static Dictionary<string, object?> DescribeNetAction(INetAction net, ulong ownerId)
    {
        var d = new Dictionary<string, object?>();
        Try(() => d["text"] = net.ToString());
        Try(() =>
        {
            switch (net)
            {
                case NetPlayCardAction p:
                    d["kind"] = "play_card";
                    d["card"] = p.modelId.ToString();
                    d["combat_card_index"] = p.card.CombatCardIndex;
                    d["target"] = DescribeCreature(p.targetId);
                    break;
                case NetUsePotionAction u:
                    d["kind"] = "use_potion";
                    d["slot"] = u.potionIndex;
                    d["potion"] = PotionAt(ownerId, (int)u.potionIndex);
                    d["target"] = DescribeCreature(u.targetId);
                    d["target_player"] = u.targetPlayerId;
                    d["in_combat"] = u.enqueuedInCombat;
                    break;
                case NetDiscardPotionGameAction dp:
                    d["kind"] = "discard_potion";
                    d["slot"] = dp.potionSlotIndex;
                    d["potion"] = PotionAt(ownerId, (int)dp.potionSlotIndex);
                    d["in_combat"] = dp.wasEnqueuedInCombat;
                    break;
                case NetEndPlayerTurnAction e:
                    d["kind"] = "end_turn";
                    d["turn"] = e.turnNumber;
                    break;
                case NetUndoEndPlayerTurnAction ue:
                    d["kind"] = "undo_end_turn";
                    d["turn"] = ue.turnNumber;
                    break;
                case NetVoteForMapCoordAction v:
                    d["kind"] = "map_vote";
                    d["source_act"] = v.source.actIndex;
                    d["source"] = Coord(v.source.coord);
                    d["destination"] = v.destination.HasValue ? Coord(v.destination.Value.coord) : null;
                    d["map_generation"] = v.destination?.mapGenerationCount;
                    break;
                case NetMoveToMapCoordAction m:
                    d["kind"] = "move_to_map_coord";
                    d["destination"] = Coord(m.destination);
                    break;
                case NetPickRelicAction r:
                    d["kind"] = "pick_relic";
                    d["index"] = r.relicIndex;
                    Try(() =>
                    {
                        var relics = RunManager.Instance.TreasureRoomRelicSynchronizer?.CurrentRelics;
                        if (r.relicIndex is int i && relics != null && i >= 0 && i < relics.Count)
                            d["relic"] = relics[i].Id.ToString();
                    });
                    break;
                case NetVoteToMoveToNextActAction n:
                    d["kind"] = "vote_next_act";
                    d["current_act"] = n.currentActIndex;
                    break;
                case NetReadyToBeginEnemyTurnAction:
                    d["kind"] = "ready_to_begin_enemy_turn";
                    break;
                case NetConsoleCmdGameAction c:
                    d["kind"] = "console_cmd";
                    d["cmd"] = c.cmd;
                    d["in_combat"] = c.inCombat;
                    d["cheat"] = true;
                    break;
                default:
                    d["kind"] = net.GetType().Name;
                    break;
            }
        });
        return d;
    }

    private static Dictionary<string, object?> DescribeChoice(Player? player, NetPlayerChoiceResult r)
    {
        var d = new Dictionary<string, object?>();
        Try(() => d["indexes"] = r.indexes?.ToList());
        Try(() => d["canonical_cards"] = r.canonicalCards?.Select(c => c?.Id.ToString()).ToList());
        Try(() => d["combat_cards"] = r.combatCards?.Select(c => (object?)new Dictionary<string, object?>
        {
            ["combat_card_index"] = c.CombatCardIndex,
            ["card"] = c.ToCardModelOrNull()?.Id.ToString(),
        }).ToList());
        Try(() => d["deck_cards"] = r.deckCards?.Select(c => (object?)new Dictionary<string, object?>
        {
            ["deck_index"] = c.DeckIndex,
            ["card"] = player != null && c.DeckIndex < player.Deck.Cards.Count
                ? player.Deck.Cards[(int)c.DeckIndex].Id.ToString()
                : null,
        }).ToList());
        Try(() => d["mutable_cards"] = r.mutableCards?.Select(c => c?.Id?.ToString()).ToList());
        Try(() => d["mutable_card_owner"] = r.mutableCardOwner);
        Try(() => d["chosen_player"] = r.playerId);
        return d;
    }

    private static Dictionary<string, object?>? DescribeCreature(uint? combatId)
    {
        if (!combatId.HasValue) return null;
        var d = new Dictionary<string, object?> { ["combat_id"] = combatId.Value };
        Try(() =>
        {
            var creature = CombatManager.Instance.DebugOnlyGetState()?.GetCreature(combatId);
            if (creature == null) return;
            d["monster"] = creature.Monster?.Id.ToString();
            d["player"] = creature.Player?.NetId;
        });
        return d;
    }

    private static string? PotionAt(ulong ownerId, int slot)
    {
        try
        {
            var player = RunManager.Instance.State?.Players.FirstOrDefault(p => p.NetId == ownerId);
            return player?.GetPotionAtSlotIndex(slot)?.Id.ToString();
        }
        catch { return null; }
    }

    private static string Coord(MegaCrit.Sts2.Core.Map.MapCoord? c) =>
        c.HasValue ? $"{c.Value.col},{c.Value.row}" : "none";

    /// <summary>
    /// The game's own packet encoding of a <see cref="CombatReplayEvent"/>,
    /// base64. Decodes with <c>PacketReader.Read&lt;CombatReplayEvent&gt;()</c>
    /// on the same game build (net ids depend on <c>ModelIdSerializationCache</c>
    /// and <c>ActionTypes</c>, which shift between builds — see header.game).
    /// </summary>
    private static string? NetForm(CombatReplayEvent evt)
    {
        try
        {
            var writer = new PacketWriter { WarnOnGrow = false };
            writer.Write(evt);
            return Convert.ToBase64String(writer.Buffer, 0, writer.BytePosition);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"Run recording: net form failed: {e.Message}");
            return null;
        }
    }

    // -------- Session / header helpers --------

    /// <summary>
    /// Open (or create) the recording for <paramref name="gameStartTime"/>.
    /// A newly created recording gets a <c>recording_started</c> event;
    /// <paramref name="midRunIfCreated"/> says whether its first snapshot will
    /// be a mid-run save rather than the run's real start (SpireLens or
    /// recording first saw this run after it began).
    /// </summary>
    private static ReplaySession? EnsureSessionLocked(
        long gameStartTime,
        Func<ReplayHeader>? createHeader,
        bool midRunIfCreated,
        out bool created)
    {
        created = false;
        if (gameStartTime <= 0) return null;
        if (_session != null && _session.GameStartTime == gameStartTime) return _session;

        _session?.Dispose();
        _session = null;
        try
        {
            Func<ReplayHeader>? factory = createHeader == null
                ? null
                : () =>
                {
                    var h = createHeader();
                    h.RecordingStartedMidRun = midRunIfCreated;
                    return h;
                };
            _session = ReplaySession.Open(gameStartTime, factory, out created);
            if (created)
            {
                CoreMain.Logger.Info($"Run recording: started {_session!.Dir}");
                var started = BuildStamp();
                started["mid_run"] = midRunIfCreated;
                _session.Append(ReplayFormat.EvRecordingStarted, Context(), started);
            }
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"Run recording: cannot open recording for {gameStartTime}: {e.Message}");
            _session = null;
        }
        return _session;
    }

    /// <summary>The session for the run currently in progress, opening it lazily (e.g. right after a hot reload).</summary>
    private static ReplaySession? LiveSessionLocked()
    {
        var rm = RunManager.Instance;
        if (rm == null || !rm.IsInProgress) return _session;
        if (rm.NetService?.Type == NetGameType.Replay) return null;
        long start = rm._startTime;
        if (_session != null && _session.GameStartTime == start) return _session;
        var state = rm.State;
        return EnsureSessionLocked(start, state == null ? null : () => HeaderFromRunState(state), midRunIfCreated: true, out _);
    }

    private static Dictionary<string, object?>? Context()
    {
        try
        {
            var rm = RunManager.Instance;
            var state = rm?.State;
            if (state == null) return null;
            var coord = state.CurrentMapCoord;
            return new Dictionary<string, object?>
            {
                ["floor"] = state.TotalFloor,
                ["act"] = state.CurrentActIndex,
                ["coord"] = coord.HasValue ? $"{coord.Value.col},{coord.Value.row}" : null,
                ["in_combat"] = CombatManager.Instance?.IsInProgress == true,
                ["run_time"] = rm!.RunTime,
            };
        }
        catch
        {
            return null;
        }
    }

    internal static string SavePointKey(SerializableRun save)
    {
        var rngs = new List<RngPosition>();
        if (save.SerializableRng?.Rngs != null)
        {
            foreach (var kv in save.SerializableRng.Rngs)
                rngs.Add(new RngPosition("run." + kv.Key, kv.Value.counter, kv.Value.state0));
        }
        if (save.Players != null)
        {
            for (int i = 0; i < save.Players.Count; i++)
            {
                var prngs = save.Players[i]?.Rng?.Rngs;
                if (prngs == null) continue;
                foreach (var kv in prngs)
                    rngs.Add(new RngPosition($"p{i}.{kv.Key}", kv.Value.counter, kv.Value.state0));
            }
        }

        var last = save.VisitedMapCoords != null && save.VisitedMapCoords.Count > 0
            ? save.VisitedMapCoords[^1]
            : (MegaCrit.Sts2.Core.Map.MapCoord?)null;
        return ReplayFormat.BuildSavePointKey(
            save.CurrentActIndex,
            last?.col,
            last?.row,
            save.FloorReached,
            save.PreFinishedRoom?.RoomType.ToString(),
            rngs);
    }

    /// <summary>
    /// Game build identity, stamped on run_started / core_reloaded /
    /// recording_started so a run continued on a different game build (the
    /// header keeps the build it began on) is detectable in the log.
    /// </summary>
    private static Dictionary<string, object?> BuildStamp()
    {
        var d = new Dictionary<string, object?>();
        Try(() =>
        {
            var info = ReleaseInfoManager.Instance.ReleaseInfo;
            d["game_version"] = info?.Version;
            d["game_commit"] = info?.Commit;
        });
        Try(() => d["model_id_hash"] = ModelIdSerializationCache.Hash);
        return d;
    }

    private static ReplayHeader HeaderFromRunState(RunState state)
    {
        var h = NewHeaderWithBuildInfo();
        Try(() => h.SeedString = state.Rng.StringSeed);
        Try(() => h.SeedHash = state.Rng.Seed);
        Try(() => h.Ascension = state.AscensionLevel);
        Try(() => h.GameMode = state.GameMode.ToString());
        Try(() => h.Modifiers = state.Modifiers.Select(m => m.Id.ToString()).ToList());
        Try(() => h.Players = state.Players.Select(p => new ReplayPlayerInfo
        {
            NetId = p.NetId,
            Character = p.Character?.Id.ToString(),
        }).ToList());
        Try(() => h.DailyTimeUtc = RunManager.Instance.DailyTime?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        return h;
    }

    private static ReplayHeader HeaderFromSave(SerializableRun save)
    {
        var h = NewHeaderWithBuildInfo();
        Try(() => h.SeedString = save.SerializableRng?.Seed);
        Try(() => h.Ascension = save.Ascension);
        Try(() => h.GameMode = save.GameMode.ToString());
        Try(() => h.Modifiers = save.Modifiers.Select(m => m.Id?.ToString() ?? "?").ToList());
        Try(() => h.Players = save.Players.Select(p => new ReplayPlayerInfo
        {
            NetId = p.NetId,
            Character = p.CharacterId?.ToString(),
        }).ToList());
        Try(() => h.DailyTimeUtc = save.DailyTime?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        return h;
    }

    private static ReplayHeader NewHeaderWithBuildInfo()
    {
        string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var h = new ReplayHeader { CreatedAtUtc = now, UpdatedAtUtc = now };
        Try(() =>
        {
            var info = ReleaseInfoManager.Instance.ReleaseInfo;
            h.Game.Version = info?.Version;
            h.Game.Commit = info?.Commit;
            h.Game.ReleaseDate = info?.Date.ToString("o", CultureInfo.InvariantCulture);
            h.Game.MainAssemblyHash = info?.MainAssemblyHash;
        });
        Try(() => h.Game.ModelIdHash = ModelIdSerializationCache.Hash);
        Try(() => h.NetType = RunManager.Instance.NetService?.Type.ToString());
        Try(() =>
        {
            h.Mods = ModManager.Mods
                .Where(m => m.state == ModLoadState.Loaded)
                .Select(m => new ReplayModInfo
                {
                    Id = m.manifest?.id,
                    Version = m.manifest?.version,
                    AffectsGameplay = m.manifest?.affectsGameplay ?? true,
                })
                .ToList();
            h.SpirelensVersion = h.Mods.FirstOrDefault(m => m.Id == CoreMain.ModId)?.Version;
        });
        Try(() =>
        {
            var asm = typeof(ReplayRecorder).Assembly;
            h.SpirelensCoreBuild = $"{asm.GetName().Version} ({asm.ManifestModule.ModuleVersionId:N})";
        });
        return h;
    }

    // -------- Guards --------

    private static void Guard(string site, Action body)
    {
        if (_disabled || !_active) return;
        PatchGuard.Run("ReplayRecorder." + site, body);
    }

    private static void Try(Action body)
    {
        try { body(); }
        catch { /* best-effort readable detail; the exact net form is the source of truth */ }
    }
}
