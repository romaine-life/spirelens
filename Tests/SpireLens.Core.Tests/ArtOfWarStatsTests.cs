using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Relics;
using SpireLens.Core;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class ArtOfWarStatsTests
{
    private const string ArtOfWarRelicId = "RELIC.ART_OF_WAR";

    private static readonly MethodInfo BuildArtOfWarBodyMethod =
        typeof(RelicHoverShowPatch).GetMethod(
            "BuildArtOfWarBodyBBCode",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildArtOfWarBodyBBCode not found.");

    [Fact]
    public void Patch_TargetsArtOfWarAfterEnergyResetWithExpectedParameters()
    {
        var target = typeof(ArtOfWar).GetMethod(nameof(ArtOfWar.AfterEnergyReset));

        Assert.NotNull(target);
        Assert.Equal(
            new[] { typeof(Player) },
            target!.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void RelicAggregate_ArtOfWarFields_DefaultToZero()
    {
        var agg = new RelicAggregate();

        Assert.Equal(0, agg.EnergyGenerated);
        Assert.Equal(0, agg.ArtOfWarTurns);
        Assert.Equal(0, agg.EnergyGeneratedCombats);
        Assert.Equal(0, agg.ArtOfWarEnergyAddedThisCombat);
        Assert.Equal(0, agg.ArtOfWarEnergyAddedThisTurn);
        Assert.Equal(0, agg.ArtOfWarTurnsThisCombat);
    }

    [Fact]
    public void RelicAggregate_ArtOfWarFields_JsonRoundtripPreservesValues()
    {
        var run = new RunData();
        run.RelicAggregates[ArtOfWarRelicId] = PopulatedAggregate();

        var json = JsonSerializer.Serialize(run, RunStorage.Options);
        var restored = JsonSerializer.Deserialize<RunData>(json, RunStorage.Options);

        Assert.Contains("\"energy_generated\"", json);
        Assert.Contains("\"art_of_war_turns\"", json);
        Assert.Contains("\"energy_generated_combats\"", json);
        Assert.NotNull(restored);
        AssertPopulatedAggregate(restored!.RelicAggregates[ArtOfWarRelicId]);
    }

    [Fact]
    public void RunTracker_ArtOfWarHelpers_AccumulateEnergyAndHeldPeriods()
    {
        var agg = new RelicAggregate();

        RunTracker.RecordArtOfWarEnergyGainedForTest(agg, 1);
        RunTracker.RecordArtOfWarEnergyGainedForTest(agg, 3);
        RunTracker.RecordArtOfWarTurnForTest(agg, 8);
        RunTracker.RecordArtOfWarCombatForTest(agg, 2);

        AssertPopulatedAggregate(agg);
    }

    [Fact]
    public void RunTracker_ArtOfWarHelpers_IgnoreNegativeValuesAndCounts()
    {
        var agg = new RelicAggregate();

        RunTracker.RecordArtOfWarEnergyGainedForTest(agg, -1);
        RunTracker.RecordArtOfWarTurnForTest(agg, -2);
        RunTracker.RecordArtOfWarCombatForTest(agg, -3);

        Assert.Equal(0, agg.EnergyGenerated);
        Assert.Equal(0, agg.ArtOfWarTurns);
        Assert.Equal(0, agg.EnergyGeneratedCombats);
    }

    [Fact]
    public void RelicAggregate_ArtOfWarFields_Merge()
    {
        var target = PopulatedAggregate();

        RunTracker.MergeRelicAggregateInto(target, PopulatedAggregate());

        Assert.Equal(8, target.EnergyGenerated);
        Assert.Equal(16, target.ArtOfWarTurns);
        Assert.Equal(4, target.EnergyGeneratedCombats);
    }

    [Fact]
    public void RelicTooltip_ArtOfWar_ShowsTotalAndHeldPeriodAverages()
    {
        var agg = PopulatedAggregate();
        agg.ArtOfWarEnergyAddedThisCombat = 3;
        agg.ArtOfWarEnergyAddedThisTurn = 1;
        agg.ArtOfWarTurnsThisCombat = 4;
        agg.EnergyWasted = 1;

        var body = BuildBody(agg);

        // Generated and wasted share one row now, so the relic can never
        // report the first without the second.
        Assert.Contains("Total energy gained/wasted", body);
        Assert.Contains("[b]4/1[/b]", body);
        Assert.Contains("25%", body);
        Assert.Contains("Avg energy gained per turn", body);
        Assert.Contains("[b]0.5[/b]", body);
        Assert.Contains("Avg energy gained per combat", body);
        Assert.Contains("[b]2[/b]", body);
        Assert.Contains("Energy added this combat", body);
        Assert.Contains("[b]3[/b]", body);
        Assert.Contains("Energy added this turn", body);
        Assert.Contains("[b]1[/b]", body);
        Assert.Contains("Avg energy added per turn this combat", body);
        Assert.Contains("[b]0.75[/b]", body);
        Assert.Equal(1, CountOccurrences(body, "[table=4]"));
        Assert.Equal(0, CountOccurrences(body, "[table=2]"));
        Assert.Contains("align=center", body);
    }

    [Fact]
    public void RelicTooltip_ArtOfWar_ShowsZeroAveragesWithoutDenominators()
    {
        var body = BuildBody(new RelicAggregate { EnergyGenerated = 2 });

        // None of the 2 was wasted, and with every route into the pool owned
        // that zero is a measurement rather than an absence of one.
        Assert.Contains("Total energy gained/wasted", body);
        Assert.Contains("[b]2/0[/b]", body);
        Assert.Contains("Avg energy gained per turn", body);
        Assert.Contains("Avg energy gained per combat", body);
        Assert.Contains("Energy added this combat", body);
        Assert.Contains("Energy added this turn", body);
        Assert.Contains("Avg energy added per turn this combat", body);
        Assert.Equal(5, CountOccurrences(body, "[b]0[/b]"));
    }

    [Fact]
    [Trait("Category", "RequiresLiveGame")]
    public void RelicTooltip_ArtOfWar_DispatchesForModel()
    {
        var relic = (ArtOfWar)RuntimeHelpers.GetUninitializedObject(typeof(ArtOfWar));

        var recognized = RelicHoverShowPatch.TryBuildBodyBBCode(
            relic,
            new RelicAggregate(),
            floorCount: null,
            out var title,
            out var body);

        Assert.True(recognized);
        Assert.Equal("Art of War", title);
        Assert.Contains("Total energy gained", body);
    }

    [Fact]
    public void RelicAggregate_OlderShapeWithoutArtOfWarFields_DefaultsToZero()
    {
        var agg = JsonSerializer.Deserialize<RelicAggregate>("{}", RunStorage.Options);

        Assert.NotNull(agg);
        Assert.Equal(0, agg!.EnergyGenerated);
        Assert.Equal(0, agg.ArtOfWarTurns);
        Assert.Equal(0, agg.EnergyGeneratedCombats);
    }

    private static RelicAggregate PopulatedAggregate()
    {
        var agg = new RelicAggregate();
        RunTracker.RecordArtOfWarEnergyGainedForTest(agg, 1);
        RunTracker.RecordArtOfWarEnergyGainedForTest(agg, 3);
        RunTracker.RecordArtOfWarTurnForTest(agg, 8);
        RunTracker.RecordArtOfWarCombatForTest(agg, 2);
        return agg;
    }

    private static void AssertPopulatedAggregate(RelicAggregate agg)
    {
        Assert.Equal(4, agg.EnergyGenerated);
        Assert.Equal(8, agg.ArtOfWarTurns);
        Assert.Equal(2, agg.EnergyGeneratedCombats);
    }

    private static string BuildBody(RelicAggregate agg)
        => (string)(BuildArtOfWarBodyMethod.Invoke(null, new object?[] { agg })
            ?? throw new InvalidOperationException("BuildArtOfWarBodyBBCode returned null."));

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
