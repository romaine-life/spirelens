using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class RelicCompendiumFilterTests
{
    private static readonly string RepoRoot =
        RepoPaths.Root;

    [Fact]
    public void CombatRelevanceTurn_IsAnExclusiveCutoff()
    {
        Assert.True(RelicBarFilterPatch.IsBeforeCombatRelevanceCutoff(0, 2));
        Assert.True(RelicBarFilterPatch.IsBeforeCombatRelevanceCutoff(1, 2));
        Assert.False(RelicBarFilterPatch.IsBeforeCombatRelevanceCutoff(2, 2));
        Assert.False(RelicBarFilterPatch.IsBeforeCombatRelevanceCutoff(3, 2));
        Assert.True(RelicBarFilterPatch.IsBeforeCombatRelevanceCutoff(3, null));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void EffectiveNonCombatState_IncludesClassificationUseUpAndCombatFire(
        bool classifiedNonCombat,
        bool usedUp,
        bool firedThisCombat)
    {
        Assert.True(RelicBarFilterPatch.IsEffectivelyNonCombat(
            classifiedNonCombat,
            usedUp,
            firedThisCombat));
    }

    [Fact]
    public void EffectiveNonCombatState_RemainsCombatBeforeAnyResolutionSignal()
    {
        Assert.False(RelicBarFilterPatch.IsEffectivelyNonCombat(
            isClassifiedNonCombat: false,
            isUsedUp: false,
            firedThisCombat: false));
    }

    [Theory]
    [InlineData(typeof(BurningSticks), "_wasUsedThisCombat")]
    [InlineData(typeof(CentennialPuzzle), "_usedThisCombat")]
    [InlineData(typeof(LavaLamp), "_tookDamageThisCombat")]
    [InlineData(typeof(PaelsEye), "_usedThisCombat")]
    [InlineData(typeof(Permafrost), "_activatedThisCombat")]
    [InlineData(typeof(RuinedHelmet), "_usedThisCombat")]
    [InlineData(typeof(ThrowingAxe), "_usedThisCombat")]
    [InlineData(typeof(UnsettlingLamp), "_isFinishedTriggering")]
    [InlineData(typeof(Vambrace), "_blockGainedThisCombat")]
    public void TerminalCombatRelics_ReadTheirAuthoritativeNativeState(
        Type relicType,
        string stateFieldName)
    {
        var relic = (RelicModel)RuntimeHelpers.GetUninitializedObject(relicType);

        Assert.True(RelicBarFilterPatch.IsTerminalCombatRelic(relic));
        Assert.False(RelicBarFilterPatch.HasNativeTerminalCombatState(relic));

        var stateField = relicType.GetField(
            stateFieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(stateField);
        stateField!.SetValue(relic, true);

        Assert.True(RelicBarFilterPatch.HasNativeTerminalCombatState(relic));
    }

    [Theory]
    [InlineData(typeof(BeltBuckle))]
    [InlineData(typeof(MeatOnTheBone))]
    [InlineData(typeof(RedSkull))]
    public void ReversibleCombatStates_AreNotTerminal(Type relicType)
    {
        var relic = (RelicModel)RuntimeHelpers.GetUninitializedObject(relicType);

        Assert.False(RelicBarFilterPatch.IsTerminalCombatRelic(relic));
        Assert.False(RelicBarFilterPatch.HasNativeTerminalCombatState(relic));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void ContextualRelicFilter_RestoresFullBarForDeckAndActMap(
        bool isActMapOpen,
        bool isDeckViewOpen,
        bool expectedFilter)
    {
        Assert.Equal(
            expectedFilter,
            RelicBarFilterPatch.ShouldFilterForContext(
                forceCombatRelicsOnly: false,
                showCombatOnlyAtCombatScreen: true,
                isCombatInProgress: true,
                isActMapOpen,
                isDeckViewOpen));
    }

    [Fact]
    public void ForcedRelicFilter_RemainsActiveOnActMap()
    {
        Assert.True(RelicBarFilterPatch.ShouldFilterForContext(
            forceCombatRelicsOnly: true,
            showCombatOnlyAtCombatScreen: false,
            isCombatInProgress: true,
            isActMapOpen: true,
            isDeckViewOpen: false));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    public void HiddenRelicFocus_PrefersNearestVisibleRelicToTheRight(
        int sourceIndex,
        int expectedIndex)
    {
        var visibility = new[] { true, true, true };
        visibility[sourceIndex] = false;

        Assert.Equal(
            expectedIndex,
            RelicBarFilterPatch.FindNearestVisibleIndex(visibility, sourceIndex));
    }

    [Fact]
    public void HiddenRelicFocus_FallsBackLeftWhenNoVisibleRelicRemainsToTheRight()
    {
        Assert.Equal(
            0,
            RelicBarFilterPatch.FindNearestVisibleIndex(
                new[] { true, false, false },
                sourceIndex: 2));
    }

    [Fact]
    public void HiddenRelicFocus_ReturnsNoTargetWhenEveryRelicIsHidden()
    {
        Assert.Equal(
            -1,
            RelicBarFilterPatch.FindNearestVisibleIndex(
                new[] { false, false, false },
                sourceIndex: 0));
    }

    [Fact]
    public void Taxonomy_OmitsRemovedEnergyCategory()
    {
        Assert.DoesNotContain(RelicTaxonomy.Categories, category => category.Id == "energy");
        Assert.DoesNotContain(RelicTaxonomy.Categories, category => category.DisplayName == "Energy relics");
    }

    [Fact]
    public void TaxonomyJson_MirrorsHierarchyAndPlacesEveryRelicExactlyOnce()
    {
        using var taxonomyDocument = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot, "Core", "Config", "relic-taxonomy.json")));
        using var classificationDocument = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot, "Core", "Config", "relic-classifications.json")));

        var root = taxonomyDocument.RootElement;
        Assert.Equal(
            new[] { "uncategorized", "charge" },
            root.EnumerateObject().Select(property => property.Name).ToArray());

        var charge = root.GetProperty("charge");
        Assert.Equal(
            new[] { "across_combats", "across_turns", "resets_each_turn" },
            charge.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(
            new[] { "cycling", "non_cycling" },
            charge.GetProperty("across_combats").EnumerateObject()
                .Select(property => property.Name).ToArray());
        Assert.Equal(
            new[] { "limited_activations", "unlimited_activations" },
            charge.GetProperty("resets_each_turn").EnumerateObject()
                .Select(property => property.Name).ToArray());

        var lists = EnumerateRelicLists(root).ToArray();
        foreach (var list in lists)
        {
            Assert.Equal(
                list.RelicIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                list.RelicIds);
        }

        var placements = lists.SelectMany(list => list.RelicIds).ToArray();
        Assert.Equal(
            placements.Length,
            placements.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var knownRelics = classificationDocument.RootElement.GetProperty("combat").EnumerateArray()
            .Concat(classificationDocument.RootElement.GetProperty("non_combat").EnumerateArray())
            .Select(value => value.GetString()!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            knownRelics,
            placements.OrderBy(id => id, StringComparer.Ordinal).ToArray());

        Assert.Contains(
            "RELIC.ART_OF_WAR",
            charge.GetProperty("resets_each_turn").GetProperty("limited_activations")
                .EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain(
            "RELIC.ART_OF_WAR",
            root.GetProperty("uncategorized").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void ChargeTaxonomy_NestsCombatAndTurnResetGroups()
    {
        var charge = RelicTaxonomy.RootCategories.Single();

        Assert.Equal(RelicTaxonomy.ChargeCategoryId, charge.Id);
        Assert.Equal("Charge", charge.DisplayName);
        Assert.Empty(charge.RelicIds);
        Assert.Equal(
            new[]
            {
                RelicTaxonomy.ChargeAcrossCombatsCategoryId,
                RelicTaxonomy.ChargeAcrossTurnsCategoryId,
                RelicTaxonomy.ChargeResetsEachTurnCategoryId,
            },
            charge.Children.Select(category => category.Id).ToArray());
        Assert.Equal(
            new[] { "Across combats", "Across turns", "Resets each turn" },
            charge.Children.Select(category => category.DisplayName).ToArray());

        var acrossCombats = charge.Children.Single(
            category => category.Id == RelicTaxonomy.ChargeAcrossCombatsCategoryId);

        Assert.Empty(acrossCombats.RelicIds);
        Assert.Equal(
            new[]
            {
                RelicTaxonomy.ChargeAcrossCombatsCyclingCategoryId,
                RelicTaxonomy.ChargeAcrossCombatsNonCyclingCategoryId,
            },
            acrossCombats.Children.Select(category => category.Id).ToArray());
        Assert.Equal(
            new[] { "Cycling", "Non-cycling" },
            acrossCombats.Children.Select(category => category.DisplayName).ToArray());

        var resetsEachTurn = charge.Children.Single(
            category => category.Id == RelicTaxonomy.ChargeResetsEachTurnCategoryId);

        Assert.Empty(resetsEachTurn.RelicIds);
        Assert.Equal(
            new[]
            {
                RelicTaxonomy.ChargeResetsEachTurnLimitedActivationsCategoryId,
                RelicTaxonomy.ChargeResetsEachTurnUnlimitedActivationsCategoryId,
            },
            resetsEachTurn.Children.Select(category => category.Id).ToArray());
        Assert.Equal(
            new[] { "Limited activations", "Unlimited activations" },
            resetsEachTurn.Children.Select(category => category.DisplayName).ToArray());
        Assert.Equal(
            new[]
            {
                RelicTaxonomy.ChargeAcrossCombatsCyclingCategoryId,
                RelicTaxonomy.ChargeAcrossCombatsNonCyclingCategoryId,
                RelicTaxonomy.ChargeAcrossTurnsCategoryId,
                RelicTaxonomy.ChargeResetsEachTurnLimitedActivationsCategoryId,
                RelicTaxonomy.ChargeResetsEachTurnUnlimitedActivationsCategoryId,
            },
            RelicTaxonomy.LeafCategories.Select(category => category.Id).ToArray());
    }

    [Fact]
    public void ChargeAcrossTurnsTaxonomy_IncludesOnlyCombatLocalCounters()
    {
        var charge = RelicTaxonomy.LeafCategories.Single(
            c => c.Id == RelicTaxonomy.ChargeAcrossTurnsCategoryId);

        Assert.Equal(
            new[]
            {
                "RELIC.METRONOME",
                "RELIC.PAELS_FLESH",
                "RELIC.PAELS_LEGION",
                "RELIC.STONE_CALENDAR",
            },
            charge.RelicIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ChargeAcrossCombatsCyclingTaxonomy_IncludesRestartingCounters()
    {
        var charge = RelicTaxonomy.LeafCategories.Single(
            c => c.Id == RelicTaxonomy.ChargeAcrossCombatsCyclingCategoryId);

        Assert.Equal(
            new[]
            {
                "RELIC.BOOK_OF_FIVE_RINGS",
                "RELIC.FAKE_HAPPY_FLOWER",
                "RELIC.FISHING_ROD",
                "RELIC.GALACTIC_DUST",
                "RELIC.HAPPY_FLOWER",
                "RELIC.IRON_CLUB",
                "RELIC.JOSS_PAPER",
                "RELIC.LASTING_CANDY",
                "RELIC.NUNCHAKU",
                "RELIC.PAELS_WING",
                "RELIC.PENDULUM",
                "RELIC.PEN_NIB",
                "RELIC.POLLINOUS_CORE",
                "RELIC.TOY_BOX",
                "RELIC.TUNING_FORK",
            },
            charge.RelicIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ChargeAcrossCombatsNonCyclingTaxonomy_IncludesNonWrappingCounters()
    {
        var charge = RelicTaxonomy.LeafCategories.Single(
            c => c.Id == RelicTaxonomy.ChargeAcrossCombatsNonCyclingCategoryId);

        Assert.Equal(
            new[]
            {
                "RELIC.GIRYA",
                "RELIC.PAELS_TOOTH",
                "RELIC.PUMPKIN_CANDLE",
                "RELIC.SILVER_CRUCIBLE",
                "RELIC.SWORD_OF_STONE",
                "RELIC.WINGED_BOOTS",
                "RELIC.WONGOS_MYSTERY_TICKET",
            },
            charge.RelicIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ChargeResetsEachTurnLimitedActivationsTaxonomy_IncludesCappedRelics()
    {
        var charge = RelicTaxonomy.LeafCategories.Single(
            c => c.Id == RelicTaxonomy.ChargeResetsEachTurnLimitedActivationsCategoryId);

        Assert.Equal(
            new[]
            {
                "RELIC.ART_OF_WAR",
                "RELIC.BRILLIANT_SCARF",
                "RELIC.DIAMOND_DIADEM",
                "RELIC.POCKETWATCH",
                "RELIC.RAINBOW_RING",
                "RELIC.VELVET_CHOKER",
            },
            charge.RelicIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ChargeResetsEachTurnUnlimitedActivationsTaxonomy_IncludesRepeatableRelics()
    {
        var charge = RelicTaxonomy.LeafCategories.Single(
            c => c.Id == RelicTaxonomy.ChargeResetsEachTurnUnlimitedActivationsCategoryId);

        Assert.Equal(
            new[]
            {
                "RELIC.KUNAI",
                "RELIC.KUSARIGAMA",
                "RELIC.LETTER_OPENER",
                "RELIC.ORNAMENTAL_FAN",
                "RELIC.SHURIKEN",
            },
            charge.RelicIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void IsRelicInAnySelectedCategory_UsesSelectedCategories()
    {
        Assert.False(RelicTaxonomy.IsRelicInAnySelectedCategory(
            "RELIC.PEN_NIB",
            Enumerable.Empty<string>()));

        Assert.True(RelicTaxonomy.IsRelicInAnySelectedCategory(
            "RELIC.PEN_NIB",
            new[] { RelicTaxonomy.ChargeAcrossCombatsCyclingCategoryId }));

        Assert.True(RelicTaxonomy.IsRelicInAnySelectedCategory(
            "RELIC.GIRYA",
            new[] { RelicTaxonomy.ChargeAcrossCombatsNonCyclingCategoryId }));

        Assert.True(RelicTaxonomy.IsRelicInAnySelectedCategory(
            "RELIC.METRONOME",
            new[] { RelicTaxonomy.ChargeAcrossTurnsCategoryId }));

        Assert.True(RelicTaxonomy.IsRelicInAnySelectedCategory(
            "RELIC.LETTER_OPENER",
            new[] { RelicTaxonomy.ChargeResetsEachTurnUnlimitedActivationsCategoryId }));

        Assert.True(RelicTaxonomy.IsRelicInAnySelectedCategory(
            "RELIC.BRILLIANT_SCARF",
            new[] { RelicTaxonomy.ChargeResetsEachTurnLimitedActivationsCategoryId }));

        Assert.False(RelicTaxonomy.IsRelicInAnySelectedCategory(
            "RELIC.PEN_NIB",
            new[] { RelicTaxonomy.ChargeResetsEachTurnUnlimitedActivationsCategoryId }));
    }

    private static IEnumerable<(string Path, string[] RelicIds)> EnumerateRelicLists(
        JsonElement element,
        string path = "")
    {
        foreach (var property in element.EnumerateObject())
        {
            var childPath = string.IsNullOrEmpty(path)
                ? property.Name
                : $"{path}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                yield return (
                    childPath,
                    property.Value.EnumerateArray().Select(value => value.GetString()!).ToArray());
                continue;
            }

            foreach (var child in EnumerateRelicLists(property.Value, childPath))
                yield return child;
        }
    }

    [Fact]
    public void ChargeSelection_ParentControlsLeavesAndReflectsPartialState()
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            RelicTaxonomyCategorySelectionState.Unselected,
            RelicTaxonomy.GetSelectionState(RelicTaxonomy.ChargeCategoryId, selected));

        RelicTaxonomy.SetCategorySelection(
            selected,
            RelicTaxonomy.ChargeCategoryId,
            selected: true);

        Assert.Equal(
            new[]
            {
                RelicTaxonomy.ChargeAcrossCombatsCyclingCategoryId,
                RelicTaxonomy.ChargeAcrossCombatsNonCyclingCategoryId,
                RelicTaxonomy.ChargeAcrossTurnsCategoryId,
                RelicTaxonomy.ChargeResetsEachTurnLimitedActivationsCategoryId,
                RelicTaxonomy.ChargeResetsEachTurnUnlimitedActivationsCategoryId,
            },
            selected.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            RelicTaxonomyCategorySelectionState.Selected,
            RelicTaxonomy.GetSelectionState(RelicTaxonomy.ChargeCategoryId, selected));

        RelicTaxonomy.SetCategorySelection(
            selected,
            RelicTaxonomy.ChargeResetsEachTurnCategoryId,
            selected: false);

        Assert.Equal(
            RelicTaxonomyCategorySelectionState.Partial,
            RelicTaxonomy.GetSelectionState(RelicTaxonomy.ChargeCategoryId, selected));

        RelicTaxonomy.SetCategorySelection(
            selected,
            RelicTaxonomy.ChargeCategoryId,
            selected: true);

        Assert.Equal(5, selected.Count);
        Assert.Equal(
            RelicTaxonomyCategorySelectionState.Selected,
            RelicTaxonomy.GetSelectionState(RelicTaxonomy.ChargeCategoryId, selected));

        RelicTaxonomy.SetCategorySelection(
            selected,
            RelicTaxonomy.ChargeCategoryId,
            selected: false);

        Assert.Empty(selected);
        Assert.Equal(
            RelicTaxonomyCategorySelectionState.Unselected,
            RelicTaxonomy.GetSelectionState(RelicTaxonomy.ChargeCategoryId, selected));
    }

    [Fact]
    public void AcrossCombatsSelection_ReflectsMixedCyclingChildren()
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RelicTaxonomy.ChargeAcrossCombatsCyclingCategoryId,
        };

        Assert.Equal(
            RelicTaxonomyCategorySelectionState.Partial,
            RelicTaxonomy.GetSelectionState(
                RelicTaxonomy.ChargeAcrossCombatsCategoryId,
                selected));

        RelicTaxonomy.SetCategorySelection(
            selected,
            RelicTaxonomy.ChargeAcrossCombatsCategoryId,
            selected: true);

        Assert.Equal(2, selected.Count);
        Assert.Equal(
            RelicTaxonomyCategorySelectionState.Selected,
            RelicTaxonomy.GetSelectionState(
                RelicTaxonomy.ChargeAcrossCombatsCategoryId,
                selected));
    }

    [Fact]
    public void ResetsEachTurnSelection_ReflectsMixedActivationChildren()
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RelicTaxonomy.ChargeResetsEachTurnLimitedActivationsCategoryId,
        };

        Assert.Equal(
            RelicTaxonomyCategorySelectionState.Partial,
            RelicTaxonomy.GetSelectionState(
                RelicTaxonomy.ChargeResetsEachTurnCategoryId,
                selected));

        RelicTaxonomy.SetCategorySelection(
            selected,
            RelicTaxonomy.ChargeResetsEachTurnCategoryId,
            selected: true);

        Assert.Equal(2, selected.Count);
        Assert.Equal(
            RelicTaxonomyCategorySelectionState.Selected,
            RelicTaxonomy.GetSelectionState(
                RelicTaxonomy.ChargeResetsEachTurnCategoryId,
                selected));
    }

    [Fact]
    public void GetVisualAction_MapsModeAndMatchToVisualAction()
    {
        Assert.Equal(
            CompendiumRelicEntryVisualAction.Normal,
            RelicCompendiumFilterContext.GetVisualAction(CompendiumRelicFilterMode.Off, true, true, false));
        Assert.Equal(
            CompendiumRelicEntryVisualAction.Normal,
            RelicCompendiumFilterContext.GetVisualAction(CompendiumRelicFilterMode.Off, false, false, false));
        Assert.Equal(
            CompendiumRelicEntryVisualAction.Normal,
            RelicCompendiumFilterContext.GetVisualAction(
                CompendiumRelicFilterMode.IconGlossary,
                true,
                false,
                false));
        Assert.Equal(
            CompendiumRelicEntryVisualAction.Normal,
            RelicCompendiumFilterContext.GetVisualAction(CompendiumRelicFilterMode.Compare, true, true, true));
        Assert.Equal(
            CompendiumRelicEntryVisualAction.Dim,
            RelicCompendiumFilterContext.GetVisualAction(CompendiumRelicFilterMode.Compare, true, true, false));
        Assert.Equal(
            CompendiumRelicEntryVisualAction.Normal,
            RelicCompendiumFilterContext.GetVisualAction(CompendiumRelicFilterMode.Compare, false, true, false));
        Assert.Equal(
            CompendiumRelicEntryVisualAction.Hidden,
            RelicCompendiumFilterContext.GetVisualAction(CompendiumRelicFilterMode.Compare, false, false, false));
        Assert.Equal(
            CompendiumRelicEntryVisualAction.Normal,
            RelicCompendiumFilterContext.GetVisualAction(CompendiumRelicFilterMode.Filter, true, true, true));
        Assert.Equal(
            CompendiumRelicEntryVisualAction.Hidden,
            RelicCompendiumFilterContext.GetVisualAction(CompendiumRelicFilterMode.Filter, true, true, false));
        Assert.Equal(
            CompendiumRelicEntryVisualAction.Normal,
            RelicCompendiumFilterContext.GetVisualAction(CompendiumRelicFilterMode.Filter, false, true, false));
        Assert.Equal(
            CompendiumRelicEntryVisualAction.Hidden,
            RelicCompendiumFilterContext.GetVisualAction(CompendiumRelicFilterMode.Filter, false, false, false));
    }
}
