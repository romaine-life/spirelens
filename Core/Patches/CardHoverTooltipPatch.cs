using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace SpireLens.Core.Patches;

/// <summary>
/// Builds the native SpireLens hover-tip entry for a physical card holder.
/// NHoverTipSet owns the rendered node and its complete lifecycle.
/// </summary>
public static class CardHoverShowPatch
{
    private const int InlineKeywordIconSize = 16;
    private const string ShivMetaNote = "Reflects All Shiv Usage";
    private const string SoulMetaNote = "Reflects All Soul Usage";
    private const string BlockIconPath = "res://images/ui/combat/block.png";
    private const string DrawCardsNextTurnPowerIconPath = "res://images/atlases/power_atlas.sprites/draw_cards_next_turn_power.tres";
    private const string BlockedDrawIconPath = DrawCardsNextTurnPowerIconPath;
    private const int DebtGoldLossPerTrigger = 5;
    // Meta-power rows key off CARD ids, not power ids. Card ids carry no type
    // suffix, so they are safe as compile-time switch labels, while power ids
    // now come from the game types via MetaPowerRegistry and cannot be spelled
    // as constants here without reintroducing the copy that broke them.
    private const string AggressionCardId = "CARD.AGGRESSION";
    private const string BufferCardId = "CARD.BUFFER";
    private const string ConsumingShadowCardId = "CARD.CONSUMING_SHADOW";
    private const string DanseMacabreCardId = "CARD.DANSE_MACABRE";
    private const string DarkEmbraceCardId = "CARD.DARK_EMBRACE";
    private const string EntropyCardId = "CARD.ENTROPY";
    private const string FeelNoPainCardId = "CARD.FEEL_NO_PAIN";
    private const string JugglingCardId = "CARD.JUGGLING";
    private const string PanacheCardId = "CARD.PANACHE";
    private const string RuptureCardId = "CARD.RUPTURE";
    private const string StampedeCardId = "CARD.STAMPEDE";
    private const string UnmovableCardId = "CARD.UNMOVABLE";
    private const string ViciousCardId = "CARD.VICIOUS";
    // Unrelenting and Pounce are not Power cards, so they have no meta-power
    // registry entry. These ids are the canonical runtime ones already.
    private const string FreeAttackPowerId = "POWER.FREE_ATTACK_POWER";
    private const string FreeSkillPowerId = "POWER.FREE_SKILL_POWER";
    private const string PoisonPowerIconPath = "res://images/atlases/power_atlas.sprites/poison_power.tres";
    private const string StarIconPath = "res://images/packed/sprite_fonts/star_icon.png";
    private const string SovereignBladeMetaNote = "Reflects All Sovereign Blade Usage";

    internal static bool TryBuildNativeHoverTip(
        NCardHolder holder,
        out HoverTip statsTip)
    {
        statsTip = default;

        // Card tooltips are an explicit display-only opt-in on every surface.
        // Keep this gate ahead of tracker locks, aggregate merging, and tooltip
        // markup; attribution continues normally while the UI is disabled.
        var cardStatsEnabled = ViewStatsInjectorPatch.CardStatsEnabled;
        var viewStatsEnabled = ViewStatsInjectorPatch.StatsVisibilityEnabled;
        if (!ResolveCardStatsEnabled(viewStatsEnabled, cardStatsEnabled)) return false;

        if (IsCardRewardSelectionSurface(holder)) return false;

        var cardModel = holder.CardModel;
        if (cardModel == null) return false;

        if (RunHistoryStatsContext.TryBuildHistoricalDeckCardHoverTip(
                cardModel,
                out statsTip))
        {
            return true;
        }

        // Per-instance display: every deck card gets a stable "#N" number
        // assigned at RunStarted for the starting deck and lazily for cards
        // added mid-run.
        //
        // The game's Title includes a trailing "+" when upgraded. Strip it
        // here so the physical card's SpireLens identity remains stable.
        var rawTitle = cardModel.Title;
        if (cardModel.CurrentUpgradeLevel > 0 && !string.IsNullOrEmpty(rawTitle))
        {
            rawTitle = rawTitle.TrimEnd('+').TrimEnd();
        }
        var title = !string.IsNullOrWhiteSpace(rawTitle) ? rawTitle : cardModel.Id.ToString();
        var instanceNum = RunTracker.GetInstanceNumber(cardModel);
        var displayName = instanceNum > 0 ? $"{title} #{instanceNum}" : title;
        if (RunTracker.TryGetMetaPowerDeckViewDefinition(
                cardModel,
                out var metaPowerDefinition))
        {
            displayName = $"{metaPowerDefinition.DisplayName} · Meta Power";
        }

        CoreMain.LogDebug(
            $"hover: id={cardModel.Id} rawTitle='{rawTitle}' instance={instanceNum} " +
            $"displayName='{displayName}' hash={cardModel.GetHashCode()} " +
            $"deckVersionNull={cardModel.DeckVersion == null}");

        bool compact = holder is NHandCardHolder
            && !RuntimeOptionsProvider.Current.UseVerboseHandStats;
        var body = BuildBodyBBCode(cardModel, displayName, compact);
        statsTip = StatsTooltip.CreateNativeTip(
            displayName,
            body,
            stretchHorizontally: StatsTooltip.ContainsScalarStatTable(body));
        return true;
    }

    internal static bool ResolveCardStatsEnabled(
        bool viewStatsEnabled,
        bool cardStatsEnabled)
        => viewStatsEnabled && cardStatsEnabled;

    private static bool IsCardRewardSelectionSurface(Node? node)
    {
        for (var current = node; current != null; current = current.GetParent())
        {
            if (IsCardRewardSelectionSurfaceType(current.GetType()))
                return true;
        }

        return false;
    }

    internal static bool IsCardRewardSelectionSurfaceType(Type? nodeType)
    {
        return nodeType != null && typeof(NCardRewardSelectionScreen).IsAssignableFrom(nodeType);
    }

    /// <summary>
    /// Render the BODY portion of the native hover tip. The physical card
    /// identity is supplied separately through HoverTip.Title; this method
    /// produces HoverTip.Description BBCode.
    ///
    /// <paramref name="compact"/> controls density:
    ///   - true (hand hovers): just the high-signal numbers the player
    ///     needs mid-combat — Played/Drawn, Total damage (if attack),
    ///     Energy gained (if any), Block gained (if any), Kills (if any). Skips lineage, energy
    ///     details, averages, percentages.
    ///   - false (deck view, graveyard, draw pile, etc.): full breakdown
    ///     including lineage and all tabled stats.
    /// </summary>
    private static string BuildBodyBBCode(MegaCrit.Sts2.Core.Models.CardModel cardModel, string displayName, bool compact = false)
    {
        var run = RunTracker.Current;
        var sb = new StringBuilder();
        bool isShivMetaCard = RunTracker.IsShivDeckViewCard(cardModel);
        bool isSoulMetaCard = RunTracker.IsSoulDeckViewCard(cardModel);
        bool isStatusMetaCard = RunTracker.IsStatusDeckViewCard(cardModel);
        bool isSovereignBladeMetaCard = RunTracker.IsSovereignBladeDeckViewCard(cardModel);
        bool isMetaPowerCard = RunTracker.TryGetMetaPowerDeckViewDefinition(
            cardModel,
            out var metaPowerDefinition);
        bool isSupplementalMetaCard =
            isShivMetaCard
            || isSoulMetaCard
            || isStatusMetaCard
            || isSovereignBladeMetaCard
            || isMetaPowerCard;

        // The card identity now lives in the gold title slot for both compact
        // and full views, so repeating it again in the body just adds noise.
        // Supplemental meta cards (pooled Shiv / Sovereign Blade)
        // get a red explanatory banner instead of the generic ephemeral
        // "not present in deck" note.
        if (isShivMetaCard)
            sb.Append($"[color=#e04c4c][b]{ShivMetaNote}[/b][/color]\n");
        else if (isSoulMetaCard)
            sb.Append($"[color=#e04c4c][b]{SoulMetaNote}[/b][/color]\n");
        else if (isStatusMetaCard)
            sb.Append(
                $"[color=#e04c4c][b]Reflects All {StatsTooltip.EscapeBbcode(cardModel.Title)} Usage[/b][/color]\n");
        else if (isSovereignBladeMetaCard)
            sb.Append($"[color=#e04c4c][b]{SovereignBladeMetaNote}[/b][/color]\n");
        else if (isMetaPowerCard)
            sb.Append("[color=#68aee8][b]Shared Meta-Power Record[/b][/color]\n");

        if (isMetaPowerCard && metaPowerDefinition != null)
        {
            AppendCanonicalMetaPowerStats(
                sb,
                metaPowerDefinition,
                RunTracker.GetEffectiveMetaStats());
            return sb.ToString();
        }

        // Merges committed run + current pending combat so mid-combat plays
        // show up immediately (don't wait for CombatEnded). If we have no
        // aggregate entry yet for this card (unplayed), treat it as an
        // empty/zero aggregate and render the normal stats layout with
        // zeros. Per Nelson: "no data this run" was an awkward escape
        // hatch — zeros are more informative and structurally consistent.
        var agg = RunTracker.GetEffectiveAggregate(cardModel) ?? new CardAggregate();
        CoreMain.LogDebug($"  lookup: Plays={agg.Plays} Intended={agg.TotalIntended}");

        // Compact mode (hand hovers) — skip lineage and everything tabular,
        // return just the signals a player needs mid-combat.
        if (compact)
        {
            AppendCompactBody(sb, cardModel, agg);
            return sb.ToString();
        }

        // Lineage: when/how the card entered the deck, and any upgrades
        // since. Label/value style matches the stats tables below — no
        // colons, bold numbers, subdued surrounding prose.
        //
        // Special case: cards that aren't in the player's permanent deck
        // (combat-generated Souls, Shivs, short-lived transformed cards,
        // etc.). For those, "Received floor X" is misleading — the card
        // isn't a deck member, it just exists transiently. Render a
        // distinct "Card not present in deck" note instead.
        //
        // FloorAdded: the game sets this to 1 for starter cards (see
        // Player.PopulateStartingDeck). Mid-run adds get current floor.
        // No special "starting deck" text — starters just show "floor 1".
        if (IsCardInDeck(cardModel))
        {
            //   "Received floor 1"                        → starter or floor-1 acquisition
            //   "Received floor 22, came upgraded +1"      → pre-upgraded (shop/event)
            //   "Received floor 22" + "Upgraded floor 22 → +1" → got and upgraded same floor
            string floorStr = agg.FloorAdded.HasValue
                ? $"floor [b]{agg.FloorAdded.Value}[/b]"
                : "unknown floor";
            if (agg.InitialUpgradeLevel > 0)
                sb.Append($"[color=#b5b5b5]Received {floorStr}, came upgraded +[b]{agg.InitialUpgradeLevel}[/b][/color]\n");
            else
                sb.Append($"[color=#b5b5b5]Received {floorStr}[/color]\n");

            foreach (var ue in RunTracker.GetUpgradeEvents(cardModel))
            {
                string ufloor = ue.Floor.HasValue
                    ? $"floor [b]{ue.Floor.Value}[/b]"
                    : "?";
                int level = ue.UpgradeLevel ?? 0;
                sb.Append($"[color=#b5b5b5]Upgraded {ufloor} → +[b]{level}[/b][/color]\n");
            }

            // Removal marker. Shown in the tooltip so users can tell at a
            // glance that this is a removed card even without the visual
            // grouping in the deck view. Floor 0 defaults to "?" text.
            if (agg.Removed)
                AppendRemovalLine(sb, agg);
        }
        else
        {
            // Distinguish "was removed from deck" from "never entered deck"
            // (combat-generated ephemerals like Souls/Shivs). Removed gets
            // a red bold banner since it's an important run decision to
            // flag at a glance. Supplemental deck-view meta cards already
            // emitted their own explanatory red banner above, so we suppress
            // the generic "not present" line for them. Other ephemerals keep
            // the subdued grey note.
            if (agg.Removed)
            {
                sb.Append("[color=#e04c4c][b]Card Removed[/b][/color]\n");
                AppendRemovalLine(sb, agg);
            }
            else if (!isSupplementalMetaCard)
                sb.Append("[color=#b5b5b5]Card not present in deck[/color]\n");
        }

        AppendFullStatRows(
            sb,
            cardModel,
            agg,
            RunTracker.GetEffectiveMetaStats(),
            RunTracker.GetEtherealCardsPlayedThisCombat(),
            GetSupermassiveCardsCreatedThisCombat(cardModel),
            RunTracker.GetTotalEffectiveCardDamage());

        // No footer. Previously we rendered "A4 · DEFECT · this run" here
        // as a mirror of SlayTheStats' filter-context footer — but they need
        // that line because their data aggregates across many runs with
        // configurable filters. Ours is scoped to one run by construction,
        // so the line was repeating back run info the user already knows.
        // Reintroduce a scope marker when/if we add cross-run lifetime stats.
        _ = run;  // silence unused-variable warning; keeps RunTracker reference live for debug.

        return sb.ToString();
    }

    internal static string BuildHistoricalBodyBBCode(
        MegaCrit.Sts2.Core.Models.CardModel cardModel,
        CardAggregate agg,
        RunMetaStats? metaStats,
        IEnumerable<CardEvent>? upgradeEvents = null,
        bool compact = false)
    {
        agg ??= new CardAggregate();
        metaStats ??= new RunMetaStats();

        var sb = new StringBuilder();
        if (compact)
        {
            AppendCompactBodyWithMetaStats(sb, cardModel, agg, metaStats);
            return sb.ToString();
        }

        string floorStr = agg.FloorAdded.HasValue
            ? $"floor [b]{agg.FloorAdded.Value}[/b]"
            : "unknown floor";
        if (agg.InitialUpgradeLevel > 0)
            sb.Append($"[color=#b5b5b5]Received {floorStr}, came upgraded +[b]{agg.InitialUpgradeLevel}[/b][/color]\n");
        else
            sb.Append($"[color=#b5b5b5]Received {floorStr}[/color]\n");

        if (upgradeEvents != null)
        {
            foreach (var ue in upgradeEvents)
            {
                string ufloor = ue.Floor.HasValue
                    ? $"floor [b]{ue.Floor.Value}[/b]"
                    : "?";
                int level = ue.UpgradeLevel ?? 0;
                sb.Append($"[color=#b5b5b5]Upgraded {ufloor} → +[b]{level}[/b][/color]\n");
            }
        }

        if (agg.Removed)
            AppendRemovalLine(sb, agg);

        AppendFullStatRows(
            sb,
            cardModel,
            agg,
            metaStats,
            totalCardDamageThisRun: TotalCardDamageIn(RunHistoryStatsContext.GetCurrentRunData()));
        return sb.ToString();
    }

    private static void AppendRemovalLine(StringBuilder sb, CardAggregate agg)
    {
        string floor = agg.RemovedAtFloor.HasValue
            ? $"floor [b]{agg.RemovedAtFloor.Value}[/b]"
            : "floor [b]?[/b]";
        string source = string.IsNullOrWhiteSpace(agg.RemovalSource)
            ? ""
            : $" · {StatsTooltip.EscapeBbcode(agg.RemovalSource)}";
        string cost = agg.RemovalGoldCost.HasValue
            ? $" · {StatConceptGlossary.RenderHintedGlyph("gold")} [b]{agg.RemovalGoldCost.Value}[/b]"
            : "";

        sb.Append($"[color=#b5b5b5]Removed {floor}{source}{cost}[/color]\n");
    }

    private static long? TotalCardDamageIn(RunData? run)
        => run?.Aggregates.Values.Sum(aggregate => (long)aggregate.TotalEffective);

    private static void AppendFullStatRows(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel cardModel,
        CardAggregate agg,
        RunMetaStats metaStats,
        int? etherealCardsPlayedThisCombat = null,
        int? cardsCreatedThisCombat = null,
        long? totalCardDamageThisRun = null)
    {
        // Per-play averages — the actual "utility" signal. Guard against
        // div-by-zero for the unplayed case.
        float avgIntended = agg.Plays > 0 ? (float)agg.TotalIntended / agg.Plays : 0f;
        float avgEffective = agg.Plays > 0 ? (float)agg.TotalEffective / agg.Plays : 0f;
        float overkillPct = agg.TotalIntended > 0 ? 100f * agg.TotalOverkill / agg.TotalIntended : 0f;
        float blockedPct = agg.TotalIntended > 0 ? 100f * agg.TotalBlocked / agg.TotalIntended : 0f;

        // All stat rows use the same 3-col table layout for visual
        // consistency: label | value | (optional percent). Rows without a
        // percentage get an empty 3rd cell so the label and value columns
        // align vertically across every row in the tooltip. Cell padding
        // prevents adjacent cells from crowding against each other (was
        // observed as "Played/Drawn1/1100%" with zero space between).
        bool isUnplayable = cardModel.Type == CardType.Curse
            || (cardModel.Keywords != null && cardModel.Keywords.Contains(CardKeyword.Unplayable));

        if (isUnplayable)
        {
            // For unplayable cards, just show Drawn. Played is always 0.
            Row3(sb, GetDrawStatLabel("drawn"), agg.TimesDrawn.ToString(), "");
        }
        else
        {
            // Playable cards: show Played/Drawn ratio with play rate %.
            float playRate = agg.TimesDrawn > 0 ? 100f * agg.Plays / agg.TimesDrawn : 0f;
            Row3(sb, "Played/Drawn", $"{agg.Plays}/{agg.TimesDrawn}", $"{playRate:F0}%");
        }

        AppendDeathMarchStats(
            sb,
            cardModel,
            RunTracker.GetDeathMarchCardsDrawnThisTurn(cardModel));
        Row3(sb, "Combats in deck", agg.CombatsInDeck.ToString(), "");

        if (etherealCardsPlayedThisCombat.HasValue)
            AppendPullFromBelowStats(sb, cardModel, etherealCardsPlayedThisCombat.Value);
        if (cardsCreatedThisCombat.HasValue)
            AppendSupermassiveStats(sb, cardModel, cardsCreatedThisCombat.Value);
        AppendMakeItSoStats(sb, cardModel, agg, compact: false);
        AppendUnleashStats(sb, cardModel, agg, compact: false);
        AppendOstySummonStats(sb, cardModel, agg, metaStats, compact: false);
        AppendSoulPileStats(sb, agg);
        AppendPhysicalMetaPowerSummary(sb, cardModel, metaStats);
        AppendAlchemizePotionStats(sb, cardModel, agg, compact: false);
        AppendRandomCardGenerationStats(sb, cardModel, agg, compact: false);
        AppendJackOfAllTradesStats(sb, cardModel, agg, compact: false);
        AppendDiscoveryStats(sb, cardModel, agg, compact: false);
        AppendSplashStats(sb, cardModel, agg);
        AppendAllForOneStats(sb, cardModel, agg, compact: false);
        AppendOutbreakStats(sb, cardModel, agg);
        AppendArmamentsStats(sb, cardModel, agg);
        AppendDrainPowerStats(sb, cardModel, agg, compact: false);
        AppendUnrelentingFreeAttackStats(sb, cardModel, metaStats, compact: false);
        AppendPounceFreeSkillStats(sb, cardModel, metaStats, compact: false);
        AppendDebtStats(sb, cardModel, agg);
        AppendNormalityStats(sb, cardModel, agg);
        AppendReplayStats(sb, agg);

        bool hasDedicatedPoison = AppendDedicatedPoisonStats(sb, agg, compact: false);
        AppendAppliedEffects(sb, agg, compact: false, excludePoison: hasDedicatedPoison);
        AppendArtifactBlockedSummary(sb, agg, excludePoison: hasDedicatedPoison);

        // Energy-gain rows — cards like Adrenaline / Concentrate / energy
        // pot-style effects need a direct "what did this card give me?"
        // stat, independent of the existing energy-spent cost tracking.
        if (agg.TotalEnergyGenerated > 0)
        {
            // One gained/wasted row rather than the two the pair would cost,
            // following the offered/picked convention: a bare "gained" number
            // reads as pure profit until the same row says how much of it
            // expired in the pool unspent.
            float avgGenerated = agg.Plays > 0 ? (float)agg.TotalEnergyGenerated / agg.Plays : 0f;
            float wastedPct = 100f * agg.TotalEnergyWasted / agg.TotalEnergyGenerated;
            Row3(
                sb,
                GetEnergyStatLabel("gained/wasted"),
                $"{agg.TotalEnergyGenerated}/{agg.TotalEnergyWasted}",
                $"{wastedPct:F0}%");
            Row3(sb, GetEnergyStatLabel("avg gained"), $"{avgGenerated:F1}", "");
        }

        if (agg.TotalStarsGenerated > 0)
        {
            float avgGenerated = agg.Plays > 0 ? (float)agg.TotalStarsGenerated / agg.Plays : 0f;
            Row3(sb, GetStarStatLabel("gained"), agg.TotalStarsGenerated.ToString(), "");
            Row3(sb, GetStarStatLabel("avg gained"), $"{avgGenerated:F1}", "");
        }

        if (agg.TotalForgeGenerated > 0m)
        {
            decimal avgGenerated = agg.Plays > 0 ? agg.TotalForgeGenerated / agg.Plays : 0m;
            Row3(sb, GetForgeStatLabel("gained"), FormatDecimal(agg.TotalForgeGenerated), "");
            Row3(sb, GetForgeStatLabel("avg gained"), FormatDecimal(avgGenerated), "");
        }

        AppendOrbCreationStats(
            sb,
            cardModel,
            agg,
            metaStats,
            compact: false);

        // Energy-spent rows — only rendered when the card's cost is actually
        // variable (see IsEnergyInteresting). Same 3-col layout as every
        // other stat row; percent column stays empty since there's nothing
        // to percentage-ify here.
        if (IsEnergyInteresting(cardModel, agg))
        {
            float avgEnergy = agg.Plays > 0 ? (float)agg.TotalEnergySpent / agg.Plays : 0f;
            Row3(sb, GetEnergyStatLabel("total spent"), agg.TotalEnergySpent.ToString(), "");
            Row3(sb, GetEnergyStatLabel("avg cost"), $"{avgEnergy:F1}", "");
        }

        if (IsStarInteresting(cardModel, agg))
        {
            float avgStars = agg.Plays > 0 ? (float)agg.TotalStarsSpent / agg.Plays : 0f;
            Row3(sb, GetStarStatLabel("total spent"), agg.TotalStarsSpent.ToString(), "");
            Row3(sb, GetStarStatLabel("avg cost"), $"{avgStars:F1}", "");
        }

        // Damage section rules:
        //   - Attack cards: always show the damage block. With zeros for
        //     unplayed or 0-damage attacks (target died / fully blocked
        //     case), with the full breakdown once damage has been dealt.
        //   - Non-attack (Skill/Power/Status/Curse): skip the section
        //     entirely unless we somehow accumulated damage (edge case;
        //     shouldn't happen but respects the data if it does).
        bool isAttack = cardModel.Type == CardType.Attack;
        bool showDamage = isAttack || agg.TotalIntended > 0;
        if (showDamage)
        {
            // Total damage = effective damage = HP actually removed by this
            // card across the whole run. "Effective" over "intended" because
            // that's what players mean by "this card has done X damage" —
            // block and overkill waste don't count.
            // Damage section in the same 3-col layout. Total damage =
            // effective damage = HP actually removed. "Effective" over
            // "intended" because that's what players mean by "X damage".
            // Avg intended intentionally omitted pending issue #15.
            Row3(sb, "Total damage", agg.TotalEffective.ToString(), "");
            Row3(sb, "Avg effective", $"{avgEffective:F1}", "");
            // Damage bought per energy actually paid. A card that never spent
            // energy cannot be divided, so it shows its damage over a zero-cost
            // orb rather than a ratio — the same shape the deck-view sort uses,
            // so hovering a card corroborates what you sorted by.
            if (agg.TotalEffective > 0)
            {
                Row3(
                    sb,
                    "Damage per energy",
                    agg.TotalEnergySpent > 0
                        ? $"{(float)agg.TotalEffective / agg.TotalEnergySpent:F1}"
                        : $"{agg.TotalEffective} / 0{StatEnergyIcon.RenderInline(18)}",
                    "");
            }

            // Share of everything this run's cards dealt, with the ratio it
            // came from so the percentage can be checked rather than trusted.
            if (agg.TotalEffective > 0 && totalCardDamageThisRun is > 0)
            {
                var share = 100d * agg.TotalEffective / totalCardDamageThisRun.Value;
                Row3(
                    sb,
                    "Damage share",
                    $"{agg.TotalEffective} / {totalCardDamageThisRun.Value}",
                    $"{share:F1}%");
            }

            _ = avgIntended;  // still computed above; silence unused warning

            // Clarify 0 damage for attacks that played without dealing any:
            // the game skips DamageReceivedEntry when the target is in the
            // "dead but not yet removed" state, so the play is real but
            // we have no damage event to attribute. Rendered as a subdued
            // own-line annotation rather than inline with Total damage.
            if (isAttack && agg.Plays > 0 && agg.TotalIntended == 0)
                sb.Append("[color=#7a7a85]  (target died / fully blocked)[/color]\n");
            // Overkill and Blocked: whole number + %. Same 3-col row as
            // everything else; here the percent cell is populated.
            Row3(sb, "Overkill", agg.TotalOverkill.ToString(), $"{overkillPct:F0}%");
            Row3(sb, "Blocked", agg.TotalBlocked.ToString(), $"{blockedPct:F0}%");
            if (agg.Kills > 0) Row3(sb, "Kills", agg.Kills.ToString(), "");
        }

        // Block gained — rendered for cards that have actually produced
        // block. Absorbed uses FIFO consumption across the player's block
        // ledger; wasted uses LIFO across whatever survived until clear/
        // expiry, which matches the "later block was redundant overfill"
        // mental model described in issue #6.
        if (agg.TotalBlockGained > 0)
        {
            float avgBlock = agg.Plays > 0 ? (float)agg.TotalBlockGained / agg.Plays : 0f;
            float absorbedPct = 100f * agg.TotalBlockEffective / agg.TotalBlockGained;
            float wastedPct = 100f * agg.TotalBlockWasted / agg.TotalBlockGained;
            Row3(sb, GetBlockStatLabel("gained"), agg.TotalBlockGained.ToString(), "");
            Row3(sb, GetBlockStatLabel("avg"), $"{avgBlock:F1}", "");
            Row3(sb, GetBlockStatLabel("absorbed"), agg.TotalBlockEffective.ToString(), $"{absorbedPct:F0}%");
            Row3(sb, GetBlockStatLabel("wasted"), agg.TotalBlockWasted.ToString(), $"{wastedPct:F0}%");
        }

        AppendBufferChargeStats(sb, agg, compact: false);

        // Discarded count — shown only when > 0 because for most cards
        // discarding doesn't happen. When it does (end-of-turn with card
        // still in hand, discard-triggering effects), the number is
        // useful signal — a card you keep discarding without playing is
        // probably dead weight.
        if (agg.TimesDiscarded > 0)
            Row3(sb, "Discarded", agg.TimesDiscarded.ToString(), "");

        // Pile-top placements — signals draw-order manipulation. Only
        // rendered when > 0 to keep noise down on normal cards.
        if (agg.TimesPlacedOnTopFromHand > 0)
            Row3(sb, "Top from hand", agg.TimesPlacedOnTopFromHand.ToString(), "");
        if (agg.TimesPlacedOnTopFromDiscard > 0)
            Row3(sb, "Top from graveyard", agg.TimesPlacedOnTopFromDiscard.ToString(), "");

        // Exhausted other cards — Havoc-style side-effect stat. Only
        // shown for cards that have actually caused an exhaust.
        if (agg.TimesExhaustedOtherCards > 0)
        {
            Row3(sb, "Exhausted others", agg.TimesExhaustedOtherCards.ToString(), "");
            // Subset rows, only when the run actually produced them — a
            // Havoc that never ate a curse says nothing about curses.
            if (agg.TimesExhaustedOtherCurses > 0)
                Row3(sb, "of which curses", agg.TimesExhaustedOtherCurses.ToString(), "");
            if (agg.TimesExhaustedOtherStatusCards > 0)
                Row3(sb, "of which status cards", agg.TimesExhaustedOtherStatusCards.ToString(), "");
        }

        // How often THIS card itself got exhausted. Full-view only; useful
        // for exhaust-tag cards and ephemeral generated cards, but not worth
        // the space in the compact in-hand view.
        if (agg.TimesExhausted > 0)
            Row3(sb, "Exhausted", agg.TimesExhausted.ToString(), "");

        AppendCardDrawStats(sb, agg);

        // HP lost from playing this card — Ironclad self-damage cards.
        // POST-reduction value, so Tungsten Rod / buffer interactions
        // show as reduced HP loss, which is the true cost signal.
        if (agg.TotalHpLost > 0)
            Row3(sb, "HP lost", agg.TotalHpLost.ToString(), "");
        if (agg.TotalMaxHpLost > 0)
            Row3(sb, "Max HP lost", agg.TotalMaxHpLost.ToString(), "");
        if (cardModel is Feed
            || IsCardId(cardModel, "CARD.FEED")
            || agg.TotalMaxHpGained > 0)
        {
            Row3(sb, "Max HP gained", agg.TotalMaxHpGained.ToString(), "");
        }
    }

    /// <summary>
    /// Compact stats body for hand hovers during combat. High-signal
    /// numbers only — the player's deciding what to play, not studying
    /// lifetime performance.
    ///
    /// Shows: Played/Drawn ratio, Total damage (if attack), Energy gained
    /// (if any), Block gained (if any), Kills (if any). Skips: lineage, most energy details, per-play
    /// averages, overkill/blocked percentages. Everything uses the same
    /// 3-col layout as the full view for visual consistency.
    /// </summary>
    private static void AppendCompactBody(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel cardModel,
        CardAggregate agg)
    {
        AppendCompactBodyWithMetaStats(
            sb,
            cardModel,
            agg,
            RunTracker.GetEffectiveMetaStats(),
            RunTracker.GetEtherealCardsPlayedThisCombat(),
            GetSupermassiveCardsCreatedThisCombat(cardModel));
    }

    private static void AppendCompactBodyWithMetaStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel cardModel,
        CardAggregate agg,
        RunMetaStats metaStats,
        int? etherealCardsPlayedThisCombat = null,
        int? cardsCreatedThisCombat = null)
    {
        bool isAttack = cardModel.Type == CardType.Attack;

        bool isUnplayable = cardModel.Type == CardType.Curse
            || (cardModel.Keywords != null && cardModel.Keywords.Contains(CardKeyword.Unplayable));

        if (isUnplayable)
        {
            // For unplayable cards, just show Drawn. Played is always 0.
            Row3(sb, GetDrawStatLabel("drawn"), agg.TimesDrawn.ToString(), "");
        }
        else
        {
            float playRate = agg.TimesDrawn > 0 ? 100f * agg.Plays / agg.TimesDrawn : 0f;
            Row3(sb, "Played/Drawn", $"{agg.Plays}/{agg.TimesDrawn}", $"{playRate:F0}%");
        }

        AppendDeathMarchStats(
            sb,
            cardModel,
            RunTracker.GetDeathMarchCardsDrawnThisTurn(cardModel));
        bool hasDedicatedPoison = AppendDedicatedPoisonStats(sb, agg, compact: true);
        AppendAppliedEffects(sb, agg, compact: true, excludePoison: hasDedicatedPoison);

        if (agg.TotalEnergyGenerated > 0)
            Row3(
                sb,
                GetEnergyStatLabel("gained/wasted"),
                $"{agg.TotalEnergyGenerated}/{agg.TotalEnergyWasted}",
                "");

        if (agg.TotalStarsGenerated > 0)
            Row3(sb, GetStarStatLabel("gained"), agg.TotalStarsGenerated.ToString(), "");

        if (agg.TotalForgeGenerated > 0m)
            Row3(sb, GetForgeStatLabel("gained"), FormatDecimal(agg.TotalForgeGenerated), "");

        AppendOrbCreationStats(
            sb,
            cardModel,
            agg,
            metaStats,
            compact: true);

        if (etherealCardsPlayedThisCombat.HasValue)
            AppendPullFromBelowStats(sb, cardModel, etherealCardsPlayedThisCombat.Value);
        if (cardsCreatedThisCombat.HasValue)
            AppendSupermassiveStats(sb, cardModel, cardsCreatedThisCombat.Value);
        AppendMakeItSoStats(sb, cardModel, agg, compact: true);
        AppendUnleashStats(sb, cardModel, agg, compact: true);
        AppendOstySummonStats(sb, cardModel, agg, metaStats, compact: true);
        AppendSoulPileStats(sb, agg);
        AppendPhysicalMetaPowerSummary(sb, cardModel, metaStats);
        AppendAlchemizePotionStats(sb, cardModel, agg, compact: true);
        AppendRandomCardGenerationStats(sb, cardModel, agg, compact: true);
        AppendJackOfAllTradesStats(sb, cardModel, agg, compact: true);
        AppendDiscoveryStats(sb, cardModel, agg, compact: true);
        AppendSplashStats(sb, cardModel, agg);
        AppendAllForOneStats(sb, cardModel, agg, compact: true);
        AppendOutbreakStats(sb, cardModel, agg);
        AppendArmamentsStats(sb, cardModel, agg);
        AppendDrainPowerStats(sb, cardModel, agg, compact: true);
        AppendUnrelentingFreeAttackStats(sb, cardModel, metaStats, compact: true);
        AppendPounceFreeSkillStats(sb, cardModel, metaStats, compact: true);
        AppendDebtStats(sb, cardModel, agg);
        AppendNormalityStats(sb, cardModel, agg);
        AppendReplayStats(sb, agg);

        bool showDamage = isAttack || agg.TotalIntended > 0;
        if (showDamage)
        {
            Row3(sb, "Total damage", agg.TotalEffective.ToString(), "");
            if (agg.Kills > 0) Row3(sb, "Kills", agg.Kills.ToString(), "");
        }

        if (agg.TotalBlockGained > 0)
            Row3(sb, GetBlockStatLabel("gained"), agg.TotalBlockGained.ToString(), "");

        AppendBufferChargeStats(sb, agg, compact: true);

        if (agg.TotalHpLost > 0)
            Row3(sb, "HP lost", agg.TotalHpLost.ToString(), "");
        if (agg.TotalMaxHpLost > 0)
            Row3(sb, "Max HP lost", agg.TotalMaxHpLost.ToString(), "");
        if (cardModel is Feed
            || IsCardId(cardModel, "CARD.FEED")
            || agg.TotalMaxHpGained > 0)
        {
            Row3(sb, "Max HP gained", agg.TotalMaxHpGained.ToString(), "");
        }
    }

    /// <summary>
    /// A deck card is "in deck" if its canonical deck reference still
    /// exists in the player's permanent deck list. Combat clones point back
    /// to the deck original via DeckVersion; deck-view cards are already the
    /// canonical object. Removed cards are intentionally NOT in the list.
    ///
    /// If we can't read the deck state (no active run, etc.) we default to
    /// TRUE so we fall back to the normal lineage display. That's the safer
    /// path — mis-reporting a deck card as "not present" is worse than the
    /// reverse.
    /// </summary>
    private static bool IsCardInDeck(MegaCrit.Sts2.Core.Models.CardModel card)
    {
        try
        {
            var player = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.State?.Players.FirstOrDefault();
            if (player?.Deck?.Cards == null) return true;  // unknown → assume deck

            var canonical = card.DeckVersion ?? card;
            foreach (var c in player.Deck.Cards)
            {
                var cCanonical = c.DeckVersion ?? c;
                if (System.Object.ReferenceEquals(cCanonical, canonical)) return true;
            }
            return false;
        }
        catch
        {
            return true;  // error → assume deck
        }
    }

    private static void AppendUnleashStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        CardAggregate agg,
        bool compact)
    {
        if (agg.TotalOstyHpAttackBonus <= 0) return;
        if (card is not MegaCrit.Sts2.Core.Models.Cards.Unleash
            && !IsCardId(card, "CARD.UNLEASH"))
            return;

        Row3(sb, "Osty HP damage", agg.TotalOstyHpAttackBonus.ToString(), "");
        if (compact) return;

        decimal avgBonus = agg.TimesOstyHpAttackBonusApplied > 0
            ? (decimal)agg.TotalOstyHpAttackBonus / agg.TimesOstyHpAttackBonusApplied
            : 0m;
        Row3(sb, "avg Osty HP damage", FormatDecimal(avgBonus), "");
    }

    private static void AppendOstySummonStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        CardAggregate agg,
        RunMetaStats metaStats,
        bool compact)
    {
        agg ??= new CardAggregate();
        metaStats ??= new RunMetaStats();

        if (!IsOstySummonStatsHome(card, agg)) return;
        if (agg.TotalOstyHpSummoned <= 0m
            && metaStats.TotalOstyHpSummoned <= 0m
            && metaStats.TotalOstyDamageAbsorbed <= 0m)
            return;

        if (agg.TotalOstyHpSummoned > 0m)
            Row3(sb, "Summon gained", FormatDecimal(agg.TotalOstyHpSummoned), "");

        if (!compact && agg.TimesOstySummoned > 0)
            Row3(sb, "Osty summons", agg.TimesOstySummoned.ToString(), "");

        if (metaStats.TotalOstyHpSummoned > 0m)
            Row3(sb, "All Osty total summon", FormatDecimal(metaStats.TotalOstyHpSummoned), "");

        if (metaStats.TotalOstyDamageAbsorbed > 0m)
            Row3(sb, "All Osty damage absorbed", FormatDecimal(metaStats.TotalOstyDamageAbsorbed), "");
    }

    private static void AppendSoulPileStats(StringBuilder sb, CardAggregate agg)
    {
        if (agg.SoulsAddedToDrawPile > 0)
            Row3(sb, "Souls added to draw pile", agg.SoulsAddedToDrawPile.ToString(), "");
        if (agg.SoulsAddedToHand > 0)
            Row3(sb, "Souls added to hand", agg.SoulsAddedToHand.ToString(), "");
        if (agg.SoulsAddedToDiscardPile > 0)
            Row3(sb, "Souls added to discard pile", agg.SoulsAddedToDiscardPile.ToString(), "");
    }

    private static bool IsOstySummonStatsHome(
        MegaCrit.Sts2.Core.Models.CardModel card,
        CardAggregate agg)
    {
        return agg.TimesOstySummoned > 0
            || agg.TotalOstyHpSummoned > 0m
            || card is SummonForth
            || IsCardId(card, "CARD.SUMMON_FORTH");
    }

    /// <summary>
    /// This physical card's own Buffer charges, from the charge ledger.
    /// Separate from the pooled power total the meta-power rows show, the same
    /// way per-card block sits alongside the shared block pool.
    /// </summary>
    private static void AppendBufferChargeStats(
        StringBuilder sb,
        CardAggregate agg,
        bool compact)
    {
        if (agg.BufferChargesGranted <= 0) return;

        Row3(
            sb,
            "HP loss prevented",
            FormatDecimal(agg.BufferDamagePrevented),
            "");
        if (compact) return;

        var utilization = 100m * agg.BufferChargesUsed / agg.BufferChargesGranted;
        var preventedPerCharge = agg.BufferChargesUsed <= 0
            ? 0m
            : agg.BufferDamagePrevented / agg.BufferChargesUsed;
        Row3(
            sb,
            "Charges used/granted",
            $"{agg.BufferChargesUsed}/{agg.BufferChargesGranted}",
            $"{utilization:F0}%");
        Row3(
            sb,
            "Avg HP loss prevented per charge",
            FormatDecimal(preventedPerCharge),
            "");
    }

    private static void AppendPhysicalMetaPowerSummary(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        RunMetaStats metaStats)
    {
        if (!MetaPowerRegistry.TryGetByCard(card, out var definition)
            || definition == null)
        {
            return;
        }

        var aggregate = GetMetaPowerAggregate(metaStats, definition);
        AppendMetaPowerLifetimeStats(
            sb,
            definition,
            aggregate,
            metaStats,
            detailed: false);
    }

    private static void AppendCanonicalMetaPowerStats(
        StringBuilder sb,
        MetaPowerDefinition definition,
        RunMetaStats metaStats)
    {
        var aggregate = GetMetaPowerAggregate(metaStats, definition);

        Row3(
            sb,
            "Power cards played",
            aggregate.PowerCardsPlayed.ToString(),
            "");
        Row3(
            sb,
            "Generated Power cards played",
            aggregate.GeneratedPowerCardsPlayed.ToString(),
            "");
        Row3(
            sb,
            "Successful applications",
            aggregate.SuccessfulApplications.ToString(),
            "");

        AppendMetaPowerLifetimeStats(
            sb,
            definition,
            aggregate,
            metaStats,
            detailed: true);
        if (OrbCardRegistry.IsRecurringPowerId(definition.PowerId))
        {
            var powerOutcomes = new Dictionary<string, CardOrbAggregate>(
                StringComparer.Ordinal);
            MergeOrbOutcomesForDisplay(
                powerOutcomes,
                aggregate.OrbOutcomes);
            AppendOrbOutcomeRows(
                sb,
                OrbCardRegistry.GetExpectedOrbIdsForPower(definition.PowerId),
                powerOutcomes,
                aggregate.TotalOrbsCreated,
                compact: false);
        }
        AppendMetaPowerRates(sb, definition, aggregate);
    }

    private static PowerAggregate GetMetaPowerAggregate(
        RunMetaStats metaStats,
        MetaPowerDefinition definition)
    {
        metaStats ??= new RunMetaStats();
        if (metaStats.PowerAggregates != null
            && metaStats.PowerAggregates.TryGetValue(
                definition.PowerId,
                out var aggregate))
        {
            return aggregate;
        }

        return new PowerAggregate
        {
            PowerId = definition.PowerId,
            DisplayName = definition.DisplayName,
        };
    }

    private static void AppendMetaPowerLifetimeStats(
        StringBuilder sb,
        MetaPowerDefinition definition,
        PowerAggregate aggregate,
        RunMetaStats metaStats,
        bool detailed)
    {
        if (RandomCardGenerationRegistry.IsRecurringPowerId(
                definition.PowerId))
        {
            AppendRandomCardGenerationOutcomeStats(
                sb,
                aggregate.RandomCardGeneration,
                compact: !detailed,
                combatsInDeck: 0,
                metaPowerAggregate: aggregate);
        }

        switch (definition.CardId)
        {
            case ConsumingShadowCardId:
                Row3(
                    sb,
                    "Orbs evoked",
                    aggregate.OrbsEvoked.ToString(),
                    "");
                break;

            case JugglingCardId:
                Row3(sb, "Total attacks copied", aggregate.AttacksCopied.ToString(), "");
                if (detailed)
                {
                    Row3(sb, "Common attacks copied", aggregate.CommonAttacksCopied.ToString(), "");
                    Row3(sb, "Uncommon attacks copied", aggregate.UncommonAttacksCopied.ToString(), "");
                    Row3(sb, "Rare attacks copied", aggregate.RareAttacksCopied.ToString(), "");
                }
                break;

            case DanseMacabreCardId:
                Row3(sb, "Times triggered", aggregate.TimesTriggered.ToString(), "");
                Row3(
                    sb,
                    GetBlockStatLabel("Block gained"),
                    FormatDecimal(aggregate.BlockGained),
                    "");
                break;

            case DarkEmbraceCardId:
                Row3(
                    sb,
                    GetDrawStatLabel("cards drawn"),
                    aggregate.DarkEmbraceCardsDrawn.ToString(),
                    "");
                break;

            case EntropyCardId:
                Row3(
                    sb,
                    "Cards generated",
                    aggregate.EntropyCardsGenerated.ToString(),
                    "");
                if (detailed)
                {
                    Row3(
                        sb,
                        "Chains of Binding broken",
                        aggregate.EntropyChainsOfBindingBroken.ToString(),
                        "");
                    Row3(sb, "Commons generated", aggregate.EntropyCommonCardsGenerated.ToString(), "");
                    Row3(sb, "Uncommons generated", aggregate.EntropyUncommonCardsGenerated.ToString(), "");
                    Row3(sb, "Rares generated", aggregate.EntropyRareCardsGenerated.ToString(), "");
                }
                break;

            case FeelNoPainCardId:
                Row3(
                    sb,
                    GetBlockStatLabel("Block gained"),
                    FormatDecimal(aggregate.BlockGained),
                    "");
                break;

            case RuptureCardId:
                Row3(
                    sb,
                    "Strength gained",
                    FormatDecimal(aggregate.StrengthGained),
                    "");
                break;

            case StampedeCardId:
                Row3(
                    sb,
                    "Attacks stampeded",
                    aggregate.StampedeAttacksPlayed.ToString(),
                    "");
                Row3(
                    sb,
                    GetEnergyStatLabel("saved"),
                    aggregate.StampedeEnergySaved.ToString(),
                    "");
                if (detailed)
                {
                    Row3(sb, "Common attacks", aggregate.StampedeCommonAttacksPlayed.ToString(), "");
                    Row3(sb, "Uncommon attacks", aggregate.StampedeUncommonAttacksPlayed.ToString(), "");
                    Row3(sb, "Rare attacks", aggregate.StampedeRareAttacksPlayed.ToString(), "");
                }
                break;

            case AggressionCardId:
                Row3(
                    sb,
                    "Cards returned to hand",
                    aggregate.AggressionCardsReturnedToHand.ToString(),
                    "");
                Row3(
                    sb,
                    "Cards upgraded",
                    aggregate.AggressionCardsUpgraded.ToString(),
                    "");
                break;

            case UnmovableCardId:
                decimal extraBlock = aggregate.UnmovableExtraBlockGained > 0m
                    ? aggregate.UnmovableExtraBlockGained
                    : metaStats.ExtraBlockGainedFromUnmovablePower;
                Row3(
                    sb,
                    GetBlockStatLabel("Extra block gained"),
                    FormatDecimal(extraBlock),
                    "");
                break;

            case ViciousCardId:
                Row3(
                    sb,
                    GetDrawStatLabel("cards drawn"),
                    aggregate.ViciousCardsDrawn.ToString(),
                    "");
                break;

            // Panache is the only power that deals damage itself, so it is the
            // only one with damage rows. Same labels as a card's, because it
            // is the same measurement: effective damage is HP actually removed.
            case PanacheCardId:
                Row3(sb, "Total damage", aggregate.TotalEffective.ToString(), "");
                if (aggregate.TotalIntended > 0)
                {
                    Row3(
                        sb,
                        "Overkill",
                        aggregate.TotalOverkill.ToString(),
                        $"{100f * aggregate.TotalOverkill / aggregate.TotalIntended:F0}%");
                    Row3(
                        sb,
                        "Blocked",
                        aggregate.TotalBlocked.ToString(),
                        $"{100f * aggregate.TotalBlocked / aggregate.TotalIntended:F0}%");
                }

                if (aggregate.Kills > 0)
                    Row3(sb, "Kills", aggregate.Kills.ToString(), "");
                break;

            case BufferCardId:
                Row3(
                    sb,
                    "HP loss prevented",
                    FormatDecimal(aggregate.BufferDamagePrevented),
                    "");
                if (detailed)
                {
                    var chargeUtilization = aggregate.BufferChargesGranted <= 0
                        ? 0m
                        : 100m * aggregate.BufferChargesUsed
                            / aggregate.BufferChargesGranted;
                    var preventedPerCharge = aggregate.BufferChargesUsed <= 0
                        ? 0m
                        : aggregate.BufferDamagePrevented
                            / aggregate.BufferChargesUsed;
                    Row3(
                        sb,
                        "Charges used/granted",
                        $"{aggregate.BufferChargesUsed}/{aggregate.BufferChargesGranted}",
                        $"{chargeUtilization:F0}%");
                    Row3(
                        sb,
                        "Avg HP loss prevented per charge",
                        FormatDecimal(preventedPerCharge),
                        "");
                }
                break;
        }
    }

    private static void AppendMetaPowerRates(
        StringBuilder sb,
        MetaPowerDefinition definition,
        PowerAggregate aggregate)
    {
        switch (definition.CardId)
        {
            case JugglingCardId:
                AppendMetaPowerRateTriplet(
                    sb,
                    "attacks copied",
                    aggregate.RateAttacksCopied,
                    aggregate);
                break;
            case DanseMacabreCardId:
                AppendMetaPowerRateTriplet(
                    sb,
                    "triggers",
                    aggregate.RateTimesTriggered,
                    aggregate);
                AppendMetaPowerRateTriplet(
                    sb,
                    "block gained",
                    aggregate.RateBlockGained,
                    aggregate);
                break;
            case DarkEmbraceCardId:
                AppendMetaPowerRateTriplet(
                    sb,
                    "cards drawn",
                    aggregate.RateDarkEmbraceCardsDrawn,
                    aggregate);
                break;
            case EntropyCardId:
                AppendMetaPowerRateTriplet(
                    sb,
                    "cards generated",
                    aggregate.RateEntropyCardsGenerated,
                    aggregate);
                break;
            case FeelNoPainCardId:
                AppendMetaPowerRateTriplet(
                    sb,
                    "block gained",
                    aggregate.RateBlockGained,
                    aggregate);
                break;
            case RuptureCardId:
                AppendMetaPowerRateTriplet(
                    sb,
                    "strength gained",
                    aggregate.RateStrengthGained,
                    aggregate);
                break;
            case StampedeCardId:
                AppendMetaPowerRateTriplet(
                    sb,
                    "attacks stampeded",
                    aggregate.RateStampedeAttacksPlayed,
                    aggregate);
                AppendMetaPowerRateTriplet(
                    sb,
                    "energy saved",
                    aggregate.RateStampedeEnergySaved,
                    aggregate);
                break;
            case AggressionCardId:
                AppendMetaPowerRateTriplet(
                    sb,
                    "cards returned",
                    aggregate.RateAggressionCardsReturnedToHand,
                    aggregate);
                AppendMetaPowerRateTriplet(
                    sb,
                    "cards upgraded",
                    aggregate.RateAggressionCardsUpgraded,
                    aggregate);
                break;
            case UnmovableCardId:
                AppendMetaPowerRateTriplet(
                    sb,
                    "extra block gained",
                    aggregate.RateUnmovableExtraBlockGained,
                    aggregate);
                break;
            case ViciousCardId:
                AppendMetaPowerRateTriplet(
                    sb,
                    "cards drawn",
                    aggregate.RateViciousCardsDrawn,
                    aggregate);
                break;
        }
    }

    private static void AppendMetaPowerRateTriplet(
        StringBuilder sb,
        string outcome,
        decimal numerator,
        PowerAggregate aggregate)
    {
        Row3(
            sb,
            $"Avg {outcome} / turn",
            FormatDecimal(DivideMetaPowerRate(
                numerator,
                aggregate.MetaDeckTurns)),
            "");
        Row3(
            sb,
            $"Avg {outcome} / active turn",
            FormatDecimal(DivideMetaPowerRate(
                numerator,
                aggregate.MetaActiveTurns)),
            "");
        Row3(
            sb,
            $"Avg {outcome} / active application-turn",
            FormatDecimal(DivideMetaPowerRate(
                numerator,
                aggregate.MetaActiveApplicationTurns)),
            "");
    }

    internal static decimal DivideMetaPowerRate(
        decimal numerator,
        int denominator)
        => denominator <= 0 ? 0m : numerator / denominator;

    private static void AppendUnmovablePowerStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        RunMetaStats metaStats)
    {
        metaStats ??= new RunMetaStats();
        if (card is not Unmovable && !IsCardId(card, "CARD.UNMOVABLE")) return;

        Row3(
            sb,
            "Extra block gained from unmovable's power",
            FormatDecimal(metaStats.ExtraBlockGainedFromUnmovablePower),
            "");
    }

    private static void AppendAlchemizePotionStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        CardAggregate agg,
        bool compact)
    {
        if (card is not Alchemize && !IsCardId(card, "CARD.ALCHEMIZE")) return;

        // Match White Beast Statue's potion outcome rows. Alchemize has no
        // reward screen, so its skipped count means the observed procure
        // result failed (for example, a full potion belt or Sozu).
        Row3(sb, "Potions gained", agg.PotionsGained.ToString(), "");
        Row3(sb, "Potions skipped", agg.PotionsSkipped.ToString(), "");
        if (compact) return;

        Row3(sb, "common potions", agg.CommonPotionsGained.ToString(), "");
        Row3(sb, "uncommon potions", agg.UncommonPotionsGained.ToString(), "");
        Row3(sb, "rare potions", agg.RarePotionsGained.ToString(), "");
    }

    private static void AppendRandomCardGenerationStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        CardAggregate agg,
        bool compact)
    {
        if (!RandomCardGenerationRegistry.IsDirectGenerator(card)) return;

        AppendRandomCardGenerationOutcomeStats(
            sb,
            agg.RandomCardGeneration,
            compact,
            agg.CombatsInDeck,
            metaPowerAggregate: null);
    }

    private static void AppendRandomCardGenerationOutcomeStats(
        StringBuilder sb,
        RandomCardGenerationAggregate? generation,
        bool compact,
        int combatsInDeck,
        PowerAggregate? metaPowerAggregate)
    {
        generation ??= new RandomCardGenerationAggregate();
        var utilization = generation.CardsGenerated <= 0
            ? 0m
            : 100m * generation.GeneratedCardsPlayed / generation.CardsGenerated;

        Row3(
            sb,
            "Cards generated",
            generation.CardsGenerated.ToString(),
            "",
            "Cards that successfully entered a combat pile from this random-card generator.");
        Row3(
            sb,
            "Generated cards played",
            generation.GeneratedCardsPlayed.ToString(),
            $"{utilization:0}%",
            "Distinct physical generated cards that completed at least one play; the percentage is generated-card utilization.");

        if (compact) return;

        if (generation.GeneratedCardPlays > generation.GeneratedCardsPlayed)
        {
            Row3(
                sb,
                "All generated card plays",
                generation.GeneratedCardPlays.ToString(),
                "",
                "Every completed play of a generated card, including replays of the same physical card.");
        }

        if (metaPowerAggregate == null)
        {
            var generatedPerCombat = combatsInDeck <= 0
                ? 0m
                : (decimal)generation.CardsGenerated / combatsInDeck;
            Row3(
                sb,
                "Avg cards generated per combat",
                FormatDecimal(generatedPerCombat),
                "",
                "Average successful generated-card arrivals across every combat this physical source card was in the deck, including combats with none.");
        }
        else
        {
            Row3(
                sb,
                "Avg cards generated per turn",
                FormatDecimal(DivideMetaPowerRate(
                    generation.CardsGenerated,
                    metaPowerAggregate.MetaDeckTurns)),
                "",
                "Average generated cards per turn in combats where this Power card was in the permanent deck, including turns before activation.");
            Row3(
                sb,
                "Avg cards generated while active per turn",
                FormatDecimal(DivideMetaPowerRate(
                    generation.CardsGenerated,
                    metaPowerAggregate.MetaActiveTurns)),
                "",
                "Average generated cards per turn while at least one stack of the shared Power was active, including zero-output active turns.");
            Row3(
                sb,
                "Avg cards generated per turn each active application",
                FormatDecimal(DivideMetaPowerRate(
                    generation.CardsGenerated,
                    metaPowerAggregate.MetaActiveApplicationTurns)),
                "",
                "Average generated cards per active Power application per turn, so stacked applications contribute separately to the denominator.");
        }

        if (generation.CardsGenerated > 0
            && (generation.EnergyCostBeforeDiscountTotal > 0
                || generation.XCostCardsGenerated > 0))
        {
            var nonXCards = Math.Max(
                1,
                generation.CardsGenerated - generation.XCostCardsGenerated);
            var averageCost = (decimal)generation.EnergyCostBeforeDiscountTotal
                              / nonXCards;
            Row3(
                sb,
                GetEnergyStatLabel("avg generated-card cost before discount"),
                FormatDecimal(averageCost),
                "");
        }

        if (generation.EnergyDiscountGrantedTotal > 0)
        {
            var averageDiscount = generation.CardsGenerated <= 0
                ? 0m
                : (decimal)generation.EnergyDiscountGrantedTotal
                  / generation.CardsGenerated;
            Row3(
                sb,
                GetEnergyStatLabel("avg discount granted"),
                FormatDecimal(averageDiscount),
                "");
        }

        if (generation.UpgradedCardsGenerated > 0)
            Row3(sb, "Upgraded cards generated", generation.UpgradedCardsGenerated.ToString(), "");
        if (generation.XCostCardsGenerated > 0)
            Row3(sb, "X-cost cards generated", generation.XCostCardsGenerated.ToString(), "");

        var positiveRarityBuckets = new[]
        {
            generation.BasicCardsGenerated,
            generation.CommonCardsGenerated,
            generation.UncommonCardsGenerated,
            generation.RareCardsGenerated,
            generation.StatusCardsGenerated,
            generation.CurseCardsGenerated,
            generation.OtherRarityCardsGenerated,
        }.Count(value => value > 0);
        if (positiveRarityBuckets > 1 || generation.OtherRarityCardsGenerated > 0)
        {
            if (generation.BasicCardsGenerated > 0)
                Row3(sb, "Basic cards generated", generation.BasicCardsGenerated.ToString(), "");
            if (generation.CommonCardsGenerated > 0)
                Row3(sb, "Common cards generated", generation.CommonCardsGenerated.ToString(), "");
            if (generation.UncommonCardsGenerated > 0)
                Row3(sb, "Uncommon cards generated", generation.UncommonCardsGenerated.ToString(), "");
            if (generation.RareCardsGenerated > 0)
                Row3(sb, "Rare cards generated", generation.RareCardsGenerated.ToString(), "");
            if (generation.StatusCardsGenerated > 0)
                Row3(sb, "Statuses generated", generation.StatusCardsGenerated.ToString(), "");
            if (generation.CurseCardsGenerated > 0)
                Row3(sb, "Curses generated", generation.CurseCardsGenerated.ToString(), "");
            if (generation.OtherRarityCardsGenerated > 0)
                Row3(sb, "Other cards generated", generation.OtherRarityCardsGenerated.ToString(), "");
        }

        var positiveTypeBuckets = new[]
        {
            generation.AttacksGenerated,
            generation.SkillsGenerated,
            generation.PowersGenerated,
            generation.OtherTypeCardsGenerated,
        }.Count(value => value > 0);
        if (positiveTypeBuckets > 1)
        {
            if (generation.AttacksGenerated > 0)
                Row3(sb, "Attacks generated", generation.AttacksGenerated.ToString(), "");
            if (generation.SkillsGenerated > 0)
                Row3(sb, "Skills generated", generation.SkillsGenerated.ToString(), "");
            if (generation.PowersGenerated > 0)
                Row3(sb, "Powers generated", generation.PowersGenerated.ToString(), "");
            if (generation.OtherTypeCardsGenerated > 0)
                Row3(sb, "Other cards generated", generation.OtherTypeCardsGenerated.ToString(), "");
        }

        var nonHandDestinations = generation.CardsAddedToDrawPile
                                  + generation.CardsAddedToDiscardPile
                                  + generation.CardsAddedElsewhere;
        if (nonHandDestinations > 0)
        {
            if (generation.CardsAddedToHand > 0)
                Row3(sb, "Cards added to hand", generation.CardsAddedToHand.ToString(), "");
            if (generation.CardsAddedToDrawPile > 0)
                Row3(sb, "Cards added to draw pile", generation.CardsAddedToDrawPile.ToString(), "");
            if (generation.CardsAddedToDiscardPile > 0)
                Row3(sb, "Cards added to discard pile", generation.CardsAddedToDiscardPile.ToString(), "");
            if (generation.CardsAddedElsewhere > 0)
                Row3(sb, "Cards added elsewhere", generation.CardsAddedElsewhere.ToString(), "");
        }
    }

    private static void AppendJackOfAllTradesStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        CardAggregate agg,
        bool compact)
    {
        if (card is not JackOfAllTrades && !IsCardId(card, "CARD.JACK_OF_ALL_TRADES")) return;

        // The common generator aggregate supersedes Jack's original exact-add
        // rows once new observation-era data exists. Keep the legacy display
        // for older run files instead of showing two copies of the same facts.
        if (agg.RandomCardGeneration?.CardsGenerated > 0) return;

        Row3(sb, "Colorless cards added", agg.JackColorlessCardsAdded.ToString(), "");
        if (compact) return;

        Row3(sb, "uncommons added", agg.JackUncommonCardsAdded.ToString(), "");
        Row3(sb, "rares added", agg.JackRareCardsAdded.ToString(), "");
        Row3(sb, "Attacks added", agg.JackAttacksAdded.ToString(), "");
        Row3(sb, "Skills added", agg.JackSkillsAdded.ToString(), "");
        Row3(sb, "Powers added", agg.JackPowersAdded.ToString(), "");

        var averageCost = agg.JackColorlessCardsAdded <= 0
            ? 0m
            : (decimal)agg.JackAddedCardCostTotal / agg.JackColorlessCardsAdded;
        Row3(sb, "Avg cost of cards added", FormatDecimal(averageCost), "");
    }

    private static void AppendDiscoveryStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        CardAggregate agg,
        bool compact)
    {
        if (card is not Discovery && !IsCardId(card, "CARD.DISCOVERY")) return;

        OfferedPickedRow(
            sb,
            ["all", "card"],
            agg.DiscoveryCardsOffered,
            agg.DiscoveryCardsPicked,
            "Cards offered/picked — every option Discovery put on a choose-a-card screen, and how many of them were picked. Runs recorded before offers were observed read as zero offered.");
        if (compact) return;

        OfferedPickedRow(
            sb,
            ["card"],
            agg.DiscoveryCommonCardsOffered,
            agg.DiscoveryCommonCardsPicked,
            "Commons offered/picked from Discovery's choose-a-card screens.");
        OfferedPickedRow(
            sb,
            ["card_uncommon"],
            agg.DiscoveryUncommonCardsOffered,
            agg.DiscoveryUncommonCardsPicked,
            "Uncommons offered/picked from Discovery's choose-a-card screens.");
        OfferedPickedRow(
            sb,
            ["card_rare"],
            agg.DiscoveryRareCardsOffered,
            agg.DiscoveryRareCardsPicked,
            "Rares offered/picked from Discovery's choose-a-card screens.");
        OfferedPickedRow(
            sb,
            ["attack"],
            agg.DiscoveryAttacksOffered,
            agg.DiscoveryAttacksPicked,
            "Attacks offered/picked from Discovery's choose-a-card screens.");
        OfferedPickedRow(
            sb,
            ["skill"],
            agg.DiscoverySkillsOffered,
            agg.DiscoverySkillsPicked,
            "Skills offered/picked from Discovery's choose-a-card screens.");
        OfferedPickedRow(
            sb,
            ["power"],
            agg.DiscoveryPowersOffered,
            agg.DiscoveryPowersPicked,
            "Powers offered/picked from Discovery's choose-a-card screens.");

        var averageDiscount = agg.DiscoveryCardsPicked <= 0
            ? 0m
            : (decimal)agg.DiscoveryEnergyDiscountTotal / agg.DiscoveryCardsPicked;
        Row3(
            sb,
            GetEnergyStatLabel("avg discount of picked card"),
            FormatDecimal(averageDiscount),
            "");
    }

    /// <summary>
    /// One row for an offer bucket and the subset of it the player picked. The
    /// two numbers only mean anything next to each other, so they share a row
    /// instead of costing the tooltip two.
    /// </summary>
    private static void OfferedPickedRow(
        StringBuilder sb,
        IReadOnlyList<string> itemConceptIds,
        int offered,
        int picked,
        string fullDescription)
    {
        var presentation = StatsTooltip.CreateStatRowPresentation(
            string.Empty,
            fullDescription,
            [.. itemConceptIds, "offered", "taken"]);
        StatsTooltip.AppendScalarStatRow(
            sb,
            presentation,
            $"{Math.Max(0, offered)}/{Math.Max(0, picked)}",
            "",
            labelColor: "#e0e0e0");
    }

    private static void AppendAllForOneStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        CardAggregate agg,
        bool compact)
    {
        if (card is not AllForOne && !IsCardId(card, "CARD.ALL_FOR_ONE")) return;

        Row3(
            sb,
            "0-cost cards returned",
            agg.AllForOneZeroCostCardsReturned.ToString(),
            "",
            "Zero-cost cards returned from the discard pile to hand by All for One.");
        if (compact) return;

        var returnedPerPlay = agg.Plays <= 0
            ? 0m
            : (decimal)agg.AllForOneZeroCostCardsReturned / agg.Plays;
        var returnedPerCombat = agg.CombatsInDeck <= 0
            ? 0m
            : (decimal)agg.AllForOneZeroCostCardsReturned / agg.CombatsInDeck;
        Row3(
            sb,
            "Avg returned per play",
            FormatDecimal(returnedPerPlay),
            "",
            "Average zero-cost cards returned each time All for One was played.");
        Row3(
            sb,
            "Avg returned per combat",
            FormatDecimal(returnedPerCombat),
            "",
            "Average zero-cost cards returned by All for One per combat in the deck.");
    }

    private static void AppendSplashStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        CardAggregate agg)
    {
        if (card is not Splash && !IsCardId(card, "CARD.SPLASH")) return;

        Row3(
            sb,
            "Common Attacks taken",
            agg.SplashCommonAttacksTaken.ToString(),
            "",
            "Common Attacks selected from Splash.");
        Row3(
            sb,
            "Uncommon Attacks taken",
            agg.SplashUncommonAttacksTaken.ToString(),
            "",
            "Uncommon Attacks selected from Splash.");
        Row3(
            sb,
            "Rare Attacks taken",
            agg.SplashRareAttacksTaken.ToString(),
            "",
            "Rare Attacks selected from Splash.");

        var averageDiscount = agg.SplashAttacksTaken <= 0
            ? 0m
            : (decimal)agg.SplashEnergyDiscountTotal / agg.SplashAttacksTaken;
        Row3(
            sb,
            GetEnergyStatLabel("avg discount"),
            FormatDecimal(averageDiscount),
            "",
            "Average energy-cost discount applied to Attacks selected from Splash.");
    }

    private static void AppendOutbreakStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        CardAggregate agg)
    {
        if (card is not Outbreak && !IsCardId(card, "CARD.OUTBREAK")) return;

        Row3(
            sb,
            GetInlineIconStatLabel(
                PoisonPowerIconPath,
                "damage from extra triggers"),
            agg.OutbreakExtraPoisonTriggerDamage.ToString(),
            "",
            "Damage dealt by the extra Poison triggers caused by Outbreak.");
    }

    private static void AppendDrainPowerStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        CardAggregate agg,
        bool compact)
    {
        if (card is not DrainPower && !IsCardId(card, "CARD.DRAIN_POWER")) return;

        Row3(sb, "Cards upgraded", agg.DrainPowerCardsUpgraded.ToString(), "");
        if (compact) return;

        var upgradesPerTurn = agg.DrainPowerTurnsInDeck <= 0
            ? 0m
            : (decimal)agg.DrainPowerCardsUpgraded / agg.DrainPowerTurnsInDeck;
        var upgradesPerCombat = agg.CombatsInDeck <= 0
            ? 0m
            : (decimal)agg.DrainPowerCardsUpgraded / agg.CombatsInDeck;
        var upgradedPlaysPerTurn = agg.DrainPowerTurnsInDeck <= 0
            ? 0m
            : (decimal)agg.DrainPowerUpgradedCardPlays / agg.DrainPowerTurnsInDeck;
        var upgradedPlaysPerCombat = agg.CombatsInDeck <= 0
            ? 0m
            : (decimal)agg.DrainPowerUpgradedCardPlays / agg.CombatsInDeck;

        Row3(sb, "Avg cards upgraded per turn", FormatDecimal(upgradesPerTurn), "");
        Row3(sb, "Avg cards upgraded per combat", FormatDecimal(upgradesPerCombat), "");
        Row3(sb, "Avg upgraded-card plays per turn", FormatDecimal(upgradedPlaysPerTurn), "");
        Row3(sb, "Avg upgraded-card plays per combat", FormatDecimal(upgradedPlaysPerCombat), "");
    }

    private static void AppendArmamentsStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        CardAggregate agg)
    {
        if (card is not Armaments && !IsCardId(card, "CARD.ARMAMENTS")) return;

        Row3(sb, "Cards upgraded", agg.ArmamentsCardsUpgraded.ToString(), "");
    }

    private static void AppendUnrelentingFreeAttackStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        RunMetaStats metaStats,
        bool compact)
    {
        if (card is not Unrelenting && !IsCardId(card, "CARD.UNRELENTING")) return;

        metaStats ??= new RunMetaStats();
        PowerAggregate? powerAgg = null;
        if (metaStats.PowerAggregates != null)
        {
            metaStats.PowerAggregates.TryGetValue(FreeAttackPowerId, out powerAgg);
            powerAgg ??= metaStats.PowerAggregates.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.PowerId, FreeAttackPowerId, StringComparison.Ordinal)
                || string.Equals(candidate.DisplayName, "Free Attack", StringComparison.OrdinalIgnoreCase));
        }
        powerAgg ??= new PowerAggregate();

        AppendFreeCardDiscountStats(
            sb,
            "Free Attack",
            "Attacks",
            powerAgg.FreeAttackChargesGranted,
            powerAgg.FreeAttackChargesUsed,
            powerAgg.FreeAttackZeroEnergySavingsUses,
            powerAgg.FreeAttackEnergySaved,
            powerAgg.FreeAttackBasicAttacksDiscounted,
            powerAgg.FreeAttackCommonAttacksDiscounted,
            powerAgg.FreeAttackUncommonAttacksDiscounted,
            powerAgg.FreeAttackRareAttacksDiscounted,
            compact);
    }

    private static void AppendPounceFreeSkillStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        RunMetaStats metaStats,
        bool compact)
    {
        if (card is not Pounce && !IsCardId(card, "CARD.POUNCE")) return;

        metaStats ??= new RunMetaStats();
        PowerAggregate? powerAgg = null;
        if (metaStats.PowerAggregates != null)
        {
            metaStats.PowerAggregates.TryGetValue(FreeSkillPowerId, out powerAgg);
            powerAgg ??= metaStats.PowerAggregates.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.PowerId, FreeSkillPowerId, StringComparison.Ordinal)
                || string.Equals(candidate.DisplayName, "Free Skill", StringComparison.OrdinalIgnoreCase));
        }
        powerAgg ??= new PowerAggregate();

        AppendFreeCardDiscountStats(
            sb,
            "Free Skill",
            "Skills",
            powerAgg.FreeSkillChargesGranted,
            powerAgg.FreeSkillChargesUsed,
            powerAgg.FreeSkillZeroEnergySavingsUses,
            powerAgg.FreeSkillEnergySaved,
            powerAgg.FreeSkillBasicSkillsDiscounted,
            powerAgg.FreeSkillCommonSkillsDiscounted,
            powerAgg.FreeSkillUncommonSkillsDiscounted,
            powerAgg.FreeSkillRareSkillsDiscounted,
            compact);
    }

    private static void AppendFreeCardDiscountStats(
        StringBuilder sb,
        string chargeName,
        string discountedCardType,
        int chargesGranted,
        int chargesUsed,
        int zeroSavingsUses,
        decimal energySaved,
        int basicDiscounted,
        int commonDiscounted,
        int uncommonDiscounted,
        int rareDiscounted,
        bool compact)
    {
        var utilization = chargesGranted <= 0
            ? 0m
            : 100m * chargesUsed / chargesGranted;
        Row3(
            sb,
            $"{chargeName} charges used/granted",
            $"{chargesUsed}/{chargesGranted}",
            $"{utilization:F0}%");
        Row3(
            sb,
            GetEnergyStatLabel("total saved"),
            FormatDecimal(energySaved),
            "");
        if (compact) return;

        var averageEnergySaved = chargesUsed <= 0
            ? 0m
            : energySaved / chargesUsed;
        Row3(
            sb,
            GetEnergyStatLabel("charges used with 0 saved"),
            zeroSavingsUses.ToString(),
            "");
        Row3(
            sb,
            GetEnergyStatLabel("avg saved per charge used"),
            FormatDecimal(averageEnergySaved),
            "");
        Row3(sb, $"Basic {discountedCardType} discounted", basicDiscounted.ToString(), "");
        Row3(sb, $"Common {discountedCardType} discounted", commonDiscounted.ToString(), "");
        Row3(sb, $"Uncommon {discountedCardType} discounted", uncommonDiscounted.ToString(), "");
        Row3(sb, $"Rare {discountedCardType} discounted", rareDiscounted.ToString(), "");
    }

    private static void AppendDebtStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        CardAggregate agg)
    {
        if (card is not Debt && !IsCardId(card, "CARD.DEBT")) return;

        Row3(sb, "Times triggered", agg.DebtTriggers.ToString(), "");
        Row3(sb, "Gold loss attempted", (agg.DebtTriggers * DebtGoldLossPerTrigger).ToString(), "");
        Row3(sb, "Gold lost", agg.DebtGoldLost.ToString(), "");
        Row3(sb, "Gold loss blocked", agg.DebtGoldLossBlocked.ToString(), "");
    }

    private static void AppendNormalityStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel card,
        CardAggregate agg)
    {
        if (card is not Normality && !IsCardId(card, "CARD.NORMALITY")) return;

        var averageExcessEnergy = agg.NormalityTurnsEndedInHand <= 0
            ? 0m
            : (decimal)agg.NormalityExcessEnergyAtTurnEndTotal
                / agg.NormalityTurnsEndedInHand;
        Row3(
            sb,
            "Turns ended in hand",
            agg.NormalityTurnsEndedInHand.ToString(),
            "");
        Row3(
            sb,
            GetEnergyStatLabel("avg excess at turn end"),
            FormatDecimal(averageExcessEnergy),
            "");
    }

    private static bool IsCardId(MegaCrit.Sts2.Core.Models.CardModel? card, string id)
    {
        try
        {
            return card?.Id != null
                && string.Equals(card.Id.ToString(), id, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void AppendReplayStats(StringBuilder sb, CardAggregate agg)
    {
        if (agg.TimesReplayExtraPlanned <= 0
            && agg.TimesReplayExtraPlayed <= 0
            && agg.TimesReplayAttackNoDamage <= 0)
            return;

        if (agg.TimesReplayExtraPlanned > 0)
            Row3(sb, "Replay planned/played", $"{agg.TimesReplayExtraPlanned}/{agg.TimesReplayExtraPlayed}", "");
        else
            Row3(sb, "Replay extra plays", agg.TimesReplayExtraPlayed.ToString(), "");
        foreach (var reason in agg.ReplayExtraPlayReasons.Values
                     .Where(r => r.Count > 0)
                     .OrderByDescending(r => r.Count)
                     .ThenBy(r => r.DisplayName))
        {
            var displayName = StatsTooltip.EscapeBbcode(string.IsNullOrWhiteSpace(reason.DisplayName)
                ? reason.ReasonId
                : reason.DisplayName);
            Row3(sb, $"Replay from {displayName}", reason.Count.ToString(), "");
        }

        if (agg.TimesReplayAttackNoDamage <= 0) return;

        Row3(sb, "Replay no-damage attacks", agg.TimesReplayAttackNoDamage.ToString(), "");
        foreach (var reason in agg.ReplayAttackNoDamageReasons.Values
                     .Where(r => r.Count > 0)
                     .OrderByDescending(r => r.Count)
                     .ThenBy(r => r.DisplayName))
        {
            var displayName = StatsTooltip.EscapeBbcode(string.IsNullOrWhiteSpace(reason.DisplayName)
                ? reason.ReasonId
                : reason.DisplayName);
            Row3(sb, $"No damage from {displayName}", reason.Count.ToString(), "");
        }
    }

    /// <summary>
    /// Emit a single stat row in the canonical natural-width scalar table.
    /// Consecutive rows share the table, so the widest label establishes one
    /// left-aligned value column. <paramref name="pct"/> can be empty; its
    /// cell remains present so percentage-bearing rows retain the same shape.
    /// </summary>
    private static void Row3(
        StringBuilder sb,
        string label,
        string value,
        string pct,
        string? fullDescription = null)
    {
        var presentation = StatsTooltip.CreateStatRowPresentation(
            label,
            fullDescription);
        StatsTooltip.AppendScalarStatRow(
            sb,
            presentation,
            value,
            pct,
            labelColor: "#e0e0e0");
    }

    private static bool AppendDedicatedPoisonStats(StringBuilder sb, CardAggregate agg, bool compact)
    {
        var poison = GetPoisonSummary(agg);
        if (poison == null) return false;

        if (compact)
        {
            if (poison.Value.TimesApplied <= 0 && poison.Value.TotalAmountApplied == 0m)
                return false;

            var extra = poison.Value.TimesApplied > 0
                ? poison.Value.TimesApplied > 1 ? $"{poison.Value.TimesApplied}x" : "1x"
                : "";
            Row3(sb, GetPoisonStatLabel(poison.Value, "applied"), FormatDecimal(poison.Value.TotalAmountApplied), extra);
            return true;
        }

        decimal avgPoison = agg.Plays > 0 ? poison.Value.TotalAmountApplied / agg.Plays : 0m;

        Row3(sb, GetPoisonStatLabel(poison.Value, "total applied"), FormatDecimal(poison.Value.TotalAmountApplied), "");
        Row3(sb, GetPoisonStatLabel(poison.Value, "avg applied"), FormatDecimal(avgPoison), "");
        Row3(sb, GetPoisonStatLabel(poison.Value, "applications"), poison.Value.TimesApplied.ToString(), "");

        if (poison.Value.TotalTriggeredEffectiveDamage > 0m || poison.Value.TotalTriggeredOverkill > 0m)
        {
            decimal avgPoisonDamage = agg.Plays > 0 ? poison.Value.TotalTriggeredEffectiveDamage / agg.Plays : 0m;
            Row3(sb, GetPoisonStatLabel(poison.Value, "damage"), FormatDecimal(poison.Value.TotalTriggeredEffectiveDamage), "");
            Row3(sb, GetPoisonStatLabel(poison.Value, "avg damage"), FormatDecimal(avgPoisonDamage), "");

            if (poison.Value.TotalTriggeredOverkill > 0m)
                Row3(sb, GetPoisonStatLabel(poison.Value, "overkill"), FormatDecimal(poison.Value.TotalTriggeredOverkill), "");
        }

        if (poison.Value.TimesBlockedByArtifact > 0)
        {
            string extra = poison.Value.TimesBlockedByArtifact > 1
                ? $"{poison.Value.TimesBlockedByArtifact}x"
                : "1x";
            Row3(sb, GetPoisonStatLabel(poison.Value, "blocked by Artifact"), FormatDecimal(poison.Value.TotalAmountBlockedByArtifact), extra);
        }

        return true;
    }

    private static PoisonEffectSummary? GetPoisonSummary(CardAggregate agg)
    {
        if (agg.AppliedEffects == null || agg.AppliedEffects.Count == 0) return null;

        int timesApplied = 0;
        decimal totalAmountApplied = 0m;
        int timesBlockedByArtifact = 0;
        decimal totalAmountBlockedByArtifact = 0m;
        decimal totalTriggeredEffectiveDamage = 0m;
        decimal totalTriggeredOverkill = 0m;
        string? iconPath = null;

        foreach (var effect in agg.AppliedEffects.Values)
        {
            if (!IsPoisonEffect(effect)) continue;

            timesApplied += effect.TimesApplied;
            totalAmountApplied += effect.TotalAmountApplied;
            timesBlockedByArtifact += effect.TimesBlockedByArtifact;
            totalAmountBlockedByArtifact += effect.TotalAmountBlockedByArtifact;
            totalTriggeredEffectiveDamage += effect.TotalTriggeredEffectiveDamage;
            totalTriggeredOverkill += effect.TotalTriggeredOverkill;
            if (string.IsNullOrWhiteSpace(iconPath) && !string.IsNullOrWhiteSpace(effect.IconPath))
                iconPath = effect.IconPath;
        }

        if (timesApplied <= 0 &&
            totalAmountApplied == 0m &&
            timesBlockedByArtifact <= 0 &&
            totalAmountBlockedByArtifact == 0m &&
            totalTriggeredEffectiveDamage == 0m &&
            totalTriggeredOverkill == 0m)
            return null;

        return new PoisonEffectSummary(
            timesApplied,
            totalAmountApplied,
            timesBlockedByArtifact,
            totalAmountBlockedByArtifact,
            totalTriggeredEffectiveDamage,
            totalTriggeredOverkill,
            iconPath);
    }

    private static string GetPoisonStatLabel(PoisonEffectSummary poison, string suffix)
    {
        if (!string.IsNullOrWhiteSpace(poison.IconPath))
            return GetInlineIconStatLabel(poison.IconPath, suffix);

        return $"Poison {suffix}";
    }

    private static string GetBlockStatLabel(string suffix)
    {
        return GetInlineIconStatLabel(BlockIconPath, suffix);
    }

    private static string GetDrawStatLabel(string suffix)
    {
        return GetInlineIconStatLabel(DrawCardsNextTurnPowerIconPath, suffix);
    }

    private static string GetEnergyStatLabel(string suffix)
    {
        return $"{StatEnergyIcon.RenderInline(InlineKeywordIconSize)} {suffix}";
    }

    private static string GetStarStatLabel(string suffix)
    {
        return GetInlineIconStatLabel(StarIconPath, suffix);
    }

    private static string GetForgeStatLabel(string suffix)
    {
        return suffix switch
        {
            "avg gained" => "Forge avg",
            _ => $"Forge {suffix}",
        };
    }

    private static void AppendDeathMarchStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel cardModel,
        int cardsDrawnThisTurn)
    {
        if (cardModel is not DeathMarch
            && !IsCardId(cardModel, "CARD.DEATH_MARCH"))
        {
            return;
        }

        Row3(
            sb,
            "Cards drawn this turn",
            Math.Max(0, cardsDrawnThisTurn).ToString(),
            "",
            "Cards drawn this turn — successful draws by this card's owner during the current turn, excluding the automatic opening-hand draw to match Death March's damage scaling.");
    }

    private static void AppendMakeItSoStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel cardModel,
        CardAggregate agg,
        bool compact)
    {
        if (cardModel is not MakeItSo) return;

        int? currentCounter = null;
        int threshold = 0;
        if (RunTracker.TryGetMakeItSoSkillCounter(cardModel, out var current, out var currentThreshold))
        {
            currentCounter = current;
            threshold = currentThreshold;
        }

        AppendMakeItSoStats(sb, agg, compact, currentCounter, threshold);
    }

    private static void AppendMakeItSoStats(
        StringBuilder sb,
        CardAggregate agg,
        bool compact,
        int? currentCounter,
        int threshold)
    {
        if (currentCounter.HasValue && threshold > 0)
            Row3(sb, "Skills this turn", $"{currentCounter.Value}/{threshold}", "");

        if (!compact && agg.TimesSummonedToHand > 0)
            Row3(sb, "Times triggered", agg.TimesSummonedToHand.ToString(), "");
    }

    private static void AppendPullFromBelowStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel cardModel,
        int etherealCardsPlayedThisCombat)
    {
        if (cardModel is not PullFromBelow) return;

        Row3(sb, "Ethereal cards played this combat", etherealCardsPlayedThisCombat.ToString(), "");
    }

    private static void AppendSupermassiveStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel cardModel,
        int cardsCreatedThisCombat)
    {
        if (cardModel is not Supermassive
            && !IsCardId(cardModel, "CARD.SUPERMASSIVE"))
        {
            return;
        }

        Row3(
            sb,
            "Cards created this combat",
            Math.Max(0, cardsCreatedThisCombat).ToString(),
            "");
    }

    private static int? GetSupermassiveCardsCreatedThisCombat(
        MegaCrit.Sts2.Core.Models.CardModel cardModel)
    {
        if (cardModel is not Supermassive
            && !IsCardId(cardModel, "CARD.SUPERMASSIVE"))
        {
            return null;
        }

        return RunTracker.GetCardsCreatedThisCombat(cardModel.Owner);
    }

    private static string GetInlineIconStatLabel(string iconPath, string suffix)
    {
        var normalizedPath = NormalizeResourcePath(iconPath);
        return $"[img={InlineKeywordIconSize}x{InlineKeywordIconSize}]{normalizedPath}[/img] {suffix}";
    }

    private static string NormalizeResourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return path.StartsWith("res://", StringComparison.Ordinal)
            ? path
            : $"res://{path.TrimStart('/')}";
    }

    private static void AppendAppliedEffects(StringBuilder sb, CardAggregate agg, bool compact, bool excludePoison)
    {
        if (agg.AppliedEffects == null || agg.AppliedEffects.Count == 0) return;
        bool hasArtifactBlockedSummary = GetArtifactBlockedTotals(agg, excludePoison).Times > 0;
        var visibleEffects = agg.AppliedEffects.Values
            .Where(effect => ShouldShowAppliedEffectRow(effect, hasArtifactBlockedSummary, excludePoison))
            .OrderByDescending(e => e.TimesApplied)
            .ThenBy(e => e.DisplayName)
            .ToList();

        if (visibleEffects.Count == 0) return;

        if (!compact)
            sb.Append("[color=#b5b5b5]Effects applied[/color]\n");

        int shown = 0;
        foreach (var effect in visibleEffects)
        {
            if (compact && shown >= 2) break;

            var label = GetAppliedEffectLabel(effect);
            var value = FormatDecimal(effect.TotalAmountApplied);
            var extra = effect.TimesApplied > 1 ? $"{effect.TimesApplied}x" : "1x";
            Row3(sb, label, value, extra);

            if (!compact && effect.TotalTriggeredCardsDrawBlocked > 0)
                Row3(sb, GetAppliedEffectBlockedDrawLabel(effect), effect.TotalTriggeredCardsDrawBlocked.ToString(), "");

            shown++;
        }
    }

    private static void AppendArtifactBlockedSummary(StringBuilder sb, CardAggregate agg, bool excludePoison)
    {
        var (times, amount) = GetArtifactBlockedTotals(agg, excludePoison);
        if (times <= 0) return;

        var label = GetArtifactStrippedLabel(agg, excludePoison);
        var value = times.ToString();
        var extra = amount != times ? $"{FormatDecimal(amount)} amt" : "";
        Row3(sb, label, value, extra);
    }

    private static (int Times, decimal Amount) GetArtifactBlockedTotals(CardAggregate agg, bool excludePoison)
    {
        if (agg.AppliedEffects == null || agg.AppliedEffects.Count == 0)
            return (0, 0m);

        int times = 0;
        decimal amount = 0m;
        foreach (var effect in agg.AppliedEffects.Values)
        {
            if (excludePoison && IsPoisonEffect(effect)) continue;
            times += effect.TimesBlockedByArtifact;
            amount += effect.TotalAmountBlockedByArtifact;
        }

        return (times, amount);
    }

    private static bool ShouldShowAppliedEffectRow(AppliedEffectAggregate effect, bool hasArtifactBlockedSummary, bool excludePoison)
    {
        if (excludePoison && IsPoisonEffect(effect))
            return false;

        if (effect.TotalAmountApplied == 0m && effect.TimesBlockedByArtifact > 0)
            return false;

        if (hasArtifactBlockedSummary && IsArtifactEffect(effect) && effect.TotalAmountApplied < 0m)
            return false;

        return true;
    }

    private static string GetArtifactStrippedLabel(CardAggregate agg, bool excludePoison)
    {
        if (agg.AppliedEffects != null)
        {
            foreach (var effect in agg.AppliedEffects.Values)
            {
                if (excludePoison && IsPoisonEffect(effect)) continue;
                if (!IsArtifactEffect(effect) || string.IsNullOrWhiteSpace(effect.IconPath)) continue;
                return $"[img={InlineKeywordIconSize}x{InlineKeywordIconSize}]{effect.IconPath}[/img] stripped";
            }
        }

        return "Artifact stripped";
    }

    private static bool IsArtifactEffect(AppliedEffectAggregate effect)
    {
        if (!string.IsNullOrWhiteSpace(effect.EffectId) &&
            effect.EffectId.Contains("ARTIFACT_POWER", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(effect.DisplayName, "Artifact", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPoisonEffect(AppliedEffectAggregate effect)
    {
        if (!string.IsNullOrWhiteSpace(effect.EffectId) &&
            effect.EffectId.Contains("POISON", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(effect.DisplayName, "Poison", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAppliedEffectLabel(AppliedEffectAggregate effect)
    {
        // Escape the game/data-derived name before it lands in a BBCode cell:
        // a display name containing '[' would otherwise be parsed as markup.
        var label = StatsTooltip.EscapeBbcode(
            string.IsNullOrWhiteSpace(effect.DisplayName) ? effect.EffectId : effect.DisplayName);
        if (IsEnergyEffect(effect))
            return GetEnergyEffectLabel(label);
        if (!string.IsNullOrWhiteSpace(effect.IconPath))
            return GetInlineIconStatLabel(effect.IconPath, label);
        if (IsStarEffect(effect))
            return GetStarEffectLabel(label);
        if (IsNoxiousFumesEffect(effect))
            return GetIconBackedEffectLabel(label, effect.IconPath);

        return label;
    }

    private static string GetAppliedEffectBlockedDrawLabel(AppliedEffectAggregate effect)
    {
        return GetBlockedDrawStatLabel("cards blocked");
    }

    private static string GetBlockedDrawStatLabel(string suffix)
    {
        return GetInlineIconStatLabel(BlockedDrawIconPath, suffix);
    }

    private static void AppendCardDrawStats(StringBuilder sb, CardAggregate agg)
    {
        int attempted = agg.TimesCardsDrawAttempted;
        if (attempted <= 0)
            attempted = agg.TimesCardsDrawn + agg.TimesCardsDrawBlocked;

        if (attempted > agg.TimesCardsDrawn)
        {
            Row3(sb, GetDrawStatLabel("drawn / tried"), $"{agg.TimesCardsDrawn}/{attempted}", "");
            AppendBlockedDrawReasonRows(sb, agg, attempted - agg.TimesCardsDrawn);
            return;
        }

        if (agg.TimesCardsDrawn > 0)
            Row3(sb, GetDrawStatLabel("cards drawn"), agg.TimesCardsDrawn.ToString(), "");
    }

    private static void AppendOrbCreationStats(
        StringBuilder sb,
        MegaCrit.Sts2.Core.Models.CardModel? cardModel,
        CardAggregate agg,
        RunMetaStats? metaStats,
        bool compact)
    {
        var expectedOrbIds = new HashSet<string>(
            OrbCardRegistry.GetExpectedOrbIds(cardModel),
            StringComparer.Ordinal);
        var combined = new Dictionary<string, CardOrbAggregate>(
            StringComparer.Ordinal);
        MergeOrbOutcomesForDisplay(combined, agg.OrbOutcomes);
        var totalOrbsCreated = agg.TotalOrbsCreated;

        if (cardModel != null
            && OrbCardRegistry.TryGetRecurringByCard(
                cardModel,
                out var definition)
            && definition != null)
        {
            foreach (var orbId in OrbCardRegistry.GetExpectedOrbIdsForPower(
                         definition.PowerId))
            {
                expectedOrbIds.Add(orbId);
            }

            var powerAggregate = GetOrbPowerAggregate(
                metaStats,
                definition);
            totalOrbsCreated += powerAggregate.TotalOrbsCreated;
            MergeOrbOutcomesForDisplay(combined, powerAggregate.OrbOutcomes);
        }

        AppendOrbOutcomeRows(
            sb,
            expectedOrbIds,
            combined,
            totalOrbsCreated,
            compact);
    }

    private static PowerAggregate GetOrbPowerAggregate(
        RunMetaStats? metaStats,
        OrbPowerDefinition definition)
    {
        if (metaStats?.PowerAggregates != null
            && metaStats.PowerAggregates.TryGetValue(
                definition.PowerId,
                out var aggregate))
        {
            return aggregate;
        }

        return new PowerAggregate
        {
            PowerId = definition.PowerId,
            DisplayName = definition.DisplayName,
        };
    }

    private static void AppendOrbOutcomeRows(
        StringBuilder sb,
        IEnumerable<string> expectedOrbIdSource,
        Dictionary<string, CardOrbAggregate> combined,
        int totalOrbsCreated,
        bool compact)
    {
        var expectedOrbIds = expectedOrbIdSource.ToHashSet(
            StringComparer.Ordinal);
        var observedOrbIds = combined.Values
            .Where(outcome =>
                outcome != null
                && (outcome.Created > 0
                    || outcome.PassiveActivations > 0
                    || outcome.Evokes > 0
                    || outcome.Fizzles > 0
                    || outcome.BlockGained > 0
                    || outcome.EnergyGenerated > 0
                    || HasOrbDamageStats(outcome)))
            .Select(outcome => outcome.OrbId)
            .Where(orbId => !string.IsNullOrWhiteSpace(orbId))
            .ToHashSet(StringComparer.Ordinal);

        // Fixed one-to-three-orb cards show their complete zero state. Random
        // generators (Chaos and Trash to Treasure) avoid a five-type wall and
        // expand only as each actual orb type is observed.
        var orbIdsToRender = new HashSet<string>(observedOrbIds, StringComparer.Ordinal);
        if (expectedOrbIds.Count <= 3)
            orbIdsToRender.UnionWith(expectedOrbIds);

        var outcomes = orbIdsToRender
            .Select(orbId => combined.TryGetValue(orbId, out var outcome)
                ? outcome
                : new CardOrbAggregate { OrbId = orbId })
            .OrderBy(outcome => outcome.OrbId, StringComparer.Ordinal)
            .ToList();

        if (compact)
        {
            if (totalOrbsCreated > 0 || expectedOrbIds.Count > 0)
            {
                var compactOrbIds = outcomes.Count > 0
                    ? outcomes.Select(outcome => outcome.OrbId)
                    : expectedOrbIds;
                Row3(
                    sb,
                    GetOrbGroupStatLabel(compactOrbIds, "created"),
                    totalOrbsCreated.ToString(),
                    "");
            }
            return;
        }

        if (outcomes.Count == 0)
        {
            if (totalOrbsCreated > 0 || expectedOrbIds.Count > 0)
            {
                Row3(
                    sb,
                    GetOrbGroupStatLabel(expectedOrbIds, "created"),
                    totalOrbsCreated.ToString(),
                    "");
            }
            return;
        }

        foreach (var outcome in outcomes)
        {
            var orbId = string.IsNullOrWhiteSpace(outcome.OrbId)
                ? "ORB.UNKNOWN"
                : outcome.OrbId;
            Row3(
                sb,
                GetOrbStatLabel(orbId, "created"),
                outcome.Created.ToString(),
                "");
            Row3(
                sb,
                GetOrbStatLabel(orbId, "passive activations"),
                outcome.PassiveActivations.ToString(),
                "");
            Row3(
                sb,
                GetOrbStatLabel(orbId, "evoked"),
                outcome.Evokes.ToString(),
                "");
            Row3(
                sb,
                GetOrbStatLabel(orbId, "fizzled"),
                outcome.Fizzles.ToString(),
                "");

            if (IsDamageOrbId(orbId) || HasOrbDamageStats(outcome))
            {
                Row3(
                    sb,
                    GetOrbStatLabel(orbId, "damage attempted"),
                    outcome.DamageAttempted.ToString(),
                    "");
                Row3(
                    sb,
                    GetOrbStatLabel(orbId, "damage dealt"),
                    outcome.DamageDealt.ToString(),
                    "");
                Row3(
                    sb,
                    GetOrbStatLabel(orbId, "damage blocked"),
                    outcome.DamageBlocked.ToString(),
                    "");
                Row3(
                    sb,
                    GetOrbStatLabel(orbId, "overkill"),
                    outcome.DamageOverkill.ToString(),
                    "");
                Row3(
                    sb,
                    GetOrbStatLabel(orbId, "kills"),
                    outcome.Kills.ToString(),
                    "");
                Row3(
                    sb,
                    GetOrbStatLabel(orbId, "targets hit"),
                    outcome.TargetsHit.ToString(),
                    "");
            }

            if (IsFrostOrbId(orbId))
            {
                Row3(
                    sb,
                    GetFrostOrbBlockStatLabel(),
                    outcome.BlockGained.ToString(),
                    "");
            }

            if (IsPlasmaOrbId(orbId))
            {
                Row3(
                    sb,
                    GetPlasmaOrbEnergyStatLabel(),
                    outcome.EnergyGenerated.ToString(),
                    "");
            }
        }
    }

    private static void MergeOrbOutcomesForDisplay(
        Dictionary<string, CardOrbAggregate> target,
        Dictionary<string, CardOrbAggregate>? source)
    {
        if (source == null) return;

        foreach (var (key, value) in source)
        {
            if (value == null) continue;
            var orbId = NormalizeOrbId(
                string.IsNullOrWhiteSpace(value.OrbId) ? key : value.OrbId);
            if (!target.TryGetValue(orbId, out var combined))
            {
                combined = new CardOrbAggregate { OrbId = orbId };
                target[orbId] = combined;
            }

            combined.Created += value.Created;
            combined.PassiveActivations += value.PassiveActivations;
            combined.Evokes += value.Evokes;
            combined.Fizzles += value.Fizzles;
            combined.BlockGained += value.BlockGained;
            combined.EnergyGenerated += value.EnergyGenerated;
            combined.DamageAttempted += value.DamageAttempted;
            combined.DamageDealt += value.DamageDealt;
            combined.DamageBlocked += value.DamageBlocked;
            combined.DamageOverkill += value.DamageOverkill;
            combined.Kills += value.Kills;
            combined.TargetsHit += value.TargetsHit;
        }
    }

    private static string GetOrbStatLabel(string orbId, string suffix)
    {
        return $"{RenderOrbInlineIcon(orbId)} {suffix}";
    }

    private static string GetOrbGroupStatLabel(
        IEnumerable<string> orbIds,
        string suffix)
    {
        var icons = orbIds
            .Where(orbId => !string.IsNullOrWhiteSpace(orbId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(orbId => orbId, StringComparer.Ordinal)
            .Select(RenderOrbInlineIcon)
            .ToList();
        if (icons.Count == 0)
            icons.Add(RenderOrbInlineIcon("ORB.UNKNOWN"));

        return $"{string.Join(" ", icons)} {suffix}";
    }

    internal static string RenderOrbInlineIcon(string orbId)
    {
        // Godot 4's RichTextLabel uses named width/height attributes. The old
        // [img=16x16] form survives the vocabulary parser but renders as an
        // empty slot, which is why the screenshot showed only the semantic
        // activation/damage icons.
        return StatConceptGlossary.RenderHintedInlineImage(
            GetOrbIconPath(orbId),
            GetOrbHint(orbId));
    }

    private static string GetOrbHint(string orbId)
    {
        var normalizedOrbId = NormalizeOrbId(orbId);
        var fallbackTitle = RunTracker.FormatOrbIdForDisplay(normalizedOrbId);

        try
        {
            var orb = ModelDb.GetByIdOrNull<OrbModel>(
                ModelId.Deserialize(normalizedOrbId));
            if (orb == null) return fallbackTitle;

            var title = GetLocalizedOrbText(
                () => orb.Title.GetFormattedText(),
                () => orb.Title.GetRawText(),
                fallbackTitle);
            var description = GetLocalizedOrbText(
                () => orb.Description.GetFormattedText(),
                () => orb.Description.GetRawText(),
                string.Empty);
            description = StripBbcodeForHint(description);

            return string.IsNullOrWhiteSpace(description)
                ? title
                : $"{title}: {description}";
        }
        catch
        {
            // Historical stats can contain an orb that no longer resolves in
            // the current model database. Its readable ID is still a useful
            // hint, and avoids making the image silently non-interactive.
            return fallbackTitle;
        }
    }

    private static string GetLocalizedOrbText(
        Func<string> getFormattedText,
        Func<string> getRawText,
        string fallback)
    {
        try
        {
            var text = getFormattedText();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        catch
        {
        }

        try
        {
            var text = getRawText();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        catch
        {
        }

        return fallback;
    }

    private static string StripBbcodeForHint(string value)
    {
        const string escapedBracketPlaceholder = "\uE000";
        var protectedEscapedBrackets = value.Replace(
            "[lb]",
            escapedBracketPlaceholder,
            StringComparison.Ordinal);
        var withoutTags = Regex.Replace(protectedEscapedBrackets, @"\[[^\]]+\]", " ");
        return Regex.Replace(withoutTags, @"\s+", " ")
            .Replace(escapedBracketPlaceholder, "[", StringComparison.Ordinal)
            .Trim();
    }

    private static string GetFrostOrbBlockStatLabel()
    {
        return $"{RenderOrbInlineIcon(OrbCardRegistry.FrostOrbId)} "
            + $"[img={InlineKeywordIconSize}x{InlineKeywordIconSize}]"
            + $"{BlockIconPath}[/img]";
    }

    private static string GetPlasmaOrbEnergyStatLabel()
    {
        return $"{RenderOrbInlineIcon(OrbCardRegistry.PlasmaOrbId)} "
            + GetEnergyStatLabel("");
    }

    private static bool IsFrostOrbId(string orbId)
    {
        return string.Equals(
            NormalizeOrbId(orbId),
            OrbCardRegistry.FrostOrbId,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDamageOrbId(string orbId)
    {
        var normalized = NormalizeOrbId(orbId);
        return string.Equals(
                   normalized,
                   OrbCardRegistry.LightningOrbId,
                   StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                normalized,
                OrbCardRegistry.DarkOrbId,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                normalized,
                OrbCardRegistry.GlassOrbId,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlasmaOrbId(string orbId)
    {
        return string.Equals(
            NormalizeOrbId(orbId),
            OrbCardRegistry.PlasmaOrbId,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasOrbDamageStats(CardOrbAggregate outcome)
    {
        return outcome.DamageAttempted > 0
            || outcome.DamageDealt > 0
            || outcome.DamageBlocked > 0
            || outcome.DamageOverkill > 0
            || outcome.Kills > 0
            || outcome.TargetsHit > 0;
    }

    private static string GetOrbIconPath(string orbId)
    {
        orbId = NormalizeOrbId(orbId);
        var separator = orbId.LastIndexOf('.');
        var entry = separator >= 0 && separator < orbId.Length - 1
            ? orbId[(separator + 1)..]
            : orbId;
        if (string.IsNullOrWhiteSpace(entry))
            entry = "unknown";

        var safeEntry = new string(entry
            .Where(ch => char.IsLetterOrDigit(ch) || ch == '_')
            .ToArray())
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(safeEntry))
            safeEntry = "unknown";

        return $"res://images/orbs/{safeEntry}.png";
    }

    private static string NormalizeOrbId(string? orbId)
    {
        if (string.IsNullOrWhiteSpace(orbId)) return "ORB.UNKNOWN";

        var separator = orbId.LastIndexOf('.');
        var entry = separator >= 0 && separator < orbId.Length - 1
            ? orbId[(separator + 1)..]
            : orbId;
        var canonicalEntry = entry.ToUpperInvariant() switch
        {
            "DARK" or "DARK_ORB" => "DARK_ORB",
            "FROST" or "FROST_ORB" => "FROST_ORB",
            "GLASS" or "GLASS_ORB" => "GLASS_ORB",
            "LIGHTNING" or "LIGHTNING_ORB" => "LIGHTNING_ORB",
            "PLASMA" or "PLASMA_ORB" => "PLASMA_ORB",
            _ => entry,
        };
        return $"ORB.{canonicalEntry}";
    }

    private static void AppendBlockedDrawReasonRows(StringBuilder sb, CardAggregate agg, int blockedGap)
    {
        if (blockedGap <= 0) return;

        int categorized = 0;
        foreach (var reason in agg.BlockedDrawReasons.Values
                     .OrderByDescending(r => r.Count)
                     .ThenBy(r => r.DisplayName))
        {
            if (reason.Count <= 0) continue;
            Row3(sb, GetBlockedDrawReasonLabel(reason.DisplayName), reason.Count.ToString(), "");
            categorized += reason.Count;
        }

        int uncategorized = Math.Max(0, blockedGap - categorized);
        if (uncategorized > 0)
            Row3(sb, GetBlockedDrawReasonLabel("other"), uncategorized.ToString(), "");
    }

    private static string GetBlockedDrawReasonLabel(string reasonDisplayName)
    {
        return GetBlockedDrawStatLabel($"blocked by {StatsTooltip.EscapeBbcode(reasonDisplayName)}");
    }

    private static bool IsEnergyEffect(AppliedEffectAggregate effect)
    {
        if (!string.IsNullOrWhiteSpace(effect.EffectId) &&
            effect.EffectId.Contains("ENERGY", StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(effect.DisplayName) &&
               effect.DisplayName.Contains("Energy", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetEnergyEffectLabel(string label)
    {
        const string energyPrefix = "Energy ";
        if (label.StartsWith(energyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = label.Substring(energyPrefix.Length).Trim();
            if (!string.IsNullOrWhiteSpace(suffix))
                return GetEnergyStatLabel(suffix);
        }

        return GetEnergyStatLabel(label);
    }

    private static bool IsStarEffect(AppliedEffectAggregate effect)
    {
        if (!string.IsNullOrWhiteSpace(effect.EffectId) &&
            effect.EffectId.Contains("STAR", StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(effect.DisplayName) &&
               effect.DisplayName.StartsWith("Star", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetStarEffectLabel(string label)
    {
        const string pluralPrefix = "Stars ";
        const string singularPrefix = "Star ";

        if (label.StartsWith(pluralPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = label.Substring(pluralPrefix.Length).Trim();
            if (!string.IsNullOrWhiteSpace(suffix))
                return GetInlineIconStatLabel(StarIconPath, suffix);
        }

        if (label.StartsWith(singularPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = label.Substring(singularPrefix.Length).Trim();
            if (!string.IsNullOrWhiteSpace(suffix))
                return GetInlineIconStatLabel(StarIconPath, suffix);
        }

        return GetInlineIconStatLabel(StarIconPath, label);
    }

    private static bool IsNoxiousFumesEffect(AppliedEffectAggregate effect)
    {
        if (!string.IsNullOrWhiteSpace(effect.EffectId) &&
            effect.EffectId.Contains("NOXIOUS_FUMES", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(effect.DisplayName, "Noxious Fumes", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetIconBackedEffectLabel(string label, string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
            return label;

        return GetInlineIconStatLabel(iconPath, label);
    }

    private static string FormatDecimal(decimal value)
    {
        return decimal.Truncate(value) == value
            ? value.ToString("0")
            : value.ToString("0.##");
    }

    /// <summary>
    /// Whether the energy-spent stats are worth showing. Rule (per Nelson):
    /// show only when empirical variance exists between the actual energy
    /// paid across all plays and what you'd expect if every play cost the
    /// listed amount. If the rows aren't there, the user can safely assume
    /// 1 play = 1 listed cost and not think about it.
    ///
    /// This single rule subsumes every specific trigger we previously
    /// enumerated (Snecko, Master Planner, Sly, Corruption, upgrade cost
    /// change, X-cost, ...): they ALL manifest as a TotalEnergySpent that
    /// doesn't equal listed-cost × plays. If any of those mechanics is
    /// active and the card's been played, variance will show up and the
    /// rows will appear.
    ///
    /// Consequence: unplayed cards don't show energy stats even under a
    /// cost-variance relic. Acceptable — play the card once and it starts
    /// showing. Simpler than maintaining an enumeration of triggers the
    /// game's balance team might add to in a future patch.
    /// </summary>
    private static bool IsEnergyInteresting(
        MegaCrit.Sts2.Core.Models.CardModel card, CardAggregate agg)
    {
        try
        {
            if (agg.Plays <= 0) return false;
            // A card that was played and never cost anything is interesting on
            // its own, whatever its printed cost says. The comparison below
            // treats a 0-cost card as unremarkable (0 spent == 0 expected), but
            // "spent nothing all run" is exactly the evidence behind its rank
            // when the deck is sorted by damage per energy — so the rows that
            // would back that up must not be suppressed.
            if (agg.TotalEnergySpent == 0) return true;
            int expectedPerPlay = card.EnergyCost.GetWithModifiers(CostModifiers.None);
            if (expectedPerPlay < 0) return true;  // X-cost / negative sentinel — show if played
            return agg.TotalEnergySpent != expectedPerPlay * agg.Plays;
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"IsEnergyInteresting failed: {e.Message}");
            return false;
        }
    }

    private static bool IsStarInteresting(
        MegaCrit.Sts2.Core.Models.CardModel card, CardAggregate agg)
    {
        try
        {
            if (agg.Plays <= 0 || agg.TotalStarsSpent <= 0) return false;
            if (card.HasStarCostX) return true;

            int expectedPerPlay = Math.Max(0, card.GetStarCostWithModifiers());
            return agg.TotalStarsSpent != expectedPerPlay * agg.Plays;
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"IsStarInteresting failed: {e.Message}");
            return false;
        }
    }
}

internal readonly record struct PoisonEffectSummary(
    int TimesApplied,
    decimal TotalAmountApplied,
    int TimesBlockedByArtifact,
    decimal TotalAmountBlockedByArtifact,
    decimal TotalTriggeredEffectiveDamage,
    decimal TotalTriggeredOverkill,
    string? IconPath);