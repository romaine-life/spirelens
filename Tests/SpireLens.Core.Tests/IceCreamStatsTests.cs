using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class IceCreamStatsTests
{
    private const string IceCreamRelicId = "RELIC.ICE_CREAM";

    private static readonly MethodInfo BuildBodyMethod =
        typeof(RelicHoverShowPatch).GetMethod(
            "BuildIceCreamBodyBBCode",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildIceCreamBodyBBCode not found.");

    [Fact]
    public void Patch_TargetsTheResetOrCarryDecision()
    {
        var target = typeof(Hook).GetMethod(nameof(Hook.ShouldPlayerResetEnergy));

        Assert.NotNull(target);
        Assert.Equal(typeof(bool), target!.ReturnType);
        Assert.Equal(
            [typeof(MegaCrit.Sts2.Core.Combat.ICombatState), typeof(Player)],
            target.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void ResetAndCarryRemainTheTwoOutcomesOfThatDecision()
    {
        // The conserved amount is only the leftover pool because the losing
        // branch overwrites Energy with MaxEnergy while the winning branch adds
        // to it. If either mutation disappears the measurement changes meaning.
        Assert.NotNull(typeof(PlayerCombatState).GetMethod(
            nameof(PlayerCombatState.ResetEnergy)));
        Assert.NotNull(typeof(PlayerCombatState).GetMethod(
            nameof(PlayerCombatState.AddMaxEnergyToCurrent)));
    }

    [Fact]
    public void IceCreamIsTheOnlyModelThatSuppressesTheEnergyReset()
    {
        // Credit is gated on Ice Cream's own answer, so a second suppressor
        // would not corrupt the total — but it would make the number a shared
        // outcome rather than Ice Cream's alone, which the tooltip claims.
        var suppressors = typeof(AbstractModel).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(AbstractModel).IsAssignableFrom(t))
            .Where(t => t.GetMethod(
                nameof(AbstractModel.ShouldPlayerResetEnergy),
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) != null)
            .ToList();

        Assert.Equal([typeof(IceCream)], suppressors);
    }

    [Fact]
    public void RelicAggregate_IceCreamFields_DefaultToEmpty()
    {
        var aggregate = new RelicAggregate();

        Assert.Equal(0, aggregate.IceCreamEnergyConserved);
        Assert.Equal(0, aggregate.IceCreamTurns);
        Assert.Equal(0, aggregate.IceCreamCombats);
    }

    [Fact]
    public void RelicAggregate_IceCreamFields_JsonRoundtripPreservesValues()
    {
        var run = new RunData();
        run.RelicAggregates[IceCreamRelicId] = new RelicAggregate
        {
            IceCreamEnergyConserved = 27,
            IceCreamTurns = 36,
            IceCreamCombats = 9,
        };

        var json = JsonSerializer.Serialize(run, RunStorage.Options);
        var restored = JsonSerializer.Deserialize<RunData>(json, RunStorage.Options);

        Assert.Contains("ice_cream_energy_conserved", json);
        Assert.Contains("ice_cream_turns", json);
        Assert.Contains("ice_cream_combats", json);
        Assert.NotNull(restored);
        var relic = restored!.RelicAggregates[IceCreamRelicId];
        Assert.Equal(27, relic.IceCreamEnergyConserved);
        Assert.Equal(36, relic.IceCreamTurns);
        Assert.Equal(9, relic.IceCreamCombats);
    }

    [Fact]
    public void EnergyConserved_SumsTheLeftoverPool()
    {
        var aggregate = new RelicAggregate();

        RunTracker.RecordIceCreamEnergyConservedForTest(aggregate, 2);
        RunTracker.RecordIceCreamEnergyConservedForTest(aggregate, 3);

        Assert.Equal(5, aggregate.IceCreamEnergyConserved);
    }

    [Fact]
    public void EnergyConserved_IgnoresNonPositiveReads()
    {
        var aggregate = new RelicAggregate();

        RunTracker.RecordIceCreamEnergyConservedForTest(aggregate, 0);
        RunTracker.RecordIceCreamEnergyConservedForTest(aggregate, -4);

        Assert.Equal(0, aggregate.IceCreamEnergyConserved);
    }

    [Fact]
    public void MergeRelicAggregate_IceCreamFields_AreAdditive()
    {
        var target = new RelicAggregate();

        RunTracker.MergeRelicAggregateInto(
            target,
            new RelicAggregate
            {
                IceCreamEnergyConserved = 10,
                IceCreamTurns = 14,
                IceCreamCombats = 4,
            });
        RunTracker.MergeRelicAggregateInto(
            target,
            new RelicAggregate
            {
                IceCreamEnergyConserved = 17,
                IceCreamTurns = 22,
                IceCreamCombats = 5,
            });

        Assert.Equal(27, target.IceCreamEnergyConserved);
        Assert.Equal(36, target.IceCreamTurns);
        Assert.Equal(9, target.IceCreamCombats);
    }

    [Fact]
    public void Averages_DivideByTheZeroInclusiveHeldDenominators()
    {
        var aggregate = new RelicAggregate
        {
            IceCreamEnergyConserved = 27,
            IceCreamTurns = 36,
            IceCreamCombats = 9,
        };

        Assert.Equal(
            0.75m,
            RelicHoverShowPatch.CalculateIceCreamEnergyConservedPerTurn(aggregate));
        Assert.Equal(
            3m,
            RelicHoverShowPatch.CalculateIceCreamEnergyConservedPerCombat(aggregate));
    }

    [Fact]
    public void Averages_AreZeroWithoutAnyHeldPeriod()
    {
        var aggregate = new RelicAggregate { IceCreamEnergyConserved = 27 };

        Assert.Equal(
            0m,
            RelicHoverShowPatch.CalculateIceCreamEnergyConservedPerTurn(aggregate));
        Assert.Equal(
            0m,
            RelicHoverShowPatch.CalculateIceCreamEnergyConservedPerCombat(aggregate));
    }

    [Fact]
    public void RelicTooltip_IceCream_ShowsTheThreeRequestedRows()
    {
        var body = BuildBody(new RelicAggregate
        {
            IceCreamEnergyConserved = 27,
            IceCreamTurns = 36,
            IceCreamCombats = 9,
        });

        Assert.Contains("Total energy conserved", body);
        Assert.Contains("Avg energy conserved per turn", body);
        Assert.Contains("Avg energy conserved per combat", body);
        Assert.Contains("[b]27[/b]", body);
        Assert.Contains("[b]0.75[/b]", body);
        Assert.Contains("[b]3[/b]", body);
    }

    [Fact]
    public void RelicTooltip_IceCream_DispatchesForModel()
    {
        var relic = (IceCream)RuntimeHelpers.GetUninitializedObject(typeof(IceCream));

        var recognized = RelicHoverShowPatch.TryBuildBodyBBCode(
            relic,
            new RelicAggregate
            {
                IceCreamEnergyConserved = 27,
                IceCreamTurns = 36,
                IceCreamCombats = 9,
            },
            floorCount: null,
            out var title,
            out var body);

        Assert.True(recognized);
        Assert.Equal("Ice Cream", title);
        Assert.Contains("[b]27[/b]", body);
    }

    private static string BuildBody(RelicAggregate aggregate)
        => (string)(BuildBodyMethod.Invoke(null, new object?[] { aggregate })
            ?? throw new InvalidOperationException("BuildIceCreamBodyBBCode returned null."));
}
