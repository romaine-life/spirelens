using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class VenerableTeaSetStatsTests
{
    private static readonly MethodInfo BuildBodyMethod =
        typeof(RelicHoverShowPatch).GetMethod(
            "BuildVenerableTeaSetBodyBBCode",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildVenerableTeaSetBodyBBCode not found.");

    [Fact]
    [Trait("Category", "RequiresLiveGame")]
    public void Patch_TargetsBothTeaSetEnergyResetCallbacks()
    {
        var targetMethods = typeof(VenerableTeaSetStatsPatch).GetMethod(
            "TargetMethods",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("TargetMethods not found.");
        var targets = ((IEnumerable<MethodBase>)targetMethods.Invoke(null, null)!).ToList();

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, target => target.DeclaringType == typeof(FakeVenerableTeaSet));
        Assert.Contains(targets, target => target.DeclaringType == typeof(VenerableTeaSet));
        Assert.All(
            targets,
            target => Assert.Equal(
                new[] { typeof(Player) },
                target.GetParameters().Select(parameter => parameter.ParameterType)));
    }

    [Fact]
    public void Tracker_RecordsEachActivationAndObservedEnergyDelta()
    {
        var agg = new RelicAggregate();

        RunTracker.RecordVenerableTeaSetActivationForTest(agg, initialEnergy: 3, finalEnergy: 5);
        RunTracker.RecordVenerableTeaSetActivationForTest(agg, initialEnergy: 5, finalEnergy: 6);

        Assert.Equal(2, agg.Activations);
        Assert.Equal(3, agg.EnergyGenerated);
    }

    [Fact]
    public void Tooltip_ShowsRequestedActivationAndEnergyRows()
    {
        var body = (string)(BuildBodyMethod.Invoke(
            null,
            new object[]
            {
                new RelicAggregate
                {
                    Activations = 2,
                    EnergyGenerated = 3,
                    EnergyWasted = 1,
                },
            })
            ?? throw new InvalidOperationException("BuildVenerableTeaSetBodyBBCode returned null."));

        Assert.Contains("Activations", body);
        Assert.Contains("[b]2[/b]", body);
        // The Tea Set measures its own gain from a pool delta, so it claims
        // ledger ownership separately; both halves belong on the one row.
        Assert.Contains("Energy gained/wasted", body);
        Assert.Contains("[b]3/1[/b]", body);
    }

    [Theory]
    [InlineData(typeof(FakeVenerableTeaSet), "Venerable Tea Set???")]
    [InlineData(typeof(VenerableTeaSet), "Venerable Tea Set")]
    public void Tooltip_DispatchesEachTeaSetWithItsOwnTitle(Type relicType, string expectedTitle)
    {
        var relic = (RelicModel)RuntimeHelpers.GetUninitializedObject(relicType);

        var recognized = RelicHoverShowPatch.TryBuildBodyBBCode(
            relic,
            new RelicAggregate(),
            floorCount: null,
            out var title,
            out _);

        Assert.True(recognized);
        Assert.Equal(expectedTitle, title);
    }

    [Fact]
    public void Tooltip_FakeAnchorUsesFullObscuredName()
    {
        var relic = (FakeAnchor)RuntimeHelpers.GetUninitializedObject(typeof(FakeAnchor));

        var recognized = RelicHoverShowPatch.TryBuildBodyBBCode(
            relic,
            new RelicAggregate(),
            floorCount: null,
            out var title,
            out _);

        Assert.True(recognized);
        Assert.Equal("Anchor???", title);
    }
}
