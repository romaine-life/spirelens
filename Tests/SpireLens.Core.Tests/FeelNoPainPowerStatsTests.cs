using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using SpireLens.Core;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class FeelNoPainPowerStatsTests
{
    private const string FeelNoPainPowerId = "POWER.FEEL_NO_PAIN";

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
    public void Promotion_MergesFeelNoPainNumeratorAndDenominator()
    {
        var run = new RunData();
        run.MetaStats.PowerAggregates[FeelNoPainPowerId] =
            CreateAggregate(block: 12m, turns: 2);
        var pending = new PendingCombat();
        pending.MetaStats.PowerAggregates[FeelNoPainPowerId] =
            CreateAggregate(block: 24m, turns: 4);

        RunTracker.PromotePendingCombatIntoRun(pending, run);

        var agg = run.MetaStats.PowerAggregates[FeelNoPainPowerId];
        Assert.Equal(36m, agg.BlockGained);
        Assert.Equal(6, agg.TurnsActive);
    }

    [Fact]
    public void FeelNoPainTooltip_ShowsBlockPerActiveTurn()
    {
        // Resolve through the registry rather than naming the id here:
        // ids come from the game's types, so a hand-written constant
        // silently stops matching when a type is renamed.
        var definition = MetaPowerRegistry.All.Single(
            candidate => candidate.DisplayName == "Feel No Pain");
        var sb = new StringBuilder();
        var metaStats = new RunMetaStats();
        metaStats.PowerAggregates[definition.PowerId] =
            CreateAggregate(block: 36m, turns: 6);

        _ = AppendCanonicalMetaPowerStatsMethod.Invoke(
            null,
            [sb, definition, metaStats]);

        var body = sb.ToString();
        Assert.Contains("Avg block gained / active turn", body);
        Assert.Contains("[b]6[/b]", body);
    }

    [Fact]
    public void FeelNoPain_IsTrackedByEffectFrames()
    {
        Assert.Contains("FeelNoPainPower", Effects.EffectRegistry.PowerClassNames);
    }

    [Fact]
    public void FeelNoPainTooltip_AddsEffectFrameBlockToFrozenLegacyTotal()
    {
        var definition = MetaPowerRegistry.All.Single(
            candidate => candidate.DisplayName == "Feel No Pain");
        var sb = new StringBuilder();
        var metaStats = new RunMetaStats();
        var aggregate = CreateAggregate(block: 12m, turns: 6);
        aggregate.Outcomes = new() { [Effects.EffectOutcomeKeys.Block] = 24m };
        metaStats.PowerAggregates[definition.PowerId] = aggregate;

        _ = AppendCanonicalMetaPowerStatsMethod.Invoke(
            null,
            [sb, definition, metaStats]);

        var body = sb.ToString();
        Assert.Contains("[b]36[/b]", body);
        Assert.Contains("Avg block gained / active turn", body);
        Assert.Contains("[b]6[/b]", body);
    }

    private static PowerAggregate CreateAggregate(decimal block, int turns) =>
        new()
        {
            PowerId = FeelNoPainPowerId,
            DisplayName = "Feel No Pain",
            BlockGained = block,
            TurnsActive = turns,
            // #309 moved the averages onto the shared Meta* turn
            // counters. Mirror the legacy fixture values onto them so the
            // expected averages below still mean what they meant before.
            MetaActiveTurns = turns,
            RateBlockGained = block,
        };
}
