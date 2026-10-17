using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using SpireLens.Core;
using SpireLens.Core.Patches;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using Xunit;

namespace SpireLens.Core.Tests;

public class BlockTooltipTests
{
    private static readonly MethodInfo GetBlockStatLabelMethod =
        typeof(CardHoverShowPatch).GetMethod("GetBlockStatLabel", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("GetBlockStatLabel not found.");

    private static readonly MethodInfo GetDrawStatLabelMethod =
        typeof(CardHoverShowPatch).GetMethod("GetDrawStatLabel", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("GetDrawStatLabel not found.");

    private static readonly MethodInfo GetEnergyStatLabelMethod =
        typeof(CardHoverShowPatch).GetMethod("GetEnergyStatLabel", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("GetEnergyStatLabel not found.");

    private static readonly MethodInfo GetStarStatLabelMethod =
        typeof(CardHoverShowPatch).GetMethod("GetStarStatLabel", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("GetStarStatLabel not found.");

    private static readonly MethodInfo GetForgeStatLabelMethod =
        typeof(CardHoverShowPatch).GetMethod("GetForgeStatLabel", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("GetForgeStatLabel not found.");

    private static readonly MethodInfo AppendCompactBodyMethod =
        typeof(CardHoverShowPatch).GetMethod("AppendCompactBody", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AppendCompactBody not found.");

    private static readonly MethodInfo AppendCardDrawStatsMethod =
        typeof(CardHoverShowPatch).GetMethod("AppendCardDrawStats", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AppendCardDrawStats not found.");

    private static readonly MethodInfo AppendOrbCreationStatsMethod =
        typeof(CardHoverShowPatch).GetMethod("AppendOrbCreationStats", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AppendOrbCreationStats not found.");

    private static readonly MethodInfo Row3Method =
        typeof(CardHoverShowPatch).GetMethod("Row3", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Row3 not found.");

    [Fact]
    public void CardScalarRows_ShareNaturalWidthTableAndLeftAlignValues()
    {
        var body = new StringBuilder();
        _ = Row3Method.Invoke(null, new object?[] { body, "Short", "0", "", null });
        _ = Row3Method.Invoke(null, new object?[]
        {
            body,
            "The longest semantic label",
            "false",
            "100%",
            null,
        });
        var markup = body.ToString();

        Assert.Equal(1, markup.Split("[table=4]", StringSplitOptions.None).Length - 1);
        Assert.Contains("[left][b]0[/b][/left]", markup);
        Assert.Contains("[left][b]false[/b][/left]", markup);
        Assert.DoesNotContain("[right]", markup);
    }

    [Fact]
    public void GetBlockStatLabel_UsesShieldIcon()
    {
        var label = (string)(GetBlockStatLabelMethod.Invoke(null, new object?[] { "gained" })
            ?? throw new InvalidOperationException("GetBlockStatLabel returned null."));

        Assert.Equal("[img=16x16]res://images/ui/combat/block.png[/img] gained", label);
    }

    [Trait("Category", "RequiresLiveGame")]
    [Fact]
    public void AppendCompactBody_UsesShieldIconForBlockRows()
    {
        var cardModel = CreateCardModel(CardType.Skill);
        var agg = new CardAggregate
        {
            Plays = 2,
            TimesDrawn = 3,
            TotalBlockGained = 9,
        };

        var sb = new StringBuilder();
        _ = AppendCompactBodyMethod.Invoke(null, new object?[] { sb, cardModel, agg });
        var text = sb.ToString();

        Assert.Contains(StatConceptGlossary.RenderHintedGlyph("block"), text);
        Assert.Contains("gained", text);
        Assert.Contains("[b]9[/b]", text);
    }

    [Fact]
    public void GetDrawStatLabel_UsesDrawCardsNextTurnPowerIcon()
    {
        var label = (string)(GetDrawStatLabelMethod.Invoke(null, new object?[] { "cards drawn" })
            ?? throw new InvalidOperationException("GetDrawStatLabel returned null."));

        Assert.Equal("[img=16x16]res://images/atlases/power_atlas.sprites/draw_cards_next_turn_power.tres[/img] cards drawn", label);
    }

    [Fact]
    public void GetDrawStatLabel_UsesDrawCardsNextTurnPowerIconForBlockedRows()
    {
        var label = (string)(GetDrawStatLabelMethod.Invoke(null, new object?[] { "draws blocked" })
            ?? throw new InvalidOperationException("GetDrawStatLabel returned null."));

        Assert.Equal("[img=16x16]res://images/atlases/power_atlas.sprites/draw_cards_next_turn_power.tres[/img] draws blocked", label);
    }

    [Fact]
    public void GetDrawStatLabel_UsesDrawCardsNextTurnPowerIconForAttemptedRows()
    {
        var label = (string)(GetDrawStatLabelMethod.Invoke(null, new object?[] { "drawn / tried" })
            ?? throw new InvalidOperationException("GetDrawStatLabel returned null."));

        Assert.Equal("[img=16x16]res://images/atlases/power_atlas.sprites/draw_cards_next_turn_power.tres[/img] drawn / tried", label);
    }

    [Fact]
    public void GetEnergyStatLabel_UsesIroncladEnergyIconWithoutRunContext()
    {
        var label = (string)(GetEnergyStatLabelMethod.Invoke(null, new object?[] { "gained" })
            ?? throw new InvalidOperationException("GetEnergyStatLabel returned null."));

        Assert.Equal(
            "[img=16x16]res://images/packed/sprite_fonts/ironclad_energy_icon.png[/img] gained",
            label);
    }

    [Fact]
    public void GetStarStatLabel_UsesStarIcon()
    {
        var label = (string)(GetStarStatLabelMethod.Invoke(null, new object?[] { "gained" })
            ?? throw new InvalidOperationException("GetStarStatLabel returned null."));

        Assert.Equal("[img=16x16]res://images/packed/sprite_fonts/star_icon.png[/img] gained", label);
    }

    [Fact]
    public void GetForgeStatLabel_UsesQuietTextLabel()
    {
        var label = (string)(GetForgeStatLabelMethod.Invoke(null, new object?[] { "gained" })
            ?? throw new InvalidOperationException("GetForgeStatLabel returned null."));

        Assert.Equal("Forge gained", label);
    }

    [Trait("Category", "RequiresLiveGame")]
    [Fact]
    public void AppendCompactBody_UsesDrawPowerIconForUnplayableDrawRows()
    {
        var cardModel = CreateCardModel(CardType.Curse);
        var agg = new CardAggregate
        {
            TimesDrawn = 4,
        };

        var sb = new StringBuilder();
        _ = AppendCompactBodyMethod.Invoke(null, new object?[] { sb, cardModel, agg });
        var text = sb.ToString();

        Assert.Contains(StatConceptGlossary.RenderHintedGlyph("draw"), text);
        Assert.Contains("[b]4[/b]", text);
    }

    [Trait("Category", "RequiresLiveGame")]
    [Fact]
    public void AppendCompactBody_UsesCharacterEnergyIconForEnergyRows()
    {
        var cardModel = CreateCardModel(CardType.Skill);
        var agg = new CardAggregate
        {
            Plays = 2,
            TimesDrawn = 3,
            TotalEnergyGenerated = 2,
        };

        var sb = new StringBuilder();
        _ = AppendCompactBodyMethod.Invoke(null, new object?[] { sb, cardModel, agg });
        var text = sb.ToString();

        Assert.Contains(StatConceptGlossary.RenderHintedGlyph("energy"), text);
        // Generated and wasted share one row, so the compact view cannot
        // show the gain without saying how much of it expired.
        Assert.Contains("gained/wasted", text);
        Assert.Contains("[b]2/0[/b]", text);
    }

    [Trait("Category", "RequiresLiveGame")]
    [Fact]
    public void AppendCompactBody_UsesStarIconForStarRows()
    {
        var cardModel = CreateCardModel(CardType.Skill);
        var agg = new CardAggregate
        {
            Plays = 2,
            TimesDrawn = 3,
            TotalStarsGenerated = 2,
        };

        var sb = new StringBuilder();
        _ = AppendCompactBodyMethod.Invoke(null, new object?[] { sb, cardModel, agg });
        var text = sb.ToString();

        Assert.Contains(StatConceptGlossary.RenderHintedGlyph("stars"), text);
        Assert.Contains("gained", text);
        Assert.Contains("[b]2[/b]", text);
    }

    [Trait("Category", "RequiresLiveGame")]
    [Fact]
    public void AppendCompactBody_UsesQuietTextForForgeRows()
    {
        var cardModel = CreateCardModel(CardType.Skill);
        var agg = new CardAggregate
        {
            Plays = 2,
            TimesDrawn = 3,
            TotalForgeGenerated = 6m,
        };

        var sb = new StringBuilder();
        _ = AppendCompactBodyMethod.Invoke(null, new object?[] { sb, cardModel, agg });
        var text = sb.ToString();

        Assert.Contains("Forge gained", text);
        Assert.DoesNotContain("[img=16x16]", text);
        Assert.Contains("[b]6[/b]", text);
    }

    [Trait("Category", "RequiresLiveGame")]
    [Fact]
    public void AppendCompactBody_ShowsObservedOrbsCreated()
    {
        var cardModel = CreateCardModel(CardType.Skill);
        var agg = new CardAggregate
        {
            Plays = 2,
            TimesDrawn = 3,
            TotalOrbsCreated = 4,
        };

        var sb = new StringBuilder();
        _ = AppendCompactBodyMethod.Invoke(null, new object?[] { sb, cardModel, agg });
        var text = sb.ToString();

        // "Orbs" renders as an orb concept icon now, leaving "created" as the
        // residual label text, so identify the row by its hint rather than by
        // wording the vocabulary has replaced.
        Assert.Contains("[hint=\"created\"]", text);
        Assert.Contains("[b]4[/b]", text);
    }

    [Fact]
    public void AppendOrbCreationStats_ShowsLifecycleAndSeparateFrostBlockIcons()
    {
        var agg = new CardAggregate
        {
            TotalOrbsCreated = 2,
        };
        agg.OrbOutcomes["ORB.FROST_ORB"] = new CardOrbAggregate
        {
            OrbId = "ORB.FROST_ORB",
            Created = 2,
            PassiveActivations = 5,
            Evokes = 1,
            Fizzles = 0,
            BlockGained = 17,
        };

        var sb = new StringBuilder();
        _ = AppendOrbCreationStatsMethod.Invoke(
            null,
            new object?[] { sb, null, agg, null, false });
        var text = sb.ToString();

        Assert.Contains("[hint=", text);
        Assert.Contains("res://images/orbs/frost_orb.png", text);
        Assert.Contains("created", text);
        Assert.Contains(StatConceptGlossary.RenderHintedGlyph("activation"), text);
        Assert.Contains("passive", text);
        Assert.Contains("evoked", text);
        Assert.Contains("fizzled", text);
        Assert.Contains(StatConceptGlossary.RenderHintedGlyph("block"), text);
        Assert.Contains("[b]17[/b]", text);
    }

    [Fact]
    public void AppendOrbCreationStats_ShowsLightningDamageOutcomesSeparately()
    {
        var agg = new CardAggregate
        {
            TotalOrbsCreated = 2,
        };
        agg.OrbOutcomes["ORB.LIGHTNING_ORB"] = new CardOrbAggregate
        {
            OrbId = "ORB.LIGHTNING_ORB",
            Created = 2,
            PassiveActivations = 3,
            Evokes = 1,
            DamageAttempted = 18,
            DamageDealt = 12,
            DamageBlocked = 4,
            DamageOverkill = 2,
            Kills = 1,
            TargetsHit = 4,
        };

        var sb = new StringBuilder();
        _ = AppendOrbCreationStatsMethod.Invoke(
            null,
            new object?[] { sb, null, agg, null, false });
        var text = sb.ToString();

        Assert.Contains("[hint=", text);
        Assert.Contains("res://images/orbs/lightning_orb.png", text);
        Assert.Contains(
            StatConceptGlossary.RenderInformationHint("damage attempted"),
            text);
        Assert.Contains(
            StatConceptGlossary.RenderInformationHint("damage dealt"),
            text);
        Assert.Contains(
            StatConceptGlossary.RenderInformationHint("damage blocked"),
            text);
        Assert.Contains(
            StatConceptGlossary.RenderInformationHint("overkill"),
            text);
        Assert.Contains(
            StatConceptGlossary.RenderInformationHint("kills"),
            text);
        Assert.Contains(
            StatConceptGlossary.RenderInformationHint("targets hit"),
            text);
        Assert.Contains(
            StatConceptGlossary.RenderHintedGlyph("targets_hit"),
            text);
        Assert.Contains("[b]18[/b]", text);
        Assert.Contains("[b]12[/b]", text);
        Assert.Contains("[b]4[/b]", text);
        Assert.Contains("[b]2[/b]", text);
        Assert.Contains("[b]1[/b]", text);
    }

    [Fact]
    public void AppendOrbCreationStats_ShowsDarkLifecycleAndDamageAtZero()
    {
        var sb = new StringBuilder();
        _ = AppendOrbCreationStatsMethod.Invoke(
            null,
            new object?[]
            {
                sb,
                new Darkness(),
                new CardAggregate(),
                new RunMetaStats(),
                false,
            });
        var text = sb.ToString();

        Assert.Contains("[hint=", text);
        Assert.Contains("res://images/orbs/dark_orb.png", text);
        Assert.Contains("created", text);
        Assert.Contains("passive", text);
        Assert.Contains("evoked", text);
        Assert.Contains("damage attempted", text);
        Assert.Contains("targets hit", text);
    }

    [Fact]
    public void AppendOrbCreationStats_ShowsPlasmaEnergyFromExactOrb()
    {
        var agg = new CardAggregate { TotalOrbsCreated = 1 };
        agg.OrbOutcomes["ORB.PLASMA_ORB"] = new CardOrbAggregate
        {
            OrbId = "ORB.PLASMA_ORB",
            Created = 1,
            EnergyGenerated = 4,
        };

        var sb = new StringBuilder();
        _ = AppendOrbCreationStatsMethod.Invoke(
            null,
            new object?[]
            {
                sb,
                new Fusion(),
                agg,
                new RunMetaStats(),
                false,
            });
        var text = sb.ToString();

        Assert.Contains("[hint=", text);
        Assert.Contains("res://images/orbs/plasma_orb.png", text);
        Assert.Contains(StatConceptGlossary.RenderHintedGlyph("energy"), text);
        Assert.Contains("[b]4[/b]", text);
    }

    [Fact]
    public void AppendOrbCreationStats_CompactCreatedRowUsesOrbIcon()
    {
        var sb = new StringBuilder();
        _ = AppendOrbCreationStatsMethod.Invoke(
            null,
            new object?[]
            {
                sb,
                new Darkness(),
                new CardAggregate(),
                new RunMetaStats(),
                true,
            });
        var text = sb.ToString();

        Assert.Contains("[hint=", text);
        Assert.Contains("res://images/orbs/dark_orb.png", text);
        Assert.Contains("created", text);
        Assert.DoesNotContain("Orbs created", text);
    }

    [Fact]
    public void AppendOrbCreationStats_RandomFallbackShowsEligibleOrbIcons()
    {
        var sb = new StringBuilder();
        _ = AppendOrbCreationStatsMethod.Invoke(
            null,
            new object?[]
            {
                sb,
                new Chaos(),
                new CardAggregate(),
                new RunMetaStats(),
                false,
            });
        var text = sb.ToString();

        Assert.Contains("[hint=", text);
        Assert.Contains("res://images/orbs/dark_orb.png", text);
        Assert.Contains("res://images/orbs/frost_orb.png", text);
        Assert.Contains("res://images/orbs/glass_orb.png", text);
        Assert.Contains("res://images/orbs/lightning_orb.png", text);
        Assert.Contains("res://images/orbs/plasma_orb.png", text);
        Assert.Contains("created", text);
    }

    [Fact]
    public void AppendCardDrawStats_ShowsActualVersusAttemptedWhenGapExists()
    {
        var agg = new CardAggregate
        {
            TimesCardsDrawn = 1,
            TimesCardsDrawAttempted = 3,
            BlockedDrawReasons =
            {
                ["effect:POWER.NO_DRAW"] = new BlockedDrawReasonAggregate
                {
                    ReasonId = "effect:POWER.NO_DRAW",
                    DisplayName = "No Draw",
                    Count = 2,
                }
            }
        };

        var sb = new StringBuilder();
        _ = AppendCardDrawStatsMethod.Invoke(null, new object?[] { sb, agg });
        var text = sb.ToString();

        Assert.Contains(StatConceptGlossary.RenderHintedGlyph("draw"), text);
        Assert.Contains("tried", text);
        Assert.Contains("[b]1/3[/b]", text);
        Assert.Contains("blocked by No", text);
        Assert.Contains("[b]2[/b]", text);
    }

    [Fact]
    public void AppendCardDrawStats_FallsBackToLegacyBlockedGapWhenAttemptedIsMissing()
    {
        var agg = new CardAggregate
        {
            TimesCardsDrawn = 0,
            TimesCardsDrawBlocked = 3,
        };

        var sb = new StringBuilder();
        _ = AppendCardDrawStatsMethod.Invoke(null, new object?[] { sb, agg });
        var text = sb.ToString();

        Assert.Contains(StatConceptGlossary.RenderHintedGlyph("draw"), text);
        Assert.Contains("tried", text);
        Assert.Contains("[b]0/3[/b]", text);
        Assert.Contains("blocked by other", text);
    }

    [Fact]
    public void AppendCardDrawStats_ShowsHandFullReasonWhenCategorized()
    {
        var agg = new CardAggregate
        {
            TimesCardsDrawn = 1,
            TimesCardsDrawAttempted = 3,
            BlockedDrawReasons =
            {
                ["full_hand"] = new BlockedDrawReasonAggregate
                {
                    ReasonId = "full_hand",
                    DisplayName = "hand full",
                    Count = 2,
                }
            }
        };

        var sb = new StringBuilder();
        _ = AppendCardDrawStatsMethod.Invoke(null, new object?[] { sb, agg });
        var text = sb.ToString();

        Assert.Contains(StatConceptGlossary.RenderHintedGlyph("draw"), text);
        Assert.Contains("blocked by hand full", text);
        Assert.Contains("[b]2[/b]", text);
    }
    private static CardModel CreateCardModel(CardType type)
    {
        var concreteCardType = typeof(CardModel).Assembly.GetTypes()
            .FirstOrDefault(t => typeof(CardModel).IsAssignableFrom(t) && !t.IsAbstract)
            ?? throw new InvalidOperationException("No concrete CardModel subtype found.");
        var card = (CardModel)RuntimeHelpers.GetUninitializedObject(concreteCardType);

        var typeField = typeof(CardModel).GetField("<Type>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CardModel.Type backing field not found.");
        typeField.SetValue(card, type);

        var keywordsField = typeof(CardModel).GetField("_keywords", BindingFlags.Instance | BindingFlags.NonPublic);
        if (keywordsField != null && keywordsField.GetValue(card) == null)
        {
            object? emptyKeywords = keywordsField.FieldType.IsArray
                ? Array.CreateInstance(keywordsField.FieldType.GetElementType()
                    ?? throw new InvalidOperationException("Keywords element type not found."), 0)
                : Activator.CreateInstance(keywordsField.FieldType);

            if (emptyKeywords != null)
                keywordsField.SetValue(card, emptyKeywords);
        }

        return card;
    }
}
