# Replay Player

SpireLens can replay a recorded run (see [run-recording.md](run-recording.md)) **in the real game**. The game itself is the
player. SpireLens loads the run's first snapshot and then feeds the recorded inputs, in order, through the same entry
points a player's clicks reach. The game simulates everything, including combat, RNG, rewards and events, exactly as it
did the first time. A replay plays only on the exact game build that recorded it.

## Using it

From the main menu, open **Settings → Mods → SpireLens** and scroll to the **Replay** section. Each recording has its
own row with a **Play** button. Navigate the page with the mouse, keyboard or controller as on any BaseLib page.

As a shortcut, the SpireLens overlay (Left Shift or R3, from the main menu) also has **Replay → Replay a run…**, which
opens the same list as a modal. There, Up/Down, the D-pad or the left stick select, Enter or A plays, and Esc or B
closes it.

Both show the 60 newest recordings, newest first:

- each row gives the date, character, ascension, outcome and final floor;
- a recording made on another game build is listed but greyed out with the reason;
- a recording made before SpireLens or recording was enabled is marked "(from mid-run)" and starts from its earliest
  snapshot.

Both use the same loading and start code (`ReplayPickerMenu.LoadListings` / `TryStart`). A replay can start only from
the main menu; opened in a run, the Mods page shows why and greys out the Play buttons. The Mods page's Replay section
is drawn by the Core each time the page opens, so it is current and follows Core hot reloads; if no Core is loaded the
page says so in one line.

Above the Replay section, the Mods page draws a **Playlists** section. Each saved playlist is shown with its name, run
count and playable count, and a Play button; a playlist with nothing playable shows "Unavailable". The section also
has a one-line hint that playlists are made in the web page (`spirelens-db serve`, http://127.0.0.1:8737). It is
rebuilt every time the page opens. See [Playlists](#playlists).

To stop a replay: press **Esc**, choose **Stop replay** in the SpireLens overlay, or wait for the end. (The Mods page
also shows Stop replay while one plays, but playback swallows the clicks needed to get there.) The game returns to the main menu and your data is restored. A finished
replay stays on the final screen until you exit.

### Controls during playback

| | Keyboard | Controller |
|---|---|---|
| Pause / resume | Space | Select (Back) |
| Step one input (while paused) | `.` or → | D-pad → |
| Slower / faster (0.5x, 1x, 2x, 4x, max) | `-` / `=` (also `[` / `]`, keypad −/+) | LB / RB |
| Restart the current floor | R | Y |
| Previous / next floor | Page Up / Page Down | D-pad ↓ / ↑ |
| Previous / next run (playlists) | B / N | LT / RT |
| Restart the current run | Home | X |
| Exit (back to the main menu; ends the whole playlist) | Esc | SpireLens menu (R3) → Stop replay |
| SpireLens menu | Left Shift | R3 |

The status bar sits bottom-left. It shows playing or paused, the speed, the floor (`floor N/total`), the input index
(`input i/n`) and the resync count, plus a short note when something happens. While a playlist plays, it adds a line
with the playlist name and `run i/N — <label>`.

**Pause** stops feeding at the next input boundary. Animations still finish.

**Speed** sets `Engine.TimeScale` (0.5–4) and the delay between inputs. **Max** uses 4x time and no delay.

**Restart floor** reloads the recording's snapshot from the start of the current floor and replays that floor's inputs.
**Previous/next floor** loads that floor's start snapshot and continues from there.

While a replay plays, **every other click, key and controller button is swallowed**, so you cannot act on the replayed
run. Mouse movement still passes, so hover tooltips work. Left Shift / R3 (the SpireLens menu) and F5 (Core hot reload)
also pass.

## Playlists

A playlist plays a list of recordings back to back. Playlists are made in the run-database web page
(`spirelens-db serve`, at http://127.0.0.1:8737; see [run-database.md](run-database.md)). The page filters runs and
writes playlist files. The game reads those files and plays them. Saved playlists are listed with Play buttons in the
**Playlists** section of the Mods page.

### Playing a list

- **Each run plays exactly like a single replay**: it starts from its first snapshot, uses the same keyframes and
  resync, and shows the same controls.
- **Moving on.** When a run reaches its recorded end, or fails in a way playback cannot recover from (it diverged past
  its last save point, or a load threw), the next run starts after a short pause. That pause is 4 s at 1x, shorter at
  higher speeds, and held while paused.
- **Unplayable runs.** A run whose recording is missing, was made on another build, or can't be read is skipped, with
  a log line and a short note.
- **Manual navigation.** B/N (LT/RT) move to the previous or next run; Home (X) restarts the current one.
- **Exiting** (Esc, or Stop replay in the SpireLens menu) stops the whole list, returns to the main menu and restores
  your data.

**The data guard covers the whole list.** It takes one backup before the first run and does one restore when the list
ends or is exited. Nothing between runs writes your data, because every run is a playback run under the same guard.
A backup per run would only copy identical files again, and restoring between runs would need a trip through the main
menu for nothing. The backup's marker names the first run; crash and hot-reload recovery recognize any playback run,
because a playback run never saves.

### File contract (shared with the web page)

The directory is `%APPDATA%\SlayTheSpire2\SpireLens\playlists\` (`user://SpireLens/playlists/`).

**Saved playlists** are one file per playlist, `<slug>.json`:

```json
{"format_version": 1, "name": "Regent wins", "created_at_utc": "...", "updated_at_utc": "...",
 "filter": {"...": "informational, or null"},
 "runs": [{"game_start_time": 1792120868, "label": "A10 win, floor 49"}]}
```

- `game_start_time` is the recording's directory name under `replays/` (the game's run id).
- `label` is optional; without it the bar shows the recording's own description.
- Unknown fields are ignored.
- A file that can't be read is skipped with a log line: invalid JSON, no `runs` array, or `format_version` below 1.
- A run entry without a positive integer `game_start_time` is skipped.
- `play-now.json` is never treated as a playlist.
- A slug containing a path separator or `..` is refused.

**Play now** is the file `play-now.json` in the same directory. It names either a saved playlist or inline runs:

```json
{"format_version": 1, "requested_at_utc": "...", "playlist": "<slug>"}
{"format_version": 1, "requested_at_utc": "...", "runs": [{"game_start_time": 1792120868, "label": "..."}]}
```

While SpireLens Core is loaded, the game checks this file whenever it changes, whenever a main menu becomes ready, when
a replay ends, and once when Core loads. Nothing polls:

- **At the main menu with no replay playing**, it consumes the request by deleting the file and starts the list.
- **A file that doesn't parse** may have been caught mid-write. The writer's next write re-checks it; if it still
  doesn't parse 3 s after it was written, it is deleted with a toast.
- **A file still held open by its writer** is retried every 0.25 s, at most 20 times, since Windows signals no "file
  closed" event.
- **Otherwise** (mid-run, or a replay already playing), the file stays where it is. A toast says why, once per file
  version.

The page treats a file still present after about 10 s as "not picked up". `ReplayPlayNowWatcher` uses a
`FileSystemWatcher` on the playlists directory and the scene tree's `node_added` signal (an `NMainMenu`'s `ready`).
Watcher events arrive on thread-pool threads and are collapsed into one `CallDeferred` onto the main thread. Both are
released at Core shutdown, and a generation counter drops callbacks queued by an earlier Core. No game hook is involved.

## How it works

### Starting

`ReplayPlaybackPlan.Build` reads `events.jsonl` and works on the canonical timeline. That is the same cut
`ReplayFormat.BuildCanonicalTimeline` applies, so the abandoned attempts of room restarts, death undos and Continues are
skipped. It reduces the timeline to a list of steps:

| step | from | driver |
|---|---|---|
| **Keyframe** | `save_point` that is not `reused` | Wait for the game's own save point there and compare keys. |
| **Input** | `action` that is player-driven: `play_card`, `use_potion`, `discard_potion`, `end_turn`, `undo_end_turn`, `map_vote`, `pick_relic`, `vote_next_act` | Fed. |
| **Input** | `choice` | Fed. |
| **Input** | `decision`: `event_option`, `rest_option`, `reward_take`, `reward_skip`, `shop_buy`, `shop_remove_card`, `treasure_chest_open`, except `auto: true` | Fed. |
| **Checksum** | `checksum` | Compared in order with the replay's checksums. Logged only. |
| **RunEnd** | `run_end` | Playback is finished. |

The following are **not fed**, because the game derives them again itself in singleplayer:

- the move that follows a map vote;
- ready-to-begin-enemy-turn;
- hook actions;
- action resumes.

`reused` save points are the game's reload re-save, which playback never performs. Console commands are skipped and
counted, because they are cheats.

Playback loads the first keyframe's snapshot with the main menu's Continue sequence: `RunState.FromSerializable`, run
set-up, `NGame.LoadRun`. The run set-up is **`ReplayPlayer.SetUpForPlayback`**. It runs
`RunManager.SetUpSavedSingleplayer`'s own `InitializeShared` → `InitializeRunLobby` → `InitializeSavedRun` sequence,
with these differences:

- `shouldSave: false`;
- no `SaveManager.IncrementNumReloads`;
- no daily time.

### Feeding inputs

Each frame, the driver asks `ReplayInputFeeder.TryFeed` whether the next input can be fed. If it can, the feeder feeds
it. Readiness means the game is in the state the player was in when they made that decision:

- **Combat:** combat in progress, in the player's play phase, actions enabled, the action queue and executor idle, the
  hand in Play mode and no card in flight.
- **Screens:** the expected screen is open and waiting.

Inputs are fed through these entry points:

| input | how |
|---|---|
| play card | `ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target))`. The card is resolved from the recorded `NetCombatCard` index and the target from its combat id. This is the UI's own call. |
| use potion | `PotionModel.EnqueueManualUse(target)` |
| discard potion / undo end turn | decoded net action → `RequestEnqueue(net.ToGameAction(player))`, as `NPotionPopup` / `NEndTurnButton` do |
| end turn | `PlayerCmd.EndTurn(player, canBackOut: false)` |
| map vote | `NMapScreen.OnMapPointSelectedLocally(point)` on the `Travelable` node. If the map is not open yet, the driver first clicks what the player clicked to leave the room: the room's proceed button, the rewards screen's proceed, or the shop inventory's back button. Those clicks are not recorded. |
| treasure relic / chest | `TreasureRoomRelicSynchronizer.PickRelicLocally(index)`; `NTreasureRoom.OpenChest()` |
| next act | the boss rewards screen's proceed button, which calls `ActChangeSynchronizer.SetLocalPlayerReady` |
| event / rest option | `ForceClick` on the `NEventOptionButton` / `NRestSiteButton` for that option. Ancient dialogue is clicked through first. |
| reward take / skip | `ForceClick` on the `NRewardButton` holding that reward, or on the rewards screen's proceed button |
| shop buy / card removal | `MerchantEntry.OnTryPurchaseWrapper(inventory)` / `MerchantCardRemovalEntry.OnTryPurchaseWrapper(inventory, false, cancelable)`, with the inventory UI opened as the player had it |
| card selections (hand, combat pile, choose-a-card, simple grid, deck upgrade, transform, enchant or remove) | The game's own seam: `CardSelectCmd.PushSelector(selector, localOnly: true)`. Every `CardSelectCmd` path asks `LocalSelector` instead of opening its screen, after reserving the choice id as usual. `ReplayCardSelector` completes the request with the cards the recorded `NetPlayerChoiceResult` names, decoded by the game's own `PlayerChoiceResult.FromNetData`. |
| card reward / relic / bundle screens | These screens are shown even with a selector, so the driver completes them: `NCardRewardSelectionScreen.SelectCard` / `OnAlternateRewardSelected` / its completion source (skip); `NChooseARelicSelection` and `NChooseABundleSelectionScreen` completion sources |

After each input, the driver waits a pace delay before the next one. The delay is 650 ms for combat inputs, 900 ms for
selections and 1100 ms for screen decisions, divided by the speed.

### Keyframes and resync

The game reaches `RunSaveManager.SaveRun(AbstractRoom)` at every save point, even with ShouldSave off: the write itself
is skipped inside that method. The existing `RunSaveSnapshotPatch` prefix hands each save point to
`ReplayPlayer.OnGameSavePoint`. That computes `RunManager.ToSave(preFinishedRoom)`, which is the save the game would
have written, and its key with the recorder's `SavePointKey`.

When the driver reaches a keyframe step, it takes the next observed key and compares it.

**Divergence** is any of:

- a keyframe key mismatch;
- no save point within 90 s;
- an input that cannot be fed within 90 s or is impossible, for example the recorded card is not in hand.

On divergence the driver:

1. logs the detail;
2. shows a short note;
3. reloads the recording's snapshot (the mismatching keyframe, or the next one after an unfeedable input);
4. continues from the following step.

A keyframe that diverges twice in a row is stepped over. Combat checksums are noise for now: live, they differ on
many card plays while every keyframe matches. Only the first difference since the last save point is logged, at Info,
with the count when that segment ends. The next
keyframe decides.

## What is protected, and how

No new Harmony target is used. Protection rests on the game's own switches, a backup, and SpireLens's own suppression
flag.

**The game's switches**, set by `SetUpForPlayback`:

- `RunManager.ShouldSave = false`. The game then writes:
  - **no `current_run.save`** (`RunSaveManager.SaveRun` checks it);
  - **no run history entry**, end-of-run progress update, run metrics upload, current-run delete or Steam architect
    stat (`RunManager.OnEnded` wraps all of that in `if (ShouldSave)`).
- **No `IncrementNumReloads`.** `SetUpSavedSingleplayer` would rewrite `current_run.save` with it.
- **No daily time.** That means no leaderboard upload at run end.
- `AchievementsHelper` is empty in this build (v0.111.0), so no achievements fire.

**`ReplayDataGuard`** handles what the switches do not cover. The game still mutates in-memory progress during a run:

- cards, relics and potions marked seen;
- combat-won statistics;
- epochs;
- the score the game-over screen banks.

It also writes `progress.save` at every map-node save (`SaveManager.SaveRun` saves the progress file before the
ShouldSave-gated run save) and prefs (FTUE flags). So:

- **Before playback**, `ReplayDataGuard` copies the following into `user://SpireLens/replay-playback-backup/`, together
  with a manifest that is also the "replay in progress" marker:
  - `current_run.save`, `current_run_mp.save`, `progress.save` and `prefs.save` (plus their `.backup` siblings);
  - the in-memory progress, serialized with the game's own JSON.

  If the backup cannot be taken completely, playback does not start.
- **After playback** (Esc, menu, end of run, or an error), `ReplayDataGuard`:
  1. returns to the main menu;
  2. reloads the in-memory progress and saves it through the game, so a Steam Cloud copy follows;
  3. copies back every file whose bytes changed;
  4. deletes any file that did not exist before;
  5. removes the marker.

  If any step fails, the backup is kept and the error is logged.
- **Crash or game closed mid-replay:** the marker survives. At the next Core load, `ReplayPlayer.OnCoreInitialize`
  restores before any tracker attaches. If the game has not loaded progress yet at that point, the file restore is
  what it then loads.
- **Core hot reload mid-replay:** the old Core releases everything the game holds back to it:
  - its selector from the game's selector stack;
  - its checksum subscription;
  - the status bar;
  - `Engine.TimeScale`.

  The new Core sees the marker and the replay's live run. It returns to the main menu and restores.

**SpireLens's own suppression:** `ReplayPlaybackGuard.SuppressTracking` is true while a replay plays and while a marker
is pending.

- **`RunTracker`.** At start, `SuspendForPlayback` saves and parks any live record. `OnRunStarted`,
  `TryEnsureCurrentRunLocked` and `TryResumeActiveRun` then do nothing, so no run record is adopted, minted or written.
  After a replay, the next Continue of a real run adopts its record from disk, as after a game restart.
- **`ReplayRecorder`.** Nothing is recorded, so a replay never becomes a recording.
- **`RunDatabaseImporter`.** No import is scheduled.
- **`RoomResetter`.** Restart is blocked, and no death-restore point is captured from a replayed death.

**Steam Cloud:** progress written mid-replay reaches the cloud through the game's store. The restore writes the
original back the same way.

## Known gaps

- **Exact build only.** Recordings from another game commit or model-id hash are refused. That is by design.
- **Multiplayer recordings** are refused.
- **Crystal Sphere** clicks are not recorded, so that event diverges and resyncs at the next save point.
- **Queued inputs are serialized.** A player who queued cards while others were still resolving is replayed one input
  at a time. The game can cancel actions depending on timing, so this can rarely diverge; resync covers it.
- **Prefs:** FTUE flags marked during a replay stay set in memory, while the file is restored. Only "seen" flags are
  affected.
- **Hover tooltips** work. Clicking cards or piles to inspect them is blocked.
- **The game-over screen** of a finished replay banks a score in memory. That is reverted on exit.
- **Output written by the game itself:** `replays/latest.mcr` (the game's own debug combat replay) is overwritten during
  playback. It is not user progress.

## What to expect to break first

These are untested live, roughly in order of likelihood:

1. **Readiness and UI driving.** A screen the driver does not expect, such as a FTUE popup, an Ancient dialogue
   variant or a modal, can stall an input until the 90 s timeout and resync. Event option button lookup and map
   travelability timing are the most likely culprits.
2. **The selector seam:** a selection path that bypasses `LocalSelector` and opens a screen the driver does not drive.
3. **Keyframe mismatches** from inputs that were queued during resolution in the original run.
