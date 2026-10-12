# SpireLens relic stat coverage audit

Worktree: `D:\repos\spirelens\.claude\worktrees\sad-napier-14962f` (HEAD `04ecbe6`).
Game source: decompiled `MegaCrit.Sts2.Core.Models.Relics` (300 files) + `MegaCrit.Sts2.Core.Models.RelicPools`.

## 1. How SpireLens decides a relic "has stats"

There is **no registry, attribute or dictionary**. Coverage is a hand-written
`if (relicModel is X) { title = ...; body = Build...; return true; }` chain:

| Step | Location | What it does |
|---|---|---|
| Entry (in-run inventory hover) | `Core/Patches/NativeHoverTipAugmentationPatch.cs:243` -> `RelicHoverShowPatch.TryBuildNativeHoverTip` (`Core/Patches/RelicHoverTooltipPatch.cs:113`) | Builds the native stats hover tip only when the body builder returns true. |
| Aggregate lookup | `RelicHoverTooltipPatch.cs:167` `TryBuildInventoryBodyBBCode`, `:292` `GetStatsAggregateId` | Reads `RunData.RelicAggregates[relicId]`. `GetStatsAggregateId` aliases FakeAnchor->ANCHOR, FakeStrikeDummy->STRIKE_DUMMY, SwordOfJade->SWORD_OF_STONE, etc. |
| **The decision** | `RelicHoverTooltipPatch.cs:531-1802` `TryBuildBodyBBCodeWithLiveActivationCounts` | ~180 `relicModel is X` / `IsRelicModel(relicModel, "MegaCrit...X")` branches. **Falls through to `return false;` at line 1802** - no tooltip at all for any relic not listed. |
| Wax overlay (only "generic" path) | `RelicHoverTooltipPatch.cs:276-286` (`ApplyWaxRelicPresentation`, `:5360`); same in `Core/RunHistoryStatsContext.cs:310` | If `relicModel.IsWax`, appends Toy Box rows (Wax bestowed / Wax melted / Floors until melt) and returns **true even when the relic-specific builder returned false**. |
| Other surfaces | `Core/Patches/RelicCompendiumStatsPatch.cs:112,124,159`, `Core/RunHistoryStatsContext.cs:299` | Compendium, main-menu continue and run-history all call the same `TryBuildBodyBBCode` chain - same coverage. |
| Merchant hover | `RelicHoverTooltipPatch.cs:137` `TryBuildMerchantHoverTip` | Mystic Lighter only. |

Things that are **not** a "has stats" signal:
- `Core/Config/relic-classifications.json` / `relic-taxonomy.json` / `RelicClassificationStore.cs` / `RelicTaxonomy.cs` list every relic, but only drive the combat relic-bar filter and compendium taxonomy filter.
- `RelicBarFilterRelicFlashPatch` (`Core/Patches/RelicBarFilterPatch.cs:520-529`) patches `RelicModel.Flash` for **every** relic, but only marks bar-filter relevance; it records nothing into `RelicAggregate`.
- `RelicStatRowVocabulary.cs` only rewrites row labels into icon vocabulary; it has no relic list.
- Relic healing (`RunTracker.RecordRelicHealingHpChanged`, ~`RunTracker.cs:24398`) only counts heals while an owner-specific window is armed by a relic patch - not generic.

So: **a relic is tracked iff it appears in the `TryBuildBodyBBCodeWithLiveActivationCounts` chain.** 180 relic classes do (including the Fake*/alias variants).

### Generic-only
No relic is generic-only unconditionally. The single generic path is the Wax overlay: an untracked relic that Toy Box bestows as Wax shows *only* the three Wax rows. Toy Box pulls via `RelicFactory.PullNextRelicFromFront(player)` with `RollRarity` = Common/Uncommon/Rare (fallback Circlet), so these untracked relics are **generic-only when obtained as Wax** (27):

Common: DataDisk, FencingManual, SneckoSkull. Uncommon: FuneraryMask, GalacticDust, SelfFormingClay, Tingsha, TwistedFunnel. Rare: Bellows, BigHat, CharonsAshes, DemonTongue, EmotionChip, HelicalDart, IvoryTile, LunarPastry, Metronome, MiniRegent, OrangeDough, PaperKrane, PowerCell, StoneCalendar, TheCourier, ToughBandages, UnceasingTop, VexingPuzzlebox. Fallback: Circlet.

### Exclusions
- `DeprecatedRelic` - only member of `DeprecatedRelicPool`; "relic that has been removed from the game".
- `VakuuCardSelector` - lives in the Relics namespace but is an `ICardSelector` helper for Whispering Earring, not a `RelicModel`; in no pool.
- No mock relics exist (there is no `Models.Relics.Mocks` namespace). Every other class is in a live pool.

## 2. Counts

| | Count |
|---|---|
| Relic classes in `Models.Relics` | 300 |
| Tracked (in dispatch chain) | 180 |
| Excluded (deprecated / not a relic) | 2 |
| **Untracked real relics** | **118** |
| (A) fits an existing pattern | 105 |
| (B) needs new mechanics | 9 |
| (C) passive / no meaningful stat | 4 |

Untracked by rarity: Ancient 55, Shop 19, Rare 18, Event 13, Uncommon 5, Starter 4 (BlackBlood, DivineDestiny, DivineRight, RingOfTheDrake), Common 3, None 1 (Circlet).
Notable whole-character gaps: Regent starters DivineRight/DivineDestiny and every star-gain relic; all 4 Starter upgrades reachable via Touch of Orobas except PhylacteryUnbound/InfusedCore.

## 3. Untracked relics and proposed stats

Conventions: stats are observed outcomes (actual delta read around the relic-owned callback), per ADR 0001. "Hook" is the relic's own override unless noted. "Analog" names an already-tracked relic and the file holding its tracking.

### (A) Fits an existing pattern - 105

| Relic id | Class | What it does | Proposed stats | Hook | Analog in repo |
|---|---|---|---|---|---|
| RELIC.DIAMOND_DIADEM | DiamondDiadem | Turn 1: 20 Block + 1 Blur | Activations; Block gained (actual); Block effective vs wasted; Block carried into turn 2 by Blur | `AfterSideTurnStart` (TurnNumber<=1) | Anchor - `AnchorBeforeSideTurnStartPatch.cs` |
| RELIC.SAI | Sai | 7 Block at the start of every turn | Block gained total; avg per turn / per combat; Block effective vs wasted | `AfterSideTurnStart` | Anchor; Orichalcum window via `HookAfterBlockGainedPatch.cs` |
| RELIC.FAKE_ORICHALCUM | FakeOrichalcum | Exact Orichalcum copy (3 Block at turn end if 0 Block) | Same rows as Orichalcum (activations, Block gained, Block missed, per-turn/per-combat) | `BeforeSideTurnEndVeryEarly` + `BeforeSideTurnEnd` | Orichalcum - `OrichalcumBeforeTurnEndPatch.cs`; alias precedent FakeAnchor in `GetStatsAggregateId` |
| RELIC.TOUGH_BANDAGES | ToughBandages | 3 Block per card discarded on your turn | Activations (discards); Block gained; avg Block per combat | `AfterCardDiscarded` | TuningFork - `TuningForkAfterCardPlayedPatch.cs`; BoneFlute - `BoneFluteAfterAttackPatch.cs` |
| RELIC.GALACTIC_DUST | GalacticDust | 10 Block per 10 Stars spent (counter persists) | Stars spent while held; activations; Block gained | `AfterStarsSpent` | TheAbacus - `TheAbacusAfterShufflePatch.cs` (block window) |
| RELIC.SELF_FORMING_CLAY | SelfFormingClay | Unblocked HP loss -> 3 Block next turn (via SelfFormingClayPower) | Triggers; Block banked; Block actually gained when the power fires | `AfterDamageReceived` + `SelfFormingClayPower.AfterBlockCleared` | ReptileTrinket (relic-applied power) - `ReptileTrinketAfterPotionUsedPatch.cs`; CaptainsWheel (`AfterBlockCleared`) - `CaptainsWheelStatsPatch.cs` |
| RELIC.PAELS_LEGION | PaelsLegion | Doubles one card's Block, then 2-turn cooldown (pet) | Activations; extra Block from doubling (actual delta); Block effective; avg per combat | `ModifyBlockMultiplicative` + `AfterModifyingBlockAmount` + `AfterCardPlayed` | Vambrace - `VambraceStatsPatch.cs` |
| RELIC.BLACK_BLOOD | BlackBlood | Heal 12 after combat victory (Ironclad upgraded starter) | Activations; HP restored (actual); healing lost to full HP | `AfterCombatVictory` | BurningBlood - `BurningBloodAfterCombatVictoryPatch.cs` |
| RELIC.DEMON_TONGUE | DemonTongue | First unblocked HP loss on your turn each turn heals that amount | Activations; HP restored; avg heal per activation | `AfterDamageReceived` | BloodVial healing window - `BloodVialAfterPlayerTurnStartLatePatch.cs`; CentennialPuzzle - `CentennialPuzzleStatsPatch.cs` |
| RELIC.CHARONS_ASHES | CharonsAshes | 3 damage to all enemies per card exhausted | Activations; damage dealt / blocked / overkill; kills; targets hit | `AfterCardExhausted` | ForgottenSoul - `ForgottenSoulStatsPatch.cs` (`AppendRelicDamageStats`) |
| RELIC.TINGSHA | Tingsha | 3 damage to a random enemy per card discarded on your turn | Activations; damage dealt / blocked / overkill; kills | `AfterCardDiscarded` | ForgottenSoul |
| RELIC.METRONOME | Metronome | 30 damage to all enemies on the 7th orb channeled each combat | Activations; damage dealt / overkill / kills; avg orbs channeled per combat | `AfterOrbChanneled` | MercuryHourglass - `MercuryHourglassStatsPatch.cs` |
| RELIC.STONE_CALENDAR | StoneCalendar | 52 damage to all enemies at end of turn 7 | Activations (combats reaching turn 7); damage dealt / overkill / kills | `BeforeSideTurnEnd` | ScreamingFlagon (`BeforeSideTurnEnd` damage) - `ScreamingFlagonStatsPatch.cs` |
| RELIC.ROYAL_POISON | RoyalPoison | Turn 1: 4 unblockable damage to yourself | Activations; HP lost (actual) | `AfterPlayerTurnStart` | MrStruggles damage window - `MrStrugglesStatsPatch.cs` (target = owner) |
| RELIC.THE_BOOT | TheBoot | Your/Osty powered attack HP loss below 5 is raised to 5 | Activations; extra HP loss inflicted (5 - original) | `ModifyHpLostAfterOstyLate` + `AfterModifyingHpLostAfterOsty` | TungstenRod - `TungstenRodStatsPatch.cs` |
| RELIC.VITRUVIAN_MINION | VitruvianMinion | Minion-tagged cards deal x2 damage and x2 Block | Minion plays boosted; extra damage (actual); extra Block (actual) | `ModifyDamageMultiplicative` / `ModifyBlockMultiplicative` (no After callback; read inside the card-play damage window) | PenNib - `PenNibStatsPatch.cs`; Vambrace for Block |
| RELIC.UNDYING_SIGIL | UndyingSigil | Powered attacks on you from enemies with HP <= their Doom deal 50% (class doc comment about Doom timing is stale) | Hits reduced; damage prevented | `ModifyDamageMultiplicative` | PaperPhrog multiplier-delta - `PaperPhrogStatsPatch.cs`; TungstenRod "prevented" rows |
| RELIC.PAPER_KRANE | PaperKrane | Weak enemies attacking you deal 15pp less | Hits reduced; damage prevented | `PaperKrane.ModifyWeakMultiplier` (called from `WeakPower`) | PaperPhrog - `PaperPhrogStatsPatch.cs` (mirror: Vulnerable <-> Weak) |
| RELIC.HAND_DRILL | HandDrill | Breaking an enemy's Block applies 2 Vulnerable | Activations; Vulnerable applied (actual); enemies affected | `AfterBlockBroken` | BagOfMarbles - `BagOfMarblesBeforeSideTurnStartPatch.cs` (`AppliedEffects`) |
| RELIC.TWISTED_FUNNEL | TwistedFunnel | Turn 1: 4 Poison to all enemies | Activations; Poison applied (actual); enemies affected; stretch: poison damage from those stacks | `BeforeSideTurnStart` | BagOfMarbles; `PoisonPowerAfterSideTurnStartPatch.cs` for downstream damage |
| RELIC.SNECKO_SKULL | SneckoSkull | +1 to every Poison application you give | Activations; Poison stacks added | `ModifyPowerAmountGivenAdditive` + `AfterModifyingPowerAmountGiven` | UnsettlingLamp - `UnsettlingLampStatsPatch.cs` |
| RELIC.PHILOSOPHERS_STONE | PhilosophersStone | +1 max Energy; all enemies +1 Strength | Energy gained (per reset); Strength given to enemies; enemies affected | `ModifyMaxEnergy` (observe at `Hook.AfterEnergyReset`); `AfterRoomEntered` / `AfterCreatureAddedToCombat` | PrismaticGem - `PrismaticGemStatsPatch.cs`; BagOfMarbles `AppliedEffects` |
| RELIC.BRIMSTONE | Brimstone | Every turn: +2 Strength to you, +1 to all enemies | Activations; Strength gained; Strength given to enemies; Attacks played / hits with it | `AfterSideTurnStart` | Girya/Vajra `AppendStrengthIncreaseRelicStats` - `GiryaStatsPatch.cs` |
| RELIC.SLING_OF_COURAGE | SlingOfCourage | +2 Strength at the start of Elite combats | Activations (elites); Strength gained; Attacks / hits that benefited | `AfterRoomEntered` | Vajra (RunTracker) / Girya - `GiryaStatsPatch.cs` |
| RELIC.MINI_REGENT | MiniRegent | First Star spend each turn: +1 Strength | Activations; Strength gained; avg per combat | `AfterStarsSpent` | Shuriken - `UnlimitedAttackChargeRelicsStatsPatch.cs` |
| RELIC.DATA_DISK | DataDisk | +1 Focus at combat start | Activations; Focus applied | `AfterRoomEntered` | OddlySmoothStone / Vajra (combat-start power; RunTracker) |
| RELIC.HELICAL_DART | HelicalDart | Playing a Shiv grants 1 temporary Dexterity | Activations (Shivs played); Dexterity gained; stretch: Block cards played under it | `AfterCardPlayed` | Kunai - `KunaiStatsPatch.cs` |
| RELIC.BELT_BUCKLE | BeltBuckle | +2 Dexterity while you hold no potions (toggles) | Combats / turns active; Dexterity applied and removed counts | private `ApplyDexterity`/`RemoveDexterity` (via `BeforeCombatStart`, `AfterPotion*`) | OddlySmoothStone; RedSkull reversible-state tracking (RunTracker) |
| RELIC.BREAD | Bread | Turn 1: lose 2 Energy; turns 2+: +1 max Energy | Energy gained (turn 2+ resets); Energy lost turn 1 (actual); net Energy | `AfterSideTurnStart` + `ModifyMaxEnergy` (`Hook.AfterEnergyReset`) | PrismaticGem; SealOfGold cost rows - `SealOfGoldStatsPatch.cs` |
| RELIC.ECTOPLASM | Ectoplasm | +1 max Energy; you cannot gain gold | Energy gained; gold blocked (sum of prevented gains); gain events blocked | `ModifyMaxEnergy`; `ModifyGoldGained` + `AfterModifyingGoldGained` | PrismaticGem; BowlerHat - `BowlerHatStatsPatch.cs` |
| RELIC.SOZU | Sozu | +1 max Energy; you cannot obtain potions | Energy gained; potions blocked | `ModifyMaxEnergy`; `ShouldProcurePotion` (via `Hook.ShouldProcurePotion`) | PrismaticGem; WhiteBeastStatue decision patch - `WhiteBeastStatueShouldForcePotionRewardPatch.cs` |
| RELIC.BLESSED_ANTLER | BlessedAntler | +1 max Energy; turn 1 shuffles 3 Dazed into draw pile | Energy gained; Dazed added; pooled Dazed drawn/exhausted | `ModifyMaxEnergy`; `BeforeHandDraw` | PrismaticGem; CursedPearl pooled card rows (RelicHoverTooltipPatch) |
| RELIC.VELVET_CHOKER | VelvetChoker | +1 max Energy; max 6 card plays per turn | Energy gained; turns that hit the cap; plays prevented | `ModifyMaxEnergy`; `ShouldPlay` (via `Hook.ShouldPlay` out preventer) | PrismaticGem; IceCream decision-point - `IceCreamStatsPatch.cs` |
| RELIC.PAELS_FLESH | PaelsFlesh | +1 max Energy from turn 3 onward | Energy gained; combats reaching turn 3 | `ModifyMaxEnergy` / `AfterSideTurnStart` | PrismaticGem - `PrismaticGemStatsPatch.cs` |
| RELIC.PAELS_TEARS | PaelsTears | +2 Energy next turn if you ended a turn with unspent Energy | Activations; Energy gained; avg per combat | `BeforeSideTurnEnd` + `AfterSideTurnStart` | ArtOfWar - `ArtOfWarStatsPatch.cs` |
| RELIC.IVORY_TILE | IvoryTile | +1 Energy when you play a card costing 3+ | Activations; Energy gained | `AfterCardPlayed` | Nunchaku - `NunchakuStatsPatch.cs` |
| RELIC.DIVINE_RIGHT | DivineRight | Regent starter: +3 Stars at each combat start | Activations; Stars gained (actual delta) | `AfterRoomEntered` | Candelabra energy window - `CandelabraStatsPatch.cs` + `PlayerGainStarsPatch.cs` delta |
| RELIC.DIVINE_DESTINY | DivineDestiny | Upgraded Regent starter: +7 Stars turn 1 | Activations; Stars gained | `AfterSideTurnStart` | as DivineRight |
| RELIC.LUNAR_PASTRY | LunarPastry | +1 Star at the end of each turn | Stars gained; avg per combat | `AfterSideTurnEnd` | as DivineRight |
| RELIC.FENCING_MANUAL | FencingManual | Turn 1: Forge 10 | Activations; Forge granted | `AfterSideTurnStart` (`ForgeCmd.Forge(..., this)`) | `HookAfterForgePatch.cs` (source == relic) |
| RELIC.EMOTION_CHIP | EmotionChip | If you lost HP last turn, trigger all orb passives at turn start | Activations; passives triggered by orb type; damage/Block from those passives | `AfterPlayerTurnStart` | GoldPlatedCables - `GoldPlatedCablesStatsPatch.cs` (per-orb activation aggregate) |
| RELIC.RING_OF_THE_DRAKE | RingOfTheDrake | Upgraded Silent starter: +2 hand draw on turns 1-3 | Activations; extra cards drawn; draws blocked | `ModifyHandDraw` + completed draw | RingOfTheSnake - `BagOfPreparationStatsPatch.cs` |
| RELIC.PAELS_BLOOD | PaelsBlood | +1 hand draw every turn | Extra cards drawn; avg per turn; draws blocked | `ModifyHandDraw` | Pocketwatch - `PocketwatchModifyHandDrawPatch.cs` |
| RELIC.SNECKO_EYE | SneckoEye | +2 hand draw; Confused each combat | Extra cards drawn; stretch: net Energy cost change from Confused | `ModifyHandDraw`; `BeforeCombatStart` | Pocketwatch; BrilliantScarf cost rows - `BrilliantScarfStatsPatch.cs` |
| RELIC.FIDDLE | Fiddle | +2 hand draw; all other draws on your turn are prevented | Extra cards drawn; draws prevented | `ModifyHandDraw`; `ShouldDraw` + `AfterPreventingDraw` | Pocketwatch; `HookShouldDrawPatch.cs` |
| RELIC.BIG_MUSHROOM | BigMushroom | +20 max HP on pickup; -2 hand draw on turn 1 | Max HP gained; cards not drawn on turn 1 (actual) | `AfterObtained`; `ModifyHandDraw` | Strawberry - `StrawberryAfterObtainedPatch.cs`; BagOfPreparation draw observer |
| RELIC.UNCEASING_TOP | UnceasingTop | Draw a card whenever your hand empties mid-turn | Activations; cards drawn | `AfterHandEmptied` | GamePiece - `GamePieceStatsPatch.cs` |
| RELIC.POWER_CELL | PowerCell | Turn 1: move 2 zero-cost cards from draw pile to hand | Activations; cards fetched; fetched cards played | `BeforeSideTurnStart` | Crossbow "entered combat" rows - `CrossbowStatsPatch.cs` |
| RELIC.JEWELED_MASK | JeweledMask | Turn 1: a Power from draw pile to hand, free this turn | Activations; Power fetched; Energy saved when played free | `BeforeHandDraw` | Crossbow free-cost rows - `CrossbowStatsPatch.cs` |
| RELIC.RUNIC_PYRAMID | RunicPyramid | Your hand is never discarded at end of turn | Turns retained; cards retained per turn | `ShouldFlush` (via `Hook.ShouldFlush`, gate on relic's own answer) | IceCream decision-point - `IceCreamStatsPatch.cs`; Bookmark - `BookmarkStatsPatch.cs` |
| RELIC.RINGING_TRIANGLE | RingingTriangle | Hand not discarded at end of turn 1 | Cards retained on turn 1; avg per combat | `ShouldFlush` | IceCream; Bookmark |
| RELIC.BIG_HAT | BigHat | Turn 1: 2 random Ethereal class cards into hand | Activations; cards generated; generated cards played; rarity breakdown | `AfterSideTurnStart` | Crossbow - `CrossbowStatsPatch.cs` |
| RELIC.ORANGE_DOUGH | OrangeDough | Turn 1: 2 random Colorless cards into hand | Activations; cards generated / played; rarity breakdown | `AfterSideTurnStart` | Crossbow |
| RELIC.VEXING_PUZZLEBOX | VexingPuzzlebox | Turn 1: a random class card into hand, free this turn | Activations; card generated / played; Energy saved | `AfterPlayerTurnStart` | Crossbow (discount rows) |
| RELIC.NINJA_SCROLL | NinjaScroll | Turn 1: 3 Shivs into hand | Shivs generated; Shivs played; pooled Shiv damage | `BeforeHandDraw` | Toolbox - `ToolboxStatsPatch.cs`; Storybook pooled-card rows |
| RELIC.FUNERARY_MASK | FuneraryMask | Turn 1: 3 Souls into draw pile | Souls generated; drawn / played (pooled) | `BeforeHandDraw` | `SoulPileStatsPatch.cs`; Toolbox |
| RELIC.RADIANT_PEARL | RadiantPearl | Turn 1: Luminesce into hand | Generated; played; pooled Luminesce Energy | `BeforeHandDraw` | Toolbox; Storybook |
| RELIC.BIIIG_HUG | BiiigHug | Pickup: remove 4 cards; every shuffle adds a Soot to draw pile | Cards removed (names); Soot added; pooled Soot stats | `AfterObtained`; `AfterShuffle` | PrecariousShears - `PrecariousShearsStatsPatch.cs`; TheAbacus (`AfterShuffle`) |
| RELIC.TEA_OF_DISCOURTESY | TeaOfDiscourtesy | Next combat: 2 Dazed into draw pile (single use) | Dazed added; pooled Dazed drawn / exhausted | `BeforeCombatStart` | Toolbox; CursedPearl pooled rows |
| RELIC.BELLOWS | Bellows | Turn 1: upgrade every card in hand | Activations; cards actually upgraded; avg per combat | `AfterPlayerTurnStart` | RazorTooth - `RazorToothStatsPatch.cs`; FishingRod `UpgradeInternal` window - `FishingRodStatsPatch.cs` |
| RELIC.BONE_TEA | BoneTea | Next combat: upgrade hand on turn 1 (single use) | Cards actually upgraded | `AfterPlayerTurnStart` | RazorTooth |
| RELIC.NEOWS_TALISMAN | NeowsTalisman | Pickup: upgrade last basic Strike and Defend | Cards upgraded (names) | `AfterObtained` | Whetstone - `WhetstoneStatsPatch.cs` (`AppendUpgradedCardStats`) |
| RELIC.POMANDER | Pomander | Pickup: upgrade 1 chosen card | Card upgraded | `AfterObtained` | Whetstone |
| RELIC.ASTROLABE | Astrolabe | Pickup: transform 3 chosen cards, upgrade results | Transformations (from -> to) | `AfterObtained` | LeafyPoultice - `LeafyPoulticeAfterObtainedPatch.cs` (`AppendCardTransformationRows`) |
| RELIC.NEW_LEAF | NewLeaf | Pickup: transform 1 chosen card | Transformation | `AfterObtained` | LeafyPoultice |
| RELIC.PANDORAS_BOX | PandorasBox | Pickup: transform all removable basic Strikes/Defends | Transformations; count | `AfterObtained` | LeafyPoultice |
| RELIC.CLAWS | Claws | Pickup: transform up to 6 chosen cards into Maul | Transformations; pooled Maul stats | `AfterObtained` | LeafyPoultice; Storybook |
| RELIC.ARCHAIC_TOOTH | ArchaicTooth | Pickup: transform a starter card into its Ancient version | Transformation; pooled stats of resulting card | `AfterObtained` | LeafyPoultice; Storybook |
| RELIC.EMPTY_CAGE | EmptyCage | Pickup: remove 2 chosen cards | Cards removed | `AfterObtained` | PrecariousShears - `PrecariousShearsStatsPatch.cs` |
| RELIC.PRECISE_SCISSORS | PreciseScissors | Pickup: remove 1 chosen card | Card removed | `AfterObtained` | PrecariousShears |
| RELIC.PRESERVED_FOG | PreservedFog | Pickup: remove 3 cards, add Folly curse | Cards removed; pooled Folly stats | `AfterObtained` | PrecariousShears; CursedPearl pooled curse rows |
| RELIC.BEAUTIFUL_BRACELET | BeautifulBracelet | Pickup: Swift 2 on 4 random cards | Cards enchanted (names); Swift triggers (pooled) | `AfterObtained` | TriBoomerang - `TriBoomerangStatsPatch.cs`; WingCharm Swift rows |
| RELIC.ELECTRIC_SHRYMP | ElectricShrymp | Pickup: Imbued on 1 chosen card | Card enchanted; enchanted card play stats | `AfterObtained` | GnarledHammer - `GnarledHammerStatsPatch.cs` |
| RELIC.KIFUDA | Kifuda | Pickup: Adroit 3 on up to 3 cards | Cards enchanted; play stats | `AfterObtained` | GnarledHammer |
| RELIC.PUNCH_DAGGER | PunchDagger | Pickup: Momentum 5 on 1 card | Card enchanted; play stats | `AfterObtained` | GnarledHammer |
| RELIC.ROYAL_STAMP | RoyalStamp | Pickup: Royally Approved on 1 card | Card enchanted; play stats | `AfterObtained` | GnarledHammer |
| RELIC.PAELS_GROWTH | PaelsGrowth | Pickup: Clone(4) on 1 card; adds a Clone rest-site option | Card enchanted; Clone uses; cards cloned | `AfterObtained`; `TryModifyRestSiteOptions` / `CloneRestSiteOption.OnSelect` | PaelsClaw - `PaelsClawStatsPatch.cs`; Shovel - `DigRestSiteOptionShovelStatsPatch.cs` |
| RELIC.DOLLYS_MIRROR | DollysMirror | Pickup: duplicate 1 chosen card | Card copied; pooled stats of the copy | `AfterObtained` | LeadPaperweight - `LeadPaperweightStatsPatch.cs`; Storybook |
| RELIC.DUSTY_TOME | DustyTome | Pickup: add an upgraded Ancient card | Card added; its pooled stats | `AfterObtained` | Storybook pooled-card pattern (RelicHoverTooltipPatch) |
| RELIC.JEWELRY_BOX | JewelryBox | Pickup: add Apotheosis | Pooled Apotheosis stats | `AfterObtained` | Storybook |
| RELIC.NEOWS_TORMENT | NeowsTorment | Pickup: add Neow's Fury | Pooled Neow's Fury stats | `AfterObtained` | Storybook |
| RELIC.TANXS_WHISTLE | TanxsWhistle | Pickup: add Whistle | Pooled Whistle stats | `AfterObtained` | Storybook |
| RELIC.PAELS_HORN | PaelsHorn | Pickup: add 2 Relax | Pooled Relax stats | `AfterObtained` | Storybook |
| RELIC.SERE_TALON | SereTalon | Pickup: lose 9 max HP, add 3 Wish | Max HP lost; pooled Wish stats | `AfterObtained` | LeafyPoultice max-HP rows; Storybook |
| RELIC.DISTINGUISHED_CAPE | DistinguishedCape | Pickup: 2 random curses + 3 Apparitions | Curses added (pooled per curse); pooled Apparition stats | `AfterObtained` | NeowsBones granted-curse aggregates - `NeowsBonesStatsPatch.cs`; Storybook |
| RELIC.NEOWS_SACRIFICE | NeowsSacrifice | Pickup: Ambergris potion + Guilty curse | Potion procured; pooled Guilty stats | `AfterObtained` | NeowsBones; PetrifiedToad potion rows (RunTracker) |
| RELIC.CALLING_BELL | CallingBell | Pickup: Curse of the Bell + 3 relic rewards (C/U/R) | Relics granted (which); pooled curse stats | `AfterObtained` (`RewardsCmd.OfferCustom`) | LargeCapsule `RelicsGranted` - `LargeCapsuleStatsPatch.cs`; CursedPearl |
| RELIC.BYRDPIP | Byrdpip | Transforms Byrdonis Eggs into Byrd Swoop; summons an idle pet | Eggs transformed; pooled Byrd Swoop stats | `AfterObtained` | LeafyPoultice transform rows; Storybook |
| RELIC.LOOMING_FRUIT | LoomingFruit | Pickup: +31 max HP | Max HP gained | `AfterObtained` | Strawberry - `StrawberryAfterObtainedPatch.cs` |
| RELIC.DRAGON_FRUIT | DragonFruit | +1 max HP every time you gain gold | Activations; max HP gained; avg per floor | `AfterGoldGained` | DarkstonePeriapt - `DarkstonePeriaptAfterCardChangedPilesPatch.cs` |
| RELIC.GLASS_EYE | GlassEye | Pickup: 5 card rewards (C, C, U, U, R) | Offered/taken per rarity; skipped | `AfterObtained` (`OfferCustom`) | Orrery - `OrreryStatsPatch.cs`; Toolbox offered/taken rows |
| RELIC.LOST_COFFER | LostCoffer | Pickup: 1 card reward + 1 potion reward | Card offered/taken; potion taken | `AfterObtained` | Orrery |
| RELIC.KALEIDOSCOPE | Kaleidoscope | Pickup: 2 card rewards from other characters' pools | Offered/taken (by character) | `AfterObtained` | Orrery |
| RELIC.MASSIVE_SCROLL | MassiveScroll | Pickup (multiplayer): choose 1 of 3 multiplayer cards | Offered/taken | `AfterObtained` (`FromChooseACardScreen`) | HeftyTablet - `HeftyTabletStatsPatch.cs`; LeadPaperweight |
| RELIC.SEA_GLASS | SeaGlass | Pickup: take any of 15 cards from another character | Offered/taken by rarity | `AfterObtained` (`FromSimpleGridForRewards`) | SilverCrucible - `SilverCrucibleStatsPatch.cs`; ScrollBoxes |
| RELIC.CAULDRON | Cauldron | Pickup: 5 potion rewards | Potions offered/taken | `AfterObtained` | Orrery / SmallCapsule - `SmallCapsuleStatsPatch.cs` |
| RELIC.DELICATE_FROND | DelicateFrond | Before each combat, fill empty potion slots | Potions procured; avg per combat | `BeforeCombatStart` | PetrifiedToad (`BeforeCombatStartLate` potion; RunTracker) |
| RELIC.GLITTER | Glitter | Card reward options gain Glam | Rewards affected; Glam cards offered/taken; Glam replays | `TryModifyCardRewardOptionsLate` | SilkenTress (RunTracker) + `GlamEnchantPlayCountPatch.cs` |
| RELIC.LAVA_LAMP | LavaLamp | Combat with no unblocked damage taken -> card rewards upgraded | Qualifying combats; upgraded cards offered/taken | `AfterDamageReceived` + `TryModifyCardRewardOptionsLate` | Egg relics - `EggRelicStatsPatch.cs` (`UpgradedCardsOffered/Taken`) |
| RELIC.DRIFTWOOD | Driftwood | Card rewards can be rerolled | Rerollable rewards; rerolls used; taken after reroll | `TryModifyRewardsLate`; `CardReward.Reroll` | `FresnelLensStatsPatch.cs` / `OrreryStatsPatch.cs` reroll patches |
| RELIC.BLACK_STAR | BlackStar | Elites drop an extra relic reward | Activations; relics granted (taken) | `TryModifyRewards` | WhiteStar - `WhiteStarStatsPatch.cs` |
| RELIC.LAVA_ROCK | LavaRock | Act 1 boss drops 2 extra relic rewards (once) | Activations; relics granted | `TryModifyRewards` | WhiteStar |
| RELIC.DREAM_CATCHER | DreamCatcher | Resting also offers a card reward | Activations; cards offered/taken | `TryModifyRestSiteHealRewards` | TinyMailbox - `TinyMailboxStatsPatch.cs` |
| RELIC.MEAT_CLEAVER | MeatCleaver | Adds Cook rest-site option (remove 2 cards, +5 max HP) | Cook uses; cards removed; max HP gained | `TryModifyRestSiteOptions` / `CookRestSiteOption.OnSelect` | Shovel - `DigRestSiteOptionShovelStatsPatch.cs`; Girya |
| RELIC.THE_COURIER | TheCourier | 20% shop discount; purchased slots restock | Gold saved (actual price delta at purchase); restocks | `ModifyMerchantPrice`; `ShouldRefillMerchantEntry` | MembershipCard - `MembershipCardStatsPatch.cs` |

### (B) Needs new mechanics - 9

| Relic id | Class | What it does | Proposed stats | Hook | Analog in repo / what is new |
|---|---|---|---|---|---|
| RELIC.CHEMICAL_X | ChemicalX | X-cost cards get +2 X | X-card plays boosted; X added (`ModifyXValue` delta); damage/Block attributable to the extra X | `BeforeCardPlayed` (flash) + `ModifyXValue` (via `Hook.ModifyXValue`) | New: no X-value observer exists, and splitting a card's output by X share is new. Nearest: SpikedGauntlets cost-modifier rows |
| RELIC.FAKE_SNECKO_EYE | FakeSneckoEye | Confused every combat (no draw bonus) | Cards whose cost Confused changed; net Energy cost delta vs printed (saved / lost) | `BeforeCombatStart` + `ConfusedPower` cost randomization | New: needs a ConfusedPower cost observer. Nearest: BrilliantScarf cost buckets |
| RELIC.FUR_COAT | FurCoat | Marks 8 combats in its act; enemies there are set to 1 HP | Marked combats entered; enemies affected; enemy HP removed (pre-set HP - 1) | `BeforeCombatStart` + `AfterCreatureAddedToCombat` | New: HP-set (not damage) observer around `CreatureCmd.SetCurrentHp`, plus reading saved map marks |
| RELIC.GHOST_SEED | GhostSeed | Basic Strikes/Defends become Ethereal | Cards made Ethereal; of those, exhausted by Ethereal at turn end (in-combat deck thinning) | `AfterRoomEntered` + `AfterCardEnteredCombat` | New: attributing an Ethereal exhaust to the keyword's source. Nearest: JossPaper deferred Ethereal exhaust counting |
| RELIC.GOLDEN_COMPASS | GoldenCompass | Regenerates the current act as a Golden Path map; ? rooms are always Events | Act affected; events entered on the golden path | `AfterObtained` + `ModifyGeneratedMap` + `ModifyUnknownMapPointRoomTypes` | Nearest: JuzuBracelet map-point counting (`RunManagerJuzuBraceletStatsPatch.cs`); new: map-replacement context |
| RELIC.HISTORY_COURSE | HistoryCourse | From turn 2, auto-plays a dupe of last turn's last Attack | Activations; card replayed (by name); dupe damage / Block | `AfterAutoPrePlayPhaseEntered` | Nearest: WhisperingEarring autoplay / ThrowingAxe extra plays (RunTracker); new: credit a dupe CardPlay's outputs to the relic instead of the card |
| RELIC.LORDS_PARASOL | LordsParasol | Entering a shop buys everything for free (incl. card removal) | Shops looted; items acquired by type; gold value acquired (undiscounted prices); removals used | `AfterRoomEntered` -> private `PurchaseEverything` (`MerchantEntry.OnTryPurchaseWrapper(ignoreCost: true)`) | Nearest: MembershipCard merchant-price observation; new: ignoreCost purchase window |
| RELIC.MINIATURE_TENT | MiniatureTent | Picking a rest-site option does not disable the others | Rest sites with extra options used; extra options by type | `ShouldDisableRemainingRestSiteOptions` | Nearest: Shovel rest-option patch, `RunHistoryCampfireSummary.cs`; new: per-visit multi-option sequence |
| RELIC.RUNIC_CAPACITOR | RunicCapacitor | Turn 1: +3 orb slots | Activations; peak orbs held beyond base slots; channels that would have forced an evoke without the slots | `AfterSideTurnStart` | Nearest: GoldPlatedCables orb aggregate; new: slot-occupancy counterfactual (the activation itself is constant and uninformative) |

### (C) Passive / no meaningful stat - 4

| Relic id | Class | What it does | Proposed stats | Hook | Why |
|---|---|---|---|---|---|
| RELIC.CIRCLET | Circlet | Fallback relic when a pool is empty; stackable; no overrides | Floor acquired / count held only | none | Class has no behaviour at all. |
| RELIC.FAKE_MERCHANTS_RUG | FakeMerchantsRug | Fake-merchant fight reward; empty class | Floor acquired only | none | No overrides; pure trophy. |
| RELIC.WONGO_CUSTOMER_APPRECIATION_BADGE | WongoCustomerAppreciationBadge | Awarded by Welcome to Wongo's at 2000 points; empty class | Floor acquired; optionally badges held | none | No overrides; Wongo points live on `Player.ExtraFields.WongoPoints`, owned by the event. |
| RELIC.TOUCH_OF_OROBAS | TouchOfOrobas | Pickup: replaces your starter relic with its upgraded version | "Replaced X with Y" + floor | `AfterObtained` (`RelicCmd.Replace`) | One-shot swap; the value lives in the upgraded relic's own stats. Note BlackBlood, RingOfTheDrake and DivineDestiny (its outputs) are themselves untracked (group A). Could reuse SwordOfStone replace tracking (`SwordInTheStoneStatsPatch.cs`) if a provenance row is wanted. |

## 4. End-to-end pattern for a new relic stat (Ice Cream, commit 864bfd9)

`git show --stat 864bfd9` (9 files, +528):

```
Core/Patches/IceCreamStatsPatch.cs               |  59  new Harmony patch on the owner hook (Hook.ShouldPlayerResetEnergy postfix, gated on the relic's own answer)
Core/Patches/RelicHoverTooltipPatch.cs           |  53  `if (relicModel is IceCream)` branch in the dispatch chain + BuildIceCreamBodyBBCode + Calculate* helpers
Core/RunData.cs                                  |  11  additive RelicAggregate fields (IceCreamEnergyConserved/Turns/Combats)
Core/RunTracker.cs                               | 111  RecordIceCreamEnergyDecision + *ForTest seams, relic-id const, MergeRelicAggregateInto lines, PendingCombat per-player dedupe sets
Fixtures/RunSchema/README.md                     |   4  fixture entry
Fixtures/RunSchema/ice-cream-relic-run.json      |  18  snake_case relic_aggregates fixture
Tests/SpireLens.Core.Tests/IceCreamStatsTests.cs | 223  recorder + tooltip tests
Tests/SpireLens.Core.Tests/SchemaLoadingTests.cs |  28  HistoricalLoad_ + ResumableLoad_Accepts...Fixture
docs/sts2-runtime-primer.md                      |  21  attribution note under "Relic Attribution"
```

Same shape in 7b52978 (Membership Card, 10 files: adds `RelicCmdObtainStatsPatch.cs` for acquisition-time setup).
