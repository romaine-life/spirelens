using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpireLens.Core;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class BloodSoakedRoseStatsTests
{
    private const string BloodSoakedRoseRelicId = "RELIC.BLOOD_SOAKED_ROSE";
    private const string EnthralledCardId = "CARD.ENTHRALLED#1";

    private static readonly MethodInfo BuildBloodSoakedRoseBodyMethod =
        typeof(RelicHoverShowPatch).GetMethod("BuildBloodSoakedRoseBodyBBCode", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildBloodSoakedRoseBodyBBCode not found.");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void RelicAggregate_BloodSoakedRoseFields_DefaultToZero()
    {
        var relicAgg = new RelicAggregate();
        var curseAgg = new CardAggregate();

        Assert.Equal(0, relicAgg.Activations);
        Assert.Equal(0, relicAgg.EnergyGenerated);
        Assert.Equal(0, curseAgg.CombatsInDeck);
        Assert.Equal(0, curseAgg.TimesDrawn);
        Assert.Equal(0, curseAgg.TimesDiscarded);
        Assert.Equal(0, curseAgg.Plays);
        Assert.Equal(0, curseAgg.TimesExhausted);
    }

    [Fact]
    public void RelicAggregate_BloodSoakedRoseAndEnthralledStats_JsonRoundtrip_PreservesFields()
    {
        var run = new RunData();
        run.RelicAggregates[BloodSoakedRoseRelicId] = new RelicAggregate
        {
            Activations = 3,
            EnergyGenerated = 9,
        };
        run.Aggregates[EnthralledCardId] = new CardAggregate
        {
            CombatsInDeck = 3,
            TimesDrawn = 5,
            TimesDiscarded = 2,
            Plays = 1,
            TimesExhausted = 1,
        };

        var json = JsonSerializer.Serialize(run, SerializerOptions);

        Assert.Contains("energy_generated", json);
        Assert.Contains("CARD.ENTHRALLED#1", json);

        var restored = JsonSerializer.Deserialize<RunData>(json, SerializerOptions);

        Assert.NotNull(restored);
        var relicAgg = restored!.RelicAggregates[BloodSoakedRoseRelicId];
        var curseAgg = restored.Aggregates[EnthralledCardId];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(9, relicAgg.EnergyGenerated);
        Assert.Equal(3, curseAgg.CombatsInDeck);
        Assert.Equal(5, curseAgg.TimesDrawn);
        Assert.Equal(2, curseAgg.TimesDiscarded);
        Assert.Equal(1, curseAgg.Plays);
        Assert.Equal(1, curseAgg.TimesExhausted);
    }

    [Fact]
    public void RunTracker_EnergyResetRelicTestHelper_AccumulatesAndCountsCombats()
    {
        var agg = new RelicAggregate();

        RunTracker.RecordEnergyResetRelicEnergyGeneratedForTest(agg, amount: 1, countCombat: true);
        RunTracker.RecordEnergyResetRelicEnergyGeneratedForTest(agg, amount: 2, countCombat: true);
        RunTracker.RecordEnergyResetRelicEnergyGeneratedForTest(agg, amount: -1, countCombat: true);

        Assert.Equal(2, agg.Activations);
        Assert.Equal(3, agg.EnergyGenerated);
    }

    [Fact]
    public void RelicTooltip_BloodSoakedRose_ShowsEnergyAveragesAndEnthralledStats()
    {
        var body = BuildBody(
            new RelicAggregate
            {
                Activations = 2,
                EnergyGenerated = 5,
            },
            new CardAggregate
            {
                CombatsInDeck = 3,
                TimesDrawn = 7,
                TimesDiscarded = 4,
                Plays = 1,
                TimesExhausted = 2,
            });

        Assert.Contains("Energy gained total", body);
        Assert.Contains("Avg energy gained per combat", body);
        Assert.Contains("Enthralled combats", body);
        Assert.Contains("Enthralled drawn", body);
        Assert.Contains("Enthralled discarded", body);
        Assert.Contains("Enthralled played", body);
        Assert.Contains("Enthralled exhausted", body);
        Assert.Contains("[b]5/0[/b]", body);
        Assert.Contains("[b]2.5[/b]", body);
        Assert.Contains("[b]7[/b]", body);
        Assert.Contains("[b]4[/b]", body);
    }

    [Fact]
    public void RelicTooltip_BloodSoakedRose_ShowsZeroRowsWithoutStats()
    {
        var body = BuildBody(new RelicAggregate(), new CardAggregate());

        Assert.Contains("Energy gained total", body);
        Assert.Contains("Avg energy gained per combat", body);
        Assert.Contains("Enthralled combats", body);
        Assert.Contains("Enthralled drawn", body);
        Assert.Contains("Enthralled discarded", body);
        Assert.Contains("Enthralled played", body);
        Assert.Contains("Enthralled exhausted", body);
        Assert.Contains("[b]0[/b]", body);
    }

    private static string BuildBody(RelicAggregate relicAgg, CardAggregate curseAgg)
        => (string)(BuildBloodSoakedRoseBodyMethod.Invoke(null, new object?[] { relicAgg, curseAgg })
            ?? throw new InvalidOperationException("BuildBloodSoakedRoseBodyBBCode returned null."));
}
