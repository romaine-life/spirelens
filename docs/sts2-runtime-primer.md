# Slay the Spire 2 Runtime Primer

This is the stable mental model future agents should read before changing card or relic attribution. It is not a replacement for source inspection. It is the map of the game/runtime facts that have been rediscovered often enough that they should be treated as durable context.

Primary local entry points after reading this:

- `Core/RunTracker.cs`: attribution state machine, pending combat buffer, persistence boundary, card identity, block/effect/poison ledgers.
- `Core/Patches/*.cs`: trusted hook surfaces and the timing assumptions behind them.
- `Core/RunData.cs`: persisted shape and what each aggregate field means.
- `docs/architecture.md`: SpireLens topology and product-level data flow.

## The Two Worlds

SpireLens always has to keep two worlds distinct:

1. The game's live runtime: Godot nodes, CombatManager, RunManager, CardModel objects, piles, hooks, combat history, powers, relics, and async model callbacks.
2. SpireLens' run record: committed `RunData`, pending combat aggregates/events, tooltip projections, and JSON persistence.

The live runtime is mutable and object-reference heavy. The persisted run record must be stable across hot reloads, combat transitions, and future shape additions. Most bugs come from accidentally treating one world as if it had the guarantees of the other.

A good rule: observe the game as late as needed to know what really happened, but record it in SpireLens as early as needed to preserve the source context before the game discards it.

## Threading And Async Shape

The game events SpireLens relies on fire on the main thread. `RunTracker` still uses a lock because save I/O is asynchronous and because the hot-reload lifecycle can cross visible state transitions.

Several STS2 hook methods are async or participate in async card/relic/power flows. SpireLens usually does not await them. Instead, patches capture a prefix or postfix observation at a point where the relevant fact is already true or the relevant source context has not yet been lost.

Important examples:

- `Hook.AfterCardDrawn` is async in the game, but a SpireLens prefix is enough because by the time this hook is invoked, the card is already in hand.
- `Hook.ShouldDraw` runs before the draw succeeds or fails, which makes it useful for source attribution and blocked-draw attempts.
- `Hook.AfterCardChangedPiles` observes the final pile result after a move/redirection, which is better than trusting the attempted move.
- Energy and star gain are captured with before/after snapshots on the actual player resource mutation methods, so the recorded amount is the applied delta.

Do not assume that a card's source context is still available when a visible outcome appears. Some effects finish the `CardPlay` history entry before their downstream action fully resolves.

## Hot Reload Lifecycle

The runtime is split into a stable loader and a hot-reloaded core.

- `Loader/LoaderMain.cs` is the long-lived bootstrap loaded by the mod manager.
- `Core/CoreMain.cs` is reloaded on F5. It installs Harmony patches, wires tracker hooks, resumes active run state, and cleans up on shutdown.
- `CoreMain.Initialize()` must allocate only things that `CoreMain.Shutdown()` can release.
- Shutdown order is deliberately UI teardown, tooltip teardown, event unsubscription, then Harmony unpatching.

Old core assemblies are orphaned rather than truly unloaded. That is acceptable only if the old assembly stops receiving callbacks. Any new event subscription, Godot node signal, Harmony patch, static callback, or UI node must have an explicit cleanup path.

When adding a hook:

- Make sure it is installed by Harmony patch discovery.
- Make sure it is removed by `UnpatchAll(_harmonyId)` or otherwise cleaned up.
- If it subscribes to game events or Godot signals directly, add teardown.
- If it creates UI nodes, make hot-reload reinjection and `QueueFree()` behavior explicit.

### Core Loads Before The Game's Model Database Exists

On a cold start, `CoreMain.Initialize()` runs *earlier* than `ModelDb`. The
game's `NGame.GameStartup` calls `OneTimeInitialization.ExecuteVeryEarly`, which
awaits `ModManager.Initialize` — that is where the `[ModInitializer]` contract
invokes `LoaderMain.Initialize` → `LoadCore` → `CoreMain.Initialize`. Only the
next step, `ExecuteEssential`, calls `ModelDb.Init()` (and then
`ModelDb.InitIds()`). Mods are loaded first on purpose, so `ModelDb` and
`LocManager` pick up whatever a mod injected.

The consequence for us: during `CoreMain.Initialize()` on a cold start, the
`ModelDb` dictionary is **empty**. Every model lookup fails, and the failure is
an unhelpful `KeyNotFoundException` from a raw dictionary indexer rather than
the `ModelNotFoundException` the by-id accessors throw. Aggregate properties
fail on the first thing they touch — `ModelDb.AllRelics` reaches
`AllCharacters` before any relic, so it throws
`The given key 'CHARACTER.IRONCLAD' was not present in the dictionary`.

This is invisible in dev. A Core hot reload re-runs `CoreMain.Initialize()` long
after `ExecuteEssential`, so the same call succeeds. A bug of this shape is
therefore permanent for real players and never reproduces in a session that
presses F5. Treat "works after reload, failed at startup" as the signature.

So: **do not enumerate `ModelDb` from `CoreMain.Initialize()`.** Defer it to
first use and probe readiness with `ModelDb.Contains(typeof(SomeModelType))`,
which answers `false` on an empty database instead of throwing. Probing beats
catching, because a catch costs one throw per call for as long as the database
stays empty. `RelicClassificationStore.IsLiveRelicDatabaseReady` /
`TryNormalizeAgainstLiveRelics` are the worked example.

`ModelDb.Preload()` is later still — `ExecuteDeferred`, after the main menu is
up. Cached aggregates such as `_allRelics` are only assigned on a successful
enumeration, so a mod's early failed access does not poison them.

### Core Loads Before The Scene Tree Accepts New Children

Same startup ordering as the section above, different victim.
`CoreMain.Initialize()` is reached from `NGame._EnterTree` →
`GameStartupWrapper` → `GameStartup` →
`OneTimeInitialization.ExecuteVeryEarly` → `ModManager` → our
`[ModInitializer]`. `NGame` is therefore still inside `_EnterTree` while we run,
and Godot refuses to parent anything onto a node that is mid-setup:

```
ERROR: Parent node is busy setting up children, `add_child()` failed.
Consider using `add_child.call_deferred(child)` instead.
   at: add_child (scene/main/node.cpp:1689)
```

Two properties make this worse than it looks:

- **It is not an exception.** The refusal is a native `ERR_FAIL` that prints and
  returns. A `try`/`catch` around the `AddChild` never fires, the code after it
  keeps running, and `Core.Initialize complete` still gets logged. The node is
  simply left parentless — never in the tree, never `_ready`, never emitting
  signals — and nothing retries.
- **It cannot reproduce in dev.** A hot reload re-runs `CoreMain.Initialize()`
  long after `_EnterTree` returned, so the same call succeeds. "Fine after F5,
  broken at launch" is the signature; the only honest evidence is the cold-start
  window of `godot.log`, above the first `--- HOT RELOAD START ---`.

So: **anything `CoreMain.Initialize()` parents into the tree must go through
`CallDeferred`.** `RunTimeStatsTracker.AttachSampler` and
`LoaderMain.AttachInputListener` are the worked examples;
`HotReloadToast.Show` does the same for its `CanvasLayer`. Guard the deferred
callback — re-check `IsInstanceValid`, that the node is still the current one,
and that it has no parent yet — so an `Initialize`/`Shutdown` pair inside one
frame cannot attach a node the previous Core already tore down.

Only unconditional node creation is exposed. The `Reinject*` steps in
`CoreMain.Initialize()` are safe by construction: each locates an existing live
screen and no-ops when it finds none, which on a cold start is always. Hooks are
safe too — `RunManager.Instance` and `CombatManager.Instance` already exist that
early and the subscriptions survive into gameplay — and `TryResumeActiveRun`
finds a null `RunState` and defers to the later `RunStarted` adopt path.
`res://` resource loading works this early as well, which is why
`StatConceptGlossary` builds its icons here without complaint.

The second-order damage is worth understanding, because it does not look like a
startup bug from the outside. A dead sampler took the run-timer tooltip with it:
`RunTimerStatsTooltip.EnsureLiveTarget` is a no-op at `Initialize` time (the top
bar does not exist yet) and its only retry is the sampler tick, so the live
tooltip never bound at all for a launched-and-played session. Per-surface
attribution degraded the same way — with no periodic sample, the only remaining
`SampleRunTimeStats` callers were combat boundaries, and since each sample bills
all elapsed time to the *previously* recorded surface, everything between one
combat ending and the next starting landed in `reward_screen_seconds` while
`map_seconds` and `event_seconds` stayed at zero.

### Newly Added Harmony Targets Need One Full Restart

Core hot reload can install a Harmony detour, but it cannot reliably invalidate
machine code the CLR already generated for callers of that game method. A
caller may already have inlined the original method or devirtualized a virtual
call to it. In that case Harmony lists the target as patched, while the existing
compiled caller continues to execute the original path and never enters the new
prefix or postfix.

This matters specifically when a build introduces a Harmony patch for a game
method that was unpatched earlier in the same Slay the Spire 2 process:

- hot reload is sufficient to prove that patch discovery and initialization
  succeeded;
- a new combat or a new run is not sufficient, because both reuse the same CLR
  process and its compiled code;
- fully restart Slay the Spire 2 once before behavioral verification of that
  newly introduced target;
- after restart, the loader installs the patch during startup before ordinary
  gameplay compiles the relevant caller path.

Changes behind a hook that was already established before its caller compiled
can usually use the normal hot-reload loop. The restart rule is for newly added
targets and for any diagnostic where Harmony reports the target as patched but
an entry log proves the prefix/postfix never runs.

Cracked Core exposed this boundary. Its `LightningOrb.Passive` target was first
added at Core hot reload 39 in an already-running game process. Harmony listed
the patch, but the diagnostic prefix never ran while the already-compiled
`OrbModel.TriggerPassive` async path continued to execute. Starting new combats
and runs did not change that. A full game restart cleared the compiled call
site; the same patch then began recording passive activations without a code
change.

## Run And Combat Boundaries

SpireLens persistence is combat-boundary based.

- `RunManager.Instance.RunStarted` starts a new run record — or resumes one; see below.
- `CombatManager.Instance.CombatSetUp` creates `_pendingCombat`.
- `CombatSetUp` fires before `Hook.BeforeCombatStart`. Glory's two consecutive
  boss rooms each receive their own setup/start/end lifecycle; combat-start
  relic attribution must therefore count them as separate activations.
- Combat-room `AfterRoomEntered` relic callbacks also run after `CombatSetUp`
  but before the combat turn loop marks `CombatManager.IsInProgress` true.
  Receiver-side attribution for opening effects such as Red Skull Strength
  doubled by Ruined Helmet must therefore accept an existing `_pendingCombat`
  as the combat boundary instead of requiring `IsInProgress`.
- Potion Belt, Alchemical Coffer, and Phial Holster are the complete set of
  relic models that mutate potion-slot capacity. Their held-period stat takes
  one zero-inclusive `Player.Potions.Count()` snapshot at `CombatSetUp` and
  writes it to each applicable owned relic's pending combat aggregate.
- During combat, live observations accumulate in `_pendingCombat`.
- `ICombatState.RoundNumber == 1` spans the entire first round: the player's
  first side (including extra player turns) and the enemy's first side. It
  increments before the next player side. On the player side,
  `PlayerCombatState.Phase == None` is still the pre-turn setup window; reject
  it when a first-round stat should begin at the player's actual first turn.
- `CombatManager.Instance.CombatEnded` promotes pending aggregates/events into committed `RunData`, updates run metadata, saves, and clears `_pendingCombat`.
- The tracked player's final `PlayerCombatState.TurnNumber` remains available
  while pending combat data is promoted. Combat-duration buckets such as
  Sparkling Rouge should be recorded at this shared promotion boundary rather
  than by patching the relic's unrelated effect callback.
- `RunTracker.OnRunEnded` also promotes `_pendingCombat` before stamping the outcome — for the `loss` outcome only. Loss ordering (decompiled `CreatureCmd.Kill` → `LoseCombat()` → `RunManager.OnEnded`): `OnRunEnded` runs synchronously from the killing action, and the fatal combat's `CombatEnded` only fires LATER via `ProcessPendingLoss` — after the buffer has been consumed. Without this second promotion site the fatal combat's stats would be discarded. Abandoning mid-combat still discards the buffer (a half-played fight is not a resolved combat — a save-and-quit may even have rolled it back), and wins always get a normal `CombatEnded` first. Promotion is idempotent per buffer (the buffer is nulled after), so the two sites cannot double-promote. `RecordCombatEndingSuppressedDamage` stops capturing once `_currentRun` is null so the post-`OnRunEnded` damage tail can't resurrect the buffer and mint a junk run file at that deferred `CombatEnded`.
- Between-combat and between-floor reloads are supported.
- Mid-combat restore is intentionally out of scope.

This distinction is easy to blur because tooltips merge committed and pending data for immediate display. That merged view is for UI only. The permanent run file is not promoted until combat ends — or, for the final combat of a lost run, until the run ends.

`RunStarted` does NOT always mean a new run. The game re-fires `RunManager.RunStarted` with the SAME `RunManager._startTime` every time a saved run is continued from the main menu (log line: "Continuing run with character"). `RunTracker.OnRunStarted` therefore treats a matching in-progress `game_start_time` as a continuation: it keeps the in-memory `RunData` (or, after a full game restart, adopts the newest resumable on-disk record via `RunStorage.FindByGameStartTime`) and rebinds card identity through `AdoptRunLocked` instead of minting a fresh run file. Historic builds minted a fresh `RunData` here, which stranded all previously committed stats in orphaned files and reset tooltips to zero mid-run.

When implementing new combat attribution, write it into `_pendingCombat` first unless the event is truly outside combat, such as card arrival/removal/upgrade lineage. Promotion should stay centralized in `PromotePendingCombatIntoRun` and its two callers.

## The Run Save Is A Room-Boundary Snapshot

The game's run save holds **no combat state at all**. `SerializableRun` carries acts, modifiers, map history, RNG sets, an optional `PreFinishedRoom`, and a `SerializablePlayer` list (HP, gold, deck, relics, potions, discoveries). There is no hand, no draw/discard/exhaust pile, no enemy, no power, no block, no turn number, and no `SerializableCombat` type anywhere in the assembly.

That is by design, and the write points are what make it coherent. The whole assembly contains exactly three gameplay-path `SaveRun` calls:

- `RunManager.EnterMapPointInternal` calls `SaveManager.Instance.SaveRun(null)` on map-node entry — *before* the room type is rolled and the room is created.
- `CombatManager` saves again at victory, via `room.MarkPreFinished()` then `SaveRun(room, saveProgress: false)`, so continuing lands in the post-combat reward state instead of refighting.
- `EventRoom.OnEventStateChanged` does the same when an **Ancient** event finishes, and only for `AncientEventModel` — ordinary events return early from that handler.

Nothing else writes. In particular `MerchantRoom`, `RestSiteRoom` and `TreasureRoom` never save, and nothing is written during a fight or an ordinary event.

The consequence worth remembering: **for the entire duration of a room, `current_run.save` on disk is that room's opening state**, RNG included. Reloading it replays the identical encounter and shuffle, the identical merchant stock, or the identical event, because `LoadIntoLatestMapCoord` re-rolls the room from restored RNG and `EnterMapPointInternal` re-appends the map-point history entry the save was written before. This is also why save-and-quit mid-combat rewinds the fight, and why abandoning mid-combat must discard `_pendingCombat`.

`Core/RoomResetter.cs` is built directly on this: restarting a room needs no snapshot of SpireLens' own, only a replay of the save the game already wrote. Its load sequence mirrors the main menu's Continue button (`RunState.FromSerializable` → `SetUpSavedSingleplayer` → `NGame.LoadRun`), with `RunManager.CleanUp()` inserted first because `SetUpSavedSingleplayer` throws while `RunManager.State` is non-null. `CleanUp` is the Save-and-Quit/Abandon teardown: it sets `ShouldSave = false` (the abandoned attempt can never overwrite the save), calls `CombatManager.Reset(graceful)`, and nulls `State`, but does **not** call `RunManager.OnEnded` — so no run outcome is stamped and no `RunEnded` fires. Because the result is the Continue path, `RunStarted` re-fires with the same `_startTime` and the existing adoption logic below handles it, including the `_pendingCombat` discard that keeps the abandoned attempt out of the run record.

`RoomResetter.Describe()` keys its availability off those three write points rather than off room type alone. It reads `RunState.BaseRoom`, not `CurrentRoom`, because an event option that starts a fight pushes a `CombatRoom` on top while the save still replays the map point — what actually comes back is the event. It then refuses whenever the game has already written a pre-finished-room save over the opening state: a combat room with no fight in progress (won, sitting on rewards), an `EventRoom` with `IsPreFinished` (Ancient, done), or an event whose map point has hosted a combat room that is no longer running. Rest sites and treasure rooms are equally replayable by this mechanism and are simply not offered yet.

### Rewinding The Game Means Rewinding The Record

A restarted **combat** costs the run record nothing on its own: combat stats live in `_pendingCombat` until promotion, and the adopt path drops that buffer. **Shops and events get no such protection.** Gold spent, cards bought, relics taken and HP traded are committed to `_currentRun` as they happen — `RecordGoldLoss` writes straight to `_currentRun.GoldStats` whenever `inCombat` is false, and drags the map-legend category, the gold room-visit counters and the Maw Bank / Old Coin / Golden Pearl aggregates along with it. Replaying the room without rewinding the record banks all of that a second time, in the exact stats the room exists to produce and in per-relic attribution, which is the mod's whole point. That is a defect, not an acceptable rounding error, and it is not comparable to the crash-rollback case the tracker already tolerates: a crash is rare and accidental, a restart button inflates the run every time it is pressed.

So the record rewinds with the game:

- `RunTracker.CaptureRoomEntrySnapshot` serializes `_currentRun` (through `RunStorage.Options`, so it round-trips the on-disk shape) when a room opens. The call rides `SignetRingStatsPatch`, the existing shared observer on `Hook.AfterRoomEntered` — **no new Harmony target**. That hook fires just after `EnterMapPointInternal` wrote the run save, so the snapshot and the save describe the same instant by construction. It is taken last in that patch, after the room-entry recorders, and skipped when `CurrentRoomCount > 1` so an event-spawned combat cannot overwrite the event's snapshot.
- `RunTracker.RollBackToRoomEntry` restores it, and `RoomResetter` calls it after every abort path and immediately before `CleanUp()` — the point of no return — so a corrupt-save abort leaves both the game and the record untouched. It writes via `RunStorage.SaveAsync` rather than `SaveCurrentRun`, because the latter re-stamps the still-live (post-purchase) instance numbers over the restored ones. The live identity maps are left alone on purpose: `RunStarted` re-fires moments later and `AdoptRunLocked` clears and rebuilds them from the restored record against the reloaded deck.

The snapshot is in memory only, so a hot reload mid-room drops it and `Describe()` blocks with `no room-entry snapshot` until the next room re-arms it. That is the deliberate direction to fail in — refusing a restart is cheap, silently inflating a run is not.

Note that `SetUpSavedSingleplayer` awaits `SaveManager.IncrementNumReloads`, so every restart increments the save's `num_reloads`.

## RunStarted Is Not Deck-Ready

Do not use `RunStarted` as the source of truth for starter deck population.

Earlier code tried to walk `player.Deck.Cards` at `RunStarted`, but fresh runs had a timing race: the deck was not always populated yet. The durable hook is `CardPile.AddInternal` filtered to `PileType.Deck`, implemented by `CardEnterDeckPatch`.

Continue-loads make the ordering worse: when a saved run is continued from the main menu, the game repopulates the deck with brand-new `CardModel` refs, and `CardPile.AddInternal` can fire BEFORE or AFTER the `RunStarted` re-fire — neither order is guaranteed. Observed consequence of the before-ordering: `CardEnterDeckPatch` stamps the fresh refs with brand-new instance numbers continuing the old counters (ghost aggregates like `CARD.DEATH_MARCH#3/#4` for a two-copy deck) before run adoption can rebind them. `AdoptRunLocked` handles both orderings with one mechanism: it seeds the saved `InstanceNumbersByDef` snapshot into `_pendingRankRestores` (per-def queues in deck-rank order), walks whatever portion of the deck already exists through `GetOrAssignNumber` (which claims queued numbers in arrival order before minting), leaves the rest queued for later `CardEnterDeckPatch` arrivals, and prunes the ghost stamps afterward via `PruneGhostAggregates`. Known assumption (shared with the hot-reload deck walk): repopulation arrival order matches `player.Deck.Cards` enumeration order at save time — if the game ever repopulates same-def copies out of deck order, two copies of the same def would silently swap stats. `CaptureInstanceNumbersByDeckRank` includes still-queued numbers in every snapshot, so a save during adoption cannot strand them; queues left unclaimed at combat setup are discarded with an always-on log line.

`CardPile.AddInternal` catches:

- starter deck population,
- Ascender's Bane / ascension curse insertion,
- reward cards,
- shop cards,
- event grants,
- other permanent deck entries routed through deck pile mutation.

For card arrival metadata, prefer the game's `CardModel.FloorAddedToDeck` when present. The game sets it to floor 1 for starters and current floor for mid-run additions. If it is null, SpireLens falls back to the current run floor when possible.

## Card Identity

Card identity is per physical card when a card has stable deck identity.

Key facts:

- Combat cards are often clones of permanent deck cards.
- Combat clones point back to the deck original through `CardModel.DeckVersion`.
- Deck-view cards are already the original and usually have `DeckVersion == null`.
- `RunTracker.Canonical(card)` uses `card.DeckVersion ?? card` so combat-time and hover-time references converge.
- Canonicalization is for identity and attribution, not proof that a mutation
  was permanent. For card upgrade lineage, snapshot whether the exact object
  passed to `UpgradeInternal` is present in the permanent deck; a combat clone
  pointing at a deck original remains only a temporary upgrade.
- Aggregates are keyed as `{card_definition_id}#{monotonic_number}`.
- The monotonic number is per card definition and is never reused within a run.
- Removed cards keep their aggregate and removal snapshot rather than being deleted.

Do not key long-lived attribution by raw `CardModel` reference unless you are inside a live-only ledger that will never cross reload or persistence boundaries. References are useful inside a single combat; persisted data needs stable string keys.

Non-assigning lookups matter. Hovering a preview/template card should not burn a new instance number. Paths that merely display a card should use non-assigning lookup behavior; paths that observe a real deck entry or real play may assign.

## Piles And Card Movement

Permanent deck membership is not the same thing as a card's current combat pile.

During combat, a deck card may be in draw, hand, discard, exhaust, play, or another transient pile. The permanent deck view still wants the same physical instance identity. Conversely, combat-generated cards may exist only for a combat and should not always pretend to be permanent deck members.

Useful pile facts in this repo:

- `CardPile.AddInternal` filtered to `PileType.Deck` means permanent deck entry.
- `CardPileCmd.RemoveFromDeck` prefix captures removal before the game detaches the card and mutates its state.
- `CardPileCmd.Add(card, PileType.Draw, CardPilePosition.Top, ...)` prefix can classify top-of-draw placements by reading the source pile before mutation.
- `Hook.AfterCardChangedPiles` is the generic post-mutation observation point for final pile results.

For redirections, prefer final pile observation. A card can attempt to move to hand but land elsewhere because the hand is full or because another game rule intervenes.

## Combat History

`CombatHistory.Add(entry)` is the broadest combat observation point. `CombatHistoryAddPatch` postfixes it and forwards each typed entry to `RunTracker.Observe(entry)` after the entry has been appended, which means the entry survived the game's own logic and is real.

This is a good master hook for entries that reliably reach `Add`, such as many card plays, damage entries, block entries, and power entries.

But do not assume every small `CombatHistory.*` wrapper is safe to patch. The repo has a documented draw trap:

- `CombatHistory.CardDrawn` is tiny and can be JIT-inlined.
- Harmony patches on that wrapper did not fire in diagnostic runs.
- `CardDrawnEntry` also did not appear through the generic `Observe` distribution during confirmed draws.
- The reliable draw hook is `Hook.AfterCardDrawn`, not `CombatHistory.CardDrawn`.
- Other runtime/JIT states can expose `CardDrawnEntry` through the generic
  history observer after all. Treat it as recovery/diagnostic evidence only;
  `Hook.AfterCardDrawn` remains the single live counter path, or successful
  draws will be counted twice.

When a future stat seems obvious from combat history but never appears, suspect inlining, alternate code paths, or late async flow. Add a focused diagnostic hook only long enough to prove the path, then promote the reliable hook or document the trap.

## Card Play Timing

A card play has at least three useful phases for stats:

1. The card play is recognized and source context is available.
2. The card's immediate costs/effects mutate resources, piles, powers, block, damage, etc.
3. Downstream effects may continue after the play is already considered finished by some history APIs.

`CardPlay.Resources.EnergySpent` and star spend are the source of truth for actual cost paid, not printed card cost. This captures cost reduction, free plays, X-cost behavior, and modifiers.

`CardPlay.PlayIndex` and `CardPlay.PlayCount` describe a play series. Total
card plays should still count every `CardPlayFinishedEntry`; replay extra plays
are the subset where `PlayIndex > 0`. This preserves the normal "played N"
stat while revealing how often Replay/Glam-like mechanics caused additional
plays.

Replay source attribution is a two-phase pattern. Capture play-count modifiers
when the game calculates the series, then spend those pending sources only when
an actual extra `CardPlayFinishedEntry` arrives. `Hook.ModifyCardPlayCount`
exposes power/model contributors such as Burst, Echo Form, One Two Punch,
Duplication, Signal Boost, and Tag Team. `Glam.EnchantPlayCount` covers the
card-enchantment Replay path. If an extra play has no captured source, count it
as plain `Replay`; this is the fallback for card-native/base replay counts and
other effects that mutate the card's replay count before the hook can expose a
source.

Throwing Axe uses this same hook-listener path: its model appears in
`Hook.ModifyCardPlayCount`'s `modifyingModels` only when it contributes one
extra play. Attribute the relic from a later finished replay, not from the
planned count alone. Every `CardPlay` in the series shares the same
`ResourceInfo`; `EnergyValue` is therefore the play-time cost/value of the card
that Throwing Axe replayed, while `EnergySpent` describes the one resource
payment for the series.

Replay shortfalls and no-outcome replays are not the same thing. `PlayCardAction`
can cancel before `OnPlayWrapper` starts if `CanPlay` or `IsValidTarget` fails;
that produces no `CardPlayStartedEntry`, no `CardPlayFinishedEntry`, and no
resource spend. Once `OnPlayWrapper` starts, the game loops through the generated
`playCount`, emits started/finished history for each iteration, and lets the
card's own async `OnPlay` body run its commands. A later replay can therefore be
a real finished card play while a command such as `AttackCommand` finds no valid
target and produces no damage entries. Track planned replay extras, finished
replay extras, and command-specific no-outcome buckets separately.

For source context, `RunTracker` keeps notions like current player card play, recently completed player card play, pending draw source, pending effect source, and history counts. These are deliberately temporal and should be handled carefully. When adding a stat, ask:

- Is the source card still current at the outcome hook?
- If not, can the source be captured before the outcome and resolved after it?
- Is there a recent-completed play fallback, and how many history entries should it remain valid for?
- Could an enemy, relic, or power produce the same outcome without a card source?

Do not casually widen temporal attribution windows. A wide window can make unrelated follow-up effects look card-caused.

Alchemize creates one random in-combat potion and awaits the exact
`PotionCmd.TryToProcure(PotionModel, Player, int)` overload, but discards its
`PotionProcureResult`. Capture the currently resolving physical Alchemize card
when that command begins, then wrap the returned task and record its observed
result before returning it to Alchemize. A successful result supplies the
actual gained potion and rarity; a failed result means no potion entered the
belt (currently a full belt or a `ShouldProcurePotion` blocker such as Sozu).
Do not resolve the source after awaiting: `CardPlayFinished` can clear the
current-card context as soon as Alchemize resumes.

Petrified Toad is the only game model that directly procures a Potion Shaped
Rock. Arm its owner at `CombatSetUp`, consume that marker only when the existing
`PotionCmd.TryToProcure(PotionModel, Player, int)` patch sees the Rock request,
then use the returned `PotionProcureResult`: `success` counts a potion given,
`TooFull` counts a full-belt block, and `NotAllowed` is not a full-belt block.

Run-level potion provenance has a separate identity path from Alchemize's card
aggregate. `PotionReward.CreateIcon` is the visible concrete reward-offer
boundary, while `MerchantPotionEntry.FillSlot` is the shop boundary where the
game has selected and marked the stocked potion as seen. A successful
`Player.AddPotionInternal` result is the final belt-insertion truth; failed
reward clicks and full-belt/blocker failures leave the offer in the not-taken
lane. A potion reward is rejected only when its exact `PotionReward.OnSkipped`
callback runs; visibility and unsuccessful selection attempts are not terminal
outcomes. Preserve the concrete potion rarity on the history entry so the
run-wide belt summary can split combat-reward offers without reconstructing
historic mutable models. `Player.RemoveUsedPotionInternal` and `Player.DiscardPotionInternal`
confirm the two terminal belt removals. Bind the mutable potion reference to a
monotonic run sequence so duplicate definitions remain separate, and rebuild
bindings from live belt order after Continue/hot reload. Keep that sequence as
an internal chronological identity; derive the tooltip's visible instance
number independently per potion definition, so Weak Potion 3 means the third
Weak Potion observed rather than the third potion of any kind. Potion mutations
that happen in combat belong in the pending-combat history snapshot; their
merged gallery view is immediate, but they are not persisted until promotion.

Blood Potion's protected `OnUse` callback owns and awaits its one
`CreatureCmd.Heal` call before the broader `AfterPotionUsed` hook runs. Snapshot
the targeted player's current HP around that exact callback and attach the
nonnegative observed delta to the owner's potion-history entry already bound to
the mutable potion reference. This supports another player as the target,
records zero when used at full HP, respects heal clamping and prevention, and
excludes unrelated healing from later generic potion hooks.

Swift Potion calls the public four-argument `CardPileCmd.Draw` overload once
from its potion-owned branching choice context. Require the exact Swift Potion
as both that context's `Source` and `LastInvolvedModel`, then claim the first
non-hand draw on that context so nested draw effects cannot replace its result.
The cards returned by the completed draw task are the observed value; the
nonnegative difference between the requested count and that returned count is
the potion's blocked draws, matching opening-hand relic semantics for No Draw,
hand capacity, and pile exhaustion.

Fortifier's protected `OnUse` computes its grant from the target's current
block, then calls the final decimal/`ValueProp` `CreatureCmd.GainBlock`
overload without passing its potion choice context. Arm only across the direct
synchronous call from `OnUse`, and keep an async-local stack for every nested
gain-block command until its task completes. The active Fortifier frame at the
game's `BlockGainedEntry` is the exact source of the post-modifier amount and a
potion-owned `BlockChunk`. That chunk shares the normal FIFO absorbed / LIFO
wasted ledger semantics. If a tracked owner targets a co-op partner, retain the
observed gain on the potion but do not mix the partner's block pool into the
tracked player's effective/wasted ledger.

Explosive Ampoule's protected `OnUse` callback snapshots the hittable enemies,
waits for its VFX, then calls the final multi-target `CreatureCmd.Damage`
overload. Its branching `PlayerChoiceContext` still has the exact ampoule as
`LastInvolvedModel` when that command begins. Use that model reference instead
of a dealer-wide attribution window, require the same ampoule as the branching
context's `Source`, and claim the context's first owner-dealt, non-card damage
command so nested damage cannot overwrite the potion's result. Wrap the
returned damage task and attach the observed blocked/unblocked/overkill/kill
split to the already-bound potion history entry. An empty result is still an
observed zero-target outcome; older entries remain distinguishable because the
persisted damage fields are nullable.

Jack of All Trades selects distinct cards from the unlocked colorless combat
pool, then awaits `CardPileCmd.AddGeneratedCardToCombat` once for each selected
card. Capture the currently resolving physical Jack before each async command,
then classify only a successful returned `CardPileAddResult.cardAdded`. That
post-hook card is the authoritative source for rarity, card type, and effective
energy cost; the pre-command candidate is intent and can diverge from the card
that pile/add hooks actually allow into combat.

Discovery awaits `CardSelectCmd.FromChooseACardScreen`, then calls
`SetToFreeThisTurn` on the returned card before adding it to Hand. Wrap the
exact `SetToFreeThisTurn` call only when Discovery is the currently resolving
card, classify that card, and measure its effective energy cost before and
after the call. A null/skip result never reaches this boundary and does not
count as a pick.

The same resolving-card guard also identifies Discovery's own screen at the
`FromChooseACardScreen` prefix, where the option list is still the exact set the
player is about to choose from. Counting those options there gives the picks a
real denominator without a selection window: Discovery enters the command
synchronously from its own resolve, so Toolbox, Hefty Tablet, Lead Paperweight,
and every other caller of the shared screen stay unclaimed. The prefix needs no
postfix — the pick is already observed downstream at `SetToFreeThisTurn`.

Debt applies its curse effect from the owner-specific
`Debt.OnTurnEndInHand(PlayerChoiceContext)` callback. Its intended loss is the
card's `Gold` dynamic var, but Debt clamps the amount passed to
`PlayerCmd.LoseGold` to the owner's current balance. Observe the owner's gold
before and after the completed callback: that delta is actual gold lost, and
`intended - actual` is the amount blocked by insufficient gold. Patching the
generic gold-loss command would lose Debt's unclamped intent and risk
attributing unrelated gold changes.

Normality has no owner-specific turn-end callback; its behavior is a passive
`ShouldPlay` veto while the card remains in Hand. Observe the established
`Hook.BeforeSideTurnEnd` prefix instead, where both Hand contents and the
player's unspent energy are still intact. Record each exact physical Normality
in Hand once for that player turn, and include zero-energy qualifying turns in
the denominator for average excess energy.

Seal of Gold uses its owner-specific
`AfterSideTurnStart(CombatSide, IReadOnlyList<Creature>, ICombatState)`
callback. It activates only when the owner is in the callback's participant
list and has at least its five-gold cost, then awaits energy gain followed by
gold loss. Apply the same affordability gate in the prefix, wrap the returned
task, and observe both resource deltas on completion. Count held combats
separately from activations so its boss-relic energy-per-combat average includes
combats where the owner ran out of gold and the relic produced no energy.

Amethyst Aubergine uses its owner-specific
`TryModifyRewards(Player, List<Reward>, AbstractRoom)` callback. A `true`
return confirms a trigger, and the `GoldReward` appended during that exact call
contains the observed extra-gold amount. Snapshot the reward-list count before
the callback and inspect only the appended tail afterward; do not copy the
relic's current 15-gold text value into tracking.

## Damage Attribution

For direct card damage, `DamageReceivedEntry` is the important observed outcome.

Enemy damage totals are computed from the game-reported pieces:

- `BlockedDamage`: damage absorbed by target block.
- `UnblockedDamage`: HP actually lost.
- `OverkillDamage`: attempted damage beyond lethal.

SpireLens definitions:

- intended damage = blocked + unblocked + overkill,
- effective damage = unblocked,
- blocked = blocked,
- overkill = overkill,
- kill = `WasTargetKilled`.

Effective damage is the user-facing total damage because it is the HP actually removed. Intended damage is useful internally and for waste percentages.

Relic damage should follow the same observed-result path when the relic emits a damage command. For example, Festive Popper arms attribution from `FestivePopper.AfterPlayerTurnStart` only when its owner is about to fire on turn one, then records the actual `CreatureCmd.Damage` results so blocked damage, overkill, and dead targets are not inferred from the relic text.

Known trap: an attack can play and produce no damage event, for example if the target is already dead/not fully removed or if no damage is actually received. Tooltip code treats a played attack with zero intended damage as a real but zero-damage case rather than inventing damage.

Known trap — combat-ending killing blows: `DamageReceivedEntry` is NOT complete. In decompiled `CreatureCmd.Damage`, HP loss (`LoseHpInternal`) is applied BEFORE the emission gate `if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsEnding) History.DamageReceived(...)`, and `CombatManager.IsEnding` is a live computed property that flips true the moment no living primary enemy remains — so the hit that kills the last enemy suppresses its own history entry and never reaches `CombatHistory.Add`. Mid-combat kills (another enemy still alive) emit normally. Scaling finishers systematically lose their biggest hits to this (diagnosed 2026-07-02 with Death March: 5/5 combat-ending kills unrecorded, all mid-combat hits recorded). SpireLens closes the gap with `HookAfterDamageGivenPatch`: `Hook.AfterDamageGiven` is dispatched directly by the game (its own doc comment: it "fires from the same damage event that ends combat (the killing hit), so it must still resolve for that hit"), fires after the possible emission for the same `DamageResult`, and runs before `Kill()` triggers `CombatEnded`. `RunTracker.RecordCombatEndingSuppressedDamage` synthesizes the missing `DamageReceivedEntry` (never touching the game's own history) only when `IsEnding` is true AND the `DamageResult` reference wasn't already observed via `CombatHistory.Add` — dedup is by result-object reference through `TryMarkDamageResultObserved`. Deliberate scope note: this captures ALL history-suppressed damage in the ending window, not only the killing blow itself — e.g. thorns or residual hits that really applied HP loss while combat wound down. That is the observed-outcomes principle: the damage genuinely happened (`LoseHpInternal` ran); only the game's history omitted it. Damage that never applied produces no `Hook.AfterDamageGiven` dispatch and is never invented.

Player self-damage is tracked as HP lost from playing a card and uses observed unblocked damage after reductions. That is the real cost, not the text value.

Maximum-HP costs are a separate signal from current-HP damage. Brightest
Flame's owner-specific `OnPlay` gains energy, draws, and then awaits
`CreatureCmd.LoseMaxHp`. That command records the requested amount in the
game's map history but clamps the resulting max HP to at least one; it can also
deal current-HP damage with no card source when current HP is above the new
maximum. Wrap Brightest Flame's returned `OnPlay` task and compare the owner's
max HP before and after the callback so SpireLens records only the actual max
HP removed, including a zero delta at the one-max-HP floor. Keep this in the
card aggregate, separate from `TotalHpLost`.

Feed's owner-specific `OnPlay` awaits its damage command, applies its own Fatal
eligibility and observed-kill checks, and only then awaits
`CreatureCmd.GainMaxHp` as the callback's final action. Wrap Feed's returned
`OnPlay` task and compare the owner's max HP before and after successful
completion. This credits only the physical Feed that produced an actual gain;
nonlethal plays and enemies whose powers suppress Fatal naturally produce a
zero delta.

Storybook grants a permanent-deck Brightest Flame but does not retain a
reference to that exact granted card. Its tooltip therefore uses the explicit
pooled-by-definition model: combine every `CARD.BRIGHTEST_FLAME` aggregate,
including pending combat data in live views, and show the card's play, draw,
energy, card-draw, and max-HP-loss stats. Brightest Flame can also come from
the event card pool, so this is deliberately a family-wide projection rather
than exact Storybook-grant lineage.

## Osty Body Attribution

Necrobinder has both investment cards that create or maintain Osty and payoff
cards that spend the resulting board state. Track those as different signals.

- Patch `OstyCmd.Summon`, not individual card text, for all successful Osty
  summons. The command carries the source model and summoner and returns the
  game-observed summon amount.
- Attribute the summon amount to the source card. That is the card's own
  contribution. Tooltip label: `Summon gained`.
- Also add every successful summon amount for the tracked player's Osty to
  run-level Osty meta stats, including card, Power, Potion, and either
  Phylactery source.
- Attribute Osty HP lost through `Hook.AfterCurrentHpChanged` negative deltas
  on any Osty creature into run-level meta stats. Do not assign all later Osty
  damage absorbed to the card that happened to summon most recently. Tooltip
  label: `All Osty damage absorbed`.
- Keep payoff tracking separate. Unleash's Osty-current-HP attack bonus belongs
  on the physical Unleash card, while the same observed play-time HP also
  contributes to the run-level Phylactery family total.
- Bound Phylactery and Phylactery Unbound share one Osty-body presentation.
  Count zero-inclusive turns and combats while either form is held; never
  restart those denominators when the relic upgrades.

## Block Attribution

Block has two different stats:

- block gained: what a card added to the player's block pool,
- block effective/wasted: what that block later absorbed or failed to absorb.

The game has one block pool, not per-card block. SpireLens uses a provenance ledger inside `_pendingCombat.PlayerBlockLedger`.

The current mental model:

- When a tracked source grants block, add a `BlockChunk` with exactly one
  owner: a card instance or relic id. Truly unknown/innate block remains
  ownerless.
- When incoming damage consumes block, absorbed block is charged through the
  ledger in FIFO order and returned to that chunk's card or relic aggregate.
- When block clears/expires unused, wasted block is charged through surviving
  ledger chunks in LIFO order and returned to that chunk's owner, matching the
  idea that later overfill was more likely redundant.
- Retain/prevent-clear effects must cancel pending clear attribution.

Relevant hooks:

- block gained comes from observed combat outcomes in `RunTracker.Observe` / block entries,
- `Hook.ShouldClearBlock` arms a possible clear with the current player block amount,
- `Hook.AfterBlockCleared` confirms clear and attributes waste,
- `Hook.AfterPreventingBlockClear` cancels the armed clear.

When changing block logic, be explicit that effective/wasted block is heuristic
ledger attribution, not a game-native per-source truth.

## Draw Attribution

Draw stats have three related but different signals:

- this card was drawn (`TimesDrawn`),
- this card caused other cards to be drawn (`TimesCardsDrawn`),
- this card attempted draws that were blocked or redirected (`TimesCardsDrawAttempted`, `TimesCardsDrawBlocked`, `BlockedDrawReasons`).

Reliable hooks:

- `Hook.AfterCardDrawn` records that a card arrived in hand/draw flow. `fromHandDraw` distinguishes turn-start automatic draw from effect-side draw.
- `Hook.ShouldDraw` prefix notes draw attempts while source context is still recoverable.
- `Hook.ShouldDraw` postfix records blocked attempts when result is false and exposes the blocking modifier.
- `Hook.AfterCardChangedPiles` can identify final pile result for redirected movement.

Do not derive draw solely from play counts. A card can be drawn and not played; a draw effect can attempt to draw and be blocked by No Draw, hand size, or other prevention.

Blocked draw reason rows should stay explanatory rather than pretending full certainty. Known buckets include No Draw, hand full, and other/uncategorized.

## Energy, Stars, And Forge

For resource generation, record the actual mutation, not card text.

Energy:

- Hook `PlayerCombatState.GainEnergy`.
- Prefix captures before value.
- Postfix computes positive delta and attributes to the currently resolving card owned by that player.

Energy generated is paired with energy wasted through a provenance ledger in
`_pendingCombat.PlayerEnergyLedger`, built on exactly the model the block
ledger uses. Generated alone reads as pure profit; the ledger is what lets a
card say how much of what it handed you expired unspent.

- Every arbitrated gain appends an `EnergyChunk` owned by one source: a card
  instance, a relic id, or nobody. Orb energy and the player's own turn refill
  are deliberately ownerless, so waste falls on them before it falls on a card.
- `PlayerCombatState.LoseEnergy` is the single spend point — card costs,
  X-cost payments and enemy drains all reach it — and consumes the ledger FIFO.
  Do not infer the spend from a finished card play instead: the pool is already
  short by the card's cost while the card is still resolving, so a
  mid-resolution gain would reconcile that shortfall as LIFO *waste* rather
  than FIFO *spend*, and blame the wrong card.
- `PlayerCombatState.ResetEnergy` is the waste point, patched as a prefix
  because the leftover is unreadable afterwards. `CombatManager.SetupPlayerTurn`
  calls `AddMaxEnergyToCurrent` instead when `Hook.ShouldPlayerResetEnergy`
  says the pool carries over, so conserved chunks never reach the waste path.
  `Hook.AfterEnergyReset` fires on BOTH branches and cannot tell them apart —
  do not use it for this.
- The turn allowance is split into one chunk per source rather than entering as
  one ownerless block. `PlayerCombatState.MaxEnergy` is the character's own
  allowance folded through `Hook.ModifyMaxEnergy`, which runs each combat hook
  listener in turn; replaying that fold at both refill points recovers exactly
  what each source added, so a max-energy relic owns its point of the pool.
  This is what keeps attribution from needing an invented rule: left as one
  block, splitting waste across max-energy relics would take a made-up
  convention, whereas ordered chunks inherit the FIFO/LIFO convention every
  other chunk follows, in the game's own listener order. Quantise per step so
  the chunks sum to the integer the pool receives, and let a failed replay fall
  through to the next reconcile rather than guessing.
- Energy still in the pool when combat ends is wasted too; there is no later
  turn to spend it on.
- Three relics measure their own gain from a before/after pool delta rather
  than through an attribution window: Art of War, Seal of Gold and Venerable
  Tea Set. They arm an `AttributionEventKind.PlayerEnergyLedgerOwner` window
  instead, which names the chunk's owner and credits no counter. An ordinary
  `PlayerEnergyGain` window would tag the chunk AND add the amount to
  `EnergyGenerated` a second time, and removing their own counting instead
  would strip the headless coverage those stats have today.
- Reconcile against `PlayerCombatState.Energy` after every observed mutation
  rather than trying to catch every route into the pool. A shortfall enters
  untagged, a surplus is charged off as waste. Skipping this lets an
  unobserved gain shift later waste onto the wrong chunk.

Every route into the player's energy pool is owned by one of those three
mechanisms, which is what lets the relic tooltip print generated/wasted as a
single paired row: the zero means zero rather than "not measured". A new energy
relic must be given a chunk owner too, or that row starts quietly asserting
something untrue for it.

As with block, say plainly that effective/wasted energy is ledger attribution
under a stated convention, not a game-native per-source truth.

Stars:

- Hook `PlayerCombatState.GainStars` the same way.
- Star spend is from actual play resources, not listed star cost.

Forge:

- Hook `Hook.AfterForge`.
- Record actual forge amount, forger, and source.
- This also preserves effect source context for immediate follow-up effects and marks Sovereign Blade overlay availability.

For UI, energy/star spent rows intentionally appear only when empirical variance exists or when the resource is otherwise interesting. Absence of a row usually means actual spend matched expected listed cost across plays.

## Card-Sourced Orb Creation

`OrbCmd.Channel` emits `OrbChanneledEntry` only after `OrbQueue.TryEnqueue`
succeeds. Observe that entry through the established `CombatHistory.Add` hook
while a tracked card play is still resolving, and require the orb owner to
match the source card owner. This counts every orb that actually enters the
queue—including repeated channels from cards such as Glacier—while excluding
failed channels and orb creation from relic, turn-start, or other ownerless
card contexts. Store the orb id on the event even when the current tooltip
only needs the total, so later type breakdowns remain derivable.

Retain the exact mutable orb reference with the physical source-card instance
for the rest of combat. Subscribe that instance's native `PassiveActivated`
and `EvokeActivated` events to credit its source card's orb-type bucket;
`OrbQueue.RemoveCapacity` remains the non-evoke/fizzle boundary, while ordinary
combat cleanup remains excluded. This is lineage attribution, not a widened
"recent card" timing window, and it avoids introducing per-orb Harmony targets.

Frost orbs call `CreatureCmd.GainBlock` with a null `CardPlay`. Their native
activation event arms a one-shot window consumed by the already-established
`Hook.AfterBlockGained` path. The intervening observed `BlockGainedEntry` goes
to the Frost-orb bucket and bypasses the generic current/recent-card fallback.
Otherwise a Glacier-created Frost orb can incorrectly add its block to
Glacier—or to an unrelated recently completed card—as direct card block.

Lightning orbs likewise call `CreatureCmd.Damage` without a `CardSource`.
When Lightning's private `ApplyLightningDamage` routine belongs to an exact
card-sourced orb, observe that command's returned `DamageResult` values and
write the attempted, effective, blocked, overkill, kill, and target outcomes
to the source card's orb-type bucket. Keep these separate from the card's
direct Attack totals: Ball Lightning's own hit and the lifetime damage from
the Lightning orb it channeled are distinct value streams.

## Effects And Powers

Power/effect attribution needs two layers:

1. Application attribution: which card caused a power/effect to be applied and for how much.
2. Downstream attribution: what that applied effect later caused, if observable and meaningful.

`PowerReceivedEntry` and related hooks can record applications when card source is available. But receiver-side modifiers can change or eliminate the application.

Artifact-blocked debuffs require a before/after pair:

- `Hook.BeforePowerAmountChanged` captures attempted power, amount, target, applier, and card source before receiver-side modifiers.
- `Hook.ModifyPowerAmountReceived` postfix sees the final result and modifiers. If requested amount was reduced to zero by Artifact, SpireLens can still credit the blocked attempt to the source card.

Do not record only successful applications. A debuff eaten by Artifact is still an important thing the card caused, and it should surface as blocked/stripped rather than disappearing.

Buffer is the reference case for a stacking counter power whose payoff cannot
be traced to one physical card:

- `BufferPower` is `PowerStackType.Counter` and inherits
  `PowerInstanceType.None`, so `PowerCmd.FindExistingInstanceForStacking`
  resolves it to `target.GetPower(id)` and every application merges into one
  power object with an integer `Amount`. Two Buffer plays do not make two
  power instances; they make `Amount = 2`. There is no game-native answer to
  "which application is this charge."
- Application attribution is still exact, because `PowerCmd.Apply` and
  `ModifyAmount` both thread `cardSource`, and `Cards.Buffer.OnPlay` passes
  its own instance. `Potions.LuckyTonic` also grants Buffer, with a null card
  source, so charge accounting has to sit ahead of any card-source
  requirement or the potion's charges vanish.
- Downstream attribution is observed from the power itself.
  `BufferPower.ModifyHpLostAfterOstyLate` returns 0 for its owner, so the
  prevented amount is the drop across that call — post-Block unblocked HP
  loss, not the attack's intended damage. `AfterModifyingHpLostAfterOsty`
  then confirms the spend.
- Arm on the modifier and commit on the follow-up, and gate the arm on the
  same `decimal.Truncate(before) != decimal.Truncate(after)` test that
  `Hook.ModifyHpLost` uses to decide which modifiers get the follow-up call.
  That keeps every armed prevention paired with exactly one confirmed charge,
  and it is why a fully blocked or zero-damage hit never burns a charge.
- Commit on the follow-up's prefix, not after its `PowerCmd.Decrement`.
  `ModifyAmount` early-returns while `CombatManager.IsEnding`, so waiting for
  the decrement would discard preventions that really stopped HP loss in the
  killing-blow window.
- `Hook.ModifyHpLost` is called from exactly one place, `CreatureCmd.Damage`,
  so Buffer never touches card-cost or curse HP loss. Note that the Osty
  redirect branch runs the `AfterOsty` phase a second time for overkill spill,
  which can legitimately spend a second charge in one damage command.
- Outcomes land in **both** places, and the distinction matters. The pooled
  `PowerAggregate` carries the family total. `_pendingCombat.PlayerBufferLedger`
  carries per-source provenance, so an individual Buffer card and a Lucky Tonic
  each get their own charges and prevented HP.
- Do not pool a multi-source power just because the surface is power-shaped.
  Free Attack and Free Skill pool because Unrelenting and Pounce are their only
  sources, so pooling costs them nothing. Buffer has two sources, which makes
  the right analogues `PlayerBlockLedger` and the Poison ownership ledger —
  both multi-contributor, both solved with a contributor ledger documented as
  heuristic.
- Charges granted are exact: the application event names its source, either
  through `cardSource` or through the Lucky Tonic use frame. Only the question
  of which charge absorbed which hit is heuristic, and FIFO is the stated
  convention, matching block's absorb order. Buffer is in fact more determinate
  than block: charges are discrete, so a spend consumes exactly one ledger
  unit rather than an arbitrary slice of a fungible pool.

### Registry Ids Come From The Game, Not From Typing

`ModelDb.GetId` slugifies the whole type name and only strips `_MODEL` from
the category. So `RupturePower` is `POWER.RUPTURE_POWER` and `BufferPower` is
`POWER.BUFFER_POWER` — the suffix is part of the id, not decoration to trim.
Card ids have no such suffix, so `CARD.RUPTURE` is already correct.

`MetaPowerRegistry`, `OrbCardRegistry`, and `RandomCardGenerationRegistry`
therefore derive their ids from the game types through `ModelIds.TryGet<T>()`
rather than hand-copied strings. `TryGetByPower` matches on
`power.Id.ToString()`, and every hand-copied entry used a shortened form that
never matched, so `SuccessfulApplications` and `MetaActiveTurns` recorded zero
for every meta power across every run from the registry's introduction until
this was fixed. Do not reintroduce power-id string literals in these
registries; add the type pair instead, so a renamed game class is a compile
error rather than a lookup that quietly returns nothing.

Tooltip switch labels use **card** ids for the same reason: card ids are safe
as compile-time constants, derived power ids are not.

Legacy data note: run files written before the fix can contain two aggregates
for one power — denominators under the shortened key (written from the
card-driven path, which always worked) and outcomes under the canonical key
(written from `power.Id.ToString()` paths). There is deliberately no
reconciliation: the two stats that actually broke were never written to disk,
so a merge could only recover a negligible amount, and it is not worth new
logic in the persistence path every run file passes through.

## Poison And Other Downstream Damage

Poison demonstrates the core challenge of downstream attribution: the damage tick often arrives with `CardSource == null`, but users care which card originally applied the poison.

Current poison model:

- Poison applications are recorded as applied effects on the source card.
- `_pendingCombat.PoisonOwnershipByTarget` tracks ownership shares by target and source effect.
- `PoisonPower.AfterSideTurnStart` arms a one-shot attribution window for the target.
- The next null-source damage on that target can be recognized as poison tick damage and charged through the poison ownership ledger.
- Damage and overkill from poison ticks are added back into the source effect summary.

Noxious Fumes has an extra wrinkle:

- It is a power that later applies poison without direct card source on each application.
- `NoxiousFumesPower.AfterSideTurnStart` arms a short attribution window.
- Contribution ledgers preserve which source card owns the Fumes effect before the poison fanout is added to poison ownership.

Outbreak is deliberately separate from the ordinary side-turn poison ledger:

- Its current `OnPlay` first applies Poison to every hittable enemy, then explicitly calls each surviving `PoisonPower.Trigger()` while the physical Outbreak card is still the resolving player card.
- `PoisonPower.Trigger` may issue multiple damage commands according to `TriggerCount`. Each uses the single-target decimal `CreatureCmd.Damage` overload with `ValueProp.Unblockable | ValueProp.Unpowered` and null card source/play.
- Arm at `PoisonPower.Trigger` only while Outbreak is resolving, consume only those exact target/amount/flags/null-source commands, and sum their returned `DamageResult.UnblockedDamage` values. This observes effective damage without claiming ordinary side-turn Poison ticks or unrelated damage.
- Because the resolving physical card is still known, this outcome belongs on that Outbreak card instance rather than a pooled power aggregate.

When adding another downstream effect stat, follow the poison pattern only if there is a reliable arming event and a narrow enough outcome window. Anonymous damage or anonymous pile movement without a narrow source window should not be guessed broadly.

## Relic Attribution

Relics are not cards, but many relic stats use the same runtime lessons.

Combat-start relics often hook relic model callbacks directly by type name with `AccessTools.TypeByName`, then filter on side and round:

- Bag of Marbles: `BeforeSideTurnStart`, `CombatSide.Player`, `RoundNumber == 1`, count alive enemies, record Vulnerable applied.
- Red Mask: same shape, record Weak applied.

This is intentionally outcome-shaped but still simple. It assumes the relic applies one stack to each alive enemy at combat start. If a future relic has prevention/modifier interactions, use the same observed-result discipline as cards rather than only counting targets.

Relic aggregates live in `RunData.RelicAggregates`, keyed by relic id. Fields are shared across relics; each relic uses only relevant fields.

The filtered relic bar treats combat relevance as an effective live state,
not a mutation of the saved classification. A relic with a finite combat
duration enters a per-model, combat-local resolved set when its native
`RelicModel.Flash(IEnumerable<Creature>)` activation fires. Bag of Preparation,
Ring of the Snake, and Symbiotic Virus do not flash, so their established
owner-specific activation hooks mark the same state directly. The configured
turn is retained as an exclusive fallback cutoff, which also reconstructs the
right projection after a Core hot reload. Any relic whose native `IsUsedUp`
becomes true is effectively non-combat immediately; recurring finite relics
leave the transient set at combat end and remain combat-classified for the
next fight. This state is presentation-only and never removes or disables a
relic.

Terminal combat relevance is a separate no-turn-fallback path. Burning Sticks,
Centennial Puzzle, Pael's Eye, Permafrost, Ruined Helmet, Throwing Axe,
Unsettling Lamp, and Vambrace have one native activation per combat; their
activation flash resolves their filtered-bar relevance immediately. Lava Lamp
has the equivalent irreversible failure state: its `TookDamageThisCombat` flag
permanently disqualifies its reward upgrade until the next combat. Read each
relic's authoritative native boolean in addition to the transient flash set so
a Core reload during combat reconstructs the terminal state without inventing
a turn cutoff. Reversible states such as Belt Buckle, Meat on the Bone, and Red
Skull are deliberately excluded because they can become relevant again in the
same combat.

Bag of Preparation raises its owner's first-turn hand-draw request inside
`ModifyHandDraw`; the game then finishes the complete normal/late modifier
chain, raises the first hand to any larger eligible Innate count, clamps it to
the hand limit, and finally awaits the four-argument `CardPileCmd.Draw`.
Count the positive owner-specific modifier as the activation, but measure cards
drawn from that completed draw task. Its observed contribution is the cards
that arrived beyond the counterfactual request with Bag's delta removed,
capped by Bag's surviving contribution after Innate and hand-limit clamping.
This preserves activations when draw prevention yields no cards and avoids
crediting Bag for a starting hand that Innate cards would already have made
equally large. Record the difference between that surviving contribution and
the observed contribution as Bag-attributed card draws blocked.

Ring of the Snake has the same `ModifyHandDraw(Player, decimal)` body and uses
the same completed first-hand draw observer. Keep its pending counterfactual
and relic aggregate separate from Bag of Preparation so a player holding both
receives each relic's own marginal observed contribution rather than a pooled
opening-hand total. Its blocked-draw total uses the same surviving-contribution
shortfall.

Joss Paper is a mixed immediate/deferred exhaust counter. Its owner-specific
`AfterCardExhausted` callback is the authoritative successful-exhaust count,
but Ethereal cards are only folded into `CardsExhausted` at
`AfterSideTurnEnd`. Patch the private `DrawIfThresholdMet` method to count every
completed five-card threshold (one deferred batch can cross multiple
thresholds), then observe the returned cards from the exact four-argument
`CardPileCmd.Draw` call instead of assuming every requested draw reached the
hand. Sample the 0-4 remainder only after Joss Paper's awaited turn-end callback
finishes, so the snapshot includes Ethereal cards and any resulting reset.

Centennial Puzzle already exposes the exact combat-local state needed for a
live tooltip through `UsedThisCombat`. The relic sets it before drawing from
its first qualifying unblocked HP-loss callback and resets it in
`AfterCombatEnd`. Read that property directly for `Triggered this combat`;
do not infer the boolean from cumulative activations or persist it in the run
aggregate.

Juzu Bracelet is map-point based, not resolved-room based. Count
`RunManager.EnterMapPointInternal` when the original `MapPointType` is
`Unknown` and the player currently holds the relic. Do not infer this stat from
`RoomType.Event` or `EventRoom`: a `?` can resolve into multiple room types, and
later room transitions can happen after the map site was already entered.

Winged Boots owns the authoritative signal for spending one of its three
off-path jumps. Its `AfterRoomEntered` callback compares the prior map point's
children with the current point and increments its saved `TimesUsed` only when
the move consumed a charge. Compare `TimesUsed` before and after that callback,
then record the current point's original `MapPointType` under the resulting use
number. Do not infer activations from general room entry or merely from the
relic allowing free travel; those also occur for normal connected movement.

Wongo's Mystery Ticket activates through its owner-specific
`TryModifyRewards` callback after its saved `CombatsFinished` counter reaches
five. A successful call appends three concrete `RelicReward` objects to that
combat's reward list. Mark only those appended references, preserve the weak
markers in process-stable `AppDomain` data across Core hot reloads, and record
each reward's `ClaimedRelic` only after its `OnSelect` task succeeds. This keeps
an ordinary Elite relic reward on the same screen out of the ticket's ledger.
The activation floor is the current run floor at that successful reward
modification; subtract the relic's saved `FloorAddedToDeck` to report floors
ascended before activation.

Small Capsule constructs one unpopulated `RelicReward` and passes it to
`RewardsCmd.OfferCustom` synchronously before its pickup callback's first
await. Open a registration window around `SmallCapsule.AfterObtained`, bind
only that exact reward in the shared `OfferCustom` hook, and preserve the weak
marker in process-stable `AppDomain` data across Core reloads. The concrete
relic is authoritative only after `RelicReward.Populate`; resolve that same
entry as taken only after `OnSelect` returns true and exposes `ClaimedRelic`,
or as skipped from `OnSkipped`. Do not infer either outcome from a later relic
inventory comparison.

Dowsing Rod itself only grants the Dowsing quest card. The card owns the saved
`RoomsEntered` counter, updates it only for qualifying `?` room entries, and
transforms into Abundance at five. Observe the `Dowsing.RoomsEntered` setter and
derive remaining rooms as `5 - RoomsEntered`; do not independently decrement at
the broader map-point hook. Reconcile from the live Dowsing/Abundance deck card
after Continue or hot reload so the relic tooltip inherits the game's persisted
quest state.

Fishing Rod owns a saved `CombatsSeen` counter and increments it only after
normal monster combats. Every third increment, `FishingRod.AfterCombatEnd`
chooses one random upgradable permanent-deck card and calls `CardCmd.Upgrade`
synchronously before returning its completed task. Attribute the result by
arming a window around that exact callback and consuming the existing
`CardModel.UpgradeInternal` observation; do not infer the chosen card from deck
state or independently reproduce the relic's RNG selection.

The same completed callback is Fishing Rod's floor-pacing boundary. After it
succeeds, record the normal combat's total run floor even when no card was
upgraded. Combat distance runs from acquisition to the first normal combat and
then between consecutive normal combats; upgrade distance runs from acquisition
to the first successful upgrade and then between successful upgrades. If floor
tracking begins after the relic's saved `CombatsSeen` or SpireLens upgrade count
already indicates prior events, use the first newly observed event only as the
baseline rather than inventing a distance from incomplete history.

Armaments upgrades through the same synchronous `CardCmd.Upgrade` /
`CardModel.UpgradeInternal` path while its `OnPlay` callback is still the
currently resolving card. Attribute each observed upgrade to that physical
Armaments instance at the shared `UpgradeInternal` observer. Normal Armaments
therefore contributes at most one upgrade per play, while Armaments+ contributes
one for every upgradable hand card it actually changes; already fully upgraded
cards are absent from the game's candidate set and are not inferred or counted.

War Hammer's `AfterCombatVictory` callback runs after an Elite victory, before
the game's later `CombatEnded` event promotes SpireLens's pending combat. It
selects up to four random upgradable permanent-deck cards and calls
`CardCmd.Upgrade` synchronously. Wrap the callback's returned task so attribution
finishes before promotion, and consume the existing `CardModel.UpgradeInternal`
observation. Persist each upgraded card's stable deck instance id as well as
its display name; later completed plays can then be attributed to the exact
physical cards across combats and hot reloads. Use every player turn and combat
where War Hammer was held as the zero-inclusive play-rate denominators.

Sword in the Stone (`SwordOfStone`) advances in its owner-specific
`AfterCombatVictory(CombatRoom)` callback and replaces itself with Sword of
Jade after the fifth Elite. Capture the Elite encounter title/id and run floor
before the callback, then persist the kill only after its returned task
completes successfully so a failed replacement is not recorded as progression.
Keep both relic forms under the original `RELIC.SWORD_OF_STONE` stats identity.
Sword of Jade's `AfterRoomEntered(AbstractRoom)` applies Strength on combat-room
entry; measure the owner's Strength before and after that completed callback so
the shared aggregate records the observed gain rather than its listed base
value. Count every combat in which Sword of Jade is held as the zero-inclusive
Strength-rate denominator, but exclude pre-transformation Sword in the Stone
combats because that form cannot grant Strength. Keep a matching
observation-era Strength numerator for this denominator: lifetime Strength
totals can predate combat-rate tracking and must not be divided by only newly
observed combats.

Molten Egg, Toxic Egg, and Frozen Egg share `EggRelicHelper.UpgradeValidCards`
for both late card-reward modification and merchant inventory modification.
For each matching upgradable option, that helper calls the two-argument
`CardCreationResult.ModifyCard(CardModel, RelicModel)` overload with the egg as
the modifying relic. Observe that exact call to count upgraded cards offered:
it covers rewards, shops, and other choosable-card surfaces that use the shared
helper, while excluding the eggs' separate `TryModifyCardBeingAddedToDeck`
path for direct non-choosable deck additions. The `CardModel` passed to
`ModifyCard` is the upgraded clone and retains the offered card's rarity, so
Common, Uncommon, and Rare breakdowns should be read from that same confirmed
offer callback rather than reconstructed from later reward state. Keep a
one-use reference marker on that exact modified `CardModel`; card rewards,
merchant purchases, and other normal choice surfaces then pass the same model
to `CardPileCmd.Add(CardModel, PileType, ...)`. Consume the marker only after a
successful permanent-deck add to count the card as taken. This distinguishes
observed takes from skipped offers and from direct non-choosable grants without
inferring egg attribution from the card's upgrade level. Keep this weak
reference ledger in process-stable `AppDomain` data rather than a Core-static
collection so an open offer remains attributable across a Core hot reload;
weak keys let skipped or replaced offers disappear with the game's card model.

Bing Bong handles every same-owner card entering the permanent Deck by cloning
that card and calling the five-argument `CardPileCmd.Add` overload with the
relic itself as `clonedBy`. Observe that established overload and wrap its
returned task only when `clonedBy is BingBong`; classify the final
`CardPileAddResult.cardAdded` after a successful add rather than the requested
clone before the shared add pipeline can replace or reject it. Curse is a card
type and should take precedence over the card's rarity so Curse and
Common/Uncommon/Rare remain mutually exclusive display buckets. Basic or Event
edge cases still belong in the total successful extra-card count without being
misreported as one of those requested buckets.

Pael's sacrifice reward option is owned by `PaelsWing`, not `PaelsFlesh`.
`PaelsWing.TryModifyCardRewardAlternatives` adds the `SACRIFICE` card reward
alternative and `PaelsWing.OnSacrifice` increments the saved sacrifice count.
`PaelsFlesh` is a separate combat max-energy relic that activates after turn 3.
Track consumed card reward rarities and skipped sacrifice opportunities from the
card reward alternative flow, not from PaelsFlesh's energy hooks. Every second
sacrifice pulls and obtains a normal `RelicModel`. Capture that direct relic
from the owner's first `RelicObtained` event during `OnSacrifice`; the event fires
synchronously when the relic enters inventory, before its async `AfterObtained`
callback. Store its id, display name, and count in Pael's Wing's `RelicsGranted`
ledger. Derive the displayed sacrifice rate from exact recorded opportunities:
`SacrificesMade / (SacrificesMade + SacrificesSkipped)`. Do not use floors since
pickup; that incorrectly counts acquisition or non-reward floors where Sacrifice
was never offered.

Pael's Eye can activate at most once per combat through its owner-specific
`BeforeSideTurnEndEarly` callback. Snapshot the exact cards in its owner's hand
and the owner's current `PlayerCombatState.TurnNumber` after the native
`ShouldTakeExtraTurn` condition succeeds, then wait for the callback to finish.
Count only snapshot cards whose final pile is Exhaust; classify starter Strikes
and Defends with the game's `IsBasicStrikeOrDefend` property. Held combats are
a separate zero-inclusive denominator, while activation-turn samples include
only completed activations, so combats where the relic did not fire do not
pull the average activation turn toward zero.

Pael's Tooth is a separate pickup-and-return mechanic. Its native `CardTitles`
text is rebuilt from only the `SerializableCards` still held by the relic, so a
card intentionally disappears from that text when it is returned. At combat
end, `Hook.AfterCombatEnd` awaits relic listeners sequentially before the
game's `CombatEnded` event: Tooth reconstructs one stored card, upgrades it,
awaits its deck insertion, then removes the stored entry. Wrap Tooth's returned
task so attribution completes before combat promotion, and compare raw deck
references before and after to capture the final physical card after upgrade
and deck-add replacement modifiers. Do not infer a successful return merely
from the stored entry disappearing; Tooth removes it even when the deck-add
result reports failure.

Spiked Gauntlets has no owner callback for its Power tax: its
`TryModifyEnergyCostInCombat` modifier adds one to every same-owner Power, while
`ModifyMaxEnergy` grants one Ancient max energy. Count completed owner Power
plays at the established `CardPlayFinishedEntry` boundary and keep both the
resolved `EnergyValue` cost and actual `EnergySpent`; autoplay/free plays can
have a taxed resolved cost while spending zero. Sample held turns and combats
zero-inclusively, and count Ancient energy at the established
`Hook.AfterEnergyReset` boundary rather than from repeated max-energy queries.

For relics that grant block after a specific owner-owned condition, arm a narrow block-gain window at the relic callback and let `Hook.AfterBlockGained` record the modified amount. Permafrost follows this pattern from `Permafrost.AfterCardPlayed`: mirror the first-owned-Power condition, count that combat trigger, then derive block per triggered combat from observed block gained divided by triggers. Count every combat where Permafrost was held, including zero-trigger combats, as the separate trigger-rate denominator. Older runs predate that denominator; because Permafrost can trigger at most once per combat, backfill the minimum known historical combat count from its activation total before adding newly observed combats. Its private `_activatedThisCombat` field is the authoritative live source for whether it has triggered in the current combat; display that state directly rather than inferring it from persisted activation totals.

Orichalcum only pays out when the player ends the turn holding no Block, and its
`BeforeSideTurnEndVeryEarly` callback is where the game itself performs that
comparison — the leftover Block readable there is the exact value that decided
the outcome. When leftover Block is present the relic stays silent, and the
interesting quantity is what the leftover cost: leftover strictly between zero
and the relic's own `DynamicVars.Block.BaseValue` leaves the player holding less
than the suppressed trigger would have granted, so score the difference. Leftover
at or above that amount costs nothing and must not be scored, which makes the
undercut-turn count a strict subset of the existing blocked-trigger count. Read
the threshold from the relic rather than hardcoding 6 so an upgraded or modified
amount stays correct. This shortfall is a **counterfactual**, not an observed
outcome: it assumes the leftover Block could have been spent or avoided, which an
unavoidable auto-block source can make false. Keep it in fields separate from the
observed `AdditionalBlockGained` ledger, and keep it separate from
`AdditionalBlockWasted`, which already means relic-granted Block that expired
unused — a different measurement entirely. Count every player turn and combat
where the relic was held as the average denominators so periods that missed
nothing stay in both.

Cloak Clasp's owner-specific `BeforeSideTurnEnd` callback makes exactly one
block command for `cards in hand * Block`, and skips the command when the hand
is empty. Keep its existing one-shot observed-block attribution window for the
numerator. Count every player turn and combat where the relic was held as the
average denominators, including empty-hand turns and turns where combat ended
before the end-turn callback could run.

Intimidating Helmet's `BeforeCardPlayed` condition uses the frozen play-time
`cardPlay.Resources.EnergyValue`, not printed cost or `EnergySpent`. Normal
plays therefore use the energy actually paid, resolved X-cost plays use their
captured X value, and autoplay can qualify at its resolved cost despite spending
zero energy. The callback immediately awaits the `BlockVar` overload of
`CreatureCmd.GainBlock`; consume an owner-creature marker at that exact command
and record its returned post-modifier amount. Use all distinct combats and
player turns while the relic is held, including zero-trigger ones, as the
per-combat and per-turn block denominators.

`JugglingPower` already owns the exact turn-local progress needed for its live
counter. `AfterApplied` seeds `attacksPlayedThisTurn` from owner Attack plays in
combat history, `BeforeCardPlayed` increments it for subsequent owner Attacks,
and `AfterSideTurnEnd` resets it. Surface that field through `DisplayAmount` and
raise `DisplayAmountChanged` after each mutation; do not repurpose `Amount`,
which remains Juggling's stack count and controls how many Attack copies it
creates. The progress counter continues above the third-Attack trigger and does
not belong in persisted run data. Juggling's outcomes are power-owned rather
than source-card-owned: arm a narrow window when the pre-increment counter is
`2`, observe each awaited `CardPileCmd.AddGeneratedCardToCombat` result, and
count only copies that actually enter a pile. Persist those totals and rarity
splits in the run meta-stats power aggregate keyed by `POWER.JUGGLING`; every
Juggling card tooltip projects that shared record. Count the application turn
and each later turn that starts with the power, plus each distinct combat, so
the per-turn and per-combat averages include active zero-copy periods.

Musical Box's owner-specific `AfterCardPlayed` callback retains the first
owner Attack of the turn in `_cardBeingPlayed`, clones it, gives the clone
Ethereal, and awaits one `CardPileCmd.AddGeneratedCardToCombat`. Arm a
single-attempt attribution window only when that private reference is the exact
callback card, then classify the final successful `CardPileAddResult.cardAdded`
rather than the requested clone. Keep the exact added card reference through
combat and consume it only when `Hook.AfterCardExhausted` reports
`causedByEthereal`; `CardExhaustedEntry` does not preserve that reason. Count
all successful Attack copies in the total, but keep Basic/Event edge cases out
of the Common/Uncommon/Rare buckets. Held-turn and held-combat denominators are
zero-inclusive so the creation averages include turns and combats where the
box produced no card.

`ViciousPower.AfterPowerAmountChanged` owns the exact trigger condition: a
positive Vulnerable change applied by the power's owner. Arm a narrow window
around that callback and consume it at the immediate `CardPileCmd.Draw` call.
Count the non-null cards returned by that command, not the Vicious stack amount,
so No Draw, hand capacity, and pile exhaustion remain observed zero-value
outcomes. Persist the shared total under `POWER.VICIOUS` and project it on
every Vicious card rather than attributing later triggers to one physical copy.

`StampedePower.AfterAutoPostPlayPhaseEntered` selects eligible Attacks from the
owner’s hand and sequentially awaits `CardCmd.AutoPlay`. Keep a callback-wide
window, claim each direct autoplay, and suspend that window until the claimed
task completes so nested autoplays caused by the Attack are not misattributed.
Count the selection only when its primary `CardPlayFinishedEntry` arrives.
Use that play's `Resources.EnergyValue` as energy saved because it is the
resolved amount a normal play would spend while autoplay spends zero. Persist
the totals and rarity splits under `POWER.STAMPEDE` and project them on every
Stampede card.

`AggressionPower.BeforeSideTurnStart` selects owner Attacks from the discard
pile, sequentially awaits `CardPileCmd.Add(card, PileType.Hand)`, and then
upgrades each selected card only when it remains upgradable. Keep a
callback-wide window while excluding nested pile adds. Count a return only
from a successful add whose resulting card is in hand; independently count an
upgrade only when the exact callback-selected card reaches
`CardModel.UpgradeInternal`. Persist both shared outcomes under
`POWER.AGGRESSION` and project them on every Aggression card.

All for One selects effective zero-cost non-X Attacks, Skills, and Powers from
its owner's discard pile, then sequentially awaits their movement to Hand.
Count returns from the established `Hook.AfterCardChangedPiles` final-result
boundary only when the currently resolving card is All for One, the owner
matches, and the exact card moved from Discard into Hand while still matching
the game's cost/type filter. This keeps hand-full redirects and failed moves
out of the total. Use the physical All for One's ordinary `Plays` and
`CombatsInDeck` fields for its per-play and zero-inclusive per-combat averages.

`RupturePower` has two payoff boundaries. Qualifying self-damage outside the
currently resolving owner card applies Strength immediately from
`AfterDamageReceived`; damage caused during an owner card play is accumulated
and applied once from `AfterCardPlayed`. Compare the owner's Strength before
and after both completed callbacks, so multi-hit cards are counted once at
their combined payoff and Strength modifiers remain reflected in the observed
gain. Count the positive application turn from `PowerReceivedEntry` and every
later player turn that starts with Rupture, including zero-trigger turns.
Persist `StrengthGained` and `TurnsActive` under `POWER.RUPTURE`, then project
the shared total and per-active-turn quotient on every Rupture card.

`FeelNoPainPower.AfterCardExhausted` owns an exact owner-card check and then
awaits the decimal/`ValueProp` `CreatureCmd.GainBlock` overload. Arm from that
callback and replace the command's returned task with an observer so the
post-modifier block amount is recorded before the power listener completes.
For the per-active-turn denominator, count the positive application turn from
`PowerReceivedEntry` and every later player turn that starts with the power,
including turns with no exhausts. Persist the numerator as `BlockGained` and
the denominator as `TurnsActive` under `POWER.FEEL_NO_PAIN`, then project the
shared quotient on every Feel No Pain card.

`DarkEmbracePower` has two draw paths. Non-Ethereal owner cards draw
immediately from `AfterCardExhausted`; Ethereal cards increment an internal
counter and produce one deferred batch from `AfterSideTurnEnd`. Arm each native
callback independently, consume the window at its direct `CardPileCmd.Draw`,
and count only the cards returned by that command so blocked or capacity-limited
draws stay observed outcomes. Count the application turn and later turns that
start while the power is active for the active-turn denominator. Also count
every player turn in a combat where Dark Embrace became active, including turns
before it was played, by finalizing that combat-wide denominator at promotion.
Persist both turn denominators, the active-combat count, and cards drawn under
`POWER.DARK_EMBRACE`, then project all four rows on every Dark Embrace card.

`ConsumingShadowPower.AfterSideTurnEnd` sequentially calls and awaits
`OrbCmd.EvokeLast` once per Power stack, but later calls can become no-ops when
the queue runs empty or combat begins ending. Keep a callback-wide window,
claim each direct `EvokeLast` while excluding nested calls, retain the exact
last orb, and count it only when that same orb reaches `Hook.AfterOrbEvoked`
after its `Evoke` task completes. Persist the observed count under the shared
`POWER.CONSUMING_SHADOW` aggregate because multiple card applications stack
into one Power; do not confuse this with the lifecycle evocations credited to
the cards that originally channeled those orbs.

`DanseMacabrePower.BeforeCardPlayed` owns both the exact trigger condition and
the block command: an owner card whose resolved energy cost is at least the
power's Energy dynamic variable causes one flash and one awaited
`CreatureCmd.GainBlock(Creature, decimal, ValueProp, CardPlay, bool)`. Count
the callback trigger there, arm only that immediate overload, and use its
returned post-modifier amount as block gained. The application turn is
observed from the successful `PowerReceivedEntry`; later turns come from
`Hook.AfterPlayerTurnStart`. Persist the shared totals and active
turn/combat denominators under the power ID, and project them on every Danse
Macabre card rather than assigning later power behavior to one physical copy.

For relics that emit damage commands, prefer the relic-owned callback plus the resolved `CreatureCmd.Damage` result. Mercury Hourglass arms from `MercuryHourglass.AfterPlayerTurnStart` and records the actual multi-target damage split from the command result on each turn. Every armed turn start is one activation; the per-combat averages divide by a separate zero-inclusive held-combat denominator (`MercuryHourglassCombats`, counted from the shared held-combat baseline pass) rather than by the activation count.

Screaming Flagon follows the same observed-result pattern from its owner-specific
`BeforeSideTurnEnd` callback. Mirror the callback's participating-owner and
empty-hand conditions, count that callback as one activation, and consume only
the immediately emitted multi-target `DamageVar` command. Keep the activation
when the resolved result is empty: the relic still fired even if there were no
damageable targets. That same callback is also the authoritative hand-size
snapshot before the empty-hand check: record every player-side invocation,
including non-empty hands, and reconcile a combat-ending turn at pending-combat
promotion. Held turns and combats are zero-inclusive denominators.

Lost Wisp uses the same observed-result pattern from
`LostWisp.AfterCardPlayed`. Arm only when the callback receives an owner Power
while combat is in progress, count that qualifying Power as the activation,
then consume the window at the immediately emitted decimal-plus-`ValueProp`
multi-target `CreatureCmd.Damage` overload. The resolved results are
authoritative for blocked damage, overkill, kills, and targets hit.

Game Piece also owns an `AfterCardPlayed` callback for each same-owner Power
played while combat remains in progress. Count that qualifying Power at the
owner callback, then consume the callback's direct non-hand
`CardPileCmd.Draw` call and count the non-null cards returned by its completed
task. The requested-minus-returned difference is the blocked draw total, so No
Draw, a full hand, and pile exhaustion remain observed zero-value outcomes.
Count every distinct player turn and combat while Game Piece is held as its
zero-inclusive cards-drawn rate denominators, and reconcile the current turn
at combat promotion so a combat-ending turn is not omitted.

Forgotten Soul owns `AfterCardExhausted` and flashes for every same-owner card
exhaust, even when no hittable enemy remains. Count that callback as the
activation, then keep a callback-scoped window around the exact single-target
`DamageVar` overload it may emit. The resolved result is authoritative for
dealt/blocked damage, kills, and targets hit; always disarm the window when the
callback completes so a no-target activation cannot claim unrelated later
damage. Use every player turn and combat where Forgotten Soul was held as the
zero-inclusive damage-rate denominators.

Gremlin Horn's `AfterDeath` callback still runs for the combat-ending enemy and
flashes the relic, but `PlayerCmd.GainEnergy` and `CardPileCmd.Draw` suppress
their outcomes once combat is over or ending. Exclude that callback before
incrementing activations or arming resource-attribution windows; otherwise
activation count measures attempted callbacks while the other rows measure
observed outcomes.

Mr. Struggles follows the same owner-specific turn-start pattern, but its
damage amount is the current turn number, so it uses the multi-target
`CreatureCmd.Damage` overload with a decimal amount plus `ValueProp` rather
than the `DamageVar` overload used by Mercury Hourglass and Festive Popper.

Pen Nib is detected at its `ModifyDamageMultiplicative` hook when the relic
returns `2` for the actual `CardPlay`, but the amount recorded comes from the
raw per-hit value passed into `CreatureCmd.Damage` before hook modifiers run.
SpireLens labels this "base damage added" rather than deriving from final
damage, so effects such as Lethality or Vulnerable do not inflate the stat.

Pickup relics with multi-step health effects should record the observed result
across the full pickup callback when that is what the player experiences. Lee's
Waffle records current-HP gained across `AfterObtained`, covering both its
max-HP grant and the follow-up heal-to-full.

Lee's Waffle??? (`FakeLeesWaffle`) is mechanically different: its
`AfterObtained` callback attempts to heal ten percent of the owner's current
maximum HP and does not change maximum HP. Calculate that exact decimal attempt
before the callback, then finalize the shared relic-healing ledger after the
returned task completes so restored HP, full-HP overfill, and other prevention
remain distinct from normal Lee's Waffle's aggregate.

For simple max-HP pickup relics such as Strawberry, Pear, Mango, Mango???
(`FakeMango`), and Nutritious Oyster, snapshot the owner's max HP in an
`AfterObtained` prefix and observe it only after the returned task completes
successfully. This captures the actual gain, including caps or other runtime
changes, without counting relic restoration. Mango and Mango??? use separate
relic aggregates even though they share the same presentation.

Scroll Boxes generates two three-card bundles, awaits one bundle selection,
then adds each selected card to the permanent deck. Snapshot physical deck
references around the full `AfterObtained` task and persist only new references
present after successful completion. This records the chosen bundle's observed
cards, including the three-Claw exception and any final deck-add replacements,
without treating generated-but-unchosen bundle cards as grants.

Lead Paperweight creates two concrete Colorless options inside its
owner-specific `AfterObtained` callback, then awaits the shared
`CardSelectCmd.FromChooseACardScreen` command with a real skip option. Arm the
choice from that relic callback, capture the exact option list at the selector,
and wait for the full pickup callback before resolving the outcome against new
physical permanent-deck references. Mark an option taken only when the chosen
card actually produces a deck addition; preserve a null selection as a true
skip rather than treating selection intent as acquisition.

Instance numbers must be reclaimed, not just restored. `AdoptRunLocked` seeds
`_pendingRankRestores` from the saved `instance_numbers_by_def` snapshot, but that
snapshot only covers cards in the deck at the last save. Any deck card the queue
does not cover used to mint a fresh number, orphaning the stats already recorded
against that physical card — and because each Continue and each Core reload
repopulates the deck, the orphans accumulated into whole generations per
definition. An observed floor-49 run held 124 tracked instances for a 33-card
deck: starter Strikes as `#1-#4` (played, then orphaned), `#5-#8` (minted and
immediately superseded, zero activity), `#9-#11` (the live generation), and `#12`
removed, with 21% of the run's recorded plays sitting on numbers no live card was
bound to. `PruneGhostAggregates` does not clean these up: it skips any number at
or below the last saved `def_counters` value, which every accumulated generation
is. So before minting in `GetOrAssignNumber`, take the lowest number this run
already tracks for the definition that no live card is bound to, that is not
waiting in the snapshot queue, and that is not marked removed. Out of combat
only — combat-generated copies route through the same primitive, and letting an
ephemeral card claim a deck card's identity is the hazard the queue already
avoids by clearing at combat setup. This does not repair runs whose generations
already split; nothing in the data says which orphan belonged to which surviving
copy, so a retroactive merge would be invented attribution.

Choices Paradox generates five distinct combat cards from its owner's own
character card pool on the first player turn of a combat, applies Retain to each
one, and awaits the shared `CardSelectCmd.FromSimpleGrid` command with a
select-exactly-one prompt. Open a window in a prefix on the relic's own
`AfterPlayerTurnStart` override and close it in that method's finalizer. The
relic generates its options and enters the grid command synchronously before its
first await, so the window is still open when the grid is armed and has already
closed by the time the async callback hands its task back; every other caller of
that widely shared command stays unclaimed. Count the selection screen at the
grid call rather than at the relic callback: the relic's own zero-options
softlock guard returns before any grid exists, so a screen counted at the grid
was really offered, and the offered-rarity buckets can never disagree with the
screen count. `CardFactory.FilterForCombat` drops Basic, Ancient, and Event
cards, so Common, Uncommon, and Rare fully account for the options. The command
materializes its result list before returning, so awaiting the same task
alongside the relic reads the observed pick; do not infer the taken card from
the prompt's requested count of one, and do not assume the offered rarities
follow reward-screen weights — the options are a uniform draw over the distinct
eligible pool.

Yummy Cookie's pickup flow calls `CardModel.UpgradeInternal` both for its real
permanent-deck upgrades and while constructing non-deck copies of cards that
were already upgraded. A callback-wide UpgradeInternal window therefore
overcounts every upgraded card displayed during pickup. Require the exact
object passed to `UpgradeInternal` to be a current permanent-deck member; those
positive mutations are the observed Cookie outcomes.

Fragrant Mushroom's owner-specific `AfterObtained` callback first awaits its
unblockable, unpowered damage and then synchronously upgrades two eligible
permanent-deck cards. Snapshot the owner's current HP before the callback and
after its task completes so the relic reports the observed starting and
resulting HP instead of assuming all 15 listed damage landed. Keep the existing
callback-wide permanent-deck `UpgradeInternal` window for its upgraded-card
list.

Gnarled Hammer's `AfterObtained` awaits a deck selection, then synchronously
calls `CardCmd.Enchant` with Sharp on each returned physical deck card. Snapshot
the deck card references and their optional Sharp amounts before the callback,
then compare them after its task completes successfully. A transition to Sharp
or an increased Sharp amount is an observed enchantment; the selector's
three-card maximum is not itself evidence that three cards changed.

Tri-Boomerang uses the same `AfterObtained` lifecycle with Instinct. Snapshot
the permanent deck's exact card references and optional Instinct amounts, then
persist only cards whose Instinct amount actually changed. Keep each card's
stable SpireLens instance ID with its display name: later completed combat
plays arrive on clones, so canonical instance identity—not a name or card
definition—is what proves the played card was enchanted by Tri-Boomerang.
Count held combats at combat setup so zero-play combats remain in the
Instinct-card-play average.

Darkstone Periapt is owned by `DarkstonePeriapt.AfterCardChangedPiles`. Mirror
the relic's own final-pile condition (`card.Pile.Type == Deck`, same owner,
`CardType.Curse`), then record the actual max-HP delta after the async
`GainMaxHp` command resolves. Count the curse acquisition from that same
owner-specific match rather than from every generic curse card entry.

Lucky Fysh uses the same owner-specific `AfterCardChangedPiles` surface for
every same-owner card whose final pile is the permanent Deck. Wrap its returned
task, count the confirmed deck addition only after successful completion, and
measure the owner's completed gold-balance delta so gold modifiers or
prevention are reflected instead of assuming its base 15 gold.

Read the added card's `CardType` and the owner's `RunState.CurrentRoom` in the
prefix, before the await. The card type is what makes a Curse addition
countable, and the pre-await room is the room the addition actually happened
in — the awaited gold command can resolve after a room transition, so reading
`CurrentRoom` in the continuation would attribute the add to the wrong room.
Combat additions cover Monster, Elite, and Boss rooms, because card rewards
resolve while the player is still standing in the combat room. The observed
balance delta is split onto the same buckets, so each rate reports cards and
the gold they paid out over one shared denominator. The per-room gold totals
sum to at most the lifetime total, which also covers additions in rooms outside
the four buckets.

Denominators for the relic's per-room averages come from the shared resolved
`Hook.AfterRoomEntered` prefix, counted once per floor per room type while the
relic is held. That hook fires for the resolved room, so ? nodes land in the
bucket they actually became. It cannot see a room the player was already
standing in when the relic arrived, so the deck-addition path counts its own
room too; the once-per-floor marker keeps the two paths from double-counting
and keeps Continue and Core reloads idempotent.

Bowler Hat applies its 25% multiplier through the central
`PlayerCmd.GainGold` → `Hook.ModifyGoldGained` path without filtering the
source. It therefore affects normal Gold rewards (including stolen gold being
returned), events, cards, potions, relic grants, and any other positive grant
that uses `PlayerCmd.GainGold`; direct balance restoration/loading is outside
that hook. Snapshot the owner's balance and the command's unmodified amount,
then wait for the complete command. The observed bonus is the completed
integer balance gain minus the integer unmodified grant, clamped at zero. This
captures truncation and correctly records zero when Ectoplasm later prevents
the gain. For SpireLens, count an activation only when at least one bonus gold
actually reaches the balance; zero-benefit modifier calls stay out of both the
activation and average.

Maw Bank gains gold from its owner-specific `AfterRoomEntered` callback while
its saved `HasItemBeenBought` flag is false. Mirror the callback's BaseRoom
gate, then record the owner's completed gold-balance delta rather than its
listed 12-gold value. A shop skip is not known at shop entry: persist that
MerchantRoom's floor as an open visit, then resolve it at the next distinct
room entry. Count the skip only when `HasItemBeenBought` is still false; a
positive-gold purchase sets that game-owned flag and therefore resolves the
visit without a skip. Keeping the pending floor in `RunData` makes duplicate
same-room callbacks idempotent and preserves an open shop across Continue or
Core hot reload. For spending outside shops, reuse the established
`PlayerCmd.LoseGold` before/after balance observation: count only transactions
classified by the game as `GoldLossType.Spent`, while `HasItemBeenBought` is
still false and the owner's current `BaseRoom` is not a `MerchantRoom`.
Ordinary gold loss and shop purchases do not belong in that total.

Membership Card has no activation callback at all: it only overrides
`ModifyMerchantPrice`, and `MerchantEntry.Cost` re-runs the entire
`Hook.ModifyMerchantPrice` chain on every read — including the reads the shop
UI makes while merely drawing prices. Do not treat that override as a trigger.
The observed unit is a completed purchase, and the marginal saving is obtained
by reading the same entry's `Cost` twice inside the purchase prefix: once
normally, once with only this relic's own override short-circuited to return
`originalPrice`. Both reads run the rest of the chain, so The Courier's
overlapping 20% stays credited to The Courier and the game's own final
`(int)` truncation applies to both sides. Suppression must be armed for exactly
one nested read on the arming thread and released in a `finally`; a leaked flag
would silently remove the discount from live shop prices.

Take that snapshot before the purchase runs, not at
`Hook.AfterItemPurchased`. `MerchantEntry.OnTryPurchaseWrapper` calls
`RestockAfterPurchase` (which re-runs `CalcCost` for a *different* item) or
`ClearAfterPurchase` between charging the player and announcing the sale, so
`_cost` at hook time can already belong to the replacement stock whenever the
player also owns The Courier. `MerchantCardRemovalEntry` declares its own
cancelable `OnTryPurchaseWrapper` overload rather than calling the shared one,
so both wrappers need the prefix. `Hook.AfterItemPurchased` then supplies the
authoritative `goldSpent` and fires only on success; a free purchase
(`ignoreCost`) reports zero and must record no saving rather than the full
price.

The relic's two framing stats need no new hooks. Its shop path pays through
`PlayerCmd.LoseGold` before `RelicCmd.Obtain`, so the owner's balance at the
existing obtain observation is already the gold held after buying it — stamp it
once and never overwrite. Gold earned afterwards is every gain seen at the
established `RunTracker.RecordRunGoldGain` boundary while the relic is held;
ownership starts at the purchase, so held-period gains need no stored cutoff
floor and survive Continue and Core hot reload for free. Melted wax relics drop
out of `IterateHookListeners`, which makes both the discount measurement and
the held-period gain stop on their own.

Book of Five Rings also owns an `AfterCardChangedPiles` callback for every
same-owner card whose final pile is the permanent Deck. Its saved `CardsAdded`
counter advances on each callback and triggers healing whenever the post-add
counter is divisible by its five-card threshold. Mirror that exact transition
before the async callback starts so the shared relic-healing ledger is armed
before `CreatureCmd.Heal`; finalize after the callback task completes to retain
actual healing and the blocked remainder. Count outer `CardReward.OnSkipped`
calls separately while the tracked owner holds the book. For cards added per
floor, use inclusive floors held from the relic's pickup floor rather than all
floors in the run.

Chosen Cheese gains max HP from `AfterCombatEnd`, then the game heals the same
amount as part of `CreatureCmd.GainMaxHp`. Snapshot the owner's max HP before
the async relic callback and record only the actual max-HP delta after
successful completion. Its baseline max HP comes from the `RelicCmd.Obtain`
pickup boundary. Do not display or store a Chosen Cheese "resulting max HP":
other max-HP effects can interleave between its later gains, so the only durable
run-level facts are pickup-time starting max HP and total max HP gained. Because
the combat-end callback can complete around combat promotion, route the gained
amount to pending combat when it still exists and directly to the run otherwise.

Strike Dummy identifies eligible cards through the game's `CardTag.Strike`.
Its damage modifier can run per damage evaluation, so count Strike cards played
from finished card-play events while the relic is owned; use the modifier only
to confirm the eligibility rule. Base Strikes are `IsBasicStrikeOrDefend` cards
that also carry the Strike tag, while non-base Strike cards are every other
permanent deck card with that tag.

Oddly Smooth Stone's tracked input is a completed play of a card whose
`CardModel.GainsBlock` property is true while the relic is owned. That is the
game's explicit classification for cards that immediately gain Block through
their Dexterity-sensitive block value; it intentionally excludes delayed block
engines such as Shadowmeld. Count the finished play even if the resulting Block
is later modified or prevented, because this stat measures qualifying cards
played rather than Block actually gained.

Kunai, Kusarigama, Ornamental Fan, and Shuriken share the same repeatable
three-Attack counter shape: their owner-specific `AfterCardPlayed` callback
increments a turn-local counter and activates at every threshold multiple.
Count owner Attack plays at that callback, snapshot unused modulo charge from
`Hook.BeforeSideTurnEnd` before the relic resets, and observe each payoff at its
narrow outcome: power delta for Kunai/Shuriken, the resolved block-command
result for Ornamental Fan, and the resolved single-target damage result for
Kusarigama. Ornamental Fan's block command has a null `CardPlay`; recognize its
single resulting history entry as relic-owned, bypass the generic
current/recent-card fallback, and append a relic-source block chunk. The shared
FIFO-absorb/LIFO-waste ledger then credits effective and wasted block to
Ornamental Fan alone. Use every held player turn and combat for its block-rate
denominators, with a matching observation-era block numerator because its
lifetime block total predates those denominators. Ornamental Fan preserves
zero-charge turn ends as an explicit bucket in addition to the shared average
charge sample. Kusarigama only
activates when its threshold play can choose a hittable enemy; do not infer an
activation from the counter alone.

For the owned-relic tooltip, Kunai, Ornamental Fan, and Shuriken expose their
private cumulative `_attacksPlayedThisTurn` counters. Divide that value by the
live Cards threshold for exact activations this turn; read activations this
combat only from the current pending relic aggregate, never from the merged
lifetime aggregate.

Kunai and Shuriken deliberately share one three-Attack scaling presentation
and one persisted activation-rate window. Their owner callbacks still observe
different outcomes—Dexterity for Kunai and Strength for Shuriken—but both
increment the same per-relic `ThreeAttackScalingRateActivations`, held-turn,
and held-combat fields. Those denominators include periods with zero
activations and must remain paired with the matching observation-era numerator;
do not divide their older lifetime `Activations` totals by the newer rate
window.

Razor Tooth upgrades eligible Attack and Skill cards synchronously inside its
owner-specific `AfterCardPlayed` callback, after the finished card-play history
entry has already been emitted. Combat history has no upgrade entry for this
effect, and `CardCmd.Upgrade` only adds the game's run-history upgrade record for
cards in the permanent Deck pile. Snapshot the played card's
`CurrentUpgradeLevel` before and after Razor Tooth's callback and count only a
positive observed delta; `CardCmd.Upgrade` can still no-op while combat is
ending. Keep successfully upgraded cards in a combat-local, raw-reference set:
the same combat `CardModel` survives pile moves and later replay/draw events,
while canonicalizing would risk giving a distinct generated or copied card the
same credit. `CardPlayFinished` occurs before Razor Tooth's callback, so the
triggering play is not an upgraded-card play; later finished replay iterations
are. Count later successful draws only from `Hook.AfterCardDrawn`, and use held
player turns/combats (including zero-result ones) as rate denominators.

Drain Power awaits its attack, then synchronously calls `CardCmd.Upgrade` on
random upgradable cards in its owner's Discard pile before its `OnPlay`
finishes. The existing `CardModel.UpgradeInternal` postfix is therefore the
observed upgrade boundary, and `FindCurrentlyResolvingCardPlay` still resolves
the physical Drain Power source there. Retain each upgraded combat card by raw
reference and associate it with that source instance; later completed plays of
the exact same combat card count for the source, while copies do not. Use every
turn and combat where that physical Drain Power was in the permanent deck as
the zero-inclusive average denominators.

Miniature Cannon checks the live attack source's `IsUpgraded` property when
modifying powered Attack damage. Its play and hit attribution must inspect that
same combat card rather than its canonical deck version. Permanent upgrades are
copied onto combat cards, while combat-only upgrades such as Drain Power exist
only on the raw combat card. Its deck composition rows inspect the permanent
deck, while its combat composition rows inspect every live card across the
owner's hand, draw, discard, exhaust, and play piles. Use each card's live
`IsUpgraded` value for both splits so generated cards and temporary combat
upgrades are represented exactly as Miniature Cannon sees them.

Ember Tea's `AfterRoomEntered` callback runs after `CombatSetUp`, applies
Strength for a combat while `CombatsLeft` is positive, and immediately
decrements the saved counter. The fifth activation therefore spends the last
charge and leaves `CombatsLeft == 0` before turn one even though Tea is active
for that combat. Wrap the callback and retain a combat-local active-player
marker only after the awaited callback successfully consumes a charge. Count
finished owner Attack plays and each observed enemy damage entry from those
Attacks while that marker exists; multi-hit and multi-target Attacks contribute
one hit per resolved entry. Active turn/combat denominators include marked
periods with zero attacks or hits.

Red Skull's private mutable `StrengthApplied` flag is its authoritative active
state; the visible status and HP threshold merely explain why that state
changes. Observe it after the established async `Hook.AfterCurrentHpChanged`
dispatch fully completes, and again at each player-turn start, to count every
distinct active turn/combat including zero-attack periods. Capture whether an
Attack was active at `CardPlayStartedEntry` and commit that play only when its
matching finished entry arrives. Count hits from observed enemy damage entries
only while `StrengthApplied` is currently true, so multi-hit/multi-target
Attacks count each resolved hit and mid-resolution threshold changes are
respected.

Brilliant Scarf increments its per-turn card counter from `AfterCardPlayed`,
after `CardPlayFinished` has already entered combat history. Its actual cost
discounts happen through `TryModifyEnergyCostInCombatLate` and `TryModifyStarCost`
when that counter is one short of the configured threshold. Cost modifiers are
queried repeatedly for UI/playability, so count the offer from the counter
transition and use the modifier only to measure energy saved by the card that
later consumes the offer. Count every distinct held player turn from
`Hook.AfterPlayerTurnStart`, and reconcile the current turn at combat
promotion, so average energy saved per turn includes zero-offer and
zero-saving turns as well as combat-ending turns. Persist a matching
observation-era saved-energy numerator for this new denominator; historic run
files can already contain total saved energy but cannot reconstruct their
earlier held turns, so mixing that total with only newly observed turns would
inflate the average after an upgrade or hot reload.

Pendulum advances its persistent turn counter from its owner-specific
`BeforeHandDraw` callback and contributes its activation through
`ModifyHandDraw`; it can activate multiple times in a long combat—or not at all
in a short one. Compare the completed hand draw with the same request minus
Pendulum's marginal modifier so Innate and hand-limit clamping do not create
false credit. Count every combat where the relic was held as the per-combat
denominator rather than using activations. Its public
`TurnsSeen` counter is always modulo three, so snapshot that live 0/1/2 value
once during combat promotion, before the pending aggregate is merged into the
run, for combat-end charge buckets and averages.

The owner's `PlayerCombatState.TurnNumber` is already the turn being drawn for
when the hand-draw modifier chain runs — `CombatManager.SwitchSides` increments
it before the upcoming player side, and `SetupPlayerTurn` itself branches on
`TurnNumber == 1` for first-turn Innate handling immediately after
`Hook.ModifyHandDraw`. Read it at the positive `ModifyHandDraw` delta for the
average-activation-turn sample; `Hook.ModifyHandDraw` has that single call site,
so one turn cannot yield two samples. Samples count activations rather than
combats, so a long combat contributes each of its turn-3/6/9 activations and a
combat too short to activate contributes nothing instead of pulling the average
toward zero.

Pocketwatch increments its private current-turn counter from owner
`AfterCardPlayed` callbacks and transfers that value to its private
previous-turn counter during `BeforeSideTurnStart`. Its hand-draw modifier is
the authoritative activation signal: count an activation only when
`ModifyHandDraw` returns a positive bonus, and read that same callback's
previous-turn counter for the activation-value sample. Count every held player
turn at `Hook.BeforeSideTurnEnd`, using finished owner card plays as the
turn-counter observation, then reconcile the still-current turn during combat
promotion because a combat-ending play can bypass the turn-end hook. A turn
ending above `CardThreshold` is a missed activation; a qualifying final combat
turn is neither an activation nor a miss because no later hand draw occurred.
Keep the new turn/combat denominators and their numerators observation-era:
historic additional-draw totals cannot reconstruct the earlier card-count
distribution.

Stone Cracker selects and upgrades combat-card instances from the owner's draw
pile inside `AfterRoomEntered`; it does not upgrade the permanent deck cards.
All upgrades occur synchronously before that async callback's first await, so
snapshot draw-pile card references and upgrade levels in a prefix, compare them
in the immediate postfix, and retain the positively changed raw references for
later finished-play attribution during that combat.

Mummified Hand resolves entirely inside its `AfterCardPlayed` callback despite
returning `Task.CompletedTask`: after an owner Power play, it selects one card
already in hand and calls that card's `SetToFreeThisTurn`. Observe the selected
card and its effective energy cost immediately around that exact call. The
triggering `CardPlay.Resources.EnergyValue` is the Power's play-time cost, while
`EnergySpent` is the distinct numerator for spend-to-discounted-cost ratios.
Read both type and rarity from that exact selected card; a trigger with no card
left to discount belongs in activation rates but not in either recipient
breakdown.

Splash follows Discovery's choose-card flow: after a non-null Attack selection,
it synchronously calls `SetToFreeThisTurn` before awaiting
`CardPileCmd.AddGeneratedCardToCombat`. Observe rarity and effective energy
cost immediately around that exact mutation. A skipped choice never reaches
the boundary, and the resolving Splash play distinguishes it from Discovery,
Crossbow, and other callers of the shared cost method.

Crossbow selects one unlocked Attack, calls `SetToFreeThisTurn`, and starts
`CardPileCmd.AddGeneratedCardsToCombat` before its owner-specific
`AfterSideTurnStart` callback reaches the first await. A thread-local scope may
therefore capture the effective before/after energy cost and the exact batch
attempt synchronously, but gain and rarity must wait for the returned
`CardPileAddResult`. Count held player turns and combats independently so a
failed or zero-discount generation still contributes to the rate denominator.

Unrelenting applies one stack of the shared `FreeAttackPower`; multiple
physical copies therefore lose distinct ownership once their stacks merge.
Persist downstream charge use in a power-ID-keyed aggregate and project that
shared record from every Unrelenting tooltip. The power's
`TryModifyEnergyCostInCombatLate` method can be queried repeatedly for UI and
playability, so its marginal cost reduction is only a snapshot. Confirm one
use only after the exact Attack reaches `BeforeCardPlayed` and the awaited
`PowerCmd.Decrement` actually lowers the stack. Naturally free and auto-played
Attacks still consume a charge; count those uses with zero energy saved.

Pael's Claw applies Goopy with amount 1 to every eligible permanent deck card
in its synchronous `AfterObtained` callback. That initial amount is the
enchantment baseline: Goopy's block bonus is `Amount - 1`. A finished Goopy
card play is observable from `CardPlayFinishedEntry`, but Goopy earns its
permanent increment later in `Goopy.AfterCardPlayed`, where both the combat
copy and `DeckVersion` amounts are incremented. The game skips the entire
`Hook.AfterCardPlayed` dispatch once combat has ended, so count finished Goopy
plays and observed earned enhancements separately rather than assuming they
are identical.

Stone Humidifier applies its repeatable max-HP gain from the owner-specific
async `AfterRestSiteHeal(Player, bool)` callback. Snapshot the owner's max HP
before the callback and only record the resulting max HP after its returned
task completes successfully; the callback awaits `CreatureCmd.GainMaxHp`, so
the post-task value is the observed result after game modifiers or prevention.

Sturdy Clamp prevents the normal player block clear in `ShouldClearBlock`, and
its owner-specific `AfterPreventingBlockClear(AbstractModel, Creature)` callback
runs from `Creature.AfterTurnStart` on every player turn after turn 1, including
when block is zero. Capture the pre-callback block, then wait for its task before
reading retained block because the relic asynchronously removes the amount over
10. The pre-callback amount above 10 is the excess; the post-task block is the
observed retained result.

Ripple Basin checks its owner's finished card-play history in
`BeforeSideTurnEnd` and grants block only when no Attack was played that turn.
Use that exact owner-specific callback for activation and observed-block
attribution, but do not use activations as a rate denominator. Count every
distinct held player turn from `Hook.AfterPlayerTurnStart` and every combat
where the relic was held, including periods where an Attack prevented the
block. Reconcile the current turn at combat promotion so combat-ending paths do
not omit the denominator.

Reptile Trinket's owner-specific `AfterPotionUsed` callback is the activation
source of truth: it has already confirmed the potion owner and active combat,
and its Strength dynamic variable supplies the amount actually requested.
Count every held player turn at `Hook.AfterPlayerTurnStart` and every held
combat in the combat baseline so zero-activation periods remain in both
averages. Track the current turn live: activation two places that turn in the
exactly-two bucket, while activation three moves the same turn into the
more-than-two bucket. Later activations do not change its bucket. Because the
bucket transition is complete when the activation occurs, no turn-end callback
is needed and a combat-ending activation cannot be lost; the combat promotion
boundary only reconciles a final held turn that missed the normal start hook.

Toasty Mittens selects and exhausts one card from the populated hand and then
applies Strength inside its async `AfterPlayerTurnStart` callback. Keep an async-flow-local scope
around that callback. `CardCmd.Exhaust` writes `CardExhaustedEntry` only after
the card reaches the exhaust pile and before dispatching nested exhaust hooks,
so the first matching owner-card entry in the scope is the relic's confirmed
direct exhaust. Retain the last matching owner-to-owner `StrengthPower`
`PowerReceivedEntry`: Toasty Mittens applies its own Strength as the callback's
final operation, after the exhaust and its nested hooks. Commit both observed
outcomes only after the callback task succeeds. Count every combat where the
relic was held as the zero-inclusive per-combat denominator.

Beating Remnant caps post-Osty HP loss in its owner-specific
`ModifyHpLostAfterOsty` modifier. The positive difference between that method's
input and output is the HP loss prevented by Beating Remnant itself; do not
credit it with prevention performed elsewhere in the damage pipeline. Its
`BeforeSideTurnStart` callback resets the internal received-damage counter when
the participants include its owner, making that the matching held-turn
boundary. Count held combats at setup so both averages include zero-prevention
periods.

Tungsten Rod uses the same owner-specific `ModifyHpLostAfterOsty` observation:
its positive input/output delta is the exact HP loss prevented by the rod.
Direct Curse and Status card sources are authoritative. Normal cards owned by
the target player and target-owned Buff power callbacks are self-inflicted;
enemy dealers and target-owned Debuff power callbacks are enemy-sourced. The
game sometimes passes the player or null as dealer for Debuff ticks, so keep a
narrow async-local source frame around the current player-damaging power
callbacks rather than classifying those from dealer alone. Unidentified sources
belong in the total but not a guessed source bucket. Use every held player turn
and combat as the zero-inclusive average denominators.

Ruined Helmet doubles the first positive Strength amount its owner receives in
each combat through `TryModifyPowerAmountReceived`. Capture its exact local
contribution as `modifiedAmount - amount` at that callback, but do not commit it
there: a later power-application guard can still cancel the effect. Commit the
staged bonus only from the relic's matching
`AfterModifyingPowerAmountReceived` callback, which `PowerCmd` invokes after the
Strength was actually applied. Count that confirmed application as the
activation denominator for bonus Strength per activation. Use every combat
where the relic was held, including zero-trigger combats, as the separate
per-combat denominator.

Daughter of the Wind's owner-specific `AfterCardPlayed` callback checks for an
owner Attack, then issues and awaits exactly one
`CreatureCmd.GainBlock(Creature, BlockVar, CardPlay, bool)` command. Arm only
that immediate command from the relic callback and record the command task's
returned post-modifier block amount. Count every player turn and combat where
the relic was held, including zero-Attack periods, as the average denominators.

Art of War's owner-specific `AfterEnergyReset(Player)` callback runs at each
owner energy reset, including turn one, but only calls `PlayerCmd.GainEnergy`
after turn one when no Attack was played on the preceding turn. Snapshot the
owner's energy pool around that callback and record the positive delta only
after its task completes successfully. Count every callback turn and every
combat where the relic was held, including non-trigger periods, as the average
denominators. For live tooltip values, the pending relic aggregate is the
current-combat energy numerator and turn denominator. Reset a separate
combat-local, per-player turn bucket at each newly observed callback turn, then
add the same observed positive delta to that bucket.

Pumpkin Candle contributes its max-energy dynamic value while its saved
`KindleCount` is positive. Count that contribution at the established
`Hook.AfterEnergyReset` boundary rather than from `ModifyMaxEnergy`, which can
be queried without granting a turn's energy. Snapshot `KindleCount` at
`CombatSetUp`, including zero-charge held combats. Initial pickup calls
`Rekindle` to seed five charges, so count user rekindles from a selected
`KindleRestSiteOption` at the established local rest-site exit boundary; do not
count the pickup call as a rekindle.

Cracked Core and Infused Core channel their starting Lightning orbs from their
owner-specific turn-one callbacks: `CrackedCore.BeforeSideTurnStart` and
`InfusedCore.AfterSideTurnStart`, respectively. Snapshot the owner's orb queue
around the completed callback and retain every exact newly added mutable
`LightningOrb` reference with the source relic id for the combat. Count each
successfully completed `LightningOrb.Passive` and `LightningOrb.Evoke` call
carrying one of those references; multi-evoke effects therefore count every
actual evoke. The gameplay non-evoke removal path is
`OrbQueue.RemoveCapacity`, currently used by Bulk Up, so compare raw queue
references around that method for fizzles. Normal combat cleanup uses
`OrbQueue.Clear` and must not count as a fizzle. For damage, scope Lightning's
private `ApplyLightningDamage` call to that exact tracked orb and observe the
returned `DamageResult` values from its five-parameter `CreatureCmd.Damage`
command. This preserves the actual attempted, effective, blocked, overkill,
kill, and target outcomes under the correct source relic without claiming
damage from other Lightning orbs owned at the same time. Infused Core's native
Lightning value modifier is naturally reflected in those observed results.

Symbiotic Virus follows the same exact-reference lifecycle with its
owner-specific `AfterSideTurnStart` callback and the newly queued mutable
`DarkOrb`. Keep its tracked-orb set and persisted counters separate from
Cracked Core's: both relics can be owned at once, and neither should claim the
other relic's orb. Count completed `DarkOrb.Passive` and `DarkOrb.Evoke` calls,
and route `OrbQueue.RemoveCapacity` removals through both exact-reference sets
so only the matching relic records a fizzle.

Gold-Plated Cables contributes through the global
`Hook.ModifyOrbPassiveTriggerCount` chain. That hook's returned
`modifyingModels` list is the authoritative confirmation that the relic
actually increased the trigger count. Observe that list when it is passed to
`Hook.AfterModifyingOrbPassiveTriggerCount`; its `OrbModel` argument is the
exact first orb that received the additional passive trigger. When the queue
is empty, neither the relic modifier nor its follow-up callback runs; count
that missed opportunity separately at the tracked owner's exact
`OrbQueue.BeforeTurnEnd` pass, not from generic orb traffic.

Shovel adds `DigRestSiteOption` from `TryModifyRestSiteOptions`; the relic
itself does not receive the obtained relic payload. Patch
`DigRestSiteOption.OnSelect`, snapshot the owner's relic inventory before the
async selection, and after a successful result record the newly present relic
instances and their actual `RelicRarity`. To count missed Dig opportunities,
inspect `RestSiteSynchronizer.BeforeLocalRestSiteExited`: at that point the
local option list and chosen-option index still reveal whether a Dig option was
available and whether the selected option was anything other than Dig.

Tiny Mailbox appends two unpopulated `PotionReward` objects from its
owner-specific `TryModifyRestSiteHealRewards` callback. Bind those exact
objects from the callback's before/after reward-list delta, then read their
populated `Potion` when each reward is selected or skipped. Selection success
is the source of truth for potions taken; an attempted selection can fail while
the potion belt is full. Fruit Juice is identified from the concrete potion
model and intentionally overlaps the Rare offer bucket. Actual campfires where
Rest was available but another option was chosen can share Shovel's
`RestSiteSynchronizer.BeforeLocalRestSiteExited` observation point.

Girya's saved `TimesLifted` counter is incremented inside
`LiftRestSiteOption.OnSelect`, before the rest site exits. Observe a selected
`LiftRestSiteOption` at `RestSiteSynchronizer.BeforeLocalRestSiteExited`; the
counter is then the completed result and can make the floor-distance update
idempotent across reloads. Sample that counter once at the successful relic
obtain boundary and once for each later `RunManager.EnterMapPointInternal`
destination floor. Sampling before the destination room acts means a Lift at a
rest site begins weighting the next floor, while count zero still weights the
acquisition and pre-Lift floors. Girya applies Strength only from its awaited
`AfterRoomEntered` callback for combat rooms with `TimesLifted > 0`; measure the
owner's actual Strength before and after that callback rather than assuming the
saved count was fully applied.

Fresnel Lens applies Nimble from its owner-specific
`TryModifyCardRewardOptionsLate` callback. Count the final option only when its
`CardCreationResult` is still Nimble and names Fresnel Lens in
`ModifyingRelics`; this distinguishes relic-caused Nimble from an option that
was already enchanted. `CardReward.OnSelect` opens the actual selection and
removes a card from its option list only after `CardPileCmd.Add(..., Deck)`
succeeds, so an initial-versus-remaining option snapshot measures cards taken.
The card-screen Skip returns to the outer rewards page without consuming the
reward, so keep the same reference-keyed pending snapshot across reopenings and
finalize it only on a completed selection or `CardReward.OnSkipped`. A reroll
reuses that same reward/screen and must refresh, not increment, its snapshot.
Drowning Beacon applies its max-HP loss before obtaining Fresnel Lens; wrap the
full async `DrowningBeacon.ClimbOption` to preserve the observed before/after
max HP because a relic pickup hook begins too late to recover the baseline.

Wing Charm uses the same native card-reward provenance: its
`TryModifyCardRewardOptionsLate` callback applies Swift to one eligible
`CardCreationResult` and records Wing Charm in `ModifyingRelics`. Snapshot that
exact result object and its final rarity when the selection opens. A result
removed from `CardReward._cards` was successfully taken; a terminal selection
or outer skip with the result remaining is not taken. A reroll visibly offered
the old Swift option, so resolve that option before `_cards` is cleared, then
register the newly generated Swift option after repopulation.

Lasting Candy's owner-specific `TryModifyCardRewardOptions` appends one Power
as a new `CardCreationResult` and records itself in that result's
`ModifyingRelics`. Snapshot that exact result after `CardReward.Populate` (or
when an already-populated reward opens after a Core reload). A successful deck
add removes the result object, so its absence at terminal resolution is an
observed taken outcome; choosing another card, an outer skip, a terminal
alternative, or a Driftwood reroll leaves it present and therefore rejects it.
Resolve the old Candy option before reroll clears `_cards`, then register the
new Candy-tagged result after repopulation. Inner card-screen Skip is not
terminal and must preserve the pending snapshot.

Prismatic Gem and Dingy Rug are pool-shape relics, not per-option relics, so
neither appears in any `CardCreationResult.ModifyingRelics`. Their offered side
comes from `Hook.TryModifyCardRewardOptions`, which reports the final visible
option list for every reward generated while the relic is held — deliberately
meta, because another modifier may have contributed the same option. Read each
option's pool from `CardModel.Pool` (`IsColorless` first, then the pool id with
its `_CARD_POOL` suffix stripped) so the key survives display-name changes. The
taken side reuses the same `CardReward._cards` removal truth as Wing Charm:
snapshot every visible option with its pool when the selection opens, and treat
a result missing at terminal resolution as taken. Because both relics observe
the whole option list rather than one tagged option, a player holding both is
credited on both, and the two counters are independent — an offer recorded at
generation time can never be assumed taken, and old runs that predate the taken
counter read as zero taken rather than as zero offers.

White Star and Prayer Wheel own a whole extra `CardReward` rather than one
option inside a shared one, so neither needs per-result identity: a bucketed
option count snapshotted when the selection opens and diffed against the same
count at terminal resolution is enough. Bucket White Star by card type among
Rares and Prayer Wheel by rarity. The screen-level declined/rejected counters
are the same observation read as a boolean — no bucket dropped means nothing was
taken — so they must be derived from the same snapshot diff rather than from a
second, independently timed check that could disagree with the per-card counts.

Silver Crucible's first, second, and third reward numbers are generation order,
not click order. `CardReward.Populate` runs before the outer rewards page is
shown, and `SilverCrucible.AfterModifyingCardRewardOptions` synchronously
increments its saved `TimesUsed`; snapshot that before/after transition to bind
the final ordered `_cards` options to the exact one-based use. Multiple rewards
such as Prayer Wheel can consume consecutive uses before either is opened. An
ordinary Driftwood reroll clears the old `CardCreationResult` objects and calls
`Populate` again with `IsCardReward`, so finalize the old set as all not taken
and register the rerolled set as the next Silver use. Compare result-object
references, not card ids or `CardModel` references: `ModifyCard` can replace the
displayed card while preserving its result object, and a successful deck add
removes that result object from `_cards`. Inner card-screen Skip remains
non-terminal; completed selection, outer `OnSkipped`, and pre-clear reroll are
the terminal outcome boundaries.
Persist the generated offer immediately as unresolved, then upsert its final
taken flags at a terminal boundary. On Core reload or Continue, rebind current
`CardReward` objects to same-floor unresolved screens by ordered card
id signature. Continued reward sets may regenerate different cards
because they serialize creation options rather than `_cards`; allow generation-
order fallback only inside that one `RewardsSet.GenerateWithoutOffering` batch,
never on an arbitrary later reward. Otherwise the already-advanced `TimesUsed`
counter makes those ordinals impossible to recover after the in-memory
reference map is cleared.
`CardReward.OnRelicObtained` can apply a newly obtained Silver Crucible to an
already-populated reward, but the game then calls `AfterModifyingRewards`
rather than `AfterModifyingCardRewardOptions`, so that free modification does
not consume `TimesUsed`. Keep it outside the numbered three-use ledger unless
the game changes its own counter behavior.

Orrery constructs five distinct `CardReward` objects in creation order, then
passes that exact list to `RewardsCmd.OfferCustom` before its first await.
Register those object references inside a narrow `Orrery.AfterObtained`
source window rather than inferring Orrery from generic `CardCreationSource.Other`
rewards. A terminal outer `CardReward.OnSkipped` is an observed skip; a
successful `CardReward.OnSelect` can be resolved to the physical cards newly
present in the deck; and terminal reward alternatives should be wrapped at
`CardRewardAlternative.Generate` so their exact option id remains available.
Pael's Wing uses the `SACRIFICE` alternative. Inner card-screen Skip and
Driftwood reroll do not consume the reward and must preserve its original
Orrery number.

Prayer Wheel appends one dedicated `CardReward` from its owner-specific
`TryModifyRewards` callback after a normal monster combat. Bind only the
appended reward reference so the ordinary card reward on the same page never
enters Prayer Wheel's totals. Count every populated option set, including
Driftwood rerolls, by the final cards' observed rarities. A completed selection
removes its obtained card from the reward, so compare before/after rarity
counts to classify the actual Common, Uncommon, or Rare card taken. A completed
selection that removes no card and an outer `CardReward.OnSkipped` are terminal
rejections; the inner card-screen Skip is not terminal and must preserve the
same pending reward.

## Generated And Supplemental Cards

Not every visible card should become a permanent per-instance deck card.

Patterns already in use:

- Stable deck cards get normal instance ids.
- Removed deck cards keep stats and render in the separate not-in-deck view.
- Combat-generated cards can get per-observed identities if they are actually played/tracked.
- Some generated cards are better represented as pooled meta-card summaries.

Examples:

- Shiv data is pooled under a synthetic Shiv meta-card once a Shiv has been generated.
- Soul data is pooled under a synthetic Soul meta-card once a Soul has been generated.
- A Core hot reload during combat loses the ordinary pending-combat buffer, but
  the game's live `CombatHistory` remains intact. On resume, SpireLens narrowly
  rebuilds directly observable Soul usage (completed plays, draws, discards,
  exhausts, paid resources, and cards actually drawn during Soul resolution)
  into the pooled Soul aggregate. This does not make general mid-combat restore
  supported; source windows and outcomes absent from history remain lost.
- Sovereign Blade gets a pooled meta-card once forged/generated behavior makes it relevant.
- Each Status definition that reaches a combat pile gets one pooled meta-card
  whose tooltip merges every observed instance of that Status.

The deck screen is either in normal mode or not-in-deck mode. Normal mode
contains only the permanent deck. Not-in-deck mode contains removed physical
cards plus the supported meta-card registry and must not retain any current
deck cards. Normally a registry entry appears only after its generated-card
event or pooled aggregate proves it appeared this run. The show-all option
constructs every supported registry card even without data, allowing the
zero-value tooltip to document what SpireLens can track. Use pooled summaries
when a card does not meaningfully exist as a stable deck resident and per-copy
identities would mislead the user.

Status encounter availability is recorded at the established
`Hook.AfterCardGeneratedForCombat` boundary, after the generated card has
reached its final combat pile. The default not-in-deck view therefore includes
only Status definitions actually encountered during the run. Its show-all
option enumerates `ModelDb.AllCards` for every `CardType.Status`, while the
tooltip pools normal per-instance aggregates by card definition.

Recurring Power-card outcomes use a second kind of pooled meta-card. One
synthetic card per supported power family reuses the source Power card's art
and name, carries an explicit meta-power badge, and is the canonical home for
the complete shared record. It appears after that card definition is actually
played; the show-all option also exposes supported zero-value entries.
Physical copies project only a compact shared summary.

Keep these cohorts and denominators distinct:

- `PowerCardsPlayed` counts every completed play of the card definition,
  including replays and generated copies.
- `GeneratedPowerCardsPlayed` is a subset: completed plays whose canonical
  card is not a member of the permanent deck.
- `SuccessfulApplications` counts positive observed applications of the
  matching shared power. Do not infer this from the power's `Amount`; one card
  play can add several Amount units.
- `/ turn` uses every player turn in a combat where at least one permanent
  copy of that card definition was present at combat setup. Multiple copies do
  not multiply this denominator. A generated-only combat is excluded.
- `/ active turn` uses one unit for each turn where the shared power is live,
  regardless of its stack/application count.
- `/ active application-turn` uses one unit per successfully applied Power
  card per turn it remains active. This measures output per played
  application without confusing a card's dynamic Amount with card count.

Generated applications contribute to active and application-turn metrics. If
a permanent copy also made the combat deck-eligible, all family output in that
combat contributes to the `/ turn` deck metric; do not claim an exact marginal
split that the shared game power no longer preserves.

When adding these denominators to an existing lifetime stat, also add a
matching observation-era numerator. Older run files can contain the lifetime
total without any historic denominator samples; dividing the old total by a
new denominator would manufacture a false rate.

Entropy's shared power owns its later transformations. Keep a narrow window
around `EntropyPower.AfterPlayerTurnStart`, then observe the already-established
`CardCmd.Transform(IEnumerable<CardTransformation>, Rng, CardPreviewStyle)`
result. The successful result's replacement card is authoritative for rarity.
Snapshot whether the original card has the Queen's `Bound` affliction before
the transform removes that original; count a broken Chain of Binding only when
that same transform succeeds. Count the combat when Entropy becomes active,
including an active combat that produces no replacement cards.

Enemy status-card pollution uses a source-window plus observed-result pattern:

- Patch the exact enemy-owned move or power trigger that creates the status
  card. Examples include `HauntedShip.HauntMove` and Entomancer's
  `PersonalHivePower.AfterDamageReceived` callback.
- While that owner-specific window is active, observe
  `CardPileCmd.AddGeneratedCardsToCombat` results.
- Count only successful `CardPileAddResult` entries whose `cardAdded.Type` is
  `CardType.Status`.
- Attribute the result to the enemy definition, not to a card or relic. Split
  by destination pile and by status card id so enemy hovers can answer what the
  enemy actually added.

Enemy damage dealt to the player can be observed from `DamageReceivedEntry`
when `Receiver.IsPlayer` and `Dealer.Monster` is present. Attribute this to the
enemy definition, not a card. Use `BlockedDamage + UnblockedDamage` as attempted
damage, `BlockedDamage` as blocked, and `UnblockedDamage` as dealt/effective.

## UI Timing And Tooltip Surfaces

Card stats are exposed through Godot UI patches, not through game combat state alone.

Important surfaces:

- Adding text to a card face belongs in `CardModel.GetDescriptionForPile`, not
  in the card's Godot nodes. `NCard.UpdateVisuals` asks the model for a string
  and then does
  `_descriptionLabel.SetTextAutoSize("[center]" + text + "[/center]")`, so a
  postfix that appends to the returned string inherits the game's own
  centring, font and auto-shrink, and is recomputed on every render.
  `MegaRichTextLabel` auto-sizes between font sizes 8 and 100, so a longer
  card simply shrinks.

  Do not reach the same effect through `NCardHolder.SetCard` /
  `ReassignToCard` / `Clear`. That is the base class for every card surface
  including the combat hand, the holder is mid-construction when `SetCard`
  fires — not yet parented, and `NGridCardHolder.CardModel` still reports the
  PREVIOUS card until `UpdateCardModel` runs — and the grid recycles holders
  while scrolling without going through any of the three. Working around that
  means deferring a frame and then polling, and a deferred callback routinely
  lands after its holder was freed. `GodotObject.IsInstanceValid` THROWS
  `ObjectDisposedException` on a disposed wrapper rather than returning false,
  so that exception escapes into whatever the game was doing — in practice,
  cutting a five-card draw down to two. The card grid's holders are also
  centred local visuals with size (0,0), so anchoring a child to them yields a
  negative width and an invisible node.

- Run-history `MapPointHistoryEntry.PlayerStats` already stores exact per-player
  `rest_site_choices`. Campfire summaries should read that list in map-point
  order, advance the displayed floor across act boundaries exactly as
  `NMapPointHistory.LoadHistory` does, and refresh when `NRunHistory.SelectPlayer`
  changes the selected player. Pair each choice with the selected player's
  concrete map-point outcomes (`hp_healed`, `upgraded_cards`, picked relics,
  card gains/removals/transformations, and Max HP changes) rather than stopping
  at the action label. Fixed relic actions without a separate saved delta may
  describe their canonical result: Lift adds one Girya Strength level and
  Kindle adds five Pumpkin Candle charges. Do not reconstruct campfire choices
  from those side effects, and do not add parallel SpireLens persistence for
  data the game history already owns. Mend's history is lossy: healing is saved
  on the recipient without the source or a per-Mend amount, so it cannot be
  attributed exactly from a completed run file. Rest-site `hp_healed` also
  combines the selected campfire action with Eternal Feather's earlier
  room-entry healing. Preserve Eternal Feather's owner-specific observed
  restored amount per floor so the campfire tooltip can subtract that exact
  share and render it on its own line; older runs without those records retain
  the combined native value.
- `ViewStatsInjectorPatch` hooks `NCardsViewScreen.ConnectSignals`, gates to `NDeckViewScreen`, persists preferences, and reinjects its menu shortcut on hot reload if the deck view is already open. The master on/off control gates every SpireLens stats surface, while separate default-off controls gate card stats and monster hover stats. Gate `NativeStatsHoverTipFactory` before aggregate lookup or tooltip construction when the relevant stats option is off. The Card Stats control is presentation-only and must not be wired to `DisableCardStatsDuringCombat`, which suppresses attribution itself.
- `StatsVisibilityHotkeyPatch` postfixes the stable Loader input node so hot-reloaded Core code can handle both keyboard and controller events. A standalone Left Shift tap and raw Right Stick (R3) press share the persisted master toggle and the same focus/overlay/transition/rebind guards. Shift chords are rejected whether the other modifier is pressed before or after Shift, covering shortcuts such as Windows+Shift+S. Left Stick press is the game's Peek action; R3 is absent from the shipped and saved controller action maps. Native Steam Input layouts must expose R3 as a virtual joypad button for the raw event to reach the mod.
- `NCardsViewScreen.ConnectSignals` calls its controller-state update before the SpireLens postfix. Controller mode hides the built-in `%Upgrades` tickbox, so clones of that subtree inherit `Visible=false` unless SpireLens explicitly restores visibility. Any injected deck controls cloned from View Upgrades must set their own visibility rather than inherit the source's controller-specific state.
- `CardHoverTooltipPatch` builds compact/full card titles and BBCode bodies; it
  does not create or remove UI nodes.
- `NativeHoverTipAugmentationPatch` prefixes the game's nontrivial
  `NHoverTipSet.CreateAndShow(Control, IEnumerable<IHoverTip>,
  HoverTipAlignment)` overload and appends at most one SpireLens `HoverTip` to
  that owner’s native sequence. Owner-specific builders cover card holders,
  owned relics, enemies, visible compendium relics, and run-history cards and
  relics. Its postfix styles only the final native text control created for
  that appended tip: the SpireLens blue background tint and top-right brand
  return without creating a parallel panel or retaining the control.
- Card/relic pinning does not override ordinary unfocus or
  `NHoverTipSet.Remove`. A right-clicked card or relic receives a dedicated
  surrogate `Control` owner for a second native tooltip set; ordinary unfocus
  removes only the transient source-owned set. While pinned, attempts to create
  another transient set for that same source are suppressed. The surrogate,
  any mouse-input signal, and the lock badge are all removed during Core
  shutdown so hot reload cannot leave callbacks from an orphaned assembly.
  Pointer motion is the only input that preserves a pin: the stable Loader
  input postfix dismisses it before the next mouse, keyboard, or controller
  action continues through the game's normal input path. A second right press
  over the pinned source unlocks during that global pass. The manager keeps a
  right-button press/release latch until the physical button is released, so
  the later holder callback cannot repin on the same dispatch even when Godot
  supplies `_Input` and `_GuiInput` with different managed wrappers for that
  one native event. The pinned stats page's **Copy** button is the sole click
  exception: the global input pass preserves the pin when the press lands in
  that button, then the button hides through a completed render frame while
  `StatsImageCapture` crops the selected item and complete tooltip set from the
  viewport texture in their existing visible arrangement. The resulting
  RGBA image is converted entirely in process to a bottom-up Windows DIB and
  transferred to the OS clipboard; no temporary file, process, or companion
  service participates. Do not correlate input phases with
  `GodotObject.GetInstanceId()`.
- Run-history pinning attaches to both the existing card/relic rows and the
  native card/relic containers. Those containers survive multiplayer player
  selection while their rows are rebuilt, so their `ChildEnteredTree` signals
  attach the same right-click behavior to each replacement row without
  introducing separate lifecycle patches for the two row factories.
- Live top-bar HP and gold controls register with the shared pin manager when
  their first augmented native tooltip is built. Their pinned sets recreate
  the stock Hit Points or Money Pouch page before the SpireLens stats page and
  retain the native below-the-control placement. Top-bar counters and potion
  holders keep their pin surrogate and lock badge under the scene root rather
  than under the hovered control; adding children to those layout participants
  can change the top-bar minimum size and shift the potion belt.
- Card right-click must be claimed on the press, not the release.
  `NCardHolder.OnMousePressed` normally stores right press as
  `_currentPressedAction`; its matching release then emits `AltPressed`.
  SpireLens prefixes every implementation of that virtual method declared by
  an `NCardHolder` subtype and skips the original when the pin toggle handles
  the press. Patching only the base method is insufficient because
  `NHandCardHolder` declares an override. The prefix claims right press only
  when the holder has an `NCardPileScreen` or `NCardsViewScreen` ancestor;
  combat-hand and active card-selection surfaces retain their normal input.
- `StatsTooltip` only constructs the native `HoverTip` value and escapes
  dynamic BBCode. It also wraps the stats description in the established 20px
  body size. It must not retain a `Control`, create a scene-root panel, position
  UI per frame, or mirror native focus/unfocus cleanup. `NHoverTipSet` owns the
  stats node together with the rest of that owner’s tips, so the game’s
  ordinary `Remove(owner)` and tree-exit paths remove it.
- Reusable stat concepts are loaded once from the embedded
  `Core/Config/stat-concepts.json`. Inline symbols and their short definitions
  use Godot `RichTextLabel` `[hint]` markup, while the relic compendium's
  **Icon glossary** mode renders the same cached definitions in its existing
  scrollable content area. This is interactive content inside the pinned native
  SpireLens page, not a second `NHoverTipSet` page.
- Hand hovers are compact unless verbose hand stats are enabled.
- Deck view and other card-view hovers can show full lineage and stat breakdown.
- Tooltip aggregate display merges committed run data plus current pending combat so combat stats appear immediately.

Do not treat UI merged aggregate as proof that a combat has been saved. It is a presentation merge.

When modifying tooltip rows:

- Preserve compact-vs-full distinction.
- Keep rows self-describing without loud section headers unless they genuinely reduce confusion.
- Prefer game icon assets for recognizable concepts like block/draw/energy/stars/effects.
- Avoid creating instance numbers from hover-only preview/template cards.

## Persistence And Shape

`RunData` is the serialized shape. Changes must be additive so that older run files keep loading.

Current persistence facts:

- One run file per run under Godot `user://SpireLens/runs/`.
- File name is SpireLens run id, not the game's run-history file name.
- `GameStartTime` stores the game's run identifier (`RunManager._startTime`) so SpireLens can correlate with game run history and resume active runs after hot reload.
- `RunStorage.SaveAsync` serializes on the caller thread while `RunTracker` holds its lock, then writes on a background task.
- The on-disk shape is detected structurally, not by an explicit version number. The historic pooled shape (aggregates keyed by card definition id) is history-only because it cannot rebuild per-instance live state. Everything else is the current per-instance shape and is resumable across hot reload.
- Per-instance shape is identified by presence of `instance_numbers_by_def` or `def_counters` at the top level of the JSON file. Files predating per-instance identity lack both fields entirely.

For new persisted fields:

- keep them additive so old files deserialize with safe defaults,
- add a fixture under `Fixtures/RunSchema` capturing the new shape,
- update `SchemaLoadingTests` to assert the new shape loads and any new fields land where expected,
- update tooltip/tests if user-facing.

## Choosing A Hook

Use this decision order when adding a stat:

1. Prefer the narrowest reliable owner-specific hook for the mechanic being measured. See [ADR 0001](adr/0001-owner-specific-attribution-hooks.md).
2. Prefer an observed outcome hook over card text or intent.
3. If the observed outcome lacks source context, capture the source earlier and resolve it later through a narrow pending window.
4. If a high-level combat-history wrapper is tiny, beware JIT inlining; prefer a substantive `Hook.*` method or actual mutation point.
5. If the game method mutates a value, use prefix/postfix before/after snapshots to record actual delta.
6. If an action can be redirected, use a final post-mutation hook for the result.
7. If the outcome is heuristic, label the implementation and tooltip behavior as heuristic.
8. If no narrow source window exists, do not guess. Prefer no attribution or a pooled/unknown bucket.

Owner-specific means the game is invoking the card, relic, power, or mechanic
that owns the behavior, not merely reporting a nearby downstream fact. For
example, Book Repair Knife should start from
`BookRepairKnife.AfterDiedToDoom`, then treat the killed-creature payload as the
per-enemy trigger/value unit because the relic heals once per killed creature.
A global Doom-death observer is too broad unless there is no reliable
relic-owned callback.

For per-enemy-killed effects, do not equate "callback invoked once" with
"triggered once" unless the game mechanic really works once per callback. If an
owner callback receives three killed creatures and applies its effect per
creature, the user-facing trigger count is three. If the effect also changes a
resource, measure the actual resource delta around the owner-specific callback
when the attempted amount can diverge from the observed result.

Healing has its own attribution rule: track attempted, actually restored, and
lost healing separately, with lost-healing reason buckets such as `full_hp` and
specific blocker ids as they are discovered. See [ADR 0002](adr/0002-healing-attribution.md).

Blood Vial and Blood Vial??? (`FakeBloodVial`) both heal from their
owner-specific `AfterPlayerTurnStartLate` callback on the first turn. Arm the
shared relic-healing ledger from that exact callback, use the model's current
`DynamicVars.Heal` value as the attempted amount, and finalize after the
returned task completes. Keep `RELIC.BLOOD_VIAL` and
`RELIC.FAKE_BLOOD_VIAL` in separate aggregates even though their tooltip rows
are identical.

Toy Box wax relics are ordinary mutable relic models with `IsWax = true`, not
separate wrapper types or ids. `ToyBox.AfterObtained` pulls each model from the
front of the seeded relic grab bag before marking it wax, so the same ordinary
relic cannot later be rolled from that bag. Keep its effect attribution under
the original relic id, and keep Toy Box's ordered bestowed/melted lifecycle in
its own aggregate. `RelicCmd.Melt` synchronously sets `IsMelted` before awaiting
`AfterRemoved`, making its postfix an observed boundary for the exact wax relic
and melt floor.

Happy Flower and Happy Flower??? (`FakeHappyFlower`) both grant energy from
their owner-specific `AfterSideTurnStart` callback, but on three-turn and
five-turn counters respectively. Patch both callbacks, key the energy window
to the relic owner, and keep `RELIC.HAPPY_FLOWER` and
`RELIC.FAKE_HAPPY_FLOWER` in separate aggregates. Their held-combat
denominators must likewise be keyed by both player and relic id so owning one
variant cannot suppress the other's combat count.

Meat on the Bone evaluates its healing threshold and heals from the
owner-specific `AfterCombatVictoryEarly` callback. Mirror the game's integer
threshold calculation (`current HP <= int(max HP * threshold percent)`), arm
the shared relic-healing ledger only when that condition is true, and finalize
after the callback's returned task completes. Its pre-trigger health stats use
the same prefix snapshot: accumulate the signed raw
`current HP - (max HP * 0.5)` difference from exactly 50% and one
`current HP / max HP` percentage per qualifying trigger. Negative differences
are below half and positive differences are above half. Average the per-trigger
percentages directly, then subtract 50 percentage points for the signed
percentage display, so max-HP changes during the run do not bias the result
toward combats with a larger health pool.

Good hook surfaces already proven useful:

- `CombatHistory.Add`: broad real-entry observation point. Caveat: does NOT see damage from combat-ending killing blows (see the Damage Attribution known trap).
- `Hook.AfterDamageGiven`: fires for every `DamageResult` including the killing hit that ends combat (game dispatches it directly, bypassing the combat-hook guard); the surface used to capture history-suppressed combat-ending damage.
- `Hook.AfterCardDrawn`: reliable card draw arrival.
- `Hook.ShouldDraw`: draw attempts and blocked draw modifier.
- `Hook.AfterCardChangedPiles`: final pile result.
- `Hook.AfterCardGeneratedForCombat`: a generated or transformed card has
  already reached its final combat pile. This is the reliable boundary for
  per-source Soul destination counts; use the still-resolving card play as the
  source and the generated Soul's actual pile as the destination.
- `PlayerCombatState.GainEnergy`: actual energy delta.
- `FakeVenerableTeaSet.AfterEnergyReset` and
  `VenerableTeaSet.AfterEnergyReset`: each checks its saved
  `GainEnergyInNextCombat` flag, awaits its immediate energy command, and then
  clears the flag. Capture the armed state and starting energy in a prefix,
  then record the completed callback's observed energy delta; keep the fake
  and revealed relic ids separate.
- `PlayerCombatState.GainStars`: actual star delta.
- `Hook.AfterForge`: actual forge gain/source.
- `Hook.BeforePowerAmountChanged`: attempted power application context.
- `Hook.ModifyPowerAmountReceived`: final modified power amount and blockers.
- `Hook.ShouldClearBlock`, `Hook.AfterBlockCleared`, `Hook.AfterPreventingBlockClear`: block expiry/waste window.
- `CardPile.AddInternal` filtered to Deck: permanent card entry.
- `CardPileCmd.RemoveFromDeck` prefix: permanent card removal.
- `CardModel.UpgradeInternal` prefix/postfix: source-specific stats can observe
  upgrades from all sources, but card lineage must first snapshot that the
  exact upgraded object is in the permanent deck. Do not canonicalize a combat
  clone through `DeckVersion` for the “Upgraded floor…” lineage.
- `RunManager.EnterMapPointInternal`: original map point entry, before `?`
  points resolve into concrete room types.
- Specific power/relic methods via `AccessTools.TypeByName`: useful when no public compile-time type is safe or when patching optional/specific models.
- `RainbowRing.AfterCardPlayed`: its private Attack, Skill, and Power
  current-turn counters are updated inside this callback. A successful trigger
  is authoritative only after its returned task completes and
  `_activationCountThisTurn` advances; that increment follows both of the
  relic's awaited power applications.

## Diagnostic Habits

When a new stat does not work, first determine which of these failed:

- The patch did not install.
- The patch installed after its caller was already JIT-compiled and needs one
  full game restart before its behavior can be judged.
- The target method never fires for this mechanic.
- The target fires but before/after timing is wrong.
- The outcome has no card source at that point.
- The source card is a combat clone and was not canonicalized.
- The event occurred outside `_pendingCombat`.
- The data is pending but tooltip reads only committed data.
- The data was recorded but shape/default/merge omitted it.
- The stat is correct but compact tooltip intentionally hides it.

`CoreMain.Initialize()` logs Harmony-patched methods for diagnostics. Use that
list to confirm a hook exists before chasing tracker logic. For a newly added
target, “listed as patched” plus “entry diagnostic never fires” is a restart
signal before it is evidence that the target method is wrong.

For source/context debugging, log compact identifiers that line up with JSON events: card id, instance id, card hash, `DeckVersion` status, creature id, history count, current/pending play, and current floor.

## Common Future-Agent Questions

Where do I start for a card stat?

- Find the observed runtime outcome first. Search patches for the closest hook. Then add a `RunTracker` record method and a persisted aggregate if needed.

Where do I start for a relic stat?

- Identify whether the relic has its own callback for the behavior first. If it does, prefer that owner-specific callback and decide whether callback count or payload-item count matches the mechanic's semantic trigger. For per-enemy effects, payload count is often the trigger/value unit. If the relic mutates a resource, record the actual observed delta when it can diverge. If no owner callback exists, identify whether the behavior fires at combat start, turn start, damage, block, draw, or resource mutation. Direct relic model callbacks are preferred when specific and stable; common observed hooks are fallbacks.

How do I know whether a stat belongs to a card instance, pooled generated summary, effect summary, or relic aggregate?

- Stable physical deck card: per-instance card aggregate.
- Combat-generated card with no meaningful deck identity: pooled generated summary or ephemeral aggregate, depending on UI semantics.
- Power/debuff whose later ticks matter: applied effect summary plus downstream ledger if reliably attributable.
- Relic-owned behavior: relic aggregate.

Can I save mid-combat state for reload?

- No, not under the current contract. Pending combat is intentionally not persisted. Keep mid-combat display as pending-only and commit at combat end.

Can I read card text to infer behavior?

- Avoid it. The project goal is observed outcomes. Text can guide which hook to investigate, but not be the source of truth when the game can diverge.

Can I assume `CardSource` is present?

- No. Direct card damage often has it; downstream power damage often does not. Poison is the canonical example of needing an ownership ledger.

Can I assume the current card play is still current?

- No. Some effects resolve after card play history has advanced. Use pending source context only with narrow windows.

Can I patch a private method?

- Yes. Harmony string-name patching and the Publicizer setup make private members accessible where needed. Still document why that method is stable enough to depend on.

Can I patch a tiny wrapper?

- Be suspicious. `CombatHistory.CardDrawn` was unreliable because it could be inlined. Prefer non-trivial hooks or mutation points.

## Maintenance Contract For This Document

Update this primer whenever you learn a durable runtime fact that a future agent would otherwise rediscover. Good candidates:

- a hook that proved reliable or unreliable,
- a timing window around async card/relic/power behavior,
- a source-context trap,
- a canonicalization/pile identity trap,
- a combat-history entry semantic,
- a UI lifecycle quirk,
- a persistence/resume invariant.

Do not turn this into a changelog. Keep it focused on stable game/runtime mechanics and the attribution implications of those mechanics.
