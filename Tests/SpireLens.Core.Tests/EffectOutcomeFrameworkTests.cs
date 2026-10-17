using System.Collections.Generic;
using System.Linq;
using SpireLens.Core;
using SpireLens.Core.Effects;
using Xunit;

namespace SpireLens.Core.Tests;

/// <summary>
/// Pure-logic coverage for the generic effect-frame framework: outcome bag
/// arithmetic, the contributor ledger's proportional shares, and the shared
/// tooltip row builder. The live frame/observer wiring needs the game.
/// </summary>
public class EffectOutcomeFrameworkTests
{
    [Fact]
    public void AddEffectOutcome_CreatesAndAccumulates()
    {
        var bag = RunTracker.AddEffectOutcome(null, EffectOutcomeKeys.Block, 5m);
        bag = RunTracker.AddEffectOutcome(bag, EffectOutcomeKeys.Block, 3m);

        Assert.Equal(8m, bag[EffectOutcomeKeys.Block]);
    }

    [Fact]
    public void MergeEffectOutcomes_SumsKeysAndKeepsUnknownOnes()
    {
        var target = new Dictionary<string, decimal> { ["block"] = 2m };
        var source = new Dictionary<string, decimal> { ["block"] = 4m, ["future_key"] = 1m };

        var merged = RunTracker.MergeEffectOutcomes(target, source);

        Assert.Equal(6m, merged!["block"]);
        Assert.Equal(1m, merged["future_key"]);
    }

    [Fact]
    public void MergeEffectOutcomes_NullSourceLeavesTargetNull()
    {
        Assert.Null(RunTracker.MergeEffectOutcomes(null, null));
    }

    [Fact]
    public void ComputeEffectSourceShares_SplitsByStacksPerDistinctSource()
    {
        var ledger = new List<EffectStackContribution>
        {
            new("CARD.DEMON_FORM#1", null, 2m),
            new(null, "RELIC.BRIMSTONE", 1m),
            new("CARD.DEMON_FORM#1", null, 1m),
        };

        var shares = RunTracker.ComputeEffectSourceShares(ledger);

        Assert.Equal(2, shares.Count);
        Assert.Equal("CARD.DEMON_FORM#1", shares[0].CardInstanceId);
        Assert.Equal(0.75m, shares[0].Share);
        Assert.Equal("RELIC.BRIMSTONE", shares[1].RelicId);
        Assert.Equal(0.25m, shares[1].Share);
    }

    [Fact]
    public void ReduceEffectPowerLedger_ScalesEverySourceProportionally()
    {
        var ledger = new List<EffectStackContribution>
        {
            new("CARD.A#1", null, 3m),
            new("CARD.B#1", null, 1m),
        };

        RunTracker.ReduceEffectPowerLedger(ledger, 2m);

        Assert.Equal(1.5m, ledger[0].Stacks);
        Assert.Equal(0.5m, ledger[1].Stacks);
        Assert.Equal(0.75m, RunTracker.ComputeEffectSourceShares(ledger)[0].Share);
    }

    [Fact]
    public void ReduceEffectPowerLedger_FullReductionEmptiesLedger()
    {
        var ledger = new List<EffectStackContribution> { new("CARD.A#1", null, 2m) };

        RunTracker.ReduceEffectPowerLedger(ledger, 5m);

        Assert.Empty(ledger);
        Assert.Empty(RunTracker.ComputeEffectSourceShares(ledger));
    }

    [Fact]
    public void Rows_FollowDisplayOrderAndAddPerCombatRates()
    {
        var outcomes = new Dictionary<string, decimal>
        {
            [EffectOutcomeKeys.Block] = 42m,
            [EffectOutcomeKeys.Triggers] = 6m,
            [EffectOutcomeKeys.Combats] = 4m,
        };

        var rows = EffectOutcomeRows.Build(outcomes, "Sai");

        Assert.Equal("Activations", rows[0].Label);
        Assert.Equal("6", rows[0].Value);
        Assert.Equal("Block gained", rows[1].Label);
        Assert.Equal("Avg block gained per combat", rows[2].Label);
        Assert.Equal("10.5", rows[2].Value);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void Rows_SkipZeroValues()
    {
        var outcomes = new Dictionary<string, decimal>
        {
            [EffectOutcomeKeys.Energy] = 0m,
            [EffectOutcomeKeys.Gold] = 25m,
        };

        var row = Assert.Single(EffectOutcomeRows.Build(outcomes, "Dragon Fruit"));
        Assert.Equal("Gold gained", row.Label);
        Assert.Equal("25", row.Value);
    }

    [Fact]
    public void HasVisibleOutcomes_IgnoresDenominatorOnlyBags()
    {
        Assert.False(EffectOutcomeRows.HasVisibleOutcomes(
            new Dictionary<string, decimal> { [EffectOutcomeKeys.Combats] = 3m }));
        Assert.True(EffectOutcomeRows.HasVisibleOutcomes(
            new Dictionary<string, decimal> { [EffectOutcomeKeys.Gold] = 30m }));
    }

    [Theory]
    [InlineData("POWER.DEMON_FORM_POWER", "Demon Form")]
    [InlineData("RELIC.CHARONS_ASHES", "Charons Ashes")]
    public void Humanize_TurnsModelIdsIntoNames(string id, string expected)
    {
        Assert.Equal(expected, EffectOutcomeRows.Humanize(id));
    }

    [Fact]
    public void TryParsePower_SplitsSelfAndEnemyKeys()
    {
        Assert.True(EffectOutcomeKeys.TryParsePower("power:POWER.X", out var self, out var selfEnemy));
        Assert.Equal("POWER.X", self);
        Assert.False(selfEnemy);

        Assert.True(EffectOutcomeKeys.TryParsePower("debuff:POWER.Y", out var enemy, out var onEnemy));
        Assert.Equal("POWER.Y", enemy);
        Assert.True(onEnemy);

        Assert.False(EffectOutcomeKeys.TryParsePower("block", out _, out _));
    }

    [Fact]
    public void Registry_EnrollsNoRelicTwice()
    {
        Assert.Equal(
            EffectRegistry.RelicClassNames.Length,
            EffectRegistry.RelicClassNames.Distinct().Count());
        Assert.Equal(
            EffectRegistry.PowerClassNames.Length,
            EffectRegistry.PowerClassNames.Distinct().Count());
    }
}
