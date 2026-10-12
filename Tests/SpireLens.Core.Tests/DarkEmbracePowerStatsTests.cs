using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class DarkEmbracePowerStatsTests
{
    private const string DarkEmbracePowerId = "POWER.DARK_EMBRACE";

    // #309 folded the per-power appenders into two shared renderers: the
    // canonical full view a shared meta-power record shows, and the compact
    // summary a physical copy of that Power card shows.
    private static readonly MethodInfo AppendCanonicalMetaPowerStatsMethod =
        typeof(CardHoverShowPatch).GetMethod(
            "AppendCanonicalMetaPowerStats",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "AppendCanonicalMetaPowerStats not found.");

    private static readonly MethodInfo AppendPhysicalMetaPowerSummaryMethod =
        typeof(CardHoverShowPatch).GetMethod(
            "AppendPhysicalMetaPowerSummary",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "AppendPhysicalMetaPowerSummary not found.");

    [Fact]
    public void PowerAggregate_DarkEmbraceFields_DefaultAndSerialize()
    {
        var empty = new PowerAggregate();
        Assert.Equal(0, empty.DarkEmbraceCardsDrawn);
        Assert.Equal(0, empty.DarkEmbraceCombatTurns);

        var run = new RunData();
        run.MetaStats.PowerAggregates[DarkEmbracePowerId] =
            CreateAggregate(cards: 18, activeTurns: 6, combatTurns: 9, combats: 3);

        var json = JsonSerializer.Serialize(run, RunStorage.Options);
        var restored = JsonSerializer.Deserialize<RunData>(
            json,
            RunStorage.Options);

        Assert.Contains("\"dark_embrace_cards_drawn\"", json);
        Assert.Contains("\"dark_embrace_combat_turns\"", json);
        Assert.NotNull(restored);
        var agg = restored!.MetaStats.PowerAggregates[DarkEmbracePowerId];
        Assert.Equal(18, agg.DarkEmbraceCardsDrawn);
        Assert.Equal(9, agg.DarkEmbraceCombatTurns);
    }

    [Fact]
    public void Promotion_MergesDarkEmbraceNumeratorsAndDenominators()
    {
        var run = new RunData();
        run.MetaStats.PowerAggregates[DarkEmbracePowerId] =
            CreateAggregate(cards: 7, activeTurns: 2, combatTurns: 4, combats: 1);
        var pending = new PendingCombat();
        pending.MetaStats.PowerAggregates[DarkEmbracePowerId] =
            CreateAggregate(cards: 11, activeTurns: 4, combatTurns: 5, combats: 2);

        RunTracker.PromotePendingCombatIntoRun(pending, run);

        var agg = run.MetaStats.PowerAggregates[DarkEmbracePowerId];
        Assert.Equal(18, agg.DarkEmbraceCardsDrawn);
        Assert.Equal(6, agg.TurnsActive);
        Assert.Equal(9, agg.DarkEmbraceCombatTurns);
        Assert.Equal(3, agg.CombatsActive);
    }

    [Fact]
    public void DarkEmbraceTooltip_UsesDistinctTurnDenominators()
    {
        // Resolve through the registry rather than naming the id here:
        // ids come from the game's types, so a hand-written constant
        // silently stops matching when a type is renamed.
        var definition = MetaPowerRegistry.All.Single(
            candidate => candidate.DisplayName == "Dark Embrace");
        var sb = new StringBuilder();
        var metaStats = new RunMetaStats();
        metaStats.PowerAggregates[definition.PowerId] =
            CreateAggregate(cards: 18, activeTurns: 6, combatTurns: 9, combats: 3);

        _ = AppendCanonicalMetaPowerStatsMethod.Invoke(
            null,
            [sb, definition, metaStats]);

        var body = sb.ToString();
        Assert.Contains("cards drawn", body);
        Assert.Contains("Avg cards drawn / active turn", body);
        Assert.Contains("Avg cards drawn / turn", body);
        Assert.Contains("Avg cards drawn / active application-turn", body);
        Assert.Contains("[b]3[/b]", body);
        Assert.Contains("[b]2[/b]", body);
        Assert.Contains("[b]6[/b]", body);
    }

    private static PowerAggregate CreateAggregate(
        int cards,
        int activeTurns,
        int combatTurns,
        int combats) =>
        new()
        {
            PowerId = DarkEmbracePowerId,
            DisplayName = "Dark Embrace",
            DarkEmbraceCardsDrawn = cards,
            TurnsActive = activeTurns,
            DarkEmbraceCombatTurns = combatTurns,
            CombatsActive = combats,
            // #309 moved the averages onto the shared Meta* turn
            // counters. Mirror the legacy fixture values onto them so the
            // expected averages below still mean what they meant before.
            MetaDeckTurns = combatTurns,
            RateDarkEmbraceCardsDrawn = cards,
            MetaActiveTurns = activeTurns,
            MetaActiveApplicationTurns = combats,
        };
}
