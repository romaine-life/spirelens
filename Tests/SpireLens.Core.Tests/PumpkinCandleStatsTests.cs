using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models.Relics;
using SpireLens.Core;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class PumpkinCandleStatsTests
{
    private const string PumpkinCandleRelicId = "RELIC.PUMPKIN_CANDLE";

    private static readonly MethodInfo BuildBodyMethod =
        typeof(RelicHoverShowPatch).GetMethod(
            "BuildPumpkinCandleBodyBBCode",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "BuildPumpkinCandleBodyBBCode not found.");

    [Fact]
    public void RelicAggregate_PumpkinCandleFields_JsonRoundtripPreservesFields()
    {
        var run = new RunData();
        run.RelicAggregates[PumpkinCandleRelicId] = new RelicAggregate
        {
            EnergyGenerated = 14,
            PumpkinCandleCombatStartChargeTotal = 12,
            PumpkinCandleCombatStartChargeSamples = 4,
            PumpkinCandleRekindles = 2,
        };

        var json = JsonSerializer.Serialize(run, RunStorage.Options);
        var restored = JsonSerializer.Deserialize<RunData>(json, RunStorage.Options);

        Assert.NotNull(restored);
        var agg = restored!.RelicAggregates[PumpkinCandleRelicId];
        Assert.Equal(14, agg.EnergyGenerated);
        Assert.Equal(12, agg.PumpkinCandleCombatStartChargeTotal);
        Assert.Equal(4, agg.PumpkinCandleCombatStartChargeSamples);
        Assert.Equal(2, agg.PumpkinCandleRekindles);
    }

    [Fact]
    public void RunTracker_PumpkinCandleHelpers_RecordZeroInclusiveStartsAndRekindles()
    {
        var agg = new RelicAggregate();

        RunTracker.RecordPumpkinCandleCombatStartForTest(agg, 5);
        RunTracker.RecordPumpkinCandleCombatStartForTest(agg, 3);
        RunTracker.RecordPumpkinCandleCombatStartForTest(agg, 0);
        RunTracker.RecordPumpkinCandleRekindledForTest(agg);
        RunTracker.RecordPumpkinCandleRekindledForTest(agg);
        RunTracker.RecordEnergyResetRelicEnergyGeneratedForTest(
            agg,
            amount: 7,
            countCombat: false);

        Assert.Equal(8, agg.PumpkinCandleCombatStartChargeTotal);
        Assert.Equal(3, agg.PumpkinCandleCombatStartChargeSamples);
        Assert.Equal(2, agg.PumpkinCandleRekindles);
        Assert.Equal(7, agg.EnergyGenerated);
    }

    [Fact]
    public void MergeRelicAggregateInto_PumpkinCandleFields_Accumulate()
    {
        var target = new RelicAggregate
        {
            EnergyGenerated = 5,
            PumpkinCandleCombatStartChargeTotal = 7,
            PumpkinCandleCombatStartChargeSamples = 2,
            PumpkinCandleRekindles = 1,
        };
        var source = new RelicAggregate
        {
            EnergyGenerated = 9,
            PumpkinCandleCombatStartChargeTotal = 5,
            PumpkinCandleCombatStartChargeSamples = 2,
            PumpkinCandleRekindles = 1,
        };

        RunTracker.MergeRelicAggregateInto(target, source);

        Assert.Equal(14, target.EnergyGenerated);
        Assert.Equal(12, target.PumpkinCandleCombatStartChargeTotal);
        Assert.Equal(4, target.PumpkinCandleCombatStartChargeSamples);
        Assert.Equal(2, target.PumpkinCandleRekindles);
    }

    [Fact]
    public void RelicTooltip_PumpkinCandle_ShowsEnergyChargeAndRekindleStats()
    {
        var relic = (PumpkinCandle)RuntimeHelpers.GetUninitializedObject(
            typeof(PumpkinCandle));
        var agg = new RelicAggregate
        {
            EnergyGenerated = 14,
            PumpkinCandleCombatStartChargeTotal = 12,
            PumpkinCandleCombatStartChargeSamples = 4,
            PumpkinCandleRekindles = 2,
        };

        var recognized = RelicHoverShowPatch.TryBuildBodyBBCode(
            relic,
            agg,
            floorCount: null,
            out var title,
            out var body);

        Assert.True(recognized);
        Assert.Equal("Pumpkin Candle", title);
        Assert.Contains("Energy gained total", body);
        Assert.Contains("Avg energy gained per combat", body);
        Assert.Contains("Avg charges at combat start", body);
        Assert.Contains(
            StatConceptGlossary.RenderHintedGlyph("campfire"),
            body);
        Assert.Contains(
            StatConceptGlossary.RenderHintedInlineImage(
                RelicHoverShowPatch.ResolveGrantedRelicIconPath(
                    PumpkinCandleRelicId),
                "Pumpkin Candle"),
            body);
        Assert.Contains(
            "Times Pumpkin Candle was rekindled at a campfire.",
            body);
        Assert.Contains("[b]14/0[/b]", body);
        Assert.Contains("[b]3.5[/b]", body);
        Assert.Contains("[b]3[/b]", body);
        Assert.Contains("[b]2[/b]", body);
        Assert.Equal(BuildBody(agg), body);
    }

    [Fact]
    public void OlderShapeWithoutPumpkinCandleFields_DefaultsToZero()
    {
        var agg = JsonSerializer.Deserialize<RelicAggregate>(
            "{}",
            RunStorage.Options);

        Assert.NotNull(agg);
        Assert.Equal(0, agg!.PumpkinCandleCombatStartChargeTotal);
        Assert.Equal(0, agg.PumpkinCandleCombatStartChargeSamples);
        Assert.Equal(0, agg.PumpkinCandleRekindles);
    }

    private static string BuildBody(RelicAggregate agg)
        => (string)(BuildBodyMethod.Invoke(null, new object?[] { agg })
            ?? throw new InvalidOperationException(
                "BuildPumpkinCandleBodyBBCode returned null."));
}
