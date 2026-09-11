using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using SpireLens.Core.Patches;

namespace SpireLens.Core;

/// <summary>
/// One SpireLens-tracked number the deck view can be ordered by. Adding a new
/// entry to <see cref="DeckViewSpireLensSort.Metrics"/> is the whole cost of
/// adding a new option to the menu — the ordering, the menu rows, and the
/// handoff back to the game's own sorters are all metric-agnostic.
/// </summary>
internal sealed class DeckSortMetric
{
    internal DeckSortMetric(
        string id,
        string label,
        string group,
        Func<CardAggregate, double> select,
        Func<CardAggregate, double>? tieBreak = null,
        Func<CardAggregate, string>? display = null)
    {
        Id = id;
        Label = label;
        Group = group;
        Select = select;
        TieBreak = tieBreak;
        Display = display;
    }

    internal string Id { get; }

    internal string Label { get; }

    /// <summary>Section this metric is listed under in the sort menu.</summary>
    internal string Group { get; }

    /// <summary>
    /// Value to order by. Untracked cards score 0 and trail. Double rather
    /// than integer so per-play averages are orderable at full precision.
    /// </summary>
    internal Func<CardAggregate, double> Select { get; }

    /// <summary>
    /// Second sort key, for metrics whose primary can legitimately tie across
    /// many cards. Damage per energy uses it: every card that never spent
    /// energy scores the same unrankable "infinity", so they are ordered
    /// against each other by total damage instead of by whatever order the
    /// game happened to hand us.
    /// </summary>
    internal Func<CardAggregate, double>? TieBreak { get; }

    /// <summary>
    /// Overrides the printed value when the number alone would mislead.
    /// </summary>
    internal Func<CardAggregate, string>? Display { get; }
}

/// <summary>
/// Orders the in-run deck view by a SpireLens attribution stat instead of one
/// of the game's four built-in sorts.
///
/// How it reaches the grid: <c>NCardGrid.SetCards</c> only runs its own
/// comparator when <c>sortingPriority[0]</c> is something other than
/// <c>Ascending</c> / <c>Descending</c> — with <c>Ascending</c> at the head it
/// renders the supplied list verbatim. So a SpireLens ordering is just
/// "reorder <c>_cards</c>, then pin <c>Ascending</c>", applied from the same
/// <c>DisplayCards</c> prefix that already decides which cards to show
/// (see <see cref="DeckViewNotInDeckPatch"/>). No new Harmony target, and no
/// patch on the shared card grid.
///
/// Handing ordering back: the four stock sorters mutate <c>_sortingPriority</c>
/// and their own direction flag. <c>OnObtainedSort</c>'s ascending result is
/// byte-identical to the head we pin, so the priority list cannot tell us a
/// stock sorter ran — the direction flags can, and they flip on release for
/// mouse and controller alike. We snapshot all four each time we order, and
/// stand down as soon as one of them moves.
///
/// State is deliberately session-only (no loader-backed pref): persisting it
/// would need a new Loader bridge method, which a Core hot reload cannot add,
/// and a view ordering is not worth a game restart.
/// </summary>
internal static class DeckViewSpireLensSort
{
    internal const string GroupDamage = "Damage";
    internal const string GroupBlock = "Block";
    internal const string GroupCost = "Cost";
    internal const string GroupFlow = "Flow";

    /// <summary>Section order in the menu.</summary>
    internal static readonly IReadOnlyList<string> Groups =
        new[] { GroupDamage, GroupBlock, GroupCost, GroupFlow };

    /// <summary>
    /// Every metric mirrors a row the card tooltip already prints, so a number
    /// you sorted by and a number you hovered to read never disagree.
    /// </summary>
    internal static readonly IReadOnlyList<DeckSortMetric> Metrics = new[]
    {
        // "Total damage" is exactly the tooltip row of that name: effective
        // damage, i.e. HP this physical card actually removed across the run.
        // Block and overkill waste are excluded, which is what players mean by
        // "this card has done X damage".
        new DeckSortMetric("total_damage", "Total damage", GroupDamage,
            agg => agg.TotalEffective),
        // Total damage rewards whatever you drew most. This is the tooltip's
        // "Avg effective", which separates a consistent workhorse from a card
        // that only looks big because it came up a lot.
        new DeckSortMetric("avg_damage", "Avg damage per play", GroupDamage,
            agg => agg.Plays > 0 ? (double)agg.TotalEffective / agg.Plays : 0d),
        // Damage bought per energy paid, using energy actually spent rather
        // than printed cost, so discounts and cost modifiers count.
        //
        // Cards that never spent energy cannot be divided at all, so they are
        // not given a fake ratio: they sort above every card that did pay,
        // ranked against each other by total damage, and print their damage
        // over a zero-cost orb rather than a number that would read as a
        // ratio. "Never spent energy" is a claim about this run, not about the
        // card's printed cost — a card played only while discounted lands here
        // too, which is the more interesting fact.
        new DeckSortMetric("damage_per_energy", "Damage per energy", GroupDamage,
            agg => agg.TotalEffective <= 0
                // No damage means nothing to rate, and must NOT read as
                // infinite efficiency — otherwise every card that was never
                // played would head the ranking.
                ? 0d
                : agg.TotalEnergySpent > 0
                    ? (double)agg.TotalEffective / agg.TotalEnergySpent
                    : double.PositiveInfinity,
            tieBreak: agg => agg.TotalEffective,
            display: FormatDamagePerEnergy),
        // Share of everything this run's cards have dealt. Ordering matches
        // Total damage, since the denominator is the same for every card —
        // the value is in reading how much of the run one card accounted for.
        new DeckSortMetric("damage_share", "Damage share", GroupDamage,
            agg => agg.TotalEffective,
            display: FormatDamageShare),
        new DeckSortMetric("kills", "Kills", GroupDamage,
            agg => agg.Kills),

        // Block generated, and the part of it that actually ate damage. The
        // gap between the two is block you paid for and never used.
        new DeckSortMetric("block_gained", "Block gained", GroupBlock,
            agg => agg.TotalBlockGained),
        new DeckSortMetric("block_absorbed", "Block absorbed", GroupBlock,
            agg => agg.TotalBlockEffective),

        // What the card charged you: energy actually paid (not printed cost),
        // and HP spent on itself.
        new DeckSortMetric("energy_spent", "Energy spent", GroupCost,
            agg => agg.TotalEnergySpent),
        new DeckSortMetric("hp_lost", "HP lost", GroupCost,
            agg => agg.TotalHpLost),
        // The energy this card handed you that then expired unspent. Sorting
        // by it finds the ramp cards you keep playing on turns you had
        // nothing left to spend the energy on.
        new DeckSortMetric("energy_wasted", "Energy wasted", GroupCost,
            agg => agg.TotalEnergyWasted),

        new DeckSortMetric("times_played", "Times played", GroupFlow,
            agg => agg.Plays),
        new DeckSortMetric("times_drawn", "Times drawn", GroupFlow,
            agg => agg.TimesDrawn),
        // Clamped at zero: a card can be played without being drawn (summoned
        // straight to hand, played off the draw pile), which would otherwise
        // score negative. Unplayable curses and statuses top this list by
        // construction, since their Plays is always 0 — that is precisely the
        // dead weight the metric exists to surface, so they are not excluded.
        new DeckSortMetric("drawn_not_played", "Drawn, not played", GroupFlow,
            agg => Math.Max(0, agg.TimesDrawn - agg.Plays)),
        // Cards this card CAUSED to be drawn — distinct from "Times drawn",
        // which is how often the card itself reached hand.
        new DeckSortMetric("cards_drawn", "Cards drawn", GroupFlow,
            agg => agg.TimesCardsDrawn),
    };

    internal static DeckSortMetric? ActiveMetric { get; private set; }

    /// <summary>Highest first. The default for every metric we offer.</summary>
    internal static bool Descending { get; private set; } = true;

    private static NDeckViewScreen? _snapshotScreen;
    private static bool[]? _snapshotDirections;

    internal static bool IsActive(DeckSortMetric metric)
        => ReferenceEquals(metric, ActiveMetric);

    /// <summary>
    /// Choose a metric. Choosing the one already active flips the direction,
    /// mirroring how the game's own sorters reverse on a second click.
    /// </summary>
    internal static void Select(DeckSortMetric metric, string source)
    {
        if (IsActive(metric))
        {
            Descending = !Descending;
        }
        else
        {
            ActiveMetric = metric;
            Descending = true;
        }

        ForgetSnapshot();
        CoreMain.Logger.Info(
            $"Deck sort set to {metric.Id} {(Descending ? "descending" : "ascending")} ({source})");
        DeckViewSortMenu.RefreshButtonText();
        DeckViewSortMenu.RefreshDeckView();
    }

    /// <summary>Stand down and let the game's own sorting govern again.</summary>
    internal static void Clear(string source)
    {
        if (ActiveMetric == null) return;

        ActiveMetric = null;
        ForgetSnapshot();
        CoreMain.Logger.Info($"Deck sort cleared ({source})");
        DeckViewSortMenu.RefreshButtonText();
        DeckViewSortMenu.RefreshSortCaption();
    }

    /// <summary>
    /// Reorder the screen's pending card list. Called at the end of the
    /// <c>DisplayCards</c> prefix, after the not-in-deck view has decided
    /// which collection is on screen, so it orders whatever is actually shown.
    /// </summary>
    internal static void Apply(NDeckViewScreen screen)
    {
        try
        {
            var metric = ActiveMetric;
            if (metric == null) return;

            if (StockSorterMoved(screen))
            {
                // The player reached for one of the game's sorters. Clearing
                // before we reorder means this very render already honours
                // their choice — no stale frame, no second DisplayCards pass.
                Clear("stock sorter used");
                return;
            }

            // The run-history viewer reuses this screen type over a rebuilt
            // historical deck, so its numbers must come from the archived run
            // rather than from whatever run is live now.
            var historical = RunHistoryDeckViewer.IsHistoricalDeckViewer(screen);
            RefreshDamageShareTotal(historical);

            var cards = screen._cards;
            if (cards != null && cards.Count > 0)
            {
                var scored = cards
                    .Select(card => (Card: card, Value: ValueFor(card, metric, historical)))
                    .ToList();

                // Show only the cards this metric actually says something
                // about. Sorting by damage across a whole deck buries the five
                // cards you care about under twenty Skills tied at zero; for
                // Total damage this leaves the attacks (plus anything that
                // genuinely dealt damage, like a Skill's poison).
                //
                // Unless nothing qualifies — an empty deck view reads as a
                // broken screen, so a run with no data for this metric keeps
                // showing the whole deck.
                var qualifying = scored.Where(entry => entry.Value > 0d).ToList();
                if (qualifying.Count > 0) scored = qualifying;

                // OrderBy is stable, so equal values keep the game's order.
                var ordered = Descending
                    ? scored.OrderByDescending(entry => entry.Value)
                    : scored.OrderBy(entry => entry.Value);
                if (metric.TieBreak != null)
                {
                    ordered = Descending
                        ? ordered.ThenByDescending(entry => TieBreakFor(entry.Card, metric, historical))
                        : ordered.ThenByDescending(entry => TieBreakFor(entry.Card, metric, historical));
                }

                screen._cards = ordered.Select(entry => entry.Card).ToList();
            }

            PinUnsortedHead(screen);
            TakeSnapshot(screen);
            DeckViewSortMenu.RefreshSortCaption();

            CoreMain.LogDebug(
                $"DeckViewSpireLensSort: ordered {screen._cards?.Count ?? 0} cards by " +
                $"{metric.Id} {(Descending ? "desc" : "asc")}");
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"DeckViewSpireLensSort.Apply failed: {e.Message}");
        }
    }

    /// <summary>
    /// Caption for one card in the deck grid: the metric's full name and this
    /// card's value, e.g. "Total damage: 432". The name is repeated on every
    /// card on purpose — a screenshot or a stream frame carries no memory of
    /// which sort was chosen, so a bare number would be unreadable out of
    /// context.
    ///
    /// False when no SpireLens sort is active, or when the card scored zero:
    /// on a deck sorted by damage that is every Skill, Power and Curse, and
    /// captioning two dozen cards with "0" buries the ones that did work.
    /// </summary>
    internal static bool TryGetCaption(CardModel card, out string caption)
    {
        caption = string.Empty;

        var metric = ActiveMetric;
        if (metric == null) return false;

        var value = ValueFor(card, metric, IsHistoricalCard(card));
        if (value <= 0d) return false;

        var aggregate = ResolveAggregate(card, IsHistoricalCard(card));
        caption = metric.Display != null && aggregate != null
            ? $"{metric.Label}: {metric.Display(aggregate)}"
            : $"{metric.Label}: {FormatValue(value)}";
        return true;
    }

    /// <summary>
    /// Per-play averages need a decimal; counts must not show one. Deciding on
    /// the value rather than on a per-metric flag keeps whole-numbered averages
    /// reading as "12" instead of "12.0".
    /// </summary>
    private static string FormatValue(double value)
        => Math.Abs(value - Math.Round(value)) < 0.05d
            ? value.ToString("F0", CultureInfo.InvariantCulture)
            : value.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>
    /// A card belongs to the archived run exactly when the run-history viewer
    /// is open and holds a key for it. That map is cleared when the viewer
    /// closes, and its cards are freshly built CardModels that can never
    /// collide with the live deck's, so this needs no screen reference.
    /// </summary>
    private static bool IsHistoricalCard(CardModel card)
        => RunHistoryStatsContext.TryGetHistoricalDeckAggregate(card, out _);

    /// <summary>
    /// "12.4" when energy was paid; "593 / 0<orb>" when none ever was. The orb
    /// is the game's own character-coloured energy icon, the same one its card
    /// text uses, so the line reads as card text rather than as a mod string.
    /// </summary>
    private static double TieBreakFor(CardModel card, DeckSortMetric metric, bool historical)
    {
        var aggregate = ResolveAggregate(card, historical);
        return aggregate == null || metric.TieBreak == null ? 0d : metric.TieBreak(aggregate);
    }

    /// <summary>
    /// "18% (589 / 3271)" — the share, then the whole ratio it came from, so
    /// the percentage can be checked rather than taken on trust.
    ///
    /// The denominator is refreshed each time the deck is re-rendered, from
    /// the run actually on screen, and for the live run it includes the
    /// current combat's buffered damage rather than stopping at the last room
    /// boundary. It does not tick during a combat while the screen sits open.
    /// </summary>
    private static string FormatDamageShare(CardAggregate agg)
    {
        var total = _damageShareTotal;
        if (total <= 0) return $"{agg.TotalEffective}";

        var percent = 100d * agg.TotalEffective / total;
        // Break before the ratio rather than letting it wrap. Left to itself
        // the line runs past the card's width on nearly every card and breaks
        // mid-parenthetical, so "(589 /" ends one line and "4771)" starts the
        // next. An explicit break puts the share on one line and the ratio it
        // came from on the next, both centred with the rest of the card text.
        return $"{FormatValue(percent)}%\n{agg.TotalEffective} / {total}";
    }

    private static long _damageShareTotal;

    /// <summary>
    /// Refresh the damage-share denominator for the run currently on screen.
    /// The archived viewer must not be measured against the live run's total.
    /// </summary>
    private static void RefreshDamageShareTotal(bool historical)
    {
        try
        {
            if (!historical)
            {
                _damageShareTotal = RunTracker.GetTotalEffectiveCardDamage();
                return;
            }

            var run = RunHistoryStatsContext.GetCurrentRunData();
            _damageShareTotal = run == null
                ? 0
                : run.Aggregates.Values.Sum(aggregate => (long)aggregate.TotalEffective);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Warn($"Damage-share total failed: {e.Message}");
            _damageShareTotal = 0;
        }
    }

    private static string FormatDamagePerEnergy(CardAggregate agg)
        => agg.TotalEnergySpent > 0
            ? FormatValue((double)agg.TotalEffective / agg.TotalEnergySpent)
            : $"{agg.TotalEffective} / 0{StatEnergyIcon.RenderInline(EnergyIconSize)}";

    private const int EnergyIconSize = 20;

    internal static void Reset()
    {
        ActiveMetric = null;
        Descending = true;
        ForgetSnapshot();
    }

    internal static CardAggregate? ResolveAggregate(CardModel card, bool historical)
    {
        if (historical)
        {
            RunHistoryStatsContext.TryGetHistoricalDeckAggregate(card, out var archived);
            return archived;
        }

        return RunTracker.GetEffectiveAggregate(card);
    }

    private static double ValueFor(CardModel card, DeckSortMetric metric, bool historical)
    {
        try
        {
            var aggregate = ResolveAggregate(card, historical);
            return aggregate == null ? 0d : metric.Select(aggregate);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Warn($"DeckViewSpireLensSort: value lookup failed: {e.Message}");
            return 0d;
        }
    }

    private static void PinUnsortedHead(NDeckViewScreen screen)
    {
        var priority = screen._sortingPriority;
        if (priority == null) return;

        priority.Remove(SortingOrders.Ascending);
        priority.Remove(SortingOrders.Descending);
        priority.Insert(0, SortingOrders.Ascending);
    }

    private static bool StockSorterMoved(NDeckViewScreen screen)
    {
        // A freshly opened screen has nothing to compare against: its sorters
        // start at their scene defaults and the player has not touched them.
        if (!ReferenceEquals(screen, _snapshotScreen) || _snapshotDirections == null)
            return false;

        var current = ReadDirections(screen);
        for (var i = 0; i < current.Length; i++)
        {
            if (current[i] != _snapshotDirections[i]) return true;
        }

        return false;
    }

    private static void TakeSnapshot(NDeckViewScreen screen)
    {
        _snapshotScreen = screen;
        _snapshotDirections = ReadDirections(screen);
    }

    private static void ForgetSnapshot()
    {
        _snapshotScreen = null;
        _snapshotDirections = null;
    }

    private static bool[] ReadDirections(NDeckViewScreen screen) =>
    [
        ReadDirection(screen._obtainedSorter),
        ReadDirection(screen._typeSorter),
        ReadDirection(screen._costSorter),
        ReadDirection(screen._alphabetSorter),
    ];

    // Read only: the IsDescending SETTER re-renders the sorter's arrow icon.
    private static bool ReadDirection(NCardViewSortButton? sorter)
        => sorter != null && GodotObject.IsInstanceValid(sorter) && sorter.IsDescending;
}
