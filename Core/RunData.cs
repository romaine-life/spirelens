using System;
using System.Collections.Generic;

namespace SpireLens.Core;

/// <summary>
/// Serialized shape of one run's stats. Written to disk as JSON.
///
/// The on-disk shape evolves additively: new persisted fields default safely
/// on missing-field deserialization, so older files continue to load without
/// an explicit version number. The historic pooled shape (aggregates keyed by
/// card definition id rather than per-instance id, written before
/// <see cref="InstanceNumbersByDef"/> and <see cref="DefCounters"/> existed)
/// is detected structurally by <see cref="RunStorage"/>; everything else is
/// the current per-instance shape.
/// </summary>
public class RunData
{
    public string RunId { get; set; } = "";
    public string StartedAt { get; set; } = "";  // ISO-8601 UTC
    public string UpdatedAt { get; set; } = "";
    public string? EndedAt { get; set; }
    public string? Character { get; set; }
    public int? Ascension { get; set; }
    public int? FloorReached { get; set; }
    public string Outcome { get; set; } = "in_progress";  // in_progress | win | loss | abandoned

    /// <summary>
    /// The game's own run identifier — Unix seconds of run start, sourced from
    /// <c>RunManager._startTime</c>. The game saves its run history to
    /// <c>{StartTime}.run</c>, so this field is the correlation key for M5
    /// (Run History integration): user clicks a past run in the game, the
    /// game knows its start_time, we find our file where <see cref="GameStartTime"/>
    /// matches. Our file name stays a GUID for identity independence.
    ///
    /// Null for runs created before this field was added or runs that observed
    /// combat before RunStarted fired (edge case — mod loaded mid-run).
    /// </summary>
    public long? GameStartTime { get; set; }

    /// <summary>Per-card aggregates. Keyed by card definition ID (e.g. "STRIKE_KIN"). Upgraded and base versions share a key for now; upgrade breakout is a future issue.</summary>
    public Dictionary<string, CardAggregate> Aggregates { get; set; } = new();

    /// <summary>Full per-event log for later deep analysis. One entry per card play + one entry per damage-received-from-card.</summary>
    public List<CardEvent> Events { get; set; } = new();

    /// <summary>Per-relic stat aggregates. Keyed by relic id (e.g. "RELIC.BAG_OF_MARBLES").</summary>
    public Dictionary<string, RelicAggregate> RelicAggregates { get; set; } = new();

    /// <summary>
    /// Chronological potion provenance for this run. A record begins when a
    /// potion is visibly offered or successfully enters the belt, then keeps
    /// the observed acquisition, use/discard, or held-at-run-end outcome for
    /// that same potion. Sequence is monotonic within the run so duplicate
    /// potion definitions remain separate history entries.
    /// </summary>
    public List<PotionRunHistoryEntry> PotionHistory { get; set; } = new();

    /// <summary>
    /// Every observed change to the tracked player's maximum HP, in run order.
    /// Entries retain the exact before/after values instead of reconstructing
    /// deltas from per-floor game-history totals, so multiple changes on one
    /// floor (for example Feed followed by Chosen Cheese) stay distinct.
    /// </summary>
    public List<MaxHpRunHistoryEntry> MaxHpHistory { get; set; } = new();

    /// <summary>
    /// Observed current-HP loss grouped by the two non-recoverable run
    /// contexts exposed in the HP tooltip. Combat loss is buffered until the
    /// combat finishes; event loss is persisted when it occurs. Combats is a
    /// zero-inclusive denominator, so the average includes fights where no HP
    /// was lost.
    /// </summary>
    public RunHealthStats HealthStats { get; set; } = new();

    /// <summary>
    /// Observed positive gold balance changes and game-classified spending,
    /// with zero-inclusive room/combat denominators for the run-level gold
    /// tooltip. Lost and stolen gold remain outside spending totals.
    /// </summary>
    public RunGoldStats GoldStats { get; set; } = new();

    /// <summary>
    /// Run outcomes grouped by the six selectable categories shown in the
    /// map legend. Visits retain the original map-point type (so a ? remains
    /// a ?), while the Unknown bucket also records what those sites resolved
    /// into. Combat outcomes are buffered with the combat that produced them.
    /// </summary>
    public RunMapLegendStats MapLegendStats { get; set; } = new();

    /// <summary>
    /// Run-time seconds attributed from the game's own pause-aware run clock.
    /// Combat time is committed only with its completed combat; reward, event,
    /// and map time are accumulated while their native surface is active.
    /// The sampling cursor is persisted so Continue and Core hot reload can
    /// resume without counting the same interval twice.
    /// </summary>
    public RunTimeStats TimeStats { get; set; } = new();

    /// <summary>
    /// Ordered provenance for the portion of the player's gold balance that
    /// still matters to source attribution. Old Coin seeds the first tracked
    /// chunk; ordinary balance before and after it is represented by chunks
    /// without a source relic id. The ledger can be cleared once no attributed
    /// gold remains.
    /// </summary>
    public List<GoldAttributionChunk> GoldAttributionLedger { get; set; } = new();

    /// <summary>
    /// The floor of an active Maw Bank shop visit that has not yet been
    /// resolved by entering the next room. Persisting the floor makes the
    /// visit idempotent across Continue and Core hot reload while the shop is
    /// still open.
    /// </summary>
    public int? MawBankPendingShopFloor { get; set; }

    /// <summary>Per-enemy stat aggregates. Keyed by monster id (e.g. "MONSTER.HAUNTED_SHIP").</summary>
    public Dictionary<string, EnemyAggregate> EnemyAggregates { get; set; } = new();

    /// <summary>
    /// Run-level facts surfaced on related cards when that card is the natural
    /// place to inspect the mechanic, but the value is not caused by that
    /// specific card instance.
    /// </summary>
    public RunMetaStats MetaStats { get; set; } = new();

    /// <summary>
    /// Snapshot of per-instance number assignments, serialized so that hot
    /// reload mid-run can resume with the same numbers instead of losing
    /// the CardModel-ref → number mapping (which only lives in memory on
    /// the soon-to-be-orphaned Core assembly).
    ///
    /// Format: <c>{def_id → [number, number, ...]}</c> where the list is
    /// ordered by each card's current deck-rank among cards of the same
    /// def_id. Example: if the deck has 4 Strikes with instance numbers
    /// #1, #2, #4, #5 (because #3 was Smith'd), this stores
    /// <c>{"STRIKE": [1, 2, 4, 5]}</c>.
    ///
    /// On resume: walk the live deck, compute each card's
    /// (def_id, rank-among-same-def), look up the number, repopulate
    /// <c>RunTracker._instanceNumbers</c>. Removal-safe because rank is
    /// relative to the CURRENT deck composition.
    ///
    /// Presence of this field (or <see cref="DefCounters"/>) at the top
    /// level of an on-disk JSON file is also the structural marker that
    /// the file uses the per-instance shape. Files predating per-instance
    /// identity lack both fields entirely.
    /// </summary>
    public Dictionary<string, List<int>> InstanceNumbersByDef { get; set; } = new();

    /// <summary>
    /// Snapshot of the monotonic per-def counters. Preserves the invariant
    /// that numbers never get reused across hot reload — if the saved state
    /// had Strike #1..#5 and #3 was removed, <c>DefCounters["STRIKE"] == 5</c>,
    /// so the next added Strike becomes #6 (not a recycled #3).
    /// </summary>
    public Dictionary<string, int> DefCounters { get; set; } = new();

    // (Intentionally no PendingCombat field — pending-combat persistence
    // was explored and rejected. Rule-of-use: F5 between combats, not
    // during. Rest/shop/event/reward rooms are all safe because
    // _pendingCombat is null outside of active combat. See git history
    // on 2026-04-20 for the full PendingCombatSnapshot approach if we
    // ever decide to re-enable mid-combat persistence.)
}

/// <summary>
/// Run-wide observed current-HP loss and its combat denominator.
/// </summary>
public class RunHealthStats
{
    public decimal HpLostInCombats { get; set; }
    public decimal HpLostInEvents { get; set; }
    public int Combats { get; set; }
}

/// <summary>
/// Run-wide observed gold flow and its rate denominators.
/// </summary>
public class RunGoldStats
{
    public int GoldAcquired { get; set; }
    public int GoldSpent { get; set; }
    public int GoldSpentInShops { get; set; }
    public int GoldSpentInEvents { get; set; }
    public int GoldGainedInCombats { get; set; }
    public int GoldGainedInEvents { get; set; }
    public int ShopsVisited { get; set; }
    public int EventsVisited { get; set; }
    public int Combats { get; set; }
    public int? LastShopFloorCounted { get; set; }
    public int? LastEventFloorCounted { get; set; }
}

/// <summary>
/// Run-wide time spent on the timer's tracked gameplay surfaces. All elapsed
/// values use whole seconds because RunManager.RunTime is the authoritative
/// pause-aware clock shown by the stock timer.
/// </summary>
public class RunTimeStats
{
    public long CombatSeconds { get; set; }
    public long RewardScreenSeconds { get; set; }
    public long EventSeconds { get; set; }
    public long MapSeconds { get; set; }
    public int Combats { get; set; }
    public int CombatTurns { get; set; }

    /// <summary>
    /// Internal resumable sampling cursor. These fields are presentation-
    /// neutral and additive; older run files simply begin sampling when first
    /// observed by a build that understands timer stats.
    /// </summary>
    public long? LastObservedRunTime { get; set; }
    public string? ActiveCategory { get; set; }
}

/// <summary>
/// Per-map-icon run summaries plus the currently entered original map point.
/// The current point is persisted so Continue and Core hot reload preserve
/// attribution while the player is still resolving that room.
/// </summary>
public class RunMapLegendStats
{
    public MapLegendCategoryStats Unknown { get; set; } = new();
    public MapLegendCategoryStats Shop { get; set; } = new();
    public MapLegendCategoryStats Treasure { get; set; } = new();
    public MapLegendCategoryStats RestSite { get; set; } = new();
    public MapLegendCategoryStats Monster { get; set; } = new();
    public MapLegendCategoryStats Elite { get; set; } = new();

    public string? CurrentPointType { get; set; }
    public int? CurrentPointFloor { get; set; }
}

/// <summary>
/// Observed outcomes attributable to one visible map-legend category.
/// Zero-inclusive denominators (visits and completed combats) make the
/// displayed averages useful even when a room produced no reward or loss.
/// </summary>
public class MapLegendCategoryStats
{
    public int Visits { get; set; }
    public int? FirstVisitFloor { get; set; }
    public int? LastVisitFloor { get; set; }
    public int FloorsBetweenVisitsTotal { get; set; }
    public int FloorsBetweenVisitsSamples { get; set; }

    public int ResolvedEvents { get; set; }
    public int ResolvedCombats { get; set; }
    public int ResolvedElites { get; set; }
    public int ResolvedShops { get; set; }
    public int ResolvedTreasures { get; set; }
    public int ResolvedRestSites { get; set; }
    public int? LastResolutionFloor { get; set; }

    public int CombatsCompleted { get; set; }
    public int CombatsWon { get; set; }
    public int PerfectCombats { get; set; }
    public int CombatTurns { get; set; }

    public decimal HpLost { get; set; }
    public decimal HpHealed { get; set; }
    public int GoldGained { get; set; }
    public int GoldSpent { get; set; }
    public int CardsGained { get; set; }
    public int CardsUpgraded { get; set; }
    public int RelicsGained { get; set; }
    public int PotionsOffered { get; set; }
    public int PotionsGained { get; set; }
}

/// <summary>
/// One applied maximum-HP mutation. Source is best-effort presentation
/// context; the before/after values are always observed from the creature.
/// </summary>
public class MaxHpRunHistoryEntry
{
    public int Sequence { get; set; }
    public int Floor { get; set; }
    public string LocationKind { get; set; } = "";
    public string? LocationName { get; set; }
    public int? Turn { get; set; }
    public string? SourceName { get; set; }
    public int PreviousMaxHp { get; set; }
    public int NewMaxHp { get; set; }
}

/// <summary>
/// One potion offer/acquisition lifecycle in a run. Location fields are
/// deliberately additive and presentation-neutral: the gallery can render a
/// compact floor/kind/name label without needing the live room object later.
/// </summary>
public class PotionRunHistoryEntry
{
    public int Sequence { get; set; }
    public string PotionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string AcquisitionMethod { get; set; } = "";
    public string? Rarity { get; set; }

    public int? SeenFloor { get; set; }
    public string? SeenLocationKind { get; set; }
    public string? SeenLocationName { get; set; }
    public int? SeenTurn { get; set; }

    public bool Acquired { get; set; }
    public int? AcquiredFloor { get; set; }
    public string? AcquiredLocationKind { get; set; }
    public string? AcquiredLocationName { get; set; }
    public int? AcquiredTurn { get; set; }

    public bool Used { get; set; }
    public int? UsedFloor { get; set; }
    public string? UsedLocationKind { get; set; }
    public string? UsedLocationName { get; set; }
    public int? UsedTurn { get; set; }

    // Actual current HP restored by this potion's own use callback. Nullable
    // keeps historical entries distinct from newly observed zero healing.
    // Blood Potion is the first potion to populate this outcome.
    public int? HpGained { get; set; }

    // Observed cards returned by a potion-owned draw command and the
    // unfulfilled portion of that request. Nullable fields distinguish older
    // history entries from newly observed zero outcomes.
    public int? CardsDrawn { get; set; }
    public int? CardDrawsBlocked { get; set; }

    // Block granted by a potion and its later shared-pool outcomes. Effective
    // and wasted attribution use the same FIFO/LIFO contributor ledger as
    // cards and relics.
    public int? BlockGained { get; set; }
    public int? BlockEffective { get; set; }
    public int? BlockWasted { get; set; }

    // Buffer charges this potion granted, and what those specific charges went
    // on to prevent. Charges granted is exact — the application names its
    // source. Which charge absorbed which hit is FIFO ledger attribution, the
    // same heuristic the Block rows above use.
    public int? BufferChargesGranted { get; set; }
    public int? BufferChargesUsed { get; set; }
    public decimal? BufferDamagePrevented { get; set; }

    // Observed results from a potion-owned damage command. Nullable fields
    // distinguish older history entries from newly observed zero outcomes.
    // Explosive Ampoule is the first potion to populate this outcome set.
    public int? DamageAttempted { get; set; }
    public int? DamageDealt { get; set; }
    public int? DamageBlocked { get; set; }
    public int? DamageOverkill { get; set; }
    public int? Kills { get; set; }
    public int? TargetsHit { get; set; }

    public bool Discarded { get; set; }
    public int? DiscardedFloor { get; set; }
    public string? DiscardedLocationKind { get; set; }
    public string? DiscardedLocationName { get; set; }
    public int? DiscardedTurn { get; set; }

    // True only after the concrete PotionReward reaches its terminal skip
    // callback. A merely visible or unsuccessfully clicked reward remains
    // unresolved, so the belt summary does not prematurely call it rejected.
    public bool RejectedAtRewardScreen { get; set; }

    public bool HeldAtRunEnd { get; set; }
    public int? HeldAtRunEndFloor { get; set; }
}

/// <summary>
/// One definition within a random-generator outcome ledger. Generated counts
/// successful arrivals; generated-cards-played counts distinct physical
/// generated cards used at least once; plays includes replays.
/// </summary>
public class GeneratedCardOutcomeAggregate
{
    public string CardId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Generated { get; set; }
    public int GeneratedCardsPlayed { get; set; }
    public int Plays { get; set; }
}

/// <summary>
/// Shared observed-outcome shape for cards and Powers that select a random
/// combat card. Exact generated-card objects are followed only within their
/// combat; this additive aggregate is the durable run summary.
/// </summary>
public class RandomCardGenerationAggregate
{
    public int CardsGenerated { get; set; }
    public int GeneratedCardsPlayed { get; set; }
    public int GeneratedCardPlays { get; set; }

    public int BasicCardsGenerated { get; set; }
    public int CommonCardsGenerated { get; set; }
    public int UncommonCardsGenerated { get; set; }
    public int RareCardsGenerated { get; set; }
    public int StatusCardsGenerated { get; set; }
    public int CurseCardsGenerated { get; set; }
    public int OtherRarityCardsGenerated { get; set; }

    public int AttacksGenerated { get; set; }
    public int SkillsGenerated { get; set; }
    public int PowersGenerated { get; set; }
    public int OtherTypeCardsGenerated { get; set; }

    public int UpgradedCardsGenerated { get; set; }
    public int XCostCardsGenerated { get; set; }
    public int EnergyCostBeforeDiscountTotal { get; set; }
    public int EnergyDiscountGrantedTotal { get; set; }

    public int CardsAddedToHand { get; set; }
    public int CardsAddedToDrawPile { get; set; }
    public int CardsAddedToDiscardPile { get; set; }
    public int CardsAddedElsewhere { get; set; }

    public Dictionary<string, GeneratedCardOutcomeAggregate> CardsById { get; set; }
        = new();
}

/// <summary>Aggregated per-card attribution stats for this run.</summary>
public class CardAggregate
{
    // Number of combats that started while this physical card was in the
    // player's permanent deck. Useful denominator for dud cards that may
    // rarely be drawn or played.
    public int CombatsInDeck { get; set; }

    public int Plays { get; set; }

    // M1: Attack attribution. Null/zero for non-attack cards.
    public int TotalIntended { get; set; }   // damage the card tried to deal (pre-block, including overkill)
    public int TotalBlocked { get; set; }    // damage absorbed by target block
    public int TotalOverkill { get; set; }   // damage past target HP (wasted)
    public int TotalEffective { get; set; }  // damage that actually moved HP (observed unblocked damage)
    public int Kills { get; set; }           // times the card landed a killing blow

    // M3a: Energy spent. Sum of CardPlay.Resources.EnergySpent across every
    // play of this card instance. Uses EnergySpent (actual energy paid) not
    // EnergyValue (listed cost) so cost modifiers like Mummified Hand show
    // up correctly — a Strike played from Hand at 0 cost counts 0 here.
    // Average is derived on the display side via TotalEnergySpent / Plays.
    public int TotalEnergySpent { get; set; }

    // M3j: Energy generated directly by this card while it is resolving.
    // Sourced from PlayerCombatState.GainEnergy, attributed to the currently-
    // resolving card play. Tracks the ACTUAL amount added to the pool after
    // clamping / prevention, not the raw text on the card, so "gain 1" under
    // a no-energy-gain effect correctly records 0.
    public int TotalEnergyGenerated { get; set; }

    // The half of the generated energy that expired unspent. Energy is a
    // fungible pool, so this comes from an ordered provenance ledger exactly
    // like TotalBlockWasted: gains append a chunk tagged with their source,
    // spends consume oldest-first, and whatever the pool still holds when the
    // game overwrites it at turn start is charged back to the newest chunks.
    // Generated minus wasted is therefore the energy this card really bought
    // you, and an unpaired TotalEnergyGenerated no longer implies every point
    // of it was used.
    public int TotalEnergyWasted { get; set; }

    // M3k: Regent star spend / generation mirrors the energy fields above,
    // but for the character's separate star resource.
    public int TotalStarsSpent { get; set; }
    public int TotalStarsGenerated { get; set; }

    // M3l: Forge granted directly by this card while it is resolving.
    // Stored as decimal because forge values are sourced from the game's
    // dynamic vars / command path, which are decimal-backed even when most
    // current cards use whole numbers.
    public decimal TotalForgeGenerated { get; set; }

    // Orbs successfully channeled while this card was resolving. The game
    // emits OrbChanneledEntry only after the orb actually enters the queue,
    // so full-slot evocations, capacity changes, and failed channels remain
    // observed outcomes rather than being inferred from card text.
    public int TotalOrbsCreated { get; set; }
    public Dictionary<string, CardOrbAggregate> OrbOutcomes { get; set; } = new();

    // Potion procurement outcomes caused by this card. Alchemize is the
    // first card using these fields: gained counts only successful observed
    // procure results, rarity buckets use the potion actually returned by
    // the command, and skipped counts failed procure results.
    public int PotionsGained { get; set; }
    public int CommonPotionsGained { get; set; }
    public int UncommonPotionsGained { get; set; }
    public int RarePotionsGained { get; set; }
    public int PotionsSkipped { get; set; }

    // Shared random-card generation outcomes. This supplements bespoke
    // choice-screen details (Discovery and Splash) with one consistent,
    // successful-arrival and later-utilization model across the whole family.
    public RandomCardGenerationAggregate? RandomCardGeneration { get; set; }

    // Jack of All Trades generated-card outcomes. The total counts only cards
    // that the combat pile command confirms were added. Rarity/type buckets
    // and energy cost are read from that actual added card, not the candidate
    // Jack selected before add hooks and pile rules ran.
    public int JackColorlessCardsAdded { get; set; }
    public int JackUncommonCardsAdded { get; set; }
    public int JackRareCardsAdded { get; set; }
    public int JackAttacksAdded { get; set; }
    public int JackSkillsAdded { get; set; }
    public int JackPowersAdded { get; set; }
    public int JackAddedCardCostTotal { get; set; }

    // Discovery choice outcomes. CardsPicked is the denominator for average
    // discount and counts only non-null selections from Discovery's own
    // choose-card screen. The discount is the selected card's observed
    // effective energy-cost reduction when Discovery makes it free.
    // The Offered counters are the pick counters' denominators: every option
    // on the screens this physical Discovery opened, so each pick bucket can
    // be read against what the card actually put in front of the player.
    public int DiscoveryCardsOffered { get; set; }
    public int DiscoveryCommonCardsOffered { get; set; }
    public int DiscoveryUncommonCardsOffered { get; set; }
    public int DiscoveryRareCardsOffered { get; set; }
    public int DiscoveryAttacksOffered { get; set; }
    public int DiscoverySkillsOffered { get; set; }
    public int DiscoveryPowersOffered { get; set; }
    public int DiscoveryCardsPicked { get; set; }
    public int DiscoveryCommonCardsPicked { get; set; }
    public int DiscoveryUncommonCardsPicked { get; set; }
    public int DiscoveryRareCardsPicked { get; set; }
    public int DiscoveryAttacksPicked { get; set; }
    public int DiscoverySkillsPicked { get; set; }
    public int DiscoveryPowersPicked { get; set; }
    public int DiscoveryEnergyDiscountTotal { get; set; }

    // Splash choice outcomes. AttacksTaken is the denominator for average
    // discount and counts only non-null Attack selections from Splash's own
    // choose-card screen. The discount is the selected Attack's observed
    // effective energy-cost reduction when Splash makes it free.
    public int SplashAttacksTaken { get; set; }
    public int SplashCommonAttacksTaken { get; set; }
    public int SplashUncommonAttacksTaken { get; set; }
    public int SplashRareAttacksTaken { get; set; }
    public int SplashEnergyDiscountTotal { get; set; }

    // All for One outcomes. Counts only zero-cost non-X Attack/Skill/Power
    // cards observed moving from Discard into Hand while this physical All
    // for One is resolving. CombatsInDeck and Plays provide its averages.
    public int AllForOneZeroCostCardsReturned { get; set; }

    // Outbreak outcomes. This is effective HP damage observed from the exact
    // PoisonPower.Trigger calls made while this physical Outbreak resolves.
    // Ordinary side-turn Poison ticks are excluded.
    public int OutbreakExtraPoisonTriggerDamage { get; set; }

    // Armaments outcomes. Counts successful UpgradeInternal calls observed
    // while this physical Armaments is resolving. Armaments+ may add several
    // upgrades in one play; cards that were already fully upgraded never
    // enter UpgradeInternal and therefore are not counted.
    public int ArmamentsCardsUpgraded { get; set; }

    // Drain Power outcomes. CardsUpgraded counts only UpgradeInternal calls
    // observed while this physical Drain Power is resolving. The raw combat
    // cards it upgraded are remembered for the rest of that combat so their
    // later completed plays can be credited back to this source instance.
    // TurnsInDeck and the shared CombatsInDeck field are held denominators,
    // including turns/combats where Drain Power produces no upgrade or play.
    public int DrainPowerCardsUpgraded { get; set; }
    public int DrainPowerTurnsInDeck { get; set; }
    public int DrainPowerUpgradedCardPlays { get; set; }

    // Debt's end-of-turn curse effect. The intended amount comes from the
    // card's Gold dynamic var; actual loss is observed from the owner's gold
    // balance before/after the callback. Any unaffordable remainder is kept
    // separately so a trigger at zero gold is still visible and explainable.
    public int DebtTriggers { get; set; }
    public int DebtGoldLost { get; set; }
    public int DebtGoldLossBlocked { get; set; }

    // Normality turn-end exposure. A qualifying turn is observed before hand
    // cleanup while this exact physical Normality is still in Hand. The
    // energy total includes zero-energy turns so the display-side average is
    // over every qualifying turn, not only turns with leftover energy.
    public int NormalityTurnsEndedInHand { get; set; }
    public int NormalityExcessEnergyAtTurnEndTotal { get; set; }

    // M2a: Block gained (how much block this card contributed over the run,
    // summed across plays). M2b extends this with absorbed/wasted splits
    // using an ordered provenance ledger for the player's block pool.
    public int TotalBlockGained { get; set; }
    public int TotalBlockEffective { get; set; }

    // Buffer charges this physical card granted, and what those charges
    // prevented. Charges granted is exact. Prevention is FIFO ledger
    // attribution across every live charge, matching the Block ledger: the
    // game merges all Buffer into one Counter power and spends charges in no
    // order it exposes, so oldest-first is a stated convention, not a fact.
    public int BufferChargesGranted { get; set; }
    public int BufferChargesUsed { get; set; }
    public decimal BufferDamagePrevented { get; set; }
    public int TotalBlockWasted { get; set; }

    // M3c: Draw count. Every time this card instance gets drawn — at
    // turn start or via card-effect draw ("draw 2 cards"). Shows up-
    // stream of plays: you can't play a card without drawing it first,
    // so TimesDrawn >= Plays always. Useful for efficiency signals like
    // "drew 10 times, played 4" (you've been stuck with dead draws).
    public int TimesDrawn { get; set; }

    // M3e: Discarded count. Every time this card goes to the discard
    // pile — end-of-turn (still in hand), mid-combat discard effects,
    // etc. Meaningful signal when high relative to plays ("I keep
    // discarding this without playing it").
    public int TimesDiscarded { get; set; }

    // M3f: Pile-top placement counts. Tracks when THIS card gets placed
    // on top of the draw pile from specific sources. Useful for cards
    // that manipulate draw order (Shining Strike's self-retain after
    // play, Finisher effects putting attacks back on top, etc.).
    //   FromHand: card was in hand, got moved to top of draw (retain-style)
    //   FromDiscard: card was in discard pile, got moved to top of draw
    //     ("from graveyard" in player parlance)
    public int TimesPlacedOnTopFromHand { get; set; }
    public int TimesPlacedOnTopFromDiscard { get; set; }

    // M3g: Exhaust attribution. When THIS card's play caused OTHER cards
    // to be exhausted. Covers Havoc (exhausts the auto-played card), Fiend
    // Fire (exhausts the hand), Second Wind (exhausts non-attacks), etc.
    // Self-exhaust (card exhausts itself after play) is NOT counted here
    // — different signal, different meaning.
    public int TimesExhaustedOtherCards { get; set; }

    // Type breakdown of the exhausts above. Subsets of
    // TimesExhaustedOtherCards, not extra events: a curse exhausted by
    // Fiend Fire increments both the total and TimesExhaustedOtherCurses.
    // Kept separate because "exhausted 4 others" reads very differently
    // when those others were curses or statuses (deck cleaning) rather
    // than live cards (a real cost). Rendered only when non-zero.
    public int TimesExhaustedOtherCurses { get; set; }
    public int TimesExhaustedOtherStatusCards { get; set; }

    // M3g2: How often THIS card itself got exhausted, regardless of cause.
    // Useful for exhaust-tag cards, ephemeral generated cards, and effects
    // that consume a card from hand/discard. Shown only when > 0 on the
    // full tooltip.
    public int TimesExhausted { get; set; }

    // M3h: Player HP loss from playing this card. Tracks Ironclad-style
    // self-damage (Hemokinesis, Offering, Combust tick, etc.). Uses the
    // damage's UnblockedDamage, which is POST-reduction — so Tungsten Rod
    // / buffer effects naturally show up as less HP loss. That's the
    // truth of "what did this card actually cost me", not what its
    // listed damage says.
    public int TotalHpLost { get; set; }

    // Maximum HP actually lost from playing this card. Kept separate from
    // TotalHpLost because reducing max HP is a permanent run cost even when
    // the player's current HP does not move. Brightest Flame is the first
    // card using this field.
    public int TotalMaxHpLost { get; set; }

    // Maximum HP actually gained from playing this card. Feed is the first
    // card using this field. Its printed Fatal amount is not assumed: the
    // tracker records the owner's observed max-HP delta after Feed's complete
    // async play callback succeeds.
    public int TotalMaxHpGained { get; set; }

    // M3i: Draw attribution. When THIS card's play causes OTHER cards to
    // be drawn. Signal for draw-enabler cards (Prepared, Coolheaded,
    // Acrobatics etc. depending on the character). Excludes turn-start
    // auto-draw via the game's FromHandDraw flag.
    public int TimesCardsDrawn { get; set; }

    // M3i1: Total card draw attempts caused by THIS card's play, regardless
    // of whether each draw actually succeeded. Lets the tooltip show the gap
    // between "tried to draw X" and "actually drew Y" without caring whether
    // the miss came from No Draw, full hand fallback, or another prevention
    // path.
    public int TimesCardsDrawAttempted { get; set; }

    // M3i2: Blocked draw attribution. When THIS card's play ATTEMPTS to draw
    // cards but a draw-prevention hook vetoes the attempt (Battle Trance,
    // future "can't draw" effects, etc.). Counts blocked cards separately
    // from successful draws so draw cards don't silently look like they drew
    // zero when the game explicitly prevented them.
    public int TimesCardsDrawBlocked { get; set; }

    // M3i3: Categorized blocked-draw reasons for THIS card's draw attempts.
    // Keeps the card-side gap explainable without caring about the exact
    // blocker implementation: No Draw, hand full, or an "other" bucket when
    // the game prevented the draw for some reason we didn't categorize yet.
    public Dictionary<string, BlockedDrawReasonAggregate> BlockedDrawReasons { get; set; } = new();

    // M3m: Successful self-summons into Hand. Counts actual arrivals in
    // Hand, not mere attempts, so hand-full redirects to Discard stay out
    // of this number.
    public int TimesSummonedToHand { get; set; }

    // M3n: Unleash-specific Osty HP scaling. Unleash adds Osty's current HP
    // to its attack payload; these fields track that contribution separately
    // from the normal observed damage totals.
    public int TotalOstyHpAttackBonus { get; set; }
    public int TimesOstyHpAttackBonusApplied { get; set; }

    // M3o: Card-sourced successful Osty summons. These are direct card
    // contributions: how often this card summoned/revived Osty and how much
    // Osty HP the command actually added.
    public int TimesOstySummoned { get; set; }
    public decimal TotalOstyHpSummoned { get; set; }

    // Successful generated/transformed Soul arrivals caused while this card
    // was resolving. The final combat pile is observed after the game's add
    // and redirection hooks, rather than inferred from card text.
    public int SoulsAddedToDrawPile { get; set; }
    public int SoulsAddedToHand { get; set; }
    public int SoulsAddedToDiscardPile { get; set; }

    // M3p: Extra plays caused by the game's replay/multi-play series. Total
    // Plays already includes these; this field tracks the subset where the
    // finished CardPlay was not the first play in its series.
    public int TimesReplayExtraPlanned { get; set; }
    public Dictionary<string, ReplayExtraPlayReasonAggregate> ReplayExtraPlayPlannedReasons { get; set; } = new();
    public int TimesReplayExtraPlayed { get; set; }
    public Dictionary<string, ReplayExtraPlayReasonAggregate> ReplayExtraPlayReasons { get; set; } = new();
    public int TimesReplayAttackNoDamage { get; set; }
    public Dictionary<string, ReplayExtraPlayReasonAggregate> ReplayAttackNoDamageReasons { get; set; } = new();

    // M4a: Effect / power application summary for this specific card
    // instance. First pass tracks ONLY that the card caused a power/effect
    // to be applied, not what the downstream effect later did. Keyed by the
    // game's power id (e.g. "POWER.NECROBINDER_TRIGGER"), with localized
    // display text cached for tooltip rendering.
    public Dictionary<string, AppliedEffectAggregate> AppliedEffects { get; set; } = new();

    // M3d: Per-instance lineage (when the card entered the deck and at
    // what upgrade level). Lets us distinguish between "card arrived
    // upgraded" (bought from a shop pre-upgraded, event reward, etc.) and
    // "card upgraded during the run" (rest site / Armaments etc.).
    //
    //   FloorAdded:         CardModel.FloorAddedToDeck snapshot at first
    //                       observation. Null = starting deck (the game
    //                       leaves this null for the initial 5).
    //   InitialUpgradeLevel: CurrentUpgradeLevel at first observation.
    //                       If > 0, the card arrived already upgraded.
    //
    // Subsequent permanent-deck upgrades are recorded in the Events log as
    // "card_upgraded" entries with Floor + UpgradeLevel, so the tooltip can
    // render a full lineage like "Arrived: floor 3, +1" followed by
    // "Upgraded: floor 6 → +2". Temporary combat-copy upgrades are excluded.
    public int? FloorAdded { get; set; }
    public int InitialUpgradeLevel { get; set; }

    // M5a: Removal tracking. When a card is removed from the deck (Smith,
    // event, curse-dispose, etc.), we mark the aggregate rather than
    // delete it — so the user can browse "what did I remove this run and
    // how was it performing?" via the deck-view injection.
    //   Removed: true once the card left the permanent deck
    //   RemovedAtFloor: floor the removal happened on
    //   RemovalSource: best-effort observed source (Shopkeeper, a named
    //     event/relic, a card effect, or a room interaction)
    //   RemovalGoldCost: exact merchant removal charge when applicable
    //   RemovedSnapshot: the card's full serializable state at removal —
    //     upgrade level, enchantment, props, etc. Used on resume to
    //     reconstruct a CardModel ref matching the removed card's state
    //     (via CardModel.FromSerializable) so the deck-view injection
    //     renders it correctly post-reload.
    public bool Removed { get; set; }
    public int? RemovedAtFloor { get; set; }
    public string? RemovalSource { get; set; }
    public int? RemovalGoldCost { get; set; }
    public MegaCrit.Sts2.Core.Saves.Runs.SerializableCard? RemovedSnapshot { get; set; }

    // M3c: Draw count attribution. Null until M3c.
}

/// <summary>
/// Lifecycle outcomes for exact orb instances created by one physical card,
/// grouped by orb definition for compact persistence and display.
/// </summary>
public class CardOrbAggregate
{
    public string OrbId { get; set; } = "";
    public int Created { get; set; }
    public int PassiveActivations { get; set; }
    public int Evokes { get; set; }
    public int Fizzles { get; set; }

    // Observed block created by this orb type. This remains separate from the
    // originating card's direct block totals and provenance ledger.
    public int BlockGained { get; set; }

    // Observed energy added by Plasma activations. This is kept on the exact
    // originating orb rather than attributed to whichever card caused an
    // eviction/evoke while the Plasma orb happened to be in the queue.
    public int EnergyGenerated { get; set; }

    // Observed damage created by this orb type. These remain separate from the
    // originating card's direct Attack damage so cards such as Ball Lightning
    // expose both values without blending them.
    public int DamageAttempted { get; set; }
    public int DamageDealt { get; set; }
    public int DamageBlocked { get; set; }
    public int DamageOverkill { get; set; }
    public int Kills { get; set; }
    public int TargetsHit { get; set; }
}

public class RunMetaStats
{
    // Shared Osty-body facts. Bound Phylactery and Phylactery Unbound both
    // project these values so upgrading the relic never resets its history.
    public decimal TotalOstyHpSummoned { get; set; }
    public decimal TotalOstyHpWhenUnleashPlayed { get; set; }
    public decimal TotalOstyDamageAbsorbed { get; set; }
    public int OstyBodyTurns { get; set; }
    public int OstyBodyCombats { get; set; }
    public decimal ExtraBlockGainedFromUnmovablePower { get; set; }

    // Power-owned outcomes that should not be attributed to one physical
    // source-card instance. The related card can project these aggregate
    // values in its tooltip without pretending that one copy of the card
    // caused every later activation of the shared power.
    public Dictionary<string, PowerAggregate> PowerAggregates { get; set; } = new();
}

public class PowerAggregate
{
    public string PowerId { get; set; } = "";
    public string DisplayName { get; set; } = "";

    // Shared meta-power accounting. One aggregate represents every physical
    // and generated play of the same Power card definition.
    //
    // DeckTurns: every player turn in a combat where at least one permanent
    // copy of the Power card was in the deck at combat setup. Multiple copies
    // do not multiply this denominator.
    // ActiveTurns: one unit for each turn where the power family was active.
    // ActiveApplicationTurns: one unit per successful application per active
    // turn, so two played copies contribute two units on later shared turns.
    public int PowerCardsPlayed { get; set; }
    public int GeneratedPowerCardsPlayed { get; set; }
    public int SuccessfulApplications { get; set; }
    public int MetaDeckTurns { get; set; }
    public int MetaActiveTurns { get; set; }
    public int MetaActiveApplicationTurns { get; set; }

    // Random cards created by recurring Power callbacks. These belong to the
    // shared Power identity rather than whichever physical card first applied
    // the stack. MetaActiveTurns and MetaActiveApplicationTurns provide the
    // zero-inclusive denominators.
    public RandomCardGenerationAggregate? RandomCardGeneration { get; set; }

    // Orb outcomes produced later by recurring Power callbacks (Storm,
    // Spinner, Trash to Treasure, and Lightning Rod). These are folded into
    // the originating card family's tooltip without assigning them to one
    // physical card copy.
    public int TotalOrbsCreated { get; set; }
    public Dictionary<string, CardOrbAggregate> OrbOutcomes { get; set; } = new();

    // Matching observation-era numerators for the three meta-power rate
    // denominators above. Lifetime totals predate these denominators in saved
    // runs, so dividing those older totals by newer denominators would create
    // false rates.
    // Damage a power dealt itself, as opposed to damage from a card play.
    // Panache is the first power in the game to deal any, so these are the
    // first damage fields PowerAggregate has ever needed. Named to match the
    // equivalents on CardAggregate so the tooltip vocabulary lines up.
    public int TotalIntended { get; set; }
    public int TotalBlocked { get; set; }
    public int TotalOverkill { get; set; }
    public int TotalEffective { get; set; }
    public int Kills { get; set; }

    public int RateAttacksCopied { get; set; }
    public int RateTimesTriggered { get; set; }
    public decimal RateBlockGained { get; set; }
    public int RateEntropyCardsGenerated { get; set; }
    public int RateViciousCardsDrawn { get; set; }
    public int RateDarkEmbraceCardsDrawn { get; set; }
    public int RateStampedeAttacksPlayed { get; set; }
    public int RateStampedeEnergySaved { get; set; }
    public int RateAggressionCardsReturnedToHand { get; set; }
    public int RateAggressionCardsUpgraded { get; set; }
    public decimal RateStrengthGained { get; set; }
    public decimal UnmovableExtraBlockGained { get; set; }
    public decimal RateUnmovableExtraBlockGained { get; set; }

    // Consuming Shadow tracking. This counts only completed orb evocations
    // reached through the power's own end-of-turn EvokeLast calls. It is
    // power-owned because multiple card applications stack into one shared
    // Consuming Shadow power.
    public int OrbsEvoked { get; set; }

    // Juggling tracking. Copies count only generated Attack cards confirmed
    // by the combat-pile add result. Turns and combats are held-power
    // denominators and include active periods with no copy.
    public int AttacksCopied { get; set; }
    public int CommonAttacksCopied { get; set; }
    public int UncommonAttacksCopied { get; set; }
    public int RareAttacksCopied { get; set; }
    public int TurnsActive { get; set; }
    public int CombatsActive { get; set; }

    // Danse Macabre tracking. A trigger is one qualifying owner card observed
    // by the shared power. Block is the post-modifier amount returned by the
    // power's exact gain-block command. Active denominators include the
    // application turn and later zero-trigger turns while the power remains.
    public int TimesTriggered { get; set; }
    public decimal BlockGained { get; set; }

    // Unrelenting / Free Attack tracking. The power owns these outcomes
    // because multiple physical Unrelenting cards contribute to one shared
    // stack. A use is confirmed only when FreeAttackPower completes its
    // charge decrement. Energy saved is the power's observed marginal
    // reduction from the cost immediately before its own late modifier.
    public int FreeAttackChargesGranted { get; set; }
    public int FreeAttackChargesUsed { get; set; }
    public int FreeAttackZeroEnergySavingsUses { get; set; }
    public decimal FreeAttackEnergySaved { get; set; }
    public int FreeAttackBasicAttacksDiscounted { get; set; }
    public int FreeAttackCommonAttacksDiscounted { get; set; }
    public int FreeAttackUncommonAttacksDiscounted { get; set; }
    public int FreeAttackRareAttacksDiscounted { get; set; }

    // Pounce / Free Skill tracking. This deliberately mirrors Free Attack:
    // shared stacks belong to the power aggregate, a use requires the native
    // decrement to complete, and savings use the power's own marginal cost
    // reduction rather than the card's printed cost.
    public int FreeSkillChargesGranted { get; set; }
    public int FreeSkillChargesUsed { get; set; }
    public int FreeSkillZeroEnergySavingsUses { get; set; }
    public decimal FreeSkillEnergySaved { get; set; }
    public int FreeSkillBasicSkillsDiscounted { get; set; }
    public int FreeSkillCommonSkillsDiscounted { get; set; }
    public int FreeSkillUncommonSkillsDiscounted { get; set; }
    public int FreeSkillRareSkillsDiscounted { get; set; }

    // Entropy tracking. Generated cards are confirmed from successful
    // CardCmd.Transform results. Chains broken counts replacements whose
    // original card had the Queen's Bound affliction before transformation.
    public int EntropyChainsOfBindingBroken { get; set; }
    public int EntropyCardsGenerated { get; set; }
    public int EntropyCommonCardsGenerated { get; set; }
    public int EntropyUncommonCardsGenerated { get; set; }
    public int EntropyRareCardsGenerated { get; set; }

    // Vicious tracking. This is the number of cards confirmed by the exact
    // draw command issued when the shared power reacts to its owner applying
    // Vulnerable. Failed/blocked draws contribute zero.
    public int ViciousCardsDrawn { get; set; }

    // Dark Embrace tracking. Drawn cards are the observed results of both its
    // immediate Exhaust draw and deferred Ethereal batch. TurnsActive counts
    // only turns while the power is active; DarkEmbraceCombatTurns counts all
    // player turns in combats where it became active.
    public int DarkEmbraceCardsDrawn { get; set; }
    public int DarkEmbraceCombatTurns { get; set; }

    // Stampede tracking. An Attack counts only after the exact autoplay
    // selected by Stampede reaches a finished primary play. Energy saved is
    // that play's resolved EnergyValue; autoplay itself spends zero.
    public int StampedeAttacksPlayed { get; set; }
    public int StampedeCommonAttacksPlayed { get; set; }
    public int StampedeUncommonAttacksPlayed { get; set; }
    public int StampedeRareAttacksPlayed { get; set; }
    public int StampedeEnergySaved { get; set; }

    // Aggression tracking. Returned cards count only successful moves from the
    // discard pile into hand. Upgrades are observed separately at the exact
    // card mutation, so a full hand or an already-upgraded card cannot inflate
    // the other outcome.
    public int AggressionCardsReturnedToHand { get; set; }
    public int AggressionCardsUpgraded { get; set; }

    // Rupture tracking. Strength gained is the observed positive change across
    // Rupture's own payoff callbacks. TurnsActive is the zero-inclusive
    // denominator for the power's per-active-turn average.
    public decimal StrengthGained { get; set; }

    // Buffer tracking. Buffer is a Counter power with no per-application
    // identity, so charges from every source (the Buffer card, Lucky Tonic)
    // stack into one shared power and belong to this pooled aggregate rather
    // than to one physical card instance.
    //
    // ChargesGranted counts observed positive applications to the tracked
    // player. ChargesUsed counts only spends the power's own post-modifier
    // callback confirmed, so a fully blocked or zero-damage hit never burns
    // one. DamagePrevented is the post-Block HP loss the power actually
    // zeroed, not the attack's printed or intended damage.
    //
    // Charges that never prevented anything are (granted - used): Buffer does
    // not survive a combat, so that difference stays exact at run level and
    // needs no separately persisted expiry field.
    public int BufferChargesGranted { get; set; }
    public int BufferChargesUsed { get; set; }
    public decimal BufferDamagePrevented { get; set; }
}

public class EnemyAggregate
{
    public string EnemyId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int DamageInstances { get; set; }
    public int DamageAttempted { get; set; }
    public int DamageDealt { get; set; }
    public int DamageBlocked { get; set; }
    public int StatusCardsAdded { get; set; }
    public int StatusCardsAddedToHand { get; set; }
    public int StatusCardsAddedToDraw { get; set; }
    public int StatusCardsAddedToDiscard { get; set; }
    public int StatusCardsAddedToDeck { get; set; }
    public Dictionary<string, EnemyStatusCardAggregate> StatusCardsById { get; set; } = new();
}

public class EnemyStatusCardAggregate
{
    public string CardId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>
/// First-pass effect/power tracking credited back to a card instance.
/// Keeps enough display metadata for tooltip rendering without forcing the
/// UI to re-query live game state for historical runs.
/// </summary>
public class AppliedEffectAggregate
{
    public string EffectId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? IconPath { get; set; }
    public int TimesApplied { get; set; }
    public decimal TotalAmountApplied { get; set; }
    public int TimesBlockedByArtifact { get; set; }
    public decimal TotalAmountBlockedByArtifact { get; set; }
    public decimal TotalTriggeredEffectiveDamage { get; set; }
    public decimal TotalTriggeredOverkill { get; set; }
    public int TotalTriggeredCardsDrawBlocked { get; set; }
}

public class BlockedDrawReasonAggregate
{
    public string ReasonId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Count { get; set; }
}

public class HealingLostReasonAggregate
{
    public string ReasonId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public decimal Amount { get; set; }
}

public class ReplayExtraPlayReasonAggregate
{
    public string ReasonId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Count { get; set; }
}

public class GoldAttributionChunk
{
    public string? SourceRelicId { get; set; }
    public int AmountRemaining { get; set; }
}

/// <summary>
/// Aggregated stats for a single relic across this run.
/// Fields are shared across relics; each relic uses only the fields relevant to it.
/// </summary>
public class RelicAggregate
{
    // Total times this relic's tracked effect activated.
    // Used by room/trigger-based relics such as Meal Ticket and Tuning Fork.
    public int Activations { get; set; }

    // Total enemies across all combats this run that had a debuff applied
    // by this relic at combat start.
    public int EnemiesAffected { get; set; }

    // Total Vulnerable stacks applied by this relic across all combats.
    // Used by Bag of Marbles (1 Vulnerable per enemy).
    public int VulnerableApplied { get; set; }

    // Total Weak stacks applied by this relic across all combats.
    // Used by Red Mask (1 Weak per enemy at combat start).
    public int WeakApplied { get; set; }

    // Dynamic effect/power tracking credited to this relic. Used by Unsettling
    // Lamp for debuffs beyond the fixed Vulnerable/Weak rows.
    public Dictionary<string, AppliedEffectAggregate> AppliedEffects { get; set; } = new();

    // Total additional cards drawn by this relic across the run, plus the
    // relic-attributed requests that did not produce a card.
    // Used by Pocketwatch (draws 3 extra cards when 3 or fewer cards were
    // played last turn), Gremlin Horn (draws after enemy death), Pendulum
    // (draws every N turns), Game Piece (draws after an owner Power), and
    // Booming Conch (draws extra cards at Elite combat start).
    public int AdditionalCardsDrawn { get; set; }
    public int AdditionalCardDrawsBlocked { get; set; }

    // Gremlin Horn's zero-inclusive held-period denominators. Lifetime
    // activations, observed Energy, and observed draws use shared fields.
    // RateActivations is separate from the legacy lifetime activation total
    // so upgraded runs do not divide historical triggers by new denominators.
    public int GremlinHornRateActivations { get; set; }
    public int GremlinHornTurns { get; set; }
    public int GremlinHornCombats { get; set; }

    // Game Piece's zero-inclusive held-period denominators. Its observed
    // draw total uses AdditionalCardsDrawn.
    public int GamePieceTurns { get; set; }
    public int GamePieceCombats { get; set; }

    // Centennial Puzzle's once-per-combat activation context. The turn total
    // is summed at the exact HP-loss callback and uses the owning player's
    // turn number. Turn-side and HP-loss-source buckets are mutually
    // exclusive within their respective groups.
    public int CentennialPuzzleActivationTurnTotal { get; set; }
    public int CentennialPuzzleActivationTurnSamples { get; set; }
    public int CentennialPuzzlePlayerTurnActivations { get; set; }
    public int CentennialPuzzleOpponentTurnActivations { get; set; }
    public int CentennialPuzzleStatusActivations { get; set; }
    public int CentennialPuzzleCurseActivations { get; set; }
    public int CentennialPuzzleEnemySourceActivations { get; set; }

    // Pocketwatch held-period observations. A turn is missed when its ending
    // card count is above Pocketwatch's threshold; an activation is counted
    // only when the relic actually adds cards to the following hand draw.
    public int PocketwatchTurns { get; set; }
    public int PocketwatchCombats { get; set; }
    public int PocketwatchTurnEndCountTotal { get; set; }
    public int PocketwatchTurnsActivationMissed { get; set; }
    public int PocketwatchActivatedTurnEndCountTotal { get; set; }
    public int PocketwatchActivationValueSamples { get; set; }
    public int PocketwatchMissedTurnEndCountTotal { get; set; }

    // Pollinous Core held-period observations. The relic increments its saved
    // counter before each hand draw, adds two cards on the fourth turn, then
    // resets to zero before that turn ends. AdditionalCardsDrawn stores the
    // observed marginal cards that actually reached the hand.
    public int PollinousCoreTurns { get; set; }
    public int PollinousCoreCombats { get; set; }
    public int PollinousCoreTurnsEndedOn0Counters { get; set; }
    public int PollinousCoreTurnsEndedOn1Counter { get; set; }
    public int PollinousCoreTurnsEndedOn2Counters { get; set; }
    public int PollinousCoreTurnsEndedOn3Counters { get; set; }

    // Joss Paper observes every owned card exhausted while held. Each full
    // five-card threshold is one activation and one requested card draw.
    // AdditionalCardsDrawn / AdditionalCardDrawsBlocked store the observed
    // draw result. End-of-turn snapshots retain its 0-4 remainder
    // distribution, including the deferred Ethereal exhaust batch.
    public int JossPaperCardsExhausted { get; set; }
    public int JossPaperTurns { get; set; }
    public int JossPaperCombats { get; set; }
    public int JossPaperTurnsEndedOn0Counters { get; set; }
    public int JossPaperTurnsEndedOn1Counter { get; set; }
    public int JossPaperTurnsEndedOn2Counters { get; set; }
    public int JossPaperTurnsEndedOn3Counters { get; set; }
    public int JossPaperTurnsEndedOn4Counters { get; set; }

    // Combats where Pendulum was held, including combats too short for it to
    // activate. Used as the denominator for cards drawn per combat.
    public int PendulumCombats { get; set; }

    // Pendulum's live 0-2 turn counter at the end of each completed combat.
    public int PendulumCombatsEndedOn0Charges { get; set; }
    public int PendulumCombatsEndedOn1Charge { get; set; }
    public int PendulumCombatsEndedOn2Charges { get; set; }
    public int PendulumCombatEndChargeTotal { get; set; }
    public int PendulumCombatEndChargeCount { get; set; }

    // The owner's turn number at each Pendulum activation. Pendulum can
    // activate several times in one combat, so samples count activations
    // rather than combats and exclude held combats too short to activate;
    // combats without an activation must not pull the average toward zero.
    public int PendulumActivationTurnTotal { get; set; }
    public int PendulumActivationTurnSamples { get; set; }

    // Total block gained from this relic across all combats.
    // Used by Orichalcum, Permafrost, The Abacus, Bone Flute, Cloak Clasp,
    // Anchor, Horn Cleat, Captain's Wheel, Tuning Fork, Ornamental Fan,
    // Regalite, and Vambrace's extra block from its multiplier.
    public int AdditionalBlockGained { get; set; }

    // Relic-owned block provenance outcomes. Ornamental Fan currently uses
    // these to retain sole ownership of its block after the shared player
    // pool later absorbs damage or expires unused.
    public int AdditionalBlockEffective { get; set; }
    public int AdditionalBlockWasted { get; set; }

    // Cloak Clasp held-period denominators. Turns include every player turn
    // where the relic was held, even when combat ended before its turn-end
    // callback or the player had no cards in hand.
    public int CloakClaspTurns { get; set; }
    public int CloakClaspCombats { get; set; }

    // Combats where Permafrost was held, including combats where no Power was
    // played. Used as the zero-inclusive denominator for triggers per combat.
    public int PermafrostCombats { get; set; }

    // Total times this relic got its own trigger check but was blocked by
    // its condition not being met. Used by Orichalcum when the player already
    // has block at end of turn.
    public int BlockedTriggers { get; set; }

    // Orichalcum's leftover-Block shortfall. A turn is undercut when it ended
    // holding 1..(threshold - 1) Block: the relic stayed silent, and the
    // leftover is worth less than the trigger would have been. BlockMissed sums
    // threshold - leftover across those turns and is a counterfactual — it
    // assumes the leftover Block could have been spent or avoided. Turns that
    // ended at or above the threshold lose nothing and are excluded, so this is
    // a strict subset of BlockedTriggers. OrichalcumTurns and
    // OrichalcumCombats are the zero-inclusive held-period denominators, so
    // turns and combats that missed nothing stay in both averages.
    public int OrichalcumTurnsUndercut { get; set; }
    public int OrichalcumBlockMissed { get; set; }
    public int OrichalcumTurns { get; set; }
    public int OrichalcumCombats { get; set; }

    // Total Strength this relic added. Used by Girya, Reptile Trinket,
    // Shuriken, Ruined Helmet, Sword of Jade, and Toasty Mittens.
    public decimal StrengthAdded { get; set; }

    // Toasty Mittens outcomes and its zero-inclusive held-combat denominator.
    public int ToastyMittensCardsExhausted { get; set; }
    public int ToastyMittensCombats { get; set; }

    // Reptile Trinket held-period denominators and per-turn activation
    // distribution. The two turn buckets are mutually exclusive.
    public int ReptileTrinketTurns { get; set; }
    public int ReptileTrinketCombats { get; set; }
    public int ReptileTrinketTurnsWithExactlyTwoActivations { get; set; }
    public int ReptileTrinketTurnsWithMoreThanTwoActivations { get; set; }

    // Rainbow Ring held-period denominators. Activations use the shared
    // Activations field; the current-turn card-type state is read live.
    public int RainbowRingTurns { get; set; }
    public int RainbowRingCombats { get; set; }

    // Final-turn distribution for completed combats where Sparkling Rouge was
    // held. These buckets are mutually exclusive.
    public int SparklingRougeCombatsEndedOnTurn1 { get; set; }
    public int SparklingRougeCombatsEndedOnTurn2 { get; set; }
    public int SparklingRougeCombatsEndedOnTurn3Plus { get; set; }

    // Beating Remnant tracking. The prevented amount is the positive
    // before/after delta at the relic's own post-Osty HP-loss modifier.
    // Turns and combats are zero-inclusive held denominators.
    public decimal BeatingRemnantHpLossPrevented { get; set; }
    public int BeatingRemnantTurns { get; set; }
    public int BeatingRemnantCombats { get; set; }

    // Whispering Earring tracking. Life lost is the sum of observed downward
    // current-HP deltas from player turn 1 through opponent turn 1. Combats is
    // a zero-inclusive held denominator.
    public decimal WhisperingEarringFirstRoundHpLost { get; set; }
    public int WhisperingEarringCombats { get; set; }

    // Tungsten Rod tracking. Each numerator is the exact positive delta at
    // the rod's own post-Osty HP-loss modifier. Turns and combats are shared,
    // zero-inclusive held denominators for both the total and source buckets.
    public decimal TungstenRodDamagePrevented { get; set; }
    public decimal TungstenRodSelfDamagePrevented { get; set; }
    public decimal TungstenRodCurseDamagePrevented { get; set; }
    public decimal TungstenRodStatusDamagePrevented { get; set; }
    public decimal TungstenRodEnemyDamagePrevented { get; set; }
    public int TungstenRodTurns { get; set; }
    public int TungstenRodCombats { get; set; }

    // Total Plating this relic added. Used by Gorget.
    public decimal PlatingAdded { get; set; }

    // Total cards this relic upgraded. Used by Stone Cracker, Razor Tooth,
    // Sand Castle, Whetstone, Yummy Cookie, War Paint, Fishing Rod, War
    // Hammer, and other upgrade-granting relics.
    public int CardsUpgraded { get; set; }
    public List<string> UpgradedCards { get; set; } = new();

    // Fishing Rod floor pacing. Distances contain completed samples from
    // acquisition to the first observed qualifying event, then between
    // consecutive normal combats or successful upgrades. The last-floor
    // snapshots let tracking continue accurately across saves and hot reloads.
    public List<int> FishingRodCombatFloorDistances { get; set; } = new();
    public int? FishingRodLastCombatFloor { get; set; }
    public List<int> FishingRodUpgradeFloorDistances { get; set; } = new();
    public int? FishingRodLastUpgradeFloor { get; set; }

    // Stone Cracker tracking. The upgraded-card set itself is combat-local
    // because the relic upgrades draw-pile combat instances, not permanent
    // deck cards. Combats/turns are zero-inclusive held denominators.
    public int StoneCrackerUpgradedCommons { get; set; }
    public int StoneCrackerUpgradedUncommons { get; set; }
    public int StoneCrackerUpgradedRares { get; set; }
    public int StoneCrackerUpgradedCardPlays { get; set; }
    public int StoneCrackerCombats { get; set; }
    public int StoneCrackerTurns { get; set; }

    // War Hammer tracking. The id list preserves the exact permanent deck
    // cards upgraded after Elite victories so their later completed plays can
    // be recognized across combats and hot reloads. Combats/turns are held
    // denominators and include zero-play periods.
    public List<string> WarHammerUpgradedCardInstanceIds { get; set; } = new();
    public int WarHammerUpgradedCardPlays { get; set; }
    public int WarHammerCombats { get; set; }
    public int WarHammerTurns { get; set; }

    // Permanent deck cards whose Sharp enchantment was applied or increased
    // by Gnarled Hammer's pickup effect.
    public List<string> SharpEnchantedCards { get; set; } = new();

    // Reward cards whose Glam enchantment came from Silken Tress and were
    // successfully taken into the permanent deck.
    public List<string> SilkenTressGlamCards { get; set; } = new();

    // Permanent deck cards whose Instinct enchantment was applied or increased
    // by Tri-Boomerang. Stable instance ids let later combat-copy plays remain
    // attributable across combats, saves, and hot reloads.
    public List<RelicEnchantedCardAggregate> TriBoomerangInstinctCards { get; set; } = new();
    public int TriBoomerangInstinctCardPlays { get; set; }
    public int TriBoomerangCombats { get; set; }

    // Razor Tooth tracking. Combats/turns are held denominators for averages.
    // Plays and draws count only events after the exact combat card was
    // successfully upgraded by Razor Tooth; the triggering play is excluded.
    public int RazorToothCombats { get; set; }
    public int RazorToothTurns { get; set; }
    public int RazorToothUpgradedCardPlays { get; set; }
    public int RazorToothUpgradedCardDraws { get; set; }

    // Total times Bone Flute triggered from an owned Osty attack.
    public int BoneFluteTriggers { get; set; }

    // Total Osty HP successfully summoned by this relic across the run.
    // Used by Bound Phylactery and Phylactery Unbound.
    public decimal TotalOstyHpSummoned { get; set; }

    // Curse-triggered max HP tracking. Used by Darkstone Periapt when a
    // curse successfully enters the owner's permanent deck.
    public int CursesAcquired { get; set; }
    public int TotalMaxHpGained { get; set; }

    // Healing attribution. Attempted is what the relic requested, restored is
    // observed HP actually gained, lost is the gap. Lost reasons keep the gap
    // explainable (full HP, prevention/modification, etc.). Used by Book
    // Repair Knife, Eternal Feather, Planisphere, Lee's Waffle, and other
    // healing relics.
    public decimal TotalHealingAttempted { get; set; }
    public decimal TotalHealingRestored { get; set; }
    public decimal TotalHealingLost { get; set; }
    public Dictionary<string, HealingLostReasonAggregate> HealingLostReasons { get; set; } = new();

    // Eternal Feather heals on rest-site entry before the selected campfire
    // action resolves. The game's run history combines both sources in one
    // floor-level HP-healed value, so retain the Feather's observed share per
    // activation for an exact campfire-history split.
    public List<EternalFeatherHealingActivationAggregate> EternalFeatherHealingActivations { get; set; } = new();

    // Deck size observed at each Eternal Feather rest-site activation. It is
    // the input the game divides to size the heal, so it is kept as its own
    // numerator/denominator pair rather than inferred from the activation
    // list, which only exists for activations that reached a floor number.
    public int EternalFeatherDeckCardsTotal { get; set; }
    public int EternalFeatherDeckCardsSamples { get; set; }

    // Legacy numerator from the first pre-trigger HP implementation. It used
    // max HP as the baseline and is retained only so files written by that
    // short-lived build remain loadable; new observations do not update it.
    public decimal MeatOnTheBonePreTriggerHpMissingTotal { get; set; }

    // Legacy positive-magnitude distance below 50% from the second
    // pre-trigger HP implementation. Retained so runs written by that
    // short-lived build can be projected into the signed relative-to-half
    // stat; new observations do not update these fields.
    public decimal MeatOnTheBonePreTriggerHpBelowHalfTotal { get; set; }
    public int MeatOnTheBonePreTriggerHpBelowHalfSamples { get; set; }

    // Signed raw HP-point difference from exactly 50% max HP immediately
    // before a qualifying Meat on the Bone trigger. Negative is below half,
    // positive is above half, and zero is exactly half.
    public decimal MeatOnTheBonePreTriggerHpRelativeToHalfTotal { get; set; }
    public int MeatOnTheBonePreTriggerHpRelativeToHalfSamples { get; set; }

    // The percentage total stores one normalized percentage per trigger so
    // max-HP changes do not distort the average.
    public decimal MeatOnTheBonePreTriggerHpPercentTotal { get; set; }
    public int MeatOnTheBonePreTriggerHpSamples { get; set; }

    // Relic lifecycle floor snapshots. Used by one-shot relics such as Lizard
    // Tail and Wongo's Mystery Ticket to show where they were acquired and
    // where their effect activated.
    public int? FloorAcquired { get; set; }
    public int? FloorActivated { get; set; }

    // Ordered Elite victories recorded while Sword in the Stone was owned.
    // The history survives its replacement by Sword of Jade so the transformed
    // relic can continue presenting the original acquisition/progression story.
    public List<SwordInTheStoneEliteSlainAggregate> SwordInTheStoneElitesSlain { get; set; } = new();

    // Sword of Jade's matching observation-era Strength numerator and
    // zero-inclusive held-combat denominator. Lifetime StrengthAdded predates
    // these fields, so it must not be divided by only newly observed combats.
    // Pre-transformation Sword in the Stone combats are deliberately excluded.
    public decimal SwordInTheStoneStrengthRateAdded { get; set; }
    public int SwordInTheStoneStrengthCombats { get; set; }

    // Tooltip-only projection populated from the live pending combat aggregate.
    // Internal properties are not part of the persisted System.Text.Json shape.
    internal decimal SwordInTheStoneStrengthAddedThisCombat { get; set; }

    // Girya's matching observation-era Strength numerator and eligible-combat
    // denominator. The floor samples are one counter snapshot per entered floor
    // while held (including its acquisition floor), so time spent at each Lift
    // count is weighted naturally. Use distances run from acquisition to the
    // first Lift and then between consecutive successful Lifts.
    public decimal GiryaStrengthRateAdded { get; set; }
    public int GiryaStrengthCombats { get; set; }
    public int GiryaCountFloorTotal { get; set; }
    public int GiryaFloorSamples { get; set; }
    public int? GiryaLastFloorSampled { get; set; }
    public int GiryaUseFloorDistanceTotal { get; set; }
    public int GiryaUseFloorDistanceSamples { get; set; }
    public int? GiryaLastUseFloor { get; set; }
    public int GiryaLastObservedLiftCount { get; set; }

    // Downstream use while Girya is providing Strength. Mirrors Vajra's
    // Attack-play and resolved enemy-hit counters, but excludes the pre-Lift
    // period when Girya has not added any Strength yet.
    public int GiryaAttacksPlayed { get; set; }
    public int GiryaAttackHits { get; set; }

    // Tooltip-only projection populated from the live pending combat aggregate.
    internal decimal GiryaStrengthAddedThisCombat { get; set; }

    // Total observed maximum HP gained by pickup max-HP relics and Chosen Cheese.
    public decimal MaxHpGained { get; set; }

    // Shared current-HP before/after snapshot for one-shot relic pickup
    // effects. Fragrant Mushroom records the HP surrounding its awaited
    // unblockable damage rather than assuming its listed 15 HP loss landed.
    public decimal? StartingHp { get; set; }
    public decimal? ResultingHp { get; set; }

    // Ordered observed before/after max-HP snapshots for repeatable relic
    // effects. Used by Stone Humidifier so each rest-site activation remains
    // inspectable even when unrelated max-HP changes happen between rests.
    public List<RelicMaxHpActivationAggregate> MaxHpActivations { get; set; } = new();

    // Shared max-HP before/after snapshot for relics that add or remove max HP.
    // Original is the first observed value before the relic changed max HP; New
    // is the latest observed value after its max-HP change resolved.
    // For Chosen Cheese, Original stores pickup-time starting max HP and New is
    // intentionally unused because other max-HP effects can interleave between
    // later Chosen Cheese gains.
    public decimal? OriginalMaxHp { get; set; }
    public decimal? NewMaxHp { get; set; }

    // Total times a relic triggered from one or more confirmed Doom deaths.
    // Used by Book Repair Knife.
    public int DoomDeathTriggers { get; set; }

    // Total enemies included in confirmed Doom-death trigger payloads.
    // Used by Book Repair Knife.
    public int DoomKills { get; set; }

    // Total energy generated by this relic across all combats.
    // Used by Art of War, Happy Flower (gains 1 energy every 3 turns), Gremlin Horn
    // (gains after enemy death), Booming Conch (gains at Elite combat start),
    // Lantern/Very Hot Cocoa/Candelabra/Chandelier (gain energy at the start
    // of turns 1/1/2/3),
    // Prismatic Gem, Blood-Soaked Rose, and Pumpkin Candle (max-energy relics
    // counted once per player energy reset), and Seal of Gold (turn-start
    // energy purchased with gold). Also used by Nunchaku, whose gained energy
    // is attributed from the observed PlayerCombatState.GainEnergy delta.
    public int EnergyGenerated { get; set; }

    // The relic-owned counterpart to CardAggregate.TotalEnergyWasted: how much
    // of EnergyGenerated expired unspent, resolved through the same player
    // energy ledger. Relics that hand you energy on a turn you had nothing to
    // spend it on show the difference here.
    public int EnergyWasted { get; set; }

    // Pumpkin Candle's zero-inclusive live charge at each combat start plus
    // successful selections of its Kindle campfire option. Initial pickup
    // charges are deliberately not a rekindle.
    public int PumpkinCandleCombatStartChargeTotal { get; set; }
    public int PumpkinCandleCombatStartChargeSamples { get; set; }
    public int PumpkinCandleRekindles { get; set; }

    // Spiked Gauntlets tracking. Taxed Powers are completed owner Power plays
    // while the relic is held. Cost is the resolved play-time EnergyValue;
    // spent is the observed EnergySpent. Turns and combats are zero-inclusive
    // held denominators. EnergyGenerated stores its Ancient max-energy benefit.
    public int SpikedGauntletsTaxedPowersPlayed { get; set; }
    public decimal SpikedGauntletsPowerCostTotal { get; set; }
    public int SpikedGauntletsPowerEnergySpent { get; set; }
    public int SpikedGauntletsTurns { get; set; }

    // Gold attributed to a relic effect. Lucky Fysh measures the owner's
    // completed balance delta after its gold command resolves; Bowler Hat
    // stores only the observed bonus beyond the unmodified integer grant;
    // Amethyst Aubergine records the concrete extra GoldReward amount it adds;
    // Maw Bank measures the completed balance delta from its room-entry callback.
    public int GoldGained { get; set; }

    // Shops entered while Maw Bank was active and then left without spending
    // gold. A pending visit is resolved at the next distinct room entry using
    // the relic's own saved HasItemBeenBought state.
    public int MawBankShopsSkipped { get; set; }

    // Actual gold removed by transactions the game classifies as Spent while
    // Maw Bank was still active and the player's current room was not a shop.
    public int MawBankGoldSpentOutsideShops { get; set; }

    // Old Coin's observed pickup grant and the portion of that attributed
    // grant later consumed by game transactions marked as Spent. A run-level
    // FIFO gold ledger keeps pre-existing balance ahead of the grant and later
    // unrelated gains behind it.
    public int OldCoinGoldGranted { get; set; }
    public int OldCoinGoldSpent { get; set; }

    // Membership Card tracking. Saved is the per-purchase gap between the
    // merchant price re-read with only this relic's discount suppressed and
    // the gold the purchase actually spent, so a second price modifier keeps
    // its own share. The held/earned pair frames that saving against the
    // relic's own cost: the balance left the moment it was obtained, and every
    // later observed gold gain while it was still held.
    public int MembershipCardGoldSaved { get; set; }
    public int? MembershipCardGoldHeldAfterPurchase { get; set; }
    public int MembershipCardGoldEarnedAfterPurchase { get; set; }

    // Permanent deck additions confirmed by a relic-owned pile-change
    // callback. Used by Lucky Fysh and Book of Five Rings.
    public int CardsAddedToDeck { get; set; }

    // Curses among those confirmed permanent-deck additions.
    public int CursesAddedToDeck { get; set; }

    // Lucky Fysh's confirmed permanent-deck additions split by the room the
    // add was observed in. Combats cover Monster, Elite, and Boss rooms.
    public int LuckyFyshCardsAddedInCombats { get; set; }
    public int LuckyFyshCardsAddedInShops { get; set; }
    public int LuckyFyshCardsAddedInEvents { get; set; }
    public int LuckyFyshCardsAddedInCampfires { get; set; }

    // The observed gold those same additions paid out, on the same split.
    // These sum to at most GoldGained, which also covers additions in rooms
    // outside the four buckets.
    public int LuckyFyshGoldGainedInCombats { get; set; }
    public int LuckyFyshGoldGainedInShops { get; set; }
    public int LuckyFyshGoldGainedInEvents { get; set; }
    public int LuckyFyshGoldGainedInCampfires { get; set; }

    // Rooms of each type entered while Lucky Fysh was held — the denominators
    // for its per-room averages. A room counts once per floor, and a room
    // where an addition was observed always counts even if the relic arrived
    // after the room-entered hook had already run.
    public int LuckyFyshCombatsHeld { get; set; }
    public int LuckyFyshShopsHeld { get; set; }
    public int LuckyFyshEventsHeld { get; set; }
    public int LuckyFyshCampfiresHeld { get; set; }

    // Last floor already counted into each held total, keeping Continue and
    // Core reload callbacks idempotent.
    public int? LuckyFyshLastCombatFloorHeld { get; set; }
    public int? LuckyFyshLastShopFloorHeld { get; set; }
    public int? LuckyFyshLastEventFloorHeld { get; set; }
    public int? LuckyFyshLastCampfireFloorHeld { get; set; }

    // Completed outer card rewards skipped while Book of Five Rings was held.
    public int CardRewardsSkipped { get; set; }

    // Gold actually lost to a relic effect and the portion of its attempted
    // loss that did not leave the player's balance. Seal of Gold is the first
    // relic using these observed outcome fields; attempted loss is derived
    // from its activation count and fixed five-gold cost in the tooltip.
    public int GoldLost { get; set; }
    public int GoldLossBlocked { get; set; }

    // Combats held for relics whose energy-generation tooltip reports a
    // per-combat average. Used by Art of War, Happy Flower, Nunchaku, and
    // Seal of Gold.
    public int EnergyGeneratedCombats { get; set; }

    // Player turns where Art of War was held. Includes turns where an Attack
    // on the preceding turn prevented its energy gain.
    public int ArtOfWarTurns { get; set; }

    // Tooltip-only projections populated from the live pending combat.
    // Internal properties are not part of the persisted System.Text.Json shape.
    internal int ArtOfWarEnergyAddedThisCombat { get; set; }
    internal int ArtOfWarEnergyAddedThisTurn { get; set; }
    internal int ArtOfWarTurnsThisCombat { get; set; }

    // Lifecycle outcomes for exact starting Lightning orb instances created
    // by Cracked Core or Infused Core. Property names retain the original
    // Cracked Core prefix for schema compatibility.
    public int CrackedCoreOrbEvokes { get; set; }
    public int CrackedCoreOrbPassiveTriggers { get; set; }
    public int CrackedCoreOrbFizzles { get; set; }

    // Lifecycle outcomes for the exact Dark orb instance created by Symbiotic
    // Virus at the start of combat.
    public int SymbioticVirusOrbEvokes { get; set; }
    public int SymbioticVirusOrbPassiveTriggers { get; set; }
    public int SymbioticVirusOrbFizzles { get; set; }

    // Gold-Plated Cables tracking. Activations is the total number of confirmed
    // extra passive triggers with a first orb available. The per-orb ledger
    // preserves the exact orb type selected by the game's modifier hook.
    // Empty opportunities are sampled at each owner end-turn orb-queue pass.
    public Dictionary<string, RelicOrbActivationAggregate>
        GoldPlatedCablesActivationsByOrbType { get; set; } = new();
    public int GoldPlatedCablesNoOrbTargets { get; set; }

    // Total relevant player turns that ended with unspent energy while the
    // matching turn-energy relic was held.
    public int FirstTurnsEndedWithExcessEnergy { get; set; }
    public int SecondTurnsEndedWithExcessEnergy { get; set; }
    public int ThirdTurnsEndedWithExcessEnergy { get; set; }

    // Total Vigor applied by Akabeko's combat-start effect.
    public int VigorGained { get; set; }

    // Pen Nib tracking. TotalDamageAttempted stores base damage added; these
    // fields store the attack-play denominator and turn-end charge snapshots.
    public int PenNibAttacksPlayed { get; set; }
    public int PenNibTurnsEndedOn8Charges { get; set; }
    public int PenNibTurnsEndedOn9Charges { get; set; }
    public int PenNibTurnEndChargeTotal { get; set; }
    public int PenNibTurnEndChargeCount { get; set; }

    // Total attempted/base damage from relic effects whose actual damage is not
    // source-attributed by the game's damage entries. Used by Letter Opener
    // and observed-damage relics such as Parrying Shield, Festive Popper, and
    // Mercury Hourglass. For Pen Nib, this stores the raw per-hit amount
    // handed to the damage command: the extra base damage added, before
    // downstream hook effects such as Lethality or Vulnerable.
    public int TotalDamageAttempted { get; set; }

    // Relic damage outcome split. Used by relics such as Cracked Core,
    // Parrying Shield, Festive Popper, Mercury Hourglass, and Forgotten Soul
    // when their strikes actually resolve through the game's damage command.
    public int TotalDamageDealt { get; set; }
    public int TotalDamageBlocked { get; set; }
    public int TotalDamageOverkill { get; set; }
    public int Kills { get; set; }

    // Screaming Flagon held-period observations. Hand-size total samples the
    // owner's actual hand at each player turn end, including empty hands.
    // Turns and combats are zero-inclusive denominators for its averages.
    public int ScreamingFlagonTurnEndHandSizeTotal { get; set; }
    public int ScreamingFlagonTurns { get; set; }
    public int ScreamingFlagonCombats { get; set; }

    // Forgotten Soul held-period denominators. Activations count every
    // same-owner card exhaust callback, including a callback with no hittable
    // enemy; damage and targets come only from the resolved damage command.
    public int ForgottenSoulTurns { get; set; }
    public int ForgottenSoulCombats { get; set; }

    // Total targets included in those attempted relic-damage payloads. Used by
    // Cracked Core, Letter Opener, Parrying Shield, Festive Popper, Mercury
    // Hourglass, and Forgotten Soul.
    public int TotalTargets { get; set; }

    // Mercury Hourglass held-period denominator. Activations count every
    // owner turn-start trigger, so this zero-inclusive combat count is what
    // its per-combat averages divide by.
    public int MercuryHourglassCombats { get; set; }

    // Letter Opener tracking. TotalDamageAttempted stores the attempted AoE
    // damage, TotalTargets stores live enemy targets at activation time, and
    // these fields provide average denominators plus turn-end charge buckets.
    public int LetterOpenerSkillsPlayed { get; set; }
    public int LetterOpenerCombats { get; set; }
    public int LetterOpenerTurns { get; set; }
    public int LetterOpenerTurnsEndedAt1Charge { get; set; }
    public int LetterOpenerTurnsEndedAt2Charges { get; set; }

    // Tuning Fork tracking. Counts owner Skill plays, held combats/turns for
    // averages, and player-turn-end charge snapshots for near-activation
    // pressure on its persistent 10-Skill counter.
    public int TuningForkSkillsPlayed { get; set; }
    public int TuningForkCombats { get; set; }
    public int TuningForkTurns { get; set; }
    public int TuningForkTurnsEndedOn8Charges { get; set; }
    public int TuningForkTurnsEndedOn9Charges { get; set; }
    public int TuningForkTurnEndChargeTotal { get; set; }
    public int TuningForkTurnEndChargeCount { get; set; }

    // Ripple Basin tracking. Activations and AdditionalBlockGained preserve
    // successful no-Attack turn-end outcomes; these held denominators include
    // turns and combats where the relic granted no block.
    public int RippleBasinCombats { get; set; }
    public int RippleBasinTurns { get; set; }

    // Total potions gained from this relic, split by the potion rarity that
    // was actually claimed. Used by White Beast Statue.
    public int PotionsGained { get; set; }
    public int CommonPotionsGained { get; set; }
    public int UncommonPotionsGained { get; set; }
    public int RarePotionsGained { get; set; }
    public int PotionsSkipped { get; set; }

    // Potion-slot relic held-period observations. The total includes zero-potion
    // starts, and samples counts every combat where the individual relic was
    // owned. Used by Potion Belt, Alchemical Coffer, and Phial Holster.
    public int CombatStartPotionCountTotal { get; set; }
    public int CombatStartPotionCountSamples { get; set; }

    // Petrified Toad's observed Potion Shaped Rock procurement outcomes.
    // Only TooFull failures belong to the blocked-by-full-belt count;
    // unrelated blockers such as NotAllowed remain excluded.
    public int PetrifiedToadPotionsGiven { get; set; }
    public int PetrifiedToadPotionsBlockedByFullBelt { get; set; }

    // Tiny Mailbox tracking. Offers are the exact PotionReward objects added
    // by its rest-heal callback and are counted once when selected or skipped,
    // after the game has populated their concrete potion. Fruit Juice is an
    // overlapping subset of Rare offers. Campfires not rested mirrors
    // Shovel's unused-option count.
    public int TinyMailboxPotionsOffered { get; set; }
    public int TinyMailboxPotionsTaken { get; set; }
    public int TinyMailboxCommonPotionsOffered { get; set; }
    public int TinyMailboxUncommonPotionsOffered { get; set; }
    public int TinyMailboxRarePotionsOffered { get; set; }
    public int TinyMailboxFruitJuicesOffered { get; set; }
    public int TinyMailboxCampfiresNotRested { get; set; }

    // Total relics acquired from this relic, split by the obtained relic's
    // actual rarity. Used by Shovel's Dig rest-site option.
    public int RelicsAcquired { get; set; }
    public int CommonRelicsAcquired { get; set; }
    public int UncommonRelicsAcquired { get; set; }
    public int RareRelicsAcquired { get; set; }
    public int CampfiresNotDug { get; set; }

    // Specific relics granted by relic-owned effects. Used by Large Capsule,
    // Neow's Bones, Pael's Wing, and Wongo's Mystery Ticket to show which
    // relics were obtained.
    public Dictionary<string, RelicGrantedAggregate> RelicsGranted { get; set; } = new();

    // Ordered physical wax relics bestowed by Toy Box. Each entry keeps the
    // original relic identity plus its observed acquisition and melt floors;
    // the relic's ordinary effect stats continue to live under its own id.
    public List<ToyBoxWaxRelicAggregate> ToyBoxWaxRelics { get; set; } = new();

    // Ordered relic rewards created by Small Capsule. Each entry preserves
    // the concrete relic rolled by the game and whether it was taken, skipped,
    // or is still awaiting a decision.
    public List<RelicRewardChoiceAggregate> RelicRewardChoices { get; set; } = new();

    // Total offered cards by rarity for relics that generate card-choice
    // screens. Used by Toolbox, White Star, and Prayer Wheel.
    public int CommonCardsOffered { get; set; }
    public int UncommonCardsOffered { get; set; }
    public int RareCardsOffered { get; set; }
    public int CommonCardsTaken { get; set; }
    public int UncommonCardsTaken { get; set; }
    public int RareCardsTaken { get; set; }
    public int RareAttackCardsOffered { get; set; }
    public int RareSkillCardsOffered { get; set; }
    public int RarePowerCardsOffered { get; set; }
    // The subset of White Star's Rare offers that actually reached the deck,
    // split the same way the offers are. Additive: runs recorded before these
    // fields default to zero taken, not to zero offers.
    public int RareAttackCardsTaken { get; set; }
    public int RareSkillCardsTaken { get; set; }
    public int RarePowerCardsTaken { get; set; }
    public int RareCardRewardScreensDeclined { get; set; }
    public int PrayerWheelExtraRewardScreens { get; set; }
    public int PrayerWheelExtraRewardScreensRejected { get; set; }

    // Total choosable card options actually upgraded by Molten Egg, Toxic Egg,
    // or Frozen Egg, plus which of those offers successfully entered the
    // permanent deck. Direct card grants are intentionally excluded.
    public int UpgradedCardsOffered { get; set; }
    public int UpgradedCommonCardsOffered { get; set; }
    public int UpgradedUncommonCardsOffered { get; set; }
    public int UpgradedRareCardsOffered { get; set; }
    public int UpgradedCardsTaken { get; set; }
    public int UpgradedCommonCardsTaken { get; set; }
    public int UpgradedUncommonCardsTaken { get; set; }
    public int UpgradedRareCardsTaken { get; set; }

    // Total card reward options consumed when Pael's Wing's Sacrifice option
    // is selected. The game model that owns the sacrifice option is
    // PaelsWing; PaelsFlesh is the separate max-energy-after-turn-3 relic.
    public int CommonCardsConsumed { get; set; }
    public int UncommonCardsConsumed { get; set; }
    public int RareCardsConsumed { get; set; }
    public int SacrificesMade { get; set; }
    public int SacrificesSkipped { get; set; }

    // Pael's Claw tracking. Plays count every finished play of a Goopy card
    // while the relic is held. Enhancements count the observed permanent
    // Goopy amount gained after the enchantment's own post-play callback;
    // the initial amount of 1 is the application baseline, not an earned
    // enhancement. Cards is the stable denominator of cards given Goopy.
    public int PaelsClawGoopyCardsPlayed { get; set; }
    public int PaelsClawGoopyEnhancements { get; set; }
    public int PaelsClawGoopyCards { get; set; }
    public int PaelsClawTurns { get; set; }
    public int PaelsClawCombats { get; set; }

    // Cards observed in Exhaust after Pael's Eye's extra-turn callback.
    // ActivationTurnSamples excludes held combats where the relic did not
    // activate. Combats is the zero-inclusive denominator for cards/combat.
    public int PaelsEyeCardsExhausted { get; set; }
    public int PaelsEyeStrikesAndDefendsExhausted { get; set; }
    public int PaelsEyeActivationTurnTotal { get; set; }
    public int PaelsEyeActivationTurnSamples { get; set; }
    public int PaelsEyeCombats { get; set; }
    public int StatusCardsExhausted { get; set; }
    public int CurseCardsExhausted { get; set; }
    public int CombatsWithoutActivation { get; set; }

    // Strike Dummy tracking. StrikesPlayed is cumulative since the relic was
    // picked up; RateStrikesPlayed is the matching observation-era numerator
    // for the held turn/combat denominators so older lifetime totals do not
    // manufacture rates. Deck counts are current permanent-deck snapshots.
    public int StrikeDummyStrikesPlayed { get; set; }
    public int StrikeDummyRateStrikesPlayed { get; set; }
    public int StrikeDummyTurns { get; set; }
    public int StrikeDummyCombats { get; set; }
    public int StrikeDummyBaseStrikesInDeck { get; set; }
    public int StrikeDummyNonBaseStrikeCardsInDeck { get; set; }

    // Oddly Smooth Stone tracking. Uses CardModel.GainsBlock, the game's own
    // classification for cards that immediately gain Dexterity-scaled Block.
    public int OddlySmoothStoneBlockCardsPlayed { get; set; }

    // Nutritious Soup tracking. Counts finished plays of basic Strike-tagged
    // cards carrying the Tezcataras Ember enchantment while the relic is held.
    public int NutritiousSoupEnchantedStrikesPlayed { get; set; }

    // Miniature Cannon tracking. Plays and hits are cumulative while the relic
    // is held; deck counts are current permanent-deck snapshots.
    public int MiniatureCannonUpgradedAttacksInDeck { get; set; }
    public int MiniatureCannonNonUpgradedAttacksInDeck { get; set; }
    public int MiniatureCannonUpgradedAttackPlays { get; set; }
    public int MiniatureCannonUpgradedAttackHits { get; set; }

    // Tooltip-only projections from the live combat-card piles. Internal
    // properties are intentionally excluded from persisted run JSON.
    internal int MiniatureCannonUpgradedAttacksInCombat { get; set; }
    internal int MiniatureCannonNonUpgradedAttacksInCombat { get; set; }

    // Vajra tracking. Counts attack cards played while held, and actual enemy
    // damage hits from those attacks. Multi-hit attacks increment hits per
    // resolved damage entry.
    public int VajraAttacksPlayed { get; set; }
    public int VajraAttackHits { get; set; }

    // Ember Tea tracking is limited to combats where the relic successfully
    // consumed a charge. The active-combat marker survives the fifth charge
    // reaching zero immediately after Strength is applied.
    public int EmberTeaAttacksPlayedWhileActive { get; set; }
    public int EmberTeaHitsWhileActive { get; set; }
    public int EmberTeaActiveTurns { get; set; }
    public int EmberTeaActiveCombats { get; set; }

    // Red Skull tracking uses the relic's live StrengthApplied flag as the
    // active-state source of truth. Turns and combats count distinct periods
    // in which that flag was actually active, including periods with no
    // qualifying attacks or hits.
    public int RedSkullAttacksPlayedWhileActive { get; set; }
    public int RedSkullHitsWhileActive { get; set; }
    public int RedSkullActiveTurns { get; set; }
    public int RedSkullActiveCombats { get; set; }

    // Kunai tracking. Counts owner attack plays, actual Dexterity gained from
    // activation resolution, and player-turn-end charge snapshots.
    public int KunaiAttacksPlayed { get; set; }
    public int KunaiDexterityGained { get; set; }
    public int KunaiTurnsEndedAt1Charge { get; set; }
    public int KunaiTurnsEndedAt2Charges { get; set; }
    public int KunaiTurnEndChargeTotal { get; set; }
    public int KunaiTurnEndChargeCount { get; set; }

    // Kusarigama, Ornamental Fan, and Shuriken share Kunai's repeatable
    // three-Attack, turn-reset counter. Their payoff uses the shared relic
    // damage, block, and Strength fields above; these fields preserve each
    // relic's input count and player-turn-end unused charge.
    public int KusarigamaAttacksPlayed { get; set; }
    public int KusarigamaTurnsEndedAt1Charge { get; set; }
    public int KusarigamaTurnsEndedAt2Charges { get; set; }
    public int KusarigamaTurnEndChargeTotal { get; set; }
    public int KusarigamaTurnEndChargeCount { get; set; }
    public int OrnamentalFanAttacksPlayed { get; set; }
    public int OrnamentalFanTurnsEndedAt0Charges { get; set; }
    public int OrnamentalFanTurnsEndedAt1Charge { get; set; }
    public int OrnamentalFanTurnsEndedAt2Charges { get; set; }
    public int OrnamentalFanTurnEndChargeTotal { get; set; }
    public int OrnamentalFanTurnEndChargeCount { get; set; }
    // Held-period denominators and their matching observation-era numerator.
    // AdditionalBlockGained predates these fields, so older lifetime block
    // must not be divided by only newly observed turns/combats.
    public int OrnamentalFanRateBlockGained { get; set; }
    public int OrnamentalFanTurns { get; set; }
    public int OrnamentalFanCombats { get; set; }
    public int ShurikenAttacksPlayed { get; set; }
    public int ShurikenTurnsEndedAt1Charge { get; set; }
    public int ShurikenTurnsEndedAt2Charges { get; set; }
    public int ShurikenTurnEndChargeTotal { get; set; }
    public int ShurikenTurnEndChargeCount { get; set; }

    // Kunai and Shuriken share one canonical three-Attack scaling contract.
    // These matching observation-era counters back their activation-rate rows;
    // lifetime Activations predates these denominators in existing run files.
    // RelicAggregate is already keyed by relic id, so both relics deliberately
    // use these same fields instead of parallel Kunai/Shuriken rate fields.
    public int ThreeAttackScalingRateActivations { get; set; }
    public int ThreeAttackScalingTurns { get; set; }
    public int ThreeAttackScalingCombats { get; set; }

    // Paper Phrog tracking. Damage added is the actual current damage amount
    // multiplied by Paper Phrog's extra Vulnerable multiplier. Enhanced attacks
    // count each real damage activation where the relic increased Vulnerable.
    public decimal PaperPhrogDamageAdded { get; set; }
    public int PaperPhrogEnhancedAttacks { get; set; }
    public int PaperPhrogCombats { get; set; }
    public int PaperPhrogTurns { get; set; }

    // Regalite tracking. Cards created counts owner-created combat cards while
    // the relic is held; combats and turns are held denominators for block
    // averages.
    public int RegaliteCardsCreated { get; set; }
    public int RegaliteCombats { get; set; }
    public int RegaliteTurns { get; set; }

    // Musical Box tracking. Creation uses the final successful combat-pile
    // result and retains that exact card reference so only a confirmed
    // causedByEthereal exhaust is counted. Turns and combats are held-period,
    // zero-inclusive denominators.
    public int MusicBoxAttacksCreated { get; set; }
    public int MusicBoxCommonAttacksCreated { get; set; }
    public int MusicBoxUncommonAttacksCreated { get; set; }
    public int MusicBoxRareAttacksCreated { get; set; }
    public int MusicBoxAttacksExhaustedByEthereal { get; set; }
    public int MusicBoxTurns { get; set; }
    public int MusicBoxCombats { get; set; }

    // Crossbow tracking. Attacks gained and rarity use only successful
    // generated-card add results. Discount is the effective energy-cost delta
    // around Crossbow's exact SetToFreeThisTurn call; turns and combats are
    // zero-inclusive held denominators.
    public int CrossbowAttacksGained { get; set; }
    public int CrossbowCommonAttacksGained { get; set; }
    public int CrossbowUncommonAttacksGained { get; set; }
    public int CrossbowRareAttacksGained { get; set; }
    public decimal CrossbowDiscountGivenTotal { get; set; }
    public int CrossbowTurns { get; set; }
    public int CrossbowCombats { get; set; }

    // Intimidating Helmet tracking. Activations is the number of owner card
    // plays whose play-time EnergyValue met the relic's 2+ threshold;
    // AdditionalBlockGained is the observed block. These are held combat/turn
    // denominators for the requested averages, including zero-trigger periods.
    public int IntimidatingHelmetCombats { get; set; }
    public int IntimidatingHelmetTurns { get; set; }

    // Daughter of the Wind tracking. AdditionalBlockGained is the observed
    // post-modifier result of its owner-Attack block command. Turns and
    // combats are held denominators, including zero-trigger periods.
    public int DaughterOfTheWindCombats { get; set; }
    public int DaughterOfTheWindTurns { get; set; }

    // Sturdy Clamp tracking. Block retained is the observed block remaining
    // after its async block-clear prevention callback. Excess block is the
    // pre-callback amount above the relic's 10-block retention cap. Turns count
    // each callback opportunity, including zero-block turns; combats count
    // every combat where the relic was held.
    public int SturdyClampBlockRetained { get; set; }
    public int SturdyClampExcessBlockOverTen { get; set; }
    public int SturdyClampTurns { get; set; }
    public int SturdyClampCombats { get; set; }

    // Ruined Helmet tracking. Activations counts confirmed successful
    // applications; StrengthAdded is the observed extra Strength contributed
    // by its doubling modifier. Combats includes every combat where the relic
    // was held, including zero-trigger combats.
    public int RuinedHelmetCombats { get; set; }

    // Tooltip-only projection populated from the live pending combat aggregate.
    // Internal properties are not part of the persisted System.Text.Json shape.
    internal decimal RuinedHelmetStrengthAddedThisCombat { get; set; }

    // Mummified Hand tracking. Activations is every qualifying owner Power
    // play, including triggers with no card left to discount. Power cost uses
    // the play-time EnergyValue; the ratio uses actual EnergySpent divided by
    // the selected card's pre-discount energy cost. Combats and turns are held
    // denominators, including zero-trigger periods.
    public decimal MummifiedHandTriggeringPowerCostTotal { get; set; }
    public decimal MummifiedHandDiscountGivenTotal { get; set; }
    public decimal MummifiedHandEnergySpentToDiscountedCostRatioTotal { get; set; }
    public int MummifiedHandEnergySpentToDiscountedCostRatioCount { get; set; }
    public int MummifiedHandCombats { get; set; }
    public int MummifiedHandTurns { get; set; }
    public int MummifiedHandDiscountedPowers { get; set; }
    public int MummifiedHandDiscountedAttacks { get; set; }
    public int MummifiedHandDiscountedSkills { get; set; }
    public int MummifiedHandDiscountedCommons { get; set; }
    public int MummifiedHandDiscountedUncommons { get; set; }
    public int MummifiedHandDiscountedRares { get; set; }

    // Burning Sticks tracking. Activations counts confirmed duplicate cards
    // added to combat. The generated-card play count follows those exact card
    // objects, while combats is the held denominator for both requested
    // averages. Rarity buckets use the successfully added duplicate.
    public int BurningSticksCombats { get; set; }
    public int BurningSticksGeneratedCardPlays { get; set; }
    public int BurningSticksCommonCardsDuplicated { get; set; }
    public int BurningSticksUncommonCardsDuplicated { get; set; }
    public int BurningSticksRareCardsDuplicated { get; set; }

    // Throwing Axe tracking. An extra play counts only after the relic's
    // contribution to the shared replay series produces a finished CardPlay.
    // Energy cost is the replayed card's play-time EnergyValue; combats is a
    // zero-inclusive held denominator for the requested average.
    public int ThrowingAxeExtraCardsPlayed { get; set; }
    public int ThrowingAxeExtraPlayEnergyCostTotal { get; set; }
    public int ThrowingAxeCombats { get; set; }
    public int ThrowingAxeCommonCardsPlayed { get; set; }
    public int ThrowingAxeUncommonCardsPlayed { get; set; }
    public int ThrowingAxeRareCardsPlayed { get; set; }

    // Bing Bong tracking. Counts only successful clonedBy:BingBong additions
    // to the permanent deck. Curse is a mutually exclusive type bucket; the
    // rarity buckets cover non-Curse Common, Uncommon, and Rare cards.
    public int BingBongExtraCardsAdded { get; set; }
    public int BingBongCommonCardsAdded { get; set; }
    public int BingBongUncommonCardsAdded { get; set; }
    public int BingBongRareCardsAdded { get; set; }
    public int BingBongCurseCardsAdded { get; set; }

    // Bookmark tracking. Activations is total cost-reduction activations;
    // BookmarkCombats is the denominator for average activations per combat.
    public int BookmarkCombats { get; set; }
    public int BookmarkCommonActivations { get; set; }
    public int BookmarkUncommonActivations { get; set; }
    public int BookmarkRareActivations { get; set; }

    // Nunchaku tracking. Attacks and energy are cumulative while held; combat
    // end charge stats snapshot the relic's in-game counter at promotion time.
    public int NunchakuAttacksPlayed { get; set; }
    public int NunchakuCombatsEndedOn8Charges { get; set; }
    public int NunchakuCombatsEndedOn9Charges { get; set; }
    public int NunchakuCombatEndChargeTotal { get; set; }

    // Iron Club tracking. Cards drawn is stored in AdditionalCardsDrawn; this
    // stores the held-combat denominator, combat-end charge buckets, and
    // explicit charge samples for the average charge at combat end.
    public int IronClubCombats { get; set; }
    public int IronClubCombatsEndedOn0Charges { get; set; }
    public int IronClubCombatsEndedOn1Charges { get; set; }
    public int IronClubCombatsEndedOn2Charges { get; set; }
    public int IronClubCombatsEndedOn3Charges { get; set; }
    public int IronClubCombatEndChargeTotal { get; set; }
    public int IronClubCombatEndChargeCount { get; set; }

    // Cost-discount tracking. Used by Brilliant Scarf. The turn-average
    // numerator starts with its additive turn denominator so an older run's
    // historical saved-energy total is not divided by only newly seen turns.
    public int DiscountCombats { get; set; }
    public int DiscountTurns { get; set; }
    public int DiscountsOffered { get; set; }
    public int DiscountsTaken { get; set; }
    public int EnergySavedByDiscount { get; set; }
    public int BrilliantScarfEnergySavedForTurnAverage { get; set; }
    public Dictionary<string, DiscountedCardCostAggregate> DiscountedCardCosts { get; set; } = new();

    // Total cards actually discarded while Gambling Chip's combat-start
    // selection resolves.
    public int CardsDiscarded { get; set; }

    // Total ? map points entered while the relic was held. Used by Juzu Bracelet.
    public int QuestionMarkSitesEntered { get; set; }

    // Current Dowsing quest progress granted by Dowsing Rod. Nullable keeps
    // historic run files distinguishable from an observed zero remaining.
    public int? DowsingQuestionRoomsRemaining { get; set; }

    // Floors already ascended when the first shop map point is entered while
    // the relic is held. Used by Cursed Pearl.
    public int? FloorsAscendedBeforeFirstShop { get; set; }

    // Distance from Signet Ring's pickup floor to the first MerchantRoom
    // entered afterward. Nullable distinguishes a pending search from a
    // completed zero-floor result.
    public int? FloorsTraveledUntilNextShop { get; set; }

    // Distance from Golden Pearl's pickup floor to the first observed gold
    // loss classified by the game as spending. Nullable distinguishes no
    // expense yet from a same-floor expense.
    public int? FloorsBeforeFirstGoldExpense { get; set; }

    // Ordered off-path destinations reached by spending Winged Boots charges.
    // UseNumber comes from the relic's own saved TimesUsed counter so tracking
    // remains correctly numbered when the mod is hot-reloaded mid-run.
    public List<WingedBootsDestinationAggregate> WingedBootsDestinations { get; set; } = new();

    // Cards actually removed while Precarious Shears' pickup effect resolves.
    public List<string> CardsRemoved { get; set; } = new();

    // Legacy max-HP snapshot around Precarious Shears' pickup cost. New max-HP
    // changing relics should write OriginalMaxHp/NewMaxHp instead.
    public decimal? StartingMaxHp { get; set; }
    public decimal? ResultingMaxHp { get; set; }

    // Total card rewards whose creation options were modified by this relic.
    // Used by Prismatic Gem.
    public int CardRewardsAffected { get; set; }

    // Fresnel Lens card-reward tracking. The any-Nimble counter overlaps the
    // exact-two and three-or-more breakdowns. Taken counts only successful
    // picks from those rewards.
    public int NimbleCardsTaken { get; set; }
    public int RewardScreensWithNimbleCards { get; set; }
    public int RewardScreensWithTwoNimbleCards { get; set; }
    public int RewardScreensWithThreeOrMoreNimbleCards { get; set; }
    public int RewardScreensWithoutNimbleCards { get; set; }
    public int RewardScreensWithNimbleCardsButNoneTaken { get; set; }

    // Wing Charm card-reward tracking. Each offered rarity is sourced from
    // the exact CardCreationResult modified by Wing Charm with Swift.
    public int WingCharmSwiftCardsTaken { get; set; }
    public int WingCharmSwiftCardsNotTaken { get; set; }
    public int WingCharmCommonSwiftCardsOffered { get; set; }
    public int WingCharmUncommonSwiftCardsOffered { get; set; }
    public int WingCharmRareSwiftCardsOffered { get; set; }

    // Lasting Candy card-reward outcomes. Each count comes from the exact
    // Power CardCreationResult the relic appended to a triggering combat
    // reward; rejected means that result remained when the reward resolved or
    // was replaced by a reroll. Elite/Boss activations classify those exact
    // appended options by the combat room that produced their reward.
    public int LastingCandyPowersOffered { get; set; }
    public int LastingCandyPowersTaken { get; set; }
    public int LastingCandyPowersRejected { get; set; }
    public int LastingCandyUncommonPowersOffered { get; set; }
    public int LastingCandyUncommonPowersTaken { get; set; }
    public int LastingCandyUncommonPowersRejected { get; set; }
    public int LastingCandyRarePowersOffered { get; set; }
    public int LastingCandyRarePowersTaken { get; set; }
    public int LastingCandyRarePowersRejected { get; set; }
    public int LastingCandyEliteActivations { get; set; }
    public int LastingCandyBossActivations { get; set; }

    // Ordered relic-owned card-choice offers. Silver Crucible uses its
    // one-based use number (1-3); Lead Paperweight uses screen 1. Cards keeps
    // the visible left-to-right option order and, once resolved, explicit
    // taken/not-taken outcomes. A list is required because duplicate card
    // definitions can be offered and must remain distinct observations.
    public List<RelicCardRewardScreenAggregate> CardRewardScreens { get; set; } = new();

    // Orrery's five card rewards in creation order. Each entry keeps its final
    // handling (skipped, obtained card, or reward alternative) and persists
    // the offered-card signature used to rebind a live reward after hot reload.
    public List<OrreryRewardAggregate> OrreryRewards { get; set; } = new();

    // Observed card reward options by card pool while Prismatic Gem is owned,
    // paired with how many of those options the player actually took. This is
    // intentionally meta: other reward modifiers may also affect the final
    // options. Used by Prismatic Gem and Dingy Rug.
    public Dictionary<string, CardRewardCategoryAggregate> CardRewardCategories { get; set; } = new();

    // Specific cards granted by relic-owned effects. Used by Hefty Tablet to
    // show which rare card was picked from its pickup screen, Arcane Scroll to
    // show the rare card it added, Scroll Boxes to show its chosen bundle,
    // Lead Paperweight to preserve the final card that reached the deck, and
    // Neow's Bones to show the curse it added.
    public Dictionary<string, RelicCardAggregate> CardsGranted { get; set; } = new();

    // Physical cards Pael's Tooth has actually returned to the deck. The list
    // preserves observed return order, duplicate definitions, the final title,
    // the post-return upgrade level, and the number of floors climbed since
    // the relic removed the cards on pickup.
    public List<RelicCardReturnAggregate> CardsReturned { get; set; } = new();

    // Times a relic-owned card choice was skipped. Used by Hefty Tablet and
    // Lead Paperweight.
    public int CardChoicesSkipped { get; set; }

    // Actual card transformations caused by relic-owned effects. Used by Leafy
    // Poultice to show the two basic cards it transformed and their results.
    public List<RelicCardTransformationAggregate> CardTransformations { get; set; } = new();
}

public class CardRewardCategoryAggregate
{
    public string DisplayName { get; set; } = "";
    public int Count { get; set; }
    // Options from this pool that actually left the reward screen and entered
    // the deck. Additive: runs recorded before this field default to zero, so
    // an old run reads as "offered, taken unknown" rather than "none taken".
    public int Taken { get; set; }
}

public class RelicCardAggregate
{
    public string CardId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Count { get; set; }
}

public class RelicOrbActivationAggregate
{
    public string OrbId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Activations { get; set; }
}

public class RelicEnchantedCardAggregate
{
    public string CardInstanceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public class RelicCardReturnAggregate
{
    public string CardId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int UpgradeLevel { get; set; }
    public int? FloorsClimbed { get; set; }
}

public class RelicCardRewardScreenAggregate
{
    public int ScreenNumber { get; set; }
    // Floor where this Silver use generated its options. This bounds hot-
    // reload/Continue re-association so an abandoned unresolved screen cannot
    // attach itself to an unrelated reward on a later floor.
    public int? Floor { get; set; }
    // False while the reward is generated but no terminal selection/skip/
    // reroll outcome has been observed yet. Persisting this state keeps the
    // screen recoverable across a Core hot reload between floors.
    public bool Resolved { get; set; }
    public List<RelicCardRewardOptionAggregate> Cards { get; set; } = new();
}

public class RelicCardRewardOptionAggregate
{
    public string CardId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int UpgradeLevel { get; set; }
    public bool Taken { get; set; }
}

public class OrreryRewardAggregate
{
    public int RewardNumber { get; set; }
    public int? Floor { get; set; }
    public string Outcome { get; set; } = "pending";
    public string AlternativeId { get; set; } = "";
    public List<string> OfferedCardIds { get; set; } = new();
    public List<OrreryObtainedCardAggregate> CardsObtained { get; set; } = new();
}

public class OrreryObtainedCardAggregate
{
    public string CardId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int UpgradeLevel { get; set; }
}

public class RelicGrantedAggregate
{
    public string RelicId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Count { get; set; }
}

public class ToyBoxWaxRelicAggregate
{
    public int Sequence { get; set; }
    public string RelicId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int? FloorBestowed { get; set; }
    public int? FloorMelted { get; set; }
}

public class RelicRewardChoiceAggregate
{
    public int ChoiceNumber { get; set; }
    public string RelicId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Outcome { get; set; } = "pending";
}

public class RelicCardTransformationAggregate
{
    public string SourceCardId { get; set; } = "";
    public string SourceDisplayName { get; set; } = "";
    public string ResultCardId { get; set; } = "";
    public string ResultDisplayName { get; set; } = "";
}

public class RelicMaxHpActivationAggregate
{
    public decimal StartingHp { get; set; }
    public decimal ResultingHp { get; set; }
}

public class EternalFeatherHealingActivationAggregate
{
    public int Floor { get; set; }
    public decimal HpRestored { get; set; }
}

public class SwordInTheStoneEliteSlainAggregate
{
    public int Floor { get; set; }
    public string EncounterId { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public class WingedBootsDestinationAggregate
{
    public int UseNumber { get; set; }
    public string Destination { get; set; } = "";
}

public class DiscountedCardCostAggregate
{
    public int EnergyCost { get; set; }
    public int StarCost { get; set; }
    public int Count { get; set; }
}

public readonly record struct CardRewardCategoryObservation(string Key, string DisplayName);

/// <summary>
/// One entry in the full event log. Captures what the mod observed, not what the
/// external analysis will compute on top (that's the aggregates' job).
/// </summary>
public class CardEvent
{
    public string T { get; set; } = "";          // ISO-8601 UTC timestamp
    public string Type { get; set; } = "";       // "card_played" | "damage_received" | "energy_gained" | "stars_gained" | "forge_gained" | "orb_created" | "orb_passive" | "orb_evoked" | "orb_fizzled" | "orb_block_gained" | "orb_energy_gained" | "orb_damage"
    public string CardId { get; set; } = "";

    // card_played fields
    public string? Target { get; set; }          // if the card targeted an enemy, their entity id (e.g. "KIN_PRIEST_0")
    public int? EnergySpent { get; set; }        // actual energy paid for this play (accounts for cost modifiers)
    public int? EnergyGained { get; set; }       // actual energy added to the pool while this card was resolving
    public int? StarsSpent { get; set; }         // actual stars paid for this play
    public int? StarsGained { get; set; }        // actual stars added while this card was resolving
    public decimal? ForgeGained { get; set; }    // actual forge added while this card was resolving
    public string? OrbId { get; set; }            // successfully channeled orb definition id

    // card_upgraded fields (and general-purpose: Floor also stamped on
    // other event types when useful). UpgradeLevel is the NEW level AFTER
    // the upgrade (post-increment); Floor is RunManager.State.TotalFloor
    // at the moment the upgrade fired.
    public int? Floor { get; set; }
    public int? UpgradeLevel { get; set; }

    // damage_received / orb_damage fields
    public string? Receiver { get; set; }
    public int? Blocked { get; set; }
    public int? Unblocked { get; set; }
    public int? Overkill { get; set; }
    public bool? Killed { get; set; }
}
