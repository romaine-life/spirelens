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
- `Core/Replay/DecisionCapture.cs` reads the state and the legal options at
  each decision.
- `Core/Replay/DecisionFormat.cs` holds the pure decision-record shape and
  the join logic.
- The hooks are in `Core/Patches/Replay*Patch.cs`.

To turn recording off, set the environment variable
`SPIRELENS_DISABLE_RUN_RECORDING=1`. It is read once per Core load.

## Decisions

These product decisions were made by the user on 2026-10-12. They constrain
every later change to this format.

- **Replay means SpireLens drives the real game as the player.** It is not a
  separate simulator or a video. The replay driver re-issues the recorded
  inputs through the game's own entry points.
- **Recordings play only on the exact game build that made them.** Older
  recordings are kept but are not playable on a newer build. The player must
  refuse a mismatch clearly, comparing `header.game.commit` and
  `model_id_hash`, and never attempt a best-effort replay.
- **A database is wanted for two uses:** an in-game run list, and
  out-of-game stats. It is derived from these files; it does not replace
  them.
- **The user plans to train a model to play from these recordings.** That is
  why every local decision gets a self-contained record (see "Decision
  records"): state, legal options and the choice. Training data must never
  depend on re-simulating, because re-simulation breaks across game builds.
- **Storage:** the recordings live on this laptop, and the raw recordings are
  backed up to a Synology NAS. The NAS sync is outside SpireLens. Files are
  append-only or closed-then-gzipped, so a file-level sync tool sees each
  file change only while it is being written.
- **Past runs:** the roughly 764 existing game `history/*.run` files will be
  imported later as summaries. They are not replayable.
- **Retention: keep everything forever.** That includes abandoned branches,
  old-build recordings and every snapshot. Nothing here deletes recordings.

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
    decisions\s000301.jsonl.gz, ...               per-decision training records, one segment per save point
             \s000377.jsonl                       the open segment (plain until the next save point / run end)
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

## Decision records (training data)

`decisions/` holds one record per decision the **local** player makes. Each
record is self-contained: the full state at that instant, the legal options
that were available, and the option taken. A trainer reads these records
alone. It never re-simulates, so the data stays usable on any later game
build.

`header.decisions_format_version` is the record format version. It is
currently `1`, and each record also carries it as `format_version`. Changes
are **additive only**: new fields may appear, and existing fields keep their
meaning. A breaking change would bump the version.

### Which decisions get a record

| `kind` | source event | options |
|---|---|---|
| `play_card`, `use_potion`, `discard_potion`, `end_turn` | `action` | Every legal combat action: playable hand cards (`CardModel.CanPlay`) × valid targets (`IsValidTarget`, including the no-target case), usable potions (`CombatOnly`/`AnyTime`) × valid targets, potion discards, and end turn. |
| `use_potion` / `discard_potion` out of combat | `action` | `AnyTime` potions × targets, and discards |
| `undo_end_turn`, `vote_next_act` | `action` | the single option |
| `map_vote` | `action` | Map nodes reachable from the current point (`CurrentMapPoint.Children`, or `Map.startMapPoints` at an act start), each with its `point_type`. |
| `pick_relic` | `action` (treasure room) | `TreasureRoomRelicSynchronizer.CurrentRelics` by index, plus skip (`index: -1`) |
| `card_reward` | `choice` | The card reward screen's cards, then its alternatives (reroll and similar, `index = cards + j`). Skip is no index. |
| `card_select` | `choice` | Whatever selection screen offered the choice: choose-a-card, the simple, deck, upgrade, transform, enchant or combat-pile grids, a bundle, a relic, or an in-hand selection. `selection` adds the screen name, `min_select`, `max_select` and `can_skip`. |
| `event_option` | `decision` | the event's current options: `id` = text key, plus `locked`, `proceed` and `relic`. This covers Neow and the Ancients. |
| `rest_option` | `decision` | the rest site options: `id` = option id, plus `enabled` |
| `reward_take`, `reward_skip` | `decision` | the rewards screen's rewards (cards, relic, potion, text), then skip at `index = rewards.Count` |
| `shop_buy`, `shop_remove_card` | `decision` | The whole shop: `id` = category (`character_card`, `colorless_card`, `relic`, `potion`, `card_removal`), `index` = slot, plus cost, stocked and affordable. Purchases made by game logic (`auto`) get no record. |
| `treasure_chest_open` | `decision` | the single option |

Leaving a shop or event is not a separate decision. The next decision is the
`map_vote`.

### Record shape

Each record is one JSON line:

```
format_version, dseq, event_seq, event_type, anchor_save_seq, t, kind,
phase ("combat" | "run"), player, context {floor, act, coord, in_combat, run_time},
chosen          readable form of what was taken (the event's sem / data)
chosen_index    index into options (null if the taken action was not in the enumeration)
chosen_indexes  multi-select choices (card_select / card_reward)
options         [{kind, index?, combat_card_index?, target?, target_player?, slot?, id?, label?, ...detail}]
options_complete  false when the option set is known to be partial (see notes)
selection       {screen, min_select, max_select, can_skip, filter_known}   (choices only)
state           readable state (below)
hidden          information the player could NOT see (below); a trainer should exclude it
net_state       {form, b64}: the game's own exact encoding (below)
notes           ["options_unavailable: ...", "chosen_not_in_enumerated_options", ...]
```

**`state.run`** is always present:

- `character`, `hp`, `max_hp`, `gold`, `floor`, `act`, `coord`, `room_type`, `ascension`, `max_energy`
- `deck`: a list of `{id, upgrade, enchantment{id, amount}, deck_index}`
- `relics`: a list of `{id, counter?, status}`
- `potions`: one entry per slot, either `{slot, id, usage, target_type}` or `null`

**`state.combat`** is present in combat:

- `round`, `side`, `turn`, `energy`, `max_energy`, `stars`, `hp`, `max_hp`, `block`
- `powers`: `[{id, amount}]`
- `orbs`: `[{id, passive, evoke}]`
- `hand`: one entry per card, with:
  - `id`, `upgrade`, `enchantment`
  - `combat_card_index`, `type`, `target_type`
  - `cost` (with modifiers), `star_cost`, `affliction`
  - `playable` and `unplayable_reason`
  - `hand_index`
- `draw_pile_count` and `draw_pile`. The draw pile is **sorted**: its
  composition is visible in game, but its order is not.
- `discard_pile` and `exhaust_pile`, in pile order
- `creatures`: one entry per combatant, with:
  - `combat_id`, `side`, `monster` / `player`
  - `hp`, `max_hp`, `block`, `alive`, `powers`
  - for monsters, `intents`: `[{type, damage?, hits?}]`. Damage is the same
    preview the intent UI shows. A hidden intent reports only its type.

**`hidden`** holds information the player cannot see:

- `draw_pile_order`: the true order of the draw pile;
- `monster_moves`: each monster's next move id.

**`net_state`** is the game's own exact encoding, base64 of `PacketWriter`
output:

- In combat, `form` is `NetFullCombatState`, built by `NetFullCombatState.FromRun`.
  This is the same state the game checksums for multiplayer desync detection:
  creatures, powers, piles **in order**, orbs, potions, relics and **RNG
  state**.
- Out of combat, `form` is `SerializablePlayer`, built by `Player.ToSerializable`.
  It holds the deck, relics, potions and the player's RNG.

Both forms contain hidden information (draw order, RNG) and decode only on the
same build, so treat them as an audit and exactness channel, not as trainer
input.

### Joining and branches

- Every decision event in `events.jsonl` (`action`, `choice` or `decision`)
  carries `data.dseq`.
- The record carries `event_seq` (the event's `seq`) and `event_type`.
- `dseq` is monotonic per run and resumes after a hot reload or restart from
  the last `dseq` linked in `events.jsonl`.
- **Records follow the event timeline.** A record is canonical if and only if
  its `event_seq` is on the canonical path, so a load cuts records exactly
  like events (`DecisionFormat.CanonicalRecords`).
- `anchor_save_seq` is the `save_point` the record's segment belongs to.

### Storage choice: segmented by save point, gzipped when closed

Records go to `decisions/s<anchor>.jsonl`, where `<anchor>` is the seq of the
`save_point` that opened the segment, or `0` before the first save. Each new
`save_point` closes the previous segment, and so does the run end. Closing
gzips the segment off the game thread into `s<anchor>.jsonl.gz`; a reopened
segment gets `s<anchor>.<n>.jsonl.gz`. A Core load sweeps up stale plain
segments.

Why this layout:

- **It keeps `events.jsonl` light.** Records are about 5-30 KB each, mostly
  state. The event log stays small and grep-able.
- **Segment boundaries are exactly the points where a load can cut the
  timeline.** A branch is a contiguous set of whole segments, plus at most
  the tail of one.
- **Only the current room is uncompressed.** A crash loses no closed data,
  because closed segments are complete gzip files, and at most the
  still-queued tail of the open one.
- **Closed files never change again**, which suits the NAS backup.

### Game-thread cost (expected; not measured)

Synchronous, on the game thread, before the decision applies:

- the readable state;
- the legal-option enumeration. In combat this is about hand × (creatures + 1)
  `CanPlay`/`IsValidTarget` calls, plus one damage preview per monster intent;
- the detached snapshot (`NetFullCombatState.FromRun` or `Player.ToSerializable`,
  which copy cards, relics and RNG);
- one events.jsonl line.

The expected cost is roughly 0.3-2 ms per decision, depending on hand and deck
size. Packet-encoding the snapshot, JSON-serializing the record, appending it
and gzipping closed segments all run on the background chain. Decisions come
at human pace, a few per second at most.

### Capture points

No new Harmony targets; see the primer.

- Actions use the existing `ActionQueueSet.ActionEnqueued` subscription. It
  fires before the action executes, so the state is pre-decision.
- Choices use the existing `PlayerChoiceSynchronizer.PlayerChoiceReceived`.
  The offered options come from a snapshot taken when the selection UI opened:
  - **Overlay screens:** `NOverlayStack.Changed`, a plain C# event raised on
    every push. The handler reads the top screen's `_cards`, `_options`,
    `_extraOptions`, `_bundles`, `_relics` and `_prefs`.
  - **In-hand selections:** `NPlayerHand.ModeChanged`. Prefs and filter are
    read with a deferred call, because `SelectCards` sets them after the mode
    change.

  The screens close before the choice syncs, which is why the snapshot is
  taken at open. Both subscriptions are refreshed lazily, because the objects
  are recreated per run and per combat. They are dropped at Core shutdown.
- Non-action decisions use the existing decision prefixes (event, rest site,
  reward, shop, treasure chest). Those were installed by the previous build;
  their hot-reload behavior is covered in the primer.

### Known limits

- **Stale selection snapshots.** A choice made without a preceding snapshot
  (for example an auto-resolved selection with no screen) is recorded with
  `options: []` and a note. A snapshot left over from a screen that never
  produced a choice could be attached to the next choice. The record's
  `selection.screen` lets a trainer check consistency.
- **Treasure relic voting** is recorded per vote. In singleplayer, the vote is
  the pick.
- **Multiplayer:** records are written only for the local player.
- **Potion usability** is decided by `Usage` and `IsValidTarget`. Any
  per-potion custom usability check the game applies elsewhere is not
  modeled. The option set can then be slightly too large; it is never too
  small.

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

One subscription is made once per Core load rather than per run:

- `SaveManager.Instance.Saved`, which forwards `RunSaveManager.Saved`, produces
  `save_point` and the snapshot. `RunSaveManager.SaveRun(SerializableRun save,
  bool isMultiplayer)` raises it right after writing the file. That method
  handles every run-save write, including the `IncrementNumReloads` re-save on
  each load.
  - The handler reads back the file the game just wrote (the newer of
    `current_run.save` and `current_run_mp.save`), so the snapshot is
    byte-for-byte the save.
  - It deserializes the file with `JsonSerializationUtility.GetTypeInfo<SerializableRun>()`
    to compute the key and fields.

  The handler runs after the game's `await` of the file write. It may
  therefore land on a thread-pool thread, and it touches run state read-only.

**Why an event and not a Harmony prefix.** The first build prefixed
`SaveRun(SerializableRun, bool)`. Live, after a Core hot reload with no game
restart, Harmony listed the method as patched, yet the prefix never ran: zero
`save_point` events over six floors. The method's only callers are
`SaveRun(AbstractRoom)` in the same class and `SaveManager.IncrementNumReloads`.
Both were compiled before the hot reload added the detour, and they kept
calling the original. This is the primer's "Newly Added Harmony Targets Need
One Full Restart" case. The existing `RunSaveSnapshotPatch` on the
`AbstractRoom` overload works because it has been patched since process start.
A public event works from the moment it is subscribed, with no restart.

Harmony patches (prefixes unless noted):

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
- per save, one read-back and one deserialize of the run save the game just
  wrote (50-70 KB);
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
