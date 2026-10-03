using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpireLens.Core;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class BoomingConchStatsTests
{
    private const string BoomingConchRelicId = "RELIC.BOOMING_CONCH";

    private static readonly MethodInfo BuildBoomingConchBodyMethod =
        typeof(RelicHoverShowPatch).GetMethod("BuildBoomingConchBodyBBCode", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildBoomingConchBodyBBCode not found.");

    private static readonly MethodInfo TargetMethod =
        typeof(BoomingConchAfterSideTurnStartPatch).GetMethod("TargetMethod", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Booming Conch energy TargetMethod not found.");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Trait("Category", "RequiresLiveGame")]
    [Fact]
    public void EnergyPatch_TargetsAfterSideTurnStart()
    {
        var method = TargetMethod.Invoke(null, null) as MethodBase;

        Assert.NotNull(method);
        Assert.Equal("AfterSideTurnStart", method!.Name);
    }

    [Fact]
    public void RelicAggregate_BoomingConchFields_DefaultToZero()
    {
        var agg = new RelicAggregate();

        Assert.Equal(0, agg.EnergyGenerated);
        Assert.Equal(0, agg.AdditionalCardsDrawn);
    }

    [Fact]
    public void RelicAggregate_BoomingConchFields_JsonRoundtrip_PreservesFields()
    {
        var run = new RunData();
        run.RelicAggregates[BoomingConchRelicId] = new RelicAggregate
        {
            EnergyGenerated = 2,
            AdditionalCardsDrawn = 4,
        };

        var json = JsonSerializer.Serialize(run, SerializerOptions);

        Assert.Contains("energy_generated", json);
        Assert.Contains("additional_cards_drawn", json);

        var restored = JsonSerializer.Deserialize<RunData>(json, SerializerOptions);

        Assert.NotNull(restored);
        var agg = restored!.RelicAggregates[BoomingConchRelicId];
        Assert.Equal(2, agg.EnergyGenerated);
        Assert.Equal(4, agg.AdditionalCardsDrawn);
    }

    [Fact]
    public void RelicTooltip_BoomingConch_ShowsEnergyAndCardsDrawn()
    {
        var agg = new RelicAggregate
        {
            EnergyGenerated = 2,
            AdditionalCardsDrawn = 4,
        };

        var body = (string)(BuildBoomingConchBodyMethod.Invoke(null, new object?[] { agg })
            ?? throw new InvalidOperationException("BuildBoomingConchBodyBBCode returned null."));

        Assert.Contains("Energy generated", body);
        Assert.Contains("Cards drawn", body);
        Assert.Contains("[b]2/0[/b]", body);
        Assert.Contains("[b]4[/b]", body);
    }
}
