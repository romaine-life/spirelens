# SpireLens card-coverage audit

Audited at worktree `sad-napier-14962f` (HEAD 04ecbe6) against the decompiled game source.
Every card listed in the 14 `MegaCrit.Sts2.Core.Models.CardPools` files was opened and classified.
That's 597 pool entries, which is 596 card files plus one `MockCard<T>` parse artifact in DeprivedCardPool.
The Mock pool holds only `Cards.Mocks` types and was skipped.

## How coverage is determined

**Generic attribution (category 1).** Credit goes to whichever card play is resolving, or to the history entry's `CardSource`.
- `Core/RunTracker.cs:3891`: a `DamageReceivedEntry` with `CardSource` goes to `RecordDamageFromCard` (`:36949`, which also books self-damage as `TotalHpLost` at `:36978`). Null-source damage is credited only by the Poison ledger (`:3897`, `TryRecordPoisonTickDamage`) and by Panache (`:3901`).
- `Core/RunTracker.cs:34818`: `FindCurrentlyResolvingCardPlay`, the attribution window behind every recorder below.
- `:36097`: `RecordPowerReceived`. Powers applied while the card resolves go to `AppliedEffects`, plus the Buffer and meta-power hooks.
- `:36493`: `RecordBlockGainedEntry`. Block gained, with the effective/wasted ledger.
- `:4352`: `RecordEnergyGained`. Owner-only; energy given to an ally is dropped.
- `:4418`: stars. `:5058`: forge (only when the forge source is a CardModel).
- `:34789`: other cards exhausted (`RecordCardExhausted`). `:35907`: cards drawn.
- `:34729`: placed-on-top. This credits the MOVED card, not the card that moved it.
- `:3936`: `RecordCardPlay`. Plays, replays, and actual energy spent, so X-cost and self cost reductions are already visible.
- Two generic paths are registry-gated:
  - Orb channel: `Core/OrbCardRegistry.cs:39-62`, gated at `RunTracker.cs:4532`.
  - Random card generation: `Core/RandomCardGenerationRegistry.cs:16-32`.
- Field inventory: `Core/RunData.cs:423-770` (`CardAggregate`).

**Card-specific stats (category 2).**
- Per-instance fields in `Core/RunData.cs`:
  - Potions `:484`, Jack of All Trades `:499`, Discovery `:514`, Splash `:534`, All For One `:543`, Outbreak `:548`.
  - Armaments `:554`, Drain Power `:562`, Debt `:570`, Normality `:578`, Feed / Brightest Flame max HP `:654-660`.
  - Osty / Unleash `:691-703`, Souls `:708`.
- Each has a `Core/Patches/*StatsPatch.cs`.
- Tooltip gates by card id are in `Core/Patches/CardHoverTooltipPatch.cs`:
  - meta-power card-id constants `:34-46`;
  - the compact-body dispatch list `:644-720`;
  - per-card `Append*Stats` gates `:1291-1889` (Unmovable, Alchemize, Jack, Discovery, All For One, Splash, Outbreak, Drain Power, Armaments, Unrelenting, Pounce, Debt, Normality);
  - context rows `:2106-2190` (Death March, Make It So, Pull From Below, Supermassive).
- Pooled power payoff: `Core/MetaPowerRegistry.cs:46-66`, 21 card/power pairs.

**Gap (category 3).** The card's defining effect is one of the following, none of which the paths above observe:
- moving other cards between piles, discarding them, or creating fixed cards/copies;
- auto-playing other cards, upgrading or transforming them;
- cost or keyword changes on other cards;
- evoking or triggering orbs channeled by another card;
- heal, gold, or energy loss;
- a conditional or scaling damage bonus that is the card's point;
- giving resources to allies.

## Counts

Columns:
- **A** = gap that fits an existing pattern
- **B** = gap that needs new mechanics
- **P** = payoff only via a Power (flagged for the power audit)
- **P+** = also listed under A or B

| Pool | A | B | P | (P+) | Generic | Specific | Excluded | Total |
|---|---|---|---|---|---|---|---|---|
| Ironclad | 18 | 1 | 21 | 0 | 37 | 13 | 0 | 90 |
| Silent | 27 | 2 | 29 | 3 | 31 | 2 | 0 | 91 |
| Defect | 22 | 2 | 21 | 1 | 18 | 28 | 0 | 91 |
| Necrobinder | 14 | 4 | 28 | 1 | 39 | 6 | 0 | 91 |
| Regent | 20 | 1 | 33 | 0 | 30 | 7 | 0 | 91 |
| Colorless | 16 | 1 | 17 | 0 | 23 | 8 | 0 | 65 |
| Event | 5 | 2 | 7 | 0 | 8 | 6 | 0 | 28 |
| Quest | 1 | 3 | 0 | 0 | 0 | 0 | 0 | 4 |
| Token | 0 | 0 | 4 | 0 | 7 | 3 | 0 | 14 |
| Curse | 3 | 0 | 0 | 0 | 3 | 2 | 10 | 18 |
| Status | 1 | 0 | 0 | 0 | 7 | 0 | 4 | 12 |
| Deprived | 0 | 0 | 0 | 0 | 0 | 0 | 1 (parse artifact) | 1 |
| Deprecated | 0 | 0 | 0 | 0 | 0 | 0 | 1 | 1 |
| **Total** | **127** | **16** | **160** | **5** | **203** | **75** | **16** | **597** |

## Cross-cutting families (cheapest wins first)

Several A-gaps share one root cause. One generic causer-side recorder, modeled on `RecordCardExhausted`, would close a whole family at once:

1. **Discarding other cards** (~9 cards):
   - Cards: Acrobatics, Calculated Gamble, Dagger Throw, Hidden Daggers, Prepared, Shadow Step, Storm of Steel, Survivor, Scrape.
   - Fix: add `TimesDiscardedOtherCards` in `RecordCardDiscarded`, using the resolving play and skipping self. Mirrors `TimesExhaustedOtherCards`.
2. **Fixed-card / copy / token creation** (~23 cards):
   - Shivs: Blade Dance, Cloak and Dagger, Blade of Ink, Blade Symphony, Fan of Knives, Hidden Daggers, Leading Strike, Storm of Steel, Up My Sleeve.
   - Clones and copies: Anger, Outrage, Adaptive Strike, Undeath, Dual Wield, Heirloom Hammer, Nightmare.
   - Tokens and statuses: Collision Course, Crash Landing, Boost Away, Fight Through, Gunk Up, Overclock, Turbo.
   - Fix: generalize Souls' `SoulsAdded*` into "cards created (by id/pile)" at `Hook.AfterCardGeneratedForCombat` while the card resolves. Reuse RandomCardGeneration's "generated cards later played".
3. **Causer-side pile moves into Hand / top of Draw** (~17 cards):
   - Cards: Headbutt, Hologram, Reboot, Dredge, Graveblast, Cosmic Indifference, Glimmer, Photon Cut, Tutor, Summon Forth, Anointed, Secret Technique, Secret Weapon, Seeker Strike, Thinking Ahead, Neow's Fury, Wish.
   - Fix: extend `RecordCardChangedPiles` (the All For One pattern) with a generic "cards moved to hand/top by this card" counter, split by source pile.
4. **Auto-plays caused** (8 cards):
   - Cards: Cascade, Havoc, Knife Trap, Uproar, Eidolon, Decisions Decisions, Beat Down, Catastrophe.
   - Fix: a `CardCmd.AutoPlay` / `CardPileCmd.AutoPlayFromDrawPile` window while the causer resolves, following the Stampede pattern.
5. **Self-return / self-autoplay** (8 cards):
   - Cards: Howl From Beyond, Right Hand Hand, Bombardment, I Am Invincible, Particle Wall, Shining Strike, Bolas, Thrumming Hatchet.
   - Fix: follow Make It So's `TimesSummonedToHand`.
6. **Defining bonus-damage/block splits** (~32 cards). These are per-card prefix reads of the `Calculated*` var or growth property:
   - Ironclad: Ashen Strike, Bully, Perfected Strike, Dismantle, Spite, Tear Asunder, Evil Eye, Rampage, Thrash.
   - Silent: Echoing Slash, Finisher, Flechettes, Memento Mori, Mirage, Murder, Precise Cut.
   - Defect: Claw, Genetic Algorithm.
   - Necrobinder: Death's Door, Hang, Protector, Rattle, Soul Storm, Squeeze, The Scythe.
   - Regent: Kingly Punch, Heavenly Drill, Lunar Blast, Radiate.
   - Colorless and Event: Gang Up, Rend, Maul.
   - Analog: Unleash `TotalOstyHpAttackBonus`, or the Death March context row.
7. **Orb evoke/trigger credit to the triggering card** (6 cards):
   - Cards: Dualcast, Multi-Cast, Quadcast, Shatter, Tesla Coil, Darkness.
   - Today the output lands on the channeler's `CardOrbAggregate`.
8. **Ally-targeted resources** (multiplayer, 6 cards):
   - Cards: Demonic Shield, Energy Surge, Believe In You, Lift, Rally, Constellation.
   - Cause: the owner/receiver filters drop these gains.
9. **One-offs**:
   - Healing: Not Yet, Spur (ADR 0002).
   - Gold: Hand of Greed, Spoils Map (Debt).
   - Energy lost: Void.
   - Turn-end self-debuff: Doubt, Shame (Normality).
   - Upgrade: Apotheosis (Armaments).
   - Transform: Compact, Begone, Guards, Charge, Primal Force, Seance (Entropy).
   - Cost/charges: Bullet Time, Rocket Punch, Synthesis, Enlightenment, Transfigure.
   - Retain/keyword grants: Expertise, Hand Trick, Snap, Sculpting Strike.
   - Granted replay: Hidden Gem.

## Incidental finding

`CardHoverTooltipPatch.cs:837` (`IsOstySummonStatsHome`) treats `SummonForth` as the Osty-summon tooltip home. But `SummonForth` is a **Regent** card: it recalls Sovereign Blades to hand and forges. It never summons Osty, so that gate never fills. The intended Necrobinder card is probably a different summon card.

## Typical file pattern for a card-specific stat

Examples: b3091bb (Discovery offered counters), abd7399 (Panache damage). They touch the same files:
- `Core/Patches/<Card>StatsPatch.cs`: owner-specific Harmony prefix/postfix, guarded by "this card is resolving".
- `Core/RunTracker.cs`:
  - a `Record<Card>...` recorder, plus a `...ForTest` seam;
  - field lines in `MergeAggregateInto` (`:37319`), which `AggregateDriftTests` enforces.
- `Core/RunData.cs`: additive `CardAggregate` fields, with a comment.
- `Core/Patches/CardHoverTooltipPatch.cs`: an `Append<Card>Stats` gate, called from both the compact and full bodies.
- `Fixtures/RunSchema/<feature>-run.json` plus a `Fixtures/RunSchema/README.md` entry.
- Tests:
  - `Tests/SpireLens.Core.Tests/SchemaLoadingTests.cs`: assertions both ways;
  - `Tests/SpireLens.Core.Tests/<Card>StatsTests.cs`.
- `docs/sts2-runtime-primer.md`: a hook note.
- Power-payoff variants also touch `Core/MetaPowerRegistry.cs`, `NotInDeckViewTests.cs` (registry order), and `PowerAggregate` in `RunData.cs`.

---

## Ironclad (90 cards checked)

### (A) Gaps that fit an existing pattern
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.ANGER | Ironclad | Adds a clone of itself to discard each play; creating card gets nothing | clones created; clone later plays | `Anger.OnPlay` → `CardPileCmd.AddGeneratedCardToCombat(CreateClone(), PileType.Discard)` while resolving | Souls (`SoulsAdded*`) + RandomCardGeneration "generated cards later played" |
| CARD.OUTRAGE | Ironclad | MP-only: adds clones of itself to each living teammate's discard; not credited | clones given to teammates; clone later plays | `Outrage.OnPlay` → `CreateCloneForPlayer` + `CardPileCmd.AddGeneratedCardToCombat(..., PileType.Discard)` while resolving | Souls / RandomCardGeneration utilization |
| CARD.ASHEN_STRIKE | Ironclad | Defining bonus (+3/+4 per card in exhaust pile) not split from base damage | bonus damage from exhaust-pile scaling; avg exhaust pile size at play | `AshenStrike.OnPlay` prefix: read `CalculatedDamageVar` multiplier (`PileType.Exhaust` count) | Unleash `TotalOstyHpAttackBonus`; Supermassive context counter |
| CARD.BULLY | Ironclad | Defining bonus (+2/+3 per Vulnerable stack on target) not split | bonus damage from Vulnerable stacks; avg stacks at play | `Bully.OnPlay` prefix: target `GetPowerAmount<VulnerablePower>()` | Unleash bonus split |
| CARD.PERFECTED_STRIKE | Ironclad | Defining bonus (+2/+3 per Strike-tagged card in combat) not split | bonus damage from Strike count; Strikes counted at play | `PerfectedStrike.OnPlay` prefix: `PlayerCombatState.AllCards.Count(Tags.Contains(Strike))` | Unleash bonus split; Death March context |
| CARD.DISMANTLE | Ironclad | Second hit only if target Vulnerable (defining condition) not split | times condition met; damage from extra hit | `Dismantle.OnPlay` prefix: `cardPlay.Target.HasPower<VulnerablePower>()` | Unleash bonus split |
| CARD.SPITE | Ironclad | Extra hits only if player lost HP this turn (defining) not split | times condition met; damage from extra hits | `Spite.OnPlay` prefix: `LostHpThisTurn` (DamageReceivedEntry this turn) | Unleash bonus split |
| CARD.TEAR_ASUNDER | Ironclad | Hit count = 1 + times player lost unblocked HP this combat; scaling not surfaced | extra hits from HP-loss count; bonus damage from extra hits | `TearAsunder.OnPlay` prefix: `CalculatedVar("CalculatedHits").Calculate` | Death March context counter + Unleash split |
| CARD.EVIL_EYE | Ironclad | Block doubled if a card was exhausted this turn (defining) not split | times condition met; bonus block from second gain | `EvilEye.OnPlay` prefix: `WasCardExhaustedThisTurn` (CardExhaustedEntry this turn) | Unleash bonus split (block variant) |
| CARD.RAMPAGE | Ironclad | Permanent per-play damage growth (+5/+10 each play) not split from base | bonus damage from Rampage growth; current growth amount | `Rampage.OnPlay` prefix: `ExtraDamageFromPlays` / `DynamicVars.Damage.BaseValue` before | Unleash bonus split |
| CARD.THRASH | Ironclad | Exhausts random Attack in hand (generic) and permanently absorbs its damage; absorbed growth not surfaced | damage absorbed; bonus damage from absorbed growth | `Thrash.OnPlay` postfix: `ExtraDamage` / `DynamicVars.Damage.BaseValue` delta | Unleash bonus split |
| CARD.CASCADE | Ironclad | X-cost: auto-plays X(+1) cards from top of draw pile; plays credited to those cards, not Cascade | cards auto-played (by type); damage/block from auto-plays | `Cascade.OnPlay` → `CardPileCmd.AutoPlayFromDrawPile(..., forceExhaust:false)` while resolving | Stampede (MetaPowerRegistry attacks auto-played) |
| CARD.HAVOC | Ironclad | Auto-plays top card of draw pile and exhausts it; not credited to Havoc | cards auto-played (by type); cards force-exhausted | `Havoc.OnPlay` → `CardPileCmd.AutoPlayFromDrawPile(..., forceExhaust:true)` while resolving | Stampede auto-play pooled stats |
| CARD.HEADBUTT | Ironclad | Puts a chosen discard card on top of draw; `TimesPlacedOnTop*` credits the moved card, not Headbutt | cards put on top of draw | `Headbutt.OnPlay` → `CardSelectCmd.FromCombatPile` + `CardPileCmd.Add(card, PileType.Draw, CardPilePosition.Top)` while resolving | All For One (`RecordCardChangedPiles` while resolving) |
| CARD.DRUM_OF_BATTLE | Ironclad | Energy gained when THIS card is exhausted (outside its play) is not credited | times exhausted-trigger fired; energy gained on exhaust | `DrumOfBattle.AfterCardExhausted` (card == this) → `PlayerCmd.GainEnergy` | Debt/Normality owner-callback pattern |
| CARD.HOWL_FROM_BEYOND | Ironclad | Auto-plays itself from exhaust pile each turn; recurrence not distinguished from hand plays | times auto-played from exhaust; damage from exhaust auto-plays | `HowlFromBeyond.AfterAutoPostPlayPhaseEntered` → `CardCmd.AutoPlay(this)` | Make It So (`TimesSummonedToHand`) self-return counter |
| CARD.NOT_YET | Ironclad | Heals 10/13; cards have no heal fields | healing attempted / restored / lost (overheal) | `NotYet.OnPlay` → `CreatureCmd.Heal` while resolving | ADR 0002 relic healing fields (Burning Blood) |
| CARD.DEMONIC_SHIELD | Ironclad | MP-only: block given to a teammate is dropped (block ledger returns early for non-tracked player receivers); self-HP loss covered | block given to ally | `DemonicShield.OnPlay` → `CreatureCmd.GainBlock(cardPlay.Target, ...)` while resolving | generic `TotalBlockGained` ledger (extend receiver filter) |

### (B) Gaps needing new mechanics
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.PRIMAL_FORCE | Ironclad | Transforms every Attack in hand into Giant Rock (upgraded if card upgraded); transform untracked, Giant Rock payoff not linked back | attacks transformed; Giant Rocks later played / their damage | `PrimalForce.OnPlay` → `CardCmd.Transform(item, GiantRock)` while resolving | none (transform); partial: RandomCardGeneration utilization |

### (P) Payoff via power (flag for power audit)
| Card id | pool | power id(s) | note |
|---|---|---|---|
| CARD.BARRICADE | Ironclad | POWER.BARRICADE_POWER | block retention across turns |
| CARD.BLAZE | Ironclad | POWER.STRENGTH_POWER | MP-only; Strength to an ally |
| CARD.BRAND | Ironclad | POWER.STRENGTH_POWER | self-HP loss + chosen exhaust are generic; Strength payoff not |
| CARD.COLOSSUS | Ironclad | POWER.COLOSSUS_POWER | `ModifyDamageMultiplicative` damage reduction; block part generic |
| CARD.CORRUPTION | Ironclad | POWER.CORRUPTION_POWER | skills cost 0 + exhaust |
| CARD.CRIMSON_MANTLE | Ironclad | POWER.CRIMSON_MANTLE_POWER | `AfterPlayerTurnStart` self-damage + block each turn |
| CARD.CRUELTY | Ironclad | POWER.CRUELTY_POWER | `ModifyVulnerableMultiplier` extra Vulnerable damage |
| CARD.DEMON_FORM | Ironclad | POWER.DEMON_FORM_POWER, POWER.STRENGTH_POWER | Strength each turn |
| CARD.DOMINATE | Ironclad | POWER.STRENGTH_POWER | Vulnerable covered; Strength = target Vulnerable stacks |
| CARD.FIGHT_ME | Ironclad | POWER.STRENGTH_POWER | self Strength payoff; enemy Strength gain is a drawback (applied, generic) |
| CARD.FLAME_BARRIER | Ironclad | POWER.FLAME_BARRIER_POWER | Thorns-like retaliation; block part generic |
| CARD.HELLRAISER | Ironclad | POWER.HELLRAISER_POWER | `AfterCardDrawnEarly` auto-plays drawn cards |
| CARD.INFERNO | Ironclad | POWER.INFERNO_POWER | turn-start self-damage; `AfterDamageReceived` AoE damage |
| CARD.INFLAME | Ironclad | POWER.STRENGTH_POWER | flat Strength |
| CARD.JUGGERNAUT | Ironclad | POWER.JUGGERNAUT_POWER | damage on block gain |
| CARD.ONE_TWO_PUNCH | Ironclad | POWER.ONE_TWO_PUNCH_POWER | `ModifyCardPlayCount` replays next attack(s); replay may show on replayed card's ReplayExtraPlay, not here |
| CARD.PYRE | Ironclad | POWER.PYRE_POWER | `ModifyMaxEnergy` +energy per turn |
| CARD.RAGE | Ironclad | POWER.RAGE_POWER | `AfterCardPlayed` block per attack this turn |
| CARD.SETUP_STRIKE | Ironclad | POWER.SETUP_STRIKE_POWER (TemporaryStrengthPower) | temporary Strength; damage part generic |
| CARD.STONE_ARMOR | Ironclad | POWER.PLATING_POWER | recurring block |
| CARD.TANK | Ironclad | POWER.TANK_POWER, POWER.GUARDED_POWER | MP-only; damage taken/redirect multipliers, applies Guarded to allies |

### Covered / excluded
- Generic (1): CARD.BASH, CARD.BATTLE_TRANCE, CARD.BLOOD_WALL, CARD.BLOODLETTING, CARD.BLUDGEON, CARD.BODY_SLAM (damage is entirely the block scaling, total captured), CARD.BREAK, CARD.BREAKTHROUGH, CARD.BURNING_PACT, CARD.CINDER, CARD.CONFLAGRATION, CARD.DEFEND_IRONCLAD, CARD.EXPECT_A_FIGHT (block total captured), CARD.FIEND_FIRE, CARD.FORGOTTEN_RITUAL, CARD.HEMOKINESIS, CARD.IMPERVIOUS, CARD.IRON_WAVE, CARD.MANGLE (ManglePower = temporary enemy Strength loss, debuff-only payoff), CARD.MIDNIGHT (self cost reduction visible via energy spent), CARD.MOLTEN_FIST, CARD.OFFERING, CARD.PACTS_END, CARD.PILLAGE, CARD.POMMEL_STRIKE, CARD.SECOND_WIND, CARD.SHRUG_IT_OFF, CARD.STOMP (self cost reduction visible via energy spent), CARD.STRIKE_IRONCLAD, CARD.SWORD_BOOMERANG, CARD.TAUNT, CARD.THUNDERCLAP, CARD.TREMBLE, CARD.TRUE_GRIT, CARD.TWIN_STRIKE, CARD.UPPERCUT, CARD.WHIRLWIND
- Card-specific (2): CARD.AGGRESSION (MetaPowerRegistry returned/upgraded), CARD.ARMAMENTS (cards upgraded), CARD.DARK_EMBRACE (meta-power cards drawn), CARD.FEED (max HP gained), CARD.FEEL_NO_PAIN (meta-power block), CARD.INFERNAL_BLADE (RandomCardGeneration), CARD.JUGGLING (meta-power attacks copied), CARD.RUPTURE (meta-power strength gained), CARD.STAMPEDE (meta-power attacks auto-played), CARD.STOKE (RandomCardGeneration; hand exhaust generic), CARD.UNMOVABLE (meta-power extra block), CARD.UNRELENTING (FreeAttackPower charges), CARD.VICIOUS (meta-power)
- Excluded: none


## Silent (91 cards checked)

### (A) Gaps that fit an existing pattern
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.ACROBATICS | Silent | draw is generic; the chosen discard of another card is not credited | cards actually discarded; Sly cards triggered by that discard | `Acrobatics.OnPlay` → `CardSelectCmd.FromHandForDiscard` + `CardCmd.Discard(choiceContext, card)` guarded by "Acrobatics is resolving" | All For One (`RecordCardChangedPiles` while resolving) |
| CARD.BLADE_OF_INK | Silent | creates N Shivs and enchants each with Inky; creator gets nothing (Shiv data pools on Shiv meta-card) | Shivs created; Inky enchants applied; later plays of those Shivs | `BladeOfInk.OnPlay` → `Shiv.CreateInHand(owner, count, ...)` (→ `CardPileCmd.AddGeneratedCardsToCombat`) + `CardCmd.Enchant<Inky>` while resolving | Souls (`SoulsAddedToHand`) + RandomCardGeneration later-played utilization |
| CARD.BLADE_DANCE | Silent | Shiv creator; creating card gets nothing | Shivs created; later plays / damage of those Shivs | `BladeDance.OnPlay` loop → `Shiv.CreateInHand(owner, combatState)` while resolving | Souls + RandomCardGeneration utilization |
| CARD.BLADE_SYMPHONY | Silent | multiplayer: creates Shivs in each living teammate's hand; nothing credited | Shivs given to teammates; teammates' later plays of them | `BladeSymphony.OnPlay` → `Shiv.CreateInHand(teammate.Player, combatState)` while resolving | Souls + RandomCardGeneration utilization |
| CARD.BULLET_TIME | Silent | makes every non-X card in hand free this turn (cost change on other cards); NoDrawPower applied | cards made free; energy saved when those cards are played | `BulletTime.OnPlay` → `CardModel.SetToFreeThisTurn` guarded by "Bullet Time is resolving" | Discovery/Splash `EnergyDiscountTotal` (SetToFreeThisTurn patch), Unrelenting energy saved |
| CARD.CALCULATED_GAMBLE | Silent | discards whole hand then draws that many; draw generic, discards not | cards actually discarded; Sly cards triggered | `CalculatedGamble.OnPlay` → `CardCmd.DiscardAndDraw(choiceContext, cards, cardsToDraw)` while resolving | All For One (`RecordCardChangedPiles`) |
| CARD.CLOAK_AND_DAGGER | Silent | block generic; Shiv creation not credited | Shivs created; later plays of those Shivs | `CloakAndDagger.OnPlay` → `Shiv.CreateInHand` while resolving | Souls + RandomCardGeneration utilization |
| CARD.DAGGER_THROW | Silent | damage + draw generic; discard of another card not credited | cards actually discarded; Sly cards triggered | `DaggerThrow.OnPlay` → `CardSelectCmd.FromHandForDiscard` + `CardCmd.Discard` while resolving | All For One (`RecordCardChangedPiles`) |
| CARD.ECHOING_SLASH | Silent | AoE repeats once per kill (defining mechanic); only the total is visible | extra echo waves triggered by kills; damage from echo waves | `EchoingSlash.OnPlay` loop → each `CreatureCmd.Damage(choiceContext, enemies, Damage, owner, this, cardPlay)` call after the first (verify this card-overload sets CardSource) | Unleash bonus split (`TotalOstyHpAttackBonus`) |
| CARD.EXPERTISE | Silent | draw generic; single-turn Retain granted to drawn cards not credited | cards given Retain; of those, cards later played | `Expertise.OnPlay` → `CardCmd.ApplySingleTurnRetain(item)` while resolving | Drain Power (marked cards' later plays) |
| CARD.FAN_OF_KNIVES | Silent | Shiv creator (+ power, see P); creating card gets nothing | Shivs created; later plays of those Shivs | `FanOfKnives.OnPlay` → `Shiv.CreateInHand` loop while resolving | Souls + RandomCardGeneration utilization |
| CARD.FINISHER | Silent | hit count = attacks played this turn (defining scaling) | attacks-this-turn at play (hit count) | `Finisher.OnPlay` prefix: `CalculatedVar("CalculatedHits").Calculate(target)` | Death March context counter |
| CARD.FLECHETTES | Silent | hit count = skills in hand (defining scaling) | skills-in-hand at play (hit count) | `Flechettes.OnPlay` prefix: `CalculatedVar("CalculatedHits").Calculate(target)` | Death March context counter |
| CARD.HAND_TRICK | Silent | block generic; grants single-turn Sly to a chosen Skill in hand, payoff not credited | Skills given Sly; Sly plays of those Skills | `HandTrick.OnPlay` → `CardSelectCmd.FromHand` + `CardCmd.ApplySingleTurnSly(card)` while resolving | Drain Power (marked cards' later plays) |
| CARD.HIDDEN_DAGGERS | Silent | discards 2 chosen cards + creates 2 Shivs (upgraded on +) | cards actually discarded; Shivs created (upgraded); later Shiv plays | `HiddenDaggers.OnPlay` → `CardCmd.Discard(choiceContext, cards)`, `Shiv.CreateInHand(owner, n, ...)`, `CardCmd.Upgrade(item)` while resolving | All For One + Souls/RandomCardGeneration utilization |
| CARD.KNIFE_TRAP | Silent | auto-plays every Shiv in exhaust pile at target (upgrading them on +); damage pools on Shiv, Knife Trap gets nothing | Shivs auto-played; damage dealt by those Shiv plays; Shivs upgraded | `KnifeTrap.OnPlay` → `CardCmd.AutoPlay(choiceContext, item, target, ...)` (+ `CardCmd.Upgrade`) while resolving | Stampede (attacks auto-played, MetaPowerRegistry) / Armaments upgrades |
| CARD.LEADING_STRIKE | Silent | damage generic; Shiv creation not credited | Shivs created; later plays of those Shivs | `LeadingStrike.OnPlay` → `Shiv.CreateInHand` loop while resolving | Souls + RandomCardGeneration utilization |
| CARD.MEMENTO_MORI | Silent | damage scales with cards discarded this turn (defining) | discards-this-turn at play; bonus damage from discards | `MementoMori.OnPlay` prefix: `CalculatedDamageVar.Calculate` (CardDiscardedEntry count) | Death March context counter / Unleash split |
| CARD.MIRAGE | Silent | block = total Poison on living enemies (defining); total block visible, context not | total enemy Poison at play | `Mirage.OnPlay` prefix: `CalculatedBlockVar.Calculate(target)` | Death March context counter |
| CARD.MURDER | Silent | damage scales with cards drawn this combat (defining) | cards-drawn-this-combat at play; bonus damage | `Murder.OnPlay` prefix: `CalculatedDamageVar.Calculate` (CardDrawnEntry count) | Death March context counter / Unleash split |
| CARD.NIGHTMARE | Silent | chosen card is copied 3x into hand next turn by NightmarePower; nothing credited to Nightmare | card chosen; copies added; later plays of the copies | `Nightmare.OnPlay` → `CardSelectCmd.FromHand` + `NightmarePower.SetSelectedCard`; delivery in `NightmarePower.BeforeHandDraw` → `CardPileCmd.AddGeneratedCardToCombat` | Souls + RandomCardGeneration utilization (via applier card) |
| CARD.PRECISE_CUT | Silent | damage reduced per other card in hand (defining) | cards in hand at play; damage lost to penalty | `PreciseCut.OnPlay` prefix: `CalculatedDamageVar.Calculate` | Unleash split / Death March context |
| CARD.PREPARED | Silent | draw generic; chosen discards not credited | cards actually discarded; Sly cards triggered | `Prepared.OnPlay` → `CardSelectCmd.FromHandForDiscard` + `CardCmd.Discard(choiceContext, cards)` while resolving | All For One (`RecordCardChangedPiles`) |
| CARD.SHADOW_STEP | Silent | discards entire hand (+ next-turn power, see P) | cards actually discarded; Sly cards triggered | `ShadowStep.OnPlay` → `CardCmd.Discard(choiceContext, hand)` while resolving | All For One (`RecordCardChangedPiles`) |
| CARD.STORM_OF_STEEL | Silent | discards hand, creates that many Shivs (upgraded on +) | cards actually discarded; Shivs created; later Shiv plays | `StormOfSteel.OnPlay` → `CardCmd.Discard`, `Shiv.CreateInHand(owner, handSize, ...)`, `CardCmd.Upgrade` while resolving | All For One + Souls/RandomCardGeneration utilization |
| CARD.SURVIVOR | Silent | block generic; chosen discard not credited | cards actually discarded; Sly cards triggered | `Survivor.OnPlay` → `CardSelectCmd.FromHandForDiscard` + `CardCmd.Discard` while resolving | All For One (`RecordCardChangedPiles`) |
| CARD.UP_MY_SLEEVE | Silent | Shiv creator (self cost reduction `EnergyCost.AddThisCombat(-1)` already visible via energy spent) | Shivs created; later plays of those Shivs | `UpMySleeve.OnPlay` → `Shiv.CreateInHand` loop while resolving | Souls + RandomCardGeneration utilization |

### (B) Gaps needing new mechanics
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.EXPOSE | Silent | strips all enemy Block and removes Artifact before Vulnerable (enemy-state manipulation, not a power application) | enemy Block stripped; Artifact stacks removed | `Expose.OnPlay` → `CreatureCmd.LoseBlock(choiceContext, target, target.Block, owner)` + `PowerCmd.Remove<ArtifactPower>` while resolving | none |
| CARD.THE_HUNT | Silent | if the hit is a fatal kill, adds an extra card reward (kill counted, reward not) | fatal kills that granted a reward; extra card rewards added | `TheHunt.OnPlay` → `CombatRoom.AddExtraReward(owner, new CardReward(...))` while resolving (TheHuntPower application already generic) | none |

### (P) Payoff via power (flag for power audit)
| Card id | pool | power id(s) | note |
|---|---|---|---|
| CARD.ABRASIVE | Silent | POWER.DEXTERITY_POWER, POWER.THORNS_POWER | Dex + Thorns retaliation damage (cardSource null); Sly |
| CARD.ACCELERANT | Silent | POWER.ACCELERANT_POWER | extra Poison triggers |
| CARD.ACCURACY | Silent | POWER.ACCURACY_POWER | `ModifyDamageAdditive` bonus on Shivs (pools on Shiv) |
| CARD.AFTERIMAGE | Silent | POWER.AFTERIMAGE_POWER | `AfterCardPlayed` → `CreatureCmd.GainBlock` per card played |
| CARD.ANTICIPATE | Silent | POWER.ANTICIPATE_POWER | TemporaryDexterityPower this turn |
| CARD.BLUR | Silent | POWER.BLUR_POWER | block generic; retained block via `ShouldClearBlock`/`AfterPreventingBlockClear` |
| CARD.BURST | Silent | POWER.BURST_POWER | `ModifyCardPlayCount` replays next Skill(s); replay credited to replayed card, not Burst |
| CARD.CORROSIVE_WAVE | Silent | POWER.CORROSIVE_WAVE_POWER | `AfterCardDrawn` → Poison to all enemies (cardSource null) |
| CARD.CONCOCT | Silent | POWER.CONCOCT_POWER | multiplayer; `AfterDamageGiven` → Poison on targets hit (cardSource null) |
| CARD.DODGE_AND_ROLL | Silent | POWER.BLOCK_NEXT_TURN_POWER | block generic; next-turn block |
| CARD.ENVENOM | Silent | POWER.ENVENOM_POWER | `AfterDamageGiven` → Poison (cardSource null) |
| CARD.FADE | Silent | POWER.FADE_POWER | multiplayer; TemporaryDexterityPower on ally |
| CARD.FAN_OF_KNIVES | Silent | POWER.FAN_OF_KNIVES_POWER | Shivs hit all enemies (also in A for Shiv creation) |
| CARD.FLANKING | Silent | POWER.FLANKING_POWER | multiplayer; `ModifyDamageMultiplicative` on target for teammates |
| CARD.SIDESTEP | Silent | POWER.ENERGY_NEXT_TURN_POWER | next-turn energy |
| CARD.FOOTWORK | Silent | POWER.DEXTERITY_POWER | Dexterity self-buff |
| CARD.INFINITE_BLADES | Silent | POWER.INFINITE_BLADES_POWER | `BeforeHandDraw` creates a Shiv each turn |
| CARD.MASTER_PLANNER | Silent | POWER.MASTER_PLANNER_POWER | `AfterCardPlayed` → `CardCmd.ApplyKeyword(card, Sly)` on played Skills |
| CARD.NIGHTMARE | Silent | POWER.NIGHTMARE_POWER | delivers 3 copies next turn (also in A) |
| CARD.NOXIOUS_FUMES | Silent | POWER.NOXIOUS_FUMES_POWER | `AfterSideTurnStart` → Poison all enemies (cardSource null) |
| CARD.PHANTOM_BLADES | Silent | POWER.PHANTOM_BLADES_POWER | Retain on Shivs + `ModifyDamageAdditive` bonus |
| CARD.PREDATOR | Silent | POWER.DRAW_CARDS_NEXT_TURN_POWER | damage generic; +2 draw next turn |
| CARD.SERPENT_FORM | Silent | POWER.SERPENT_FORM_POWER | `AfterCardPlayed` → `CreatureCmd.Damage` (no card source) |
| CARD.SHADOW_STEP | Silent | POWER.SHADOW_STEP_POWER, POWER.DOUBLE_DAMAGE_POWER | next turn applies DoubleDamage (also in A for discards) |
| CARD.SHADOWMELD | Silent | POWER.SHADOWMELD_POWER | `ModifyBlockMultiplicative` this turn |
| CARD.SNEAKY | Silent | POWER.SNEAKY_POWER | multiplayer; `AfterCardPlayed` → GainBlock; Sly |
| CARD.SPEEDSTER | Silent | POWER.SPEEDSTER_POWER | `AfterCardDrawn` → AoE `CreatureCmd.Damage` (no card source) |
| CARD.STRANGLE | Silent | POWER.STRANGLE_POWER | damage generic; `AfterCardPlayed` → unblockable HP loss on target (cardSource null) |
| CARD.TOOLS_OF_THE_TRADE | Silent | POWER.TOOLS_OF_THE_TRADE_POWER | `ModifyHandDraw` +N and `AfterPlayerTurnStart` discard N |
| CARD.TRACKING | Silent | POWER.TRACKING_POWER | `ModifyDamageMultiplicative` vs Weak targets |
| CARD.WELL_LAID_PLANS | Silent | POWER.WELL_LAID_PLANS_POWER | `ShouldFlush` retains cards |
| CARD.WRAITH_FORM | Silent | POWER.INTANGIBLE_POWER, POWER.WRAITH_FORM_POWER | Intangible prevention + Dex loss per turn |

### Covered / excluded
- Generic (1): CARD.ADRENALINE, CARD.ASSASSINATE, CARD.BACKFLIP, CARD.BACKSTAB, CARD.BOUNCING_FLASK, CARD.BUBBLE_BUBBLE, CARD.DAGGER_SPRAY, CARD.DASH, CARD.DEADLY_POISON, CARD.DEFEND_SILENT, CARD.DEFLECT, CARD.ESCAPE_PLAN, CARD.FLICK_FLACK, CARD.GRAND_FINALE, CARD.HAZE, CARD.LEG_SWEEP, CARD.MALAISE, CARD.NEUTRALIZE, CARD.PIERCING_WAIL, CARD.PINPOINT, CARD.POISONED_STAB, CARD.REFLEX, CARD.RICOCHET, CARD.SKEWER, CARD.SLICE, CARD.SNAKEBITE, CARD.STRIKE_SILENT, CARD.SUCKER_PUNCH, CARD.SUPPRESS, CARD.TACTICIAN, CARD.UNTOUCHABLE
- Card-specific (2): CARD.OUTBREAK (extra poison-trigger damage via `PoisonPower.Trigger` while resolving), CARD.POUNCE (FreeSkillPower charges granted/used/energy saved)
- Excluded: none


## Defect (91 cards checked)

### (A) Gaps that fit an existing pattern
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.ADAPTIVE_STRIKE | Defect | Adds a 0-cost clone (CreateClone + EnergyCost.SetThisCombat(0)) to discard; creator gets nothing, clone's later damage sits on the clone instance | clones created; clone plays later; clone damage dealt | AdaptiveStrike.OnPlay wrap / CardPileCmd.AddGeneratedCardToCombat while this card resolves | Souls (SoulsAdded* at final pile) + RandomCardGeneration "generated cards later played" |
| CARD.BOOST_AWAY | Defect | Creates a Dazed in discard (status fuel for Flak Cannon/Compact/Rocket Punch/Smokestack), not credited | status cards created | CardPileCmd.AddGeneratedCardToCombat(Dazed, Discard) while resolving | Souls (fixed token creation) |
| CARD.FIGHT_THROUGH | Defect | Creates 2 Wounds in discard, not credited (block covered) | status cards created | CardPileCmd.AddGeneratedCardToCombat(Wound, Discard) while resolving | Souls (fixed token creation) |
| CARD.GUNK_UP | Defect | Creates a Slimed in discard, not credited (damage covered) | status cards created | CardPileCmd.AddGeneratedCardToCombat(Slimed, Discard) while resolving | Souls (fixed token creation) |
| CARD.OVERCLOCK | Defect | Creates a Burn in discard, not credited (draw covered) | status cards created | CardPileCmd.AddGeneratedCardToCombat(Burn, Discard) while resolving | Souls (fixed token creation) |
| CARD.TURBO | Defect | Creates a Void in discard, not credited (energy covered) | status cards created | CardPileCmd.AddGeneratedCardToCombat(Void, Discard) while resolving | Souls (fixed token creation) |
| CARD.COMPACT | Defect | Transforms every transformable Status in hand into Fuel (upgraded if Compact+); block covered | statuses transformed; Fuel later played | CardCmd.Transform(list of CardTransformation) while resolving | Entropy transforms (MetaPowerRegistry) + generated-card utilization |
| CARD.CLAW | Defect | Each play permanently buffs ALL Claws (+Increase); bonus portion of damage not split | bonus damage from prior Claw buffs; Claw copies buffed | Claw.BuffFromClawPlay (private) / ExtraDamageFromClawPlays read at damage time | Unleash bonus split (TotalOstyHpAttackBonus) |
| CARD.GENETIC_ALGORITHM | Defect | Block grows permanently per play (IncreasedBlock SavedProperty, also on DeckVersion); growth not shown | current block value (context); extra block from growth | GeneticAlgorithm.BuffFromPlay / IncreasedBlock after OnPlay | Unleash bonus split + Death March context counter |
| CARD.HOLOGRAM | Defect | Chosen card returned discard->hand not credited (block covered) | cards returned to hand (by type) | CardSelectCmd.FromCombatPile + CardPileCmd.Add(card, Hand) while resolving | All For One (RecordCardChangedPiles) |
| CARD.REBOOT | Defect | Puts whole hand back into draw pile + Shuffle (draw covered) | cards shuffled back from hand | CardPileCmd.Add(item, PileType.Draw) / CardPileCmd.Shuffle while resolving | All For One (RecordCardChangedPiles) |
| CARD.SCRAPE | Defect | Discards the drawn cards that are not 0-cost; discard of other cards not tracked | cards discarded; 0-cost cards kept | CardCmd.Discard(choiceContext, cards) while resolving | All For One (RecordCardChangedPiles hand->discard) |
| CARD.ROCKET_PUNCH | Defect | Self cost -1 (EnergyCost.AddUntilPlayed) each time owner generates a Status; discount amount not shown | discounts granted; energy saved | RocketPunch.AfterCardGeneratedForCombat | Unrelenting/Pounce energy saved |
| CARD.SYNTHESIS | Defect | Grants a free Power play (FreePowerPower) after damage; charge use not tracked | charges granted/used; energy saved | FreePowerPower.TryModifyEnergyCostInCombatLate / BeforeCardPlayed | Pounce (FreeSkillPower) / Unrelenting (FreeAttackPower) |
| CARD.UPROAR | Defect | Auto-plays a random Attack from draw pile; causer gets nothing (played card gets its own stats) | attacks auto-played; times none available | CardCmd.AutoPlay while Uproar resolves | Stampede attacks auto-played (MetaPowerRegistry) |
| CARD.ENERGY_SURGE | Defect | Energy to teammates dropped by the owner filter in RunTracker.RecordEnergyGained (only own share counted) | energy given to allies | PlayerCmd.GainEnergy per teammate / PlayerGainEnergyPatch (ally bucket instead of owner-only) | generic TotalEnergyGenerated |
| CARD.DUALCAST | Defect | Evokes front orb twice; evoke output credited to channeler, evoker gets nothing | evokes triggered; evoke damage/block/energy produced | OrbCmd.EvokeNext while resolving (orb.EvokeActivated) | CardOrbAggregate evoke rows (OrbCardRegistry) - add evoker-side credit |
| CARD.MULTI_CAST | Defect | X(+1) evokes of front orb; output credited to channeler | evokes triggered; evoke output | OrbCmd.EvokeNext while resolving | CardOrbAggregate evoke rows - evoker-side credit |
| CARD.QUADCAST | Defect | Evokes front orbs 4 times; output credited to channeler | evokes triggered; evoke output | OrbCmd.EvokeNext while resolving | CardOrbAggregate evoke rows - evoker-side credit |
| CARD.SHATTER | Defect | Evokes every orb twice (its own AoE damage covered); orb output credited to channelers | evokes triggered; evoke output | OrbCmd.EvokeNext(dequeue:false) + OrbCmd.EvokeNext while resolving | CardOrbAggregate evoke rows - evoker-side credit |
| CARD.TESLA_COIL | Defect | Triggers Passive of every Lightning orb (x2 upgraded) at target; that damage credited to channeler | passives triggered; passive damage produced | OrbCmd.Passive(choiceContext, lightningOrb, cardPlay.Target) while resolving | CardOrbAggregate passive rows - trigger-side credit |
| CARD.DARKNESS | Defect | Channel already tracked (in registry), but forced Passive on ALL Dark orbs (x2 upgraded) is credited to each orb's channeler | forced passive triggers; evoke value added | OrbCmd.Passive(choiceContext, darknessOrb, null) while resolving | CardOrbAggregate passive rows - trigger-side credit |

### (B) Gaps needing new mechanics
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.CAPACITOR | Defect | Adds orb slots; no slot ledger (benefit = orbs held instead of auto-evoked) | slots actually added; orbs channeled into extra slots | OrbCmd.AddSlots while resolving | none |
| CARD.MODDED | Defect | Adds 1 orb slot (draw covered; self cost +1 via EnergyCost.AddThisCombat already visible in energy spent) | slots actually added | OrbCmd.AddSlots while resolving | none |

### (P) Payoff via power (flag for power audit)
| Card id | pool | power id(s) | note |
|---|---|---|---|
| CARD.BIASED_COGNITION | Defect | POWER.FOCUS_POWER, POWER.BIASED_COGNITION_POWER | Focus self-buff; Biased Cognition focus decay |
| CARD.BULK_UP | Defect | POWER.STRENGTH_POWER, POWER.DEXTERITY_POWER | Str/Dex self-buff; also OrbCmd.RemoveSlots (slot cost not shown) |
| CARD.CHARGE_BATTERY | Defect | POWER.ENERGY_NEXT_TURN_POWER | next-turn energy (block covered) |
| CARD.COOLANT | Defect | POWER.COOLANT_POWER | block per distinct orb type at turn start (GainBlock, null card) |
| CARD.DEFRAGMENT | Defect | POWER.FOCUS_POWER | Focus self-buff |
| CARD.ECHO_FORM | Defect | POWER.ECHO_FORM_POWER | replays first card each turn via ModifyCardPlayCount (replay visible on replayed card only) |
| CARD.FERAL | Defect | POWER.FERAL_POWER | 0-cost attacks return to hand (ModifyCardPlayResultLocation) |
| CARD.FOCUSED_STRIKE | Defect | POWER.FOCUSED_STRIKE_POWER | temporary Focus (damage covered) |
| CARD.HAILSTORM | Defect | POWER.HAILSTORM_POWER | turn-end AoE CreatureCmd.Damage (null cardSource) |
| CARD.HOTFIX | Defect | POWER.HOTFIX_POWER | temporary Focus |
| CARD.IMITATION_LEARNING | Defect | POWER.IMITATION_LEARNING_POWER | auto-plays clones of target ally's cards (CardCmd.AutoPlay) |
| CARD.ITERATION | Defect | POWER.ITERATION_POWER | draws on AfterCardDrawn |
| CARD.LOOP | Defect | POWER.LOOP_POWER | OrbCmd.Passive on front orb each turn start (output goes to channeler) |
| CARD.MACHINE_LEARNING | Defect | POWER.MACHINE_LEARNING_POWER | +hand draw (ModifyHandDraw) |
| CARD.ONE_FOR_ALL | Defect | POWER.ONE_FOR_ALL_POWER | applied to all players; ModifyDamageAdditive |
| CARD.SCAVENGE | Defect | POWER.ENERGY_NEXT_TURN_POWER | next-turn energy (exhaust of chosen card covered) |
| CARD.SIGNAL_BOOST | Defect | POWER.SIGNAL_BOOST_POWER | replays next Power card via ModifyCardPlayCount |
| CARD.SMOKESTACK | Defect | POWER.SMOKESTACK_POWER | AoE damage on each generated Status (AfterCardGeneratedForCombat) |
| CARD.SUBROUTINE | Defect | POWER.SUBROUTINE_POWER | energy on AfterCardPlayed |
| CARD.SYNCHRONIZE | Defect | POWER.SYNCHRONIZE_POWER | temporary Focus = distinct orb types |
| CARD.THUNDER | Defect | POWER.THUNDER_POWER | damage on AfterOrbEvoked (null cardSource) |
| CARD.SYNTHESIS | Defect | POWER.FREE_POWER_POWER | also in (A): free Power charge |

### Covered / excluded
- Generic (1): CARD.BARRAGE, CARD.BEAM_CELL, CARD.BOOT_SEQUENCE, CARD.COMPILE_DRIVER, CARD.DEFEND_DEFECT, CARD.DOUBLE_ENERGY, CARD.FLAK_CANNON, CARD.FTL, CARD.GO_FOR_THE_EYES, CARD.HELIX_DRILL, CARD.HYPERBEAM (HYPERBEAM_FOCUS_DOWN_POWER is a drawback; application only), CARD.LEAP, CARD.MOMENTUM_STRIKE, CARD.SKIM, CARD.STRIKE_DEFECT, CARD.SUNDER (kill energy observed while resolving), CARD.SUPERCRITICAL, CARD.SWEEPING_BEAM
- Card-specific (2): CARD.ALL_FOR_ONE (zero-cost cards returned), CARD.BALL_LIGHTNING (orb registry), CARD.BUFFER (meta-power BUFFER), CARD.CHAOS (orb registry, all orb types), CARD.CHILL (orb registry), CARD.COLD_SNAP (orb registry), CARD.CONSUMING_SHADOW (orb registry + meta-power), CARD.COOLHEADED (orb registry; draw generic), CARD.CREATIVE_AI (meta-power + random-generation power), CARD.FUSION (orb registry), CARD.GLACIER (orb registry), CARD.GLASSWORK (orb registry), CARD.HIBERNATE (orb registry; HIBERNATE_POWER payoff not credited), CARD.ICE_LANCE (orb registry), CARD.IGNITION (orb registry; channels to ally, credited via resolving card), CARD.LIGHTNING_ROD (registry recurring power), CARD.METEOR_STRIKE (orb registry), CARD.NULL (orb registry), CARD.RAINBOW (orb registry), CARD.REFRACT (orb registry), CARD.SHADOW_SHIELD (orb registry), CARD.SPINNER (orb registry + recurring power + meta-power), CARD.STORM (registry recurring power + meta-power), CARD.TEMPEST (orb registry), CARD.TRASH_TO_TREASURE (registry recurring power + meta-power), CARD.VOLTAIC (orb registry), CARD.WHITE_NOISE (random generation), CARD.ZAP (orb registry)
- Excluded: none


## Necrobinder (91 cards checked)

Osty check (Core/RunTracker.cs): card Osty summons credit via `RecordOstySummoned` (OstyCmdSummonPatch -> TimesOstySummoned / TotalOstyHpSummoned); non-card summons (powers like SummonNextTurnPower, SicEmPower, DevourLifePower) go to `RecordNonCardOstySummon`, which only feeds run MetaStats and the two Phylactery relics, never a card or power aggregate. Osty attacks built with `DamageCmd.Attack(...).FromOsty(osty, this, cardPlay)` carry CardSource, so they are card damage. Only Unleash has a bonus split (`RecordUnleashOstyHpAttackBonus`). Doom deaths (`DoomPower.DoomKill` -> `CreatureCmd.Kill`) are not damage and are credited only to Book Repair Knife (`RecordBookRepairKnifeTrigger`). `PowerCmd.ModifyAmount` also writes `History.PowerReceived`, so Misery's debuff spread already shows up in AppliedEffects.

### (A) Gaps that fit an existing pattern
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.DEATHS_DOOR | Necrobinder | Extra block gains when Doom was applied this turn (defining condition) are mixed into total block | times Doom condition met; bonus block from the condition | `DeathsDoor.OnPlay` (read `WasDoomAppliedThisTurn` in prefix; count extra `CreatureCmd.GainBlock` calls while resolving) | Unleash `TotalOstyHpAttackBonus` (bonus split) |
| CARD.DREDGE | Necrobinder | Picks up to 3 cards from discard into hand; the move isn't credited | cards returned discard->hand | `CardPileCmd.Add(IEnumerable, PileType.Hand)` while Dredge is resolving (after `CardSelectCmd.FromCombatPile`) | All For One `RecordCardChangedPiles` |
| CARD.EIDOLON | Necrobinder | Auto-plays every playable Ethereal card in the exhaust pile; the autoplays aren't credited to Eidolon | cards auto-played from exhaust; damage/block from those plays | `Eidolon.OnPlay` loop of `CardCmd.AutoPlay` (arm a callback-wide context like Stampede) | Stampede (StampedePowerStatsPatch, attacks auto-played) |
| CARD.END_OF_DAYS | Necrobinder | Applies Doom then calls `DoomPower.DoomKill` right away; those kills go through `CreatureCmd.Kill`, not damage, so they aren't credited | enemies killed by immediate Doom; Doom HP removed | `DoomPower.DoomKill` / `CreatureCmd.Kill` while End of Days is resolving | Outbreak (PoisonPower.Trigger while resolving); Book Repair Knife DoomKills |
| CARD.GRAVEBLAST | Necrobinder | Returns a chosen discard card to hand; the move isn't credited | cards returned discard->hand | `CardPileCmd.Add(card, PileType.Hand)` while Graveblast is resolving | All For One `RecordCardChangedPiles` |
| CARD.HANG | Necrobinder | Each play stacks HangPower, which multiplies later Hang damage on that target; the multiplier share is hidden in the total | bonus damage from Hang multiplier; max multiplier reached | `HangPower.ModifyDamageMultiplicative` (cardSource is Hang) while Hang is resolving | Unleash `TotalOstyHpAttackBonus` (bonus split) |
| CARD.PROTECTOR | Necrobinder | Osty damage = 10 + Osty Max HP; the Max-HP bonus is the defining mechanic and isn't split out | Osty Max HP bonus damage; times applied | `Protector.OnPlay` (read `CalculatedDamage` multiplier before the attack, like Unleash) | Unleash `RecordUnleashOstyHpAttackBonus` |
| CARD.RATTLE | Necrobinder | Extra hits for each earlier Osty attack this turn; extra hits/damage not split out | extra hits from prior Osty attacks; bonus damage | `Rattle.OnPlay` (evaluate `CalculatedHits` in prefix) | Unleash bonus split / Death March context counter |
| CARD.RIGHT_HAND_HAND | Necrobinder | Returns itself discard->hand after any 2+ energy card is played; not counted | times returned to hand | `RightHandHand.AfterCardPlayedLate` -> `CardPileCmd.Add(this, PileType.Hand)` | Make It So `TimesSummonedToHand` |
| CARD.SOUL_STORM | Necrobinder | Damage = 9 + 4 per Soul in exhaust; the Soul-scaling bonus is the defining mechanic and isn't split out | Soul-scaling bonus damage; Souls counted at play | `SoulStorm.OnPlay` (read `CalculatedDamage` multiplier in prefix) | Unleash bonus split |
| CARD.SPUR | Necrobinder | Heals Osty 5; healing isn't tracked for cards (summon is) | Osty healing attempted/restored | `CreatureCmd.Heal(base.Owner.Osty, ...)` while Spur is resolving | ADR 0002 relic healing fields (Burning Blood) |
| CARD.SQUEEZE | Necrobinder | Damage = 25 + 5 per other OstyAttack card owned; the scaling bonus isn't split out | bonus damage from Osty-attack card count; count at play | `Squeeze.OnPlay` (read `CalculatedDamage` multiplier in prefix) | Unleash bonus split |
| CARD.THE_SCYTHE | Necrobinder | Each play permanently adds +5 damage (SavedProperty `IncreasedDamage`, also on DeckVersion); growth isn't shown | accumulated damage growth; bonus damage above base 13 | `TheScythe.OnPlay` / `BuffFromPlay` (read `IncreasedDamage` before/after) | Unleash bonus split + Death March context counter |
| CARD.UNDEATH | Necrobinder | Adds a clone of itself to discard; the creation and later plays of clones aren't credited | clones created; clone plays later | `CardPileCmd.AddGeneratedCardToCombat(CreateClone(), PileType.Discard)` while Undeath is resolving | Souls `SoulsAdded*` + RandomCardGeneration "generated cards later played" |

### (B) Gaps needing new mechanics
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.SCULPTING_STRIKE | Necrobinder | Gives Ethereal to a chosen card in hand; applying a keyword to another card isn't tracked | cards made Ethereal; later plays of those cards | `CardCmd.ApplyKeyword(card, CardKeyword.Ethereal)` while Sculpting Strike is resolving | none (closest: Drain Power upgraded-card payoff) |
| CARD.SEANCE | Necrobinder | Transforms chosen draw-pile cards into Souls (`CardCmd.TransformTo<Soul>`), not via AddGeneratedCards, so SoulsAdded likely misses them | cards transformed into Souls; transformed Souls later played | `CardCmd.TransformTo<Soul>` while Seance is resolving | none (transform; Entropy meta-power transforms is the nearest) |
| CARD.SNAP | Necrobinder | Gives Retain to a chosen card in hand; retaining other cards isn't tracked | cards given Retain; turns those cards were actually retained | `CardCmd.ApplyKeyword(card, CardKeyword.Retain)` while Snap is resolving | none |
| CARD.TRANSFIGURE | Necrobinder | Gives a hand card +1 replay (`BaseReplayCount++`) and +1 cost this combat; cost change on another card plus a granted replay | cards transfigured; extra plays from granted replays; extra energy paid | `Transfigure.OnPlay` after `CardSelectCmd.FromHand` (direct field writes, no command); payoff via `Hook.ModifyCardPlayCount` | partial: ReplayExtraPlay reasons |

### (P) Payoff via power (flag for power audit)
| Card id | pool | power id(s) | note |
|---|---|---|---|
| CARD.BLIGHT_STRIKE | Necrobinder | POWER.DOOM_POWER | Doom equal to damage dealt; Doom deaths not credited |
| CARD.CACOPHONY | Necrobinder | POWER.CACOPHONY_POWER | `AfterCardDrawn` -> `CreatureCmd.Damage` with no card source |
| CARD.CALCIFY | Necrobinder | POWER.CALCIFY_POWER | `ModifyDamageAdditive` bonus (Osty damage) not split |
| CARD.COUNTDOWN | Necrobinder | POWER.COUNTDOWN_POWER, POWER.DOOM_POWER | applies Doom each turn start with cardSource null |
| CARD.DEATHBRINGER | Necrobinder | POWER.DOOM_POWER | AoE Doom (Weak part covered) |
| CARD.DELAY | Necrobinder | POWER.ENERGY_NEXT_TURN_POWER | next-turn energy (block covered) |
| CARD.DEMESNE | Necrobinder | POWER.DEMESNE_POWER | `ModifyHandDraw` / `ModifyMaxEnergy` each turn |
| CARD.DEVOUR_LIFE | Necrobinder | POWER.DEVOUR_LIFE_POWER | `AfterCardPlayed` -> `OstyCmd.Summon` (non-card summon, power not credited) |
| CARD.END_OF_DAYS | Necrobinder | POWER.DOOM_POWER | also in (A); leftover Doom deaths later |
| CARD.FORBIDDEN_GRIMOIRE | Necrobinder | POWER.FORBIDDEN_GRIMOIRE_POWER | `AfterCombatEnd` payoff |
| CARD.FRIENDSHIP | Necrobinder | POWER.FRIENDSHIP_POWER, POWER.STRENGTH_POWER | +max energy each turn; self Strength loss is the cost |
| CARD.HAUNT | Necrobinder | POWER.HAUNT_POWER | `AfterCardPlayed` -> unblockable `CreatureCmd.Damage` with null source |
| CARD.INVOKE | Necrobinder | POWER.SUMMON_NEXT_TURN_POWER, POWER.ENERGY_NEXT_TURN_POWER | next-turn summon goes to `RecordNonCardOstySummon` (not credited); next-turn energy |
| CARD.LETHALITY | Necrobinder | POWER.LETHALITY_POWER | `ModifyDamageMultiplicative` bonus not split |
| CARD.NECRO_MASTERY | Necrobinder | POWER.NECRO_MASTERY_POWER | `AfterCurrentHpChanged` (Osty HP loss) -> AoE damage, power-sourced (summon part covered) |
| CARD.NEGATIVE_PULSE | Necrobinder | POWER.DOOM_POWER | AoE Doom (block covered) |
| CARD.NO_ESCAPE | Necrobinder | POWER.DOOM_POWER | Doom scaling with existing Doom |
| CARD.OBLIVION | Necrobinder | POWER.OBLIVION_POWER, POWER.DOOM_POWER | `AfterCardPlayed` applies Doom per card played with cardSource null |
| CARD.PAGESTORM | Necrobinder | POWER.PAGESTORM_POWER | `AfterCardDrawn` -> extra `CardPileCmd.Draw` |
| CARD.REAPER_FORM | Necrobinder | POWER.REAPER_FORM_POWER, POWER.DOOM_POWER | `AfterDamageGiven` applies Doom = damage x amount |
| CARD.SCOURGE | Necrobinder | POWER.DOOM_POWER | Doom (draw covered) |
| CARD.SENTRY_MODE | Necrobinder | POWER.SENTRY_MODE_POWER | `BeforeHandDraw` -> `AddGeneratedCardToCombat` to hand each turn |
| CARD.SHROUD | Necrobinder | POWER.SHROUD_POWER | `AfterPowerAmountChanged` -> block with null source |
| CARD.SIC_EM | Necrobinder | POWER.SIC_EM_POWER | `AfterDamageGiven` (Osty hits target) -> `OstyCmd.Summon` from the power (not credited) |
| CARD.SLEIGHT_OF_FLESH | Necrobinder | POWER.SLEIGHT_OF_FLESH_POWER | `AfterPowerAmountChanged` -> damage to debuffed enemy |
| CARD.SOULBOUND | Necrobinder | POWER.SOULBOUND_POWER | `AfterCardGeneratedForCombat` -> Souls to the ally's draw pile |
| CARD.SPIRIT_OF_ASH | Necrobinder | POWER.SPIRIT_OF_ASH_POWER | `BeforeCardPlayed` -> block with null source |
| CARD.UNDERWORLD | Necrobinder | POWER.UNDERWORLD_POWER, POWER.DOOM_POWER | `AfterDamageGiven` applies Doom = damage this turn |
| CARD.VEILPIERCER | Necrobinder | POWER.VEILPIERCER_POWER | `TryModifyEnergyCostInCombatLate` discount on next card (damage covered) |

### Covered / excluded
- Generic (1): CARD.AFTERLIFE, CARD.BANSHEES_CRY (own cost reduction visible as energy spent), CARD.BODYGUARD, CARD.BONE_SHARDS, CARD.BORROWED_TIME (energy gain; BorrowedTimePower is a self-cost drawback), CARD.BURY, CARD.CAPTURE_SPIRIT (`CreatureCmd.Damage` overload with cardSource + Souls), CARD.CLEANSE (summon + other-card exhaust), CARD.DEBILITATE (enemy debuff that amplifies Weak/Vulnerable), CARD.DEFEND_NECROBINDER, CARD.DEFILE, CARD.DEFY, CARD.DIRGE (X summons + Souls), CARD.ENFEEBLING_TOUCH (temporary enemy Strength loss), CARD.ERADICATE, CARD.FEAR, CARD.FETCH, CARD.FLATTEN (own cost-to-0 visible as energy spent), CARD.GLIMPSE_BEYOND, CARD.GRAVE_WARDEN, CARD.HIGH_FIVE, CARD.LEGION_OF_BONE, CARD.MELANCHOLY (own cost reduction visible), CARD.MISERY (debuff spread via Apply/ModifyAmount both write PowerReceived), CARD.NEUROSURGE (energy + draw; NeurosurgePower is a self-Doom drawback), CARD.PARSE, CARD.POKE, CARD.PULL_AGGRO, CARD.PUTREFY, CARD.REANIMATE, CARD.REAP, CARD.REAVE, CARD.SACRIFICE (block from Osty Max HP), CARD.SEVERANCE, CARD.SHARED_FATE, CARD.SOW, CARD.STRIKE_NECROBINDER, CARD.TIMES_UP (all of its damage comes from target Doom, so the total is the payoff), CARD.WISP
- Card-specific (2): CARD.CALL_OF_THE_VOID (meta-power family), CARD.DANSE_MACABRE (meta-power outcomes), CARD.DEATH_MARCH (cards-drawn-this-turn context), CARD.DRAIN_POWER (cards upgraded + later plays), CARD.PULL_FROM_BELOW (ethereal-played context), CARD.UNLEASH (Osty-HP bonus damage split)
- Excluded: none


## Regent (91 cards checked)

Verified in Core/RunTracker.cs: Forge is credited per card via `RecordForgeGranted` (from `HookAfterForgePatch` on `Hook.AfterForge`, only when `source is CardModel`, so forge from a power such as FurnacePower/HammerTimePower is not credited to the card). Stars generated (`TotalStarsGenerated`, owner-guarded, resolving play) and stars spent (`TotalStarsSpent`) are generic. Sovereign Blade is a pooled deck-view meta-card (`RecordSovereignBladeGenerated`/`RecordSovereignBladeForged`, event `sovereign_blade_forged`). Energy/stars recorders reject gains whose target player is not the source card's owner, so ally-targeted gains are dropped.

### (A) Gaps that fit an existing pattern
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.BEGONE | Regent | Transforms a chosen hand card into Minion Strike; transform not credited | cards actually transformed; later plays of the Minion Strikes it made | `Begone.OnPlay` → `CardCmd.Transform` while Begone resolves | Entropy transforms (EntropyPowerStatsPatch arm/disarm window) |
| CARD.GUARDS | Regent | Transforms any number of hand cards into Minion Sacrifice; not credited | cards actually transformed; later plays of the Minion Sacrifices | `Guards.OnPlay` → `CardCmd.Transform` while Guards resolves | Entropy transforms |
| CARD.CHARGE | Regent | Transforms up to 2 draw-pile cards into Minion Dive Bomb; not credited | cards actually transformed; later plays of the Dive Bombs | `Charge.OnPlay` → `CardCmd.TransformTo<MinionDiveBomb>` while resolving | Entropy transforms |
| CARD.COLLISION_COURSE | Regent | Adds a Debris token to hand; creating card gets nothing | Debris added to hand | `CardPileCmd.AddGeneratedCardToCombat` while CollisionCourse resolves | Souls (`SoulsAddedToHand`) |
| CARD.CRASH_LANDING | Regent | Fills the hand with Debris; count not credited | Debris added to hand (per play / total) | `CardPileCmd.AddGeneratedCardsToCombat` while CrashLanding resolves | Souls (`SoulsAddedToHand`) |
| CARD.HEIRLOOM_HAMMER | Regent | Clones a chosen colorless hand card into hand; copy creation not credited | copies created; later plays of the copies | `CardPileCmd.AddGeneratedCardToCombat` (after `CardSelectCmd.FromHand` + `CreateClone`) while resolving | Souls + RandomCardGeneration "generated cards later played" |
| CARD.COSMIC_INDIFFERENCE | Regent | Puts a chosen discard card on top of draw; causer gets nothing (TimesPlacedOnTop credits the moved card) | cards put on top of draw | `CardPileCmd.Add(..., PileType.Draw, CardPilePosition.Top)` while resolving | All For One (`RecordCardChangedPiles` while resolving) |
| CARD.GLIMMER | Regent | Draw 3 then put a hand card on top of draw; the put-back is not credited to Glimmer | cards put back on top | `CardSelectCmd.FromHand` → `CardPileCmd.Add(..., Top)` while resolving | All For One |
| CARD.PHOTON_CUT | Regent | Draw then put a hand card on top of draw; put-back not credited | cards put back on top | `CardPileCmd.Add(..., Top)` while PhotonCut resolves | All For One |
| CARD.TUTOR | Regent | (Multiplayer) moves a chosen card from an ally's draw pile to hand; not credited | cards tutored to hand | `Tutor.OnPlay` → `CardSelectCmd.FromCombatPile` + `CardPileCmd.Add(..., Hand)` | All For One |
| CARD.SUMMON_FORTH | Regent | Recalls all Sovereign Blades to hand (forge is generic); recall not credited. Only existing wiring is the Osty-summon tooltip home in CardHoverTooltipPatch.IsOstySummonStatsHome (`card is SummonForth`), which this Regent card never fills | Sovereign Blades recalled to hand | `CardPileCmd.Add(cards, PileType.Hand)` while SummonForth resolves | All For One (`RecordCardChangedPiles`) |
| CARD.DECISIONS_DECISIONS | Regent | Auto-plays a chosen Skill 3 times; those plays credit the skill, DecisionsDecisions gets nothing | skills auto-played; total auto-plays caused | `CardCmd.AutoPlay` while DecisionsDecisions.OnPlay resolves | Stampede (`StampedeCardAutoPlayStatsPatch` on `CardCmd.AutoPlay`) |
| CARD.BOMBARDMENT | Regent | Auto-plays itself from the exhaust pile each turn; auto-plays not split from manual plays | times auto-played from exhaust; damage from those auto-plays | `Bombardment.AfterAutoPrePlayPhaseEnteredEarly` → `CardCmd.AutoPlay` | Stampede auto-played / Make It So `TimesSummonedToHand` |
| CARD.I_AM_INVINCIBLE | Regent | Auto-plays itself when on top of the draw pile; not split from manual plays | times auto-played from draw top; block from auto-plays | `IAmInvincible.AfterAutoPostPlayPhaseEntered` → `CardPileCmd.AutoPlayFromDrawPile` | Stampede / Make It So |
| CARD.PARTICLE_WALL | Regent | Returns itself to hand after play (GetResultLocationForCardPlay); not counted | times returned to hand | `ParticleWall.GetResultLocationForCardPlay` postfix (Discard→Hand) | Make It So `TimesSummonedToHand` |
| CARD.SHINING_STRIKE | Regent | Goes to top of draw instead of discard; source pile is Play, so TimesPlacedOnTop* never fires | times returned to top of draw | `ShiningStrike.GetResultLocationForCardPlay` postfix (Discard→Draw Top) | Make It So `TimesSummonedToHand` |
| CARD.KINGLY_PUNCH | Regent | Gains +Increase damage each time drawn (persistent ExtraDamage); growth is the card's mechanic | accumulated bonus damage; bonus portion of damage dealt | `KinglyPunch.AfterCardDrawn` (ExtraDamage) + split in OnPlay damage | Unleash `TotalOstyHpAttackBonus` |
| CARD.HEAVENLY_DRILL | Regent | X-cost hit count doubles when X >= 4; doubling is the defining mechanic | times threshold met; extra hits / damage from doubling | `HeavenlyDrill.OnPlay` (ResolveEnergyXValue vs Energy var) | Unleash bonus split |
| CARD.LUNAR_BLAST | Regent | Hits = skills played this turn (base 0); no context for hits per play | skills-this-turn at play (avg hits) | `LunarBlast.OnPlay` (CalculatedHits) | Death March / Make It So skills-this-turn context |
| CARD.RADIATE | Regent | Hits = stars gained this turn (base 0); no context for hits per play | stars-gained-this-turn at play (avg hits) | `Radiate.OnPlay` (CalculatedHits over StarsModifiedEntry) | Death March context counter |

### (B) Gaps needing new mechanics
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.CONSTELLATION | Regent | (Multiplayer) draw 1 + energy 1 + block 9 go to an ally; owner guards in RecordEnergyGained etc. drop cross-player gains | energy given to ally; cards ally drew; block given to ally | `Constellation.OnPlay` (CardPileCmd.Draw / PlayerCmd.GainEnergy / CreatureCmd.GainBlock on target player) | none (cross-player attribution) |

### (P) Payoff via power (flag for power audit)
| Card id | pool | power id(s) | note |
|---|---|---|---|
| CARD.ARSENAL | Regent | POWER.ARSENAL_POWER | Strength per card generated (AfterCardGeneratedForCombat) |
| CARD.BLACK_HOLE | Regent | POWER.BLACK_HOLE_POWER | AoE unpowered damage on stars gained / card played (CreatureCmd.Damage, no CardSource) |
| CARD.CHILD_OF_THE_STARS | Regent | POWER.CHILD_OF_THE_STARS_POWER | Block per star spent (AfterStarsSpent) |
| CARD.CONQUEROR | Regent | POWER.CONQUEROR_POWER | Debuff doubling Sovereign Blade damage on target; forge itself is generic |
| CARD.CONVERGENCE | Regent | POWER.RETAIN_HAND_POWER, POWER.ENERGY_NEXT_TURN_POWER, POWER.STAR_NEXT_TURN_POWER | Retain hand + next-turn energy/stars |
| CARD.FOREGONE_CONCLUSION | Regent | POWER.FOREGONE_CONCLUSION_POWER | Next turn: tutor cards from draw pile to hand (BeforeHandDraw) |
| CARD.FURNACE | Regent | POWER.FURNACE_POWER | Forge each turn start; source is the power so RecordForgeGranted skips it |
| CARD.GENESIS | Regent | POWER.GENESIS_POWER | Stars each energy reset |
| CARD.GLITTERSTREAM | Regent | POWER.BLOCK_NEXT_TURN_POWER | Next-turn block; immediate block generic |
| CARD.GLOW | Regent | POWER.DRAW_CARDS_NEXT_TURN_POWER | Next-turn draw; stars + draw now are generic |
| CARD.GUIDING_STAR | Regent | POWER.DRAW_CARDS_NEXT_TURN_POWER | Next-turn draw; damage generic |
| CARD.HAMMER_TIME | Regent | POWER.HAMMER_TIME_POWER | (Multiplayer) mirrors forge to teammates (AfterForge) |
| CARD.HEGEMONY | Regent | POWER.ENERGY_NEXT_TURN_POWER | Next-turn energy; damage generic |
| CARD.HIDDEN_CACHE | Regent | POWER.STAR_NEXT_TURN_POWER | Next-turn stars; immediate star generic |
| CARD.MONARCHS_GAZE | Regent | POWER.MONARCHS_GAZE_POWER | Applies strength-down to enemies on damage dealt (AfterDamageGiven) |
| CARD.MONOLOGUE | Regent | POWER.MONOLOGUE_POWER | Temporary Strength per card played this turn |
| CARD.NEUTRON_AEGIS | Regent | POWER.PLATING_POWER | Plating block each turn |
| CARD.ORBIT | Regent | POWER.ORBIT_POWER | Energy per 4 energy spent (AfterEnergySpent) |
| CARD.PALE_BLUE_DOT | Regent | POWER.PALE_BLUE_DOT_POWER | Next-turn draw after 5 attacks in a turn |
| CARD.PARRY | Regent | POWER.PARRY_POWER | Inert marker; Sovereign Blade reads it for block (payoff lands on pooled Sovereign Blade) |
| CARD.PATTER | Regent | POWER.VIGOR_POWER | Vigor bonus on next attack; block generic |
| CARD.PILLAR_OF_CREATION | Regent | POWER.PILLAR_OF_CREATION_POWER | Block per card generated (AfterCardGeneratedForCombat) |
| CARD.PLOT | Regent | POWER.DRAW_CARDS_NEXT_TURN_POWER | (Multiplayer) next-turn draw on teammates |
| CARD.REFINE_BLADE | Regent | POWER.ENERGY_NEXT_TURN_POWER | Next-turn energy; forge generic |
| CARD.REFLECT | Regent | POWER.REFLECT_POWER | Returns blocked damage to attacker (AfterDamageReceived); block generic |
| CARD.RESONANCE | Regent | POWER.STRENGTH_POWER | Self +Strength, enemies -1 Strength (enemy part is debuff-only) |
| CARD.ROYALTIES | Regent | POWER.ROYALTIES_POWER | Extra gold reward at combat end (AfterCombatEnd → GoldReward) |
| CARD.SEEKING_EDGE | Regent | POWER.SEEKING_EDGE_POWER | Inert marker changing Sovereign Blade behaviour; forge generic |
| CARD.SWORD_SAGE | Regent | POWER.SWORD_SAGE_POWER | Adds replays to Sovereign Blades (BaseReplayCount) |
| CARD.TERRAFORMING | Regent | POWER.VIGOR_POWER | Vigor bonus on next attack |
| CARD.THE_SEALED_THRONE | Regent | POWER.THE_SEALED_THRONE_POWER | Stars before each card played |
| CARD.TYRANNY | Regent | POWER.TYRANNY_POWER | Extra hand draw + exhaust-from-hand each turn |
| CARD.VOID_FORM | Regent | POWER.VOID_FORM_POWER | Energy/star cost reductions; card also ends the turn (PlayerCmd.EndTurn) |

### Covered / excluded
- Generic (1): CARD.ALIGNMENT, CARD.ASTRAL_PULSE, CARD.BEAT_INTO_SHAPE, CARD.BIG_BANG, CARD.BULWARK, CARD.CELESTIAL_MIGHT, CARD.CLOAK_OF_STARS, CARD.COMET, CARD.CRESCENT_SPEAR, CARD.CRUSH_UNDER, CARD.DEFEND_REGENT, CARD.DEVASTATE, CARD.DYING_STAR, CARD.FALLING_STAR, CARD.GAMMA_BLAST, CARD.GATHER_LIGHT, CARD.KINGLY_KICK, CARD.KNOCKOUT_BLOW, CARD.KNOW_THY_PLACE, CARD.METEOR_SHOWER, CARD.PROPHESIZE, CARD.ROYAL_GAMBLE, CARD.SEVEN_STARS, CARD.SOLAR_STRIKE, CARD.SPOILS_OF_BATTLE, CARD.STARDUST, CARD.STRIKE_REGENT, CARD.THE_SMITH, CARD.VENERATE, CARD.WROUGHT_IN_WAR
- Card-specific (2): CARD.BUNDLE_OF_JOY (random generation registry), CARD.LARGESSE (random generation registry), CARD.MANIFEST_AUTHORITY (random generation registry; block generic), CARD.QUASAR (random generation registry, choose-a-card), CARD.MAKE_IT_SO (times summoned to hand + skills-this-turn), CARD.SUPERMASSIVE (cards created this combat context), CARD.SPECTRUM_SHIFT (MetaPowerRegistry + random generation via SpectrumShiftPower)
- Excluded: none


## Colorless (65 cards checked)

### (A) Gaps that fit an existing pattern
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.ANOINTED | Colorless | Moves random Rare cards draw→hand (up to hand space); the move is not credited | rare cards pulled to hand; later plays of pulled cards | Anointed.OnPlay → CardPileCmd.Add(cards, PileType.Hand) while resolving (RecordCardChangedPiles) | All For One |
| CARD.BEAT_DOWN | Colorless | Auto-plays N random Attacks from discard; autoplays (and their damage) credit the played cards, not Beat Down | attacks auto-played; damage dealt by auto-played attacks | CardCmd.AutoPlay guarded by BeatDown.OnPlay resolving (sum DamageReceivedEntry of the autoplays) | Stampede (attacks auto-played, MetaPowerRegistry) |
| CARD.CATASTROPHE | Colorless | Auto-plays N random cards from draw pile; nothing credited to Catastrophe | cards auto-played (by type); damage/block from auto-played cards | CardCmd.AutoPlay guarded by Catastrophe.OnPlay resolving | Stampede (attacks auto-played) |
| CARD.BOLAS | Colorless | Returns itself to hand at start of next turn after being played | times returned to hand | Bolas.BeforeHandDraw → CardPileCmd.Add(this, PileType.Hand) | Make It So (TimesSummonedToHand) |
| CARD.THRUMMING_HATCHET | Colorless | Returns itself to hand at start of next turn after being played | times returned to hand | ThrummingHatchet.BeforeHandDraw → CardPileCmd.Add(this, PileType.Hand) | Make It So (TimesSummonedToHand) |
| CARD.BELIEVE_IN_YOU | Colorless | Energy is granted to an ally; RunTracker.RecordEnergyGained drops it (owner != target player) | energy given to allies | BelieveInYou.OnPlay → PlayerCmd.GainEnergy(…, cardPlay.Target.Player) (PlayerGainEnergyPatch; allow ally receiver while this card resolves) | generic energy-generated ledger |
| CARD.LIFT | Colorless | Block goes to an ally; block ledger returns early for non-tracked player receivers | block given to allies | Lift.OnPlay → CreatureCmd.GainBlock(cardPlay.Target, …) (BlockGainedEntry path; allow ally receiver for this card) | generic TotalBlockGained ledger |
| CARD.RALLY | Colorless | Block to all allies; only the owner's share is credited | block given to allies (excluding self) | Rally.OnPlay → CreatureCmd.GainBlock(item, …) per teammate | generic TotalBlockGained ledger |
| CARD.GANG_UP | Colorless | Damage scales +X per ally powered attack on target this turn (defining mechanic); bonus split not shown | ally hits counted; bonus damage from ally hits | GangUp.OnPlay prefix (CalculatedDamageVar multiplier = ally DamageReceivedEntry count) + DamageReceivedEntry total | Unleash (TotalOstyHpAttackBonus) |
| CARD.REND | Colorless | Damage scales +X per non-temporary debuff on target (defining mechanic); bonus split not shown | debuffs counted on target; bonus damage from debuffs | Rend.OnPlay prefix (count target.Powers via ShouldCountPower) + DamageReceivedEntry total | Unleash (TotalOstyHpAttackBonus) |
| CARD.HAND_OF_GREED | Colorless | Fatal: gains gold if the hit kills; gold is not tracked | fatal kills; gold gained | HandOfGreed.OnPlay → PlayerCmd.GainGold while resolving | Debt (gold delta) |
| CARD.HIDDEN_GEM | Colorless | Adds +Replay to a random draw-pile card (BaseReplayCount); resulting extra plays credit the target, not Hidden Gem | replay charges granted; extra plays actually made by marked cards | HiddenGem.OnPlay (BaseReplayCount delta on chosen card) + Hook.ModifyCardPlayCount replay attribution for the marked instance | Drain Power (mark card, credit later plays) |
| CARD.SECRET_TECHNIQUE | Colorless | Tutors a chosen Skill draw→hand | skills fetched to hand; later plays of fetched cards | CardSelectCmd.FromCombatPile + CardPileCmd.Add(cardModel, PileType.Hand) while SecretTechnique.OnPlay resolves | All For One |
| CARD.SECRET_WEAPON | Colorless | Tutors a chosen Attack draw→hand | attacks fetched to hand; later plays of fetched cards | CardSelectCmd.FromCombatPile + CardPileCmd.Add(cardModel, PileType.Hand) while SecretWeapon.OnPlay resolves | All For One |
| CARD.SEEKER_STRIKE | Colorless | After damage, choose 1 of 3 random draw-pile cards → hand; damage covered, tutor not | cards fetched to hand; offered/picked | CardSelectCmd.FromCombatPile + CardPileCmd.Add(…, PileType.Hand) while SeekerStrike.OnPlay resolves | All For One / Discovery (offered/picked) |
| CARD.THINKING_AHEAD | Colorless | Draw covered; putting a hand card on top of draw is credited to the moved card, not Thinking Ahead | cards put on top of draw | CardSelectCmd.FromHand + CardPileCmd.Add(…, PileType.Draw, CardPilePosition.Top) while ThinkingAhead.OnPlay resolves | All For One (RecordCardChangedPiles) |

### (B) Gaps needing new mechanics
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.THE_BALL | Colorless | +Increase damage per play persists on the instance, and the card is passed to a random teammate's draw pile (cross-player instance transfer) | times passed to teammate; bonus damage from prior plays | TheBall.GetResultLocationForCardPlay postfix (player change); TheBall.OnPlay prefix (DamageVar base vs. ExtraDamageFromPlays) | none (cross-player card ledger) |

### (P) Payoff via power (flag for power audit)
| Card id | pool | power id(s) | note |
|---|---|---|---|
| CARD.AUTOMATION | Colorless | POWER.AUTOMATION_POWER | Energy every 10 cards drawn (AutomationPower.AfterCardDrawn → PlayerCmd.GainEnergy) |
| CARD.BEACON_OF_HOPE | Colorless | POWER.BEACON_OF_HOPE_POWER | Shares 50% of own block gained to teammates (AfterBlockGained) |
| CARD.COORDINATE | Colorless | POWER.COORDINATE_POWER | Temporary Strength to an ally (TemporaryStrengthPower) |
| CARD.ETERNAL_ARMOR | Colorless | POWER.PLATING_POWER | Plating end-of-turn block |
| CARD.EQUILIBRIUM | Colorless | POWER.RETAIN_HAND_POWER | Block covered; retained hand (RetainHandPower.ShouldFlush) not credited |
| CARD.SALVO | Colorless | POWER.RETAIN_HAND_POWER | Damage covered; retained hand not credited |
| CARD.FASTEN | Colorless | POWER.FASTEN_POWER | Extra block on Defend-tagged cards (ModifyBlockAdditive) |
| CARD.INTERCEPT | Colorless | POWER.COVERED_POWER, POWER.INTERCEPT_POWER | Own block covered; redirects ally's incoming powered attacks to self (damage absorbed for ally not credited) |
| CARD.MAYHEM | Colorless | POWER.MAYHEM_POWER | Auto-plays top of draw each turn (AfterAutoPrePlayPhaseEntered → CardPileCmd.AutoPlayFromDrawPile) |
| CARD.NOSTALGIA | Colorless | POWER.NOSTALGIA_POWER | First Attack/Skill(s) each turn go to top of draw (ModifyCardPlayResultLocation) |
| CARD.PREP_TIME | Colorless | POWER.PREP_TIME_POWER, POWER.VIGOR_POWER | Vigor each turn start (AfterSideTurnStart) |
| CARD.PROLONG | Colorless | POWER.BLOCK_NEXT_TURN_POWER | Current block re-granted next turn (AfterBlockCleared) |
| CARD.PROWESS | Colorless | POWER.STRENGTH_POWER, POWER.DEXTERITY_POWER | Self Strength/Dexterity |
| CARD.ROLLING_BOULDER | Colorless | POWER.ROLLING_BOULDER_POWER | Growing AoE damage each turn start (AfterPlayerTurnStart → CreatureCmd.Damage, no card source) |
| CARD.STRATAGEM | Colorless | POWER.STRATAGEM_POWER | On shuffle, choose draw-pile card(s) → hand (AfterShuffle) |
| CARD.TAG_TEAM | Colorless | POWER.TAG_TEAM_POWER | Damage covered; next card played on target gets an extra play (ModifyCardPlayCount), not credited to Tag Team |
| CARD.THE_BOMB | Colorless | POWER.THE_BOMB_POWER | Delayed 40 AoE damage after N turns (BeforeSideTurnEnd → CreatureCmd.Damage) |

### Covered / excluded
- Generic (1): CARD.DARK_SHACKLES, CARD.DRAMATIC_ENTRANCE, CARD.FINESSE, CARD.FISTICUFFS, CARD.FLASH_OF_STEEL, CARD.GOLD_AXE, CARD.HUDDLE_UP, CARD.IMPATIENCE, CARD.KNOCKDOWN, CARD.MASTER_OF_STRATEGY, CARD.MIMIC, CARD.MIND_BLAST, CARD.OMNISLICE, CARD.PANIC_BUTTON, CARD.PRODUCTION, CARD.PURITY, CARD.RESTLESSNESS, CARD.SCRAWL, CARD.SHOCKWAVE, CARD.THE_GAMBIT, CARD.ULTIMATE_DEFEND, CARD.ULTIMATE_STRIKE, CARD.VOLLEY
- Card-specific (2): CARD.ALCHEMIZE (potions procured), CARD.CALAMITY (meta-power + random generation), CARD.DISCOVERY (offered/picked + discount), CARD.ENTROPY (meta-power transforms), CARD.JACK_OF_ALL_TRADES (random generation), CARD.JACKPOT (random generation), CARD.PANACHE (meta-power damage), CARD.SPLASH (offered/picked + discount)
- Excluded: none


## Event (28 cards checked)

### (A) Gaps that fit an existing pattern
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.APOTHEOSIS | Event | Upgrades every upgradable card in combat; upgrades not counted | cards actually upgraded; later plays of upgraded cards | `Apotheosis.OnPlay` loop -> `CardCmd.Upgrade` / `CardModel.UpgradeInternal` while Apotheosis resolving | Armaments / Drain Power |
| CARD.DUAL_WIELD | Event | Creates N clones of a chosen Attack/Power in hand (`selection.CreateClone()` + `CardPileCmd.AddGeneratedCardToCombat`); creation & copy usage untracked | copies created; copies later played (+ damage of copies) | `DualWield.OnPlay` (`CardSelectCmd.FromHand` result + `CardPileCmd.AddGeneratedCardToCombat` while resolving) | Souls `SoulsAddedToHand` + RandomCardGeneration "generated later played" |
| CARD.MAUL | Event | Each play permanently buffs damage of all Mauls (`BuffFromMaulPlay`, `ExtraDamageFromMaulPlays`); bonus part of damage not split | bonus damage from prior Maul plays; current bonus (context) | `Maul.OnPlay` (read `ExtraDamageFromMaulPlays` before attack) / `Maul.BuffFromMaulPlay` | Unleash `TotalOstyHpAttackBonus` |
| CARD.NEOWS_FURY | Event | Returns up to 2 chosen cards discard -> hand (`CardSelectCmd.FromCombatPile` + `CardPileCmd.Add(..., PileType.Hand)`) | cards returned to hand; later plays of returned cards | `NeowsFury.OnPlay` / `CardPileCmd.Add` while resolving (RecordCardChangedPiles) | All For One |
| CARD.WISH | Event | Tutors a chosen card draw pile -> hand (`CardSelectCmd.FromCombatPile` on Draw + `CardPileCmd.Add`) | cards tutored; tutored card later played | `Wish.OnPlay` / `CardPileCmd.Add` while resolving (RecordCardChangedPiles draw->hand) | All For One |

### (B) Gaps needing new mechanics
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.ENLIGHTENMENT | Event | Sets cost of every card in hand to 1 (turn / combat when upgraded) via `EnergyCost.SetThisTurnOrUntilPlayed` / `SetThisCombat(1, reduceOnly:true)`; cost changes on other cards untracked | cards discounted; energy actually saved on later plays | `Enlightenment.OnPlay` (snapshot hand costs before/after) + later play energy delta | none (closest: Discovery `EnergyDiscountTotal`) |
| CARD.WHISTLE | Event | Stuns target (`CreatureCmd.Stun(cardPlay.Target)`); enemy-intent cancellation not a power | enemies stunned; stunned intents skipped | `Whistle.OnPlay` / `CreatureCmd.Stun` while resolving | none (enemy-state) |

### (P) Payoff via power (flag for power audit)
| Card id | pool | power id(s) | note |
|---|---|---|---|
| CARD.APPARITION | Event | POWER.INTANGIBLE_POWER | self Intangible; damage prevented uncredited |
| CARD.CALTROPS | Event | POWER.THORNS_POWER | Thorns retaliation damage has null card source |
| CARD.FEEDING_FRENZY | Event | POWER.FEEDING_FRENZY_POWER | temporary Strength (5/7); extra damage uncredited |
| CARD.OUTMANEUVER | Event | POWER.ENERGY_NEXT_TURN_POWER | next-turn energy uncredited |
| CARD.REBOUND | Event | POWER.REBOUND_POWER | next card played goes on top of draw (`ModifyCardPlayResultLocation`); damage itself covered |
| CARD.RELAX | Event | POWER.DRAW_CARDS_NEXT_TURN_POWER, POWER.ENERGY_NEXT_TURN_POWER | block covered; next-turn draw/energy uncredited |
| CARD.TORIC_TOUGHNESS | Event | POWER.TORIC_TOUGHNESS_POWER | re-grants block for 2 turns (`AfterBlockCleared`); initial block covered |

### Covered / excluded
- Generic (1): CARD.BYRD_SWOOP, CARD.CLASH, CARD.ENTRENCH, CARD.EXTERMINATE, CARD.PECK, CARD.RIP_AND_TEAR, CARD.SQUASH, CARD.STACK
- Card-specific (2): CARD.ABUNDANCE (RandomCardGeneration registry), CARD.BRIGHTEST_FLAME (max HP lost), CARD.DISTRACTION (RandomCardGeneration), CARD.HELLO_WORLD (POWER.HELLO_WORLD_POWER meta-power + random gen), CARD.MAD_SCIENCE (RandomCardGeneration for Chaos rider; note: Expertise/Curious/Improvement/Choking riders apply STRENGTH/DEXTERITY/CURIOUS/IMPROVEMENT/STRANGLE powers whose payoff is uncredited), CARD.METAMORPHOSIS (RandomCardGeneration)
- Excluded: none

## Quest (4 cards checked)

### (A) Gaps that fit an existing pattern
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.SPOILS_MAP | Quest | Completing the quest grants 600 gold (`PlayerCmd.GainGold`) and removes the card; gold untracked | gold gained; quest completed (act/floor) | `SpoilsMap.OnQuestComplete` (gold delta around the Task) | Debt (gold delta on owner callback) |

### (B) Gaps needing new mechanics
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.BYRDONIS_EGG | Quest | Adds Hatch rest-site option that obtains Byrdpip relic; out-of-combat, no card stat | hatched (yes/floor); rest sites offered | `ByrdonisEgg.TryModifyRestSiteOptions` / `HatchRestSiteOption.OnSelect` | none (rest-site / out-of-combat) |
| CARD.DOWSING | Quest | Counts Unknown rooms entered (`RoomsEntered`), completes and `CardCmd.TransformTo<Abundance>` at 5 | rooms progressed; transformed into Abundance (floor) | `Dowsing.BeforeRoomEntered` | none (quest progress + transform) |
| CARD.LANTERN_KEY | Quest | Forces Act 3 Unknown rooms to Event and next event to WarHistorianRepy | unknown rooms forced to event; WarHistorianRepy reached | `LanternKey.ModifyUnknownMapPointRoomTypes` / `LanternKey.ModifyNextEvent` | none (map/event manipulation) |

### (P) Payoff via power (flag for power audit)
| Card id | pool | power id(s) | note |
|---|---|---|---|

### Covered / excluded
- Generic (1): none
- Card-specific (2): none
- Excluded: none

## Token (14 cards checked)

### (A) Gaps that fit an existing pattern
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|

### (B) Gaps needing new mechanics
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|

### (P) Payoff via power (flag for power audit)
| Card id | pool | power id(s) | note |
|---|---|---|---|
| CARD.DISINTEGRATION | Token | POWER.DISINTEGRATION_POWER | KnowledgeDemon choice token (Status type, never played); `OnChosen` applies power outside any card play, so not even the application is credited; payoff via `AfterSideTurnEndLate` |
| CARD.MIND_ROT | Token | POWER.MIND_ROT_POWER | KnowledgeDemon choice token; `OnChosen` -> hand-draw reduction (`ModifyHandDraw`) |
| CARD.SLOTH | Token | POWER.SLOTH_POWER | KnowledgeDemon choice token; `OnChosen` -> per-turn card-play cap (`BeforeCardPlayed`) |
| CARD.WASTE_AWAY | Token | POWER.WASTE_AWAY_POWER | KnowledgeDemon choice token; `OnChosen` -> max energy reduction (`ModifyMaxEnergy`) |

### Covered / excluded
- Generic (1): CARD.FUEL, CARD.GIANT_ROCK, CARD.LUMINESCE, CARD.MINION_DIVE_BOMB, CARD.MINION_SACRIFICE, CARD.MINION_STRIKE, CARD.SWEEPING_GAZE (Osty attack via `FromOsty(..., this, cardPlay)` carries CardSource)
- Card-specific (2): CARD.SHIV (pooled meta-card, DeckViewMetaCardKind.Shiv), CARD.SOUL (pooled meta-card, DeckViewMetaCardKind.Soul; its draw is generic), CARD.SOVEREIGN_BLADE (pooled meta-card, DeckViewMetaCardKind.SovereignBlade)
- Excluded: none
- Note: no other Token card is pooled; Giant Rock/Fuel/Luminesce/Minion*/Sweeping Gaze are per-instance generic aggregates, and their CREATORS get no "created" credit (see creating cards' pools).

## Deprived (1 cards checked)

### (A) Gaps that fit an existing pattern
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|

### (B) Gaps needing new mechanics
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|

### (P) Payoff via power (flag for power audit)
| Card id | pool | power id(s) | note |
|---|---|---|---|

### Covered / excluded
- Generic (1): none
- Card-specific (2): none
- Excluded: T (parse artifact of `MockCard<T>` in DeprivedCardPool, which is `IsMock => true` and contains only Mock* cards)

## Curse (18 cards checked)

### (A) Gaps that fit an existing pattern
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.DOUBT | Curse | Turn end in hand applies 1 Weak to self (`PowerCmd.Apply<WeakPower>` with cardSource=this) outside any card play, so AppliedEffects never records it | turns ended in hand; Weak self-applied | `Doubt.OnTurnEndInHand` | Debt / Normality (turn-end-in-hand) |
| CARD.SHAME | Curse | Turn end in hand applies 1 Frail to self (`PowerCmd.Apply<FrailPower>`) outside any card play; not recorded | turns ended in hand; Frail self-applied | `Shame.OnTurnEndInHand` | Debt / Normality (turn-end-in-hand) |
| CARD.ENTHRALLED | Curse | Eternal 2-cost curse whose `ShouldPlay` blocks every other card while in hand; only relic tooltip reads its pooled aggregate | turns it locked the hand; energy spent to clear it | `Enthralled.ShouldPlay` (count false returns per turn) + `Hook.BeforeSideTurnEnd` while in hand | Normality (turns ended in hand) |

### (B) Gaps needing new mechanics
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|

### (P) Payoff via power (flag for power audit)
| Card id | pool | power id(s) | note |
|---|---|---|---|

### Covered / excluded
- Generic (1): CARD.BAD_LUCK (`OnTurnEndInHand` -> `CreatureCmd.Damage(..., this, null)` on owner; RecordDamageFromCard credits TotalHpLost by CardSource), CARD.DECAY (same, 2 dmg), CARD.REGRET (same, damage = hand size captured in `BeforeSideTurnEnd`)
- Card-specific (2): CARD.DEBT (turn-end gold lost/blocked), CARD.NORMALITY (turns ended in hand + excess energy)
- Excluded: CARD.ASCENDERS_BANE (no effect), CARD.CLUMSY (no effect), CARD.CURSE_OF_THE_BELL (no effect), CARD.FOLLY (no effect), CARD.GREED (no card behaviour; referenced only by Cursed Pearl relic tooltip), CARD.GUILTY (only self-removal countdown `AfterCombatEnd` -> `CardPileCmd.RemoveFromDeck` after 5 combats), CARD.INJURY (no effect), CARD.POOR_SLEEP (no effect, Retain), CARD.SPORE_MIND (playable but no OnPlay; play/exhaust generic), CARD.WRITHE (no effect, Innate)

## Status (12 cards checked)

### (A) Gaps that fit an existing pattern
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|
| CARD.VOID | Status | On draw loses 1 energy (`PlayerCmd.LoseEnergy`); energy loss not tracked anywhere | energy actually lost; times drawn while energy > 0 | `Void.AfterCardDrawn` (card == this; energy delta around Task) | Debt (resource delta on owner callback) |

### (B) Gaps needing new mechanics
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|

### (P) Payoff via power (flag for power audit)
| Card id | pool | power id(s) | note |
|---|---|---|---|

### Covered / excluded
- Generic (1): CARD.BECKON (`OnTurnEndInHand` self-damage w/ CardSource -> TotalHpLost), CARD.BURN (same), CARD.INFECTION (same), CARD.TOXIC (same), CARD.WITHER (same; `FakeUpgrade` damage growth is enemy-driven), CARD.SLIMED (draw 1), CARD.FRANTIC_ESCAPE (`PowerCmd.ModifyAmount` on enemy SandpitPower fires History.PowerReceived while resolving -> AppliedEffects POWER.SANDPIT_POWER; its own `EnergyCost.AddThisCombat(1)` shows in energy spent)
- Card-specific (2): none (all Status types are also pooled into per-definition status meta-cards via GetStatusDeckViewCardsLocked)
- Excluded: CARD.DAZED (no effect), CARD.DEBRIS (empty OnPlay; play/exhaust generic), CARD.SOOT (no effect), CARD.WOUND (no effect)

## Deprecated (1 cards checked)

### (A) Gaps that fit an existing pattern
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|

### (B) Gaps needing new mechanics
| Card id | pool | gap (one line) | proposed stats | hook | analog |
|---|---|---|---|---|---|

### (P) Payoff via power (flag for power audit)
| Card id | pool | power id(s) | note |
|---|---|---|---|

### Covered / excluded
- Generic (1): none
- Card-specific (2): none
- Excluded: CARD.DEPRECATED_CARD (placeholder for missing/deprecated cards; draw 1 + RemoveFromDeck)


