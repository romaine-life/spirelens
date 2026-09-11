using SpireLens.Core;
using Xunit;

namespace SpireLens.Core.Tests;

public class SchemaLoadingTests
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string FixturePath(string fileName) =>
        Path.Combine(RepoRoot, "Fixtures", "RunSchema", fileName);

    [Fact]
    public void HistoricalLoad_AcceptsLegacyV1Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v1-pooled-run.json"));

        Assert.NotNull(loaded);
        Assert.False(loaded!.SupportsResume);
        Assert.False(loaded.HasPerInstanceIdentity);
        Assert.Contains("historical data", loaded.CompatibilityNote!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CARD.STRIKE_KIN", loaded.Data.Aggregates.Keys);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV2Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v2-per-instance-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Contains("CARD.STRIKE_KIN#1", loaded.Data.Aggregates.Keys);
        Assert.Equal(1, loaded.Data.DefCounters["CARD.STRIKE_KIN"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV3Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v3-per-instance-effects-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var agg = loaded.Data.Aggregates["CARD.NECROBINDER_POWER#1"];
        var effect = agg.AppliedEffects["POWER.NECROBINDER_TRIGGER"];
        Assert.Equal("Necrobinder Trigger", effect.DisplayName);
        Assert.Equal(3, effect.TimesApplied);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV4Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v4-per-instance-effects-exhaust-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var agg = loaded.Data.Aggregates["CARD.NECROBINDER_POWER#1"];
        Assert.Equal(1, agg.TimesExhausted);
        Assert.Equal(9m, agg.AppliedEffects["POWER.NECROBINDER_TRIGGER"].TotalAmountApplied);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV5Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v5-per-instance-block-ledger-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var agg = loaded.Data.Aggregates["CARD.DEFEND_KIN#1"];
        Assert.Equal(6, agg.TotalBlockEffective);
        Assert.Equal(4, agg.TotalBlockWasted);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV6Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v6-per-instance-artifact-block-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Equal(6, loaded.Data.Aggregates["CARD.DEFEND_KIN#1"].TotalBlockEffective);
        var effect = loaded.Data.Aggregates["CARD.BASH_KIN#1"].AppliedEffects["POWER.WEAK"];
        Assert.Equal(1, effect.TimesBlockedByArtifact);
        Assert.Equal(2m, effect.TotalAmountBlockedByArtifact);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV7Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v7-per-instance-poison-damage-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var effect = loaded.Data.Aggregates["CARD.DEADLY_POISON#1"].AppliedEffects["POWER.POISON"];
        Assert.Equal(9m, effect.TotalTriggeredEffectiveDamage);
        Assert.Equal(3m, effect.TotalTriggeredOverkill);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV8Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v8-per-instance-regent-stars-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Equal(2, loaded.Data.Aggregates["CARD.VENERATE#1"].TotalStarsGenerated);
        Assert.Equal(2, loaded.Data.Aggregates["CARD.STARDUST#1"].TotalStarsSpent);
        Assert.Equal(2, loaded.Data.Events[1].StarsSpent);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV9BlockedDrawFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v9-per-instance-blocked-draw-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Equal(2, loaded.Data.Aggregates["CARD.POMMEL_STRIKE#1"].TimesCardsDrawBlocked);
        Assert.Equal(1, loaded.Data.Aggregates["CARD.POMMEL_STRIKE#1"].TimesCardsDrawn);
        Assert.Equal(0, loaded.Data.Aggregates["CARD.POMMEL_STRIKE#1"].TotalStarsGenerated);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV9ForgeFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v9-per-instance-forge-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Equal(9m, loaded.Data.Aggregates["CARD.REFINE_BLADE#1"].TotalForgeGenerated);
        Assert.Equal(5m, loaded.Data.Events[0].ForgeGained);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV10ForgeFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v10-per-instance-forge-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Equal(9m, loaded.Data.Aggregates["CARD.REFINE_BLADE#1"].TotalForgeGenerated);
        Assert.Equal(0, loaded.Data.Aggregates["CARD.REFINE_BLADE#1"].TimesCardsDrawBlocked);
        Assert.Equal(4m, loaded.Data.Events[2].ForgeGained);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV11Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v11-per-instance-no-draw-blocked-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var effect = loaded.Data.Aggregates["CARD.BATTLE_TRANCE#1"].AppliedEffects["POWER.NO_DRAW"];
        Assert.Equal(2, effect.TotalTriggeredCardsDrawBlocked);
        Assert.Equal(2, loaded.Data.Aggregates["CARD.POMMEL_STRIKE#1"].TimesCardsDrawBlocked);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV12Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v12-per-instance-draw-attempt-gap-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var blocker = loaded.Data.Aggregates["CARD.BATTLE_TRANCE#1"].AppliedEffects["POWER.NO_DRAW"];
        Assert.Equal(3, blocker.TotalTriggeredCardsDrawBlocked);
        Assert.Equal(3, loaded.Data.Aggregates["CARD.BATTLE_TRANCE#2"].TimesCardsDrawAttempted);
        Assert.Equal(0, loaded.Data.Aggregates["CARD.BATTLE_TRANCE#2"].TimesCardsDrawn);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV13Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v13-per-instance-blocked-draw-reasons-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var blocker = loaded.Data.Aggregates["CARD.BATTLE_TRANCE#1"].AppliedEffects["POWER.NO_DRAW"];
        Assert.Equal(3, blocker.TotalTriggeredCardsDrawBlocked);
        var reason = loaded.Data.Aggregates["CARD.BATTLE_TRANCE#2"].BlockedDrawReasons["effect:POWER.NO_DRAW"];
        Assert.Equal("No Draw", reason.DisplayName);
        Assert.Equal(3, reason.Count);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV14Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v14-per-instance-make-it-so-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Equal(9m, loaded.Data.Aggregates["CARD.REFINE_BLADE#1"].TotalForgeGenerated);
        Assert.Equal(2, loaded.Data.Aggregates["CARD.MAKE_IT_SO#1"].TimesSummonedToHand);
        Assert.Equal(4m, loaded.Data.Events[2].ForgeGained);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV15Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v15-bag-of-marbles-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.BAG_OF_MARBLES"];
        Assert.Equal(5, relicAgg.EnemiesAffected);
        Assert.Equal(5, relicAgg.VulnerableApplied);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV16Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v16-red-mask-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.RED_MASK"];
        Assert.Equal(3, relicAgg.EnemiesAffected);
        Assert.Equal(3, relicAgg.WeakApplied);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLegacyResumableV17Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v17-orichalcum-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.ORICHALCUM"];
        Assert.Equal(12, relicAgg.AdditionalBlockGained);
    }

    [Fact]
    public void HistoricalLoad_AcceptsV18Fixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v18-pocketwatch-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        Assert.Equal(12, loaded.Data.RelicAggregates["RELIC.ORICHALCUM"].AdditionalBlockGained);
        Assert.Equal(6, loaded.Data.RelicAggregates["RELIC.POCKETWATCH"].AdditionalCardsDrawn);
    }

    [Fact]
    public void HistoricalLoad_AcceptsV19BookRepairKnifeFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v19-book-repair-knife-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        Assert.Equal(3, loaded.Data.RelicAggregates["RELIC.BOOK_REPAIR_KNIFE"].DoomDeathTriggers);
        Assert.Equal(3, loaded.Data.RelicAggregates["RELIC.BOOK_REPAIR_KNIFE"].DoomKills);
        Assert.Equal(18m, loaded.Data.RelicAggregates["RELIC.BOOK_REPAIR_KNIFE"].TotalHealingAttempted);
        Assert.Equal(10m, loaded.Data.RelicAggregates["RELIC.BOOK_REPAIR_KNIFE"].TotalHealingRestored);
        Assert.Equal(8m, loaded.Data.RelicAggregates["RELIC.BOOK_REPAIR_KNIFE"].TotalHealingLost);
        Assert.Equal(6m, loaded.Data.RelicAggregates["RELIC.BOOK_REPAIR_KNIFE"].HealingLostReasons["full_hp"].Amount);
        Assert.Equal(2m, loaded.Data.RelicAggregates["RELIC.BOOK_REPAIR_KNIFE"].HealingLostReasons["other"].Amount);
    }

    [Fact]
    public void HistoricalLoad_AcceptsV20BoneFluteFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v20-bone-flute-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        Assert.Equal(2, loaded.Data.RelicAggregates["RELIC.BONE_FLUTE"].BoneFluteTriggers);
        Assert.Equal(14, loaded.Data.RelicAggregates["RELIC.BONE_FLUTE"].AdditionalBlockGained);
    }

    [Fact]
    public void HistoricalLoad_AcceptsV21UnleashOstyHpFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v21-unleash-osty-hp-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var agg = loaded.Data.Aggregates["CARD.UNLEASH#1"];
        Assert.Equal(30, agg.TotalOstyHpAttackBonus);
        Assert.Equal(3, agg.TimesOstyHpAttackBonusApplied);
    }

    [Fact]
    public void HistoricalLoad_AcceptsV22OstySummonBodyFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v22-osty-summon-body-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var summonAgg = loaded.Data.Aggregates["CARD.SUMMON_FORTH#1"];
        Assert.Equal(2, summonAgg.TimesOstySummoned);
        Assert.Equal(18m, summonAgg.TotalOstyHpSummoned);
        Assert.Equal(18m, loaded.Data.MetaStats.TotalOstyHpSummoned);
        Assert.Equal(11m, loaded.Data.MetaStats.TotalOstyDamageAbsorbed);
    }

    [Fact]
    public void HistoricalLoad_AcceptsV23ReplayExtraPlaysFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v23-replay-extra-plays-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var agg = loaded.Data.Aggregates["CARD.STRIKE_KIN#1"];
        Assert.Equal(5, agg.Plays);
        Assert.Equal(2, agg.TimesReplayExtraPlayed);
    }

    [Fact]
    public void HistoricalLoad_AcceptsV24ReplaySourceBreakdownFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v24-replay-source-breakdown-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var agg = loaded.Data.Aggregates["CARD.STRIKE_KIN#1"];
        Assert.Equal(6, agg.Plays);
        Assert.Equal(3, agg.TimesReplayExtraPlayed);
        Assert.Equal(1, agg.ReplayExtraPlayReasons["replay"].Count);
        Assert.Equal("Replay", agg.ReplayExtraPlayReasons["replay"].DisplayName);
        Assert.Equal(2, agg.ReplayExtraPlayReasons["power:POWER.BURST"].Count);
        Assert.Equal("Burst", agg.ReplayExtraPlayReasons["power:POWER.BURST"].DisplayName);
    }

    [Fact]
    public void HistoricalLoad_AcceptsV25ReplayOutcomesFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v25-replay-outcomes-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var agg = loaded.Data.Aggregates["CARD.STRIKE_KIN#1"];
        Assert.Equal(6, agg.Plays);
        Assert.Equal(4, agg.TimesReplayExtraPlanned);
        Assert.Equal(3, agg.TimesReplayExtraPlayed);
        Assert.Equal(1, agg.TimesReplayAttackNoDamage);
        Assert.Equal(1, agg.ReplayExtraPlayPlannedReasons["replay"].Count);
        Assert.Equal(3, agg.ReplayExtraPlayPlannedReasons["power:POWER.BURST"].Count);
        Assert.Equal(2, agg.ReplayExtraPlayReasons["power:POWER.BURST"].Count);
        Assert.Equal(1, agg.ReplayAttackNoDamageReasons["power:POWER.BURST"].Count);
    }

    [Fact]
    public void HistoricalLoad_AcceptsMealTicketRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("meal-ticket-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.MEAL_TICKET"];
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(30m, relicAgg.TotalHealingAttempted);
        Assert.Equal(18m, relicAgg.TotalHealingRestored);
        Assert.Equal(12m, relicAgg.TotalHealingLost);
        Assert.Equal(12m, relicAgg.HealingLostReasons["full_hp"].Amount);
    }

    [Fact]
    public void HistoricalLoad_AcceptsBurningBloodRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("burning-blood-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.BURNING_BLOOD"];
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(12m, relicAgg.TotalHealingAttempted);
        Assert.Equal(9m, relicAgg.TotalHealingRestored);
        Assert.Equal(3m, relicAgg.TotalHealingLost);
        Assert.Equal(3m, relicAgg.HealingLostReasons["full_hp"].Amount);
    }

    [Fact]
    public void HistoricalLoad_AcceptsChosenCheeseRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("chosen-cheese-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.CHOSEN_CHEESE"];
        Assert.Equal(3m, relicAgg.MaxHpGained);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Null(relicAgg.NewMaxHp);
    }

    [Fact]
    public void HistoricalLoad_AcceptsStrawberryRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("strawberry-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.STRAWBERRY"];
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(7m, relicAgg.MaxHpGained);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(77m, relicAgg.NewMaxHp);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPearRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("pear-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.PEAR"];
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(10m, relicAgg.MaxHpGained);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(80m, relicAgg.NewMaxHp);
    }

    [Fact]
    public void HistoricalLoad_AcceptsMangoRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("mango-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.MANGO"];
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(14m, relicAgg.MaxHpGained);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(84m, relicAgg.NewMaxHp);
    }

    [Fact]
    public void HistoricalLoad_AcceptsNutritiousOysterRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("nutritious-oyster-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.NUTRITIOUS_OYSTER"];
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(11m, relicAgg.MaxHpGained);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(81m, relicAgg.NewMaxHp);
    }

    [Fact]
    public void HistoricalLoad_AcceptsWhiteBeastStatueRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("white-beast-statue-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.WHITE_BEAST_STATUE"];
        Assert.Equal(5, relicAgg.PotionsGained);
        Assert.Equal(2, relicAgg.CommonPotionsGained);
        Assert.Equal(2, relicAgg.UncommonPotionsGained);
        Assert.Equal(1, relicAgg.RarePotionsGained);
        Assert.Equal(3, relicAgg.PotionsSkipped);
    }

    [Fact]
    public void HistoricalLoad_AcceptsAlchemizeCardFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("alchemize-card-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var cardAgg = loaded.Data.Aggregates["CARD.ALCHEMIZE#1"];
        Assert.Equal(5, cardAgg.PotionsGained);
        Assert.Equal(2, cardAgg.CommonPotionsGained);
        Assert.Equal(2, cardAgg.UncommonPotionsGained);
        Assert.Equal(1, cardAgg.RarePotionsGained);
        Assert.Equal(3, cardAgg.PotionsSkipped);
    }

    [Fact]
    public void HistoricalLoad_AcceptsJackOfAllTradesCardFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("jack-of-all-trades-card-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var cardAgg = loaded.Data.Aggregates["CARD.JACK_OF_ALL_TRADES#1"];
        Assert.Equal(5, cardAgg.JackColorlessCardsAdded);
        Assert.Equal(3, cardAgg.JackUncommonCardsAdded);
        Assert.Equal(2, cardAgg.JackRareCardsAdded);
        Assert.Equal(2, cardAgg.JackAttacksAdded);
        Assert.Equal(2, cardAgg.JackSkillsAdded);
        Assert.Equal(1, cardAgg.JackPowersAdded);
        Assert.Equal(7, cardAgg.JackAddedCardCostTotal);
    }

    [Fact]
    public void HistoricalLoad_AcceptsDiscoveryCardFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("discovery-card-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var cardAgg = loaded.Data.Aggregates["CARD.DISCOVERY#1"];
        Assert.Equal(5, cardAgg.DiscoveryCardsPicked);
        Assert.Equal(2, cardAgg.DiscoveryCommonCardsPicked);
        Assert.Equal(2, cardAgg.DiscoveryUncommonCardsPicked);
        Assert.Equal(1, cardAgg.DiscoveryRareCardsPicked);
        Assert.Equal(2, cardAgg.DiscoveryAttacksPicked);
        Assert.Equal(2, cardAgg.DiscoverySkillsPicked);
        Assert.Equal(1, cardAgg.DiscoveryPowersPicked);
        Assert.Equal(7, cardAgg.DiscoveryEnergyDiscountTotal);
        Assert.Equal(0, cardAgg.DiscoveryCardsOffered);
    }

    [Fact]
    public void HistoricalLoad_AcceptsDiscoveryCardsOfferedFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("discovery-cards-offered-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertDiscoveryOfferedFixture(loaded.Data.Aggregates["CARD.DISCOVERY#1"]);
    }

    private static void AssertDiscoveryOfferedFixture(CardAggregate cardAgg)
    {
        Assert.Equal(15, cardAgg.DiscoveryCardsOffered);
        Assert.Equal(8, cardAgg.DiscoveryCommonCardsOffered);
        Assert.Equal(5, cardAgg.DiscoveryUncommonCardsOffered);
        Assert.Equal(2, cardAgg.DiscoveryRareCardsOffered);
        Assert.Equal(6, cardAgg.DiscoveryAttacksOffered);
        Assert.Equal(7, cardAgg.DiscoverySkillsOffered);
        Assert.Equal(2, cardAgg.DiscoveryPowersOffered);
        Assert.Equal(5, cardAgg.DiscoveryCardsPicked);
        Assert.Equal(2, cardAgg.DiscoveryCommonCardsPicked);
        Assert.Equal(2, cardAgg.DiscoveryUncommonCardsPicked);
        Assert.Equal(1, cardAgg.DiscoveryRareCardsPicked);
        Assert.Equal(2, cardAgg.DiscoveryAttacksPicked);
        Assert.Equal(2, cardAgg.DiscoverySkillsPicked);
        Assert.Equal(1, cardAgg.DiscoveryPowersPicked);
        Assert.Equal(7, cardAgg.DiscoveryEnergyDiscountTotal);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSplashCardFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("splash-card-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertSplashFixture(loaded.Data.Aggregates["CARD.SPLASH#1"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsDrainPowerCardFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("drain-power-card-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertDrainPowerFixture(loaded.Data.Aggregates["CARD.DRAIN_POWER#1"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsAllForOneCardFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("all-for-one-card-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertAllForOneFixture(loaded.Data.Aggregates["CARD.ALL_FOR_ONE#1"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsJugglingPowerFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("juggling-power-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertJugglingPowerFixture(
            loaded.Data.MetaStats.PowerAggregates["POWER.JUGGLING"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsViciousPowerFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("vicious-power-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertViciousPowerFixture(
            loaded.Data.MetaStats.PowerAggregates["POWER.VICIOUS"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsOutbreakCardFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("outbreak-card-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertOutbreakCardFixture(
            loaded.Data.Aggregates["CARD.OUTBREAK#1"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsDarkEmbracePowerFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("dark-embrace-power-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertDarkEmbracePowerFixture(
            loaded.Data.MetaStats.PowerAggregates["POWER.DARK_EMBRACE"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsConsumingShadowPowerFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("consuming-shadow-power-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertConsumingShadowPowerFixture(
            loaded.Data.MetaStats.PowerAggregates["POWER.CONSUMING_SHADOW"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsMetaPowerRegistryFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("meta-power-registry-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertMetaPowerRegistryFixture(loaded.Data.MetaStats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsStampedePowerFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("stampede-power-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertStampedePowerFixture(
            loaded.Data.MetaStats.PowerAggregates["POWER.STAMPEDE"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsAggressionPowerFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("aggression-power-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertAggressionPowerFixture(
            loaded.Data.MetaStats.PowerAggregates["POWER.AGGRESSION"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsRupturePowerFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("rupture-power-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertRupturePowerFixture(
            loaded.Data.MetaStats.PowerAggregates["POWER.RUPTURE"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsFeelNoPainPowerFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("feel-no-pain-power-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertFeelNoPainPowerFixture(
            loaded.Data.MetaStats.PowerAggregates["POWER.FEEL_NO_PAIN"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsBufferPowerFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("buffer-power-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertBufferPowerFixture(
            loaded.Data.MetaStats.PowerAggregates["POWER.BUFFER_POWER"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsBufferChargeLedgerFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("buffer-charge-ledger-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertBufferChargeLedgerFixture(loaded.Data);
    }

    [Fact]
    public void HistoricalLoad_AcceptsEntropyPowerFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("entropy-power-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertEntropyPowerFixture(
            loaded.Data.MetaStats.PowerAggregates["POWER.ENTROPY"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsDanseMacabrePowerFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("danse-macabre-power-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertDanseMacabrePowerFixture(
            loaded.Data.MetaStats.PowerAggregates["POWER.DANSE_MACABRE"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsUnrelentingFreeAttackPowerFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("unrelenting-free-attack-power-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertUnrelentingFreeAttackPowerFixture(
            loaded.Data.MetaStats.PowerAggregates["POWER.FREE_ATTACK_POWER"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPounceFreeSkillPowerFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("pounce-free-skill-power-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        AssertPounceFreeSkillPowerFixture(
            loaded.Data.MetaStats.PowerAggregates["POWER.FREE_SKILL_POWER"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsDebtCardFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("debt-card-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var cardAgg = loaded.Data.Aggregates["CARD.DEBT#1"];
        Assert.Equal(4, cardAgg.DebtTriggers);
        Assert.Equal(13, cardAgg.DebtGoldLost);
        Assert.Equal(7, cardAgg.DebtGoldLossBlocked);
    }

    [Fact]
    public void HistoricalLoad_AcceptsNormalityCardFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("normality-card-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var cardAgg = loaded.Data.Aggregates["CARD.NORMALITY#1"];
        Assert.Equal(4, cardAgg.NormalityTurnsEndedInHand);
        Assert.Equal(7, cardAgg.NormalityExcessEnergyAtTurnEndTotal);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSealOfGoldRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("seal-of-gold-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.SEAL_OF_GOLD"];
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(17, relicAgg.GoldLost);
        Assert.Equal(3, relicAgg.GoldLossBlocked);
        Assert.Equal(3, relicAgg.EnergyGenerated);
        Assert.Equal(2, relicAgg.EnergyGeneratedCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPhylacteryRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("phylactery-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var boundAgg = loaded.Data.RelicAggregates["RELIC.BOUND_PHYLACTERY"];
        Assert.Equal(3, boundAgg.Activations);
        Assert.Equal(12m, boundAgg.TotalOstyHpSummoned);
        var unboundAgg = loaded.Data.RelicAggregates["RELIC.PHYLACTERY_UNBOUND"];
        Assert.Equal(4, unboundAgg.Activations);
        Assert.Equal(20m, unboundAgg.TotalOstyHpSummoned);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPhylacteryOstyBodyFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("phylactery-osty-body-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var stats = loaded.Data.MetaStats;
        Assert.Equal(42m, stats.TotalOstyHpSummoned);
        Assert.Equal(27m, stats.TotalOstyHpWhenUnleashPlayed);
        Assert.Equal(30m, stats.TotalOstyDamageAbsorbed);
        Assert.Equal(6, stats.OstyBodyTurns);
        Assert.Equal(2, stats.OstyBodyCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsEnemyStatusPollutionFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("enemy-status-pollution-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var hauntedShipAgg = loaded.Data.EnemyAggregates["MONSTER.HAUNTED_SHIP"];
        Assert.Equal(2, hauntedShipAgg.DamageInstances);
        Assert.Equal(16, hauntedShipAgg.DamageAttempted);
        Assert.Equal(6, hauntedShipAgg.DamageBlocked);
        Assert.Equal(10, hauntedShipAgg.DamageDealt);
        Assert.Equal(3, hauntedShipAgg.StatusCardsAdded);
        Assert.Equal(1, hauntedShipAgg.StatusCardsAddedToHand);
        Assert.Equal(2, hauntedShipAgg.StatusCardsAddedToDraw);
        Assert.Equal(3, hauntedShipAgg.StatusCardsById["CARD.DAZED"].Count);
    }

    [Fact]
    public void ResumableLoad_RejectsLegacyV1Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v1-pooled-run.json"));

        Assert.Null(resumed);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV2Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v2-per-instance-run.json"));

        Assert.NotNull(resumed);
        Assert.Contains("CARD.ENERGY_SURGE#1", resumed!.Aggregates.Keys);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV3Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v3-per-instance-effects-run.json"));

        Assert.NotNull(resumed);
        var effect = resumed!.Aggregates["CARD.NECROBINDER_POWER#1"].AppliedEffects["POWER.NECROBINDER_TRIGGER"];
        Assert.Equal(3m, effect.TotalAmountApplied);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV4Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v4-per-instance-effects-exhaust-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(1, resumed!.Aggregates["CARD.NECROBINDER_POWER#1"].TimesExhausted);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV5Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v5-per-instance-block-ledger-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(6, resumed!.Aggregates["CARD.DEFEND_KIN#1"].TotalBlockEffective);
        Assert.Equal(4, resumed.Aggregates["CARD.DEFEND_KIN#1"].TotalBlockWasted);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV6Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v6-per-instance-artifact-block-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(6, resumed!.Aggregates["CARD.DEFEND_KIN#1"].TotalBlockEffective);
        var effect = resumed.Aggregates["CARD.BASH_KIN#1"].AppliedEffects["POWER.WEAK"];
        Assert.Equal(1, effect.TimesBlockedByArtifact);
        Assert.Equal(2m, effect.TotalAmountBlockedByArtifact);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV7Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v7-per-instance-poison-damage-run.json"));

        Assert.NotNull(resumed);
        var effect = resumed!.Aggregates["CARD.DEADLY_POISON#1"].AppliedEffects["POWER.POISON"];
        Assert.Equal(9m, effect.TotalTriggeredEffectiveDamage);
        Assert.Equal(3m, effect.TotalTriggeredOverkill);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV8Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v8-per-instance-regent-stars-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(2, resumed!.Aggregates["CARD.VENERATE#1"].TotalStarsGenerated);
        Assert.Equal(2, resumed.Aggregates["CARD.STARDUST#1"].TotalStarsSpent);
        Assert.Equal(1, resumed.Aggregates["CARD.VENERATE#1"].TimesDrawn);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV9BlockedDrawFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v9-per-instance-blocked-draw-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(2, resumed!.Aggregates["CARD.POMMEL_STRIKE#1"].TimesCardsDrawBlocked);
        Assert.Equal(1, resumed.Aggregates["CARD.POMMEL_STRIKE#1"].TimesCardsDrawn);
        Assert.Equal(0, resumed.Aggregates["CARD.POMMEL_STRIKE#1"].TotalStarsSpent);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV9ForgeFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v9-per-instance-forge-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(9m, resumed!.Aggregates["CARD.REFINE_BLADE#1"].TotalForgeGenerated);
        Assert.Equal(0, resumed.Aggregates["CARD.REFINE_BLADE#1"].TimesCardsDrawBlocked);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV10ForgeFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v10-per-instance-forge-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(9m, resumed!.Aggregates["CARD.REFINE_BLADE#1"].TotalForgeGenerated);
        Assert.Equal(0, resumed.Aggregates["CARD.REFINE_BLADE#1"].TimesCardsDrawBlocked);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV11Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v11-per-instance-no-draw-blocked-run.json"));

        Assert.NotNull(resumed);
        var effect = resumed!.Aggregates["CARD.BATTLE_TRANCE#1"].AppliedEffects["POWER.NO_DRAW"];
        Assert.Equal(2, effect.TotalTriggeredCardsDrawBlocked);
        Assert.Equal(2, resumed.Aggregates["CARD.POMMEL_STRIKE#1"].TimesCardsDrawBlocked);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV12Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v12-per-instance-draw-attempt-gap-run.json"));

        Assert.NotNull(resumed);
        var blocker = resumed!.Aggregates["CARD.BATTLE_TRANCE#1"].AppliedEffects["POWER.NO_DRAW"];
        Assert.Equal(3, blocker.TotalTriggeredCardsDrawBlocked);
        Assert.Equal(3, resumed.Aggregates["CARD.BATTLE_TRANCE#2"].TimesCardsDrawAttempted);
        Assert.Equal(0, resumed.Aggregates["CARD.BATTLE_TRANCE#2"].TimesCardsDrawn);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV13Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v13-per-instance-blocked-draw-reasons-run.json"));

        Assert.NotNull(resumed);
        var blocker = resumed!.Aggregates["CARD.BATTLE_TRANCE#1"].AppliedEffects["POWER.NO_DRAW"];
        Assert.Equal(3, blocker.TotalTriggeredCardsDrawBlocked);
        var reason = resumed.Aggregates["CARD.BATTLE_TRANCE#2"].BlockedDrawReasons["effect:POWER.NO_DRAW"];
        Assert.Equal("No Draw", reason.DisplayName);
        Assert.Equal(3, reason.Count);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV14Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v14-per-instance-make-it-so-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(9m, resumed!.Aggregates["CARD.REFINE_BLADE#1"].TotalForgeGenerated);
        Assert.Equal(2, resumed.Aggregates["CARD.MAKE_IT_SO#1"].TimesSummonedToHand);
        Assert.Equal(3, resumed.Aggregates["CARD.MAKE_IT_SO#1"].TimesDrawn);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV15Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v15-bag-of-marbles-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.BAG_OF_MARBLES"];
        Assert.Equal(5, relicAgg.EnemiesAffected);
        Assert.Equal(5, relicAgg.VulnerableApplied);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV16Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v16-red-mask-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.RED_MASK"];
        Assert.Equal(3, relicAgg.EnemiesAffected);
        Assert.Equal(3, relicAgg.WeakApplied);
    }

    [Fact]
    public void ResumableLoad_AcceptsLegacyResumableV17Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v17-orichalcum-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.ORICHALCUM"];
        Assert.Equal(12, relicAgg.AdditionalBlockGained);
    }

    [Fact]
    public void ResumableLoad_AcceptsV18Fixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v18-pocketwatch-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(12, resumed!.RelicAggregates["RELIC.ORICHALCUM"].AdditionalBlockGained);
        Assert.Equal(6, resumed.RelicAggregates["RELIC.POCKETWATCH"].AdditionalCardsDrawn);
    }

    [Fact]
    public void ResumableLoad_AcceptsV19BookRepairKnifeFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v19-book-repair-knife-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(3, resumed!.RelicAggregates["RELIC.BOOK_REPAIR_KNIFE"].DoomDeathTriggers);
        Assert.Equal(3, resumed!.RelicAggregates["RELIC.BOOK_REPAIR_KNIFE"].DoomKills);
        Assert.Equal(18m, resumed!.RelicAggregates["RELIC.BOOK_REPAIR_KNIFE"].TotalHealingAttempted);
        Assert.Equal(10m, resumed!.RelicAggregates["RELIC.BOOK_REPAIR_KNIFE"].TotalHealingRestored);
        Assert.Equal(8m, resumed!.RelicAggregates["RELIC.BOOK_REPAIR_KNIFE"].TotalHealingLost);
        Assert.Equal(6m, resumed!.RelicAggregates["RELIC.BOOK_REPAIR_KNIFE"].HealingLostReasons["full_hp"].Amount);
        Assert.Equal(2m, resumed!.RelicAggregates["RELIC.BOOK_REPAIR_KNIFE"].HealingLostReasons["other"].Amount);
    }

    [Fact]
    public void HistoricalLoad_AcceptsV19HappyFlowerFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v19-happy-flower-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.HAPPY_FLOWER"];
        Assert.Equal(3, relicAgg.EnergyGenerated);
    }

    [Fact]
    public void ResumableLoad_AcceptsV19HappyFlowerFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v19-happy-flower-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.HAPPY_FLOWER"];
        Assert.Equal(3, relicAgg.EnergyGenerated);
    }

    [Fact]
    public void HistoricalLoad_AcceptsHappyFlowerEnergyAverageFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("happy-flower-energy-average-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.HAPPY_FLOWER"];
        Assert.Equal(5, relicAgg.EnergyGenerated);
        Assert.Equal(3, relicAgg.EnergyGeneratedCombats);
    }

    [Fact]
    public void ResumableLoad_AcceptsHappyFlowerEnergyAverageFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("happy-flower-energy-average-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.HAPPY_FLOWER"];
        Assert.Equal(5, relicAgg.EnergyGenerated);
        Assert.Equal(3, relicAgg.EnergyGeneratedCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsNunchakuRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("nunchaku-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.NUNCHAKU"];
        Assert.Equal(18, relicAgg.NunchakuAttacksPlayed);
        Assert.Equal(3, relicAgg.EnergyGenerated);
        Assert.Equal(4, relicAgg.EnergyGeneratedCombats);
        Assert.Equal(2, relicAgg.NunchakuCombatsEndedOn8Charges);
        Assert.Equal(1, relicAgg.NunchakuCombatsEndedOn9Charges);
        Assert.Equal(34, relicAgg.NunchakuCombatEndChargeTotal);
    }

    [Fact]
    public void ResumableLoad_AcceptsNunchakuRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("nunchaku-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.NUNCHAKU"];
        Assert.Equal(18, relicAgg.NunchakuAttacksPlayed);
        Assert.Equal(3, relicAgg.EnergyGenerated);
        Assert.Equal(4, relicAgg.EnergyGeneratedCombats);
        Assert.Equal(2, relicAgg.NunchakuCombatsEndedOn8Charges);
        Assert.Equal(1, relicAgg.NunchakuCombatsEndedOn9Charges);
        Assert.Equal(34, relicAgg.NunchakuCombatEndChargeTotal);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPenNibRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("pen-nib-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.PEN_NIB"];
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(27, relicAgg.TotalDamageAttempted);
        Assert.Equal(9, relicAgg.PenNibAttacksPlayed);
        Assert.Equal(2, relicAgg.PenNibTurnsEndedOn8Charges);
        Assert.Equal(1, relicAgg.PenNibTurnsEndedOn9Charges);
        Assert.Equal(34, relicAgg.PenNibTurnEndChargeTotal);
        Assert.Equal(5, relicAgg.PenNibTurnEndChargeCount);
    }

    [Fact]
    public void ResumableLoad_AcceptsPenNibRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("pen-nib-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.PEN_NIB"];
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(27, relicAgg.TotalDamageAttempted);
        Assert.Equal(9, relicAgg.PenNibAttacksPlayed);
        Assert.Equal(2, relicAgg.PenNibTurnsEndedOn8Charges);
        Assert.Equal(1, relicAgg.PenNibTurnsEndedOn9Charges);
        Assert.Equal(34, relicAgg.PenNibTurnEndChargeTotal);
        Assert.Equal(5, relicAgg.PenNibTurnEndChargeCount);
    }

    [Fact]
    public void HistoricalLoad_AcceptsIronClubRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("iron-club-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.IRON_CLUB"];
        Assert.Equal(7, relicAgg.AdditionalCardsDrawn);
        Assert.Equal(4, relicAgg.IronClubCombats);
        Assert.Equal(1, relicAgg.IronClubCombatsEndedOn0Charges);
        Assert.Equal(1, relicAgg.IronClubCombatsEndedOn1Charges);
        Assert.Equal(0, relicAgg.IronClubCombatsEndedOn2Charges);
        Assert.Equal(2, relicAgg.IronClubCombatsEndedOn3Charges);
        Assert.Equal(7, relicAgg.IronClubCombatEndChargeTotal);
        Assert.Equal(4, relicAgg.IronClubCombatEndChargeCount);
    }

    [Fact]
    public void ResumableLoad_AcceptsIronClubRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("iron-club-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.IRON_CLUB"];
        Assert.Equal(7, relicAgg.AdditionalCardsDrawn);
        Assert.Equal(4, relicAgg.IronClubCombats);
        Assert.Equal(1, relicAgg.IronClubCombatsEndedOn0Charges);
        Assert.Equal(1, relicAgg.IronClubCombatsEndedOn1Charges);
        Assert.Equal(0, relicAgg.IronClubCombatsEndedOn2Charges);
        Assert.Equal(2, relicAgg.IronClubCombatsEndedOn3Charges);
        Assert.Equal(7, relicAgg.IronClubCombatEndChargeTotal);
        Assert.Equal(4, relicAgg.IronClubCombatEndChargeCount);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPendulumCombatEndChargeFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("pendulum-combat-end-charge-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.PENDULUM"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(6, relicAgg.AdditionalCardsDrawn);
        Assert.Equal(4, relicAgg.PendulumCombats);
        Assert.Equal(1, relicAgg.PendulumCombatsEndedOn0Charges);
        Assert.Equal(1, relicAgg.PendulumCombatsEndedOn1Charge);
        Assert.Equal(2, relicAgg.PendulumCombatsEndedOn2Charges);
        Assert.Equal(5, relicAgg.PendulumCombatEndChargeTotal);
        Assert.Equal(4, relicAgg.PendulumCombatEndChargeCount);

        // Pre-dates activation-turn tracking; missing fields default to zero.
        Assert.Equal(0, relicAgg.PendulumActivationTurnTotal);
        Assert.Equal(0, relicAgg.PendulumActivationTurnSamples);
    }

    [Fact]
    public void ResumableLoad_AcceptsPendulumCombatEndChargeFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("pendulum-combat-end-charge-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.PENDULUM"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(6, relicAgg.AdditionalCardsDrawn);
        Assert.Equal(4, relicAgg.PendulumCombats);
        Assert.Equal(1, relicAgg.PendulumCombatsEndedOn0Charges);
        Assert.Equal(1, relicAgg.PendulumCombatsEndedOn1Charge);
        Assert.Equal(2, relicAgg.PendulumCombatsEndedOn2Charges);
        Assert.Equal(5, relicAgg.PendulumCombatEndChargeTotal);
        Assert.Equal(4, relicAgg.PendulumCombatEndChargeCount);
        Assert.Equal(0, relicAgg.PendulumActivationTurnTotal);
        Assert.Equal(0, relicAgg.PendulumActivationTurnSamples);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPendulumActivationTurnFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("pendulum-activation-turn-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.PENDULUM"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(3, relicAgg.AdditionalCardsDrawn);
        Assert.Equal(2, relicAgg.PendulumCombats);
        Assert.Equal(1, relicAgg.PendulumCombatsEndedOn0Charges);
        Assert.Equal(1, relicAgg.PendulumCombatsEndedOn1Charge);
        Assert.Equal(0, relicAgg.PendulumCombatsEndedOn2Charges);
        Assert.Equal(1, relicAgg.PendulumCombatEndChargeTotal);
        Assert.Equal(2, relicAgg.PendulumCombatEndChargeCount);
        Assert.Equal(12, relicAgg.PendulumActivationTurnTotal);
        Assert.Equal(3, relicAgg.PendulumActivationTurnSamples);
    }

    [Fact]
    public void ResumableLoad_AcceptsPendulumActivationTurnFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("pendulum-activation-turn-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.PENDULUM"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(2, relicAgg.PendulumCombats);
        Assert.Equal(12, relicAgg.PendulumActivationTurnTotal);
        Assert.Equal(3, relicAgg.PendulumActivationTurnSamples);
    }

    [Fact]
    public void HistoricalLoad_AcceptsV20CloakClaspFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("v20-cloak-clasp-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.CLOAK_CLASP"];
        Assert.Equal(21, relicAgg.AdditionalBlockGained);
        Assert.Equal(7, relicAgg.CloakClaspTurns);
        Assert.Equal(3, relicAgg.CloakClaspCombats);
    }

    [Fact]
    public void ResumableLoad_AcceptsV20BoneFluteFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v20-bone-flute-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(2, resumed!.RelicAggregates["RELIC.BONE_FLUTE"].BoneFluteTriggers);
        Assert.Equal(14, resumed!.RelicAggregates["RELIC.BONE_FLUTE"].AdditionalBlockGained);
    }

    [Fact]
    public void ResumableLoad_AcceptsV20CloakClaspFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v20-cloak-clasp-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.CLOAK_CLASP"];
        Assert.Equal(21, relicAgg.AdditionalBlockGained);
        Assert.Equal(7, relicAgg.CloakClaspTurns);
        Assert.Equal(3, relicAgg.CloakClaspCombats);
    }

    [Fact]
    public void ResumableLoad_AcceptsV21UnleashOstyHpFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v21-unleash-osty-hp-run.json"));

        Assert.NotNull(resumed);
        var agg = resumed!.Aggregates["CARD.UNLEASH#1"];
        Assert.Equal(30, agg.TotalOstyHpAttackBonus);
        Assert.Equal(3, agg.TimesOstyHpAttackBonusApplied);
    }

    [Fact]
    public void ResumableLoad_AcceptsV22OstySummonBodyFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v22-osty-summon-body-run.json"));

        Assert.NotNull(resumed);
        var summonAgg = resumed!.Aggregates["CARD.SUMMON_FORTH#1"];
        Assert.Equal(2, summonAgg.TimesOstySummoned);
        Assert.Equal(18m, summonAgg.TotalOstyHpSummoned);
        Assert.Equal(18m, resumed.MetaStats.TotalOstyHpSummoned);
        Assert.Equal(11m, resumed.MetaStats.TotalOstyDamageAbsorbed);
    }

    [Fact]
    public void ResumableLoad_AcceptsV23ReplayExtraPlaysFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v23-replay-extra-plays-run.json"));

        Assert.NotNull(resumed);
        var agg = resumed!.Aggregates["CARD.STRIKE_KIN#1"];
        Assert.Equal(5, agg.Plays);
        Assert.Equal(2, agg.TimesReplayExtraPlayed);
    }

    [Fact]
    public void ResumableLoad_AcceptsV24ReplaySourceBreakdownFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v24-replay-source-breakdown-run.json"));

        Assert.NotNull(resumed);
        var agg = resumed!.Aggregates["CARD.STRIKE_KIN#1"];
        Assert.Equal(6, agg.Plays);
        Assert.Equal(3, agg.TimesReplayExtraPlayed);
        Assert.Equal(1, agg.ReplayExtraPlayReasons["replay"].Count);
        Assert.Equal(2, agg.ReplayExtraPlayReasons["power:POWER.BURST"].Count);
    }

    [Fact]
    public void ResumableLoad_AcceptsV25ReplayOutcomesFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v25-replay-outcomes-run.json"));

        Assert.NotNull(resumed);
        var agg = resumed!.Aggregates["CARD.STRIKE_KIN#1"];
        Assert.Equal(6, agg.Plays);
        Assert.Equal(4, agg.TimesReplayExtraPlanned);
        Assert.Equal(3, agg.TimesReplayExtraPlayed);
        Assert.Equal(1, agg.TimesReplayAttackNoDamage);
        Assert.Equal(3, agg.ReplayExtraPlayPlannedReasons["power:POWER.BURST"].Count);
        Assert.Equal(2, agg.ReplayExtraPlayReasons["power:POWER.BURST"].Count);
        Assert.Equal(1, agg.ReplayAttackNoDamageReasons["power:POWER.BURST"].Count);
    }

    [Fact]
    public void ResumableLoad_AcceptsMealTicketRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("meal-ticket-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.MEAL_TICKET"];
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(30m, relicAgg.TotalHealingAttempted);
        Assert.Equal(18m, relicAgg.TotalHealingRestored);
        Assert.Equal(12m, relicAgg.TotalHealingLost);
        Assert.Equal(12m, relicAgg.HealingLostReasons["full_hp"].Amount);
    }

    [Fact]
    public void ResumableLoad_AcceptsBurningBloodRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("burning-blood-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.BURNING_BLOOD"];
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(12m, relicAgg.TotalHealingAttempted);
        Assert.Equal(9m, relicAgg.TotalHealingRestored);
        Assert.Equal(3m, relicAgg.TotalHealingLost);
        Assert.Equal(3m, relicAgg.HealingLostReasons["full_hp"].Amount);
    }

    [Fact]
    public void ResumableLoad_AcceptsChosenCheeseRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("chosen-cheese-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.CHOSEN_CHEESE"];
        Assert.Equal(3m, relicAgg.MaxHpGained);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Null(relicAgg.NewMaxHp);
    }

    [Fact]
    public void ResumableLoad_AcceptsStrawberryRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("strawberry-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.STRAWBERRY"];
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(7m, relicAgg.MaxHpGained);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(77m, relicAgg.NewMaxHp);
    }

    [Fact]
    public void ResumableLoad_AcceptsPearRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("pear-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.PEAR"];
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(10m, relicAgg.MaxHpGained);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(80m, relicAgg.NewMaxHp);
    }

    [Fact]
    public void ResumableLoad_AcceptsMangoRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("mango-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.MANGO"];
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(14m, relicAgg.MaxHpGained);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(84m, relicAgg.NewMaxHp);
    }

    [Fact]
    public void ResumableLoad_AcceptsNutritiousOysterRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("nutritious-oyster-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.NUTRITIOUS_OYSTER"];
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(11m, relicAgg.MaxHpGained);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(81m, relicAgg.NewMaxHp);
    }

    [Fact]
    public void ResumableLoad_AcceptsWhiteBeastStatueRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("white-beast-statue-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.WHITE_BEAST_STATUE"];
        Assert.Equal(5, relicAgg.PotionsGained);
        Assert.Equal(2, relicAgg.CommonPotionsGained);
        Assert.Equal(2, relicAgg.UncommonPotionsGained);
        Assert.Equal(1, relicAgg.RarePotionsGained);
        Assert.Equal(3, relicAgg.PotionsSkipped);
    }

    [Fact]
    public void ResumableLoad_AcceptsAlchemizeCardFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("alchemize-card-run.json"));

        Assert.NotNull(resumed);
        var cardAgg = resumed!.Aggregates["CARD.ALCHEMIZE#1"];
        Assert.Equal(5, cardAgg.PotionsGained);
        Assert.Equal(2, cardAgg.CommonPotionsGained);
        Assert.Equal(2, cardAgg.UncommonPotionsGained);
        Assert.Equal(1, cardAgg.RarePotionsGained);
        Assert.Equal(3, cardAgg.PotionsSkipped);
    }

    [Fact]
    public void ResumableLoad_AcceptsJackOfAllTradesCardFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("jack-of-all-trades-card-run.json"));

        Assert.NotNull(resumed);
        var cardAgg = resumed!.Aggregates["CARD.JACK_OF_ALL_TRADES#1"];
        Assert.Equal(5, cardAgg.JackColorlessCardsAdded);
        Assert.Equal(3, cardAgg.JackUncommonCardsAdded);
        Assert.Equal(2, cardAgg.JackRareCardsAdded);
        Assert.Equal(2, cardAgg.JackAttacksAdded);
        Assert.Equal(2, cardAgg.JackSkillsAdded);
        Assert.Equal(1, cardAgg.JackPowersAdded);
        Assert.Equal(7, cardAgg.JackAddedCardCostTotal);
    }

    [Fact]
    public void ResumableLoad_AcceptsDiscoveryCardFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("discovery-card-run.json"));

        Assert.NotNull(resumed);
        var cardAgg = resumed!.Aggregates["CARD.DISCOVERY#1"];
        Assert.Equal(5, cardAgg.DiscoveryCardsPicked);
        Assert.Equal(2, cardAgg.DiscoveryCommonCardsPicked);
        Assert.Equal(2, cardAgg.DiscoveryUncommonCardsPicked);
        Assert.Equal(1, cardAgg.DiscoveryRareCardsPicked);
        Assert.Equal(2, cardAgg.DiscoveryAttacksPicked);
        Assert.Equal(2, cardAgg.DiscoverySkillsPicked);
        Assert.Equal(1, cardAgg.DiscoveryPowersPicked);
        Assert.Equal(7, cardAgg.DiscoveryEnergyDiscountTotal);
        Assert.Equal(0, cardAgg.DiscoveryCardsOffered);
    }

    [Fact]
    public void ResumableLoad_AcceptsDiscoveryCardsOfferedFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("discovery-cards-offered-run.json"));

        Assert.NotNull(resumed);
        AssertDiscoveryOfferedFixture(resumed!.Aggregates["CARD.DISCOVERY#1"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsSplashCardFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("splash-card-run.json"));

        Assert.NotNull(resumed);
        AssertSplashFixture(resumed!.Aggregates["CARD.SPLASH#1"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsDrainPowerCardFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("drain-power-card-run.json"));

        Assert.NotNull(resumed);
        AssertDrainPowerFixture(resumed!.Aggregates["CARD.DRAIN_POWER#1"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsAllForOneCardFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("all-for-one-card-run.json"));

        Assert.NotNull(resumed);
        AssertAllForOneFixture(resumed!.Aggregates["CARD.ALL_FOR_ONE#1"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsJugglingPowerFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("juggling-power-run.json"));

        Assert.NotNull(resumed);
        AssertJugglingPowerFixture(
            resumed!.MetaStats.PowerAggregates["POWER.JUGGLING"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsViciousPowerFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("vicious-power-run.json"));

        Assert.NotNull(resumed);
        AssertViciousPowerFixture(
            resumed!.MetaStats.PowerAggregates["POWER.VICIOUS"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsOutbreakCardFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("outbreak-card-run.json"));

        Assert.NotNull(resumed);
        AssertOutbreakCardFixture(
            resumed!.Aggregates["CARD.OUTBREAK#1"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsDarkEmbracePowerFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("dark-embrace-power-run.json"));

        Assert.NotNull(resumed);
        AssertDarkEmbracePowerFixture(
            resumed!.MetaStats.PowerAggregates["POWER.DARK_EMBRACE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsConsumingShadowPowerFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("consuming-shadow-power-run.json"));

        Assert.NotNull(resumed);
        AssertConsumingShadowPowerFixture(
            resumed!.MetaStats.PowerAggregates["POWER.CONSUMING_SHADOW"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsMetaPowerRegistryFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("meta-power-registry-run.json"));

        Assert.NotNull(resumed);
        AssertMetaPowerRegistryFixture(resumed!.MetaStats);
    }

    [Fact]
    public void ResumableLoad_AcceptsStampedePowerFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("stampede-power-run.json"));

        Assert.NotNull(resumed);
        AssertStampedePowerFixture(
            resumed!.MetaStats.PowerAggregates["POWER.STAMPEDE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsAggressionPowerFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("aggression-power-run.json"));

        Assert.NotNull(resumed);
        AssertAggressionPowerFixture(
            resumed!.MetaStats.PowerAggregates["POWER.AGGRESSION"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsRupturePowerFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("rupture-power-run.json"));

        Assert.NotNull(resumed);
        AssertRupturePowerFixture(
            resumed!.MetaStats.PowerAggregates["POWER.RUPTURE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsFeelNoPainPowerFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("feel-no-pain-power-run.json"));

        Assert.NotNull(resumed);
        AssertFeelNoPainPowerFixture(
            resumed!.MetaStats.PowerAggregates["POWER.FEEL_NO_PAIN"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsBufferPowerFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("buffer-power-run.json"));

        Assert.NotNull(resumed);
        AssertBufferPowerFixture(
            resumed!.MetaStats.PowerAggregates["POWER.BUFFER_POWER"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsBufferChargeLedgerFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("buffer-charge-ledger-run.json"));

        Assert.NotNull(resumed);
        AssertBufferChargeLedgerFixture(resumed!);
    }

    [Fact]
    public void ResumableLoad_AcceptsEntropyPowerFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("entropy-power-run.json"));

        Assert.NotNull(resumed);
        AssertEntropyPowerFixture(
            resumed!.MetaStats.PowerAggregates["POWER.ENTROPY"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsDanseMacabrePowerFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("danse-macabre-power-run.json"));

        Assert.NotNull(resumed);
        AssertDanseMacabrePowerFixture(
            resumed!.MetaStats.PowerAggregates["POWER.DANSE_MACABRE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsUnrelentingFreeAttackPowerFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("unrelenting-free-attack-power-run.json"));

        Assert.NotNull(resumed);
        AssertUnrelentingFreeAttackPowerFixture(
            resumed!.MetaStats.PowerAggregates["POWER.FREE_ATTACK_POWER"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsPounceFreeSkillPowerFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("pounce-free-skill-power-run.json"));

        Assert.NotNull(resumed);
        AssertPounceFreeSkillPowerFixture(
            resumed!.MetaStats.PowerAggregates["POWER.FREE_SKILL_POWER"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsDebtCardFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("debt-card-run.json"));

        Assert.NotNull(resumed);
        var cardAgg = resumed!.Aggregates["CARD.DEBT#1"];
        Assert.Equal(4, cardAgg.DebtTriggers);
        Assert.Equal(13, cardAgg.DebtGoldLost);
        Assert.Equal(7, cardAgg.DebtGoldLossBlocked);
    }

    [Fact]
    public void ResumableLoad_AcceptsSealOfGoldRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("seal-of-gold-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.SEAL_OF_GOLD"];
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(17, relicAgg.GoldLost);
        Assert.Equal(3, relicAgg.GoldLossBlocked);
        Assert.Equal(3, relicAgg.EnergyGenerated);
        Assert.Equal(2, relicAgg.EnergyGeneratedCombats);
    }

    [Fact]
    public void ResumableLoad_AcceptsPhylacteryRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("phylactery-relic-run.json"));

        Assert.NotNull(resumed);
        var boundAgg = resumed!.RelicAggregates["RELIC.BOUND_PHYLACTERY"];
        Assert.Equal(3, boundAgg.Activations);
        Assert.Equal(12m, boundAgg.TotalOstyHpSummoned);
        var unboundAgg = resumed.RelicAggregates["RELIC.PHYLACTERY_UNBOUND"];
        Assert.Equal(4, unboundAgg.Activations);
        Assert.Equal(20m, unboundAgg.TotalOstyHpSummoned);
    }

    [Fact]
    public void ResumableLoad_AcceptsPhylacteryOstyBodyFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("phylactery-osty-body-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(42m, resumed!.MetaStats.TotalOstyHpSummoned);
        Assert.Equal(27m, resumed.MetaStats.TotalOstyHpWhenUnleashPlayed);
        Assert.Equal(30m, resumed.MetaStats.TotalOstyDamageAbsorbed);
        Assert.Equal(6, resumed.MetaStats.OstyBodyTurns);
        Assert.Equal(2, resumed.MetaStats.OstyBodyCombats);
    }

    [Fact]
    public void ResumableLoad_AcceptsV26PrismaticGemFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v26-prismatic-gem-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.PRISMATIC_GEM"];
        Assert.Equal(4, relicAgg.EnergyGenerated);
        Assert.Equal(2, relicAgg.CardRewardsAffected);
    }

    [Fact]
    public void ResumableLoad_AcceptsV27OrichalcumBlockedAndCombatsInDeckFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v27-orichalcum-blocked-and-combats-in-deck-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.ORICHALCUM"];
        Assert.Equal(12, relicAgg.AdditionalBlockGained);
        Assert.Equal(4, relicAgg.BlockedTriggers);
        Assert.Equal(3, resumed.Aggregates["CARD.SPOILS_MAP#1"].CombatsInDeck);
        Assert.Equal(3, resumed.Aggregates["CARD.STRIKE_IRONCLAD#1"].CombatsInDeck);
    }

    [Fact]
    public void ResumableLoad_AcceptsV28ReptileTrinketFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v28-reptile-trinket-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.REPTILE_TRINKET"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(6m, relicAgg.StrengthAdded);
        Assert.Equal(0, relicAgg.ReptileTrinketTurns);
        Assert.Equal(0, relicAgg.ReptileTrinketCombats);
        Assert.Equal(0, relicAgg.ReptileTrinketTurnsWithExactlyTwoActivations);
        Assert.Equal(0, relicAgg.ReptileTrinketTurnsWithMoreThanTwoActivations);
    }

    [Fact]
    public void HistoricalLoad_AcceptsReptileTrinketRatesFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("reptile-trinket-rates-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertReptileTrinketRatesFixture(
            loaded.Data.RelicAggregates["RELIC.REPTILE_TRINKET"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsReptileTrinketRatesFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("reptile-trinket-rates-run.json"));

        Assert.NotNull(resumed);
        AssertReptileTrinketRatesFixture(
            resumed!.RelicAggregates["RELIC.REPTILE_TRINKET"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsRainbowRingRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("rainbow-ring-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertRainbowRingFixture(loaded.Data.RelicAggregates["RELIC.RAINBOW_RING"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsRainbowRingRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("rainbow-ring-relic-run.json"));

        Assert.NotNull(resumed);
        AssertRainbowRingFixture(resumed!.RelicAggregates["RELIC.RAINBOW_RING"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSparklingRougeRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("sparkling-rouge-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertSparklingRougeFixture(
            loaded.Data.RelicAggregates["RELIC.SPARKLING_ROUGE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsSparklingRougeRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("sparkling-rouge-relic-run.json"));

        Assert.NotNull(resumed);
        AssertSparklingRougeFixture(
            resumed!.RelicAggregates["RELIC.SPARKLING_ROUGE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsV29GorgetFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v29-gorget-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.GORGET"];
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(12m, relicAgg.PlatingAdded);
    }

    [Fact]
    public void ResumableLoad_AcceptsV30StoneCrackerFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v30-stone-cracker-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.STONE_CRACKER"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(6, relicAgg.CardsUpgraded);
    }

    [Fact]
    public void HistoricalLoad_AcceptsStoneCrackerPlayTrackingFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("stone-cracker-play-tracking-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertStoneCrackerPlayTracking(loaded.Data.RelicAggregates["RELIC.STONE_CRACKER"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsStoneCrackerPlayTrackingFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("stone-cracker-play-tracking-run.json"));

        Assert.NotNull(resumed);
        AssertStoneCrackerPlayTracking(resumed!.RelicAggregates["RELIC.STONE_CRACKER"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsV31PrismaticGemRewardCategoriesFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v31-prismatic-gem-reward-categories-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.PRISMATIC_GEM"];
        Assert.Equal(4, relicAgg.EnergyGenerated);
        Assert.Equal(2, relicAgg.CardRewardsAffected);
        Assert.Equal(3, relicAgg.CardRewardCategories["defect"].Count);
        Assert.Equal("Defect", relicAgg.CardRewardCategories["defect"].DisplayName);
        Assert.Equal(0, relicAgg.CardRewardCategories["defect"].Taken);
        Assert.Equal(1, relicAgg.CardRewardCategories["colorless"].Count);
        Assert.Equal("Colorless", relicAgg.CardRewardCategories["colorless"].DisplayName);
        Assert.Equal(0, relicAgg.CardRewardCategories["colorless"].Taken);
    }

    [Fact]
    public void ResumableLoad_AcceptsDingyRugRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("dingy-rug-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.DINGY_RUG"];
        Assert.Equal(3, relicAgg.CardRewardsAffected);
        Assert.Equal(6, relicAgg.CardRewardCategories["ironclad"].Count);
        Assert.Equal("Ironclad", relicAgg.CardRewardCategories["ironclad"].DisplayName);
        Assert.Equal(0, relicAgg.CardRewardCategories["ironclad"].Taken);
        Assert.Equal(3, relicAgg.CardRewardCategories["colorless"].Count);
        Assert.Equal("Colorless", relicAgg.CardRewardCategories["colorless"].DisplayName);
        Assert.Equal(0, relicAgg.CardRewardCategories["colorless"].Taken);
    }

    [Fact]
    public void ResumableLoad_AcceptsCardRewardPoolTakenFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("card-reward-pool-taken-run.json"));

        Assert.NotNull(resumed);

        var gem = resumed!.RelicAggregates["RELIC.PRISMATIC_GEM"];
        Assert.Equal(6, gem.EnergyGenerated);
        Assert.Equal(3, gem.CardRewardsAffected);
        Assert.Equal(5, gem.CardRewardCategories["ironclad"].Count);
        Assert.Equal(2, gem.CardRewardCategories["ironclad"].Taken);
        Assert.Equal(4, gem.CardRewardCategories["defect"].Count);
        Assert.Equal(1, gem.CardRewardCategories["defect"].Taken);
        Assert.Equal(2, gem.CardRewardCategories["colorless"].Count);
        Assert.Equal(0, gem.CardRewardCategories["colorless"].Taken);

        var rug = resumed.RelicAggregates["RELIC.DINGY_RUG"];
        Assert.Equal(5, rug.CardRewardCategories["ironclad"].Count);
        Assert.Equal(2, rug.CardRewardCategories["ironclad"].Taken);
        Assert.Equal(2, rug.CardRewardCategories["colorless"].Count);
        Assert.Equal(1, rug.CardRewardCategories["colorless"].Taken);
    }

    [Fact]
    public void ResumableLoad_AcceptsV32EnemyDamageFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("v32-enemy-damage-run.json"));

        Assert.NotNull(resumed);
        var enemyAgg = resumed!.EnemyAggregates["MONSTER.JAW_WORM"];
        Assert.Equal("MONSTER.JAW_WORM", enemyAgg.EnemyId);
        Assert.Equal(20, enemyAgg.DamageAttempted);
        Assert.Equal(12, enemyAgg.DamageDealt);
        Assert.Equal(8, enemyAgg.DamageBlocked);
    }

    [Fact]
    public void ResumableLoad_AcceptsEnemyStatusPollutionFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("enemy-status-pollution-run.json"));

        Assert.NotNull(resumed);
        var hauntedShipAgg = resumed!.EnemyAggregates["MONSTER.HAUNTED_SHIP"];
        Assert.Equal(2, hauntedShipAgg.DamageInstances);
        Assert.Equal(16, hauntedShipAgg.DamageAttempted);
        Assert.Equal(6, hauntedShipAgg.DamageBlocked);
        Assert.Equal(10, hauntedShipAgg.DamageDealt);
        Assert.Equal(3, hauntedShipAgg.StatusCardsAdded);
        Assert.Equal(1, hauntedShipAgg.StatusCardsAddedToHand);
        Assert.Equal(2, hauntedShipAgg.StatusCardsAddedToDraw);
        Assert.Equal(3, hauntedShipAgg.StatusCardsById["CARD.DAZED"].Count);
        var entomancerAgg = resumed.EnemyAggregates["MONSTER.ENTOMANCER"];
        Assert.Equal(2, entomancerAgg.StatusCardsAdded);
        Assert.Equal(2, entomancerAgg.StatusCardsById["CARD.DAZED"].Count);
    }

    [Fact]
    public void HistoricalLoad_AcceptsOpenBranchRelicStatsFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("open-branch-relic-stats-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var anchorAgg = loaded.Data.RelicAggregates["RELIC.ANCHOR"];
        Assert.Equal(2, anchorAgg.Activations);
        Assert.Equal(20, anchorAgg.AdditionalBlockGained);
        var letterOpenerAgg = loaded.Data.RelicAggregates["RELIC.LETTER_OPENER"];
        Assert.Equal(3, letterOpenerAgg.Activations);
        Assert.Equal(45, letterOpenerAgg.TotalDamageAttempted);
        Assert.Equal(9, letterOpenerAgg.TotalTargets);
        var bloodVialAgg = loaded.Data.RelicAggregates["RELIC.BLOOD_VIAL"];
        Assert.Equal(2, bloodVialAgg.Activations);
        Assert.Equal(3m, bloodVialAgg.TotalHealingRestored);
        Assert.Equal(1m, bloodVialAgg.TotalHealingLost);
        Assert.Equal(1m, bloodVialAgg.HealingLostReasons["full_hp"].Amount);
        Assert.Equal(16, loaded.Data.RelicAggregates["RELIC.AKABEKO"].VigorGained);
        var boomingConchAgg = loaded.Data.RelicAggregates["RELIC.BOOMING_CONCH"];
        Assert.Equal(2, boomingConchAgg.EnergyGenerated);
        Assert.Equal(4, boomingConchAgg.AdditionalCardsDrawn);
        var pendulumAgg = loaded.Data.RelicAggregates["RELIC.PENDULUM"];
        Assert.Equal(3, pendulumAgg.Activations);
        Assert.Equal(6, pendulumAgg.AdditionalCardsDrawn);
        Assert.Equal(2, pendulumAgg.PendulumCombats);
        var parryingShieldAgg = loaded.Data.RelicAggregates["RELIC.PARRYING_SHIELD"];
        Assert.Equal(2, parryingShieldAgg.Activations);
        Assert.Equal(17, parryingShieldAgg.TotalDamageAttempted);
        Assert.Equal(11, parryingShieldAgg.TotalDamageDealt);
        Assert.Equal(4, parryingShieldAgg.TotalDamageBlocked);
        Assert.Equal(2, parryingShieldAgg.TotalDamageOverkill);
        Assert.Equal(1, parryingShieldAgg.Kills);
        Assert.Equal(2, parryingShieldAgg.TotalTargets);
        var hornCleatAgg = loaded.Data.RelicAggregates["RELIC.HORN_CLEAT"];
        Assert.Equal(2, hornCleatAgg.Activations);
        Assert.Equal(24, hornCleatAgg.AdditionalBlockGained);
        var toolboxAgg = loaded.Data.RelicAggregates["RELIC.TOOLBOX"];
        Assert.Equal(2, toolboxAgg.Activations);
        Assert.Equal(4, toolboxAgg.UncommonCardsOffered);
        Assert.Equal(1, toolboxAgg.RareCardsOffered);
        Assert.Equal(2, toolboxAgg.UncommonCardsTaken);
        Assert.Equal(1, toolboxAgg.RareCardsTaken);
    }

    [Fact]
    public void ResumableLoad_AcceptsOpenBranchRelicStatsFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("open-branch-relic-stats-run.json"));

        Assert.NotNull(resumed);
        var letterOpenerAgg = resumed!.RelicAggregates["RELIC.LETTER_OPENER"];
        Assert.Equal(45, letterOpenerAgg.TotalDamageAttempted);
        Assert.Equal(9, letterOpenerAgg.TotalTargets);
        Assert.Equal(16, resumed.RelicAggregates["RELIC.AKABEKO"].VigorGained);
        Assert.Equal(4, resumed.RelicAggregates["RELIC.BOOMING_CONCH"].AdditionalCardsDrawn);
        Assert.Equal(6, resumed.RelicAggregates["RELIC.PENDULUM"].AdditionalCardsDrawn);
        Assert.Equal(2, resumed.RelicAggregates["RELIC.PENDULUM"].PendulumCombats);
        Assert.Equal(11, resumed.RelicAggregates["RELIC.PARRYING_SHIELD"].TotalDamageDealt);
        Assert.Equal(4, resumed.RelicAggregates["RELIC.PARRYING_SHIELD"].TotalDamageBlocked);
        Assert.Equal(24, resumed.RelicAggregates["RELIC.HORN_CLEAT"].AdditionalBlockGained);
        Assert.Equal(4, resumed.RelicAggregates["RELIC.TOOLBOX"].UncommonCardsOffered);
        Assert.Equal(1, resumed.RelicAggregates["RELIC.TOOLBOX"].RareCardsOffered);
        Assert.Equal(2, resumed.RelicAggregates["RELIC.TOOLBOX"].UncommonCardsTaken);
        Assert.Equal(1, resumed.RelicAggregates["RELIC.TOOLBOX"].RareCardsTaken);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLetterOpenerRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("letter-opener-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.LETTER_OPENER"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(45, relicAgg.TotalDamageAttempted);
        Assert.Equal(9, relicAgg.TotalTargets);
        Assert.Equal(9, relicAgg.LetterOpenerSkillsPlayed);
        Assert.Equal(3, relicAgg.LetterOpenerCombats);
        Assert.Equal(6, relicAgg.LetterOpenerTurns);
        Assert.Equal(2, relicAgg.LetterOpenerTurnsEndedAt1Charge);
        Assert.Equal(3, relicAgg.LetterOpenerTurnsEndedAt2Charges);
    }

    [Fact]
    public void ResumableLoad_AcceptsLetterOpenerRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("letter-opener-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.LETTER_OPENER"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(45, relicAgg.TotalDamageAttempted);
        Assert.Equal(9, relicAgg.TotalTargets);
        Assert.Equal(9, relicAgg.LetterOpenerSkillsPlayed);
        Assert.Equal(3, relicAgg.LetterOpenerCombats);
        Assert.Equal(6, relicAgg.LetterOpenerTurns);
        Assert.Equal(2, relicAgg.LetterOpenerTurnsEndedAt1Charge);
        Assert.Equal(3, relicAgg.LetterOpenerTurnsEndedAt2Charges);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPermafrostCombatAverageFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("permafrost-combat-average-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.PERMAFROST"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(21, relicAgg.AdditionalBlockGained);
        Assert.Equal(5, relicAgg.PermafrostCombats);
    }

    [Fact]
    public void ResumableLoad_AcceptsPermafrostCombatAverageFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("permafrost-combat-average-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.PERMAFROST"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(21, relicAgg.AdditionalBlockGained);
        Assert.Equal(5, relicAgg.PermafrostCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsBronzeScalesRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("bronze-scales-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.BRONZE_SCALES"];
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(13, relicAgg.TotalDamageAttempted);
        Assert.Equal(10, relicAgg.TotalDamageDealt);
        Assert.Equal(2, relicAgg.TotalDamageBlocked);
        Assert.Equal(1, relicAgg.TotalDamageOverkill);
        Assert.Equal(1, relicAgg.Kills);
        Assert.Equal(4, relicAgg.TotalTargets);
    }

    [Fact]
    public void ResumableLoad_AcceptsBronzeScalesRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("bronze-scales-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.BRONZE_SCALES"];
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(10, relicAgg.TotalDamageDealt);
        Assert.Equal(2, relicAgg.TotalDamageBlocked);
        Assert.Equal(1, relicAgg.TotalDamageOverkill);
    }

    [Fact]
    public void HistoricalLoad_AcceptsCandelabraRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("candelabra-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.CANDELABRA"];
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(8, relicAgg.EnergyGenerated);
        Assert.Equal(2, relicAgg.SecondTurnsEndedWithExcessEnergy);
    }

    [Fact]
    public void ResumableLoad_AcceptsCandelabraRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("candelabra-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.CANDELABRA"];
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(8, relicAgg.EnergyGenerated);
        Assert.Equal(2, relicAgg.SecondTurnsEndedWithExcessEnergy);
    }

    [Fact]
    public void HistoricalLoad_AcceptsTurnEnergyRelicsFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("turn-energy-relics-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);

        var lanternAgg = loaded.Data.RelicAggregates["RELIC.LANTERN"];
        Assert.Equal(2, lanternAgg.Activations);
        Assert.Equal(2, lanternAgg.EnergyGenerated);
        Assert.Equal(1, lanternAgg.FirstTurnsEndedWithExcessEnergy);

        var veryHotCocoaAgg = loaded.Data.RelicAggregates["RELIC.VERY_HOT_COCOA"];
        Assert.Equal(3, veryHotCocoaAgg.Activations);
        Assert.Equal(3, veryHotCocoaAgg.EnergyGenerated);
        Assert.Equal(2, veryHotCocoaAgg.FirstTurnsEndedWithExcessEnergy);

        var candelabraAgg = loaded.Data.RelicAggregates["RELIC.CANDELABRA"];
        Assert.Equal(4, candelabraAgg.Activations);
        Assert.Equal(8, candelabraAgg.EnergyGenerated);
        Assert.Equal(2, candelabraAgg.SecondTurnsEndedWithExcessEnergy);
        Assert.Equal(1, candelabraAgg.CombatsWithoutActivation);

        var chandelierAgg = loaded.Data.RelicAggregates["RELIC.CHANDELIER"];
        Assert.Equal(3, chandelierAgg.Activations);
        Assert.Equal(9, chandelierAgg.EnergyGenerated);
        Assert.Equal(1, chandelierAgg.ThirdTurnsEndedWithExcessEnergy);
        Assert.Equal(2, chandelierAgg.CombatsWithoutActivation);
    }

    [Fact]
    public void ResumableLoad_AcceptsTurnEnergyRelicsFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("turn-energy-relics-run.json"));

        Assert.NotNull(resumed);

        var lanternAgg = resumed!.RelicAggregates["RELIC.LANTERN"];
        Assert.Equal(2, lanternAgg.Activations);
        Assert.Equal(2, lanternAgg.EnergyGenerated);
        Assert.Equal(1, lanternAgg.FirstTurnsEndedWithExcessEnergy);

        var veryHotCocoaAgg = resumed.RelicAggregates["RELIC.VERY_HOT_COCOA"];
        Assert.Equal(3, veryHotCocoaAgg.Activations);
        Assert.Equal(3, veryHotCocoaAgg.EnergyGenerated);
        Assert.Equal(2, veryHotCocoaAgg.FirstTurnsEndedWithExcessEnergy);

        var candelabraAgg = resumed.RelicAggregates["RELIC.CANDELABRA"];
        Assert.Equal(4, candelabraAgg.Activations);
        Assert.Equal(8, candelabraAgg.EnergyGenerated);
        Assert.Equal(2, candelabraAgg.SecondTurnsEndedWithExcessEnergy);
        Assert.Equal(1, candelabraAgg.CombatsWithoutActivation);

        var chandelierAgg = resumed.RelicAggregates["RELIC.CHANDELIER"];
        Assert.Equal(3, chandelierAgg.Activations);
        Assert.Equal(9, chandelierAgg.EnergyGenerated);
        Assert.Equal(1, chandelierAgg.ThirdTurnsEndedWithExcessEnergy);
        Assert.Equal(2, chandelierAgg.CombatsWithoutActivation);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPaelsWingSacrificeRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("paels-wing-sacrifice-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.PAELS_WING"];
        Assert.Equal(5, relicAgg.CommonCardsConsumed);
        Assert.Equal(3, relicAgg.UncommonCardsConsumed);
        Assert.Equal(1, relicAgg.RareCardsConsumed);
        Assert.Equal(3, relicAgg.SacrificesMade);
        Assert.Equal(2, relicAgg.SacrificesSkipped);
        Assert.Equal(1, relicAgg.RelicsGranted["RELIC.KUNAI"].Count);
        Assert.Equal("Kunai", relicAgg.RelicsGranted["RELIC.KUNAI"].DisplayName);
    }

    [Fact]
    public void ResumableLoad_AcceptsPaelsWingSacrificeRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("paels-wing-sacrifice-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.PAELS_WING"];
        Assert.Equal(5, relicAgg.CommonCardsConsumed);
        Assert.Equal(3, relicAgg.UncommonCardsConsumed);
        Assert.Equal(1, relicAgg.RareCardsConsumed);
        Assert.Equal(3, relicAgg.SacrificesMade);
        Assert.Equal(2, relicAgg.SacrificesSkipped);
        Assert.Equal(1, relicAgg.RelicsGranted["RELIC.KUNAI"].Count);
        Assert.Equal("Kunai", relicAgg.RelicsGranted["RELIC.KUNAI"].DisplayName);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPaelsToothRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("paels-tooth-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertPaelsToothFixture(loaded.Data.RelicAggregates["RELIC.PAELS_TOOTH"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsPaelsToothRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("paels-tooth-relic-run.json"));

        Assert.NotNull(resumed);
        AssertPaelsToothFixture(resumed!.RelicAggregates["RELIC.PAELS_TOOTH"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsStrikeDummyRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("strike-dummy-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.STRIKE_DUMMY"];
        Assert.Equal(8, relicAgg.StrikeDummyStrikesPlayed);
        Assert.Equal(6, relicAgg.StrikeDummyRateStrikesPlayed);
        Assert.Equal(4, relicAgg.StrikeDummyTurns);
        Assert.Equal(2, relicAgg.StrikeDummyCombats);
        Assert.Equal(4, relicAgg.StrikeDummyBaseStrikesInDeck);
        Assert.Equal(3, relicAgg.StrikeDummyNonBaseStrikeCardsInDeck);
    }

    [Fact]
    public void ResumableLoad_AcceptsStrikeDummyRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("strike-dummy-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.STRIKE_DUMMY"];
        Assert.Equal(8, relicAgg.StrikeDummyStrikesPlayed);
        Assert.Equal(6, relicAgg.StrikeDummyRateStrikesPlayed);
        Assert.Equal(4, relicAgg.StrikeDummyTurns);
        Assert.Equal(2, relicAgg.StrikeDummyCombats);
        Assert.Equal(4, relicAgg.StrikeDummyBaseStrikesInDeck);
        Assert.Equal(3, relicAgg.StrikeDummyNonBaseStrikeCardsInDeck);
    }

    [Fact]
    public void HistoricalLoad_AcceptsUnsettlingLampRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("unsettling-lamp-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.UNSETTLING_LAMP"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(4, relicAgg.VulnerableApplied);
        Assert.Equal(2, relicAgg.WeakApplied);
        Assert.Equal(6m, relicAgg.AppliedEffects["POWER.POISON"].TotalAmountApplied);
    }

    [Fact]
    public void ResumableLoad_AcceptsUnsettlingLampRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("unsettling-lamp-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.UNSETTLING_LAMP"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(4, relicAgg.VulnerableApplied);
        Assert.Equal(2, relicAgg.WeakApplied);
        Assert.Equal(6m, relicAgg.AppliedEffects["POWER.POISON"].TotalAmountApplied);
    }

    [Fact]
    public void HistoricalLoad_AcceptsNutritiousSoupRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("nutritious-soup-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.NUTRITIOUS_SOUP"];
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(4, relicAgg.NutritiousSoupEnchantedStrikesPlayed);
    }

    [Fact]
    public void ResumableLoad_AcceptsNutritiousSoupRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("nutritious-soup-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.NUTRITIOUS_SOUP"];
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(4, relicAgg.NutritiousSoupEnchantedStrikesPlayed);
    }

    [Fact]
    public void HistoricalLoad_AcceptsBrilliantScarfRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("brilliant-scarf-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.BRILLIANT_SCARF"];
        Assert.Equal(2, relicAgg.DiscountCombats);
        Assert.Equal(0, relicAgg.DiscountTurns);
        Assert.Equal(5, relicAgg.DiscountsOffered);
        Assert.Equal(3, relicAgg.DiscountsTaken);
        Assert.Equal(7, relicAgg.EnergySavedByDiscount);
        Assert.Equal(0, relicAgg.BrilliantScarfEnergySavedForTurnAverage);
        Assert.Equal(2, relicAgg.DiscountedCardCosts["energy:2|stars:0"].Count);
        Assert.Equal(1, relicAgg.DiscountedCardCosts["energy:1|stars:2"].Count);
        Assert.Equal(1, relicAgg.DiscountedCardCosts["energy:4|stars:0"].Count);
    }

    [Fact]
    public void HistoricalLoad_AcceptsBrilliantScarfTurnAverageFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("brilliant-scarf-turn-average-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.BRILLIANT_SCARF"];
        Assert.Equal(2, relicAgg.DiscountCombats);
        Assert.Equal(6, relicAgg.DiscountTurns);
        Assert.Equal(5, relicAgg.DiscountsOffered);
        Assert.Equal(3, relicAgg.DiscountsTaken);
        Assert.Equal(7, relicAgg.EnergySavedByDiscount);
        Assert.Equal(7, relicAgg.BrilliantScarfEnergySavedForTurnAverage);
    }

    [Fact]
    public void HistoricalLoad_AcceptsDarkstonePeriaptRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("darkstone-periapt-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.DARKSTONE_PERIAPT"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(3, relicAgg.CursesAcquired);
        Assert.Equal(18, relicAgg.TotalMaxHpGained);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(88m, relicAgg.NewMaxHp);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLuckyFyshRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("lucky-fysh-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.LUCKY_FYSH"];
        Assert.Equal(45, relicAgg.GoldGained);
        Assert.Equal(3, relicAgg.CardsAddedToDeck);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLuckyFyshRoomAveragesFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("lucky-fysh-room-averages-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.LUCKY_FYSH"];
        Assert.Equal(4, relicAgg.FloorAcquired);
        Assert.Equal(70, relicAgg.GoldGained);
        Assert.Equal(6, relicAgg.CardsAddedToDeck);
        Assert.Equal(2, relicAgg.CursesAddedToDeck);
        Assert.Equal(3, relicAgg.LuckyFyshCardsAddedInCombats);
        Assert.Equal(2, relicAgg.LuckyFyshCardsAddedInShops);
        Assert.Equal(1, relicAgg.LuckyFyshCardsAddedInEvents);
        Assert.Equal(0, relicAgg.LuckyFyshCardsAddedInCampfires);
        Assert.Equal(35, relicAgg.LuckyFyshGoldGainedInCombats);
        Assert.Equal(24, relicAgg.LuckyFyshGoldGainedInShops);
        Assert.Equal(11, relicAgg.LuckyFyshGoldGainedInEvents);
        Assert.Equal(0, relicAgg.LuckyFyshGoldGainedInCampfires);
        Assert.Equal(6, relicAgg.LuckyFyshCombatsHeld);
        Assert.Equal(2, relicAgg.LuckyFyshShopsHeld);
        Assert.Equal(4, relicAgg.LuckyFyshEventsHeld);
        Assert.Equal(3, relicAgg.LuckyFyshCampfiresHeld);
        Assert.Equal(15, relicAgg.LuckyFyshLastCombatFloorHeld);
        Assert.Equal(12, relicAgg.LuckyFyshLastShopFloorHeld);
        Assert.Equal(14, relicAgg.LuckyFyshLastEventFloorHeld);
        Assert.Equal(13, relicAgg.LuckyFyshLastCampfireFloorHeld);
    }

    [Fact]
    public void HistoricalLoad_LuckyFyshRelicFixture_DefaultsNewRoomFieldsToZero()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("lucky-fysh-relic-run.json"));

        Assert.NotNull(loaded);
        var relicAgg = loaded!.Data.RelicAggregates["RELIC.LUCKY_FYSH"];
        Assert.Equal(0, relicAgg.CursesAddedToDeck);
        Assert.Equal(0, relicAgg.LuckyFyshCardsAddedInCombats);
        Assert.Equal(0, relicAgg.LuckyFyshGoldGainedInCombats);
        Assert.Equal(0, relicAgg.LuckyFyshCombatsHeld);
        Assert.Null(relicAgg.LuckyFyshLastCombatFloorHeld);
    }

    [Fact]
    public void HistoricalLoad_AcceptsMawBankRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("maw-bank-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.MAW_BANK"];
        Assert.Equal(6, relicAgg.Activations);
        Assert.Equal(72, relicAgg.GoldGained);
        Assert.Equal(2, relicAgg.MawBankShopsSkipped);
        Assert.Equal(34, relicAgg.MawBankGoldSpentOutsideShops);
        Assert.Equal(11, loaded.Data.MawBankPendingShopFloor);
    }

    [Fact]
    public void HistoricalLoad_AcceptsOldCoinRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("old-coin-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.OLD_COIN"];
        Assert.Equal(300, relicAgg.OldCoinGoldGranted);
        Assert.Equal(120, relicAgg.OldCoinGoldSpent);
        Assert.Collection(
            loaded.Data.GoldAttributionLedger,
            chunk =>
            {
                Assert.Equal("RELIC.OLD_COIN", chunk.SourceRelicId);
                Assert.Equal(180, chunk.AmountRemaining);
            },
            chunk =>
            {
                Assert.Null(chunk.SourceRelicId);
                Assert.Equal(25, chunk.AmountRemaining);
            });
    }

    [Fact]
    public void HistoricalLoad_AcceptsPocketwatchTurnStatsFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("pocketwatch-turn-stats-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.POCKETWATCH"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(9, relicAgg.AdditionalCardsDrawn);
        Assert.Equal(6, relicAgg.PocketwatchTurns);
        Assert.Equal(2, relicAgg.PocketwatchCombats);
        Assert.Equal(20, relicAgg.PocketwatchTurnEndCountTotal);
        Assert.Equal(2, relicAgg.PocketwatchTurnsActivationMissed);
        Assert.Equal(5, relicAgg.PocketwatchActivatedTurnEndCountTotal);
        Assert.Equal(3, relicAgg.PocketwatchActivationValueSamples);
        Assert.Equal(10, relicAgg.PocketwatchMissedTurnEndCountTotal);
    }

    [Fact]
    public void HistoricalLoad_AcceptsBookOfFiveRingsRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("book-of-five-rings-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertBookOfFiveRingsFixture(loaded.Data.RelicAggregates["RELIC.BOOK_OF_FIVE_RINGS"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSignetRingRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("signet-ring-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.SIGNET_RING"];
        Assert.Equal(4, relicAgg.FloorsTraveledUntilNextShop);
    }

    [Fact]
    public void HistoricalLoad_AcceptsShovelRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("shovel-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.SHOVEL"];
        Assert.Equal(4, relicAgg.RelicsAcquired);
        Assert.Equal(1, relicAgg.CommonRelicsAcquired);
        Assert.Equal(2, relicAgg.UncommonRelicsAcquired);
        Assert.Equal(1, relicAgg.RareRelicsAcquired);
        Assert.Equal(2, relicAgg.CampfiresNotDug);
    }

    [Fact]
    public void HistoricalLoad_AcceptsJuzuBraceletRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("juzu-bracelet-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.JUZU_BRACELET"];
        Assert.Equal(3, relicAgg.QuestionMarkSitesEntered);
    }

    [Fact]
    public void ResumableLoad_AcceptsBrilliantScarfRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("brilliant-scarf-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.BRILLIANT_SCARF"];
        Assert.Equal(2, relicAgg.DiscountCombats);
        Assert.Equal(0, relicAgg.DiscountTurns);
        Assert.Equal(5, relicAgg.DiscountsOffered);
        Assert.Equal(3, relicAgg.DiscountsTaken);
        Assert.Equal(7, relicAgg.EnergySavedByDiscount);
        Assert.Equal(0, relicAgg.BrilliantScarfEnergySavedForTurnAverage);
        Assert.Equal(2, relicAgg.DiscountedCardCosts["energy:2|stars:0"].Count);
        Assert.Equal(1, relicAgg.DiscountedCardCosts["energy:1|stars:2"].Count);
        Assert.Equal(1, relicAgg.DiscountedCardCosts["energy:4|stars:0"].Count);
    }

    [Fact]
    public void ResumableLoad_AcceptsBrilliantScarfTurnAverageFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("brilliant-scarf-turn-average-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.BRILLIANT_SCARF"];
        Assert.Equal(2, relicAgg.DiscountCombats);
        Assert.Equal(6, relicAgg.DiscountTurns);
        Assert.Equal(5, relicAgg.DiscountsOffered);
        Assert.Equal(3, relicAgg.DiscountsTaken);
        Assert.Equal(7, relicAgg.EnergySavedByDiscount);
        Assert.Equal(7, relicAgg.BrilliantScarfEnergySavedForTurnAverage);
    }

    [Fact]
    public void ResumableLoad_AcceptsDarkstonePeriaptRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("darkstone-periapt-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.DARKSTONE_PERIAPT"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(3, relicAgg.CursesAcquired);
        Assert.Equal(18, relicAgg.TotalMaxHpGained);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(88m, relicAgg.NewMaxHp);
    }

    [Fact]
    public void ResumableLoad_AcceptsLuckyFyshRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("lucky-fysh-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.LUCKY_FYSH"];
        Assert.Equal(45, relicAgg.GoldGained);
        Assert.Equal(3, relicAgg.CardsAddedToDeck);
    }

    [Fact]
    public void ResumableLoad_AcceptsLuckyFyshRoomAveragesFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("lucky-fysh-room-averages-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.LUCKY_FYSH"];
        Assert.Equal(70, relicAgg.GoldGained);
        Assert.Equal(6, relicAgg.CardsAddedToDeck);
        Assert.Equal(2, relicAgg.CursesAddedToDeck);
        Assert.Equal(3, relicAgg.LuckyFyshCardsAddedInCombats);
        Assert.Equal(2, relicAgg.LuckyFyshCardsAddedInShops);
        Assert.Equal(1, relicAgg.LuckyFyshCardsAddedInEvents);
        Assert.Equal(0, relicAgg.LuckyFyshCardsAddedInCampfires);
        Assert.Equal(35, relicAgg.LuckyFyshGoldGainedInCombats);
        Assert.Equal(24, relicAgg.LuckyFyshGoldGainedInShops);
        Assert.Equal(11, relicAgg.LuckyFyshGoldGainedInEvents);
        Assert.Equal(0, relicAgg.LuckyFyshGoldGainedInCampfires);
        Assert.Equal(6, relicAgg.LuckyFyshCombatsHeld);
        Assert.Equal(2, relicAgg.LuckyFyshShopsHeld);
        Assert.Equal(4, relicAgg.LuckyFyshEventsHeld);
        Assert.Equal(3, relicAgg.LuckyFyshCampfiresHeld);
        Assert.Equal(15, relicAgg.LuckyFyshLastCombatFloorHeld);
    }

    [Fact]
    public void ResumableLoad_AcceptsMawBankRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("maw-bank-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.MAW_BANK"];
        Assert.Equal(6, relicAgg.Activations);
        Assert.Equal(72, relicAgg.GoldGained);
        Assert.Equal(2, relicAgg.MawBankShopsSkipped);
        Assert.Equal(34, relicAgg.MawBankGoldSpentOutsideShops);
        Assert.Equal(11, resumed.MawBankPendingShopFloor);
    }

    [Fact]
    public void ResumableLoad_AcceptsOldCoinRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("old-coin-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.OLD_COIN"];
        Assert.Equal(300, relicAgg.OldCoinGoldGranted);
        Assert.Equal(120, relicAgg.OldCoinGoldSpent);
        Assert.Equal(2, resumed.GoldAttributionLedger.Count);
    }

    [Fact]
    public void ResumableLoad_AcceptsPocketwatchTurnStatsFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("pocketwatch-turn-stats-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.POCKETWATCH"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(9, relicAgg.AdditionalCardsDrawn);
        Assert.Equal(6, relicAgg.PocketwatchTurns);
        Assert.Equal(2, relicAgg.PocketwatchCombats);
        Assert.Equal(20, relicAgg.PocketwatchTurnEndCountTotal);
        Assert.Equal(2, relicAgg.PocketwatchTurnsActivationMissed);
        Assert.Equal(5, relicAgg.PocketwatchActivatedTurnEndCountTotal);
        Assert.Equal(3, relicAgg.PocketwatchActivationValueSamples);
        Assert.Equal(10, relicAgg.PocketwatchMissedTurnEndCountTotal);
    }

    [Fact]
    public void ResumableLoad_AcceptsBookOfFiveRingsRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("book-of-five-rings-relic-run.json"));

        Assert.NotNull(resumed);
        AssertBookOfFiveRingsFixture(resumed!.RelicAggregates["RELIC.BOOK_OF_FIVE_RINGS"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsSignetRingRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("signet-ring-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.SIGNET_RING"];
        Assert.Equal(4, relicAgg.FloorsTraveledUntilNextShop);
    }

    [Fact]
    public void ResumableLoad_AcceptsShovelRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("shovel-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.SHOVEL"];
        Assert.Equal(4, relicAgg.RelicsAcquired);
        Assert.Equal(1, relicAgg.CommonRelicsAcquired);
        Assert.Equal(2, relicAgg.UncommonRelicsAcquired);
        Assert.Equal(1, relicAgg.RareRelicsAcquired);
        Assert.Equal(2, relicAgg.CampfiresNotDug);
    }

    [Fact]
    public void ResumableLoad_AcceptsJuzuBraceletRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("juzu-bracelet-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.JUZU_BRACELET"];
        Assert.Equal(3, relicAgg.QuestionMarkSitesEntered);
    }

    [Fact]
    public void HistoricalLoad_AcceptsCursedPearlRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("cursed-pearl-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.CURSED_PEARL"];
        var curseAgg = loaded.Data.Aggregates["CARD.GREED#1"];
        Assert.Equal(6, relicAgg.FloorsAscendedBeforeFirstShop);
        Assert.Equal(4, curseAgg.CombatsInDeck);
        Assert.Equal(8, curseAgg.TimesDrawn);
        Assert.Equal(3, curseAgg.TimesDiscarded);
        Assert.Equal(1, curseAgg.Plays);
        Assert.Equal(2, curseAgg.TimesExhausted);
    }

    [Fact]
    public void ResumableLoad_AcceptsCursedPearlRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("cursed-pearl-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.CURSED_PEARL"];
        var curseAgg = resumed.Aggregates["CARD.GREED#1"];
        Assert.Equal(6, relicAgg.FloorsAscendedBeforeFirstShop);
        Assert.Equal(4, curseAgg.CombatsInDeck);
        Assert.Equal(8, curseAgg.TimesDrawn);
        Assert.Equal(3, curseAgg.TimesDiscarded);
        Assert.Equal(1, curseAgg.Plays);
        Assert.Equal(2, curseAgg.TimesExhausted);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLeafyPoulticeRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("leafy-poultice-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.LEAFY_POULTICE"];
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(58m, relicAgg.NewMaxHp);
        AssertLeafyPoulticeTransformations(relicAgg);
    }

    [Fact]
    public void ResumableLoad_AcceptsLeafyPoulticeRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("leafy-poultice-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.LEAFY_POULTICE"];
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(58m, relicAgg.NewMaxHp);
        AssertLeafyPoulticeTransformations(relicAgg);
    }

    [Fact]
    public void HistoricalLoad_AcceptsGamblingChipRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("gambling-chip-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.GAMBLING_CHIP"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(7, relicAgg.CardsDiscarded);
    }

    [Fact]
    public void ResumableLoad_AcceptsGamblingChipRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("gambling-chip-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.GAMBLING_CHIP"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(7, relicAgg.CardsDiscarded);
    }

    [Fact]
    public void HistoricalLoad_AcceptsCentennialPuzzleRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("centennial-puzzle-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.CENTENNIAL_PUZZLE"];
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(11, relicAgg.AdditionalCardsDrawn);
        AssertCentennialPuzzleActivationContext(relicAgg);
    }

    [Fact]
    public void ResumableLoad_AcceptsCentennialPuzzleRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("centennial-puzzle-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.CENTENNIAL_PUZZLE"];
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(11, relicAgg.AdditionalCardsDrawn);
        AssertCentennialPuzzleActivationContext(relicAgg);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPollinousCoreRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("pollinous-core-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertPollinousCoreFixture(
            loaded.Data.RelicAggregates["RELIC.POLLINOUS_CORE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsPollinousCoreRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("pollinous-core-relic-run.json"));

        Assert.NotNull(resumed);
        AssertPollinousCoreFixture(
            resumed!.RelicAggregates["RELIC.POLLINOUS_CORE"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsJossPaperRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("joss-paper-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertJossPaperFixture(
            loaded.Data.RelicAggregates["RELIC.JOSS_PAPER"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsJossPaperRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("joss-paper-relic-run.json"));

        Assert.NotNull(resumed);
        AssertJossPaperFixture(
            resumed!.RelicAggregates["RELIC.JOSS_PAPER"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsWhiteStarRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("white-star-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertWhiteStarFixture(
            loaded.Data.RelicAggregates["RELIC.WHITE_STAR"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsWhiteStarRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("white-star-relic-run.json"));

        Assert.NotNull(resumed);
        AssertWhiteStarFixture(
            resumed!.RelicAggregates["RELIC.WHITE_STAR"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsOddlySmoothStoneRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("oddly-smooth-stone-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.Equal(
            7,
            loaded.Data.RelicAggregates["RELIC.ODDLY_SMOOTH_STONE"]
                .OddlySmoothStoneBlockCardsPlayed);
    }

    [Fact]
    public void ResumableLoad_AcceptsOddlySmoothStoneRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("oddly-smooth-stone-relic-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(
            7,
            resumed!.RelicAggregates["RELIC.ODDLY_SMOOTH_STONE"]
                .OddlySmoothStoneBlockCardsPlayed);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPrayerWheelRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("prayer-wheel-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertPrayerWheelFixture(
            loaded.Data.RelicAggregates["RELIC.PRAYER_WHEEL"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsPrayerWheelRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("prayer-wheel-relic-run.json"));

        Assert.NotNull(resumed);
        AssertPrayerWheelFixture(
            resumed!.RelicAggregates["RELIC.PRAYER_WHEEL"]);
    }

    private static void AssertCentennialPuzzleActivationContext(RelicAggregate relicAgg)
    {
        Assert.Equal(9, relicAgg.CentennialPuzzleActivationTurnTotal);
        Assert.Equal(4, relicAgg.CentennialPuzzleActivationTurnSamples);
        Assert.Equal(3, relicAgg.CentennialPuzzlePlayerTurnActivations);
        Assert.Equal(1, relicAgg.CentennialPuzzleOpponentTurnActivations);
        Assert.Equal(1, relicAgg.CentennialPuzzleStatusActivations);
        Assert.Equal(1, relicAgg.CentennialPuzzleCurseActivations);
        Assert.Equal(2, relicAgg.CentennialPuzzleEnemySourceActivations);
    }

    private static void AssertPollinousCoreFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(5, relicAgg.AdditionalCardsDrawn);
        Assert.Equal(1, relicAgg.AdditionalCardDrawsBlocked);
        Assert.Equal(10, relicAgg.PollinousCoreTurns);
        Assert.Equal(2, relicAgg.PollinousCoreCombats);
        Assert.Equal(2, relicAgg.PollinousCoreTurnsEndedOn0Counters);
        Assert.Equal(3, relicAgg.PollinousCoreTurnsEndedOn1Counter);
        Assert.Equal(3, relicAgg.PollinousCoreTurnsEndedOn2Counters);
        Assert.Equal(2, relicAgg.PollinousCoreTurnsEndedOn3Counters);
    }

    private static void AssertJossPaperFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(5, relicAgg.AdditionalCardsDrawn);
        Assert.Equal(1, relicAgg.AdditionalCardDrawsBlocked);
        Assert.Equal(11, relicAgg.JossPaperCardsExhausted);
        Assert.Equal(10, relicAgg.JossPaperTurns);
        Assert.Equal(2, relicAgg.JossPaperCombats);
        Assert.Equal(2, relicAgg.JossPaperTurnsEndedOn0Counters);
        Assert.Equal(3, relicAgg.JossPaperTurnsEndedOn1Counter);
        Assert.Equal(3, relicAgg.JossPaperTurnsEndedOn2Counters);
        Assert.Equal(2, relicAgg.JossPaperTurnsEndedOn3Counters);
        Assert.Equal(0, relicAgg.JossPaperTurnsEndedOn4Counters);
    }

    private static void AssertWhiteStarFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(8, relicAgg.RareCardsOffered);
        Assert.Equal(4, relicAgg.RareAttackCardsOffered);
        Assert.Equal(2, relicAgg.RareSkillCardsOffered);
        Assert.Equal(2, relicAgg.RarePowerCardsOffered);
        Assert.Equal(2, relicAgg.RareCardRewardScreensDeclined);
        // Predates the taken counters, so they read as zero taken.
        Assert.Equal(0, relicAgg.RareCardsTaken);
        Assert.Equal(0, relicAgg.RareAttackCardsTaken);
        Assert.Equal(0, relicAgg.RareSkillCardsTaken);
        Assert.Equal(0, relicAgg.RarePowerCardsTaken);
    }

    [Fact]
    public void ResumableLoad_AcceptsWhiteStarRareCardsTakenFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("white-star-rare-cards-taken-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.WHITE_STAR"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(8, relicAgg.RareCardsOffered);
        Assert.Equal(4, relicAgg.RareAttackCardsOffered);
        Assert.Equal(2, relicAgg.RareSkillCardsOffered);
        Assert.Equal(2, relicAgg.RarePowerCardsOffered);
        Assert.Equal(3, relicAgg.RareCardsTaken);
        Assert.Equal(2, relicAgg.RareAttackCardsTaken);
        Assert.Equal(1, relicAgg.RareSkillCardsTaken);
        Assert.Equal(0, relicAgg.RarePowerCardsTaken);
        Assert.Equal(1, relicAgg.RareCardRewardScreensDeclined);
    }

    private static void AssertPrayerWheelFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(4, relicAgg.PrayerWheelExtraRewardScreens);
        Assert.Equal(1, relicAgg.PrayerWheelExtraRewardScreensRejected);
        Assert.Equal(7, relicAgg.CommonCardsOffered);
        Assert.Equal(4, relicAgg.UncommonCardsOffered);
        Assert.Equal(1, relicAgg.RareCardsOffered);
        Assert.Equal(3, relicAgg.CommonCardsTaken);
        Assert.Equal(1, relicAgg.UncommonCardsTaken);
        Assert.Equal(1, relicAgg.RareCardsTaken);
    }

    [Fact]
    public void HistoricalLoad_AcceptsRegalPillowRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("regal-pillow-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.REGAL_PILLOW"];
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(12m, relicAgg.TotalHealingAttempted);
        Assert.Equal(9m, relicAgg.TotalHealingRestored);
        Assert.Equal(3m, relicAgg.TotalHealingLost);
        Assert.Equal(3m, relicAgg.HealingLostReasons["full_hp"].Amount);
    }

    [Fact]
    public void ResumableLoad_AcceptsRegalPillowRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("regal-pillow-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.REGAL_PILLOW"];
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(12m, relicAgg.TotalHealingAttempted);
        Assert.Equal(9m, relicAgg.TotalHealingRestored);
        Assert.Equal(3m, relicAgg.TotalHealingLost);
        Assert.Equal(3m, relicAgg.HealingLostReasons["full_hp"].Amount);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPrecariousShearsRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("precarious-shears-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.PRECARIOUS_SHEARS"];
        Assert.Equal(new[] { "Strike", "Defend+" }, relicAgg.CardsRemoved);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(63m, relicAgg.NewMaxHp);
        Assert.Equal(70m, relicAgg.StartingMaxHp);
        Assert.Equal(63m, relicAgg.ResultingMaxHp);
    }

    [Fact]
    public void ResumableLoad_AcceptsPrecariousShearsRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("precarious-shears-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.PRECARIOUS_SHEARS"];
        Assert.Equal(new[] { "Strike", "Defend+" }, relicAgg.CardsRemoved);
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(63m, relicAgg.NewMaxHp);
        Assert.Equal(70m, relicAgg.StartingMaxHp);
        Assert.Equal(63m, relicAgg.ResultingMaxHp);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSandCastleRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("sand-castle-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.SAND_CASTLE"];
        Assert.Equal(2, relicAgg.CardsUpgraded);
        Assert.Equal(new[] { "Strike+", "Defend+" }, relicAgg.UpgradedCards);
    }

    [Fact]
    public void ResumableLoad_AcceptsSandCastleRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("sand-castle-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.SAND_CASTLE"];
        Assert.Equal(2, relicAgg.CardsUpgraded);
        Assert.Equal(new[] { "Strike+", "Defend+" }, relicAgg.UpgradedCards);
    }

    [Fact]
    public void HistoricalLoad_AcceptsFragrantMushroomRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("fragrant-mushroom-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.FRAGRANT_MUSHROOM"];
        Assert.Equal(2, relicAgg.CardsUpgraded);
        Assert.Equal(new[] { "Strike+", "Defend+" }, relicAgg.UpgradedCards);
        Assert.Equal(52m, relicAgg.StartingHp);
        Assert.Equal(37m, relicAgg.ResultingHp);
    }

    [Fact]
    public void ResumableLoad_AcceptsFragrantMushroomRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("fragrant-mushroom-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.FRAGRANT_MUSHROOM"];
        Assert.Equal(2, relicAgg.CardsUpgraded);
        Assert.Equal(new[] { "Strike+", "Defend+" }, relicAgg.UpgradedCards);
        Assert.Equal(52m, relicAgg.StartingHp);
        Assert.Equal(37m, relicAgg.ResultingHp);
    }

    [Fact]
    public void HistoricalLoad_AcceptsWhetstoneRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("whetstone-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.WHETSTONE"];
        Assert.Equal(2, relicAgg.CardsUpgraded);
        Assert.Equal(new[] { "Strike+", "Pommel Strike+" }, relicAgg.UpgradedCards);
    }

    [Fact]
    public void ResumableLoad_AcceptsWhetstoneRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("whetstone-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.WHETSTONE"];
        Assert.Equal(2, relicAgg.CardsUpgraded);
        Assert.Equal(new[] { "Strike+", "Pommel Strike+" }, relicAgg.UpgradedCards);
    }

    [Fact]
    public void HistoricalLoad_AcceptsFishingRodRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("fishing-rod-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.FISHING_ROD"];
        Assert.Equal(2, relicAgg.CardsUpgraded);
        Assert.Equal(new[] { "Grave Warden+", "Reap+" }, relicAgg.UpgradedCards);
    }

    [Fact]
    public void ResumableLoad_AcceptsFishingRodRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("fishing-rod-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.FISHING_ROD"];
        Assert.Equal(2, relicAgg.CardsUpgraded);
        Assert.Equal(new[] { "Grave Warden+", "Reap+" }, relicAgg.UpgradedCards);
    }

    [Fact]
    public void HistoricalLoad_AcceptsFishingRodFloorAveragesFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("fishing-rod-floor-averages-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertFishingRodFloorAverages(
            loaded.Data.RelicAggregates["RELIC.FISHING_ROD"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsFishingRodFloorAveragesFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("fishing-rod-floor-averages-run.json"));

        Assert.NotNull(resumed);
        AssertFishingRodFloorAverages(
            resumed!.RelicAggregates["RELIC.FISHING_ROD"]);
    }

    private static void AssertFishingRodFloorAverages(RelicAggregate relicAgg)
    {
        Assert.Equal(5, relicAgg.FloorAcquired);
        Assert.Equal(new[] { 2, 2, 3, 3, 4, 1 }, relicAgg.FishingRodCombatFloorDistances);
        Assert.Equal(20, relicAgg.FishingRodLastCombatFloor);
        Assert.Equal(new[] { 7, 8 }, relicAgg.FishingRodUpgradeFloorDistances);
        Assert.Equal(20, relicAgg.FishingRodLastUpgradeFloor);
    }

    [Fact]
    public void HistoricalLoad_AcceptsWarHammerRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("war-hammer-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertWarHammerFixture(loaded.Data.RelicAggregates["RELIC.WAR_HAMMER"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsWarHammerRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("war-hammer-relic-run.json"));

        Assert.NotNull(resumed);
        AssertWarHammerFixture(resumed!.RelicAggregates["RELIC.WAR_HAMMER"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsEggRelicOffersFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("egg-relic-offers-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertEggOfferFixture(
            loaded.Data.RelicAggregates["RELIC.MOLTEN_EGG"],
            total: 7,
            common: 4,
            uncommon: 2,
            rare: 1,
            taken: 4,
            commonTaken: 2,
            uncommonTaken: 1,
            rareTaken: 1);
        AssertEggOfferFixture(
            loaded.Data.RelicAggregates["RELIC.TOXIC_EGG"],
            total: 5,
            common: 2,
            uncommon: 2,
            rare: 1,
            taken: 3,
            commonTaken: 1,
            uncommonTaken: 1,
            rareTaken: 1);
        AssertEggOfferFixture(
            loaded.Data.RelicAggregates["RELIC.FROZEN_EGG"],
            total: 3,
            common: 1,
            uncommon: 1,
            rare: 1,
            taken: 2,
            commonTaken: 1,
            uncommonTaken: 0,
            rareTaken: 1);
    }

    [Fact]
    public void ResumableLoad_AcceptsEggRelicOffersFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("egg-relic-offers-run.json"));

        Assert.NotNull(resumed);
        AssertEggOfferFixture(
            resumed!.RelicAggregates["RELIC.MOLTEN_EGG"],
            total: 7,
            common: 4,
            uncommon: 2,
            rare: 1,
            taken: 4,
            commonTaken: 2,
            uncommonTaken: 1,
            rareTaken: 1);
        AssertEggOfferFixture(
            resumed.RelicAggregates["RELIC.TOXIC_EGG"],
            total: 5,
            common: 2,
            uncommon: 2,
            rare: 1,
            taken: 3,
            commonTaken: 1,
            uncommonTaken: 1,
            rareTaken: 1);
        AssertEggOfferFixture(
            resumed.RelicAggregates["RELIC.FROZEN_EGG"],
            total: 3,
            common: 1,
            uncommon: 1,
            rare: 1,
            taken: 2,
            commonTaken: 1,
            uncommonTaken: 0,
            rareTaken: 1);
    }

    private static void AssertEggOfferFixture(
        RelicAggregate agg,
        int total,
        int common,
        int uncommon,
        int rare,
        int taken,
        int commonTaken,
        int uncommonTaken,
        int rareTaken)
    {
        Assert.Equal(total, agg.UpgradedCardsOffered);
        Assert.Equal(common, agg.UpgradedCommonCardsOffered);
        Assert.Equal(uncommon, agg.UpgradedUncommonCardsOffered);
        Assert.Equal(rare, agg.UpgradedRareCardsOffered);
        Assert.Equal(taken, agg.UpgradedCardsTaken);
        Assert.Equal(commonTaken, agg.UpgradedCommonCardsTaken);
        Assert.Equal(uncommonTaken, agg.UpgradedUncommonCardsTaken);
        Assert.Equal(rareTaken, agg.UpgradedRareCardsTaken);
    }

    [Fact]
    public void HistoricalLoad_AcceptsBloodSoakedRoseRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("blood-soaked-rose-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.BLOOD_SOAKED_ROSE"];
        var curseAgg = loaded.Data.Aggregates["CARD.ENTHRALLED#1"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(9, relicAgg.EnergyGenerated);
        Assert.Equal(3, curseAgg.CombatsInDeck);
        Assert.Equal(5, curseAgg.TimesDrawn);
        Assert.Equal(2, curseAgg.TimesDiscarded);
        Assert.Equal(1, curseAgg.Plays);
        Assert.Equal(1, curseAgg.TimesExhausted);
    }

    [Fact]
    public void ResumableLoad_AcceptsBloodSoakedRoseRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("blood-soaked-rose-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.BLOOD_SOAKED_ROSE"];
        var curseAgg = resumed.Aggregates["CARD.ENTHRALLED#1"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(9, relicAgg.EnergyGenerated);
        Assert.Equal(3, curseAgg.CombatsInDeck);
        Assert.Equal(5, curseAgg.TimesDrawn);
        Assert.Equal(2, curseAgg.TimesDiscarded);
        Assert.Equal(1, curseAgg.Plays);
        Assert.Equal(1, curseAgg.TimesExhausted);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPlanisphereRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("planisphere-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.PLANISPHERE"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(15m, relicAgg.TotalHealingAttempted);
        Assert.Equal(11m, relicAgg.TotalHealingRestored);
        Assert.Equal(4m, relicAgg.TotalHealingLost);
        Assert.Equal(4m, relicAgg.HealingLostReasons["full_hp"].Amount);
    }

    [Fact]
    public void ResumableLoad_AcceptsPlanisphereRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("planisphere-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.PLANISPHERE"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(15m, relicAgg.TotalHealingAttempted);
        Assert.Equal(11m, relicAgg.TotalHealingRestored);
        Assert.Equal(4m, relicAgg.TotalHealingLost);
        Assert.Equal(4m, relicAgg.HealingLostReasons["full_hp"].Amount);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLizardTailRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("lizard-tail-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.LIZARD_TAIL"];
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(7, relicAgg.FloorAcquired);
        Assert.Equal(19, relicAgg.FloorActivated);
        Assert.Equal(36m, relicAgg.TotalHealingAttempted);
        Assert.Equal(36m, relicAgg.TotalHealingRestored);
        Assert.Equal(0m, relicAgg.TotalHealingLost);
    }

    [Fact]
    public void ResumableLoad_AcceptsLizardTailRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("lizard-tail-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.LIZARD_TAIL"];
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(7, relicAgg.FloorAcquired);
        Assert.Equal(19, relicAgg.FloorActivated);
        Assert.Equal(36m, relicAgg.TotalHealingAttempted);
        Assert.Equal(36m, relicAgg.TotalHealingRestored);
        Assert.Equal(0m, relicAgg.TotalHealingLost);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPantographRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("pantograph-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.PANTOGRAPH"];
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(50m, relicAgg.TotalHealingAttempted);
        Assert.Equal(31m, relicAgg.TotalHealingRestored);
        Assert.Equal(19m, relicAgg.TotalHealingLost);
        Assert.Equal(19m, relicAgg.HealingLostReasons["full_hp"].Amount);
    }

    [Fact]
    public void ResumableLoad_AcceptsPantographRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("pantograph-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.PANTOGRAPH"];
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(50m, relicAgg.TotalHealingAttempted);
        Assert.Equal(31m, relicAgg.TotalHealingRestored);
        Assert.Equal(19m, relicAgg.TotalHealingLost);
        Assert.Equal(19m, relicAgg.HealingLostReasons["full_hp"].Amount);
    }

    [Fact]
    public void HistoricalLoad_AcceptsHeftyTabletRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("hefty-tablet-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.HEFTY_TABLET"];
        Assert.Equal(1, relicAgg.CardsGranted["CARD.ADRENALINE"].Count);
        Assert.Equal("Adrenaline", relicAgg.CardsGranted["CARD.ADRENALINE"].DisplayName);
        Assert.Equal(1, relicAgg.CardChoicesSkipped);
    }

    [Fact]
    public void ResumableLoad_AcceptsHeftyTabletRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("hefty-tablet-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.HEFTY_TABLET"];
        Assert.Equal(1, relicAgg.CardsGranted["CARD.ADRENALINE"].Count);
        Assert.Equal("Adrenaline", relicAgg.CardsGranted["CARD.ADRENALINE"].DisplayName);
        Assert.Equal(1, relicAgg.CardChoicesSkipped);
    }

    [Fact]
    public void HistoricalLoad_AcceptsArcaneScrollRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("arcane-scroll-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.ARCANE_SCROLL"];
        Assert.Equal(1, relicAgg.CardsGranted["CARD.ADRENALINE"].Count);
        Assert.Equal("Adrenaline", relicAgg.CardsGranted["CARD.ADRENALINE"].DisplayName);
    }

    [Fact]
    public void ResumableLoad_AcceptsArcaneScrollRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("arcane-scroll-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.ARCANE_SCROLL"];
        Assert.Equal(1, relicAgg.CardsGranted["CARD.ADRENALINE"].Count);
        Assert.Equal("Adrenaline", relicAgg.CardsGranted["CARD.ADRENALINE"].DisplayName);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLargeCapsuleRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("large-capsule-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.LARGE_CAPSULE"];
        Assert.Equal(1, relicAgg.RelicsGranted["RELIC.DATA_DISK"].Count);
        Assert.Equal("Data Disk", relicAgg.RelicsGranted["RELIC.DATA_DISK"].DisplayName);
        Assert.Equal(1, relicAgg.RelicsGranted["RELIC.BAG_OF_PREPARATION"].Count);
        Assert.Equal("Bag of Preparation", relicAgg.RelicsGranted["RELIC.BAG_OF_PREPARATION"].DisplayName);
    }

    [Fact]
    public void ResumableLoad_AcceptsLargeCapsuleRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("large-capsule-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.LARGE_CAPSULE"];
        Assert.Equal(1, relicAgg.RelicsGranted["RELIC.DATA_DISK"].Count);
        Assert.Equal("Data Disk", relicAgg.RelicsGranted["RELIC.DATA_DISK"].DisplayName);
        Assert.Equal(1, relicAgg.RelicsGranted["RELIC.BAG_OF_PREPARATION"].Count);
        Assert.Equal("Bag of Preparation", relicAgg.RelicsGranted["RELIC.BAG_OF_PREPARATION"].DisplayName);
    }

    [Fact]
    public void HistoricalLoad_AcceptsNeowsBonesRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("neows-bones-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.NEOWS_BONES"];
        Assert.Equal(1, relicAgg.RelicsGranted["RELIC.NEOWS_TALISMAN"].Count);
        Assert.Equal("Neow's Talisman", relicAgg.RelicsGranted["RELIC.NEOWS_TALISMAN"].DisplayName);
        Assert.Equal(1, relicAgg.RelicsGranted["RELIC.NEOWS_TORMENT"].Count);
        Assert.Equal("Neow's Torment", relicAgg.RelicsGranted["RELIC.NEOWS_TORMENT"].DisplayName);
        Assert.Equal(1, relicAgg.CardsGranted["CARD.INJURY"].Count);
        Assert.Equal("Injury", relicAgg.CardsGranted["CARD.INJURY"].DisplayName);

        var curseAgg = loaded.Data.Aggregates["CARD.INJURY#1"];
        Assert.Equal(3, curseAgg.CombatsInDeck);
        Assert.Equal(5, curseAgg.TimesDrawn);
        Assert.Equal(2, curseAgg.TimesDiscarded);
        Assert.Equal(1, curseAgg.Plays);
        Assert.Equal(2, curseAgg.TimesExhausted);
    }

    [Fact]
    public void ResumableLoad_AcceptsNeowsBonesRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("neows-bones-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.NEOWS_BONES"];
        Assert.Equal(1, relicAgg.RelicsGranted["RELIC.NEOWS_TALISMAN"].Count);
        Assert.Equal("Neow's Talisman", relicAgg.RelicsGranted["RELIC.NEOWS_TALISMAN"].DisplayName);
        Assert.Equal(1, relicAgg.RelicsGranted["RELIC.NEOWS_TORMENT"].Count);
        Assert.Equal("Neow's Torment", relicAgg.RelicsGranted["RELIC.NEOWS_TORMENT"].DisplayName);
        Assert.Equal(1, relicAgg.CardsGranted["CARD.INJURY"].Count);
        Assert.Equal("Injury", relicAgg.CardsGranted["CARD.INJURY"].DisplayName);

        var curseAgg = resumed.Aggregates["CARD.INJURY#1"];
        Assert.Equal(3, curseAgg.CombatsInDeck);
        Assert.Equal(5, curseAgg.TimesDrawn);
        Assert.Equal(2, curseAgg.TimesDiscarded);
        Assert.Equal(1, curseAgg.Plays);
        Assert.Equal(2, curseAgg.TimesExhausted);
    }

    [Fact]
    public void HistoricalLoad_AcceptsVambraceRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("vambrace-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.VAMBRACE"];
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(13, relicAgg.AdditionalBlockGained);
    }

    [Fact]
    public void ResumableLoad_AcceptsVambraceRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("vambrace-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.VAMBRACE"];
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(13, relicAgg.AdditionalBlockGained);
    }

    [Fact]
    public void HistoricalLoad_AcceptsRegaliteRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("regalite-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.REGALITE"];
        Assert.Equal(6, relicAgg.RegaliteCardsCreated);
        Assert.Equal(12, relicAgg.AdditionalBlockGained);
        Assert.Equal(4, relicAgg.RegaliteTurns);
        Assert.Equal(2, relicAgg.RegaliteCombats);
    }

    [Fact]
    public void ResumableLoad_AcceptsRegaliteRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("regalite-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.REGALITE"];
        Assert.Equal(6, relicAgg.RegaliteCardsCreated);
        Assert.Equal(12, relicAgg.AdditionalBlockGained);
        Assert.Equal(4, relicAgg.RegaliteTurns);
        Assert.Equal(2, relicAgg.RegaliteCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsIntimidatingHelmetRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("intimidating-helmet-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.INTIMIDATING_HELMET"];
        Assert.Equal(6, relicAgg.Activations);
        Assert.Equal(30, relicAgg.AdditionalBlockGained);
        Assert.Equal(8, relicAgg.IntimidatingHelmetTurns);
        Assert.Equal(3, relicAgg.IntimidatingHelmetCombats);
    }

    [Fact]
    public void ResumableLoad_AcceptsIntimidatingHelmetRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("intimidating-helmet-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.INTIMIDATING_HELMET"];
        Assert.Equal(6, relicAgg.Activations);
        Assert.Equal(30, relicAgg.AdditionalBlockGained);
        Assert.Equal(8, relicAgg.IntimidatingHelmetTurns);
        Assert.Equal(3, relicAgg.IntimidatingHelmetCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsTuningForkRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("tuning-fork-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.TUNING_FORK"];
        Assert.Equal(27, relicAgg.TuningForkSkillsPlayed);
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(18, relicAgg.AdditionalBlockGained);
        Assert.Equal(2, relicAgg.TuningForkCombats);
        Assert.Equal(7, relicAgg.TuningForkTurns);
        Assert.Equal(2, relicAgg.TuningForkTurnsEndedOn8Charges);
        Assert.Equal(1, relicAgg.TuningForkTurnsEndedOn9Charges);
        Assert.Equal(31, relicAgg.TuningForkTurnEndChargeTotal);
        Assert.Equal(5, relicAgg.TuningForkTurnEndChargeCount);
    }

    [Fact]
    public void HistoricalLoad_AcceptsRippleBasinRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("ripple-basin-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.RIPPLE_BASIN"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(12, relicAgg.AdditionalBlockGained);
        Assert.Equal(2, relicAgg.RippleBasinCombats);
        Assert.Equal(6, relicAgg.RippleBasinTurns);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPaelsEyeRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("paels-eye-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.PAELS_EYE"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(11, relicAgg.PaelsEyeCardsExhausted);
        Assert.Equal(3, relicAgg.PaelsEyeStrikesAndDefendsExhausted);
        Assert.Equal(7, relicAgg.PaelsEyeActivationTurnTotal);
        Assert.Equal(3, relicAgg.PaelsEyeActivationTurnSamples);
        Assert.Equal(8, relicAgg.PaelsEyeCombats);
        Assert.Equal(4, relicAgg.StatusCardsExhausted);
        Assert.Equal(2, relicAgg.CurseCardsExhausted);
        Assert.Equal(5, relicAgg.CombatsWithoutActivation);
    }

    [Fact]
    public void ResumableLoad_AcceptsPaelsEyeRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("paels-eye-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.PAELS_EYE"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(11, relicAgg.PaelsEyeCardsExhausted);
        Assert.Equal(3, relicAgg.PaelsEyeStrikesAndDefendsExhausted);
        Assert.Equal(7, relicAgg.PaelsEyeActivationTurnTotal);
        Assert.Equal(3, relicAgg.PaelsEyeActivationTurnSamples);
        Assert.Equal(8, relicAgg.PaelsEyeCombats);
        Assert.Equal(4, relicAgg.StatusCardsExhausted);
        Assert.Equal(2, relicAgg.CurseCardsExhausted);
        Assert.Equal(5, relicAgg.CombatsWithoutActivation);
    }

    [Fact]
    public void ResumableLoad_AcceptsTuningForkRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("tuning-fork-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.TUNING_FORK"];
        Assert.Equal(27, relicAgg.TuningForkSkillsPlayed);
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(18, relicAgg.AdditionalBlockGained);
        Assert.Equal(2, relicAgg.TuningForkCombats);
        Assert.Equal(7, relicAgg.TuningForkTurns);
        Assert.Equal(2, relicAgg.TuningForkTurnsEndedOn8Charges);
        Assert.Equal(1, relicAgg.TuningForkTurnsEndedOn9Charges);
        Assert.Equal(31, relicAgg.TuningForkTurnEndChargeTotal);
        Assert.Equal(5, relicAgg.TuningForkTurnEndChargeCount);
    }

    [Fact]
    public void ResumableLoad_AcceptsRippleBasinRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("ripple-basin-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.RIPPLE_BASIN"];
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(12, relicAgg.AdditionalBlockGained);
        Assert.Equal(2, relicAgg.RippleBasinCombats);
        Assert.Equal(6, relicAgg.RippleBasinTurns);
    }

    [Fact]
    public void HistoricalLoad_AcceptsWarPaintRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("war-paint-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.WAR_PAINT"];
        Assert.Equal(2, relicAgg.CardsUpgraded);
        Assert.Equal(new[] { "Defend+", "Battle Trance+" }, relicAgg.UpgradedCards);
    }

    [Fact]
    public void ResumableLoad_AcceptsWarPaintRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("war-paint-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.WAR_PAINT"];
        Assert.Equal(2, relicAgg.CardsUpgraded);
        Assert.Equal(new[] { "Defend+", "Battle Trance+" }, relicAgg.UpgradedCards);
    }

    [Fact]
    public void HistoricalLoad_AcceptsMiniatureCannonRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("miniature-cannon-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.MINIATURE_CANNON"];
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(3, relicAgg.MiniatureCannonUpgradedAttacksInDeck);
        Assert.Equal(5, relicAgg.MiniatureCannonNonUpgradedAttacksInDeck);
        Assert.Equal(10, relicAgg.MiniatureCannonUpgradedAttackPlays);
        Assert.Equal(17, relicAgg.MiniatureCannonUpgradedAttackHits);
    }

    [Fact]
    public void ResumableLoad_AcceptsMiniatureCannonRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("miniature-cannon-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.MINIATURE_CANNON"];
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(3, relicAgg.MiniatureCannonUpgradedAttacksInDeck);
        Assert.Equal(5, relicAgg.MiniatureCannonNonUpgradedAttacksInDeck);
        Assert.Equal(10, relicAgg.MiniatureCannonUpgradedAttackPlays);
        Assert.Equal(17, relicAgg.MiniatureCannonUpgradedAttackHits);
    }

    [Fact]
    public void HistoricalLoad_AcceptsVajraRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("vajra-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.VAJRA"];
        Assert.Equal(6, relicAgg.VajraAttacksPlayed);
        Assert.Equal(11, relicAgg.VajraAttackHits);
    }

    [Fact]
    public void ResumableLoad_AcceptsVajraRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("vajra-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.VAJRA"];
        Assert.Equal(6, relicAgg.VajraAttacksPlayed);
        Assert.Equal(11, relicAgg.VajraAttackHits);
    }

    [Fact]
    public void HistoricalLoad_AcceptsEmberTeaRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("ember-tea-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.EMBER_TEA"];
        Assert.Equal(14, relicAgg.EmberTeaAttacksPlayedWhileActive);
        Assert.Equal(22, relicAgg.EmberTeaHitsWhileActive);
        Assert.Equal(6, relicAgg.EmberTeaActiveTurns);
        Assert.Equal(2, relicAgg.EmberTeaActiveCombats);
    }

    [Fact]
    public void ResumableLoad_AcceptsEmberTeaRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("ember-tea-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.EMBER_TEA"];
        Assert.Equal(14, relicAgg.EmberTeaAttacksPlayedWhileActive);
        Assert.Equal(22, relicAgg.EmberTeaHitsWhileActive);
        Assert.Equal(6, relicAgg.EmberTeaActiveTurns);
        Assert.Equal(2, relicAgg.EmberTeaActiveCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsRedSkullRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("red-skull-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.RED_SKULL"];
        Assert.Equal(9, relicAgg.RedSkullAttacksPlayedWhileActive);
        Assert.Equal(17, relicAgg.RedSkullHitsWhileActive);
        Assert.Equal(5, relicAgg.RedSkullActiveTurns);
        Assert.Equal(3, relicAgg.RedSkullActiveCombats);
    }

    [Fact]
    public void ResumableLoad_AcceptsRedSkullRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("red-skull-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.RED_SKULL"];
        Assert.Equal(9, relicAgg.RedSkullAttacksPlayedWhileActive);
        Assert.Equal(17, relicAgg.RedSkullHitsWhileActive);
        Assert.Equal(5, relicAgg.RedSkullActiveTurns);
        Assert.Equal(3, relicAgg.RedSkullActiveCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsOrichalcumBlockMissedFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("orichalcum-block-missed-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.ORICHALCUM"];
        Assert.Equal(18, relicAgg.AdditionalBlockGained);
        Assert.Equal(5, relicAgg.BlockedTriggers);
        Assert.Equal(4, relicAgg.OrichalcumTurnsUndercut);
        Assert.Equal(11, relicAgg.OrichalcumBlockMissed);
        Assert.Equal(14, relicAgg.OrichalcumTurns);
        Assert.Equal(3, relicAgg.OrichalcumCombats);
    }

    [Fact]
    public void ResumableLoad_AcceptsOrichalcumBlockMissedFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("orichalcum-block-missed-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.ORICHALCUM"];
        Assert.Equal(4, relicAgg.OrichalcumTurnsUndercut);
        Assert.Equal(11, relicAgg.OrichalcumBlockMissed);
        Assert.Equal(14, relicAgg.OrichalcumTurns);
        Assert.Equal(3, relicAgg.OrichalcumCombats);
    }

    [Fact]
    public void OlderOrichalcumFixture_DefaultsBlockMissedFieldsToZero()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("v27-orichalcum-blocked-and-combats-in-deck-run.json"));

        Assert.NotNull(loaded);
        var relicAgg = loaded!.Data.RelicAggregates["RELIC.ORICHALCUM"];
        Assert.Equal(4, relicAgg.BlockedTriggers);
        Assert.Equal(0, relicAgg.OrichalcumTurnsUndercut);
        Assert.Equal(0, relicAgg.OrichalcumBlockMissed);
        Assert.Equal(0, relicAgg.OrichalcumTurns);
        Assert.Equal(0, relicAgg.OrichalcumCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsToastyMittensRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("toasty-mittens-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.TOASTY_MITTENS"];
        Assert.Equal(7, relicAgg.ToastyMittensCardsExhausted);
        Assert.Equal(10m, relicAgg.StrengthAdded);
        Assert.Equal(4, relicAgg.ToastyMittensCombats);
    }

    [Fact]
    public void ResumableLoad_AcceptsToastyMittensRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("toasty-mittens-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.TOASTY_MITTENS"];
        Assert.Equal(7, relicAgg.ToastyMittensCardsExhausted);
        Assert.Equal(10m, relicAgg.StrengthAdded);
        Assert.Equal(4, relicAgg.ToastyMittensCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsKunaiRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("kunai-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.KUNAI"];
        Assert.Equal(14, relicAgg.KunaiAttacksPlayed);
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(4, relicAgg.KunaiDexterityGained);
        Assert.Equal(2, relicAgg.KunaiTurnsEndedAt1Charge);
        Assert.Equal(3, relicAgg.KunaiTurnsEndedAt2Charges);
        Assert.Equal(11, relicAgg.KunaiTurnEndChargeTotal);
        Assert.Equal(7, relicAgg.KunaiTurnEndChargeCount);
    }

    [Fact]
    public void ResumableLoad_AcceptsKunaiRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("kunai-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.KUNAI"];
        Assert.Equal(14, relicAgg.KunaiAttacksPlayed);
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(4, relicAgg.KunaiDexterityGained);
        Assert.Equal(2, relicAgg.KunaiTurnsEndedAt1Charge);
        Assert.Equal(3, relicAgg.KunaiTurnsEndedAt2Charges);
        Assert.Equal(11, relicAgg.KunaiTurnEndChargeTotal);
        Assert.Equal(7, relicAgg.KunaiTurnEndChargeCount);
    }

    [Fact]
    public void HistoricalLoad_AcceptsUnlimitedAttackChargeRelicsFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("unlimited-attack-charge-relics-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);

        var kusarigama = loaded.Data.RelicAggregates["RELIC.KUSARIGAMA"];
        Assert.Equal(14, kusarigama.KusarigamaAttacksPlayed);
        Assert.Equal(4, kusarigama.Activations);
        Assert.Equal(24, kusarigama.TotalDamageAttempted);
        Assert.Equal(17, kusarigama.TotalDamageDealt);
        Assert.Equal(3, kusarigama.TotalDamageBlocked);
        Assert.Equal(4, kusarigama.TotalDamageOverkill);
        Assert.Equal(2, kusarigama.Kills);
        Assert.Equal(4, kusarigama.TotalTargets);
        Assert.Equal(2, kusarigama.KusarigamaTurnsEndedAt1Charge);
        Assert.Equal(3, kusarigama.KusarigamaTurnsEndedAt2Charges);
        Assert.Equal(8, kusarigama.KusarigamaTurnEndChargeTotal);
        Assert.Equal(7, kusarigama.KusarigamaTurnEndChargeCount);

        var ornamentalFan = loaded.Data.RelicAggregates["RELIC.ORNAMENTAL_FAN"];
        Assert.Equal(11, ornamentalFan.OrnamentalFanAttacksPlayed);
        Assert.Equal(3, ornamentalFan.Activations);
        Assert.Equal(13, ornamentalFan.AdditionalBlockGained);
        Assert.Equal(1, ornamentalFan.OrnamentalFanTurnsEndedAt0Charges);
        Assert.Equal(1, ornamentalFan.OrnamentalFanTurnsEndedAt1Charge);
        Assert.Equal(3, ornamentalFan.OrnamentalFanTurnsEndedAt2Charges);
        Assert.Equal(7, ornamentalFan.OrnamentalFanTurnEndChargeTotal);
        Assert.Equal(5, ornamentalFan.OrnamentalFanTurnEndChargeCount);

        var shuriken = loaded.Data.RelicAggregates["RELIC.SHURIKEN"];
        Assert.Equal(17, shuriken.ShurikenAttacksPlayed);
        Assert.Equal(5, shuriken.Activations);
        Assert.Equal(5m, shuriken.StrengthAdded);
        Assert.Equal(2, shuriken.ShurikenTurnsEndedAt1Charge);
        Assert.Equal(2, shuriken.ShurikenTurnsEndedAt2Charges);
        Assert.Equal(6, shuriken.ShurikenTurnEndChargeTotal);
        Assert.Equal(6, shuriken.ShurikenTurnEndChargeCount);
    }

    [Fact]
    public void ResumableLoad_AcceptsUnlimitedAttackChargeRelicsFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("unlimited-attack-charge-relics-run.json"));

        Assert.NotNull(resumed);

        var kusarigama = resumed!.RelicAggregates["RELIC.KUSARIGAMA"];
        Assert.Equal(14, kusarigama.KusarigamaAttacksPlayed);
        Assert.Equal(4, kusarigama.Activations);
        Assert.Equal(24, kusarigama.TotalDamageAttempted);
        Assert.Equal(17, kusarigama.TotalDamageDealt);
        Assert.Equal(3, kusarigama.TotalDamageBlocked);
        Assert.Equal(4, kusarigama.TotalDamageOverkill);
        Assert.Equal(2, kusarigama.Kills);
        Assert.Equal(4, kusarigama.TotalTargets);
        Assert.Equal(2, kusarigama.KusarigamaTurnsEndedAt1Charge);
        Assert.Equal(3, kusarigama.KusarigamaTurnsEndedAt2Charges);
        Assert.Equal(8, kusarigama.KusarigamaTurnEndChargeTotal);
        Assert.Equal(7, kusarigama.KusarigamaTurnEndChargeCount);

        var ornamentalFan = resumed.RelicAggregates["RELIC.ORNAMENTAL_FAN"];
        Assert.Equal(11, ornamentalFan.OrnamentalFanAttacksPlayed);
        Assert.Equal(3, ornamentalFan.Activations);
        Assert.Equal(13, ornamentalFan.AdditionalBlockGained);
        Assert.Equal(1, ornamentalFan.OrnamentalFanTurnsEndedAt0Charges);
        Assert.Equal(1, ornamentalFan.OrnamentalFanTurnsEndedAt1Charge);
        Assert.Equal(3, ornamentalFan.OrnamentalFanTurnsEndedAt2Charges);
        Assert.Equal(7, ornamentalFan.OrnamentalFanTurnEndChargeTotal);
        Assert.Equal(5, ornamentalFan.OrnamentalFanTurnEndChargeCount);

        var shuriken = resumed.RelicAggregates["RELIC.SHURIKEN"];
        Assert.Equal(17, shuriken.ShurikenAttacksPlayed);
        Assert.Equal(5, shuriken.Activations);
        Assert.Equal(5m, shuriken.StrengthAdded);
        Assert.Equal(2, shuriken.ShurikenTurnsEndedAt1Charge);
        Assert.Equal(2, shuriken.ShurikenTurnsEndedAt2Charges);
        Assert.Equal(6, shuriken.ShurikenTurnEndChargeTotal);
        Assert.Equal(6, shuriken.ShurikenTurnEndChargeCount);
    }

    [Fact]
    public void HistoricalLoad_AcceptsThreeAttackScalingRatesFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("three-attack-scaling-rates-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertThreeAttackScalingRatesFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsThreeAttackScalingRatesFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("three-attack-scaling-rates-run.json"));

        Assert.NotNull(resumed);
        AssertThreeAttackScalingRatesFixture(resumed!);
    }

    private static void AssertThreeAttackScalingRatesFixture(RunData run)
    {
        var kunai = run.RelicAggregates["RELIC.KUNAI"];
        Assert.Equal(20, kunai.KunaiAttacksPlayed);
        Assert.Equal(6, kunai.Activations);
        Assert.Equal(6, kunai.KunaiDexterityGained);
        Assert.Equal(4, kunai.ThreeAttackScalingRateActivations);
        Assert.Equal(6, kunai.ThreeAttackScalingTurns);
        Assert.Equal(2, kunai.ThreeAttackScalingCombats);

        var shuriken = run.RelicAggregates["RELIC.SHURIKEN"];
        Assert.Equal(20, shuriken.ShurikenAttacksPlayed);
        Assert.Equal(6, shuriken.Activations);
        Assert.Equal(6m, shuriken.StrengthAdded);
        Assert.Equal(4, shuriken.ThreeAttackScalingRateActivations);
        Assert.Equal(6, shuriken.ThreeAttackScalingTurns);
        Assert.Equal(2, shuriken.ThreeAttackScalingCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsOrnamentalFanBlockAttributionFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("ornamental-fan-block-attribution-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertOrnamentalFanBlockAttributionFixture(
            loaded.Data.RelicAggregates["RELIC.ORNAMENTAL_FAN"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsOrnamentalFanBlockAttributionFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("ornamental-fan-block-attribution-run.json"));

        Assert.NotNull(resumed);
        AssertOrnamentalFanBlockAttributionFixture(
            resumed!.RelicAggregates["RELIC.ORNAMENTAL_FAN"]);
    }

    private static void AssertOrnamentalFanBlockAttributionFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(17, relicAgg.OrnamentalFanAttacksPlayed);
        Assert.Equal(5, relicAgg.Activations);
        Assert.Equal(20, relicAgg.AdditionalBlockGained);
        Assert.Equal(14, relicAgg.AdditionalBlockEffective);
        Assert.Equal(6, relicAgg.AdditionalBlockWasted);
        Assert.Equal(20, relicAgg.OrnamentalFanRateBlockGained);
        Assert.Equal(8, relicAgg.OrnamentalFanTurns);
        Assert.Equal(3, relicAgg.OrnamentalFanCombats);
        Assert.Equal(2, relicAgg.OrnamentalFanTurnsEndedAt0Charges);
        Assert.Equal(3, relicAgg.OrnamentalFanTurnsEndedAt1Charge);
        Assert.Equal(3, relicAgg.OrnamentalFanTurnsEndedAt2Charges);
        Assert.Equal(9, relicAgg.OrnamentalFanTurnEndChargeTotal);
        Assert.Equal(8, relicAgg.OrnamentalFanTurnEndChargeCount);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPaperPhrogRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("paper-phrog-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.PAPER_PHROG"];
        Assert.Equal(18.75m, relicAgg.PaperPhrogDamageAdded);
        Assert.Equal(6, relicAgg.PaperPhrogEnhancedAttacks);
        Assert.Equal(3, relicAgg.PaperPhrogCombats);
        Assert.Equal(5, relicAgg.PaperPhrogTurns);
    }

    [Fact]
    public void ResumableLoad_AcceptsPaperPhrogRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("paper-phrog-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.PAPER_PHROG"];
        Assert.Equal(18.75m, relicAgg.PaperPhrogDamageAdded);
        Assert.Equal(6, relicAgg.PaperPhrogEnhancedAttacks);
        Assert.Equal(3, relicAgg.PaperPhrogCombats);
        Assert.Equal(5, relicAgg.PaperPhrogTurns);
    }

    [Fact]
    public void HistoricalLoad_AcceptsRazorToothRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("razor-tooth-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.RAZOR_TOOTH"];
        Assert.Equal(6, relicAgg.CardsUpgraded);
        Assert.Equal(2, relicAgg.RazorToothCombats);
        Assert.Equal(8, relicAgg.RazorToothTurns);
        Assert.Equal(4, relicAgg.RazorToothUpgradedCardPlays);
        Assert.Equal(2, relicAgg.RazorToothUpgradedCardDraws);
    }

    [Fact]
    public void ResumableLoad_AcceptsRazorToothRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("razor-tooth-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.RAZOR_TOOTH"];
        Assert.Equal(6, relicAgg.CardsUpgraded);
        Assert.Equal(2, relicAgg.RazorToothCombats);
        Assert.Equal(8, relicAgg.RazorToothTurns);
        Assert.Equal(4, relicAgg.RazorToothUpgradedCardPlays);
        Assert.Equal(2, relicAgg.RazorToothUpgradedCardDraws);
    }

    [Fact]
    public void HistoricalLoad_AcceptsStorybookRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("storybook-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.Data.RelicAggregates.ContainsKey("RELIC.STORYBOOK"));
        var cardAgg = loaded.Data.Aggregates["CARD.BRIGHTEST_FLAME#1"];
        Assert.Equal(4, cardAgg.Plays);
        Assert.Equal(6, cardAgg.TimesDrawn);
        Assert.Equal(8, cardAgg.TotalEnergyGenerated);
        Assert.Equal(8, cardAgg.TimesCardsDrawn);
        Assert.Equal(4, cardAgg.TotalMaxHpLost);
    }

    [Fact]
    public void ResumableLoad_AcceptsStorybookRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("storybook-relic-run.json"));

        Assert.NotNull(resumed);
        Assert.True(resumed!.RelicAggregates.ContainsKey("RELIC.STORYBOOK"));
        var cardAgg = resumed.Aggregates["CARD.BRIGHTEST_FLAME#1"];
        Assert.Equal(4, cardAgg.Plays);
        Assert.Equal(6, cardAgg.TimesDrawn);
        Assert.Equal(8, cardAgg.TotalEnergyGenerated);
        Assert.Equal(8, cardAgg.TimesCardsDrawn);
        Assert.Equal(4, cardAgg.TotalMaxHpLost);
        Assert.Equal(new[] { 1 }, resumed.InstanceNumbersByDef["CARD.BRIGHTEST_FLAME"]);
        Assert.Equal(1, resumed.DefCounters["CARD.BRIGHTEST_FLAME"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsBookmarkRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("bookmark-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var relicAgg = loaded.Data.RelicAggregates["RELIC.BOOKMARK"];
        Assert.Equal(7, relicAgg.Activations);
        Assert.Equal(4, relicAgg.BookmarkCombats);
        Assert.Equal(2, relicAgg.BookmarkCommonActivations);
        Assert.Equal(3, relicAgg.BookmarkUncommonActivations);
        Assert.Equal(2, relicAgg.BookmarkRareActivations);
    }

    [Fact]
    public void ResumableLoad_AcceptsBookmarkRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("bookmark-relic-run.json"));

        Assert.NotNull(resumed);
        var relicAgg = resumed!.RelicAggregates["RELIC.BOOKMARK"];
        Assert.Equal(7, relicAgg.Activations);
        Assert.Equal(4, relicAgg.BookmarkCombats);
        Assert.Equal(2, relicAgg.BookmarkCommonActivations);
        Assert.Equal(3, relicAgg.BookmarkUncommonActivations);
        Assert.Equal(2, relicAgg.BookmarkRareActivations);
    }

    [Fact]
    public void HistoricalLoad_AcceptsFresnelLensRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("fresnel-lens-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertFresnelLensFixture(loaded.Data.RelicAggregates["RELIC.FRESNEL_LENS"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsFresnelLensRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("fresnel-lens-relic-run.json"));

        Assert.NotNull(resumed);
        AssertFresnelLensFixture(resumed!.RelicAggregates["RELIC.FRESNEL_LENS"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsWingCharmRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("wing-charm-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertWingCharmFixture(
            loaded.Data.RelicAggregates["RELIC.WING_CHARM"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsWingCharmRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("wing-charm-relic-run.json"));

        Assert.NotNull(resumed);
        AssertWingCharmFixture(
            resumed!.RelicAggregates["RELIC.WING_CHARM"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSilverCrucibleRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("silver-crucible-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertSilverCrucibleFixture(loaded.Data.RelicAggregates["RELIC.SILVER_CRUCIBLE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsSilverCrucibleRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("silver-crucible-relic-run.json"));

        Assert.NotNull(resumed);
        AssertSilverCrucibleFixture(resumed!.RelicAggregates["RELIC.SILVER_CRUCIBLE"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsOrreryRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("orrery-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertOrreryFixture(loaded.Data.RelicAggregates["RELIC.ORRERY"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsOrreryRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("orrery-relic-run.json"));

        Assert.NotNull(resumed);
        AssertOrreryFixture(resumed!.RelicAggregates["RELIC.ORRERY"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsUnmovablePowerMetaFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("unmovable-power-meta-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.True(loaded.HasPerInstanceIdentity);
        Assert.Null(loaded.CompatibilityNote);
        Assert.Equal(24m, loaded.Data.MetaStats.ExtraBlockGainedFromUnmovablePower);
    }

    [Fact]
    public void ResumableLoad_AcceptsUnmovablePowerMetaFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("unmovable-power-meta-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(24m, resumed!.MetaStats.ExtraBlockGainedFromUnmovablePower);
    }

    [Fact]
    public void HistoricalLoad_AcceptsDowsingRodRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("dowsing-rod-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.Equal(
            2,
            loaded.Data.RelicAggregates["RELIC.DOWSING_ROD"].DowsingQuestionRoomsRemaining);
    }

    [Fact]
    public void ResumableLoad_AcceptsDowsingRodRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("dowsing-rod-relic-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(
            2,
            resumed!.RelicAggregates["RELIC.DOWSING_ROD"].DowsingQuestionRoomsRemaining);
    }

    [Fact]
    public void HistoricalLoad_AcceptsWingedBootsRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("winged-boots-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.Collection(
            loaded.Data.RelicAggregates["RELIC.WINGED_BOOTS"].WingedBootsDestinations,
            entry => Assert.Equal((1, "combat"), (entry.UseNumber, entry.Destination)),
            entry => Assert.Equal((2, "shop"), (entry.UseNumber, entry.Destination)),
            entry => Assert.Equal((3, "question_mark"), (entry.UseNumber, entry.Destination)));
    }

    [Fact]
    public void ResumableLoad_AcceptsWingedBootsRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("winged-boots-relic-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(
            new[] { "combat", "shop", "question_mark" },
            resumed!.RelicAggregates["RELIC.WINGED_BOOTS"]
                .WingedBootsDestinations
                .Select(entry => entry.Destination));
    }

    private static void AssertLeafyPoulticeTransformations(RelicAggregate relicAgg)
    {
        Assert.Equal(2, relicAgg.CardTransformations.Count);
        Assert.Equal("CARD.STRIKE_IRONCLAD", relicAgg.CardTransformations[0].SourceCardId);
        Assert.Equal("Strike", relicAgg.CardTransformations[0].SourceDisplayName);
        Assert.Equal("CARD.BASH", relicAgg.CardTransformations[0].ResultCardId);
        Assert.Equal("Bash", relicAgg.CardTransformations[0].ResultDisplayName);
        Assert.Equal("CARD.DEFEND_IRONCLAD", relicAgg.CardTransformations[1].SourceCardId);
        Assert.Equal("Defend", relicAgg.CardTransformations[1].SourceDisplayName);
        Assert.Equal("CARD.SHRUG_IT_OFF", relicAgg.CardTransformations[1].ResultCardId);
        Assert.Equal("Shrug It Off", relicAgg.CardTransformations[1].ResultDisplayName);
    }

    [Fact]
    public void HistoricalLoad_AcceptsMummifiedHandRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("mummified-hand-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertMummifiedHandFixture(loaded.Data.RelicAggregates["RELIC.MUMMIFIED_HAND"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsMummifiedHandRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("mummified-hand-relic-run.json"));

        Assert.NotNull(resumed);
        AssertMummifiedHandFixture(resumed!.RelicAggregates["RELIC.MUMMIFIED_HAND"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsBurningSticksRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("burning-sticks-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertBurningSticksFixture(loaded.Data.RelicAggregates["RELIC.BURNING_STICKS"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsBurningSticksRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("burning-sticks-relic-run.json"));

        Assert.NotNull(resumed);
        AssertBurningSticksFixture(resumed!.RelicAggregates["RELIC.BURNING_STICKS"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsGnarledHammerRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("gnarled-hammer-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertGnarledHammerFixture(loaded.Data.RelicAggregates["RELIC.GNARLED_HAMMER"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsGnarledHammerRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("gnarled-hammer-relic-run.json"));

        Assert.NotNull(resumed);
        AssertGnarledHammerFixture(resumed!.RelicAggregates["RELIC.GNARLED_HAMMER"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSilkenTressRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("silken-tress-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertSilkenTressFixture(
            loaded.Data.RelicAggregates["RELIC.SILKEN_TRESS"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsSilkenTressRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("silken-tress-relic-run.json"));

        Assert.NotNull(resumed);
        AssertSilkenTressFixture(
            resumed!.RelicAggregates["RELIC.SILKEN_TRESS"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsTriBoomerangRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("tri-boomerang-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertTriBoomerangFixture(
            loaded.Data.RelicAggregates["RELIC.TRI_BOOMERANG"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsTriBoomerangRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("tri-boomerang-relic-run.json"));

        Assert.NotNull(resumed);
        AssertTriBoomerangFixture(
            resumed!.RelicAggregates["RELIC.TRI_BOOMERANG"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsStoneHumidifierRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("stone-humidifier-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertStoneHumidifierFixture(loaded.Data.RelicAggregates["RELIC.STONE_HUMIDIFIER"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsStoneHumidifierRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("stone-humidifier-relic-run.json"));

        Assert.NotNull(resumed);
        AssertStoneHumidifierFixture(resumed!.RelicAggregates["RELIC.STONE_HUMIDIFIER"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSturdyClampRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("sturdy-clamp-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertSturdyClampFixture(loaded.Data.RelicAggregates["RELIC.STURDY_CLAMP"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsSturdyClampRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("sturdy-clamp-relic-run.json"));

        Assert.NotNull(resumed);
        AssertSturdyClampFixture(resumed!.RelicAggregates["RELIC.STURDY_CLAMP"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsBeatingRemnantRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("beating-remnant-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertBeatingRemnantFixture(
            loaded.Data.RelicAggregates["RELIC.BEATING_REMNANT"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsBeatingRemnantRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("beating-remnant-relic-run.json"));

        Assert.NotNull(resumed);
        AssertBeatingRemnantFixture(
            resumed!.RelicAggregates["RELIC.BEATING_REMNANT"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsWhisperingEarringRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("whispering-earring-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertWhisperingEarringFixture(
            loaded.Data.RelicAggregates["RELIC.WHISPERING_EARRING"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsWhisperingEarringRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("whispering-earring-relic-run.json"));

        Assert.NotNull(resumed);
        AssertWhisperingEarringFixture(
            resumed!.RelicAggregates["RELIC.WHISPERING_EARRING"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsTungstenRodRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("tungsten-rod-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertTungstenRodFixture(
            loaded.Data.RelicAggregates["RELIC.TUNGSTEN_ROD"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsTungstenRodRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("tungsten-rod-relic-run.json"));

        Assert.NotNull(resumed);
        AssertTungstenRodFixture(
            resumed!.RelicAggregates["RELIC.TUNGSTEN_ROD"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsRuinedHelmetRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("ruined-helmet-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertRuinedHelmetFixture(loaded.Data.RelicAggregates["RELIC.RUINED_HELMET"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsRuinedHelmetRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("ruined-helmet-relic-run.json"));

        Assert.NotNull(resumed);
        AssertRuinedHelmetFixture(resumed!.RelicAggregates["RELIC.RUINED_HELMET"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsDaughterOfTheWindRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("daughter-of-the-wind-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertDaughterOfTheWindFixture(
            loaded.Data.RelicAggregates["RELIC.DAUGHTER_OF_THE_WIND"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsDaughterOfTheWindRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("daughter-of-the-wind-relic-run.json"));

        Assert.NotNull(resumed);
        AssertDaughterOfTheWindFixture(
            resumed!.RelicAggregates["RELIC.DAUGHTER_OF_THE_WIND"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsArtOfWarRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("art-of-war-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertArtOfWarFixture(loaded.Data.RelicAggregates["RELIC.ART_OF_WAR"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsArtOfWarRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("art-of-war-relic-run.json"));

        Assert.NotNull(resumed);
        AssertArtOfWarFixture(resumed!.RelicAggregates["RELIC.ART_OF_WAR"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsCrackedCoreRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("cracked-core-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertCrackedCoreFixture(loaded.Data.RelicAggregates["RELIC.CRACKED_CORE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsCrackedCoreRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("cracked-core-relic-run.json"));

        Assert.NotNull(resumed);
        AssertCrackedCoreFixture(resumed!.RelicAggregates["RELIC.CRACKED_CORE"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSymbioticVirusRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("symbiotic-virus-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertSymbioticVirusFixture(
            loaded.Data.RelicAggregates["RELIC.SYMBIOTIC_VIRUS"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsSymbioticVirusRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("symbiotic-virus-relic-run.json"));

        Assert.NotNull(resumed);
        AssertSymbioticVirusFixture(
            resumed!.RelicAggregates["RELIC.SYMBIOTIC_VIRUS"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsBingBongRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("bing-bong-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertBingBongFixture(
            loaded.Data.RelicAggregates["RELIC.BING_BONG"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsBingBongRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("bing-bong-relic-run.json"));

        Assert.NotNull(resumed);
        AssertBingBongFixture(
            resumed!.RelicAggregates["RELIC.BING_BONG"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsGoldPlatedCablesRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("gold-plated-cables-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertGoldPlatedCablesFixture(
            loaded.Data.RelicAggregates["RELIC.GOLD_PLATED_CABLES"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsGoldPlatedCablesRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("gold-plated-cables-relic-run.json"));

        Assert.NotNull(resumed);
        AssertGoldPlatedCablesFixture(
            resumed!.RelicAggregates["RELIC.GOLD_PLATED_CABLES"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPaelsClawRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("paels-claw-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertPaelsClawFixture(loaded.Data.RelicAggregates["RELIC.PAELS_CLAW"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsPaelsClawRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("paels-claw-relic-run.json"));

        Assert.NotNull(resumed);
        AssertPaelsClawFixture(resumed!.RelicAggregates["RELIC.PAELS_CLAW"]);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSoulPileCardFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("soul-pile-card-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertSoulPileCardFixture(loaded.Data.Aggregates["CARD.SEVERANCE#1"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsSoulPileCardFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("soul-pile-card-run.json"));

        Assert.NotNull(resumed);
        AssertSoulPileCardFixture(resumed!.Aggregates["CARD.SEVERANCE#1"]);
    }

    private static void AssertReptileTrinketRatesFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(9, relicAgg.Activations);
        Assert.Equal(18m, relicAgg.StrengthAdded);
        Assert.Equal(6, relicAgg.ReptileTrinketTurns);
        Assert.Equal(3, relicAgg.ReptileTrinketCombats);
        Assert.Equal(2, relicAgg.ReptileTrinketTurnsWithExactlyTwoActivations);
        Assert.Equal(1, relicAgg.ReptileTrinketTurnsWithMoreThanTwoActivations);
    }

    private static void AssertRainbowRingFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(5, relicAgg.Activations);
        Assert.Equal(12, relicAgg.RainbowRingTurns);
        Assert.Equal(4, relicAgg.RainbowRingCombats);
    }

    private static void AssertSparklingRougeFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(2, relicAgg.SparklingRougeCombatsEndedOnTurn1);
        Assert.Equal(3, relicAgg.SparklingRougeCombatsEndedOnTurn2);
        Assert.Equal(4, relicAgg.SparklingRougeCombatsEndedOnTurn3Plus);
    }

    private static void AssertPaelsClawFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(7, relicAgg.PaelsClawGoopyCardsPlayed);
        Assert.Equal(5, relicAgg.PaelsClawGoopyEnhancements);
        Assert.Equal(4, relicAgg.PaelsClawGoopyCards);
        Assert.Equal(4, relicAgg.PaelsClawTurns);
        Assert.Equal(2, relicAgg.PaelsClawCombats);
    }

    private static void AssertDrainPowerFixture(CardAggregate cardAgg)
    {
        Assert.Equal(3, cardAgg.CombatsInDeck);
        Assert.Equal(8, cardAgg.DrainPowerCardsUpgraded);
        Assert.Equal(6, cardAgg.DrainPowerTurnsInDeck);
        Assert.Equal(9, cardAgg.DrainPowerUpgradedCardPlays);
    }

    private static void AssertAllForOneFixture(CardAggregate cardAgg)
    {
        Assert.Equal(4, cardAgg.CombatsInDeck);
        Assert.Equal(6, cardAgg.Plays);
        Assert.Equal(12, cardAgg.AllForOneZeroCostCardsReturned);
    }

    private static void AssertJugglingPowerFixture(PowerAggregate powerAgg)
    {
        Assert.Equal("POWER.JUGGLING", powerAgg.PowerId);
        Assert.Equal("Juggling", powerAgg.DisplayName);
        Assert.Equal(7, powerAgg.AttacksCopied);
        Assert.Equal(3, powerAgg.CommonAttacksCopied);
        Assert.Equal(2, powerAgg.UncommonAttacksCopied);
        Assert.Equal(2, powerAgg.RareAttacksCopied);
        Assert.Equal(5, powerAgg.TurnsActive);
        Assert.Equal(2, powerAgg.CombatsActive);
    }

    private static void AssertViciousPowerFixture(PowerAggregate powerAgg)
    {
        Assert.Equal("POWER.VICIOUS", powerAgg.PowerId);
        Assert.Equal("Vicious", powerAgg.DisplayName);
        Assert.Equal(11, powerAgg.ViciousCardsDrawn);
    }

    private static void AssertOutbreakCardFixture(CardAggregate cardAgg)
    {
        Assert.Equal(4, cardAgg.CombatsInDeck);
        Assert.Equal(3, cardAgg.Plays);
        Assert.Equal(37, cardAgg.OutbreakExtraPoisonTriggerDamage);
    }

    private static void AssertDarkEmbracePowerFixture(PowerAggregate powerAgg)
    {
        Assert.Equal("POWER.DARK_EMBRACE", powerAgg.PowerId);
        Assert.Equal("Dark Embrace", powerAgg.DisplayName);
        Assert.Equal(18, powerAgg.DarkEmbraceCardsDrawn);
        Assert.Equal(6, powerAgg.TurnsActive);
        Assert.Equal(9, powerAgg.DarkEmbraceCombatTurns);
        Assert.Equal(3, powerAgg.CombatsActive);
    }

    private static void AssertConsumingShadowPowerFixture(PowerAggregate powerAgg)
    {
        Assert.Equal("POWER.CONSUMING_SHADOW", powerAgg.PowerId);
        Assert.Equal("Consuming Shadow", powerAgg.DisplayName);
        Assert.Equal(3, powerAgg.PowerCardsPlayed);
        Assert.Equal(3, powerAgg.SuccessfulApplications);
        Assert.Equal(7, powerAgg.OrbsEvoked);
    }

    private static void AssertMetaPowerRegistryFixture(RunMetaStats metaStats)
    {
        var danse = metaStats.PowerAggregates["POWER.DANSE_MACABRE"];
        Assert.Equal(5, danse.PowerCardsPlayed);
        Assert.Equal(2, danse.GeneratedPowerCardsPlayed);
        Assert.Equal(4, danse.SuccessfulApplications);
        Assert.Equal(12, danse.MetaDeckTurns);
        Assert.Equal(8, danse.MetaActiveTurns);
        Assert.Equal(13, danse.MetaActiveApplicationTurns);
        Assert.Equal(9, danse.TimesTriggered);
        Assert.Equal(7, danse.RateTimesTriggered);
        Assert.Equal(31m, danse.BlockGained);
        Assert.Equal(24m, danse.RateBlockGained);

        var unmovable = metaStats.PowerAggregates["POWER.UNMOVABLE"];
        Assert.Equal(12m, unmovable.UnmovableExtraBlockGained);
        Assert.Equal(8m, unmovable.RateUnmovableExtraBlockGained);
    }

    private static void AssertStampedePowerFixture(PowerAggregate powerAgg)
    {
        Assert.Equal("POWER.STAMPEDE", powerAgg.PowerId);
        Assert.Equal("Stampede", powerAgg.DisplayName);
        Assert.Equal(9, powerAgg.StampedeAttacksPlayed);
        Assert.Equal(4, powerAgg.StampedeCommonAttacksPlayed);
        Assert.Equal(3, powerAgg.StampedeUncommonAttacksPlayed);
        Assert.Equal(2, powerAgg.StampedeRareAttacksPlayed);
        Assert.Equal(14, powerAgg.StampedeEnergySaved);
    }

    private static void AssertAggressionPowerFixture(PowerAggregate powerAgg)
    {
        Assert.Equal("POWER.AGGRESSION", powerAgg.PowerId);
        Assert.Equal("Aggression", powerAgg.DisplayName);
        Assert.Equal(8, powerAgg.AggressionCardsReturnedToHand);
        Assert.Equal(5, powerAgg.AggressionCardsUpgraded);
    }

    private static void AssertRupturePowerFixture(PowerAggregate powerAgg)
    {
        Assert.Equal("POWER.RUPTURE", powerAgg.PowerId);
        Assert.Equal("Rupture", powerAgg.DisplayName);
        Assert.Equal(18m, powerAgg.StrengthGained);
        Assert.Equal(6, powerAgg.TurnsActive);
    }

    private static void AssertFeelNoPainPowerFixture(PowerAggregate powerAgg)
    {
        Assert.Equal("POWER.FEEL_NO_PAIN", powerAgg.PowerId);
        Assert.Equal("Feel No Pain", powerAgg.DisplayName);
        Assert.Equal(36m, powerAgg.BlockGained);
        Assert.Equal(6, powerAgg.TurnsActive);
    }

    private static void AssertBufferChargeLedgerFixture(RunData run)
    {
        var card = run.Aggregates["CARD.BUFFER#1"];
        Assert.Equal(5, card.BufferChargesGranted);
        Assert.Equal(4, card.BufferChargesUsed);
        Assert.Equal(51m, card.BufferDamagePrevented);

        var potion = Assert.Single(run.PotionHistory!);
        Assert.Equal("POTION.LUCKY_TONIC", potion.PotionId);
        Assert.Equal(2, potion.BufferChargesGranted);
        Assert.Equal(1, potion.BufferChargesUsed);
        Assert.Equal(14m, potion.BufferDamagePrevented);

        // The pooled power total is the family sum, so it stays greater than
        // either contributor on its own.
        var powerAgg = run.MetaStats.PowerAggregates["POWER.BUFFER_POWER"];
        Assert.Equal(7, powerAgg.BufferChargesGranted);
        Assert.Equal(5, powerAgg.BufferChargesUsed);
        Assert.Equal(65m, powerAgg.BufferDamagePrevented);
    }

    private static void AssertBufferPowerFixture(PowerAggregate powerAgg)
    {
        Assert.Equal("POWER.BUFFER_POWER", powerAgg.PowerId);
        Assert.Equal("Buffer", powerAgg.DisplayName);
        Assert.Equal(3, powerAgg.PowerCardsPlayed);
        Assert.Equal(3, powerAgg.SuccessfulApplications);
        Assert.Equal(7, powerAgg.BufferChargesGranted);
        Assert.Equal(5, powerAgg.BufferChargesUsed);
        Assert.Equal(63m, powerAgg.BufferDamagePrevented);
        Assert.Equal(11, powerAgg.TurnsActive);
        Assert.Equal(4, powerAgg.CombatsActive);
    }

    private static void AssertEntropyPowerFixture(PowerAggregate powerAgg)
    {
        Assert.Equal("POWER.ENTROPY", powerAgg.PowerId);
        Assert.Equal("Entropy", powerAgg.DisplayName);
        Assert.Equal(2, powerAgg.EntropyChainsOfBindingBroken);
        Assert.Equal(7, powerAgg.EntropyCardsGenerated);
        Assert.Equal(3, powerAgg.EntropyCommonCardsGenerated);
        Assert.Equal(2, powerAgg.EntropyUncommonCardsGenerated);
        Assert.Equal(2, powerAgg.EntropyRareCardsGenerated);
        Assert.Equal(2, powerAgg.CombatsActive);
    }

    private static void AssertDanseMacabrePowerFixture(PowerAggregate powerAgg)
    {
        Assert.Equal("POWER.DANSE_MACABRE", powerAgg.PowerId);
        Assert.Equal("Danse Macabre", powerAgg.DisplayName);
        Assert.Equal(9, powerAgg.TimesTriggered);
        Assert.Equal(45m, powerAgg.BlockGained);
        Assert.Equal(6, powerAgg.TurnsActive);
        Assert.Equal(3, powerAgg.CombatsActive);
    }

    private static void AssertUnrelentingFreeAttackPowerFixture(PowerAggregate powerAgg)
    {
        Assert.Equal("POWER.FREE_ATTACK_POWER", powerAgg.PowerId);
        Assert.Equal("Free Attack", powerAgg.DisplayName);
        Assert.Equal(10, powerAgg.FreeAttackChargesGranted);
        Assert.Equal(8, powerAgg.FreeAttackChargesUsed);
        Assert.Equal(2, powerAgg.FreeAttackZeroEnergySavingsUses);
        Assert.Equal(13m, powerAgg.FreeAttackEnergySaved);
        Assert.Equal(2, powerAgg.FreeAttackBasicAttacksDiscounted);
        Assert.Equal(2, powerAgg.FreeAttackCommonAttacksDiscounted);
        Assert.Equal(3, powerAgg.FreeAttackUncommonAttacksDiscounted);
        Assert.Equal(1, powerAgg.FreeAttackRareAttacksDiscounted);
    }

    private static void AssertPounceFreeSkillPowerFixture(PowerAggregate powerAgg)
    {
        Assert.Equal("POWER.FREE_SKILL_POWER", powerAgg.PowerId);
        Assert.Equal("Free Skill", powerAgg.DisplayName);
        Assert.Equal(10, powerAgg.FreeSkillChargesGranted);
        Assert.Equal(8, powerAgg.FreeSkillChargesUsed);
        Assert.Equal(2, powerAgg.FreeSkillZeroEnergySavingsUses);
        Assert.Equal(13m, powerAgg.FreeSkillEnergySaved);
        Assert.Equal(2, powerAgg.FreeSkillBasicSkillsDiscounted);
        Assert.Equal(2, powerAgg.FreeSkillCommonSkillsDiscounted);
        Assert.Equal(3, powerAgg.FreeSkillUncommonSkillsDiscounted);
        Assert.Equal(1, powerAgg.FreeSkillRareSkillsDiscounted);
    }

    private static void AssertSturdyClampFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(17, relicAgg.SturdyClampBlockRetained);
        Assert.Equal(3, relicAgg.SturdyClampExcessBlockOverTen);
        Assert.Equal(3, relicAgg.SturdyClampTurns);
        Assert.Equal(2, relicAgg.SturdyClampCombats);
    }

    private static void AssertSoulPileCardFixture(CardAggregate cardAgg)
    {
        Assert.Equal(4, cardAgg.SoulsAddedToDrawPile);
        Assert.Equal(2, cardAgg.SoulsAddedToHand);
        Assert.Equal(3, cardAgg.SoulsAddedToDiscardPile);
    }

    private static void AssertBeatingRemnantFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(18m, relicAgg.BeatingRemnantHpLossPrevented);
        Assert.Equal(6, relicAgg.BeatingRemnantTurns);
        Assert.Equal(3, relicAgg.BeatingRemnantCombats);
    }

    private static void AssertWhisperingEarringFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(21m, relicAgg.WhisperingEarringFirstRoundHpLost);
        Assert.Equal(3, relicAgg.WhisperingEarringCombats);
    }

    private static void AssertTungstenRodFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(18m, relicAgg.TungstenRodDamagePrevented);
        Assert.Equal(4m, relicAgg.TungstenRodSelfDamagePrevented);
        Assert.Equal(3m, relicAgg.TungstenRodCurseDamagePrevented);
        Assert.Equal(2m, relicAgg.TungstenRodStatusDamagePrevented);
        Assert.Equal(8m, relicAgg.TungstenRodEnemyDamagePrevented);
        Assert.Equal(6, relicAgg.TungstenRodTurns);
        Assert.Equal(3, relicAgg.TungstenRodCombats);
    }

    private static void AssertRuinedHelmetFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(7.5m, relicAgg.StrengthAdded);
        Assert.Equal(3, relicAgg.RuinedHelmetCombats);
    }

    private static void AssertDaughterOfTheWindFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(24, relicAgg.AdditionalBlockGained);
        Assert.Equal(6, relicAgg.DaughterOfTheWindTurns);
        Assert.Equal(3, relicAgg.DaughterOfTheWindCombats);
    }

    private static void AssertArtOfWarFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(4, relicAgg.EnergyGenerated);
        Assert.Equal(8, relicAgg.ArtOfWarTurns);
        Assert.Equal(2, relicAgg.EnergyGeneratedCombats);
    }

    private static void AssertCrackedCoreFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(3, relicAgg.CrackedCoreOrbEvokes);
        Assert.Equal(7, relicAgg.CrackedCoreOrbPassiveTriggers);
        Assert.Equal(1, relicAgg.CrackedCoreOrbFizzles);
        Assert.Equal(15, relicAgg.TotalDamageAttempted);
        Assert.Equal(10, relicAgg.TotalDamageDealt);
        Assert.Equal(2, relicAgg.TotalDamageBlocked);
        Assert.Equal(3, relicAgg.TotalDamageOverkill);
        Assert.Equal(1, relicAgg.Kills);
        Assert.Equal(2, relicAgg.TotalTargets);
    }

    private static void AssertSymbioticVirusFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(3, relicAgg.SymbioticVirusOrbEvokes);
        Assert.Equal(7, relicAgg.SymbioticVirusOrbPassiveTriggers);
        Assert.Equal(1, relicAgg.SymbioticVirusOrbFizzles);
    }

    private static void AssertGoldPlatedCablesFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(7, relicAgg.Activations);
        Assert.Equal(
            4,
            relicAgg.GoldPlatedCablesActivationsByOrbType["ORB.LIGHTNING_ORB"]
                .Activations);
        Assert.Equal(
            "Lightning",
            relicAgg.GoldPlatedCablesActivationsByOrbType["ORB.LIGHTNING_ORB"]
                .DisplayName);
        Assert.Equal(
            2,
            relicAgg.GoldPlatedCablesActivationsByOrbType["ORB.FROST_ORB"]
                .Activations);
        Assert.Equal(
            1,
            relicAgg.GoldPlatedCablesActivationsByOrbType["ORB.PLASMA_ORB"]
                .Activations);
        Assert.Equal(3, relicAgg.GoldPlatedCablesNoOrbTargets);
    }

    private static void AssertStoneHumidifierFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(9m, relicAgg.MaxHpGained);
        Assert.Collection(
            relicAgg.MaxHpActivations,
            activation =>
            {
                Assert.Equal(70m, activation.StartingHp);
                Assert.Equal(75m, activation.ResultingHp);
            },
            activation =>
            {
                Assert.Equal(80m, activation.StartingHp);
                Assert.Equal(84m, activation.ResultingHp);
            });
    }

    private static void AssertMummifiedHandFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(6m, relicAgg.MummifiedHandTriggeringPowerCostTotal);
        Assert.Equal(6m, relicAgg.MummifiedHandDiscountGivenTotal);
        Assert.Equal(1.25m, relicAgg.MummifiedHandEnergySpentToDiscountedCostRatioTotal);
        Assert.Equal(2, relicAgg.MummifiedHandEnergySpentToDiscountedCostRatioCount);
        Assert.Equal(2, relicAgg.MummifiedHandCombats);
        Assert.Equal(5, relicAgg.MummifiedHandTurns);
        Assert.Equal(1, relicAgg.MummifiedHandDiscountedPowers);
        Assert.Equal(1, relicAgg.MummifiedHandDiscountedAttacks);
        Assert.Equal(1, relicAgg.MummifiedHandDiscountedSkills);
        Assert.Equal(1, relicAgg.MummifiedHandDiscountedCommons);
        Assert.Equal(1, relicAgg.MummifiedHandDiscountedUncommons);
        Assert.Equal(1, relicAgg.MummifiedHandDiscountedRares);
    }

    private static void AssertBurningSticksFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(4, relicAgg.BurningSticksCombats);
        Assert.Equal(5, relicAgg.BurningSticksGeneratedCardPlays);
        Assert.Equal(1, relicAgg.BurningSticksCommonCardsDuplicated);
        Assert.Equal(1, relicAgg.BurningSticksUncommonCardsDuplicated);
        Assert.Equal(1, relicAgg.BurningSticksRareCardsDuplicated);
    }

    private static void AssertBingBongFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(8, relicAgg.BingBongExtraCardsAdded);
        Assert.Equal(3, relicAgg.BingBongCommonCardsAdded);
        Assert.Equal(2, relicAgg.BingBongUncommonCardsAdded);
        Assert.Equal(1, relicAgg.BingBongRareCardsAdded);
        Assert.Equal(2, relicAgg.BingBongCurseCardsAdded);
    }

    private static void AssertGnarledHammerFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(
            new[] { "Pommel Strike", "Uppercut+", "Pommel Strike" },
            relicAgg.SharpEnchantedCards);
    }

    private static void AssertSilkenTressFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(
            new[] { "Pommel Strike" },
            relicAgg.SilkenTressGlamCards);
    }

    private static void AssertTriBoomerangFixture(RelicAggregate relicAgg)
    {
        Assert.Collection(
            relicAgg.TriBoomerangInstinctCards,
            card =>
            {
                Assert.Equal("CARD.REAP#1", card.CardInstanceId);
                Assert.Equal("Reap", card.DisplayName);
            },
            card =>
            {
                Assert.Equal("CARD.GRAVE_WARDEN#1", card.CardInstanceId);
                Assert.Equal("Grave Warden", card.DisplayName);
            },
            card =>
            {
                Assert.Equal("CARD.SEVERANCE#2", card.CardInstanceId);
                Assert.Equal("Severance+", card.DisplayName);
            });
        Assert.Equal(7, relicAgg.TriBoomerangInstinctCardPlays);
        Assert.Equal(3, relicAgg.TriBoomerangCombats);
    }

    private static void AssertWarHammerFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(4, relicAgg.CardsUpgraded);
        Assert.Equal(
            new[] { "Grave Warden+", "Reap+", "Defy+", "Bash+" },
            relicAgg.UpgradedCards);
        Assert.Equal(
            new[]
            {
                "CARD.GRAVE_WARDEN#1",
                "CARD.REAP#1",
                "CARD.DEFY#1",
                "CARD.BASH#1",
            },
            relicAgg.WarHammerUpgradedCardInstanceIds);
        Assert.Equal(12, relicAgg.WarHammerUpgradedCardPlays);
        Assert.Equal(3, relicAgg.WarHammerCombats);
        Assert.Equal(6, relicAgg.WarHammerTurns);
    }

    private static void AssertFresnelLensFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(70m, relicAgg.OriginalMaxHp);
        Assert.Equal(57m, relicAgg.NewMaxHp);
        Assert.Equal(9, relicAgg.NimbleCardsTaken);
        Assert.Equal(8, relicAgg.RewardScreensWithNimbleCards);
        Assert.Equal(3, relicAgg.RewardScreensWithTwoNimbleCards);
        Assert.Equal(2, relicAgg.RewardScreensWithThreeOrMoreNimbleCards);
        Assert.Equal(5, relicAgg.RewardScreensWithoutNimbleCards);
        Assert.Equal(4, relicAgg.RewardScreensWithNimbleCardsButNoneTaken);
    }

    private static void AssertWingCharmFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(3, relicAgg.WingCharmSwiftCardsTaken);
        Assert.Equal(4, relicAgg.WingCharmSwiftCardsNotTaken);
        Assert.Equal(4, relicAgg.WingCharmCommonSwiftCardsOffered);
        Assert.Equal(2, relicAgg.WingCharmUncommonSwiftCardsOffered);
        Assert.Equal(1, relicAgg.WingCharmRareSwiftCardsOffered);
    }

    private static void AssertPaelsToothFixture(RelicAggregate relicAgg)
    {
        Assert.Collection(
            relicAgg.CardsReturned,
            card => AssertPaelsToothCard(card, "CARD.STRIKE_KIN", "Strike+", 1, 1),
            card => AssertPaelsToothCard(card, "CARD.POMMEL_STRIKE", "Pommel Strike++", 2, 2),
            card => AssertPaelsToothCard(card, "CARD.STRIKE_KIN", "Strike++", 2, 3));
    }

    private static void AssertPaelsToothCard(
        RelicCardReturnAggregate card,
        string cardId,
        string displayName,
        int upgradeLevel,
        int floorsClimbed)
    {
        Assert.Equal(cardId, card.CardId);
        Assert.Equal(displayName, card.DisplayName);
        Assert.Equal(upgradeLevel, card.UpgradeLevel);
        Assert.Equal(floorsClimbed, card.FloorsClimbed);
    }

    private static void AssertSilverCrucibleFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(3, relicAgg.CardRewardScreens.Count);

        var first = relicAgg.CardRewardScreens[0];
        Assert.Equal(1, first.ScreenNumber);
        Assert.Equal(12, first.Floor);
        Assert.True(first.Resolved);
        Assert.Collection(
            first.Cards,
            card => AssertSilverCrucibleCard(card, "CARD.BASH", "Bash+", 1, taken: true),
            card => AssertSilverCrucibleCard(card, "CARD.SHRUG_IT_OFF", "Shrug It Off+", 1, taken: false),
            card => AssertSilverCrucibleCard(card, "CARD.INFLAME", "Inflame+", 1, taken: false));

        var second = relicAgg.CardRewardScreens[1];
        Assert.Equal(2, second.ScreenNumber);
        Assert.Equal(12, second.Floor);
        Assert.True(second.Resolved);
        Assert.Collection(
            second.Cards,
            card => AssertSilverCrucibleCard(card, "CARD.POMMEL_STRIKE", "Pommel Strike+", 1, taken: false),
            card => AssertSilverCrucibleCard(card, "CARD.TRUE_GRIT", "True Grit+", 1, taken: true),
            card => AssertSilverCrucibleCard(card, "CARD.SPOT_WEAKNESS", "Spot Weakness+", 1, taken: false));

        var third = relicAgg.CardRewardScreens[2];
        Assert.Equal(3, third.ScreenNumber);
        Assert.Equal(14, third.Floor);
        Assert.True(third.Resolved);
        Assert.Equal(
            new[] { "CARD.HEADBUTT", "CARD.IRON_WAVE", "CARD.BATTLE_TRANCE" },
            third.Cards.Select(card => card.CardId));
        Assert.All(third.Cards, card =>
        {
            Assert.Equal(1, card.UpgradeLevel);
            Assert.False(card.Taken);
        });
    }

    private static void AssertStoneCrackerPlayTracking(RelicAggregate relicAgg)
    {
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(6, relicAgg.CardsUpgraded);
        Assert.Equal(3, relicAgg.StoneCrackerUpgradedCommons);
        Assert.Equal(2, relicAgg.StoneCrackerUpgradedUncommons);
        Assert.Equal(1, relicAgg.StoneCrackerUpgradedRares);
        Assert.Equal(9, relicAgg.StoneCrackerUpgradedCardPlays);
        Assert.Equal(3, relicAgg.StoneCrackerCombats);
        Assert.Equal(6, relicAgg.StoneCrackerTurns);
    }

    private static void AssertSilverCrucibleCard(
        RelicCardRewardOptionAggregate card,
        string cardId,
        string displayName,
        int upgradeLevel,
        bool taken)
    {
        Assert.Equal(cardId, card.CardId);
        Assert.Equal(displayName, card.DisplayName);
        Assert.Equal(upgradeLevel, card.UpgradeLevel);
        Assert.Equal(taken, card.Taken);
    }

    private static void AssertOrreryFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, relicAgg.OrreryRewards.Select(reward => reward.RewardNumber));

        var skipped = relicAgg.OrreryRewards[0];
        Assert.Equal(12, skipped.Floor);
        Assert.Equal("skipped", skipped.Outcome);
        Assert.Empty(skipped.CardsObtained);
        Assert.Equal(
            new[] { "CARD.BASH", "CARD.SHRUG_IT_OFF", "CARD.INFLAME" },
            skipped.OfferedCardIds);

        var obtained = relicAgg.OrreryRewards[1];
        Assert.Equal("obtained", obtained.Outcome);
        Assert.Collection(
            obtained.CardsObtained,
            card =>
            {
                Assert.Equal("CARD.POMMEL_STRIKE", card.CardId);
                Assert.Equal("Pommel Strike", card.DisplayName);
                Assert.Equal(0, card.UpgradeLevel);
            });

        var sacrificed = relicAgg.OrreryRewards[2];
        Assert.Equal("alternative", sacrificed.Outcome);
        Assert.Equal("SACRIFICE", sacrificed.AlternativeId);
        Assert.Empty(sacrificed.CardsObtained);

        Assert.Equal("pending", relicAgg.OrreryRewards[3].Outcome);

        var upgraded = relicAgg.OrreryRewards[4];
        Assert.Equal("obtained", upgraded.Outcome);
        Assert.Collection(
            upgraded.CardsObtained,
            card =>
            {
                Assert.Equal("CARD.UPPERCUT", card.CardId);
                Assert.Equal("Uppercut+", card.DisplayName);
                Assert.Equal(1, card.UpgradeLevel);
            });
    }

    private static void AssertBookOfFiveRingsFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(8, relicAgg.CardsAddedToDeck);
        Assert.Equal(1, relicAgg.Activations);
        Assert.Equal(20m, relicAgg.TotalHealingAttempted);
        Assert.Equal(12m, relicAgg.TotalHealingRestored);
        Assert.Equal(8m, relicAgg.TotalHealingLost);
        Assert.Equal(8m, relicAgg.HealingLostReasons["full_hp"].Amount);
        Assert.Equal(8, relicAgg.FloorAcquired);
        Assert.Equal(3, relicAgg.CardRewardsSkipped);
    }

    [Fact]
    public void HistoricalLoad_AcceptsThrowingAxeRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("throwing-axe-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertThrowingAxeFixture(
            loaded.Data.RelicAggregates["RELIC.THROWING_AXE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsThrowingAxeRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("throwing-axe-relic-run.json"));

        Assert.NotNull(resumed);
        AssertThrowingAxeFixture(
            resumed!.RelicAggregates["RELIC.THROWING_AXE"]);
    }

    private static void AssertThrowingAxeFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(3, relicAgg.ThrowingAxeExtraCardsPlayed);
        Assert.Equal(7, relicAgg.ThrowingAxeExtraPlayEnergyCostTotal);
        Assert.Equal(4, relicAgg.ThrowingAxeCombats);
        Assert.Equal(1, relicAgg.ThrowingAxeCommonCardsPlayed);
        Assert.Equal(1, relicAgg.ThrowingAxeUncommonCardsPlayed);
        Assert.Equal(1, relicAgg.ThrowingAxeRareCardsPlayed);
    }

    [Fact]
    public void HistoricalLoad_AcceptsTinyMailboxRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("tiny-mailbox-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertTinyMailboxFixture(
            loaded.Data.RelicAggregates["RELIC.TINY_MAILBOX"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsTinyMailboxRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("tiny-mailbox-relic-run.json"));

        Assert.NotNull(resumed);
        AssertTinyMailboxFixture(
            resumed!.RelicAggregates["RELIC.TINY_MAILBOX"]);
    }

    private static void AssertTinyMailboxFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(4, relicAgg.TinyMailboxPotionsOffered);
        Assert.Equal(3, relicAgg.TinyMailboxPotionsTaken);
        Assert.Equal(1, relicAgg.TinyMailboxCommonPotionsOffered);
        Assert.Equal(1, relicAgg.TinyMailboxUncommonPotionsOffered);
        Assert.Equal(2, relicAgg.TinyMailboxRarePotionsOffered);
        Assert.Equal(1, relicAgg.TinyMailboxFruitJuicesOffered);
        Assert.Equal(2, relicAgg.TinyMailboxCampfiresNotRested);
    }

    [Fact]
    public void HistoricalLoad_AcceptsFeedCardFixture()
    {
        var loaded = RunStorage.LoadHistorical(FixturePath("feed-card-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.Equal(
            7,
            loaded.Data.Aggregates["CARD.FEED#1"].TotalMaxHpGained);
    }

    [Fact]
    public void ResumableLoad_AcceptsFeedCardFixture()
    {
        var resumed = RunStorage.LoadResumable(FixturePath("feed-card-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(7, resumed!.Aggregates["CARD.FEED#1"].TotalMaxHpGained);
    }

    [Fact]
    public void HistoricalLoad_AcceptsArmamentsCardFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("armaments-card-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.Equal(
            9,
            loaded.Data.Aggregates["CARD.ARMAMENTS#1"].ArmamentsCardsUpgraded);
    }

    [Fact]
    public void ResumableLoad_AcceptsArmamentsCardFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("armaments-card-run.json"));

        Assert.NotNull(resumed);
        Assert.Equal(
            9,
            resumed!.Aggregates["CARD.ARMAMENTS#1"].ArmamentsCardsUpgraded);
    }

    [Fact]
    public void HistoricalLoad_AcceptsForgottenSoulRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("forgotten-soul-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertForgottenSoulFixture(
            loaded.Data.RelicAggregates["RELIC.FORGOTTEN_SOUL"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsForgottenSoulRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("forgotten-soul-relic-run.json"));

        Assert.NotNull(resumed);
        AssertForgottenSoulFixture(
            resumed!.RelicAggregates["RELIC.FORGOTTEN_SOUL"]);
    }

    private static void AssertForgottenSoulFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(6, relicAgg.Activations);
        Assert.Equal(24, relicAgg.TotalDamageAttempted);
        Assert.Equal(17, relicAgg.TotalDamageDealt);
        Assert.Equal(4, relicAgg.TotalDamageBlocked);
        Assert.Equal(3, relicAgg.TotalDamageOverkill);
        Assert.Equal(2, relicAgg.Kills);
        Assert.Equal(6, relicAgg.TotalTargets);
        Assert.Equal(8, relicAgg.ForgottenSoulTurns);
        Assert.Equal(3, relicAgg.ForgottenSoulCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsCardOrbsCreatedFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("card-orbs-created-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertCardOrbLifecycleFixture(
            loaded.Data.Aggregates["CARD.GLACIER#1"]);
        Assert.Equal("ORB.FROST_ORB", loaded.Data.Events[0].OrbId);
        Assert.Contains(loaded.Data.Events, entry => entry.Type == "orb_passive");
        Assert.Contains(loaded.Data.Events, entry => entry.Type == "orb_evoked");
        Assert.Contains(loaded.Data.Events, entry => entry.Type == "orb_fizzled");
        Assert.Contains(
            loaded.Data.Events,
            entry => entry.Type == "orb_block_gained" && entry.Blocked == 28);
    }

    [Fact]
    public void ResumableLoad_AcceptsCardOrbsCreatedFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("card-orbs-created-run.json"));

        Assert.NotNull(resumed);
        AssertCardOrbLifecycleFixture(
            resumed!.Aggregates["CARD.GLACIER#1"]);
        Assert.Equal("ORB.FROST_ORB", resumed.Events[0].OrbId);
    }

    private static void AssertCardOrbLifecycleFixture(CardAggregate aggregate)
    {
        Assert.Equal(6, aggregate.TotalOrbsCreated);
        var frost = aggregate.OrbOutcomes["ORB.FROST_ORB"];
        Assert.Equal("ORB.FROST_ORB", frost.OrbId);
        Assert.Equal(6, frost.Created);
        Assert.Equal(9, frost.PassiveActivations);
        Assert.Equal(4, frost.Evokes);
        Assert.Equal(1, frost.Fizzles);
        Assert.Equal(28, frost.BlockGained);
    }

    [Fact]
    public void HistoricalLoad_AcceptsBallLightningOrbDamageFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("ball-lightning-orb-damage-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertBallLightningOrbDamageFixture(
            loaded.Data.Aggregates["CARD.BALL_LIGHTNING#1"]);
        Assert.Equal(4, loaded.Data.Events.Count(entry =>
            entry.Type == "orb_damage"
            && entry.OrbId == "ORB.LIGHTNING_ORB"));
    }

    [Fact]
    public void ResumableLoad_AcceptsBallLightningOrbDamageFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("ball-lightning-orb-damage-run.json"));

        Assert.NotNull(resumed);
        AssertBallLightningOrbDamageFixture(
            resumed!.Aggregates["CARD.BALL_LIGHTNING#1"]);
    }

    private static void AssertBallLightningOrbDamageFixture(
        CardAggregate aggregate)
    {
        Assert.Equal(2, aggregate.TotalOrbsCreated);
        Assert.Equal(0, aggregate.TotalIntended);
        Assert.Equal(0, aggregate.TotalEffective);
        var lightning = aggregate.OrbOutcomes["ORB.LIGHTNING_ORB"];
        Assert.Equal("ORB.LIGHTNING_ORB", lightning.OrbId);
        Assert.Equal(2, lightning.Created);
        Assert.Equal(3, lightning.PassiveActivations);
        Assert.Equal(1, lightning.Evokes);
        Assert.Equal(0, lightning.Fizzles);
        Assert.Equal(18, lightning.DamageAttempted);
        Assert.Equal(12, lightning.DamageDealt);
        Assert.Equal(4, lightning.DamageBlocked);
        Assert.Equal(2, lightning.DamageOverkill);
        Assert.Equal(1, lightning.Kills);
        Assert.Equal(4, lightning.TargetsHit);
    }

    [Fact]
    public void HistoricalLoad_AcceptsDefectOrbFamilyFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("defect-orb-family-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertDefectOrbFamilyFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsDefectOrbFamilyFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("defect-orb-family-run.json"));

        Assert.NotNull(resumed);
        AssertDefectOrbFamilyFixture(resumed!);
    }

    private static void AssertDefectOrbFamilyFixture(RunData run)
    {
        var plasma = run.Aggregates["CARD.FUSION#1"]
            .OrbOutcomes["ORB.PLASMA_ORB"];
        Assert.Equal(5, plasma.EnergyGenerated);

        var storm = run.MetaStats.PowerAggregates["POWER.STORM"];
        Assert.Equal(3, storm.TotalOrbsCreated);
        var lightning = storm.OrbOutcomes["ORB.LIGHTNING_ORB"];
        Assert.Equal(3, lightning.Created);
        Assert.Equal(4, lightning.PassiveActivations);
        Assert.Equal(2, lightning.Evokes);
        Assert.Equal(1, lightning.Fizzles);
        Assert.Equal(24, lightning.DamageAttempted);
        Assert.Equal(19, lightning.DamageDealt);
        Assert.Equal(3, lightning.DamageBlocked);
        Assert.Equal(2, lightning.DamageOverkill);
        Assert.Equal(1, lightning.Kills);
        Assert.Equal(6, lightning.TargetsHit);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPotionRunHistoryFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("potion-run-history.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        Assert.Equal(4, loaded.Data.PotionHistory.Count);

        var notTaken = loaded.Data.PotionHistory[0];
        Assert.Equal("POTION.FIRE_POTION", notTaken.PotionId);
        Assert.Equal("Shop", notTaken.AcquisitionMethod);
        Assert.Equal("Common", notTaken.Rarity);
        Assert.False(notTaken.Acquired);
        Assert.Equal(4, notTaken.SeenFloor);

        var used = loaded.Data.PotionHistory[1];
        Assert.True(used.Acquired);
        Assert.True(used.Used);
        Assert.Equal(6, used.AcquiredFloor);
        Assert.Null(used.AcquiredTurn);
        Assert.Equal(9, used.UsedFloor);
        Assert.Null(used.UsedTurn);
        Assert.Equal("Elite combat", used.UsedLocationKind);

        var held = loaded.Data.PotionHistory[2];
        Assert.True(held.HeldAtRunEnd);
        Assert.Equal(12, held.HeldAtRunEndFloor);

        var rejected = loaded.Data.PotionHistory[3];
        Assert.Equal("Rare", rejected.Rarity);
        Assert.True(rejected.RejectedAtRewardScreen);
        Assert.False(rejected.Acquired);
    }

    [Fact]
    public void ResumableLoad_AcceptsPotionRunHistoryFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("potion-run-history.json"));

        Assert.NotNull(resumed);
        Assert.Equal(4, resumed!.PotionHistory.Count);
        Assert.Equal("Potion reward", resumed.PotionHistory[1].AcquisitionMethod);
        Assert.True(resumed.PotionHistory[3].RejectedAtRewardScreen);
    }

    [Fact]
    public void HistoricalLoad_AcceptsMaxHpRunHistoryFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("max-hp-run-history.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertMaxHpRunHistoryFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsMaxHpRunHistoryFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("max-hp-run-history.json"));

        Assert.NotNull(resumed);
        AssertMaxHpRunHistoryFixture(resumed!);
    }

    [Fact]
    public void HistoricalLoad_AcceptsHpRunStatsFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("hp-run-stats.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertHpRunStatsFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsHpRunStatsFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("hp-run-stats.json"));

        Assert.NotNull(resumed);
        AssertHpRunStatsFixture(resumed!);
    }

    private static void AssertHpRunStatsFixture(RunData run)
    {
        Assert.Equal(31m, run.HealthStats.HpLostInCombats);
        Assert.Equal(9m, run.HealthStats.HpLostInEvents);
        Assert.Equal(5, run.HealthStats.Combats);
        Assert.Equal(2, run.MaxHpHistory.Count);
    }

    [Fact]
    public void HistoricalLoad_AcceptsGoldRunStatsFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("gold-run-stats.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertGoldRunStatsFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsGoldRunStatsFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("gold-run-stats.json"));

        Assert.NotNull(resumed);
        AssertGoldRunStatsFixture(resumed!);
    }

    private static void AssertGoldRunStatsFixture(RunData run)
    {
        Assert.Equal(480, run.GoldStats.GoldAcquired);
        Assert.Equal(300, run.GoldStats.GoldSpent);
        Assert.Equal(220, run.GoldStats.GoldSpentInShops);
        Assert.Equal(50, run.GoldStats.GoldSpentInEvents);
        Assert.Equal(180, run.GoldStats.GoldGainedInCombats);
        Assert.Equal(80, run.GoldStats.GoldGainedInEvents);
        Assert.Equal(4, run.GoldStats.ShopsVisited);
        Assert.Equal(5, run.GoldStats.EventsVisited);
        Assert.Equal(10, run.GoldStats.Combats);
        Assert.Equal(9, run.GoldStats.LastShopFloorCounted);
        Assert.Equal(11, run.GoldStats.LastEventFloorCounted);
    }

    [Fact]
    public void HistoricalLoad_AcceptsMapLegendRunStatsFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("map-legend-run-stats.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertMapLegendRunStatsFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsMapLegendRunStatsFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("map-legend-run-stats.json"));

        Assert.NotNull(resumed);
        AssertMapLegendRunStatsFixture(resumed!);
    }

    private static void AssertMapLegendRunStatsFixture(RunData run)
    {
        Assert.Equal(4, run.MapLegendStats.Unknown.Visits);
        Assert.Equal(2, run.MapLegendStats.Unknown.ResolvedEvents);
        Assert.Equal(1, run.MapLegendStats.Unknown.ResolvedCombats);
        Assert.Equal(2, run.MapLegendStats.Unknown.PotionsOffered);
        Assert.Equal(1, run.MapLegendStats.Unknown.PotionsGained);
        Assert.Equal(233, run.MapLegendStats.Shop.GoldSpent);
        Assert.Equal(2, run.MapLegendStats.Treasure.RelicsGained);
        Assert.Equal(37m, run.MapLegendStats.RestSite.HpHealed);
        Assert.Equal(2, run.MapLegendStats.RestSite.CardsUpgraded);
        Assert.Equal(9, run.MapLegendStats.Monster.CombatsWon);
        Assert.Equal(31, run.MapLegendStats.Monster.CombatTurns);
        Assert.Equal(3, run.MapLegendStats.Elite.RelicsGained);
        Assert.Equal(22, run.MapLegendStats.CurrentPointFloor);
        Assert.Equal("Elite", run.MapLegendStats.CurrentPointType);
    }

    [Fact]
    public void HistoricalLoad_AcceptsRunTimeStatsFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("run-time-stats.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertRunTimeStatsFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsRunTimeStatsFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("run-time-stats.json"));

        Assert.NotNull(resumed);
        AssertRunTimeStatsFixture(resumed!);
    }

    private static void AssertRunTimeStatsFixture(RunData run)
    {
        Assert.Equal(742, run.TimeStats.CombatSeconds);
        Assert.Equal(121, run.TimeStats.RewardScreenSeconds);
        Assert.Equal(318, run.TimeStats.EventSeconds);
        Assert.Equal(205, run.TimeStats.MapSeconds);
        Assert.Equal(12, run.TimeStats.Combats);
        Assert.Equal(47, run.TimeStats.CombatTurns);
        Assert.Equal(1820, run.TimeStats.LastObservedRunTime);
        Assert.Equal("Map", run.TimeStats.ActiveCategory);
    }

    private static void AssertMaxHpRunHistoryFixture(RunData run)
    {
        Assert.Equal(2, run.MaxHpHistory.Count);

        var loss = run.MaxHpHistory[0];
        Assert.Equal(4, loss.Floor);
        Assert.Equal("Drowning Beacon", loss.SourceName);
        Assert.Equal(70, loss.PreviousMaxHp);
        Assert.Equal(63, loss.NewMaxHp);

        var gain = run.MaxHpHistory[1];
        Assert.Equal(9, gain.Floor);
        Assert.Equal("Chosen Cheese", gain.SourceName);
        Assert.Equal(63, gain.PreviousMaxHp);
        Assert.Equal(66, gain.NewMaxHp);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPotionRunHistoryTurnsFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("potion-run-history-turns.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        var potion = Assert.Single(loaded.Data.PotionHistory);
        Assert.Equal(2, potion.SeenTurn);
        Assert.Equal(2, potion.AcquiredTurn);
        Assert.Equal(4, potion.UsedTurn);
    }

    [Fact]
    public void ResumableLoad_AcceptsPotionRunHistoryTurnsFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("potion-run-history-turns.json"));

        Assert.NotNull(resumed);
        var potion = Assert.Single(resumed!.PotionHistory);
        Assert.Equal(2, potion.SeenTurn);
        Assert.Equal(2, potion.AcquiredTurn);
        Assert.Equal(4, potion.UsedTurn);
    }

    [Fact]
    public void HistoricalLoad_AcceptsBloodPotionRunHistoryFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("blood-potion-run-history.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertBloodPotionHistoryFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsBloodPotionRunHistoryFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("blood-potion-run-history.json"));

        Assert.NotNull(resumed);
        AssertBloodPotionHistoryFixture(resumed!);
    }

    private static void AssertBloodPotionHistoryFixture(RunData run)
    {
        var potion = Assert.Single(run.PotionHistory);
        Assert.Equal("POTION.BLOOD_POTION", potion.PotionId);
        Assert.True(potion.Used);
        Assert.Equal(2, potion.UsedTurn);
        Assert.Equal(12, potion.HpGained);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSwiftPotionRunHistoryFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("swift-potion-run-history.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertSwiftPotionHistoryFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsSwiftPotionRunHistoryFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("swift-potion-run-history.json"));

        Assert.NotNull(resumed);
        AssertSwiftPotionHistoryFixture(resumed!);
    }

    private static void AssertSwiftPotionHistoryFixture(RunData run)
    {
        var potion = Assert.Single(run.PotionHistory);
        Assert.Equal("POTION.SWIFT_POTION", potion.PotionId);
        Assert.True(potion.Used);
        Assert.Equal(2, potion.UsedTurn);
        Assert.Equal(2, potion.CardsDrawn);
        Assert.Equal(1, potion.CardDrawsBlocked);
    }

    [Fact]
    public void HistoricalLoad_AcceptsFortifierPotionRunHistoryFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("fortifier-potion-run-history.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertFortifierPotionHistoryFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsFortifierPotionRunHistoryFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("fortifier-potion-run-history.json"));

        Assert.NotNull(resumed);
        AssertFortifierPotionHistoryFixture(resumed!);
    }

    private static void AssertFortifierPotionHistoryFixture(RunData run)
    {
        var potion = Assert.Single(run.PotionHistory);
        Assert.Equal("POTION.FORTIFIER", potion.PotionId);
        Assert.True(potion.Used);
        Assert.Equal(2, potion.UsedTurn);
        Assert.Equal(12, potion.BlockGained);
        Assert.Equal(8, potion.BlockEffective);
        Assert.Equal(4, potion.BlockWasted);
    }

    [Fact]
    public void HistoricalLoad_AcceptsExplosiveAmpouleRunHistoryFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("explosive-ampoule-run-history.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertExplosiveAmpouleHistoryFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsExplosiveAmpouleRunHistoryFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("explosive-ampoule-run-history.json"));

        Assert.NotNull(resumed);
        AssertExplosiveAmpouleHistoryFixture(resumed!);
    }

    private static void AssertExplosiveAmpouleHistoryFixture(RunData run)
    {
        var potion = Assert.Single(run.PotionHistory);
        Assert.Equal("POTION.EXPLOSIVE_AMPOULE", potion.PotionId);
        Assert.True(potion.Used);
        Assert.Equal(2, potion.UsedTurn);
        Assert.Equal(20, potion.DamageAttempted);
        Assert.Equal(9, potion.DamageDealt);
        Assert.Equal(4, potion.DamageBlocked);
        Assert.Equal(7, potion.DamageOverkill);
        Assert.Equal(1, potion.Kills);
        Assert.Equal(2, potion.TargetsHit);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPotionSlotRelicCombatStartFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("potion-slot-relic-combat-start-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertPotionSlotRelicCombatStartFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsPotionSlotRelicCombatStartFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("potion-slot-relic-combat-start-run.json"));

        Assert.NotNull(resumed);
        AssertPotionSlotRelicCombatStartFixture(resumed!);
    }

    private static void AssertPotionSlotRelicCombatStartFixture(RunData run)
    {
        var belt = run.RelicAggregates["RELIC.POTION_BELT"];
        Assert.Equal(7, belt.CombatStartPotionCountTotal);
        Assert.Equal(4, belt.CombatStartPotionCountSamples);

        var coffer = run.RelicAggregates["RELIC.ALCHEMICAL_COFFER"];
        Assert.Equal(4, coffer.CombatStartPotionCountTotal);
        Assert.Equal(3, coffer.CombatStartPotionCountSamples);

        var holster = run.RelicAggregates["RELIC.PHIAL_HOLSTER"];
        Assert.Equal(9, holster.CombatStartPotionCountTotal);
        Assert.Equal(5, holster.CombatStartPotionCountSamples);
    }

    [Fact]
    public void HistoricalLoad_AcceptsScreamingFlagonHandSizeFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("screaming-flagon-hand-size-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertScreamingFlagonHandSizeFixture(
            loaded.Data.RelicAggregates["RELIC.SCREAMING_FLAGON"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsScreamingFlagonHandSizeFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("screaming-flagon-hand-size-run.json"));

        Assert.NotNull(resumed);
        AssertScreamingFlagonHandSizeFixture(
            resumed!.RelicAggregates["RELIC.SCREAMING_FLAGON"]);
    }

    private static void AssertScreamingFlagonHandSizeFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(13, relicAgg.ScreamingFlagonTurnEndHandSizeTotal);
        Assert.Equal(5, relicAgg.ScreamingFlagonTurns);
        Assert.Equal(2, relicAgg.ScreamingFlagonCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsMercuryHourglassRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("mercury-hourglass-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertMercuryHourglassFixture(
            loaded.Data.RelicAggregates["RELIC.MERCURY_HOURGLASS"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsMercuryHourglassRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("mercury-hourglass-relic-run.json"));

        Assert.NotNull(resumed);
        AssertMercuryHourglassFixture(
            resumed!.RelicAggregates["RELIC.MERCURY_HOURGLASS"]);
    }

    private static void AssertMercuryHourglassFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(9, relicAgg.Activations);
        Assert.Equal(54, relicAgg.TotalDamageAttempted);
        Assert.Equal(44, relicAgg.TotalDamageDealt);
        Assert.Equal(6, relicAgg.TotalDamageBlocked);
        Assert.Equal(4, relicAgg.TotalDamageOverkill);
        Assert.Equal(2, relicAgg.Kills);
        Assert.Equal(18, relicAgg.TotalTargets);
        Assert.Equal(3, relicAgg.MercuryHourglassCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPetrifiedToadRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("petrified-toad-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertPetrifiedToadFixture(
            loaded.Data.RelicAggregates["RELIC.PETRIFIED_TOAD"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsPetrifiedToadRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("petrified-toad-relic-run.json"));

        Assert.NotNull(resumed);
        AssertPetrifiedToadFixture(
            resumed!.RelicAggregates["RELIC.PETRIFIED_TOAD"]);
    }

    private static void AssertPetrifiedToadFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(4, relicAgg.PetrifiedToadPotionsGiven);
        Assert.Equal(3, relicAgg.PetrifiedToadPotionsBlockedByFullBelt);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPumpkinCandleRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("pumpkin-candle-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertPumpkinCandleFixture(
            loaded.Data.RelicAggregates["RELIC.PUMPKIN_CANDLE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsPumpkinCandleRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("pumpkin-candle-relic-run.json"));

        Assert.NotNull(resumed);
        AssertPumpkinCandleFixture(
            resumed!.RelicAggregates["RELIC.PUMPKIN_CANDLE"]);
    }

    private static void AssertPumpkinCandleFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(14, relicAgg.EnergyGenerated);
        Assert.Equal(12, relicAgg.PumpkinCandleCombatStartChargeTotal);
        Assert.Equal(4, relicAgg.PumpkinCandleCombatStartChargeSamples);
        Assert.Equal(2, relicAgg.PumpkinCandleRekindles);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSpikedGauntletsRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("spiked-gauntlets-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertSpikedGauntletsFixture(
            loaded.Data.RelicAggregates["RELIC.SPIKED_GAUNTLETS"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsSpikedGauntletsRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("spiked-gauntlets-relic-run.json"));

        Assert.NotNull(resumed);
        AssertSpikedGauntletsFixture(
            resumed!.RelicAggregates["RELIC.SPIKED_GAUNTLETS"]);
    }

    private static void AssertSpikedGauntletsFixture(
        RelicAggregate relicAgg)
    {
        Assert.Equal(7, relicAgg.EnergyGenerated);
        Assert.Equal(2, relicAgg.EnergyGeneratedCombats);
        Assert.Equal(3, relicAgg.SpikedGauntletsTaxedPowersPlayed);
        Assert.Equal(8m, relicAgg.SpikedGauntletsPowerCostTotal);
        Assert.Equal(6, relicAgg.SpikedGauntletsPowerEnergySpent);
        Assert.Equal(4, relicAgg.SpikedGauntletsTurns);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSmallCapsuleRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("small-capsule-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertSmallCapsuleFixture(
            loaded.Data.RelicAggregates["RELIC.SMALL_CAPSULE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsSmallCapsuleRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("small-capsule-relic-run.json"));

        Assert.NotNull(resumed);
        AssertSmallCapsuleFixture(
            resumed!.RelicAggregates["RELIC.SMALL_CAPSULE"]);
    }

    private static void AssertSmallCapsuleFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(2, relicAgg.RelicRewardChoices.Count);
        Assert.Equal(1, relicAgg.RelicRewardChoices[0].ChoiceNumber);
        Assert.Equal("RELIC.DATA_DISK", relicAgg.RelicRewardChoices[0].RelicId);
        Assert.Equal("Data Disk", relicAgg.RelicRewardChoices[0].DisplayName);
        Assert.Equal("taken", relicAgg.RelicRewardChoices[0].Outcome);
        Assert.Equal(2, relicAgg.RelicRewardChoices[1].ChoiceNumber);
        Assert.Equal(
            "RELIC.BAG_OF_PREPARATION",
            relicAgg.RelicRewardChoices[1].RelicId);
        Assert.Equal("skipped", relicAgg.RelicRewardChoices[1].Outcome);
    }

    [Fact]
    public void HistoricalLoad_AcceptsToyBoxWaxRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("toy-box-wax-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertToyBoxWaxRelicFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsToyBoxWaxRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("toy-box-wax-relic-run.json"));

        Assert.NotNull(resumed);
        AssertToyBoxWaxRelicFixture(resumed!);
    }

    private static void AssertToyBoxWaxRelicFixture(RunData run)
    {
        var toyBox = run.RelicAggregates["RELIC.TOY_BOX"];
        Assert.Equal(2, toyBox.ToyBoxWaxRelics.Count);
        Assert.Equal("RELIC.ANCHOR", toyBox.ToyBoxWaxRelics[0].RelicId);
        Assert.Equal(5, toyBox.ToyBoxWaxRelics[0].FloorBestowed);
        Assert.Equal(9, toyBox.ToyBoxWaxRelics[0].FloorMelted);
        Assert.Equal(
            "RELIC.BAG_OF_PREPARATION",
            toyBox.ToyBoxWaxRelics[1].RelicId);
        Assert.Null(toyBox.ToyBoxWaxRelics[1].FloorMelted);

        var anchor = run.RelicAggregates["RELIC.ANCHOR"];
        Assert.Equal(2, anchor.Activations);
        Assert.Equal(20, anchor.AdditionalBlockGained);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSwordInTheStoneRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("sword-in-the-stone-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertSwordInTheStoneFixture(
            loaded.Data.RelicAggregates["RELIC.SWORD_OF_STONE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsSwordInTheStoneRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("sword-in-the-stone-relic-run.json"));

        Assert.NotNull(resumed);
        AssertSwordInTheStoneFixture(
            resumed!.RelicAggregates["RELIC.SWORD_OF_STONE"]);
    }

    private static void AssertSwordInTheStoneFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(8, relicAgg.FloorAcquired);
        Assert.Equal(3, relicAgg.SwordInTheStoneElitesSlain.Count);
        Assert.Equal(12, relicAgg.SwordInTheStoneElitesSlain[0].Floor);
        Assert.Equal("Gremlin Nob", relicAgg.SwordInTheStoneElitesSlain[0].DisplayName);
        Assert.Equal(23, relicAgg.SwordInTheStoneElitesSlain[2].Floor);
        Assert.Equal("ENCOUNTER.GREMLIN_LEADER", relicAgg.SwordInTheStoneElitesSlain[2].EncounterId);
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(6, relicAgg.StrengthAdded);
        Assert.Equal(0m, relicAgg.SwordInTheStoneStrengthRateAdded);
        Assert.Equal(0, relicAgg.SwordInTheStoneStrengthCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsSwordInTheStoneStrengthRatesFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("sword-in-the-stone-strength-rates-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertSwordInTheStoneStrengthRatesFixture(
            loaded.Data.RelicAggregates["RELIC.SWORD_OF_STONE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsSwordInTheStoneStrengthRatesFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("sword-in-the-stone-strength-rates-run.json"));

        Assert.NotNull(resumed);
        AssertSwordInTheStoneStrengthRatesFixture(
            resumed!.RelicAggregates["RELIC.SWORD_OF_STONE"]);
    }

    private static void AssertSwordInTheStoneStrengthRatesFixture(
        RelicAggregate relicAgg)
    {
        Assert.Equal(8, relicAgg.FloorAcquired);
        Assert.Equal(3, relicAgg.SwordInTheStoneElitesSlain.Count);
        Assert.Equal(4, relicAgg.Activations);
        Assert.Equal(12m, relicAgg.StrengthAdded);
        Assert.Equal(9m, relicAgg.SwordInTheStoneStrengthRateAdded);
        Assert.Equal(4, relicAgg.SwordInTheStoneStrengthCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsMusicBoxRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("music-box-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertMusicBoxFixture(loaded.Data.RelicAggregates["RELIC.MUSIC_BOX"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsMusicBoxRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("music-box-relic-run.json"));

        Assert.NotNull(resumed);
        AssertMusicBoxFixture(resumed!.RelicAggregates["RELIC.MUSIC_BOX"]);
    }

    private static void AssertMusicBoxFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(7, relicAgg.MusicBoxAttacksCreated);
        Assert.Equal(2, relicAgg.MusicBoxCommonAttacksCreated);
        Assert.Equal(2, relicAgg.MusicBoxUncommonAttacksCreated);
        Assert.Equal(1, relicAgg.MusicBoxRareAttacksCreated);
        Assert.Equal(3, relicAgg.MusicBoxAttacksExhaustedByEthereal);
        Assert.Equal(5, relicAgg.MusicBoxTurns);
        Assert.Equal(2, relicAgg.MusicBoxCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsMeatOnTheBonePreTriggerHpFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("meat-on-the-bone-pre-trigger-hp-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertMeatOnTheBonePreTriggerHpFixture(
            loaded.Data.RelicAggregates["RELIC.MEAT_ON_THE_BONE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsMeatOnTheBonePreTriggerHpFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("meat-on-the-bone-pre-trigger-hp-run.json"));

        Assert.NotNull(resumed);
        AssertMeatOnTheBonePreTriggerHpFixture(
            resumed!.RelicAggregates["RELIC.MEAT_ON_THE_BONE"]);
    }

    private static void AssertMeatOnTheBonePreTriggerHpFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(2, relicAgg.Activations);
        Assert.Equal(-20m, relicAgg.MeatOnTheBonePreTriggerHpRelativeToHalfTotal);
        Assert.Equal(2, relicAgg.MeatOnTheBonePreTriggerHpRelativeToHalfSamples);
        Assert.Equal(77.5m, relicAgg.MeatOnTheBonePreTriggerHpPercentTotal);
        Assert.Equal(2, relicAgg.MeatOnTheBonePreTriggerHpSamples);
    }

    [Fact]
    public void HistoricalLoad_AcceptsCrossbowRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("crossbow-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertCrossbowFixture(loaded.Data.RelicAggregates["RELIC.CROSSBOW"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsCrossbowRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("crossbow-relic-run.json"));

        Assert.NotNull(resumed);
        AssertCrossbowFixture(resumed!.RelicAggregates["RELIC.CROSSBOW"]);
    }

    private static void AssertCrossbowFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(6, relicAgg.CrossbowAttacksGained);
        Assert.Equal(2, relicAgg.CrossbowCommonAttacksGained);
        Assert.Equal(2, relicAgg.CrossbowUncommonAttacksGained);
        Assert.Equal(1, relicAgg.CrossbowRareAttacksGained);
        Assert.Equal(9m, relicAgg.CrossbowDiscountGivenTotal);
        Assert.Equal(4, relicAgg.CrossbowTurns);
        Assert.Equal(2, relicAgg.CrossbowCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsCardRemovalSourceFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("card-removal-source-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertCardRemovalSourceFixture(
            loaded.Data.Aggregates["CARD.STRIKE_IRONCLAD#2"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsCardRemovalSourceFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("card-removal-source-run.json"));

        Assert.NotNull(resumed);
        AssertCardRemovalSourceFixture(
            resumed!.Aggregates["CARD.STRIKE_IRONCLAD#2"]);
    }

    private static void AssertCardRemovalSourceFixture(CardAggregate cardAgg)
    {
        Assert.True(cardAgg.Removed);
        Assert.Equal(12, cardAgg.RemovedAtFloor);
        Assert.Equal("Shopkeeper", cardAgg.RemovalSource);
        Assert.Equal(100, cardAgg.RemovalGoldCost);
    }

    [Fact]
    public void HistoricalLoad_AcceptsEternalFeatherCampfireHealingFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("eternal-feather-campfire-healing-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertEternalFeatherCampfireHealingFixture(
            loaded.Data.RelicAggregates["RELIC.ETERNAL_FEATHER"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsEternalFeatherCampfireHealingFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("eternal-feather-campfire-healing-run.json"));

        Assert.NotNull(resumed);
        AssertEternalFeatherCampfireHealingFixture(
            resumed!.RelicAggregates["RELIC.ETERNAL_FEATHER"]);
    }

    private static void AssertEternalFeatherCampfireHealingFixture(
        RelicAggregate relicAgg)
    {
        Assert.Collection(
            relicAgg.EternalFeatherHealingActivations,
            activation =>
            {
                Assert.Equal(7, activation.Floor);
                Assert.Equal(9m, activation.HpRestored);
            },
            activation =>
            {
                Assert.Equal(14, activation.Floor);
                Assert.Equal(0m, activation.HpRestored);
            });
    }

    [Fact]
    public void HistoricalLoad_AcceptsEternalFeatherDeckSizeFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("eternal-feather-deck-size-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertEternalFeatherDeckSizeFixture(
            loaded.Data.RelicAggregates["RELIC.ETERNAL_FEATHER"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsEternalFeatherDeckSizeFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("eternal-feather-deck-size-run.json"));

        Assert.NotNull(resumed);
        AssertEternalFeatherDeckSizeFixture(
            resumed!.RelicAggregates["RELIC.ETERNAL_FEATHER"]);
    }

    private static void AssertEternalFeatherDeckSizeFixture(
        RelicAggregate relicAgg)
    {
        Assert.Equal(3, relicAgg.Activations);
        Assert.Equal(15m, relicAgg.TotalHealingRestored);
        Assert.Equal(62, relicAgg.EternalFeatherDeckCardsTotal);
        Assert.Equal(3, relicAgg.EternalFeatherDeckCardsSamples);
        Assert.Equal(3, relicAgg.EternalFeatherHealingActivations.Count);
    }

    [Fact]
    public void HistoricalLoad_AcceptsGamePieceRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("game-piece-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertGamePieceFixture(
            loaded.Data.RelicAggregates["RELIC.GAME_PIECE"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsGamePieceRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("game-piece-relic-run.json"));

        Assert.NotNull(resumed);
        AssertGamePieceFixture(
            resumed!.RelicAggregates["RELIC.GAME_PIECE"]);
    }

    private static void AssertGamePieceFixture(RelicAggregate relicAgg)
    {
        Assert.Equal(5, relicAgg.Activations);
        Assert.Equal(4, relicAgg.AdditionalCardsDrawn);
        Assert.Equal(1, relicAgg.AdditionalCardDrawsBlocked);
        Assert.Equal(8, relicAgg.GamePieceTurns);
        Assert.Equal(2, relicAgg.GamePieceCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsRandomCardGenerationFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("random-card-generation-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertRandomCardGenerationFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsRandomCardGenerationFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("random-card-generation-run.json"));

        Assert.NotNull(resumed);
        AssertRandomCardGenerationFixture(resumed!);
    }

    private static void AssertRandomCardGenerationFixture(RunData run)
    {
        var cardGeneration = run.Aggregates["CARD.METAMORPHOSIS#1"]
            .RandomCardGeneration!;
        Assert.Equal(6, cardGeneration.CardsGenerated);
        Assert.Equal(4, cardGeneration.GeneratedCardsPlayed);
        Assert.Equal(5, cardGeneration.GeneratedCardPlays);
        Assert.Equal(2, cardGeneration.CommonCardsGenerated);
        Assert.Equal(3, cardGeneration.UncommonCardsGenerated);
        Assert.Equal(1, cardGeneration.RareCardsGenerated);
        Assert.Equal(6, cardGeneration.AttacksGenerated);
        Assert.Equal(3, cardGeneration.UpgradedCardsGenerated);
        Assert.Equal(11, cardGeneration.EnergyCostBeforeDiscountTotal);
        Assert.Equal(11, cardGeneration.EnergyDiscountGrantedTotal);
        Assert.Equal(6, cardGeneration.CardsAddedToDrawPile);
        Assert.Equal(
            3,
            cardGeneration.CardsById["CARD.STRIKE_IRONCLAD"].Plays);

        var power = run.MetaStats.PowerAggregates["POWER.CREATIVE_AI"];
        Assert.Equal(10, power.MetaDeckTurns);
        Assert.Equal(7, power.MetaActiveTurns);
        Assert.Equal(11, power.MetaActiveApplicationTurns);
        var powerGeneration = power.RandomCardGeneration!;
        Assert.Equal(7, powerGeneration.CardsGenerated);
        Assert.Equal(3, powerGeneration.GeneratedCardsPlayed);
        Assert.Equal(4, powerGeneration.GeneratedCardPlays);
        Assert.Equal(7, powerGeneration.PowersGenerated);
        Assert.Equal(17, powerGeneration.EnergyCostBeforeDiscountTotal);
        Assert.Equal(7, powerGeneration.CardsAddedToHand);
        Assert.Equal(
            2,
            powerGeneration.CardsById["CARD.ECHO_FORM"].Plays);
    }

    [Fact]
    public void HistoricalLoad_AcceptsMembershipCardRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("membership-card-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertMembershipCardFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsMembershipCardRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("membership-card-relic-run.json"));

        Assert.NotNull(resumed);
        AssertMembershipCardFixture(resumed!);
    }

    private static void AssertMembershipCardFixture(RunData run)
    {
        var relic = run.RelicAggregates["RELIC.MEMBERSHIP_CARD"];
        Assert.Equal(4, relic.Activations);
        Assert.Equal(163, relic.MembershipCardGoldSaved);
        Assert.Equal(42, relic.MembershipCardGoldHeldAfterPurchase);
        Assert.Equal(287, relic.MembershipCardGoldEarnedAfterPurchase);
    }

    [Fact]
    public void HistoricalLoad_AcceptsGoldenPearlRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("golden-pearl-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertGoldenPearlFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsGoldenPearlRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("golden-pearl-relic-run.json"));

        Assert.NotNull(resumed);
        AssertGoldenPearlFixture(resumed!);
    }

    private static void AssertGoldenPearlFixture(RunData run)
    {
        Assert.Equal(
            5,
            run.RelicAggregates["RELIC.GOLDEN_PEARL"]
                .FloorsBeforeFirstGoldExpense);
    }

    [Fact]
    public void HistoricalLoad_AcceptsGiryaRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("girya-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertGiryaFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsGiryaRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("girya-relic-run.json"));

        Assert.NotNull(resumed);
        AssertGiryaFixture(resumed!);
    }

    private static void AssertGiryaFixture(RunData run)
    {
        var relic = run.RelicAggregates["RELIC.GIRYA"];
        Assert.Equal(5, relic.FloorAcquired);
        Assert.Equal(3, relic.Activations);
        Assert.Equal(6m, relic.StrengthAdded);
        Assert.Equal(6m, relic.GiryaStrengthRateAdded);
        Assert.Equal(4, relic.GiryaStrengthCombats);
        Assert.Equal(7, relic.GiryaAttacksPlayed);
        Assert.Equal(12, relic.GiryaAttackHits);
        Assert.Equal(11, relic.GiryaCountFloorTotal);
        Assert.Equal(10, relic.GiryaFloorSamples);
        Assert.Equal(14, relic.GiryaLastFloorSampled);
        Assert.Equal(9, relic.GiryaUseFloorDistanceTotal);
        Assert.Equal(3, relic.GiryaUseFloorDistanceSamples);
        Assert.Equal(14, relic.GiryaLastUseFloor);
        Assert.Equal(3, relic.GiryaLastObservedLiftCount);
    }

    [Fact]
    public void HistoricalLoad_AcceptsLastingCandyRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("lasting-candy-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertLastingCandyFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsLastingCandyRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("lasting-candy-relic-run.json"));

        Assert.NotNull(resumed);
        AssertLastingCandyFixture(resumed!);
    }

    private static void AssertLastingCandyFixture(RunData run)
    {
        var relic = run.RelicAggregates["RELIC.LASTING_CANDY"];
        Assert.Equal(5, relic.LastingCandyPowersOffered);
        Assert.Equal(2, relic.LastingCandyPowersTaken);
        Assert.Equal(3, relic.LastingCandyPowersRejected);
        Assert.Equal(3, relic.LastingCandyUncommonPowersOffered);
        Assert.Equal(1, relic.LastingCandyUncommonPowersTaken);
        Assert.Equal(2, relic.LastingCandyUncommonPowersRejected);
        Assert.Equal(2, relic.LastingCandyRarePowersOffered);
        Assert.Equal(1, relic.LastingCandyRarePowersTaken);
        Assert.Equal(1, relic.LastingCandyRarePowersRejected);
        Assert.Equal(2, relic.LastingCandyEliteActivations);
        Assert.Equal(1, relic.LastingCandyBossActivations);
    }

    [Fact]
    public void HistoricalLoad_AcceptsGremlinHornRelicFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("gremlin-horn-relic-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertGremlinHornFixture(loaded.Data);
    }

    [Fact]
    public void ResumableLoad_AcceptsGremlinHornRelicFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("gremlin-horn-relic-run.json"));

        Assert.NotNull(resumed);
        AssertGremlinHornFixture(resumed!);
    }

    private static void AssertGremlinHornFixture(RunData run)
    {
        var relic = run.RelicAggregates["RELIC.GREMLIN_HORN"];
        Assert.Equal(6, relic.Activations);
        Assert.Equal(5, relic.EnergyGenerated);
        Assert.Equal(4, relic.AdditionalCardsDrawn);
        Assert.Equal(6, relic.GremlinHornRateActivations);
        Assert.Equal(8, relic.GremlinHornTurns);
        Assert.Equal(3, relic.GremlinHornCombats);
    }

    [Fact]
    public void HistoricalLoad_AcceptsExhaustedOtherCardTypesFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("exhausted-other-card-types-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertExhaustedOtherCardTypesFixture(
            loaded.Data.Aggregates["CARD.FIEND_FIRE#1"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsExhaustedOtherCardTypesFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("exhausted-other-card-types-run.json"));

        Assert.NotNull(resumed);
        AssertExhaustedOtherCardTypesFixture(
            resumed!.Aggregates["CARD.FIEND_FIRE#1"]);
    }

    [Fact]
    public void HistoricalLoad_DefaultsExhaustedOtherCardTypesForOlderFixtures()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("v4-per-instance-effects-exhaust-run.json"));

        Assert.NotNull(loaded);
        var agg = loaded!.Data.Aggregates["CARD.NECROBINDER_POWER#1"];
        Assert.Equal(0, agg.TimesExhaustedOtherCurses);
        Assert.Equal(0, agg.TimesExhaustedOtherStatusCards);
    }

    [Fact]
    public void HistoricalLoad_AcceptsEnergyWastedLedgerFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("energy-wasted-ledger-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertEnergyWastedLedgerFixture(
            loaded.Data.Aggregates["CARD.ADRENALINE#1"],
            loaded.Data.RelicAggregates["RELIC.ART_OF_WAR"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsEnergyWastedLedgerFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("energy-wasted-ledger-run.json"));

        Assert.NotNull(resumed);
        AssertEnergyWastedLedgerFixture(
            resumed!.Aggregates["CARD.ADRENALINE#1"],
            resumed.RelicAggregates["RELIC.ART_OF_WAR"]);
    }

    [Fact]
    public void HistoricalLoad_DefaultsEnergyWastedForOlderFixtures()
    {
        // A run saved before the ledger existed has generated totals but no
        // wasted counterpart, and must read as zero wasted rather than
        // failing to load.
        var loaded = RunStorage.LoadHistorical(
            FixturePath("art-of-war-relic-run.json"));

        Assert.NotNull(loaded);
        var relicAgg = loaded!.Data.RelicAggregates["RELIC.ART_OF_WAR"];
        Assert.Equal(4, relicAgg.EnergyGenerated);
        Assert.Equal(0, relicAgg.EnergyWasted);
    }

    [Fact]
    public void HistoricalLoad_AcceptsPanachePowerDamageFixture()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("panache-power-damage-run.json"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.SupportsResume);
        AssertPanachePowerDamageFixture(
            loaded.Data.MetaStats.PowerAggregates["POWER.PANACHE_POWER"]);
    }

    [Fact]
    public void ResumableLoad_AcceptsPanachePowerDamageFixture()
    {
        var resumed = RunStorage.LoadResumable(
            FixturePath("panache-power-damage-run.json"));

        Assert.NotNull(resumed);
        AssertPanachePowerDamageFixture(
            resumed!.MetaStats.PowerAggregates["POWER.PANACHE_POWER"]);
    }

    [Fact]
    public void HistoricalLoad_DefaultsPowerDamageForOlderFixtures()
    {
        var loaded = RunStorage.LoadHistorical(
            FixturePath("aggression-power-run.json"));

        Assert.NotNull(loaded);
        var agg = loaded!.Data.MetaStats.PowerAggregates["POWER.AGGRESSION"];
        Assert.Equal(0, agg.TotalIntended);
        Assert.Equal(0, agg.TotalEffective);
        Assert.Equal(0, agg.Kills);
    }

    private static void AssertEnergyWastedLedgerFixture(
        CardAggregate cardAgg,
        RelicAggregate relicAgg)
    {
        Assert.Equal(8, cardAgg.TotalEnergyGenerated);
        Assert.Equal(3, cardAgg.TotalEnergyWasted);
        Assert.Equal(3, relicAgg.EnergyGenerated);
        Assert.Equal(2, relicAgg.EnergyWasted);
    }

    private static void AssertPanachePowerDamageFixture(PowerAggregate powerAgg)
    {
        Assert.Equal(42, powerAgg.TotalIntended);
        Assert.Equal(6, powerAgg.TotalBlocked);
        Assert.Equal(4, powerAgg.TotalOverkill);
        Assert.Equal(32, powerAgg.TotalEffective);
        Assert.Equal(2, powerAgg.Kills);
    }

    private static void AssertExhaustedOtherCardTypesFixture(CardAggregate cardAgg)
    {
        Assert.Equal(7, cardAgg.TimesExhaustedOtherCards);
        Assert.Equal(2, cardAgg.TimesExhaustedOtherCurses);
        Assert.Equal(3, cardAgg.TimesExhaustedOtherStatusCards);
    }

    private static void AssertSplashFixture(CardAggregate cardAgg)
    {
        Assert.Equal(6, cardAgg.SplashAttacksTaken);
        Assert.Equal(2, cardAgg.SplashCommonAttacksTaken);
        Assert.Equal(2, cardAgg.SplashUncommonAttacksTaken);
        Assert.Equal(2, cardAgg.SplashRareAttacksTaken);
        Assert.Equal(7, cardAgg.SplashEnergyDiscountTotal);
    }
}
