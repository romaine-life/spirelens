using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Models.Relics;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class CompendiumRelicStatsContextTests
{
    [Fact]
    public void ShouldShowStatsForVisibility_OnlyShowsVisibleRelics()
    {
        Assert.True(CompendiumRelicStatsContext.ShouldShowStatsForVisibility(ModelVisibility.Visible));
        Assert.False(CompendiumRelicStatsContext.ShouldShowStatsForVisibility(ModelVisibility.None));
        Assert.False(CompendiumRelicStatsContext.ShouldShowStatsForVisibility(ModelVisibility.NotSeen));
        Assert.False(CompendiumRelicStatsContext.ShouldShowStatsForVisibility(ModelVisibility.Locked));
    }

    [Theory]
    [InlineData(false, ModelVisibility.Visible, false)]
    [InlineData(true, ModelVisibility.Visible, true)]
    [InlineData(true, ModelVisibility.None, false)]
    [InlineData(true, ModelVisibility.NotSeen, false)]
    [InlineData(true, ModelVisibility.Locked, false)]
    public void ShouldShowStats_RequiresGlobalVisibilityAndVisibleRelic(
        bool statsVisibilityEnabled,
        ModelVisibility visibility,
        bool expected)
    {
        Assert.Equal(
            expected,
            CompendiumRelicStatsContext.ShouldShowStats(statsVisibilityEnabled, visibility));
    }

    [Fact]
    public void TryBuildRelicTooltipForRun_UsesSavedRunAggregate()
    {
        var run = new RunData { FloorReached = 5 };
        run.RelicAggregates["RELIC.ANCHOR"] = new RelicAggregate
        {
            Activations = 1,
            AdditionalBlockGained = 10,
        };

        var ok = CompendiumRelicStatsContext.TryBuildRelicTooltipForRun(
            Uninitialized<FakeAnchor>(),
            run,
            out var title,
            out var body);

        Assert.True(ok);
        Assert.Equal("Anchor???", title);
        Assert.Contains("Activations", body);
        Assert.Contains("[b]1[/b]", body);
        Assert.Contains("block gained", body);
        Assert.Contains("[b]10[/b]", body);
    }

    [Fact]
    public void TryBuildEmptyRelicTooltip_UsesZeroAggregateForSupportedRelic()
    {
        var ok = CompendiumRelicStatsContext.TryBuildEmptyRelicTooltip(
            Uninitialized<FakeAnchor>(),
            out var title,
            out var body);

        Assert.True(ok);
        Assert.Equal("Anchor???", title);
        Assert.Contains("Activations", body);
        Assert.Contains("block gained", body);
        Assert.Equal(2, body.Split("[b]0[/b]").Length - 1);
    }

    [Fact]
    public void TryBuildEmptyRelicTooltip_CursedPearlIncludesZeroGreedStats()
    {
        var ok = CompendiumRelicStatsContext.TryBuildEmptyRelicTooltip(
            Uninitialized<CursedPearl>(),
            out var title,
            out var body);

        Assert.True(ok);
        Assert.Equal("Cursed Pearl", title);
        Assert.Contains("Floors ascended before first shop", body);
        Assert.Contains("Greed combats", body);
        Assert.Contains("Greed drawn", body);
        Assert.Equal(5, body.Split("[b]0[/b]").Length - 1);
    }

    [Fact]
    public void TryBuildEmptyRelicTooltip_MangoUsesZeroMaxHpRows()
    {
        var ok = CompendiumRelicStatsContext.TryBuildEmptyRelicTooltip(
            Uninitialized<Mango>(),
            out var title,
            out var body);

        Assert.True(ok);
        Assert.Equal("Mango", title);
        Assert.Contains("Original max HP", body);
        Assert.Contains("New max HP", body);
        Assert.Contains("Max HP gained", body);
    }

    [Fact]
    public void TryBuildEmptyRelicTooltip_DoesNotCreatePanelForUnsupportedRelic()
    {
        // Circlet is the Toy Box fallback with no effect of its own, so it is
        // neither hand-tracked nor enrolled in the generic effect frames.
        var ok = CompendiumRelicStatsContext.TryBuildEmptyRelicTooltip(
            Uninitialized<Circlet>(),
            out var title,
            out var body);

        Assert.False(ok);
        Assert.Empty(title);
        Assert.Empty(body);
    }

    [Fact]
    public void TryBuildEmptyRelicTooltip_EffectFrameRelicGetsGenericPanel()
    {
        // Dream Catcher has no hand-written body; it is enrolled in the
        // generic effect frames (ADR 0004), so it still gets a panel.
        var ok = CompendiumRelicStatsContext.TryBuildEmptyRelicTooltip(
            Uninitialized<DreamCatcher>(),
            out _,
            out var body);

        Assert.True(ok);
        Assert.NotEmpty(body);
    }

    [Fact]
    public void TryBuildRelicTooltipForRun_StorybookUsesSavedBrightestFlameAggregate()
    {
        var run = new RunData { FloorReached = 9 };
        run.Aggregates["CARD.BRIGHTEST_FLAME#1"] = new CardAggregate
        {
            Plays = 4,
            TimesDrawn = 6,
            TotalEnergyGenerated = 8,
            TimesCardsDrawn = 8,
            TotalMaxHpLost = 4,
        };

        var ok = CompendiumRelicStatsContext.TryBuildRelicTooltipForRun(
            Uninitialized<Storybook>(),
            run,
            out var title,
            out var body);

        Assert.True(ok);
        Assert.Equal("Storybook", title);
        Assert.Contains("Brightest Flame played", body);
        Assert.Contains("Brightest Flame drawn", body);
        Assert.Contains("gained by Flame", body);
        Assert.Contains("Cards drawn by Flame", body);
        Assert.Contains("Max HP lost to Flame", body);
        Assert.Contains("[b]4[/b]", body);
        Assert.Contains("[b]6[/b]", body);
        Assert.Contains("[b]8[/b]", body);
    }

    private static T Uninitialized<T>() where T : class
    {
        return (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    }
}
