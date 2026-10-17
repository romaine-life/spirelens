using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpireLens.Core;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class CandelabraStatsTests
{
    private const string LanternRelicId = "RELIC.LANTERN";
    private const string VeryHotCocoaRelicId = "RELIC.VERY_HOT_COCOA";
    private const string CandelabraRelicId = "RELIC.CANDELABRA";
    private const string ChandelierRelicId = "RELIC.CHANDELIER";

    private static readonly MethodInfo BuildLanternBodyMethod =
        typeof(RelicHoverShowPatch).GetMethod("BuildLanternBodyBBCode", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildLanternBodyBBCode not found.");

    private static readonly MethodInfo BuildVeryHotCocoaBodyMethod =
        typeof(RelicHoverShowPatch).GetMethod("BuildVeryHotCocoaBodyBBCode", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildVeryHotCocoaBodyBBCode not found.");

    private static readonly MethodInfo BuildCandelabraBodyMethod =
        typeof(RelicHoverShowPatch).GetMethod("BuildCandelabraBodyBBCode", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildCandelabraBodyBBCode not found.");

    private static readonly MethodInfo BuildChandelierBodyMethod =
        typeof(RelicHoverShowPatch).GetMethod("BuildChandelierBodyBBCode", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildChandelierBodyBBCode not found.");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void RelicAggregate_TurnEnergyRelicFields_DefaultToZero()
    {
        var agg = new RelicAggregate();

        Assert.Equal(0, agg.Activations);
        Assert.Equal(0, agg.EnergyGenerated);
        Assert.Equal(0, agg.FirstTurnsEndedWithExcessEnergy);
        Assert.Equal(0, agg.SecondTurnsEndedWithExcessEnergy);
        Assert.Equal(0, agg.ThirdTurnsEndedWithExcessEnergy);
        Assert.Equal(0, agg.CombatsWithoutActivation);
    }

    [Fact]
    public void RelicAggregate_TurnEnergyRelicFields_JsonRoundtrip_PreservesFields()
    {
        var run = new RunData();
        run.RelicAggregates[LanternRelicId] = new RelicAggregate
        {
            Activations = 2,
            EnergyGenerated = 2,
            FirstTurnsEndedWithExcessEnergy = 1,
        };
        run.RelicAggregates[VeryHotCocoaRelicId] = new RelicAggregate
        {
            Activations = 3,
            EnergyGenerated = 3,
            FirstTurnsEndedWithExcessEnergy = 2,
        };
        run.RelicAggregates[CandelabraRelicId] = new RelicAggregate
        {
            Activations = 4,
            EnergyGenerated = 8,
            SecondTurnsEndedWithExcessEnergy = 2,
            CombatsWithoutActivation = 1,
        };
        run.RelicAggregates[ChandelierRelicId] = new RelicAggregate
        {
            Activations = 3,
            EnergyGenerated = 9,
            ThirdTurnsEndedWithExcessEnergy = 1,
            CombatsWithoutActivation = 2,
        };

        var json = JsonSerializer.Serialize(run, SerializerOptions);

        Assert.Contains("activations", json);
        Assert.Contains("energy_generated", json);
        Assert.Contains("first_turns_ended_with_excess_energy", json);
        Assert.Contains("second_turns_ended_with_excess_energy", json);
        Assert.Contains("third_turns_ended_with_excess_energy", json);
        Assert.Contains("combats_without_activation", json);

        var restored = JsonSerializer.Deserialize<RunData>(json, SerializerOptions);

        Assert.NotNull(restored);
        var lantern = restored!.RelicAggregates[LanternRelicId];
        Assert.Equal(2, lantern.Activations);
        Assert.Equal(2, lantern.EnergyGenerated);
        Assert.Equal(1, lantern.FirstTurnsEndedWithExcessEnergy);

        var veryHotCocoa = restored.RelicAggregates[VeryHotCocoaRelicId];
        Assert.Equal(3, veryHotCocoa.Activations);
        Assert.Equal(3, veryHotCocoa.EnergyGenerated);
        Assert.Equal(2, veryHotCocoa.FirstTurnsEndedWithExcessEnergy);

        var candelabra = restored.RelicAggregates[CandelabraRelicId];
        Assert.Equal(4, candelabra.Activations);
        Assert.Equal(8, candelabra.EnergyGenerated);
        Assert.Equal(2, candelabra.SecondTurnsEndedWithExcessEnergy);
        Assert.Equal(1, candelabra.CombatsWithoutActivation);

        var chandelier = restored.RelicAggregates[ChandelierRelicId];
        Assert.Equal(3, chandelier.Activations);
        Assert.Equal(9, chandelier.EnergyGenerated);
        Assert.Equal(1, chandelier.ThirdTurnsEndedWithExcessEnergy);
        Assert.Equal(2, chandelier.CombatsWithoutActivation);
    }

    [Fact]
    public void MergeRelicAggregateInto_TurnEnergyRelicFields_Accumulates()
    {
        var target = new RelicAggregate
        {
            Activations = 1,
            EnergyGenerated = 2,
            FirstTurnsEndedWithExcessEnergy = 1,
            SecondTurnsEndedWithExcessEnergy = 1,
            ThirdTurnsEndedWithExcessEnergy = 0,
            CombatsWithoutActivation = 1,
        };
        var source = new RelicAggregate
        {
            Activations = 3,
            EnergyGenerated = 6,
            FirstTurnsEndedWithExcessEnergy = 2,
            SecondTurnsEndedWithExcessEnergy = 2,
            ThirdTurnsEndedWithExcessEnergy = 4,
            CombatsWithoutActivation = 2,
        };

        RunTracker.MergeRelicAggregateInto(target, source);

        Assert.Equal(4, target.Activations);
        Assert.Equal(8, target.EnergyGenerated);
        Assert.Equal(3, target.FirstTurnsEndedWithExcessEnergy);
        Assert.Equal(3, target.SecondTurnsEndedWithExcessEnergy);
        Assert.Equal(4, target.ThirdTurnsEndedWithExcessEnergy);
        Assert.Equal(3, target.CombatsWithoutActivation);
    }

    [Fact]
    public void RunTracker_TurnEnergyRelicExcessBuckets_AreKeyedByCombatRound()
    {
        Assert.True(RunTracker.IsTurnEnergyRelicExcessRoundForTest(LanternRelicId, 1));
        Assert.True(RunTracker.IsTurnEnergyRelicExcessRoundForTest(VeryHotCocoaRelicId, 1));
        Assert.True(RunTracker.IsTurnEnergyRelicExcessRoundForTest(CandelabraRelicId, 2));
        Assert.True(RunTracker.IsTurnEnergyRelicExcessRoundForTest(ChandelierRelicId, 3));

        Assert.False(RunTracker.IsTurnEnergyRelicExcessRoundForTest(VeryHotCocoaRelicId, 0));
        Assert.False(RunTracker.IsTurnEnergyRelicExcessRoundForTest(VeryHotCocoaRelicId, 2));
        Assert.False(RunTracker.IsTurnEnergyRelicExcessRoundForTest(CandelabraRelicId, 1));
        Assert.False(RunTracker.IsTurnEnergyRelicExcessRoundForTest(ChandelierRelicId, 2));
        Assert.False(RunTracker.IsTurnEnergyRelicExcessRoundForTest("RELIC.UNKNOWN", 1));
    }

    [Fact]
    public void RelicTooltip_Lantern_ShowsRequestedRowsAndZeroValues()
    {
        var body = BuildBody(BuildLanternBodyMethod, new RelicAggregate());

        Assert.Contains("Activations", body);
        Assert.Contains("Energy generated", body);
        Assert.Contains("1st turns ended with excess energy", body);
        // The generated total now shares its row with wasted, so it is
        // one "[b]x/y[/b]" cell rather than a bare zero.
        Assert.Contains("[b]0/0[/b]", body);
        Assert.Equal(2, CountOccurrences(body, "[b]0[/b]"));
        Assert.DoesNotContain("Combats with energy not gained", body);
    }

    [Fact]
    public void RelicTooltip_VeryHotCocoa_ShowsLanternStyleRowsAndZeroValues()
    {
        var body = BuildBody(BuildVeryHotCocoaBodyMethod, new RelicAggregate());

        Assert.Contains("Activations", body);
        Assert.Contains("Energy generated", body);
        Assert.Contains("1st turns ended with excess energy", body);
        // The generated total now shares its row with wasted, so it is
        // one "[b]x/y[/b]" cell rather than a bare zero.
        Assert.Contains("[b]0/0[/b]", body);
        Assert.Equal(2, CountOccurrences(body, "[b]0[/b]"));
        Assert.DoesNotContain("Combats with energy not gained", body);
    }

    [Fact]
    public void RelicTooltip_VeryHotCocoa_ShowsTrackedCounts()
    {
        var agg = new RelicAggregate
        {
            Activations = 3,
            EnergyGenerated = 3,
            FirstTurnsEndedWithExcessEnergy = 2,
        };

        var body = BuildBody(BuildVeryHotCocoaBodyMethod, agg);

        Assert.Contains("Activations", body);
        Assert.Contains("Energy generated", body);
        Assert.Contains("[b]3[/b]", body);
        Assert.Contains("[b]2[/b]", body);
        Assert.DoesNotContain("Combats with energy not gained", body);
    }

    [Fact]
    public void RelicTooltip_Candelabra_ShowsRequestedRowsAndZeroValues()
    {
        var body = BuildBody(BuildCandelabraBodyMethod, new RelicAggregate());

        Assert.Contains("Activations", body);
        Assert.Contains("Energy generated", body);
        Assert.Contains("2nd turns ended with excess energy", body);
        Assert.Contains("Combats without energy", body);
        // The generated total now shares its row with wasted, so it is
        // one "[b]x/y[/b]" cell rather than a bare zero.
        Assert.Contains("[b]0/0[/b]", body);
        Assert.Equal(3, CountOccurrences(body, "[b]0[/b]"));
    }

    [Fact]
    public void RelicTooltip_Candelabra_ShowsTrackedCounts()
    {
        var agg = new RelicAggregate
        {
            Activations = 4,
            EnergyGenerated = 8,
            SecondTurnsEndedWithExcessEnergy = 2,
            CombatsWithoutActivation = 1,
        };

        var body = BuildBody(BuildCandelabraBodyMethod, agg);

        Assert.Contains("Activations", body);
        Assert.Contains("Energy generated", body);
        Assert.Contains("2nd turns ended with excess energy", body);
        Assert.Contains("[b]2[/b]", body);
        Assert.Contains("[b]1[/b]", body);
        Assert.Contains("[b]4[/b]", body);
        Assert.Contains("[b]8/0[/b]", body);
        Assert.Contains("[b]2[/b]", body);
    }

    [Fact]
    public void RelicTooltip_Chandelier_ShowsRequestedRowsAndTrackedCounts()
    {
        var agg = new RelicAggregate
        {
            Activations = 3,
            EnergyGenerated = 9,
            ThirdTurnsEndedWithExcessEnergy = 1,
            CombatsWithoutActivation = 2,
        };

        var body = BuildBody(BuildChandelierBodyMethod, agg);

        Assert.Contains("Activations", body);
        Assert.Contains("Energy generated", body);
        Assert.Contains("[b]9/0[/b]", body);
        Assert.Contains("[b]1[/b]", body);
        Assert.Contains("[b]2[/b]", body);
    }

    [Fact]
    public void RunData_OlderShapeWithoutTurnEnergyRelicFields_DeserializesWithZeroDefaults()
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
                "RELIC.CANDELABRA": {}
              }
            }
            """;

        var run = JsonSerializer.Deserialize<RunData>(json, SerializerOptions);

        Assert.NotNull(run);
        var agg = run!.RelicAggregates[CandelabraRelicId];
        Assert.Equal(0, agg.Activations);
        Assert.Equal(0, agg.EnergyGenerated);
        Assert.Equal(0, agg.FirstTurnsEndedWithExcessEnergy);
        Assert.Equal(0, agg.SecondTurnsEndedWithExcessEnergy);
        Assert.Equal(0, agg.ThirdTurnsEndedWithExcessEnergy);
        Assert.Equal(0, agg.CombatsWithoutActivation);
    }

    private static string BuildBody(MethodInfo method, RelicAggregate agg)
        => (string)(method.Invoke(null, new object?[] { agg })
            ?? throw new InvalidOperationException($"{method.Name} returned null."));

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
