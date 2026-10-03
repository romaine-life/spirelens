using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class NunchakuStatsTests
{
    private const string NunchakuRelicId = "RELIC.NUNCHAKU";

    private static readonly MethodInfo BuildNunchakuBodyMethod =
        typeof(RelicHoverShowPatch).GetMethod("BuildNunchakuBodyBBCode", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildNunchakuBodyBBCode not found.");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void RelicAggregate_NunchakuFields_DefaultToZero()
    {
        var agg = new RelicAggregate();

        Assert.Equal(0, agg.NunchakuAttacksPlayed);
        Assert.Equal(0, agg.EnergyGenerated);
        Assert.Equal(0, agg.EnergyGeneratedCombats);
        Assert.Equal(0, agg.NunchakuCombatsEndedOn8Charges);
        Assert.Equal(0, agg.NunchakuCombatsEndedOn9Charges);
        Assert.Equal(0, agg.NunchakuCombatEndChargeTotal);
    }

    [Fact]
    public void RelicAggregate_NunchakuFields_JsonRoundtrip_PreservesFields()
    {
        var run = new RunData();
        run.RelicAggregates[NunchakuRelicId] = new RelicAggregate
        {
            NunchakuAttacksPlayed = 18,
            EnergyGenerated = 3,
            EnergyGeneratedCombats = 4,
            NunchakuCombatsEndedOn8Charges = 2,
            NunchakuCombatsEndedOn9Charges = 1,
            NunchakuCombatEndChargeTotal = 34,
        };

        var json = JsonSerializer.Serialize(run, SerializerOptions);

        Assert.Contains("nunchaku_attacks_played", json);
        Assert.Contains("energy_generated", json);
        Assert.Contains("energy_generated_combats", json);
        Assert.Contains("nunchaku_combats_ended_on8_charges", json);
        Assert.Contains("nunchaku_combats_ended_on9_charges", json);
        Assert.Contains("nunchaku_combat_end_charge_total", json);

        var restored = JsonSerializer.Deserialize<RunData>(json, SerializerOptions);

        Assert.NotNull(restored);
        var agg = restored!.RelicAggregates[NunchakuRelicId];
        Assert.Equal(18, agg.NunchakuAttacksPlayed);
        Assert.Equal(3, agg.EnergyGenerated);
        Assert.Equal(4, agg.EnergyGeneratedCombats);
        Assert.Equal(2, agg.NunchakuCombatsEndedOn8Charges);
        Assert.Equal(1, agg.NunchakuCombatsEndedOn9Charges);
        Assert.Equal(34, agg.NunchakuCombatEndChargeTotal);
    }

    [Fact]
    public void RunTracker_NunchakuHelpers_AccumulateAndClamp()
    {
        var agg = new RelicAggregate();

        RunTracker.RecordNunchakuAttackPlayedForTest(agg, 18);
        RunTracker.RecordNunchakuAttackPlayedForTest(agg, -1);
        RunTracker.RecordNunchakuCombatEndChargeForTest(agg, 8);
        RunTracker.RecordNunchakuCombatEndChargeForTest(agg, 9);
        RunTracker.RecordNunchakuCombatEndChargeForTest(agg, 7);
        RunTracker.RecordNunchakuCombatEndChargeForTest(agg, -3);

        Assert.Equal(18, agg.NunchakuAttacksPlayed);
        Assert.Equal(1, agg.NunchakuCombatsEndedOn8Charges);
        Assert.Equal(1, agg.NunchakuCombatsEndedOn9Charges);
        Assert.Equal(24, agg.NunchakuCombatEndChargeTotal);
    }

    [Fact]
    public void RelicTooltip_Nunchaku_ShowsCountsAndAverages()
    {
        var agg = new RelicAggregate
        {
            NunchakuAttacksPlayed = 18,
            EnergyGenerated = 3,
            EnergyGeneratedCombats = 4,
            NunchakuCombatsEndedOn8Charges = 2,
            NunchakuCombatsEndedOn9Charges = 1,
            NunchakuCombatEndChargeTotal = 34,
        };

        var body = InvokeTooltipBuilder(agg);

        Assert.Contains("The number of Attack cards played while this relic was held.", body);
        Assert.Contains("Avg attacks played per combat", body);
        Assert.Contains("Energy gained total", body);
        Assert.Contains("Avg energy gained per combat", body);
        Assert.Contains("Combats ended on 8 charges", body);
        Assert.Contains("Combats ended on 9 charges", body);
        Assert.Contains("Avg charge at combat end", body);
        Assert.Contains("[b]18[/b]", body);
        Assert.Contains("[b]4.5[/b]", body);
        Assert.Contains("[b]3/0[/b]", body);
        Assert.Contains("[b]0.75[/b]", body);
        Assert.Contains("[b]2[/b]", body);
        Assert.Contains("[b]1[/b]", body);
        Assert.Contains("[b]8.5[/b]", body);
    }

    [Fact]
    public void RelicTooltip_Nunchaku_ShowsZeroRowsForEmptyAggregate()
    {
        var body = InvokeTooltipBuilder(new RelicAggregate());

        Assert.Contains("The number of Attack cards played while this relic was held.", body);
        Assert.Contains("Avg attacks played per combat", body);
        Assert.Contains("Energy gained total", body);
        Assert.Contains("Avg energy gained per combat", body);
        Assert.Contains("Combats ended on 8 charges", body);
        Assert.Contains("Combats ended on 9 charges", body);
        Assert.Contains("Avg charge at combat end", body);
        // The generated total now shares its row with wasted, so it is
        // one "[b]x/y[/b]" cell rather than a bare zero.
        Assert.Contains("[b]0/0[/b]", body);
        Assert.Equal(6, CountOccurrences(body, "[b]0[/b]"));
    }

    private static string InvokeTooltipBuilder(RelicAggregate agg)
    {
        return (string)(BuildNunchakuBodyMethod.Invoke(null, new object?[] { agg })
            ?? throw new InvalidOperationException("BuildNunchakuBodyBBCode returned null."));
    }

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
