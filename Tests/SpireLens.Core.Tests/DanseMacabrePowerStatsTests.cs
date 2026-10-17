using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.ValueProps;
using SpireLens.Core;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class DanseMacabrePowerStatsTests
{
    private const string DanseMacabrePowerId = "POWER.DANSE_MACABRE";

    // #309 folded the per-power appenders into two shared renderers: the
    // canonical full view a shared meta-power record shows, and the compact
    // summary a physical copy of that Power card shows.
    private static readonly MethodInfo AppendCanonicalMetaPowerStatsMethod =
        typeof(CardHoverShowPatch).GetMethod(
            "AppendCanonicalMetaPowerStats",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "AppendCanonicalMetaPowerStats not found.");

    private static readonly MethodInfo AppendMetaPowerLifetimeStatsMethod =
        typeof(CardHoverShowPatch).GetMethod(
            "AppendMetaPowerLifetimeStats",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "AppendMetaPowerLifetimeStats not found.");

    [Fact]
    public void RunMetaStats_DanseMacabrePowerAggregate_DefaultsSafely()
    {
        var agg = new PowerAggregate();

        Assert.Equal(0, agg.TimesTriggered);
        Assert.Equal(0m, agg.BlockGained);
        Assert.Equal(0, agg.TurnsActive);
        Assert.Equal(0, agg.CombatsActive);
    }

    [Fact]
    public void RunTracker_Promotion_MergesDanseMacabrePowerAggregates()
    {
        var run = new RunData();
        run.MetaStats.PowerAggregates[DanseMacabrePowerId] = new PowerAggregate
        {
            PowerId = DanseMacabrePowerId,
            DisplayName = "Danse Macabre",
            TimesTriggered = 4,
            BlockGained = 20m,
            TurnsActive = 2,
            CombatsActive = 1,
        };
        var pending = new PendingCombat();
        pending.MetaStats.PowerAggregates[DanseMacabrePowerId] =
            new PowerAggregate
            {
                PowerId = DanseMacabrePowerId,
                DisplayName = "Danse Macabre",
                TimesTriggered = 5,
                BlockGained = 25m,
                TurnsActive = 4,
                CombatsActive = 2,
            };

        RunTracker.PromotePendingCombatIntoRun(pending, run);

        AssertRepresentativeAggregate(
            run.MetaStats.PowerAggregates[DanseMacabrePowerId]);
    }

    [Fact]
    public void RunMetaStats_DanseMacabrePowerAggregate_JsonRoundtripPreservesFields()
    {
        var run = new RunData();
        run.MetaStats.PowerAggregates[DanseMacabrePowerId] =
            CreateRepresentativeAggregate();

        var json = JsonSerializer.Serialize(run, RunStorage.Options);
        var restored = JsonSerializer.Deserialize<RunData>(
            json,
            RunStorage.Options);

        Assert.Contains("\"times_triggered\"", json);
        Assert.Contains("\"block_gained\"", json);
        Assert.NotNull(restored);
        AssertRepresentativeAggregate(
            restored!.MetaStats.PowerAggregates[DanseMacabrePowerId]);
    }

    [Fact]
    public void DanseMacabreTooltip_FullViewShowsSharedPowerTotalsAndAverages()
    {
        var body = AppendDanseMacabrePowerStats(
            CreateRepresentativeAggregate(),
            compact: false);

        Assert.Contains("Times triggered", body);
        Assert.Contains("Avg triggers / active turn", body);
        Assert.Contains("[b]1.5[/b]", body);
        Assert.Contains("Avg triggers / active application-turn", body);
        Assert.Contains("[b]3[/b]", body);
        Assert.Contains("Block gained", body);
        Assert.Contains("[b]45[/b]", body);
        Assert.Contains("Avg block gained / active turn", body);
        Assert.Contains("[b]7.5[/b]", body);
        Assert.Contains("Avg block gained / active application-turn", body);
        Assert.Contains("[b]15[/b]", body);
    }

    [Fact]
    public void DanseMacabreTooltip_CompactViewKeepsOnlyTotals()
    {
        var body = AppendDanseMacabrePowerStats(
            CreateRepresentativeAggregate(),
            compact: true);

        Assert.Contains("Times triggered", body);
        Assert.Contains("Block gained", body);
        Assert.DoesNotContain("Avg triggers", body);
        Assert.DoesNotContain("Avg block", body);
    }

    private static PowerAggregate CreateRepresentativeAggregate() =>
        new()
        {
            PowerId = DanseMacabrePowerId,
            DisplayName = "Danse Macabre",
            TimesTriggered = 9,
            BlockGained = 45m,
            TurnsActive = 6,
            CombatsActive = 3,
            // #309 moved the averages onto the shared Meta* turn
            // counters. Mirror the legacy fixture values onto them so the
            // expected averages below still mean what they meant before.
            MetaActiveTurns = 6,
            RateTimesTriggered = 9,
            RateBlockGained = 45m,
            MetaActiveApplicationTurns = 3,
        };

    private static void AssertRepresentativeAggregate(PowerAggregate agg)
    {
        Assert.Equal(DanseMacabrePowerId, agg.PowerId);
        Assert.Equal("Danse Macabre", agg.DisplayName);
        Assert.Equal(9, agg.TimesTriggered);
        Assert.Equal(45m, agg.BlockGained);
        Assert.Equal(6, agg.TurnsActive);
        Assert.Equal(3, agg.CombatsActive);
    }

    private static string AppendDanseMacabrePowerStats(
        PowerAggregate agg,
        bool compact)
    {
        // Resolve through the registry rather than naming the id here:
        // ids come from the game's types, so a hand-written constant
        // silently stops matching when a type is renamed.
        var definition = MetaPowerRegistry.All.Single(
            candidate => candidate.DisplayName == "Danse Macabre");
        var sb = new StringBuilder();
        var card = (DanseMacabre)RuntimeHelpers.GetUninitializedObject(
            typeof(DanseMacabre));
        var metaStats = new RunMetaStats();
        metaStats.PowerAggregates[definition.PowerId] = agg;
        if (compact)
        {
            // Compact rows, as the physical Power card shows them. Resolving a
            // card to its definition needs the game's model db, so that step is
            // covered by MetaPowerRegistry's tests rather than faked here.
            _ = AppendMetaPowerLifetimeStatsMethod.Invoke(
                null,
                [sb, definition, agg, metaStats, false]);
        }
        else
        {
            // The shared meta-power record shows the canonical full view.
            _ = AppendCanonicalMetaPowerStatsMethod.Invoke(
                null,
                [sb, definition, metaStats]);
        }
        return sb.ToString();
    }
}
