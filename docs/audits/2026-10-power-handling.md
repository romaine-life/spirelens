# SpireLens power handling: current state and streamlining design

Worktree analysed: `D:\repos\spirelens\.claude\worktrees\sad-napier-14962f` (read-only; no repo files edited).
Game source: decompiled `sts2src` in the session scratchpad.
All `file:line` references are relative to the worktree root.

---

## 0. TL;DR

* Power payoff tracking today is **~6,600 lines spread over five layers** (18 patch files ≈ 2,190 lines, ~3,700 lines of
  `RunTracker` power code, a 73-field `PowerAggregate`, a ~100-line hand-written merge, ~400 lines of switch-based tooltip
  code) for **~17 powers with payoff stats**. Most of that is the same five shapes copied per power.
* There is **no single attribution model**. Six distinct shapes coexist:
  pooled-per-power-id (most meta powers), FIFO charge ledger (Buffer), decaying proportional ledger (Poison),
  proportional contributor ledger (Noxious Fumes, Bronze Scales/Thorns), last-applier map (`PlayerPowerOwnershipByModifier`),
  and per-physical-card (Drain Power, which is actually an Attack card). Each has its own state container and reset path.
* Power-produced block and draws are **probably double-booked**: Feel No Pain / Danse Macabre call
  `CreatureCmd.GainBlock(..., cardPlay: null)` from inside a card play, and `RecordBlockGainedEntry` falls back to the
  currently resolving card (`Core/RunTracker.cs:35794`). Dark Embrace / Vicious draws hit the same fallback via
  `FindLikelyDrawSourceCard` (`Core/RunTracker.cs:35813`). The block is in `PowerAggregate.BlockGained` *and* in the played
  card's `TotalBlockGained`. Needs a live check, but the code path has no exclusion.
* Of ~198 powers that cards/relics/potions apply, **only ~26 have any payoff tracking**. ~100+ with a concrete observable
  payoff (damage, block, draw, energy, granted stacks, generated cards) have none (section 4).
* The game offers **no single choke point** to patch once for "a power triggered": `PowerModel.Flash()` is protected,
  tiny, and called by only 90 of 268 power classes (Feel No Pain, Dark Embrace and Rupture never call it).
  `AbstractModel.InvokeExecutionFinished()` is tiny, non-virtual, end-only and fires for every model.
  `PlayerChoiceContext.PushModel` is used in only 24 of ~188 `Hook` dispatchers, and `GainBlock` takes no context.
  So the streamlined design uses **one generic Harmony patch class whose `TargetMethods()` comes from a declarative registry**,
  following a pattern the repo already uses (`HpLossPowerDamageSourcePatch` in `Core/Patches/TungstenRodStatsPatch.cs:84-138`).
  It pushes an `AsyncLocal` trigger frame, and a small set of shared outcome observers credit outcomes through a
  per-power-instance contributor ledger.
* Persisted shape: one additive `Dictionary<string, decimal> Payoffs` bag on the existing per-source
  `AppliedEffectAggregate` (card and relic), on potions, and on the family `PowerAggregate`. Merge becomes generic, legacy
  fields stay readable and frozen, and tooltips sum legacy + new.

---

## 1. Inventory: every place SpireLens handles powers

### 1.1 Power payoff patches (`Core/Patches/*Power*`)

| File | Lines | Harmony classes | Hook targets |
|---|---:|---:|---|
| `AggressionPowerStatsPatch.cs` | 152 | 2 | `AggressionPower.BeforeSideTurnStart`; `CardPileCmd.Add(CardModel,PileType,CardPilePosition,AbstractModel,bool)` |
| `BufferPowerStatsPatch.cs` | 80 | 2 | `BufferPower.ModifyHpLostAfterOstyLate` (postfix arm); `BufferPower.AfterModifyingHpLostAfterOsty` (prefix commit) |
| `ConsumingShadowPowerStatsPatch.cs` | 195 | 3 | `ConsumingShadowPower.AfterSideTurnEnd`; `OrbCmd.EvokeLast`; `Hook.AfterOrbEvoked` |
| `DanseMacabrePowerStatsPatch.cs` | 134 | 3 | `DanseMacabrePower.BeforeCardPlayed`; `CreatureCmd.GainBlock(Creature,decimal,ValueProp,CardPlay,bool)`; `Hook.AfterPlayerTurnStart` |
| `DarkEmbracePowerStatsPatch.cs` | 251 | 4 | `DarkEmbracePower.AfterCardExhausted`; `.AfterSideTurnEnd`; `CardPileCmd.Draw`; `Hook.AfterPlayerTurnStart` |
| `DrainPowerStatsPatch.cs` | 29 | 1 | `Hook.AfterPlayerTurnStart` (note: **Drain Power is an Attack card**, `Cards/DrainPower.cs`, not a power) |
| `EntropyPowerStatsPatch.cs` | 185 | 3 | `Entropy.OnPlay` (card); `EntropyPower.AfterPlayerTurnStart`; `CardCmd.Transform(IEnumerable<CardTransformation>,Rng,CardPreviewStyle)` |
| `FeelNoPainPowerStatsPatch.cs` | 153 | 3 | `FeelNoPainPower.AfterCardExhausted`; `CreatureCmd.GainBlock`; `Hook.AfterPlayerTurnStart` |
| `FreeAttackPowerStatsPatch.cs` | 78 | 2 | `FreeAttackPower.TryModifyEnergyCostInCombatLate`; `.BeforeCardPlayed` |
| `FreeSkillPowerStatsPatch.cs` | 77 | 2 | `FreeSkillPower.TryModifyEnergyCostInCombatLate`; `.BeforeCardPlayed` |
| `JugglingPowerStatsPatch.cs` | 240 | 5 | `PowerModel.DisplayAmount` getter (UI), `JugglingPower.AfterApplied`, `.BeforeCardPlayed`, `.AfterSideTurnEnd`; `CardPileCmd.AddGeneratedCardToCombat`; `Hook.AfterPlayerTurnStart` |
| `PanachePowerDamagePatch.cs` | 50 | 1 | `PanachePower.AfterCardPlayed` (by name) |
| `PoisonPowerAfterSideTurnStartPatch.cs` | 40 | 1 | `PoisonPower.AfterSideTurnStart` (by name) |
| `RupturePowerStatsPatch.cs` | 142 | 3 | `RupturePower.AfterDamageReceived`; `.AfterCardPlayed`; `Hook.AfterPlayerTurnStart` |
| `StampedePowerStatsPatch.cs` | 159 | 2 | `StampedePower.AfterAutoPostPlayPhaseEntered`; `CardCmd.AutoPlay` |
| `UnmovablePowerStatsPatch.cs` | 36 | 1 | `UnmovablePower.ModifyBlockMultiplicative` |
| `ViciousPowerStatsPatch.cs` | 163 | 2 | `ViciousPower.AfterPowerAmountChanged`; `CardPileCmd.Draw` |
| `MetaPowerDenominatorPatch.cs` | 25 | 1 | `Hook.AfterPlayerTurnStart` (shared meta-power turn sampler plus four relic samplers) |
| **Total** | **2,189** | **42** | |

Other power-related patches:

* `HookBeforePowerAmountChangedPatch.cs` (37) and `HookModifyPowerAmountReceivedPatch.cs` (45): application context plus
  Artifact-blocked debuffs (`RunTracker.NotePowerAmountChangeAttempt` `Core/RunTracker.cs:35151`,
  `RecordArtifactBlockedDebuffAttempt` `:35172`).
* `CanonicalPowerHoverTipsPatch.cs` (36): finalizer that swallows `CanonicalModelException` from `PowerModel.HoverTips`
  on canonical models (a BaseLib 3.3.3 compatibility shim). Not attribution.
* `MetaPowerCardBadgePatch.cs` (131): the "META POWER" badge on synthetic not-in-deck cards (UI only).
* `PowerHistoryPilePatch.cs` (456): the played-Powers history pile button (UI only).
* `TungstenRodStatsPatch.cs:84-138` `HpLossPowerDamageSourcePatch`: **already a registry-driven, multi-power
  `TargetMethods()` patch** that pushes an `AsyncLocal` power-source frame (CrimsonMantle, Demise, Disintegration, Inferno,
  MagicBomb, Poison, Strangle). This is the template for the proposed framework.
* `BronzeScalesStatsPatch.cs:115` patches `ThornsPower.BeforeDamageReceived` to credit only the relic's share of Thorns.
* `AkabekoStatsPatch.cs` (Vigor), `KunaiStatsPatch.cs` (Dexterity), `GorgetAfterRoomEnteredPatch.cs` (Plating) and
  `NoxiousFumesAfterSideTurnStartPatch.cs` (Fumes → Poison fan-out) are relic- or card-scoped power observations.
* `HookModifyCardPlayCountPatch.cs`: replay powers (Burst, Echo Form, One Two Punch, Duplication, Signal Boost, Tag Team)
  are recorded as **reason buckets on the replayed card**, not as payoffs of the power's source.

### 1.2 Registries and windows

* `Core/MetaPowerRegistry.cs:15-160`: 21 `(Card, Power, DisplayName)` triples built lazily from `ModelIds.TryGet<T>()`.
  Lookups by card, card id, power and power id. It decides which powers get a synthetic meta-card and the shared
  denominators. It does **not** declare triggers, outcomes, rows or sources.
* `Core/OrbCardRegistry.cs`, `Core/RandomCardGenerationRegistry.cs`: separate registries for recurring orb powers (Storm,
  Spinner, Trash to Treasure, Lightning Rod) and random-card powers (Calamity, Call of the Void, Creative AI, Hello World,
  Spectrum Shift). So three registries describe overlapping power sets.
* `Core/AttributionWindows.cs:1-68`: the generic `AttributionWindowRegistry` (`PlayerBlockGain`, `PlayerEnergyGain`,
  `CardDraw`), FIFO and stored on `PendingCombat.Windows`. **Only relics use it** (Orichalcum, Anchor, Abacus, Cloak Clasp,
  Happy Flower, Booming Conch, Gremlin Horn, ...). No power uses it. `ResetCombatContextState` (`Core/RunTracker.cs:2091-2140`)
  lists the power windows under "DEFERRED (not ported this pass)".

### 1.3 `RunTracker` power code (`Core/RunTracker.cs`, 39,331 lines)

Approximate power-specific lines, from method spans (`awk` over the file; methods are attributed by name, so this undercounts):

| Area | Lines | Location |
|---|---:|---|
| Poison ownership and ticks | ~295 | `NotePoisonTickStarting` :34976, `TryRecordPoisonTickDamage` :36352, `AddPoisonOwnershipLocked` :36465 |
| Meta-power cohort and denominators | ~293 | `InitializeMetaPowerDeckEligibilityLocked` :9381 … `RecordMetaPowerActiveTurnLocked` :9551 |
| Buffer | ~261 | :9112-9380 |
| Dark Embrace | ~257 | :8188-8410 |
| Entropy | ~256 | :9659-9900 |
| Juggling | ~246 | :7107-7313, :9625 |
| Noxious Fumes | ~237 | :35007, :35658-35800 |
| Aggression | ~207 | :8782-8990 |
| Danse Macabre | ~194 | :7315-7510 |
| Consuming Shadow | ~185 | :8416-8600 |
| Stampede | ~181 | :8601-8780 |
| `PowerAggregate` helpers and merge | ~168 | `GetOrCreatePowerAggregate` :9598, `MergePowerAggregatesInto` :37541-37670 |
| Free Attack / Free Skill | ~300 | :9915-10215 |
| Feel No Pain | ~155 | :7510-7667 |
| Drain Power (card) | ~139 | :10558-10690 |
| Rupture | ~131 | :8990-9110 |
| Vicious | ~127 | :7668-7790 |
| Panache | ~76 | :36276-36350 |
| Unmovable | ~36 | :13785-13820 |
| **Total** | **≈3,700** | |

Apply-time entry point: `RecordPowerReceived` (`Core/RunTracker.cs:36097-36262`). It is a hand-maintained `if (entry.Power is X)`
chain: meta application, then Danse / Feel No Pain / Rupture / Dark Embrace "active" marks, Buffer grants (deliberately
before the card-source requirement), Noxious Fumes poison windows, `AppliedEffects`, Free Attack and Free Skill grants
(gated on `causingPlay.Card is Unrelenting/Pounce`), player power ownership, and Noxious Fumes contributions.

Pending state, in three different places:

* **Static fields on `RunTracker`**, cleared by the hand-written `ResetCombatContextState`: `_pendingBufferPreventions`
  (:186), `_pendingDanseMacabreBlockAttributions` (:204), `_pendingFeelNoPainBlockAttributions` (:205),
  `_bronzeScalesThornsContributions` (:210), `_pendingPowerChangeAttempts` (:179), plus `AsyncLocal _hpLossPowerSourceFrame` (:190).
* **`PendingCombat` dictionaries** (`class PendingCombat` :38529): `PendingDarkEmbraceDraws`, `PendingViciousDraws`,
  `PendingJugglingCopyWindows`, `PendingEntropyTransformWindows`, `PendingConsumingShadowCallbacks`,
  `PendingStampedeCallbacks/AutoPlays`, `PlayerBufferLedger`, `PoisonOwnershipByTarget`, `NoxiousFumesContributionsByPower`,
  `PlayerPowerOwnershipByModifier`, `PanacheDamage`, `PendingPoisonTicks`, and **eleven per-power turn and combat dedupe maps**
  (`JugglingPowerTurnCountedTurns`, `DanseMacabrePowerCombatCountedPlayers`, `FeelNoPainPowerTurnCountedTurns`,
  `RupturePowerTurnCountedTurns`, `DarkEmbracePower*` x3, `EntropyPowerCombatCountedPlayers`, `DrainPowerTurnCountedTurns`, …)
  alongside six generic `MetaPower*ByPowerId` maps.
* **`PendingCombat.Windows`** (`AttributionWindowRegistry`): relics only.

There are 22 `Pending*` window or observation classes for powers (`Core/RunTracker.cs:38387-39064`), most with the same four
fields (`PendingCombat`, `Owner`, `PowerId`, `DisplayName`). `PendingDanseMacabreBlockAttribution` and
`PendingFeelNoPainBlockAttribution` (:38444, :38452) are byte-identical.

### 1.4 Persisted shape (`Core/RunData.cs`)

* `RunMetaStats` (:802-818): `ExtraBlockGainedFromUnmovablePower` (legacy scalar) and
  `Dictionary<string, PowerAggregate> PowerAggregates`.
* `PowerAggregate` (:820-991): **73 properties**, almost all named per power (`DarkEmbraceCardsDrawn`, `StampedeRareAttacksPlayed`,
  `FreeSkillUncommonSkillsDiscounted`, …), plus `Rate*` observation-era numerators for 13 of them, the meta denominators
  (`MetaDeckTurns`, `MetaActiveTurns`, `MetaActiveApplicationTurns`), the legacy `TurnsActive`/`CombatsActive`/`DarkEmbraceCombatTurns`,
  Panache damage fields, nested `RandomCardGeneration`, and `OrbOutcomes`.
* `CardAggregate` (per physical card): `BufferChargesGranted/Used/DamagePrevented` (:587-594),
  `DrainPowerCardsUpgraded/TurnsInDeck/UpgradedCardPlays` (:556-564), `OutbreakExtraPoisonTriggerDamage` (:548),
  `AppliedEffects` (:727) and `VulnerableApplied`/`WeakApplied`.
* `RelicAggregate.AppliedEffects` (:1086).
* `AppliedEffectAggregate` (:1020-1033), per source and per power id: `TimesApplied`, `TotalAmountApplied`,
  `TimesBlockedByArtifact`, `TotalAmountBlockedByArtifact`, and three payoff fields that are only ever written by
  Poison/Fumes (`TotalTriggeredEffectiveDamage`, `TotalTriggeredOverkill`) and No Draw (`TotalTriggeredCardsDrawBlocked`).
  **This is effectively the per-source power record already.**
* `PotionRunHistoryEntry` (:337-343): `BufferChargesGranted/Used/DamagePrevented`.

### 1.5 Tooltips (`Core/Patches/CardHoverTooltipPatch.cs`, 2,977 lines)

* Meta-card banner "Shared Meta-Power Record" (:182-191) leads to `AppendCanonicalMetaPowerStats` (:895).
* Physical-copy summary `AppendPhysicalMetaPowerSummary` (:875), called at :408 and :699.
* `AppendMetaPowerLifetimeStats` (:961-1154): a `switch (definition.CardId)` with 12 hand-written cases.
* `AppendMetaPowerRates` (:1156-1249): a second `switch` with 10 cases mapping to `Rate*` fields.
* `AppendBufferChargeStats` (:845-873): per-card Buffer rows.
* `AppendUnrelentingFreeAttackStats` / `AppendPounceFreeSkillStats` (:1755-1820): pooled power rows on the physical
  Attack/Skill card, with a fallback lookup by display name.
* `AppendDrainPowerStats` (:1715).
* `AppendUnmovablePowerStats` (:1285): **dead code, no callers**.
* `AppendAppliedEffects` (:2214), plus dedicated Poison rows.

### 1.6 Docs

* `docs/adr/0003-meta-stats-on-related-surfaces.md`: run-level meta stats must not claim per-card ownership. Labels must not
  imply the hovered card caused a meta value. Future stats must choose explicitly among per-card, effect/relic, pooled
  supplemental, and run-level meta.
* `docs/adr/0001-owner-specific-attribution-hooks.md`: prefer the mechanic's own callback over generic downstream observation.
  **This constrains the design: the framework must stay owner-callback driven, not "watch every block gain."**
* `docs/sts2-runtime-primer.md` "Effects And Powers" (:751-843): two-layer model (application, then downstream). Buffer is
  the reference stacking-counter case. It already says "Do not pool a multi-source power just because the surface is
  power-shaped" and names the block ledger and Poison ledger as the right analogues. The id-derivation rule is at :816.
  "Poison And Other Downstream Damage" is at :844. The meta-card cohorts and denominators are at :2097-2130.

### 1.7 Tests

`Tests/SpireLens.Core.Tests/`: 11 `*PowerStatsTests.cs` (2,026 lines, 64 facts), `BufferChargeLedgerTests.cs` (77 lines, 4),
`MetaPowerRegistryTests.cs` (109, 5), `UnmovableStatsTests.cs` (99). There are 18 power fixtures in `Fixtures/RunSchema/`
(`aggression-power-run.json` … `vicious-power-run.json`, `buffer-charge-ledger-run.json`, `meta-power-registry-run.json`).
Tests reach private renderers by reflection (`AppendCanonicalMetaPowerStats`) and use `*ForTest` seams, about 30 of them
in `RunTracker`.

---

## 2. Per-patch characterization

Legend for attribution model: **Pooled** = credited to `PowerAggregate[powerId]` only, never to a source.

| Power | Trigger hook (owner-specific) | Outcome observation | Attribution model | Persisted fields | Tooltip rows | Boilerplate vs. specific |
|---|---|---|---|---|---|---|
| Aggression | `BeforeSideTurnStart` window | `CardPileCmd.Add` result (discard→hand), `UpgradeInternal` | Pooled | `AggressionCardsReturnedToHand`, `AggressionCardsUpgraded`, 2× `Rate*` | "Cards returned to hand", "Cards upgraded" plus 6 rate rows | ~85% boilerplate. Specific part: the "direct add only, exclude nested" rule |
| Buffer | `ModifyHpLostAfterOstyLate` arm, `AfterModifyingHpLostAfterOsty` commit | Modifier delta | **FIFO charge ledger** (card/potion) **and** Pooled **and** per-potion | `PowerAggregate.Buffer*` x3, `CardAggregate.Buffer*` x3, `PotionRunHistoryEntry.Buffer*` x3 | "HP loss prevented" (pooled **and** per-card, same label), "Charges used/granted", "Avg HP loss prevented per charge". **No rate rows** | ~50%. Genuinely specific: the truncation gate, commit on prefix |
| Consuming Shadow | `AfterSideTurnEnd` window | `OrbCmd.EvokeLast` plus `Hook.AfterOrbEvoked` | Pooled | `OrbsEvoked` (no `Rate*`) | "Orbs evoked". **No rate rows** | ~85% |
| Danse Macabre | `BeforeCardPlayed` (mirrors cost ≥ threshold) | `CreatureCmd.GainBlock` result via `async void` | Pooled | `TimesTriggered`, `BlockGained`, `Rate*` x2, `TurnsActive`, `CombatsActive` | "Times triggered", "Block gained" plus 6 rates | ~90%. Specific: the 1-line qualifier |
| Dark Embrace | `AfterCardExhausted` (immediate) plus `AfterSideTurnEnd` (ethereal batch) | `CardPileCmd.Draw` returned count | Pooled | `DarkEmbraceCardsDrawn`, `Rate*`, `DarkEmbraceCombatTurns`, `TurnsActive`, `CombatsActive` | "Cards drawn" plus 3 rates | ~90% |
| Entropy | `EntropyPower.AfterPlayerTurnStart` (plus the card's `OnPlay` for the combat count) | `CardCmd.Transform` results | Pooled | `Entropy*` x5, `Rate*`, `CombatsActive` | "Cards generated", rarities, "Chains of Binding broken" plus 3 rates | ~70%. Specific: the Bound snapshot |
| Feel No Pain | `AfterCardExhausted` (owner card) | `GainBlock` result (wrapper) | Pooled | `BlockGained`, `RateBlockGained`, `TurnsActive` (**no `CombatsActive`**) | "Block gained" plus 3 rates | ~95% (clone of Danse Macabre) |
| Free Attack / Free Skill | `TryModifyEnergyCostInCombatLate` (remember) plus `BeforeCardPlayed` (confirm decrement) | Marginal cost delta | Pooled (justified: one source card each) | 8 fields each, no `Rate*` | Rows on the **physical** Unrelenting/Pounce card (no meta-card) | ~60%. The two files are identical modulo the type name |
| Juggling | `BeforeCardPlayed` window (third attack) | `AddGeneratedCardToCombat` result | Pooled | `AttacksCopied` plus rarities, `Rate*`, `TurnsActive`, `CombatsActive` | "Total attacks copied", rarities plus 3 rates | ~70%. It also rewrites `DisplayAmount` (UI feature) |
| Panache | `AfterCardPlayed` prefix window (history-bounded, 24 entries) | `DamageReceivedEntry` with dealer = owner, null card | Pooled | `TotalIntended/Blocked/Overkill/Effective`, `Kills` | Damage rows. **No rate rows** | ~60% |
| Poison | `AfterSideTurnStart` arm (24-entry window) | `DamageReceivedEntry` null dealer | **Decaying proportional ledger** per target, per (card, effect) | `AppliedEffect.TotalTriggeredEffectiveDamage/Overkill` (per card) | Dedicated Poison rows on the source card | Specific (normalize, decay) |
| Noxious Fumes | Fumes `AfterSideTurnStart` | Poison application fan-out | **Proportional contributor ledger** keyed by PowerModel instance | Feeds the Poison ledger | Via Poison rows | Specific |
| Rupture | `AfterDamageReceived` plus `AfterCardPlayed` | Owner Strength before/after | Pooled | `StrengthGained`, `RateStrengthGained`, `TurnsActive` | "Strength gained" plus 3 rates | ~85% |
| Stampede | `AfterAutoPostPlayPhaseEntered` window | `CardCmd.AutoPlay` direct calls, then the finished play | Pooled | `Stampede*` x5, `Rate*` x2 | 2 rows, rarities, 6 rates | ~80% |
| Unmovable | `ModifyBlockMultiplicative` postfix | `block * (mult - 1)` | Pooled, **stored twice** | `RunMetaStats.ExtraBlockGainedFromUnmovablePower` **and** `PowerAggregate.UnmovableExtraBlockGained` (+Rate) | "Extra block gained" (reads one, falls back to the other) | Small |
| Vicious | `AfterPowerAmountChanged` (owner-applied Vulnerable) | `CardPileCmd.Draw` count | Pooled | `ViciousCardsDrawn`, `Rate*` | "Cards drawn" plus 3 rates | ~90% (clone of Dark Embrace) |
| Thorns (Bronze Scales only) | `ThornsPower.BeforeDamageReceived` | Damage | **Proportional, relic share only** (static `Dictionary<PowerModel,int>`) | `RelicAggregate` damage | Relic rows | Card-applied Thorns are unattributed |
| Drain Power (card) | `UpgradeInternal` while resolving plus `AfterPlayerTurnStart` | Upgrades, later plays | Per physical card | `CardAggregate.DrainPower*` | Card rows | Not a power; misfiled |

### 2.1 Duplication, quantified

* **12 copies of one "callback window" patch shape**: Prefix arms and returns `Pending*`; Postfix replaces `ref Task __result`
  with an `ObserveAsync` that disarms in `finally`; Finalizer disarms on exception. The copies are Aggression,
  Consuming Shadow (x2), Dark Embrace (x2), Entropy, Feel No Pain, Juggling, Rupture (x2), Stampede (x2) and Vicious. Each is
  about 55 lines, so ~650 lines that would collapse into one generic class. There are 22 `ObserveAsync` wrappers and 12 `HarmonyFinalizer`s.
* **Duplicate outcome patches on shared commands**: `CreatureCmd.GainBlock` is patched by 8 files (2 for powers: Feel No Pain
  and Danse Macabre, which are near-identical). `CardPileCmd.Draw` is patched by 10 files (Dark Embrace and Vicious, which are identical).
  `CardPileCmd.Add` by 4, `AddGeneratedCardToCombat` by 4.
* **7 separate patches on `Hook.AfterPlayerTurnStart`** for powers (Danse, Dark Embrace, Drain, Feel No Pain, Juggling, Rupture, MetaPowerDenominator).
  The meta sampler already computes `MetaActiveTurns` for every registered power (`RecordMetaPowerTurnStarted` :9470).
  The other five compute a parallel `TurnsActive`/`CombatsActive` that **nothing displays**. Those fields are only read by tests and the merge.
* **Tracker per-power quad**: `Arm…` / `TryConsume…` / `Disarm…` / `Record…` plus `Record…PowerTurnStarted` plus
  `Record…PowerActiveForPlayerLocked` plus `…ForTest`. Danse Macabre and Feel No Pain (:7319-7666) are about 340 lines that differ
  by type name and one predicate. The same is true of Dark Embrace vs. Vicious draw (:7668-7790 vs :8188-8300).
* **Merge**: `MergePowerAggregatesInto` (:37541-37670) lists all 73 fields by hand. Every new power adds 3-10 lines here, 3-10
  properties to `PowerAggregate`, a `case` in each of two tooltip switches, a fixture and a schema assertion.
* **Pending classes**: 22 power `Pending*` types, most structurally identical.

---

## 3. Inconsistencies

1. **Six attribution models, chosen per power rather than by the mechanic's shape.**
   * Pooled: Aggression, Consuming Shadow, Danse Macabre, Dark Embrace, Entropy, Feel No Pain, Free Attack/Skill, Juggling,
     Panache, Rupture, Stampede, Unmovable, Vicious, plus the orb and random-card powers.
   * FIFO charge ledger: Buffer (`PlayerBufferLedger`, `BufferChargeChunk` :39156).
   * Decaying proportional ledger: Poison (`PoisonOwnershipByTarget`).
   * Proportional contributor ledger: Noxious Fumes (`NoxiousFumesContributionsByPower`), Thorns for Bronze Scales only
     (`_bronzeScalesThornsContributions`, static, int amounts).
   * Last-applier: `PlayerPowerOwnershipByModifier` (:35323), used for blocked-draw reasons. A second application overwrites the first.
   * Per physical card: Drain Power, Outbreak's extra Poison triggers.

   Most pooled powers are single-source today, so pooling costs nothing. That stops being true when relics, potions or
   generated copies apply them. The primer itself warns about this (:794-800).
2. **Same source, different sink.** Per-source power payoffs land in four places: `CardAggregate.Buffer*`,
   `AppliedEffectAggregate.TotalTriggered*`, `PotionRunHistoryEntry.Buffer*` and `RelicAggregate` (Bronze Scales). Family totals
   land in `PowerAggregate` *or* `RunMetaStats` (Unmovable, both).
3. **Probable double attribution of power outcomes to the resolving card.**
   * Block: `FeelNoPainPower.AfterCardExhausted` and `DanseMacabrePower.BeforeCardPlayed` both call `CreatureCmd.GainBlock(owner, Amount,
     Unpowered, null)` while a card is resolving. `RecordBlockGainedEntry` (:36495-36580) excludes only frost orb, Ornamental Fan
     and potion block, then calls `FindLikelyBlockSourceCard` (:35794), which returns the currently resolving card. The block is
     counted in `PowerAggregate.BlockGained` and in the played card's `TotalBlockGained` (and in its absorbed/wasted ledger share).
   * Draw: Dark Embrace's immediate draw and Vicious's draw happen during a card play. `NoteDrawAttempt` (:34834) then
     `FindLikelyDrawSourceCard` (:35813) credits `TimesCardsDrawn`/`TimesCardsDrawAttempted` to that card.
   * Verify live before fixing. If it is intended ("Bash caused a Vicious draw"), it should be labelled. Either way the
     framework fixes it structurally, because a power trigger frame shadows the card fallback.
4. **The same label for two different facts on one hover.** A physical Buffer card shows "HP loss prevented" twice. The first is
   the pooled family total including Lucky Tonic (`AppendPhysicalMetaPowerSummary` :408, then the `case BufferCardId` :1125). The second is
   that copy's ledger share (`AppendBufferChargeStats` :567). That contradicts ADR 0003 rule 4.
5. **Denominators are computed twice.** `MetaActiveTurns` comes from the shared sampler. Per-power `TurnsActive`/`CombatsActive`
   come from five extra patches, with inconsistent semantics: Feel No Pain and Rupture never increment `CombatsActive`;
   Danse, Juggling, Dark Embrace and Entropy do. Dark Embrace adds a third, `DarkEmbraceCombatTurns`. None of these is rendered.
6. **Rates exist for some powers and not others.** Buffer, Consuming Shadow, Panache and Free Attack/Skill have no `Rate*`
   numerator and no rate rows. Feel No Pain and Danse Macabre share the generic `RateBlockGained`. Rupture uses `RateStrengthGained`.
   Unmovable has its own.
7. **Pending-state lifetime has three regimes.** Static lists need a hand-maintained reset (`ResetCombatContextState` :2091,
   power entries at :2102-2122). `PendingCombat` dictionaries need `ReferenceEquals(PendingCombat, _pendingCombat)` stale guards.
   `PendingCombat.Windows` resets automatically and is used by relics only. Statics are also the hot-reload risk class
   (`CoreMain.Shutdown` cleanup), whereas `PendingCombat` state dies naturally.
8. **Three async idioms.** (a) Replace `ref Task __result` with a wrapper (most). (b) `__result.ContinueWith(..., TaskScheduler.Default)`
   without replacing the task (Danse Macabre `BeforeCardPlayed`). The disarm then runs on a thread-pool thread, after the
   awaiting game code may already have continued. (c) `async void` fire-and-forget (Danse Macabre `GainBlock`). Feel No Pain,
   the twin of Danse Macabre, uses (a) for both.
9. **Grant gating is ad hoc.** Free Attack/Skill grants count only when `causingPlay.Card is Unrelenting/Pounce` (:36215-36240).
   Buffer grants count from any source. `RecordMetaPowerApplicationLocked` (:9423) counts `SuccessfulApplications` only when the
   causing card is the registry card, so Lucky Tonic's Buffer is never an "application" but does make Buffer "active".
   Relics never seed any power ledger except Bronze Scales.
10. **Source kinds treated unevenly.** Cards resolve via `cardSource`, then `FindCurrentlyResolvingCardPlay`. Potions use two
    separate `AsyncLocal` frames (`LuckyTonicFrameTracker`, `PotionBlockFrameTracker`). Relics use one-off windows per relic.
    Powers that apply powers are handled only for Noxious Fumes. Enemies never seed ledgers: a debuff an enemy applies to the player
    has no payoff tracking at all.
11. **Tooltip phrasing drifts.** "Block gained" (Feel No Pain, Danse) vs. "Extra block gained" (Unmovable) vs. the dead
    "Extra block gained from unmovable's power". "Cards generated"/"Commons generated" (Entropy) vs. "Common attacks copied"
    (Juggling) vs. "Common attacks" (Stampede). "HP loss prevented" (Buffer) vs. "Total damage" (Panache). Rate rows say
    "/ active application-turn" for some powers and nothing for others. Meta rows are chosen by **card id** switch, while ledgers
    are keyed by **power id**.
12. **Naming and registry drift.** `DrainPowerStatsPatch`/`DrainPower*` fields track an Attack card. `MetaPowerRegistry`,
    `OrbCardRegistry` and `RandomCardGenerationRegistry` overlap. `FeelNoPainPowerStatsTests` uses `"POWER.FEEL_NO_PAIN"`,
    which is not the real id (`POWER.FEEL_NO_PAIN_POWER`). That is harmless only because the test seeds its own dictionary, and
    it is exactly the class of bug primer :816 warns about.
13. **Hot reload / combat boundary.** Treatment is uniform in principle: all live power state is combat-scoped and discarded on
    a mid-combat reload, and promotion is `MergeMetaStatsInto`/`MergePowerAggregatesInto` at `CombatEnded` (and on loss). But
    (a) statics survive in the orphaned old assembly and rely on the reset list, (b) the merge is field-by-field, so a new field
    left out of it silently vanishes at promotion. Today all 73 are merged (checked by diffing property names against
    `targetAgg.*` assignments).

---

## 4. Player-relevant powers with no payoff tracking

Method: `PowerCmd.Apply<XPower>` call sites under `Models.Cards`, `Models.Relics`, `Models.Potions` and `Models.Enchantments`
(198 distinct powers; Mocks and monster-only powers excluded). Each power's overridden hooks and issued commands were then
read from `Models.Powers/*.cs`. "Tracked" means some payoff (not just application count) is recorded.

**Tracked (≈26):** Aggression, Buffer, Calamity*, Call of the Void*, Consuming Shadow, Creative AI*, Danse Macabre, Dark Embrace,
Entropy, Feel No Pain, Free Attack, Free Skill, Hello World*, Juggling, Lightning Rod†, Noxious Fumes (via Poison), Panache,
Poison, Rupture, Spectrum Shift*, Spinner†, Stampede, Storm†, Trash to Treasure†, Unmovable, Vicious.
(* random-card registry, † orb registry.)

**Partial:**
* Thorns: only the Bronze Scales share. Abrasive, Caltrops and Liquid Bronze shares are not tracked.
* Vigor: Akabeko only. Patter, Terraforming and Prep Time are not tracked.
* Strength, Dexterity, Vulnerable, Weak: application counts only (`AppliedEffects`, `VulnerableApplied`), plus relic-specific
  application deltas (Kunai, Toasty Mittens, Red Skull).
* Doom: Book Repair Knife relic kills only.
* Replay powers (Burst, Echo Form, Duplication, One Two Punch, Signal Boost, Tag Team): recorded as reasons on the replayed card,
  never as the source card's payoff.
* No Draw: blocked-draw reasons.

**Untracked, grouped by observable payoff** (source in parentheses):

* **Damage dealt by the power** (outcome: `DamageReceivedEntry` dealer = owner, or null dealer):
  * Black Hole, Cacophony, Flame Barrier, Hailstorm, Haunt, Inferno, Juggernaut, Necro Mastery, Reflect, Rolling Boulder,
    Serpent Form, Sleight of Flesh, Smokestack, Speedster, Strangle, The Bomb, Thunder, Phantom Blades (all cards).
  * Crimson Mantle (card, also block). Demise (Powdered Demise potion). Disintegration. The Gambit (kill).
  * Thorns for non-relic sources.
* **Block generated** (`BlockGainedEntry`): Afterimage, Beacon of Hope, Block Next Turn (Dodge and Roll, Glitterstream, Prolong,
  Ship in a Bottle), Child of the Stars, Coolant, Pillar of Creation, Plating (Eternal Armor, Neutron Aegis, Stone Armor, Heart of
  Iron; Gorget counts application only), Rage, Self-Forming Clay (relic), Shroud, Sneaky, Spirit of Ash, Toric Toughness.
* **Cards drawn**: Iteration and Pagestorm (draw commands). Draw Cards Next Turn (Glow, Guiding Star, Predator, Relax, Pale
  Blue Dot) and hand-draw modifiers (Demesne, Machine Learning, Tools of the Trade, Tyranny, Clarity potion), which need a
  `ModifyHandDraw` marginal measurement.
* **Energy / stars**: Automation, Energy Next Turn (about 10 cards), Orbit, Radiance (potion), Subroutine, Genesis, Star Next Turn,
  The Sealed Throne. Max-energy modifiers: Demesne, Friendship, Pyre (Waste Away negative).
* **Stacks of another power granted** (chained provenance):
  * Strength: Arsenal, Demon Form, Monologue, Ritual (Mazaleth's Gift), Rupture (tracked).
  * Dexterity: Wraith Form. Focus: Biased Cognition. Vigor: Prep Time.
  * Doom: Countdown, Neurosurge, Reaper Form, Underworld, Oblivion.
  * Poison: Envenom, Concoct, Corrosive Wave.
  * Monarch's Gaze (Strength Down). Shadow Step (Double Damage). Tank (Guarded). Intercept/Covered.
* **Cards generated / moved / auto-played**:
  * Generated or moved: Sentry Mode, Nightmare, Soulbound, Foregone Conclusion, Stratagem, Infinite Blades.
  * Auto-played: Hellraiser, Imitation Learning, Mayhem.
  * Keyword or result location: Master Planner, Feral, Nostalgia, Rebound, Corruption.
  * Upgrade at combat end: Improvement.
  * Forge: Furnace, Hammer Time.
  * Osty summon: Devour Life, Sic 'Em, Summon Next Turn.
* **Energy cost reductions**: Free Power (Synthesis), Veilpiercer, Curious, Borrowed Time, Void Form, Corruption.
* **Damage multipliers/additives** (marginal contribution): Strength, Vigor, Accuracy, Calcify, One for All, Lethality,
  Tracking, Colossus, Conqueror, Hang, Flanking, Knockdown, Vulnerable, Weak, Gigantification, Shrink, Double Damage, Debilitate.
* **Block modifiers / retention**: Dexterity, Fasten, Barricade, Blur (Diamond Diadem relic), Shadowmeld. Frail and No Block are
  self-inflicted costs.
* **Defence / sustain**: Intangible (Apparition, Wraith Form, Ghost in a Jar), Regen (heal), Artifact.
* **Other**: Royalties (gold at combat end), Forbidden Grimoire, Retain Hand / Well-Laid Plans, Ambergris (extra turn), Hibernate.

Highest-value gaps, judged by how often players pick them and how directly the payoff is observable:
Thorns (all sources), Plating, Strength from Demon Form/Ritual/Arsenal, Afterimage, Envenom, Doom producers, Energy/Draw Next Turn,
Intangible, Regen, Infinite Blades, Hailstorm, Juggernaut, Flame Barrier.

---

## 5. Proposed design: one power-payoff framework

### 5.1 Goals and non-goals

Goals:

* One declaration per power: trigger callback(s), qualifier, contribution policy, outcomes it may claim, and tooltip rows.
* One apply-time contributor ledger, keyed by **power instance** (`PowerModel` reference), seeded with leaf sources
  (card instance, relic, potion, enemy, unknown), including chained power→power provenance.
* One `AsyncLocal` trigger frame plus shared outcome observers that already exist (block, draw, damage, energy, stars,
  power-received, generated cards, heal, orb, autoplay).
* One persisted shape, merged generically.
* One tooltip renderer driven by declared rows, honouring ADR 0003 labelling.
* All transient state on `PendingCombat`, so there is no reset list.

Non-goals:

* Do **not** replace ADR 0001. Triggers stay owner-specific callbacks. The framework only removes the copy-paste around them.
* Do not force bespoke mechanics (Buffer's truncation gate, Entropy's Bound snapshot, Aggression's nested-add exclusion,
  Stampede's direct-autoplay rule, Poison's decay) into the generic path. They plug in as small typed hooks feeding the same ledger
  and storage.
* Not a single universal Harmony patch. Section 0 explains why none exists. The generic patch is one *class*, with many
  *targets* coming from the registry.

### 5.2 Types

New folder `Core/Powers/`. Make `RunTracker` `static partial` (`Core/RunTracker.cs:63`) so the framework can live in
`Core/RunTracker.Powers.cs` with access to `_lock`, `_pendingCombat`, `GetOrAssignInstanceId`, `IsTrackedPlayer` and so on,
without widening visibility. That alone also starts carving the ~3,700 power lines out of the 39k-line file.

```csharp
// Core/Powers/PowerOutcomeKeys.cs: persisted dictionary keys. Never rename; additive only.
internal static class PowerOutcomeKeys
{
    public const string Triggers          = "triggers";
    public const string Block             = "block";
    public const string BlockAbsorbed     = "block_absorbed";   // later, via BlockChunk.PowerSource
    public const string DamageEffective   = "damage_effective";
    public const string DamageBlocked     = "damage_blocked";
    public const string DamageOverkill    = "damage_overkill";
    public const string Kills             = "kills";
    public const string CardsDrawn        = "cards_drawn";
    public const string Energy            = "energy";
    public const string Stars             = "stars";
    public const string Heal              = "heal";
    public const string HpLossPrevented   = "hp_loss_prevented";
    public const string EnergySaved       = "energy_saved";
    public const string ChargesGranted    = "charges_granted";
    public const string ChargesUsed       = "charges_used";
    public const string CardsGenerated    = "cards_generated";   // + ".common"/".uncommon"/".rare" suffixes
    public const string CardsAutoPlayed   = "cards_autoplayed";
    public const string CardsUpgraded     = "cards_upgraded";
    public const string OrbsChanneled     = "orbs_channeled";
    public const string OrbsEvoked        = "orbs_evoked";
    public static string StacksGranted(string childPowerId) => "stacks_granted:" + childPowerId; // e.g. Demon Form → Strength
}

[Flags]
internal enum PowerOutcomeKinds
{
    None = 0, Block = 1, Damage = 2, Draw = 4, Energy = 8, Stars = 16, Heal = 32,
    PowerApplied = 64, CardGenerated = 128, AutoPlay = 256, Orb = 512, Upgrade = 1024, Forge = 2048,
}

internal enum ContributionPolicy
{
    Proportional,          // payoff scales with Amount: Feel No Pain, Thorns, Plating, Afterimage, Envenom, Demon Form...
    FifoCharges,           // discrete charges consumed one per trigger: Buffer, Free Attack/Skill/Power, Blur, Intangible, replay powers
    ProportionalDecaying,  // shares shrink per tick: Poison (existing behaviour), Doom-like
    FirstApplier,          // PowerStackType.Single: Barricade, Corruption, Hellraiser, Tank...
    PooledOnly,            // honest "no per-source story" (ADR 0003): keep the family total only
}

internal enum PowerSourceKind { Card, Relic, Potion, Enemy, Unknown }

internal readonly record struct PowerSource(PowerSourceKind Kind, string Id)
{
    public static PowerSource Card(string instanceId) => new(PowerSourceKind.Card, instanceId);
    public static PowerSource Relic(string relicId)   => new(PowerSourceKind.Relic, relicId);
    public static PowerSource Potion(int sequence)    => new(PowerSourceKind.Potion, sequence.ToString(CultureInfo.InvariantCulture));
    public static readonly PowerSource Unknown = new(PowerSourceKind.Unknown, "");
}

// Declarative registry entry.
internal sealed class PowerPayoffDefinition
{
    public required Type PowerType { get; init; }              // typeof(FeelNoPainPower); ids derived lazily via ModelIds
    public required string DisplayName { get; init; }
    public Type? MetaCardType { get; init; }                   // replaces MetaPowerRegistry's (Card, Power) pair
    public ContributionPolicy Policy { get; init; } = ContributionPolicy.Proportional;
    public IReadOnlyList<PowerTriggerSpec> Triggers { get; init; } = [];
    public IReadOnlyList<PowerModifierSpec> Modifiers { get; init; } = [];
    public IReadOnlyList<PowerRowSpec> Rows { get; init; } = [];
    public string? PowerId => ModelIds.TryGetByType(PowerType); // lazy, never at patch time (primer :56)
}

internal sealed record PowerTriggerSpec(
    MethodInfo Method,                              // resolved with typeof()+nameof(), so a rename breaks the compile
    PowerOutcomeKinds Claims,                       // which observers may credit this frame
    Func<PowerModel, object?[], bool>? Qualifies,   // mirrors the native gate (owner card, cost threshold, Vulnerable-by-owner)
    bool CountsTrigger = true);

// Value-returning modifiers (no downstream command): Unmovable, Buffer, Free*, damage/block modifiers.
internal sealed record PowerModifierSpec(
    MethodInfo Method,
    string OutcomeKey,
    Func<PowerModel, object?[], object?, decimal?> Marginal,  // e.g. block * (mult - 1); before - after
    MethodInfo? ConfirmMethod = null);                         // e.g. AfterModifyingHpLostAfterOsty, BeforeCardPlayed

internal sealed record PowerRowSpec(
    string OutcomeKey, string Label, bool Rate = false, bool Detailed = false,
    string? LegacyField = null);   // name of the frozen PowerAggregate/CardAggregate property to add in (section 5.6)
```

Builder syntax for the registry (`Core/Powers/PowerPayoffRegistry.cs`). This replaces `MetaPowerRegistry.BuildAll` and absorbs
the orb and random-card lists' power side:

```csharp
Power<FeelNoPainPower>("Feel No Pain", meta: typeof(FeelNoPain))
    .On(nameof(FeelNoPainPower.AfterCardExhausted), PowerOutcomeKinds.Block,
        when: (p, a) => (a[1] as CardModel)?.Owner?.Creature == p.Owner)
    .Row(PowerOutcomeKeys.Block, "Block gained", rate: true, legacy: nameof(PowerAggregate.BlockGained));

Power<DanseMacabrePower>("Danse Macabre", meta: typeof(DanseMacabre))
    .On(nameof(DanseMacabrePower.BeforeCardPlayed), PowerOutcomeKinds.Block,
        when: (p, a) => a[0] is CardPlay cp && cp.Card.Owner.Creature == p.Owner
                        && cp.Card.EnergyCost.GetResolved() >= p.DynamicVars.Energy.IntValue)
    .Row(PowerOutcomeKeys.Triggers, "Times triggered", rate: true, legacy: nameof(PowerAggregate.TimesTriggered))
    .Row(PowerOutcomeKeys.Block, "Block gained", rate: true, legacy: nameof(PowerAggregate.BlockGained));

Power<ThornsPower>("Thorns")                                 // no meta-card: rows render on sources
    .On(nameof(ThornsPower.BeforeDamageReceived), PowerOutcomeKinds.Damage)
    .Row(PowerOutcomeKeys.DamageEffective, "Thorns damage");

Power<DemonFormPower>("Demon Form", meta: typeof(DemonForm))
    .On(nameof(DemonFormPower.AfterSideTurnStart), PowerOutcomeKinds.PowerApplied)
    .Row(PowerOutcomeKeys.StacksGranted(StrengthId), "Strength gained", rate: true);

Power<BufferPower>("Buffer", meta: typeof(BufferCard), policy: ContributionPolicy.FifoCharges)
    .Modifier(nameof(BufferPower.ModifyHpLostAfterOstyLate), PowerOutcomeKeys.HpLossPrevented,
        marginal: BufferMarginal /* keeps the Truncate gate */, confirm: nameof(BufferPower.AfterModifyingHpLostAfterOsty))
    .Row(PowerOutcomeKeys.HpLossPrevented, "HP loss prevented", legacy: nameof(PowerAggregate.BufferDamagePrevented));
```

### 5.3 Apply-time contributor ledger

```csharp
// Lives on PendingCombat: dies with the combat, so no reset list and no hot-reload residue.
internal sealed class PowerContributorLedger
{
    public required PowerModel Power { get; init; }                 // reference identity handles Instanced / InstancedPerApplier
    public required PowerPayoffDefinition Definition { get; init; }
    public List<PowerContribution> Contributions { get; } = new();  // insertion-ordered (FIFO)
    public decimal Tracked => Contributions.Sum(c => c.Stacks);

    public void Add(PowerSource source, decimal stacks);             // positive PowerReceived
    public void Reduce(decimal stacks);                              // negative delta: proportional scale or FIFO pop, per policy
    public IReadOnlyList<(PowerSource Source, decimal Share)> Shares(); // snapshot at trigger time
    public PowerSource? ConsumeCharge();                             // FifoCharges: pop one unit from the oldest
}
internal sealed class PowerContribution { public PowerSource Source; public decimal Stacks; }

// PendingCombat additions
public Dictionary<PowerModel, PowerContributorLedger> PowerLedgers { get; } = new(ReferenceEqualityComparer.Instance);
public Dictionary<PowerModel, PowerTriggerFrame> PendingPowerModifiers { get; } = new(ReferenceEqualityComparer.Instance);
```

**Seeding hook: no new patch.** `RecordPowerReceived` (`Core/RunTracker.cs:36097`) is fed by `CombatHistory.PowerReceived`, which
`PowerCmd.Apply` and `PowerCmd.ModifyAmount` both call (decompiled `PowerCmd.cs:142` and `:239`) for every non-zero change, including
decrements. Replace the `if (entry.Power is X)` chain with:

```csharp
if (PowerPayoffRegistry.TryGet(entry.Power, out var def))
    SeedOrReducePowerLedgerLocked(def, entry.Power, entry.Amount, ResolvePowerSourceLocked(entry, attempt));
```

Source resolution order, all existing signals:

1. `cardSource` from the paired `PendingPowerChangeAttempt` (`Hook.BeforePowerAmountChanged` already captures it, :35151).
   This is the exact source and covers both the `Apply` and `ModifyAmount` paths.
2. Active **power trigger frame** (5.4). The child stacks inherit the *parent ledger's leaf shares*. This is exactly what
   `TryAllocateNoxiousFumesContributions` (:35724) does today, generalised: Demon Form → Strength, Envenom → Poison,
   Countdown → Doom. The child ledger stores only leaf sources, so no dangling power references.
3. Potion use frame: unify `LuckyTonicFrameTracker` and `PotionBlockFrameTracker` into one `PotionUseFrame` AsyncLocal.
4. Relic frame: a generic `RelicTriggerFrame` pushed by the same TargetMethods technique around relic callbacks that apply
   powers (Bronze Scales, Akabeko, Gorget, Kunai, Data Disk, Diamond Diadem, Belt Buckle, Self-Forming Clay, Twisted Funnel...).
5. `FindCurrentlyResolvingCardPlay`, then `_recentCompletedPlayerCardPlay` (existing fallbacks).
6. `Applier.Monster`: `PowerSource.Enemy(defId)`, which opens enemy-debuff payoffs later.
7. `PowerSource.Unknown`. It absorbs its share without crediting anyone, matching the Buffer/Block ledger convention.

### 5.4 Trigger frame and the generic Harmony patch

```csharp
internal sealed class PowerTriggerFrame
{
    public required PowerModel Power { get; init; }
    public required PowerPayoffDefinition Definition { get; init; }
    public required PowerTriggerSpec Spec { get; init; }
    public required PendingCombat Combat { get; init; }             // stale guard is reference equality, as today
    public required IReadOnlyList<(PowerSource, decimal)> Shares { get; init; } // snapshot at arm
    public int ArmedAtHistoryCount { get; init; }
    public int CardPlayDepthAtArm { get; init; }                     // nested card plays shadow the frame
    public PowerTriggerFrame? Parent { get; init; }
    public decimal Credited;                                         // for "triggers with zero outcome" rows
}

internal static class PowerTriggerContext
{
    private static readonly AsyncLocal<PowerTriggerFrame?> CurrentFrame = new();
    public static PowerTriggerFrame? Current => CurrentFrame.Value;
    internal static PowerTriggerFrame? Push(PowerTriggerFrame f) { var prev = CurrentFrame.Value; CurrentFrame.Value = f; return prev; }
    internal static void Restore(PowerTriggerFrame? prev) => CurrentFrame.Value = prev;
}

[HarmonyPatch]
internal static class PowerTriggerTaskPatch                 // replaces ~12 hand-written window patches
{
    private static IEnumerable<MethodBase> TargetMethods()
        => PowerPayoffRegistry.TriggerMethods(returnType: typeof(Task));   // typeof-only; no ModelDb at patch time

    [HarmonyPrefix]
    public static void Prefix(PowerModel __instance, MethodBase __originalMethod, object[] __args,
                              out (PowerTriggerFrame? Frame, PowerTriggerFrame? Prev) __state)
        => __state = RunTracker.EnterPowerTrigger(__instance, __originalMethod, __args);

    [HarmonyPostfix]
    public static void Postfix((PowerTriggerFrame? Frame, PowerTriggerFrame? Prev) __state, ref Task __result)
    {
        PowerTriggerContext.Restore(__state.Prev);            // caller's flow is unaffected
        if (__state.Frame != null && __result != null)
            __result = RunTracker.ObservePowerTriggerAsync(__result, __state.Frame); // closes the frame and records the trigger
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(Exception? __exception, (PowerTriggerFrame? Frame, PowerTriggerFrame? Prev) __state)
    { if (__exception != null) { PowerTriggerContext.Restore(__state.Prev); RunTracker.AbortPowerTrigger(__state.Frame); } return __exception; }
}

[HarmonyPatch] internal static class PowerModifierDecimalPatch { /* TargetMethods: modifier specs returning decimal */ }
[HarmonyPatch] internal static class PowerModifierBoolPatch    { /* TryModify*: remember marginal per (power, card) */ }
[HarmonyPatch] internal static class PowerModifierConfirmPatch { /* confirm methods: commit the remembered marginal */ }
```

Why `AsyncLocal` works here: it is the mechanism `HpLossPowerDamageSourcePatch` already relies on (`Core/RunTracker.cs:12835-12852`),
and it is what Lucky Tonic, Pen Nib and Paper Phrog use. A value set in a prefix is captured by the async state machine of the
original method, so it flows into every awaited command it issues (`GainBlock` → `CombatHistory.BlockGained`, `Draw` → `AfterCardDrawn`,
`PowerCmd.Apply` → `PowerReceived`). Restoring in the postfix keeps the caller's context clean.

### 5.5 Shared outcome observers (no new Harmony targets)

Each existing shared observation point asks `PowerTriggerContext.Current` before its generic fallback:

| Outcome | Existing surface | Framework action |
|---|---|---|
| Block | `RecordBlockGainedEntry` (:36495) via `CombatHistoryAddPatch` | If the frame claims Block and receiver = frame owner: credit shares, set the new `BlockChunk.PowerId`/source, **skip `FindLikelyBlockSourceCard`** (fixes 3.3) |
| Draw | `NoteDrawAttempt` (:34834) / `HookAfterCardDrawnPatch` | Count non-hand draws under the frame. Skip the card fallback |
| Damage | `DamageReceivedEntry` handler plus `HookAfterDamageGivenPatch` (killing blows, primer) | Dealer = frame owner (or null for tick-type powers) and no card source |
| Energy / Stars | `PlayerGainEnergyPatch`, `PlayerGainStarsPatch` | Credit the observed delta |
| Power applied | `RecordPowerReceived` | Credit `stacks_granted:<child>` and seed the child ledger with the parent's shares |
| Card generated | `HookAfterCardGeneratedForCombatPatch` | Credit plus rarity suffix. Folds in `RandomCardGenerationRegistry`'s power side |
| Heal | `CreatureHealObservationPatch` | Credit the restored amount |
| Orbs | existing `CardOrbStatsPatch` / `Hook.AfterOrbEvoked` | Credit channel or evoke |
| Upgrade | `CardUpgradePatch` (`UpgradeInternal`) | Credit |
| Autoplay | existing `CardCmd.AutoPlay` sites | Credit the finished play |

**Nested-card shadowing rule:** when a card play starts inside a frame (Stampede, Hellraiser, Mayhem autoplay an Attack), the observers
treat the frame as shadowed for that card's own outcomes (`CardPlayDepth > frame.CardPlayDepthAtArm`). This generalises the
hand-written "exclude nested adds/autoplays" rules in Aggression, Stampede and Consuming Shadow. Powers that need the stricter "only the
direct command" semantics keep a tiny `DirectCommandOnly` flag on the spec. The current claim-and-suspend logic moves into the framework once.

### 5.6 Persisted shape (additive)

```csharp
// RunData.cs: three additive properties, one shared payload type
public class PowerPayoffBag
{
    public int Triggers { get; set; }
    public decimal StacksContributed { get; set; }               // source-side: what this source put in
    public Dictionary<string, decimal> Outcomes { get; set; } = new(); // PowerOutcomeKeys, verbatim (no DictionaryKeyPolicy)
}

public class AppliedEffectAggregate { /* existing fields unchanged */ public PowerPayoffBag? Payoff { get; set; } }  // card + relic sources
public class PotionRunHistoryEntry  { /* existing */ public Dictionary<string, PowerPayoffBag>? PowerPayoffs { get; set; } }
public class PowerAggregate         { /* existing 73 fields frozen */ public PowerPayoffBag? Payoff { get; set; } } // family total
```

* **Why `AppliedEffectAggregate`:** it is already "this source applied this power id" for cards and relics (`CardAggregate.AppliedEffects`,
  `RelicAggregate.AppliedEffects`), with the merge and tooltip plumbing in place. Its three `TotalTriggered*` fields are the first,
  ad-hoc payoff fields. Hanging the payoff bag there puts the primer's two layers (application, downstream) in one record.
* **Why a dictionary:** new powers and new outcome kinds become zero-schema changes. The JSON stays snake_case, because keys are
  written as snake_case constants and `RunStorage.Options` (`Core/RunStorage.cs:44-48`) sets no `DictionaryKeyPolicy`.
  The merge is one generic function (sum `Triggers`, `StacksContributed` and each key), replacing ~100 lines of field-by-field code.
* **Family totals** keep living under `RunMetaStats.PowerAggregates[powerId]`. ADR 0003 rule 1 is preserved: the family total is
  distinct from per-source credit, and `Payoff` on the aggregate is the family sum.
* **Denominators:** reuse the existing `MetaDeckTurns`/`MetaActiveTurns`/`MetaActiveApplicationTurns` and their sampler
  (`MetaPowerDenominatorPatch` → `RecordMetaPowerTurnStarted`), extending the sampler from `MetaPowerRegistry.All` to every registry
  entry with `Rate = true` rows. Per-source denominators are the card's existing `TurnsInDeck`/`CombatsInDeck`.

**Backward compatibility (additive rule):**

* No existing property is removed or renamed. Old files load unchanged, and new properties default to null.
* Legacy fields are **frozen**: once a power migrates, the framework stops writing them. Readers compute
  `display = legacyField + Payoff.Outcomes[key]` through `PowerRowSpec.LegacyField` (a precomputed accessor table, not runtime reflection
  on every hover). Nothing is double-counted because each era writes only one place.
* Rates: legacy `Rate*` fields are already observation-era-only (primer :2128). New `Outcomes` entries are by construction observed in the
  same era as the existing Meta denominators, so `rateNumerator = legacyRate + Outcomes[key]` stays honest. For powers that **had no**
  `Rate*` (Buffer, Panache, Consuming Shadow, Free*), the new bag starts the numerator from migration day. Older totals are not divided
  by new denominators, which avoids the false-rate trap the primer warns about.
* Unmovable: keep reading `ExtraBlockGainedFromUnmovablePower` as the legacy fallback, already done at `CardHoverTooltipPatch.cs:1084-1086`.
  Stop writing both.
* The legacy-key duplication from before #309 (primer :836-842) stays unreconciled, as documented.
* Fixtures: add one `Fixtures/RunSchema/power-payoff-bag-run.json` carrying `payoff` on an applied effect, a `power_aggregates` entry and a
  potion, plus one "mixed era" fixture (legacy `block_gained` plus `payoff.outcomes.block`). Add `SchemaLoadingTests` assertions that
  (a) the bags land, (b) all 18 existing power fixtures still load with `Payoff == null`, and (c) the display sum is correct.

### 5.7 Tooltip renderer

One `AppendPowerPayoffRows(sb, PowerPayoffDefinition def, PowerPayoffBag? bag, legacy accessor, compact)` replaces the two
`switch (definition.CardId)` blocks (:961-1249) and the special Buffer/Free* appenders.

* Meta-card (canonical record): the family `PowerAggregate.Payoff` plus legacy, rows from `def.Rows`, rate triplets for
  `Rate = true` rows. The label set comes from the registry, so every power gets the same vocabulary.
* Source card / relic / potion: a per-source line such as `Feel No Pain block (this card's share)`.
* Physical copy of a meta-power card: the family summary, prefixed per ADR 0003 (`All Buffer: HP loss prevented`). This resolves the
  duplicate "HP loss prevented" (3.4).
* Delete `AppendUnmovablePowerStats` (dead). Fold `AppendBufferChargeStats` into the generic per-source row.

### 5.8 What happens to the existing per-power code

| Existing | Becomes |
|---|---|
| 12 window patch classes (Aggression, Consuming Shadow x2, Dark Embrace x2, Entropy, Feel No Pain, Juggling, Rupture x2, Stampede, Vicious, Danse) | Registry `.On(...)` entries driven by `PowerTriggerTaskPatch` |
| Feel No Pain/Danse `GainBlock` patches, Dark Embrace/Vicious `Draw` patches | Deleted. The block and draw observers read the frame |
| 6 `Hook.AfterPlayerTurnStart` power patches plus `TurnsActive`/`CombatsActive`/`DarkEmbraceCombatTurns` | Deleted. `MetaPowerDenominatorPatch` covers every registered power. Legacy fields stay in `RunData`, frozen and unwritten |
| Static `_pendingDanseMacabre…`, `_pendingFeelNoPain…`, `_pendingBufferPreventions`, `_bronzeScalesThornsContributions` | `PendingCombat.PowerLedgers` / `PendingPowerModifiers`. Remove them from `ResetCombatContextState` |
| `PlayerBufferLedger`, `NoxiousFumesContributionsByPower`, `PoisonOwnershipByTarget` | `PowerLedgers` with `FifoCharges` / `Proportional` / `ProportionalDecaying` |
| `PlayerPowerOwnershipByModifier` (last applier) | Derived from the ledger (`Shares()` top contributor, or proportional split of a blocked draw) |
| `MetaPowerRegistry` | Thin projection of `PowerPayoffRegistry` entries with `MetaCardType != null` (keep the API for deck view and badge) |
| Bespoke logic (Buffer gate, Entropy Bound snapshot, Aggression upgrade confirmation, Stampede direct autoplay, Juggling `DisplayAmount`) | Stays as small typed hooks or modifier specs calling `RunTracker.CreditPowerOutcome(frame or power, key, amount)` |
| `DrainPowerStatsPatch` and `DrainPower*` fields | Out of scope (it is a card). Optionally rename the patch to `DrainPowerCardStatsPatch` for clarity |

Expected size after migration: patches ~2,190 → ~700 lines (generic patches ~200 plus irreducible bespoke ~500). Tracker power code
~3,700 → ~1,600. The merge goes from ~130 lines to ~15. Tooltip switch code goes from ~300 lines to ~80. Adding a power becomes one
registry entry plus a fixture/test row, instead of edits in six files.

### 5.9 Migration order

Each step is independently shippable, follows the build/deploy/hot-reload loop, and needs a full game restart only when it introduces
a **new** Harmony target (primer "Newly Added Harmony Targets Need One Full Restart"). Moving an already-patched method from a bespoke
class into the generic `TargetMethods()` class is still a re-patch of an existing target. Hot reload unpatches and repatches, so the
JIT-inlining hazard does not newly apply, but confirm with an entry log the first time.

0. **Groundwork, no behaviour change.** Make `RunTracker` partial. Add the types, `PowerPayoffBag` and the generic merge, plus fixture
   and schema tests. Add `PowerTriggerContext` and the empty `PowerTriggerTaskPatch` (no targets yet). Unify the potion frame.
1. **Feel No Pain and Danse Macabre.** These are the purest clones, so they prove the frame, the block observer, the legacy-sum
   tooltip and the double-booking fix (3.3) together. They are single-source today, so per-source credit equals the family total, which
   makes a strong correctness oracle in tests.
2. **Dark Embrace and Vicious.** This proves the draw observer and deletes two `CardPileCmd.Draw` patches.
3. **Rupture.** First `PowerApplied` claim (Strength delta becomes `stacks_granted:STRENGTH_POWER`), validating chained seeding.
4. **Thorns (all sources) plus Bronze Scales migration.** First genuinely multi-source `Proportional` power with relic, card and
   potion sources. It retires `_bronzeScalesThornsContributions`, and its value is visible to players immediately.
5. **Buffer.** Move it to `FifoCharges` with a modifier spec. Retire `PlayerBufferLedger` and the per-card/per-potion `Buffer*` writes
   (those fields stay frozen and readable). Fix the duplicate label.
6. **Noxious Fumes, then Poison.** Retire the two bespoke ledgers into `Proportional` and `ProportionalDecaying`. Keep the existing tick
   window semantics (history distance 24, amount-match fallback) inside the Poison policy. This is the riskiest step, so do it after the
   framework is proven.
7. **Remaining pooled meta powers.** Juggling, Entropy, Aggression, Consuming Shadow, Stampede, Panache, Unmovable, Free Attack/Skill,
   then the orb and random-card powers. Each moves its bespoke rules into small hooks.
8. **New coverage, cheapest first.** Each is a registry entry plus a test:
   * Block generators (Afterimage, Plating, Coolant, Rage, Sneaky, Toric Toughness, Self-Forming Clay).
   * Damage dealers (Juggernaut, Flame Barrier, Hailstorm, Speedster, Thunder, The Bomb, Rolling Boulder...).
   * Power-granters (Demon Form, Ritual, Arsenal, Wraith Form, Envenom, Doom producers).
   * Energy/Draw Next Turn, Regen, Infinite Blades.
9. **Later, separately designed.** Marginal damage/block modifier contribution (Strength, Vigor, Vulnerable, Weak, Dexterity, ...).
   The repo has frames for this (`PenNibStatsPatch`, `PaperPhrogStatsPatch`), but additive-vs-multiplicative ordering makes the marginal
   split a convention that needs its own ADR. Enemy-applied debuff payoffs too.

Write an ADR 0004 ("Power payoffs use a per-instance contributor ledger and declared owner callbacks") at step 0. Add the primer note
required by CLAUDE.md for each new attribution.

---

## 6. Risks and open questions

1. **AsyncLocal flow is not guaranteed for every game path.** If a power enqueues work into the game's action queue (a `GameAction`
   executed later on another async root) rather than awaiting a command directly, the frame will not flow. The FNP, Danse, Dark
   Embrace, Vicious, Rupture and Thorns paths all `await` commands directly, so they are safe. Each new power needs one log-verified
   observation. Mitigation: a debug counter of "frame armed, zero outcomes" per power, logged at combat end.
2. **Proportional is a convention, not a fact.** The game merges `PowerInstanceType.None` stacks into one `Amount`, exactly like Buffer
   (primer :766-776). Proportional and FIFO attribution must be labelled heuristic in tooltips and docs. This matches the existing
   Block/Buffer/Poison language. It is only exact when there is one source, which is the common case today.
3. **Nested-frame semantics.** Shadowing outcomes when a card play starts inside a frame matches today's Stampede and Aggression intent.
   But powers that react to a card (Afterimage, Rage, Danse) arm *inside* that card's play. The shadow test must compare against the play
   depth **at arm time**, not "any card resolving". Getting this wrong recreates bug 3.3 in reverse.
4. **Generic `TargetMethods` limitations.** `__instance` must be typed `PowerModel` (all targets are `PowerModel` subclasses).
   `__args` boxing adds per-call allocations, so keep qualifiers cheap. Hot paths like `BeforeCardPlayed` fire on every card play for
   every listening power, so do nothing without a qualifying ledger. Return-type variants need separate patch classes.
5. **Double counting during migration.** Every migrated power must stop writing its legacy field in the same commit that starts writing
   the bag. Add a test per power: "after the migrated recorder, the legacy field is unchanged."
6. **Fixing 3.3 changes visible numbers.** Cards like True Grit or Bash will show less block or draw once power outcomes stop being
   credited to them. That is correct per ADR 0001 and 0003, but it is a user-visible change worth calling out in release notes. Older run
   files keep the inflated numbers, and that is not recoverable.
7. **The dictionary shape loses compile-time typing.** Mitigation: only `PowerOutcomeKeys` constants may be used as keys (an analyzer-style
   test that greps for string literals in `Outcomes[...]`), and keys are append-only.
8. **Restart claims.** Moving existing targets into the generic class and adding brand-new targets have different restart requirements.
   Per AGENTS.md "Restart Claims", say "restart needed" only for newly targeted methods, with the evidence.
9. **Scope creep into modifiers.** Damage/block modifier marginal attribution is where most untracked powers live (Strength,
   Vulnerable...). It is deliberately deferred (step 9) because its conventions need a separate decision.
10. **Not yet verified live:** the double-booking in 3.3, and whether `FindCurrentlyResolvingCardPlay` is non-null during
    `BeforeCardPlayed` for Danse Macabre. Both should be confirmed with a debug log before step 1 claims a fix.

---

## Appendix A: hook and command map for player-applied powers

Generated from the decompiled `Models.Powers/*.cs` (overridden hooks | commands issued). A selection of untracked powers:

```
AfterimagePower        Counter | AfterCardPlayed,BeforeCardPlayed      | CreatureCmd.GainBlock
ArsenalPower           Counter | AfterCardGeneratedForCombat           | PowerCmd.Apply<StrengthPower>
AutomationPower        Instanced Counter | AfterCardDrawn              | PlayerCmd.GainEnergy
BlackHolePower         Counter | AfterCardPlayed,AfterStarsGained      | CreatureCmd.Damage
CoolantPower           Counter | AfterSideTurnStart                    | CreatureCmd.GainBlock
CountdownPower         Counter | AfterSideTurnStart                    | PowerCmd.Apply<DoomPower>
DemonFormPower         Counter | AfterSideTurnStart                    | PowerCmd.Apply<StrengthPower>
EnergyNextTurnPower    Counter | AfterEnergyReset                      | PlayerCmd.GainEnergy
EnvenomPower           Counter | AfterDamageGiven                      | PowerCmd.Apply<PoisonPower>
FlameBarrierPower      Counter | AfterDamageReceived                   | CreatureCmd.Damage
HailstormPower         Counter | BeforeSideTurnEnd                     | CreatureCmd.Damage
IntangiblePower        Counter | ModifyHpLostAfterOsty,ModifyDamageCap | (modifier)
IterationPower         Counter | AfterCardDrawn                        | CardPileCmd.Draw
JuggernautPower        Counter | AfterBlockGained                      | CreatureCmd.Damage
PlatingPower           Counter | BeforeSideTurnEndEarly,...            | CreatureCmd.GainBlock
RagePower              Counter | AfterCardPlayed                       | CreatureCmd.GainBlock
RegenPower             Counter | BeforeSideTurnEndEarly                | CreatureCmd.Heal
RitualPower            Counter | AfterSideTurnEnd                      | PowerCmd.Apply<StrengthPower>
ThornsPower            Counter | BeforeDamageReceived                  | CreatureCmd.Damage
ThunderPower           Counter | AfterOrbEvoked                        | CreatureCmd.Damage
ToricToughnessPower    Instanced Counter | AfterBlockCleared           | CreatureCmd.GainBlock
WraithFormPower        Counter | AfterSideTurnStart                    | PowerCmd.Apply<DexterityPower>
```

Instance types that matter for ledger keying: `Instanced` (Automation, Cacophony, Covered, Flanking, Imitation Learning, Knockdown,
Monologue, Nightmare, Orbit, Panache, Rolling Boulder, Tag Team, The Bomb, Toric Toughness) and `InstancedPerApplier` (Oblivion,
Strangle). Keying the ledger by `PowerModel` reference handles all three instance types without special cases.
