using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models.Relics;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class SpikedGauntletsStatsTests
{
    private const string RelicId = "RELIC.SPIKED_GAUNTLETS";

    private static readonly MethodInfo BuildBodyMethod =
        typeof(RelicHoverShowPatch).GetMethod(
            "BuildSpikedGauntletsBodyBBCode",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "BuildSpikedGauntletsBodyBBCode not found.");

    [Fact]
    public void RelicAggregate_SpikedGauntletsFields_DefaultToZero()
    {
        var agg = new RelicAggregate();

        Assert.Equal(0, agg.SpikedGauntletsTaxedPowersPlayed);
        Assert.Equal(0m, agg.SpikedGauntletsPowerCostTotal);
        Assert.Equal(0, agg.SpikedGauntletsPowerEnergySpent);
        Assert.Equal(0, agg.SpikedGauntletsTurns);
    }

    [Fact]
    public void RelicAggregate_SpikedGauntletsFields_JsonRoundtrip()
    {
        var run = new RunData();
        run.RelicAggregates[RelicId] = PopulatedAggregate();

        var json = JsonSerializer.Serialize(run, RunStorage.Options);
        var restored = JsonSerializer.Deserialize<RunData>(json, RunStorage.Options);

        Assert.NotNull(restored);
        AssertAggregate(restored!.RelicAggregates[RelicId]);
    }

    [Fact]
    public void RunTracker_SpikedGauntletsHelpers_RecordCostsAndDenominators()
    {
        var agg = new RelicAggregate();

        RunTracker.RecordSpikedGauntletsPowerPlayedForTest(
            agg,
            powerCost: 3m,
            energySpent: 3);
        RunTracker.RecordSpikedGauntletsPowerPlayedForTest(
            agg,
            powerCost: 2m,
            energySpent: 0);
        RunTracker.RecordSpikedGauntletsPowerPlayedForTest(
            agg,
            powerCost: 3m,
            energySpent: 3);
        RunTracker.RecordSpikedGauntletsTurnForTest(agg, 4);
        RunTracker.RecordSpikedGauntletsCombatForTest(agg, 2);
        RunTracker.RecordEnergyResetRelicEnergyGeneratedForTest(
            agg,
            amount: 7,
            countCombat: false);

        AssertAggregate(agg);
    }

    [Fact]
    public void RelicAggregate_SpikedGauntletsFields_Merge()
    {
        var target = new RelicAggregate
        {
            EnergyGenerated = 3,
            EnergyGeneratedCombats = 1,
            SpikedGauntletsTaxedPowersPlayed = 1,
            SpikedGauntletsPowerCostTotal = 3m,
            SpikedGauntletsPowerEnergySpent = 2,
            SpikedGauntletsTurns = 1,
        };
        var source = new RelicAggregate
        {
            EnergyGenerated = 4,
            EnergyGeneratedCombats = 1,
            SpikedGauntletsTaxedPowersPlayed = 2,
            SpikedGauntletsPowerCostTotal = 5m,
            SpikedGauntletsPowerEnergySpent = 4,
            SpikedGauntletsTurns = 3,
        };

        RunTracker.MergeRelicAggregateInto(target, source);

        AssertAggregate(target);
    }

    [Fact]
    public void RelicTooltip_SpikedGauntlets_ShowsRequestedStats()
    {
        var relic = (SpikedGauntlets)RuntimeHelpers.GetUninitializedObject(
            typeof(SpikedGauntlets));
        var agg = PopulatedAggregate();

        var recognized = RelicHoverShowPatch.TryBuildBodyBBCode(
            relic,
            agg,
            floorCount: null,
            out var title,
            out var body);

        Assert.True(recognized);
        Assert.Equal("Spiked Gauntlets", title);
        Assert.Contains("Taxed played", body);
        Assert.Contains("Completed owner Power plays while Spiked Gauntlets", body);
        Assert.Contains("Average resolved Energy cost", body);
        Assert.Contains("Average Energy actually spent on Powers per player turn", body);
        Assert.Contains("Average Energy actually spent on Powers per combat", body);
        Assert.Contains("Energy gained total", body);
        Assert.Contains("Avg energy gained per combat", body);
        Assert.Contains("[b]2.67[/b]", body);
        Assert.Contains("[b]1.5[/b]", body);
        Assert.Contains("[b]3[/b]", body);
        Assert.Contains("[b]7/0[/b]", body);
        Assert.Contains("[b]3.5[/b]", body);
        Assert.Equal(BuildBody(agg), body);
    }

    [Fact]
    public void OlderShapeWithoutSpikedGauntletsFields_DefaultsToZero()
    {
        var agg = JsonSerializer.Deserialize<RelicAggregate>(
            "{}",
            RunStorage.Options);

        Assert.NotNull(agg);
        Assert.Equal(0, agg!.SpikedGauntletsTaxedPowersPlayed);
        Assert.Equal(0m, agg.SpikedGauntletsPowerCostTotal);
        Assert.Equal(0, agg.SpikedGauntletsPowerEnergySpent);
        Assert.Equal(0, agg.SpikedGauntletsTurns);
    }

    private static RelicAggregate PopulatedAggregate()
        => new()
        {
            EnergyGenerated = 7,
            EnergyGeneratedCombats = 2,
            SpikedGauntletsTaxedPowersPlayed = 3,
            SpikedGauntletsPowerCostTotal = 8m,
            SpikedGauntletsPowerEnergySpent = 6,
            SpikedGauntletsTurns = 4,
        };

    private static void AssertAggregate(RelicAggregate agg)
    {
        Assert.Equal(7, agg.EnergyGenerated);
        Assert.Equal(2, agg.EnergyGeneratedCombats);
        Assert.Equal(3, agg.SpikedGauntletsTaxedPowersPlayed);
        Assert.Equal(8m, agg.SpikedGauntletsPowerCostTotal);
        Assert.Equal(6, agg.SpikedGauntletsPowerEnergySpent);
        Assert.Equal(4, agg.SpikedGauntletsTurns);
    }

    private static string BuildBody(RelicAggregate agg)
        => (string)(BuildBodyMethod.Invoke(null, new object?[] { agg })
            ?? throw new InvalidOperationException(
                "BuildSpikedGauntletsBodyBBCode returned null."));
}
