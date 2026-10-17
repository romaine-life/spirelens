using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using SpireLens.Core;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class ViciousPowerStatsTests
{
    private const string ViciousPowerId = "POWER.VICIOUS";

    // #309 folded the per-power appenders into two shared renderers: the
    // canonical full view a shared meta-power record shows, and the compact
    // summary a physical copy of that Power card shows.
    private static readonly MethodInfo AppendCanonicalMetaPowerStatsMethod =
        typeof(CardHoverShowPatch).GetMethod(
            "AppendCanonicalMetaPowerStats",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "AppendCanonicalMetaPowerStats not found.");

    private static readonly MethodInfo AppendPhysicalMetaPowerSummaryMethod =
        typeof(CardHoverShowPatch).GetMethod(
            "AppendPhysicalMetaPowerSummary",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "AppendPhysicalMetaPowerSummary not found.");

    [Fact]
    public void PowerAggregate_ViciousCardsDrawn_DefaultsAndSerializes()
    {
        var empty = new PowerAggregate();
        Assert.Equal(0, empty.ViciousCardsDrawn);

        var run = new RunData();
        run.MetaStats.PowerAggregates[ViciousPowerId] = CreateAggregate(11);

        var json = JsonSerializer.Serialize(run, RunStorage.Options);
        var restored = JsonSerializer.Deserialize<RunData>(json, RunStorage.Options);

        Assert.Contains("\"vicious_cards_drawn\"", json);
        Assert.NotNull(restored);
        Assert.Equal(
            11,
            restored!.MetaStats.PowerAggregates[ViciousPowerId].ViciousCardsDrawn);
    }

    [Fact]
    public void Promotion_MergesViciousPowerTotals()
    {
        var run = new RunData();
        run.MetaStats.PowerAggregates[ViciousPowerId] = CreateAggregate(5);
        var pending = new PendingCombat();
        pending.MetaStats.PowerAggregates[ViciousPowerId] = CreateAggregate(6);

        RunTracker.PromotePendingCombatIntoRun(pending, run);

        Assert.Equal(
            11,
            run.MetaStats.PowerAggregates[ViciousPowerId].ViciousCardsDrawn);
    }

    [Fact]
    public void Vicious_IsTrackedByEffectFrames()
    {
        Assert.Contains("ViciousPower", Effects.EffectRegistry.PowerClassNames);
        Assert.Contains("DarkEmbracePower", Effects.EffectRegistry.PowerClassNames);
        Assert.Contains("DanseMacabrePower", Effects.EffectRegistry.PowerClassNames);
    }

    [Fact]
    public void EffectOutcome_ReadsBagAndDefaultsToZero()
    {
        var aggregate = new PowerAggregate();
        Assert.Equal(0m, CardHoverShowPatch.EffectOutcome(aggregate, Effects.EffectOutcomeKeys.CardsDrawn));

        aggregate.Outcomes = new() { [Effects.EffectOutcomeKeys.CardsDrawn] = 7m };
        Assert.Equal(7m, CardHoverShowPatch.EffectOutcome(aggregate, Effects.EffectOutcomeKeys.CardsDrawn));
    }

    [Fact]
    public void ViciousTooltip_ProjectsSharedPowerCardsDrawn()
    {
        // Resolve through the registry rather than naming the id here:
        // ids come from the game's types, so a hand-written constant
        // silently stops matching when a type is renamed.
        var definition = MetaPowerRegistry.All.Single(
            candidate => candidate.DisplayName == "Vicious");
        var sb = new StringBuilder();
        var metaStats = new RunMetaStats();
        metaStats.PowerAggregates[definition.PowerId] = CreateAggregate(11);

        _ = AppendCanonicalMetaPowerStatsMethod.Invoke(
            null,
            [sb, definition, metaStats]);

        Assert.Contains("cards drawn", sb.ToString());
        Assert.Contains("[b]11[/b]", sb.ToString());
    }

    [Fact]
    [Trait("Category", "RequiresLiveGame")]
    public void Patch_TargetsViciousPowerCallbackWithExpectedParameters()
    {
        var target = typeof(ViciousPower).GetMethod(
            nameof(ViciousPower.AfterPowerAmountChanged),
            new[]
            {
                typeof(PlayerChoiceContext),
                typeof(PowerModel),
                typeof(decimal),
                typeof(Creature),
                typeof(CardModel),
            });

        Assert.NotNull(target);
    }

    private static PowerAggregate CreateAggregate(int cardsDrawn) =>
        new()
        {
            PowerId = ViciousPowerId,
            DisplayName = "Vicious",
            ViciousCardsDrawn = cardsDrawn,
        };
}
