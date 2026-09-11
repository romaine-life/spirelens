using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Models.Relics;
using SpireLens.Core;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

/// <summary>
/// Tests for Happy Flower relic stat data model, persistence, and tooltip
/// presentation. Live RunTracker integration is exercised by the verification
/// phase via live in-run MCP evidence.
/// </summary>
public class HappyFlowerStatsTests
{
    private const string HappyFlowerRelicId = "RELIC.HAPPY_FLOWER";
    private const string FakeHappyFlowerRelicId = "RELIC.FAKE_HAPPY_FLOWER";

    private static readonly MethodInfo BuildHappyFlowerBodyMethod =
        typeof(RelicHoverShowPatch).GetMethod("BuildHappyFlowerBodyBBCode", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildHappyFlowerBodyBBCode not found.");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void RelicAggregate_EnergyGenerated_DefaultsToZero()
    {
        var agg = new RelicAggregate();
        Assert.Equal(0, agg.EnergyGenerated);
        Assert.Equal(0, agg.EnergyGeneratedCombats);
    }

    [Fact]
    public void RelicAggregate_EnergyGenerated_JsonRoundtrip_PreservesField()
    {
        var agg = new RelicAggregate
        {
            EnergyGenerated = 3,
            EnergyGeneratedCombats = 2,
        };
        var run = new RunData();
        run.RelicAggregates[HappyFlowerRelicId] = agg;

        var json = JsonSerializer.Serialize(run, SerializerOptions);

        Assert.Contains("relic_aggregates", json);
        Assert.Contains("energy_generated", json);
        Assert.Contains("energy_generated_combats", json);

        var restored = JsonSerializer.Deserialize<RunData>(json, SerializerOptions);
        Assert.NotNull(restored);
        Assert.True(restored!.RelicAggregates.ContainsKey(HappyFlowerRelicId));
        var restoredAgg = restored.RelicAggregates[HappyFlowerRelicId];
        Assert.Equal(3, restoredAgg.EnergyGenerated);
        Assert.Equal(2, restoredAgg.EnergyGeneratedCombats);
    }

    [Fact]
    public void RelicAggregate_EnergyGenerated_AccumulatesAcrossTriggers()
    {
        var agg = new RelicAggregate();

        RunTracker.RecordHappyFlowerEnergyGeneratedForTest(agg, amount: 3, combats: 2);
        RunTracker.RecordHappyFlowerEnergyGeneratedForTest(agg, amount: -1, combats: -1);

        Assert.Equal(3, agg.EnergyGenerated);
        Assert.Equal(2, agg.EnergyGeneratedCombats);
    }

    [Fact]
    public void RelicTooltip_EnergyGenerated_ShowsEnergyIconTotalAndAverage()
    {
        var agg = new RelicAggregate
        {
            EnergyGenerated = 3,
            EnergyGeneratedCombats = 2,
        };

        var body = (string)(BuildHappyFlowerBodyMethod.Invoke(null, new object?[] { agg })
            ?? throw new InvalidOperationException("BuildHappyFlowerBodyBBCode returned null."));

        Assert.Contains("Energy generated", body);
        Assert.Contains("Combats held", body);
        Assert.Contains("Avg energy generated per combat", body);
        Assert.Contains("[b]3/0[/b]", body);
        Assert.Contains("[b]2[/b]", body);
        Assert.Contains("[b]1.5[/b]", body);
    }

    [Fact]
    public void RelicTooltip_EnergyGenerated_ShowsZeroAverageWithoutCombats()
    {
        var body = (string)(BuildHappyFlowerBodyMethod.Invoke(null, new object?[] { new RelicAggregate() })
            ?? throw new InvalidOperationException("BuildHappyFlowerBodyBBCode returned null."));

        Assert.Contains("Energy generated", body);
        Assert.Contains("Combats held", body);
        Assert.Contains("Avg energy generated per combat", body);
        Assert.Contains("[b]0[/b]", body);
    }

    [Fact]
    public void FakeHappyFlower_UsesSameStatsWithObscuredTitleAndSeparateAggregate()
    {
        var happyFlower = Uninitialized<HappyFlower>();
        var fakeHappyFlower = Uninitialized<FakeHappyFlower>();
        var aggregate = new RelicAggregate
        {
            EnergyGenerated = 2,
            EnergyGeneratedCombats = 4,
        };

        var realRecognized = RelicHoverShowPatch.TryBuildBodyBBCode(
            happyFlower,
            aggregate,
            floorCount: null,
            out var realTitle,
            out var realBody);
        var fakeRecognized = RelicHoverShowPatch.TryBuildBodyBBCode(
            fakeHappyFlower,
            aggregate,
            floorCount: null,
            out var fakeTitle,
            out var fakeBody);

        Assert.True(realRecognized);
        Assert.True(fakeRecognized);
        Assert.Equal("Happy Flower", realTitle);
        Assert.Equal("Happy Flower???", fakeTitle);
        Assert.Equal(realBody, fakeBody);
        Assert.Equal(
            HappyFlowerRelicId,
            RunTracker.GetHappyFlowerStatsRelicId(happyFlower));
        Assert.Equal(
            FakeHappyFlowerRelicId,
            RunTracker.GetHappyFlowerStatsRelicId(fakeHappyFlower));
    }

    [Fact]
    public void RunData_OlderShapeWithoutEnergyGenerated_DeserializesWithZeroDefault()
    {
        const string json = """
            {
              "run_id": "test",
              "started_at": "2026-01-01T00:00:00Z",
              "updated_at": "2026-01-01T00:00:00Z",
              "outcome": "in_progress",
              "aggregates": {},
              "events": [],
              "instance_numbers_by_def": {},
              "def_counters": {},
              "relic_aggregates": {
                "RELIC.HAPPY_FLOWER": {
                  "enemies_affected": 0,
                  "vulnerable_applied": 0,
                  "weak_applied": 0
                }
              }
            }
            """;

        var run = JsonSerializer.Deserialize<RunData>(json, SerializerOptions);

        Assert.NotNull(run);
        Assert.True(run!.RelicAggregates.ContainsKey(HappyFlowerRelicId));
        var agg = run.RelicAggregates[HappyFlowerRelicId];
        Assert.Equal(0, agg.EnergyGenerated);
        Assert.Equal(0, agg.EnergyGeneratedCombats);
    }

    private static T Uninitialized<T>() where T : class
        => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}
