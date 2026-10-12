# Run Recording

SpireLens records every run to disk so that a future replay driver can rebuild
or re-simulate it, and so that a database importer can load it. Today this is
**capture only**: nothing replays a run yet, and nothing in the stats UI reads
these files.

Code:

- `Core/Replay/ReplayRecorder.cs` does the capture.
- `Core/Replay/ReplayStorage.cs` handles files and sessions.
- `Core/Replay/ReplayFormat.cs` holds the pure format logic: line shape,
  save-point key and timeline cut. It has no game dependencies, so an offline
  tool can reuse it.
- The hooks are in `Core/Patches/Replay*Patch.cs`.

To turn recording off, set the environment variable
`SPIRELENS_DISABLE_RUN_RECORDING=1`. It is read once per Core load.

## What is recorded, and why it is enough to replay

- **Combat is deterministic lockstep.** Slay the Spire 2 multiplayer replays
  every input on every peer and compares checksums after each action. A run
  is therefore its starting state plus its ordered inputs.
- **The run save carries all RNG state.** `current_run.save` holds every RNG
  stream's counter and state, plus the unlock state and the act and modifier
  setup. So the starting state is the **save**, not the seed.
- **We record three layers:**
  1. **Snapshots.** The exact `SerializableRun` JSON at every game save point.
     The first one is the genesis state. Each later one is a resumable
     checkpoint. Snapshots stay loadable across game updates through the
     game's own save migrations.
  2. **The input log** (`events.jsonl`). Every game action, player choice and
     non-action decision, in order. Each action is stored in two forms:
     - the game's own packet encoding, which is exact but tied to one build;
     - a readable JSON form, which is portable.

     The log also holds the state checksum *values* the game computes, so a
     replay can detect divergence.
  3. **The game's own combat replays.** A copy of the `replays/latest.mcr` the
     game writes at each combat end. The game overwrites that single file
     every combat; we keep one per combat.

## On-disk layout

```
%APPDATA%\SlayTheSpire2\SpireLens\replays\        (user://SpireLens/replays/)
  <game_start_time>\                              the game's own run id, = {start_time}.run in history/
    header.json
    events.jsonl
    snapshots\0000.json.gz, 0001.json.gz, ...     gzip of the exact current_run.save JSON
    combats\0000-f03.mcr, ...                     game-native CombatReplay (PacketWriter binary), NNNN-f<floor>
```

This is deliberately **not** under `user://SpireLens/runs/`, which
`RunStorage` enumerates as run records. The directory is keyed by the game's
`start_time`, which stays stable across Continue, Core hot reload and a game
restart. `header.json` also carries the SpireLens `run_id`.

### header.json

This file is snake_case JSON with an explicit `format_version`. It is a new
format, unlike the structurally-versioned `RunData`. Fields:

- `format_version`, `game_start_time`, `spirelens_run_id`, `created_at_utc`, `updated_at_utc`
- `recording_started_mid_run`: true when the first thing recorded was a load
  of an existing save rather than the run's start. In that case the genesis
  snapshot is not the real run start.
- `game`:
  - `version`, `commit`, `release_date` and `main_assembly_hash`, from
    `release_info.json`;
  - `model_id_hash`, which is `ModelIdSerializationCache.Hash`. This content
    table is what the binary net forms index into.
- `mods`: loaded mods, each with `id`, `version` and `affects_gameplay`.
  Also `spirelens_version` and `spirelens_core_build`.
- `net_type`, `seed_string`, `seed_hash`, `players` (`net_id`, `character`),
  `ascension`, `game_mode`, `modifiers`, `daily_time_utc`
- `outcome`, which is one of `in_progress`, `win`, `loss` or `abandoned`.
  Also `ended_at_utc` and `final_floor`.

The header is written when the recording is created and rewritten (atomically)
at run end and on a load that un-ends a run. The header keeps the build the
run *started* on. `run_started`, `core_reloaded` and `recording_started` events
also stamp `game_version`, `game_commit` and `model_id_hash`, so a run
continued on a newer build is detectable.

### events.jsonl

The log is append-only, with one JSON object per line. Each line is flushed as
it is written. Envelope fields:

| field | meaning |
|---|---|
| `seq` | monotonically increasing per run. It resumes from the file tail after a hot reload or a restart. |
| `t` | UTC timestamp, millisecond precision |
| `type` | event type (below) |
| `floor`, `act`, `coord`, `in_combat`, `run_time` | context, present while a run is live |
| `data` | payload |

A crash can leave a torn last line. Readers must skip unparsable lines.
`ReplayFormat.TryParseEnvelope` does this. The writer starts its next line on
a fresh line.

Event types:

| type | data |
|---|---|
| `recording_started` | `mid_run`, plus the build stamp |
| `run_started` | `continued` (true when the game resumed from a save), `net_type`, plus the build stamp. The game fires RunStarted again on every Continue. |
| `core_reloaded` | build stamp. A Core hot reload happened mid-run. Nothing in the game was replayed. |
| `save_point` | `key`, `snapshot` (relative path), `reused`, `num_reloads`, `floor`, `act`, `pre_finished_room`, `is_multiplayer`, `bytes` |
| `load` | `key`, `reason` (`continue`, `room_restart` or `death_undo`), `num_reloads_before`, `floor`, `act`, `pre_finished_room`, `set_up` |
| `action` | `action_id`, `player`, `class`, `action_type`, `player_driven`, `executing_action_id`, `net_type_id`, `net`, `sem` |
| `hook_action` | `action_id`, `player`, `hook_id`, `action_type`, `net`. Automatic hook actions; needed only for game-native `Replay`-mode playback. |
| `action_resumed` | `action_id`, `net`. An action paused for a player choice resumed. |
| `action_result` | `action_id`, `state` (`Finished` or `Canceled`). Player-driven actions only. |
| `choice` | `player`, `choice_id`, `choice_type`, `net`, `sem`. Every `PlayerChoiceSynchronizer` choice: card reward picks, rerolls and alternatives; hand, deck and relic selections; the card picked for shop removal. |
| `checksum` | `id`, `value`, `context`. The game's state checksum, value only. The full state dump lives in the `.mcr`. |
| `decision` | `kind` plus fields (below) |
| `combat_replay_file` | `file`, `bytes`, `events`, `stop_recording` |
| `run_end` | `outcome`, `floor`, `in_combat` |

`net` is the base64 of the game's own `PacketWriter` encoding of a
`MegaCrit.Sts2.Core.Multiplayer.Replay.CombatReplayEvent`. That is exactly what
the game puts in its `.mcr`. Decode it with
`var reader = new PacketReader(); reader.Reset(bytes); reader.Read<CombatReplayEvent>()` on the
**same game build**: ModelId net ids and `ActionTypes` ids shift between
builds, so check `header.game.commit` and `model_id_hash` first.

`sem` is the readable form. For actions it has a `kind`:

- `play_card`: `card`, `combat_card_index`, and `target` (`combat_id`,
  `monster`, `player`)
- `use_potion`
- `discard_potion`
- `end_turn` and `undo_end_turn`
- `map_vote`: `source_act`, `source`, `destination`, `map_generation`
- `move_to_map_coord`
- `pick_relic`
- `vote_next_act`
- `ready_to_begin_enemy_turn`
- `console_cmd`, which carries `cheat: true`

Every `sem` also has the action's `text`.

`decision.kind` values:

| kind | fields | game method |
|---|---|---|
| `event_option` | `index`, `page`, `shared`, `event`, `option` (text key), `is_proceed` | `EventSynchronizer.ChooseLocalOption(int index)` |
| `rest_option` | `index`, `option_id` | `RestSiteSynchronizer.ChooseLocalOption(int index)` |
| `reward_take` | `set_id`, `index`, `reward_type`, `reward` | `RewardsSetSynchronizer.SelectLocalReward(Reward reward)` |
| `reward_skip` | `set_id` | `RewardsSetSynchronizer.SkipLocalRewardsSet()` |
| `shop_buy` | `category` (`character_card`, `colorless_card`, `relic`, `potion`), `slot`, `model`, `cost`, `auto` | `MerchantEntry.OnTryPurchaseWrapper(MerchantInventory?, bool ignoreCost)` |
| `shop_remove_card` | `cost`, `auto`, `cancelable` | `MerchantCardRemovalEntry.OnTryPurchaseWrapper(MerchantInventory?, bool, bool)` |
| `treasure_chest_open` | none | `OneOffSynchronizer.DoLocalTreasureRoomRewards()` |

`auto: true` marks purchases made by game logic (Lord's Parasol uses
`ignoreCost`), not by the player. A replay driver must not re-issue them.
Purchase *attempts* are logged, so a failed attempt (not enough gold) shows up
too. Re-issuing one is a deterministic no-op.

Not captured yet:

- the Crystal Sphere minigame's cell and tool clicks
  (`CrystalSphereMinigame.SetTool` / `CellClicked`), whose results are captured
  only through state;
- cursor, hover and map drawing (cosmetic);
- multiplayer remote players' non-action decisions.

## Save points, loads and the canonical timeline

The game saves only at these points:

- map-node entry, before the room is rolled;
- combat victory, with a pre-finished room;
- a finished Ancient event;
- run creation;
- the `IncrementNumReloads` re-save on every load.

Main-menu Continue, SpireLens room restart and SpireLens death undo all reload
the last save. Inputs recorded after that save therefore did not happen in the
surviving timeline. The log is never rewritten. Instead:

- every save writes `save_point(key)`;
- every resume writes `load(key, reason)` before the game's re-save.

**Key.** `ReplayFormat.BuildSavePointKey` combines:

- act, last visited map coord, floor, and the pre-finished room type;
- an FNV-1a hash of every run and player RNG stream's `(name, counter, s0)`.

RNG counters move with every random draw, so equal keys mean equal progress,
whatever `save_time`, `run_time` or `num_reloads` say. The same function runs
on the save being written and on the save being loaded, which is how a load is
matched to its save point. A save whose key equals the previous one reuses
that snapshot file instead of storing a duplicate (the reload re-save is the
common case).

**Canonical timeline.** `ReplayFormat.BuildCanonicalTimeline` walks the log in
`seq` order:

- On `load(K)`, it truncates the current path back to the most recent
  `save_point(K)`. The cut events are kept as a `ReplayBranch`, which is a
  save-scum or restart record, and the path continues.
- A load whose key never appeared starts a new **unanchored** root. This
  happens when recording began mid-run, or the save came from before
  recording was enabled.
- The canonical outcome is the last `run_end` on the canonical path. A death
  undo cuts the `run_end(loss)` away, and the header is reset to
  `in_progress` when the load happens.

**Specific situations:**

- **Abandon mid-combat:** the inputs stay, and `run_end{outcome: abandoned, in_combat: true}` ends the log. The partial combat's `.mcr` is copied too, because CleanUp writes it.
- **Save and Quit, then Continue:** the `load` cuts everything after the last save.
- **Core hot reload:** only a `core_reloaded` marker appears. The game replays nothing, so there is no cut.
- **Won combat, then Continue:** the save is the post-victory pre-finished-room save. Its key differs from the room-entry one in both floor and RNG, so the replay resumes at the reward screen.

## Hooks and hot-reload safety

Subscriptions to public game events, re-established per run at `RunStarted`
and on Core `Initialize` when a run is live:

- `RunManager.Instance.ActionQueueSet.ActionEnqueued` (`Action<GameAction>`) and
  `.ActionResumed` (`Action<uint>`). These are the same choke points the
  game's own `CombatReplayWriter` uses. They fire for combat and non-combat
  actions alike: cards, potions, end turn, map votes and moves, treasure relic
  picks, next-act votes, console commands and hook actions.
- `RunManager.Instance.PlayerChoiceSynchronizer.PlayerChoiceReceived`
  (`Action<Player, uint, NetPlayerChoiceResult>`). It fires inside
  `SyncLocalChoice` in singleplayer too.
- `RunManager.Instance.ChecksumTracker.ChecksumGenerated`
  (`Action<NetChecksumData, string, NetFullCombatState>`).
- `RunManager.Instance.ActionExecutor.AfterActionExecuted`, plus a per-action
  `GameAction.BeforeCancelled` for player-driven actions.

Harmony patches (prefixes unless noted):

- `RunSaveManager.SaveRun(SerializableRun save, bool isMultiplayer)` → `save_point` plus the snapshot
- `RunManager.SetUpSavedSingleplayer(RunState state, SerializableRun save)` and
  `RunManager.SetUpSavedMultiplayer(RunState state, LoadRunLobby lobby)` → `load`
- `RunManager.WriteReplay(bool stopRecording)`, prefix and postfix → `.mcr` copy
- the decision methods in the table above
- run end: the existing `RunHistoryUtilities.CreateRunHistoryEntry` postfix
  (`RunHistoryUtilitiesCreateEntryPatch`) also calls `ReplayRecorder.OnRunEnded`
  with its own `PatchGuard`

`RoomResetter` calls `ReplayRecorder.SetPendingLoadReason` immediately before
`SetUpSavedSingleplayer`, so restarts and death undos are labeled.

`CoreMain.Shutdown` → `ReplayRecorder.TeardownHooks`:

1. Mark the recorder inactive. Any stray callback still reaching the orphaned
   assembly becomes a no-op.
2. Unsubscribe all per-run events.
3. Close `events.jsonl`.
4. Wait up to 2 s for queued snapshot, `.mcr` and header writes.

The new Core recovers `seq` and file numbering from disk.

Cost on the game thread:

- per event, one small JSON line plus a `FileStream.Flush`, which lands in
  the OS cache;
- per save, one extra `JsonSerializer` pass over the save (the game does the
  same pass itself);
- per combat end, one read of the `.mcr` (35-270 KB).

Gzip and all file writes other than the per-event line run on one ordered
background task chain.

## For a future replay driver

- **Same build:** start from `snapshots/<n>` for any canonical `save_point`.
  Load it the way `RoomResetter.RestartAsync` does
  (`RunState.FromSerializable`, `SetUpSavedSingleplayer`, `NGame.LoadRun`).
  Then re-issue the canonical inputs until the next `save_point`:
  1. actions: decode `net`, then `RunManager.ActionQueueSynchronizer.RequestEnqueue(netAction.ToGameAction(player))`;
  2. choices: a `CardSelectCmd` selector (`ICardSelector`) or `PlayerChoiceSynchronizer.ReceiveReplayChoice`;
  3. decisions: call the same synchronizer and merchant methods listed above.

  Compare `checksum` values as you go, and compare the next save point's key.
  Every room starts from its own snapshot, so a divergence in one room does
  not poison the rest.
- **Per combat:** each `combats/*.mcr` can be fed through the game's own debug
  player sequence (`NMultiplayerTest.RunReplay`): `RunManager.SetUpReplay`,
  fast-forward the ids, `LoadIntoLatestMapCoord`, then feed the events. The
  built-in checksum comparison then reports divergence.
- **Timing:** the action queue can cancel inputs depending on timing (cards
  queued while a choice or end-turn is in flight). `executing_action_id` and
  `action_result` let a driver pace inputs the same way and detect a cancel
  that did not reproduce.
- **Different build:** binary `net` forms are unusable, because net ids shift.
  The `sem` forms plus the snapshots still give a portable floor-by-floor
  reconstruction.

## For a future database importer

- Treat these files as the source of truth, and build the database out of
  process from them. Keep SQLite out of the hot-reloaded Core: native DLL
  loading, file locks across reloads, and connection pooling all get in the
  way.
- One run is one directory. `header.json` is the run row.
- Parse `events.jsonl` line by line and skip unparsable lines. Run
  `BuildCanonicalTimeline` (or port it: the logic is about 40 lines) to flag
  canonical versus abandoned-branch events.
- Snapshots are gzip JSON in the game's own `SerializableRun` shape (the same
  as `current_run.save`). Join a snapshot to its event through
  `save_point.data.snapshot`.
- The game's `saves/history/{start_time}.run` and SpireLens's
  `runs/<run_id>.json` (`game_start_time`) join on the same key.
