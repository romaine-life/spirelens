using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;


namespace SpireLens.Core.Patches;

internal readonly record struct LetterOpenerRates(
    decimal DamagePerCombat,
    decimal DamagePerTurn,
    decimal TargetsPerActivation,
    decimal DamagePerSkillPlayed);

internal readonly record struct RelicLiveActivationCounts(
    int ThisTurn,
    int ThisCombat);

/// <summary>
/// Eternal Feather's heal as the game would size it right now, from the live
/// owner's deck rather than from anything observed.
/// </summary>
internal readonly record struct EternalFeatherLiveHeal(
    decimal Heal,
    int DeckCards,
    int CardsPerHeal);

internal readonly record struct ThreeAttackScalingRelicStats(
    int AttacksPlayed,
    int Activations,
    decimal AttributeGained,
    int RateActivations,
    int Turns,
    int Combats,
    int TurnsEndedAt1Charge,
    int TurnsEndedAt2Charges,
    int TurnEndChargeTotal,
    int TurnEndChargeCount);

/// <summary>
/// Builds the native SpireLens hover-tip entry for an owned relic.
/// NHoverTipSet owns the rendered node and its complete lifecycle.
/// </summary>
public static class RelicHoverShowPatch
{
    private const string StatsTableClose = "[/table]\n";
    private const string EnthralledDefinitionId = "CARD.ENTHRALLED";
    private const string CursedPearlCurseDefinitionId = "CARD.GREED";
    private const string BrightestFlameDefinitionId = "CARD.BRIGHTEST_FLAME";
    private const string DowsingRodRelicId = "RELIC.DOWSING_ROD";
    private const string ToyBoxRelicId = "RELIC.TOY_BOX";
    private const string VulnerableIconPath = "res://images/atlases/power_atlas.sprites/vulnerable_power.tres";
    private const string WeakIconPath = "res://images/atlases/power_atlas.sprites/weak_power.tres";
    private const string BlockIconPath = "res://images/ui/combat/block.png";
    private const string DrawIconPath = "res://images/atlases/power_atlas.sprites/draw_cards_next_turn_power.tres";
    private const string PaelsWingIconPath = "res://images/atlases/relic_atlas.sprites/paels_wing.tres";
    private const string GenericRelicIconPath = "res://images/ui/reward_screen/reward_icon_shared_relic.png";
    private const string StarIconPath = "res://images/packed/sprite_fonts/star_icon.png";
    private const string VigorIconPath = "res://images/atlases/power_atlas.sprites/vigor_power.tres";
    private const string MapCombatIconPath = "res://images/atlases/ui_atlas.sprites/map/icons/map_monster.tres";
    private const string MapShopIconPath = "res://images/atlases/ui_atlas.sprites/map/icons/map_shop.tres";
    private const string MapUnknownIconPath = "res://images/atlases/ui_atlas.sprites/map/icons/map_unknown.tres";
    private const string MapTreasureIconPath = "res://images/atlases/ui_atlas.sprites/map/icons/map_chest.tres";
    private const string MapRestSiteIconPath = "res://images/atlases/ui_atlas.sprites/map/icons/map_rest.tres";
    private const string AttacksPlayedDescription =
        "The number of Attack cards played while this relic was held.";
    private const string AttacksPlayedWhileActiveDescription =
        "The number of Attack cards played while this relic was active.";
    private const int SealOfGoldLossPerTrigger = 5;
    private const float SturdyClampTooltipWidth = 420f;
    private const float EmberTeaTooltipWidth = 500f;
    private const float PaelsWingTooltipWidth = 500f;
    private const float FresnelLensTooltipWidth = 500f;
    private static readonly System.Reflection.FieldInfo? VambraceBlockGainedThisCombatField =
        AccessTools.Field(typeof(Vambrace), "_blockGainedThisCombat");
    private static readonly System.Reflection.FieldInfo? PermafrostActivatedThisCombatField =
        AccessTools.Field(typeof(Permafrost), "_activatedThisCombat");
    private static readonly System.Reflection.FieldInfo? RainbowRingAttacksPlayedThisTurnField =
        AccessTools.Field(typeof(RainbowRing), "_attacksPlayedThisTurn");
    private static readonly System.Reflection.FieldInfo? RainbowRingPowersPlayedThisTurnField =
        AccessTools.Field(typeof(RainbowRing), "_powersPlayedThisTurn");
    private static readonly System.Reflection.FieldInfo? RainbowRingSkillsPlayedThisTurnField =
        AccessTools.Field(typeof(RainbowRing), "_skillsPlayedThisTurn");
    private static readonly System.Reflection.FieldInfo? KunaiAttacksPlayedThisTurnField =
        AccessTools.Field(typeof(Kunai), "_attacksPlayedThisTurn");
    private static readonly System.Reflection.FieldInfo? OrnamentalFanAttacksPlayedThisTurnField =
        AccessTools.Field(typeof(OrnamentalFan), "_attacksPlayedThisTurn");
    private static readonly System.Reflection.FieldInfo? ShurikenAttacksPlayedThisTurnField =
        AccessTools.Field(typeof(Shuriken), "_attacksPlayedThisTurn");
    internal static bool TryBuildNativeHoverTip(
        NRelicInventoryHolder holder,
        out HoverTip statsTip)
    {
        statsTip = default;
        if (!ViewStatsInjectorPatch.StatsVisibilityEnabled) return false;

        var relicModel = holder?.Relic?.Model;
        if (relicModel == null) return false;
        if (!TryBuildInventoryBodyBBCode(holder, relicModel, out var title, out var body))
            return false;

        statsTip = StatsTooltip.CreateNativeTip(
            title,
            body,
            stretchHorizontally: ShouldStretchStatsTooltip(relicModel, body));
        return true;
    }

    internal static bool TryBuildInventoryBodyBBCode(
        Node? hoverNode,
        RelicModel relicModel,
        out string title,
        out string body)
    {
        title = "";
        body = "";

        var relicId = GetStatsAggregateId(relicModel);
        var useEndedRun = IsGameOverScreenHover(hoverNode);

        RelicAggregate? aggregate = null;
        CardAggregate? bloodSoakedRoseCurseAgg = null;
        CardAggregate? cursedPearlCurseAgg = null;
        CardAggregate? storybookBrightestFlameAgg = null;
        IReadOnlyDictionary<string, CardAggregate>? neowsBonesCurseAggs = null;
        RunMetaStats? phylacteryOstyStats = null;
        int? floorCount = null;

        if (useEndedRun)
        {
            RunTracker.TryLoadLastEndedRunForCurrentGameStartTime();
            aggregate = RunTracker.GetLastEndedRelicAggregate(relicId);
            floorCount = RunTracker.GetLastEndedFloorForRateStats();
            if (relicModel is BoundPhylactery or PhylacteryUnbound)
                phylacteryOstyStats = RunTracker.GetLastEndedMetaStats();
            if (relicModel is BloodSoakedRose)
            {
                bloodSoakedRoseCurseAgg =
                    RunTracker.GetLastEndedPooledCardAggregateByDefinition(EnthralledDefinitionId)
                    ?? new CardAggregate();
            }
            else if (relicModel is CursedPearl)
            {
                cursedPearlCurseAgg =
                    RunTracker.GetLastEndedPooledCardAggregateByDefinition(CursedPearlCurseDefinitionId)
                    ?? new CardAggregate();
            }
            else if (relicModel is Storybook)
            {
                storybookBrightestFlameAgg =
                    RunTracker.GetLastEndedPooledCardAggregateByDefinition(BrightestFlameDefinitionId)
                    ?? new CardAggregate();
            }
            else if (relicModel is NeowsBones && aggregate != null)
            {
                neowsBonesCurseAggs = BuildGrantedCurseAggregates(
                    aggregate,
                    definitionId => RunTracker.GetLastEndedPooledCardAggregateByDefinition(definitionId)
                                    ?? new CardAggregate());
            }
        }

        aggregate ??= IsStrikeDummyStatsRelicModel(relicModel)
            ? RunTracker.GetStrikeDummyAggregate()
            : IsMiniatureCannonStatsRelicModel(relicModel)
                ? RunTracker.GetMiniatureCannonAggregate()
            : RunTracker.GetRelicAggregate(relicId);

        if (!useEndedRun
            && string.Equals(relicId, DowsingRodRelicId, StringComparison.Ordinal))
        {
            var liveRoomsRemaining = RunTracker.GetLiveDowsingRoomsRemaining();
            if (liveRoomsRemaining.HasValue)
            {
                aggregate ??= new RelicAggregate();
                aggregate.DowsingQuestionRoomsRemaining = liveRoomsRemaining.Value;
            }
        }

        if (relicModel is BloodSoakedRose && bloodSoakedRoseCurseAgg == null)
            bloodSoakedRoseCurseAgg = RunTracker.GetEnthralledCurseAggregate();
        if (relicModel is CursedPearl && cursedPearlCurseAgg == null)
            cursedPearlCurseAgg = RunTracker.GetCursedPearlCurseAggregate();
        if (relicModel is Storybook && storybookBrightestFlameAgg == null)
            storybookBrightestFlameAgg = RunTracker.GetPooledCardAggregateByDefinition(BrightestFlameDefinitionId);
        if (relicModel is NeowsBones && neowsBonesCurseAggs == null)
        {
            neowsBonesCurseAggs = BuildGrantedCurseAggregates(
                aggregate ?? new RelicAggregate(),
                RunTracker.GetPooledCardAggregateByDefinition);
        }

        floorCount ??= RunTracker.GetCurrentFloorForRateStats();
        if (!useEndedRun && relicModel is BoundPhylactery or PhylacteryUnbound)
            phylacteryOstyStats = RunTracker.GetEffectiveMetaStats();

        var liveActivationCounts = useEndedRun
            ? null
            : GetLiveAttackChargeRelicActivationCounts(relicModel, relicId);
        var liveEternalFeatherHeal = useEndedRun
            ? null
            : GetLiveEternalFeatherHeal(relicModel);

        var built = TryBuildBodyBBCodeWithLiveActivationCounts(
            relicModel,
            aggregate ?? new RelicAggregate(),
            floorCount,
            bloodSoakedRoseCurseAgg,
            cursedPearlCurseAgg,
            neowsBonesCurseAggs,
            storybookBrightestFlameAgg,
            phylacteryOstyStats,
            liveActivationCounts,
            liveEternalFeatherHeal,
            out title,
            out body);

        if (relicModel.IsWax)
        {
            var toyBoxAggregate = useEndedRun
                ? RunTracker.GetLastEndedRelicAggregate(ToyBoxRelicId)
                : RunTracker.GetRelicAggregate(ToyBoxRelicId);
            ApplyWaxRelicPresentation(
                relicModel,
                toyBoxAggregate,
                ref title,
                ref body);
            return true;
        }

        return built;
    }

    internal static string GetStatsAggregateId(RelicModel relicModel)
    {
        if (IsAnchorStatsRelicModel(relicModel))
            return "RELIC.ANCHOR";

        if (IsStrikeDummyStatsRelicModel(relicModel))
            return "RELIC.STRIKE_DUMMY";

        if (IsMiniatureCannonStatsRelicModel(relicModel))
            return "RELIC.MINIATURE_CANNON";

        if (IsMrStrugglesStatsRelicModel(relicModel))
            return "RELIC.MR_STRUGGLES";

        if (relicModel is Storybook)
            return "RELIC.STORYBOOK";

        if (relicModel is SwordOfStone or SwordOfJade)
            return "RELIC.SWORD_OF_STONE";

        if (relicModel is ScreamingFlagon)
            return "RELIC.SCREAMING_FLAGON";

        return relicModel.Id.ToString();
    }

    private static int? RelicFloorAddedToDeck(RelicModel relicModel)
    {
        try
        {
            var floor = relicModel.FloorAddedToDeck;
            return floor > 0 ? floor : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsGameOverScreenHover(Node? node)
    {
        for (var current = node; current != null; current = current.GetParent())
        {
            if (current is NGameOverScreen)
                return true;
        }

        // The in-run relic inventory is persistent global UI. On the game-over
        // screen its holders remain under the global top bar rather than being
        // reparented beneath NGameOverScreen, so ancestor-only detection can
        // never identify the death-screen context. Resolve the actual active
        // screen in the shared scene tree instead.
        var tree = node?.GetTree() ?? Engine.GetMainLoop() as SceneTree;
        return tree?.Root != null && ContainsVisibleGameOverScreen(tree.Root);
    }

    private static bool ContainsVisibleGameOverScreen(Node node)
    {
        if (node is NGameOverScreen screen && screen.IsVisibleInTree())
            return true;

        for (var i = 0; i < node.GetChildCount(); i++)
        {
            if (ContainsVisibleGameOverScreen(node.GetChild(i)))
                return true;
        }

        return false;
    }

    private static RelicLiveActivationCounts? GetLiveAttackChargeRelicActivationCounts(
        RelicModel relicModel,
        string relicId)
    {
        try
        {
            if (relicModel?.Owner == null) return null;

            var activationsThisCombat = RunTracker.GetRelicActivationsThisCombat(relicId);
            if (!activationsThisCombat.HasValue) return null;

            var attacksPlayedThisTurnField = relicModel switch
            {
                Kunai => KunaiAttacksPlayedThisTurnField,
                OrnamentalFan => OrnamentalFanAttacksPlayedThisTurnField,
                Shuriken => ShurikenAttacksPlayedThisTurnField,
                _ => null,
            };
            if (attacksPlayedThisTurnField?.GetValue(relicModel) is not int attacksPlayedThisTurn)
                return null;

            var attacksPerActivation = Math.Max(1, relicModel.DynamicVars.Cards.IntValue);
            return new RelicLiveActivationCounts(
                Math.Max(0, attacksPlayedThisTurn) / attacksPerActivation,
                Math.Max(0, activationsThisCombat.Value));
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"GetLiveAttackChargeRelicActivationCounts failed: {e.Message}");
            return null;
        }
    }

    internal static bool TryBuildBodyBBCode(
        RelicModel relicModel,
        RelicAggregate agg,
        int? floorCount,
        out string title,
        out string body)
    {
        return TryBuildBodyBBCode(relicModel, agg, floorCount, null, null, null, null, out title, out body);
    }

    /// <summary>
    /// Read Eternal Feather's heal for the deck as it stands now, mirroring the
    /// game's own <c>Heal * floor(deck count / Cards)</c> sizing. Returns null
    /// when the relic has no live owner, so unowned compendium entries and
    /// historical relic views do not claim a current value.
    /// </summary>
    private static EternalFeatherLiveHeal? GetLiveEternalFeatherHeal(RelicModel relicModel)
    {
        try
        {
            if (relicModel is not EternalFeather feather) return null;

            var deckCards = feather.Owner?.Deck?.Cards?.Count;
            if (!deckCards.HasValue) return null;

            var cardsPerHeal = Math.Max(1, feather.DynamicVars.Cards.IntValue);
            decimal heal = feather.DynamicVars.Heal.BaseValue * (deckCards.Value / cardsPerHeal);
            return new EternalFeatherLiveHeal(heal, deckCards.Value, cardsPerHeal);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"GetLiveEternalFeatherHeal failed: {e.Message}");
            return null;
        }
    }

    internal static float? GetPreferredStatsTooltipWidth(RelicModel? relicModel)
        => relicModel switch
        {
            SturdyClamp => SturdyClampTooltipWidth,
            EmberTea => EmberTeaTooltipWidth,
            RedSkull => EmberTeaTooltipWidth,
            WhisperingEarring => EmberTeaTooltipWidth,
            TungstenRod => EmberTeaTooltipWidth,
            PaelsWing => PaelsWingTooltipWidth,
            FresnelLens => FresnelLensTooltipWidth,
            _ => null,
        };

    internal static bool ShouldStretchStatsTooltip(
        RelicModel? relicModel,
        string? bodyBBCode)
    {
        if (relicModel == null) return false;

        // A shared scalar table deliberately sizes its label column from the
        // longest row. The game's default 360px wrapped hover tip does not
        // shrink table columns, so allow the native control to grow to that
        // calculated width instead of clipping the aligned value columns.
        return GetPreferredStatsTooltipWidth(relicModel).HasValue
               || StatsTooltip.ContainsScalarStatTable(bodyBBCode);
    }

    internal static bool TryBuildBodyBBCode(
        RelicModel relicModel,
        RelicAggregate agg,
        int? floorCount,
        CardAggregate? bloodSoakedRoseCurseAgg,
        CardAggregate? cursedPearlCurseAgg,
        IReadOnlyDictionary<string, CardAggregate>? neowsBonesCurseAggs,
        out string title,
        out string body)
    {
        return TryBuildBodyBBCode(
            relicModel,
            agg,
            floorCount,
            bloodSoakedRoseCurseAgg,
            cursedPearlCurseAgg,
            neowsBonesCurseAggs,
            null,
            out title,
            out body);
    }

    internal static bool TryBuildBodyBBCode(
        RelicModel relicModel,
        RelicAggregate agg,
        int? floorCount,
        CardAggregate? bloodSoakedRoseCurseAgg,
        CardAggregate? cursedPearlCurseAgg,
        IReadOnlyDictionary<string, CardAggregate>? neowsBonesCurseAggs,
        CardAggregate? storybookBrightestFlameAgg,
        out string title,
        out string body)
    {
        return TryBuildBodyBBCode(
            relicModel,
            agg,
            floorCount,
            bloodSoakedRoseCurseAgg,
            cursedPearlCurseAgg,
            neowsBonesCurseAggs,
            storybookBrightestFlameAgg,
            null,
            out title,
            out body);
    }

    internal static bool TryBuildBodyBBCode(
        RelicModel relicModel,
        RelicAggregate agg,
        int? floorCount,
        CardAggregate? bloodSoakedRoseCurseAgg,
        CardAggregate? cursedPearlCurseAgg,
        IReadOnlyDictionary<string, CardAggregate>? neowsBonesCurseAggs,
        CardAggregate? storybookBrightestFlameAgg,
        RunMetaStats? phylacteryOstyStats,
        out string title,
        out string body)
    {
        return TryBuildBodyBBCodeWithLiveActivationCounts(
            relicModel,
            agg,
            floorCount,
            bloodSoakedRoseCurseAgg,
            cursedPearlCurseAgg,
            neowsBonesCurseAggs,
            storybookBrightestFlameAgg,
            phylacteryOstyStats,
            null,
            null,
            out title,
            out body);
    }

    private static bool TryBuildBodyBBCodeWithLiveActivationCounts(
        RelicModel relicModel,
        RelicAggregate agg,
        int? floorCount,
        CardAggregate? bloodSoakedRoseCurseAgg,
        CardAggregate? cursedPearlCurseAgg,
        IReadOnlyDictionary<string, CardAggregate>? neowsBonesCurseAggs,
        CardAggregate? storybookBrightestFlameAgg,
        RunMetaStats? phylacteryOstyStats,
        RelicLiveActivationCounts? liveActivationCounts,
        EternalFeatherLiveHeal? liveEternalFeatherHeal,
        out string title,
        out string body)
    {
        title = "";
        body = "";
        agg ??= new RelicAggregate();

        if (relicModel is BagOfPreparation)
        {
            title = "Bag of Preparation";
            body = BuildBagOfPreparationBodyBBCode(agg);
            return true;
        }

        if (relicModel is RingOfTheSnake)
        {
            title = "Ring of the Snake";
            body = BuildRingOfTheSnakeBodyBBCode(agg);
            return true;
        }

        if (relicModel is BagOfMarbles)
        {
            title = "Bag of Marbles";
            body = BuildBagOfMarblesBodyBBCode(agg);
            return true;
        }

        if (relicModel is RedMask)
        {
            title = "Red Mask";
            body = BuildRedMaskBodyBBCode(agg);
            return true;
        }

        if (relicModel is UnsettlingLamp)
        {
            title = "Unsettling Lamp";
            body = BuildUnsettlingLampBodyBBCode(agg);
            return true;
        }

        if (relicModel is Pocketwatch)
        {
            title = "Pocketwatch";
            body = BuildPocketwatchBodyBBCode(agg);
            return true;
        }

        if (relicModel is PollinousCore)
        {
            title = "Pollinous Core";
            body = BuildPollinousCoreBodyBBCode(agg);
            return true;
        }

        if (relicModel is JossPaper)
        {
            title = "Joss Paper";
            body = BuildJossPaperBodyBBCode(agg);
            return true;
        }

        if (relicModel is Orichalcum)
        {
            title = "Orichalcum";
            body = BuildOrichalcumBodyBBCode(agg);
            return true;
        }

        if (relicModel is Permafrost permafrost)
        {
            title = "Permafrost";
            body = BuildPermafrostBodyBBCode(agg, IsPermafrostActivatedThisCombat(permafrost));
            return true;
        }

        if (relicModel is Vambrace vambrace)
        {
            title = "Vambrace";
            body = BuildVambraceBodyBBCode(agg, IsVambraceUsedThisCombat(vambrace));
            return true;
        }

        if (relicModel is TuningFork)
        {
            title = "Tuning Fork";
            body = BuildTuningForkBodyBBCode(agg);
            return true;
        }

        if (relicModel is RippleBasin)
        {
            title = "Ripple Basin";
            body = BuildRippleBasinBodyBBCode(agg);
            return true;
        }

        if (relicModel is TheAbacus)
        {
            title = "The Abacus";
            body = BuildTheAbacusBodyBBCode(agg);
            return true;
        }

        if (IsAnchorStatsRelicModel(relicModel))
        {
            title = IsFakeAnchorRelicModel(relicModel) ? "Anchor???" : "Anchor";
            body = BuildAnchorBodyBBCode(agg);
            return true;
        }

        if (relicModel is FakeVenerableTeaSet)
        {
            title = "Venerable Tea Set???";
            body = BuildVenerableTeaSetBodyBBCode(agg);
            return true;
        }

        if (relicModel is VenerableTeaSet)
        {
            title = "Venerable Tea Set";
            body = BuildVenerableTeaSetBodyBBCode(agg);
            return true;
        }

        if (IsRelicModel(relicModel, "MegaCrit.Sts2.Core.Models.Relics.LetterOpener"))
        {
            title = "Letter Opener";
            body = BuildLetterOpenerBodyBBCode(agg);
            return true;
        }

        if (IsRelicModel(relicModel, "MegaCrit.Sts2.Core.Models.Relics.Akabeko"))
        {
            title = "Akabeko";
            body = BuildAkabekoBodyBBCode(agg);
            return true;
        }

        if (relicModel is BookRepairKnife)
        {
            title = "Book Repair Knife";
            body = BuildBookRepairKnifeBodyBBCode(agg);
            return true;
        }

        if (relicModel is EternalFeather)
        {
            title = "Eternal Feather";
            body = BuildEternalFeatherBodyBBCode(agg, liveEternalFeatherHeal);
            return true;
        }

        if (relicModel is BoneFlute)
        {
            title = "Bone Flute";
            body = BuildBoneFluteBodyBBCode(agg);
            return true;
        }

        if (relicModel is ArtOfWar)
        {
            title = "Art of War";
            body = BuildArtOfWarBodyBBCode(agg);
            return true;
        }

        if (relicModel is IceCream)
        {
            title = "Ice Cream";
            body = BuildIceCreamBodyBBCode(agg);
            return true;
        }

        if (relicModel is CrackedCore or InfusedCore)
        {
            title = relicModel is InfusedCore ? "Infused Core" : "Cracked Core";
            body = BuildStartingLightningCoreBodyBBCode(agg);
            return true;
        }

        if (relicModel is SymbioticVirus)
        {
            title = "Symbiotic Virus";
            body = BuildSymbioticVirusBodyBBCode(agg);
            return true;
        }

        if (relicModel is BingBong)
        {
            title = "Bing Bong";
            body = BuildBingBongBodyBBCode(agg);
            return true;
        }

        if (relicModel is GoldPlatedCables)
        {
            title = "Gold-Plated Cables";
            body = BuildGoldPlatedCablesBodyBBCode(agg);
            return true;
        }

        if (relicModel is HappyFlower or FakeHappyFlower)
        {
            title = relicModel is FakeHappyFlower
                ? "Happy Flower???"
                : "Happy Flower";
            body = BuildHappyFlowerBodyBBCode(agg);
            return true;
        }

        if (relicModel is Nunchaku)
        {
            title = "Nunchaku";
            body = BuildNunchakuBodyBBCode(agg);
            return true;
        }

        if (relicModel is IronClub)
        {
            title = "Iron Club";
            body = BuildIronClubBodyBBCode(agg);
            return true;
        }

        if (relicModel is Vajra)
        {
            title = "Vajra";
            body = BuildVajraBodyBBCode(agg);
            return true;
        }

        if (relicModel is OddlySmoothStone)
        {
            title = "Oddly Smooth Stone";
            body = BuildOddlySmoothStoneBodyBBCode(agg);
            return true;
        }

        if (relicModel is EmberTea)
        {
            title = "Ember Tea";
            body = BuildEmberTeaBodyBBCode(agg);
            return true;
        }

        if (relicModel is RedSkull)
        {
            title = "Red Skull";
            body = BuildRedSkullBodyBBCode(agg);
            return true;
        }

        if (relicModel is ToastyMittens)
        {
            title = "Toasty Mittens";
            body = BuildToastyMittensBodyBBCode(agg);
            return true;
        }

        if (relicModel is Kunai)
        {
            title = "Kunai";
            body = BuildKunaiBodyBBCodeWithLiveActivations(agg, liveActivationCounts);
            return true;
        }

        if (relicModel is Kusarigama)
        {
            title = "Kusarigama";
            body = BuildKusarigamaBodyBBCode(agg);
            return true;
        }

        if (relicModel is OrnamentalFan)
        {
            title = "Ornamental Fan";
            body = BuildOrnamentalFanBodyBBCodeWithLiveActivations(agg, liveActivationCounts);
            return true;
        }

        if (relicModel is Shuriken)
        {
            title = "Shuriken";
            body = BuildShurikenBodyBBCodeWithLiveActivations(agg, liveActivationCounts);
            return true;
        }

        if (relicModel is RuinedHelmet)
        {
            title = "Ruined Helmet";
            body = BuildRuinedHelmetBodyBBCode(agg);
            return true;
        }

        if (relicModel is Girya)
        {
            title = "Girya";
            body = BuildGiryaBodyBBCode(agg);
            return true;
        }

        if (relicModel is SwordOfStone or SwordOfJade)
        {
            var isSwordOfJade = relicModel is SwordOfJade;
            title = isSwordOfJade ? "Sword of Jade" : "Sword in the Stone";
            body = isSwordOfJade
                ? BuildSwordOfJadeBodyBBCode(agg)
                : BuildSwordInTheStoneBodyBBCode(
                    agg,
                    RelicFloorAddedToDeck(relicModel));
            return true;
        }

        if (relicModel is PaperPhrog)
        {
            title = "Paper Phrog";
            body = BuildPaperPhrogBodyBBCode(agg);
            return true;
        }

        if (relicModel is Lantern)
        {
            title = "Lantern";
            body = BuildLanternBodyBBCode(agg);
            return true;
        }

        if (relicModel is VeryHotCocoa)
        {
            title = "Very Hot Cocoa";
            body = BuildVeryHotCocoaBodyBBCode(agg);
            return true;
        }

        if (relicModel is Candelabra)
        {
            title = "Candelabra";
            body = BuildCandelabraBodyBBCode(agg);
            return true;
        }

        if (relicModel is Chandelier)
        {
            title = "Chandelier";
            body = BuildChandelierBodyBBCode(agg);
            return true;
        }

        if (IsRelicModel(relicModel, "MegaCrit.Sts2.Core.Models.Relics.BoomingConch"))
        {
            title = "Booming Conch";
            body = BuildBoomingConchBodyBBCode(agg);
            return true;
        }

        if (relicModel is GremlinHorn)
        {
            title = "Gremlin Horn";
            body = BuildGremlinHornBodyBBCode(agg);
            return true;
        }

        if (relicModel is Pendulum)
        {
            title = "Pendulum";
            body = BuildPendulumBodyBBCode(agg);
            return true;
        }

        if (relicModel is MercuryHourglass)
        {
            title = "Mercury Hourglass";
            body = BuildMercuryHourglassBodyBBCode(agg);
            return true;
        }

        if (relicModel is MrStruggles)
        {
            title = "Mr. Struggles";
            body = BuildMrStrugglesBodyBBCode(agg);
            return true;
        }

        if (relicModel is LostWisp)
        {
            title = "Lost Wisp";
            body = BuildLostWispBodyBBCode(agg);
            return true;
        }

        if (relicModel is GamePiece)
        {
            title = "Game Piece";
            body = BuildGamePieceBodyBBCode(agg);
            return true;
        }

        if (relicModel is ForgottenSoul)
        {
            title = "Forgotten Soul";
            body = BuildForgottenSoulBodyBBCode(agg);
            return true;
        }

        if (relicModel is ParryingShield)
        {
            title = "Parrying Shield";
            body = BuildParryingShieldBodyBBCode(agg);
            return true;
        }

        if (relicModel is FestivePopper)
        {
            title = "Festive Popper";
            body = BuildFestivePopperBodyBBCode(agg);
            return true;
        }

        if (relicModel is ScreamingFlagon)
        {
            title = "Screaming Flagon";
            body = BuildScreamingFlagonBodyBBCode(agg);
            return true;
        }

        if (relicModel is BronzeScales)
        {
            title = "Bronze Scales";
            body = BuildBronzeScalesBodyBBCode(agg);
            return true;
        }

        if (relicModel is PenNib)
        {
            title = "Pen Nib";
            body = BuildPenNibBodyBBCode(agg);
            return true;
        }

        if (relicModel is HornCleat)
        {
            title = "Horn Cleat";
            body = BuildHornCleatBodyBBCode(agg);
            return true;
        }

        if (relicModel is CaptainsWheel)
        {
            title = "Captain's Wheel";
            body = BuildCaptainsWheelBodyBBCode(agg);
            return true;
        }

        if (relicModel is PrismaticGem)
        {
            title = "Prismatic Gem";
            body = BuildPrismaticGemBodyBBCode(agg);
            return true;
        }

        if (relicModel is DingyRug)
        {
            title = "Dingy Rug";
            body = BuildDingyRugBodyBBCode(agg);
            return true;
        }

        if (relicModel is PumpkinCandle)
        {
            title = "Pumpkin Candle";
            body = BuildPumpkinCandleBodyBBCode(agg);
            return true;
        }

        if (relicModel is SpikedGauntlets)
        {
            title = "Spiked Gauntlets";
            body = BuildSpikedGauntletsBodyBBCode(agg);
            return true;
        }

        if (relicModel is SealOfGold)
        {
            title = "Seal of Gold";
            body = BuildSealOfGoldBodyBBCode(agg);
            return true;
        }

        if (relicModel is FresnelLens)
        {
            title = "Fresnel Lens";
            body = BuildFresnelLensBodyBBCode(agg);
            return true;
        }

        if (relicModel is WingCharm)
        {
            title = "Wing Charm";
            body = BuildWingCharmBodyBBCode(agg);
            return true;
        }

        if (relicModel is LastingCandy)
        {
            title = "Lasting Candy";
            body = BuildLastingCandyBodyBBCode(agg);
            return true;
        }

        if (relicModel is FishingRod)
        {
            title = "Fishing Rod";
            body = BuildFishingRodBodyBBCode(agg);
            return true;
        }

        if (relicModel is MoltenEgg)
        {
            title = "Molten Egg";
            body = BuildEggBodyBBCode(agg, "attack");
            return true;
        }

        if (relicModel is ToxicEgg)
        {
            title = "Toxic Egg";
            body = BuildEggBodyBBCode(agg, "skill");
            return true;
        }

        if (relicModel is FrozenEgg)
        {
            title = "Frozen Egg";
            body = BuildEggBodyBBCode(agg, "power");
            return true;
        }

        if (relicModel is LeadPaperweight)
        {
            title = "Lead Paperweight";
            body = BuildLeadPaperweightBodyBBCode(
                agg,
                RelicFloorAddedToDeck(relicModel));
            return true;
        }

        if (relicModel is SilverCrucible)
        {
            title = "Silver Crucible";
            body = BuildSilverCrucibleBodyBBCode(agg);
            return true;
        }

        if (relicModel is Orrery)
        {
            title = "Orrery";
            body = BuildOrreryBodyBBCode(agg);
            return true;
        }

        if (relicModel is BloodSoakedRose)
        {
            title = "Blood-Soaked Rose";
            body = BuildBloodSoakedRoseBodyBBCode(agg, bloodSoakedRoseCurseAgg ?? new CardAggregate());
            return true;
        }

        if (relicModel is Storybook)
        {
            title = "Storybook";
            body = BuildStorybookBodyBBCode(storybookBrightestFlameAgg ?? new CardAggregate());
            return true;
        }

        if (relicModel is Regalite)
        {
            title = "Regalite";
            body = BuildRegaliteBodyBBCode(agg);
            return true;
        }

        if (relicModel is Crossbow)
        {
            title = "Crossbow";
            body = BuildCrossbowBodyBBCode(agg);
            return true;
        }

        if (relicModel is MusicBox)
        {
            title = "Musical Box";
            body = BuildMusicBoxBodyBBCode(agg);
            return true;
        }

        if (relicModel is IntimidatingHelmet)
        {
            title = "Intimidating Helmet";
            body = BuildIntimidatingHelmetBodyBBCode(agg);
            return true;
        }

        if (relicModel is DaughterOfTheWind)
        {
            title = "Daughter of the Wind";
            body = BuildDaughterOfTheWindBodyBBCode(agg);
            return true;
        }

        if (relicModel is SturdyClamp)
        {
            title = "Sturdy Clamp";
            body = BuildSturdyClampBodyBBCode(agg);
            return true;
        }

        if (relicModel is CursedPearl)
        {
            title = "Cursed Pearl";
            body = BuildCursedPearlBodyBBCode(agg, cursedPearlCurseAgg ?? new CardAggregate());
            return true;
        }

        if (relicModel is NeowsBones)
        {
            title = "Neow's Bones";
            body = BuildNeowsBonesBodyBBCode(agg, neowsBonesCurseAggs);
            return true;
        }

        if (relicModel is CloakClasp)
        {
            title = "Cloak Clasp";
            body = BuildCloakClaspBodyBBCode(agg);
            return true;
        }

        if (relicModel is ReptileTrinket)
        {
            title = "Reptile Trinket";
            body = BuildReptileTrinketBodyBBCode(agg);
            return true;
        }

        if (relicModel is RainbowRing rainbowRing)
        {
            title = "Rainbow Ring";
            var turnState = GetRainbowRingTurnState(rainbowRing);
            body = BuildRainbowRingBodyBBCode(
                agg,
                turnState.AttackPlayed,
                turnState.PowerPlayed,
                turnState.SkillPlayed);
            return true;
        }

        if (relicModel is SparklingRouge)
        {
            title = "Sparkling Rouge";
            body = BuildSparklingRougeBodyBBCode(agg);
            return true;
        }

        if (relicModel is BeatingRemnant)
        {
            title = "Beating Remnant";
            body = BuildBeatingRemnantBodyBBCode(agg);
            return true;
        }

        if (relicModel is WhisperingEarring)
        {
            title = "Whispering Earring";
            body = BuildWhisperingEarringBodyBBCode(agg);
            return true;
        }

        if (relicModel is TungstenRod)
        {
            title = "Tungsten Rod";
            body = BuildTungstenRodBodyBBCode(agg);
            return true;
        }

        if (relicModel is Gorget)
        {
            title = "Gorget";
            body = BuildGorgetBodyBBCode(agg);
            return true;
        }

        if (relicModel is StoneCracker)
        {
            title = "Stone Cracker";
            body = BuildStoneCrackerBodyBBCode(agg);
            return true;
        }

        if (relicModel is RazorTooth)
        {
            title = "Razor Tooth";
            body = BuildRazorToothBodyBBCode(agg);
            return true;
        }

        if (relicModel is WarHammer)
        {
            title = "War Hammer";
            body = BuildWarHammerBodyBBCode(agg);
            return true;
        }

        if (relicModel is GnarledHammer)
        {
            title = "Gnarled Hammer";
            body = BuildGnarledHammerBodyBBCode(agg);
            return true;
        }

        if (relicModel is SilkenTress)
        {
            title = "Silken Tress";
            body = BuildSilkenTressBodyBBCode(agg);
            return true;
        }

        if (relicModel is TriBoomerang)
        {
            title = "Tri-Boomerang";
            body = BuildTriBoomerangBodyBBCode(agg);
            return true;
        }

        if (relicModel is Whetstone)
        {
            title = "Whetstone";
            body = BuildWhetstoneBodyBBCode(agg);
            return true;
        }

        if (relicModel is YummyCookie)
        {
            title = "Yummy Cookie";
            body = BuildYummyCookieBodyBBCode(agg);
            return true;
        }

        if (relicModel is WarPaint)
        {
            title = "War Paint";
            body = BuildWarPaintBodyBBCode(agg);
            return true;
        }

        if (relicModel is FragrantMushroom)
        {
            title = "Fragrant Mushroom";
            body = BuildFragrantMushroomBodyBBCode(agg);
            return true;
        }

        if (relicModel is SandCastle)
        {
            title = "Sand Castle";
            body = BuildSandCastleBodyBBCode(agg);
            return true;
        }

        if (relicModel is MealTicket)
        {
            title = "Meal Ticket";
            body = BuildMealTicketBodyBBCode(agg);
            return true;
        }

        if (relicModel is MeatOnTheBone)
        {
            title = "Meat on the Bone";
            body = BuildMeatOnTheBoneBodyBBCode(agg);
            return true;
        }

        if (relicModel is Planisphere)
        {
            title = "Planisphere";
            body = BuildPlanisphereBodyBBCode(agg);
            return true;
        }

        if (relicModel is LizardTail)
        {
            title = "Lizard Tail";
            body = BuildLizardTailBodyBBCode(agg, RelicFloorAddedToDeck(relicModel));
            return true;
        }

        if (relicModel is Pantograph)
        {
            title = "Pantograph";
            body = BuildPantographBodyBBCode(agg);
            return true;
        }

        if (relicModel is BurningBlood)
        {
            title = "Burning Blood";
            body = BuildBurningBloodBodyBBCode(agg);
            return true;
        }

        if (relicModel is LeesWaffle)
        {
            title = "Lee's Waffle";
            body = BuildLeesWaffleBodyBBCode(agg);
            return true;
        }

        if (relicModel is FakeLeesWaffle)
        {
            title = "Lee's Waffle???";
            body = BuildFakeLeesWaffleBodyBBCode(agg);
            return true;
        }

        if (relicModel is Strawberry)
        {
            title = "Strawberry";
            body = BuildStrawberryBodyBBCode(agg);
            return true;
        }

        if (relicModel is Pear)
        {
            title = "Pear";
            body = BuildPearBodyBBCode(agg);
            return true;
        }

        if (relicModel is NutritiousOyster)
        {
            title = "Nutritious Oyster";
            body = BuildNutritiousOysterBodyBBCode(agg);
            return true;
        }

        if (relicModel is Mango)
        {
            title = "Mango";
            body = BuildMangoBodyBBCode(agg);
            return true;
        }

        if (relicModel is FakeMango)
        {
            title = "Mango???";
            body = BuildMangoBodyBBCode(agg);
            return true;
        }

        if (relicModel is StoneHumidifier)
        {
            title = "Stone Humidifier";
            body = BuildStoneHumidifierBodyBBCode(agg);
            return true;
        }

        if (relicModel is ChosenCheese)
        {
            title = "Chosen Cheese";
            body = BuildChosenCheeseBodyBBCode(agg);
            return true;
        }

        if (relicModel is DarkstonePeriapt)
        {
            title = "Darkstone Periapt";
            body = BuildDarkstonePeriaptBodyBBCode(agg);
            return true;
        }

        if (relicModel is LuckyFysh)
        {
            title = "Lucky Fysh";
            body = BuildLuckyFyshBodyBBCode(
                agg,
                floorCount,
                RelicFloorAddedToDeck(relicModel));
            return true;
        }

        if (relicModel is BowlerHat)
        {
            title = "Bowler Hat";
            body = BuildBowlerHatBodyBBCode(
                agg,
                floorCount,
                RelicFloorAddedToDeck(relicModel));
            return true;
        }

        if (relicModel is AmethystAubergine)
        {
            title = "Amethyst Aubergine";
            body = BuildAmethystAubergineBodyBBCode(agg);
            return true;
        }

        if (relicModel is WongosMysteryTicket)
        {
            title = "Wongo's Mystery Ticket";
            body = BuildWongosMysteryTicketBodyBBCode(agg);
            return true;
        }

        if (relicModel is MawBank)
        {
            title = "Maw Bank";
            body = BuildMawBankBodyBBCode(agg);
            return true;
        }

        if (relicModel is OldCoin)
        {
            title = "Old Coin";
            body = BuildOldCoinBodyBBCode(agg);
            return true;
        }

        if (relicModel is MembershipCard)
        {
            title = "Membership Card";
            body = BuildMembershipCardBodyBBCode(agg);
            return true;
        }

        if (relicModel is GoldenPearl)
        {
            title = "Golden Pearl";
            body = BuildGoldenPearlBodyBBCode(agg);
            return true;
        }

        if (relicModel is BookOfFiveRings)
        {
            title = "Book of Five Rings";
            body = BuildBookOfFiveRingsBodyBBCode(
                agg,
                floorCount,
                RelicFloorAddedToDeck(relicModel));
            return true;
        }

        if (relicModel is SignetRing)
        {
            title = "Signet Ring";
            body = BuildSignetRingBodyBBCode(agg);
            return true;
        }

        if (relicModel is WingedBoots wingedBoots)
        {
            title = "Winged Boots";
            body = BuildWingedBootsBodyBBCode(agg, wingedBoots.TimesUsed);
            return true;
        }

        if (relicModel is LeafyPoultice)
        {
            title = "Leafy Poultice";
            body = BuildLeafyPoulticeBodyBBCode(agg);
            return true;
        }

        if (relicModel is RegalPillow)
        {
            title = "Regal Pillow";
            body = BuildRegalPillowBodyBBCode(agg);
            return true;
        }

        if (relicModel is PrecariousShears)
        {
            title = "Precarious Shears";
            body = BuildPrecariousShearsBodyBBCode(agg);
            return true;
        }

        if (IsRelicModel(relicModel, "MegaCrit.Sts2.Core.Models.Relics.BloodVial"))
        {
            title = "Blood Vial";
            body = BuildBloodVialBodyBBCode(agg);
            return true;
        }

        if (relicModel is FakeBloodVial)
        {
            title = "Blood Vial???";
            body = BuildBloodVialBodyBBCode(agg);
            return true;
        }

        if (relicModel is ToyBox)
        {
            title = "Toy Box";
            body = BuildToyBoxBodyBBCode(agg);
            return true;
        }

        if (IsRelicModel(relicModel, "MegaCrit.Sts2.Core.Models.Relics.Toolbox"))
        {
            title = "Toolbox";
            body = BuildToolboxBodyBBCode(agg);
            return true;
        }

        if (relicModel is ChoicesParadox)
        {
            title = "Choices Paradox";
            body = BuildChoicesParadoxBodyBBCode(agg);
            return true;
        }

        if (relicModel is WhiteStar)
        {
            title = "White Star";
            body = BuildWhiteStarBodyBBCode(agg);
            return true;
        }

        if (relicModel is PrayerWheel)
        {
            title = "Prayer Wheel";
            body = BuildPrayerWheelBodyBBCode(agg);
            return true;
        }

        if (relicModel is HeftyTablet)
        {
            title = "Hefty Tablet";
            body = BuildHeftyTabletBodyBBCode(agg);
            return true;
        }

        if (relicModel is ArcaneScroll)
        {
            title = "Arcane Scroll";
            body = BuildArcaneScrollBodyBBCode(agg);
            return true;
        }

        if (relicModel is ScrollBoxes)
        {
            title = "Scroll Boxes";
            body = BuildScrollBoxesBodyBBCode(agg);
            return true;
        }

        if (relicModel is SmallCapsule)
        {
            title = "Small Capsule";
            body = BuildSmallCapsuleBodyBBCode(agg);
            return true;
        }

        if (relicModel is LargeCapsule)
        {
            title = "Large Capsule";
            body = BuildLargeCapsuleBodyBBCode(agg);
            return true;
        }

        if (relicModel is PaelsTooth)
        {
            title = "Pael's Tooth";
            body = BuildPaelsToothBodyBBCode(agg);
            return true;
        }

        if (relicModel is PaelsClaw)
        {
            title = "Pael's Claw";
            body = BuildPaelsClawBodyBBCode(agg);
            return true;
        }

        if (relicModel is PaelsWing)
        {
            title = "Pael's Wing";
            body = BuildPaelsWingBodyBBCode(agg);
            return true;
        }

        if (relicModel is PaelsEye paelsEye)
        {
            title = "Pael's Eye";
            body = BuildPaelsEyeBodyBBCode(agg, paelsEye.UsedThisCombat);
            return true;
        }

        if (IsStrikeDummyStatsRelicModel(relicModel))
        {
            title = IsFakeStrikeDummyRelicModel(relicModel) ? "???" : "Strike Dummy";
            body = BuildStrikeDummyBodyBBCode(agg);
            return true;
        }

        if (relicModel is NutritiousSoup)
        {
            title = "Nutritious Soup";
            body = BuildNutritiousSoupBodyBBCode(agg);
            return true;
        }

        if (IsMiniatureCannonStatsRelicModel(relicModel))
        {
            title = "Miniature Cannon";
            body = BuildMiniatureCannonBodyBBCode(agg);
            return true;
        }

        if (relicModel is Bookmark)
        {
            title = "Bookmark";
            body = BuildBookmarkBodyBBCode(agg);
            return true;
        }

        if (relicModel is BrilliantScarf)
        {
            title = "Brilliant Scarf";
            body = BuildBrilliantScarfBodyBBCode(agg);
            return true;
        }

        if (relicModel is MummifiedHand)
        {
            title = "Mummified Hand";
            body = BuildMummifiedHandBodyBBCode(agg);
            return true;
        }

        if (relicModel is BurningSticks)
        {
            title = "Burning Sticks";
            body = BuildBurningSticksBodyBBCode(agg);
            return true;
        }

        if (relicModel is ThrowingAxe)
        {
            title = "Throwing Axe";
            body = BuildThrowingAxeBodyBBCode(agg);
            return true;
        }

        if (relicModel is JuzuBracelet)
        {
            title = "Juzu Bracelet";
            body = BuildJuzuBraceletBodyBBCode(agg);
            return true;
        }

        if (IsRelicModel(
                relicModel,
                "MegaCrit.Sts2.Core.Models.Relics.DowsingRod"))
        {
            title = "Dowsing Rod";
            body = BuildDowsingRodBodyBBCode(agg);
            return true;
        }

        if (relicModel is GamblingChip)
        {
            title = "Gambling Chip";
            body = BuildGamblingChipBodyBBCode(agg);
            return true;
        }

        if (relicModel is CentennialPuzzle centennialPuzzle)
        {
            title = "Centennial Puzzle";
            body = BuildCentennialPuzzleBodyBBCode(agg, centennialPuzzle.UsedThisCombat);
            return true;
        }

        if (relicModel is WhiteBeastStatue)
        {
            title = "White Beast Statue";
            body = BuildWhiteBeastStatueBodyBBCode(agg);
            return true;
        }

        if (relicModel is PotionBelt)
        {
            title = "Potion Belt";
            body = BuildPotionSlotRelicBodyBBCode(agg);
            return true;
        }

        if (relicModel is AlchemicalCoffer)
        {
            title = "Alchemical Coffer";
            body = BuildPotionSlotRelicBodyBBCode(agg);
            return true;
        }

        if (relicModel is PhialHolster)
        {
            title = "Phial Holster";
            body = BuildPotionSlotRelicBodyBBCode(agg);
            return true;
        }

        if (relicModel is PetrifiedToad)
        {
            title = "Petrified Toad";
            body = BuildPetrifiedToadBodyBBCode(agg);
            return true;
        }

        if (relicModel is Shovel)
        {
            title = "Shovel";
            body = BuildShovelBodyBBCode(agg);
            return true;
        }

        if (relicModel is TinyMailbox)
        {
            title = "Tiny Mailbox";
            body = BuildTinyMailboxBodyBBCode(agg);
            return true;
        }

        if (relicModel is BoundPhylactery)
        {
            title = "Bound Phylactery";
            body = BuildPhylacteryBodyBBCode(phylacteryOstyStats ?? new RunMetaStats());
            return true;
        }

        if (relicModel is PhylacteryUnbound)
        {
            title = "Phylactery Unbound";
            body = BuildPhylacteryBodyBBCode(phylacteryOstyStats ?? new RunMetaStats());
            return true;
        }

        return false;
    }

    private static string BuildBagOfPreparationBodyBBCode(RelicAggregate agg)
        => BuildOpeningHandDrawRelicBodyBBCode(agg, "Bag of Preparation");

    private static string BuildRingOfTheSnakeBodyBBCode(RelicAggregate agg)
        => BuildOpeningHandDrawRelicBodyBBCode(agg, "Ring of the Snake");

    private static string BuildOpeningHandDrawRelicBodyBBCode(
        RelicAggregate agg,
        string relicName)
    {
        var sb = new StringBuilder();
        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            $"Activations — first-turn hand draws whose requested card count {relicName} increased.");
        Row3(
            sb,
            "Cards drawn",
            agg.AdditionalCardsDrawn.ToString(),
            "",
            $"Cards drawn — first-turn cards that were actually drawn because of {relicName}'s added hand-draw count.");
        Row3(
            sb,
            "Card draws blocked",
            agg.AdditionalCardDrawsBlocked.ToString(),
            "",
            $"Card draws blocked — first-turn draws still added by {relicName} after hand-size resolution, but prevented before a card reached the hand.");
        return sb.ToString();
    }

    private static string BuildBagOfMarblesBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(sb, VulnerableLabel("enemies affected"), agg.EnemiesAffected.ToString(), "");
        return sb.ToString();
    }

    private static string BuildRedMaskBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(sb, WeakLabel("enemies affected"), agg.EnemiesAffected.ToString(), "");
        Row3(sb, WeakLabel("weak applied"), agg.WeakApplied.ToString(), "");
        return sb.ToString();
    }

    private static string BuildUnsettlingLampBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var combats = agg.Activations;
        var vulnerablePerCombat = combats <= 0
            ? 0m
            : (decimal)agg.VulnerableApplied / combats;
        var weakPerCombat = combats <= 0
            ? 0m
            : (decimal)agg.WeakApplied / combats;

        Row3(sb, "Combats held", combats.ToString(), "");
        Row3(sb, VulnerableLabel("vulnerable applied"), agg.VulnerableApplied.ToString(), "");
        Row3(sb, VulnerableLabel("avg vulnerable/combat"), FormatDecimal(vulnerablePerCombat), "");
        Row3(sb, WeakLabel("weak applied"), agg.WeakApplied.ToString(), "");
        Row3(sb, WeakLabel("avg weak/combat"), FormatDecimal(weakPerCombat), "");

        foreach (var effect in OtherUnsettlingLampDebuffs(agg))
        {
            var average = combats <= 0
                ? 0m
                : effect.TotalAmountApplied / combats;
            Row3(sb, RelicEffectLabel(effect, "applied"), FormatDecimal(effect.TotalAmountApplied), "");
            Row3(sb, RelicEffectLabel(effect, "avg/combat"), FormatDecimal(average), "");
        }

        return sb.ToString();
    }

    private static string BuildPocketwatchBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var averageCountAtTurnEnd = agg.PocketwatchTurns <= 0
            ? 0m
            : (decimal)agg.PocketwatchTurnEndCountTotal / agg.PocketwatchTurns;
        var averageValueWhenActivated = agg.PocketwatchActivationValueSamples <= 0
            ? 0m
            : (decimal)agg.PocketwatchActivatedTurnEndCountTotal
                / agg.PocketwatchActivationValueSamples;
        var averageValueWhenMissed = agg.PocketwatchTurnsActivationMissed <= 0
            ? 0m
            : (decimal)agg.PocketwatchMissedTurnEndCountTotal
                / agg.PocketwatchTurnsActivationMissed;
        var averageActivationsPerTurn = agg.PocketwatchTurns <= 0
            ? 0m
            : (decimal)agg.Activations / agg.PocketwatchTurns;
        var averageActivationsPerCombat = agg.PocketwatchCombats <= 0
            ? 0m
            : (decimal)agg.Activations / agg.PocketwatchCombats;

        Row3(
            sb,
            "Additional cards drawn",
            agg.AdditionalCardsDrawn.ToString(),
            "",
            "Additional cards drawn — cards actually added to hand draws by Pocketwatch.");
        Row3(
            sb,
            "Avg count at turn end",
            FormatDecimal(averageCountAtTurnEnd),
            "",
            "Average count at turn end — cards played by the relic's owner at the end of each turn while Pocketwatch was held.");
        Row3(
            sb,
            "Turns activation missed",
            agg.PocketwatchTurnsActivationMissed.ToString(),
            "",
            "Turns activation missed — turns that ended above Pocketwatch's card threshold and therefore could not activate it.");
        Row3(
            sb,
            "Avg value when activated",
            FormatDecimal(averageValueWhenActivated),
            "",
            "Average value when activated — the prior turn's card count when Pocketwatch actually added cards to the next hand draw.");
        Row3(
            sb,
            "Avg value when missed",
            FormatDecimal(averageValueWhenMissed),
            "",
            "Average value when missed — the card count on turns that ended above Pocketwatch's activation threshold.");
        Row3(
            sb,
            "Avg activations per turn",
            FormatDecimal(averageActivationsPerTurn),
            "",
            "Average activations per turn — actual Pocketwatch activations divided by turns completed while it was held.");
        Row3(
            sb,
            "Avg activations per combat",
            FormatDecimal(averageActivationsPerCombat),
            "",
            "Average activations per combat — actual Pocketwatch activations divided by combats in which it was held.");
        return sb.ToString();
    }

    private static string BuildPollinousCoreBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var averageActivationsPerCombat = agg.PollinousCoreCombats <= 0
            ? 0m
            : (decimal)agg.Activations / agg.PollinousCoreCombats;
        var averageTurnsPerCombat = agg.PollinousCoreCombats <= 0
            ? 0m
            : (decimal)agg.PollinousCoreTurns / agg.PollinousCoreCombats;
        var averageCardsDrawnPerCombat = agg.PollinousCoreCombats <= 0
            ? 0m
            : (decimal)agg.AdditionalCardsDrawn / agg.PollinousCoreCombats;

        Row3(
            sb,
            "Activations",
            agg.Activations.ToString(),
            "",
            "Times Pollinous Core reached four counters and added cards to the upcoming hand draw.");
        Row3(
            sb,
            "Turns ended on 0 counters",
            agg.PollinousCoreTurnsEndedOn0Counters.ToString(),
            "",
            "Player turns that ended after Pollinous Core activated and reset its counter to zero.");
        Row3(
            sb,
            "Turns ended on 1 counter",
            agg.PollinousCoreTurnsEndedOn1Counter.ToString(),
            "",
            "Player turns that ended with Pollinous Core showing one counter.");
        Row3(
            sb,
            "Turns ended on 2 counters",
            agg.PollinousCoreTurnsEndedOn2Counters.ToString(),
            "",
            "Player turns that ended with Pollinous Core showing two counters.");
        Row3(
            sb,
            "Turns ended on 3 counters",
            agg.PollinousCoreTurnsEndedOn3Counters.ToString(),
            "",
            "Player turns that ended with Pollinous Core showing three counters.");
        Row3(
            sb,
            "Avg activations/combat",
            FormatDecimal(averageActivationsPerCombat),
            "",
            "Average activations per combat — activations divided by combats where Pollinous Core was held.");
        Row3(
            sb,
            "Avg turns/combat",
            FormatDecimal(averageTurnsPerCombat),
            "",
            "Average turns per combat — completed player turns divided by combats where Pollinous Core was held.");
        Row3(
            sb,
            "Cards drawn",
            agg.AdditionalCardsDrawn.ToString(),
            "",
            "Cards drawn — Pollinous Core cards that actually reached the hand.");
        Row3(
            sb,
            "Card draws blocked",
            agg.AdditionalCardDrawsBlocked.ToString(),
            "",
            "Card draws blocked — Pollinous Core cards requested but prevented by draw limits or draw-prevention effects.");
        Row3(
            sb,
            "Avg cards drawn/combat",
            FormatDecimal(averageCardsDrawnPerCombat),
            "",
            "Average cards drawn per combat — observed Pollinous Core cards drawn divided by combats where it was held.");
        return sb.ToString();
    }

    private static string BuildJossPaperBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var combats = agg.JossPaperCombats;
        var averageActivationsPerCombat = combats <= 0
            ? 0m
            : (decimal)agg.Activations / combats;
        var averageExhaustsPerCombat = combats <= 0
            ? 0m
            : (decimal)agg.JossPaperCardsExhausted / combats;
        var averageTurnsPerCombat = combats <= 0
            ? 0m
            : (decimal)agg.JossPaperTurns / combats;
        var averageCardsDrawnPerCombat = combats <= 0
            ? 0m
            : (decimal)agg.AdditionalCardsDrawn / combats;

        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            "Activations — five-card exhaust thresholds reached by Joss Paper.");
        ConceptRow(
            sb,
            "exhaust",
            agg.JossPaperCardsExhausted.ToString(),
            "Cards exhausted while Joss Paper was held.");
        Row3(
            sb,
            "Turns ended on 0 counters",
            agg.JossPaperTurnsEndedOn0Counters.ToString(),
            "",
            "Player turns that ended after Joss Paper activated and reset its counter to zero.");
        Row3(
            sb,
            "Turns ended on 1 counter",
            agg.JossPaperTurnsEndedOn1Counter.ToString(),
            "",
            "Player turns that ended with Joss Paper showing one counter.");
        Row3(
            sb,
            "Turns ended on 2 counters",
            agg.JossPaperTurnsEndedOn2Counters.ToString(),
            "",
            "Player turns that ended with Joss Paper showing two counters.");
        Row3(
            sb,
            "Turns ended on 3 counters",
            agg.JossPaperTurnsEndedOn3Counters.ToString(),
            "",
            "Player turns that ended with Joss Paper showing three counters.");
        Row3(
            sb,
            "Turns ended on 4 counters",
            agg.JossPaperTurnsEndedOn4Counters.ToString(),
            "",
            "Player turns that ended with Joss Paper showing four counters.");
        Row3(
            sb,
            "Avg activations/combat",
            FormatDecimal(averageActivationsPerCombat),
            "",
            "Average activations per combat — thresholds reached divided by combats where Joss Paper was held.");
        DescribedIconRow(
            sb,
            ["average", "exhaust", "combat"],
            ["combat"],
            string.Empty,
            FormatDecimal(averageExhaustsPerCombat),
            "Average cards exhausted per combat while Joss Paper was held.");
        Row3(
            sb,
            "Avg turns/combat",
            FormatDecimal(averageTurnsPerCombat),
            "",
            "Average turns per combat — completed player turns divided by combats where Joss Paper was held.");
        Row3(
            sb,
            "Cards drawn",
            agg.AdditionalCardsDrawn.ToString(),
            "",
            "Cards drawn — Joss Paper cards that actually reached the hand.");
        Row3(
            sb,
            "Card draws blocked",
            agg.AdditionalCardDrawsBlocked.ToString(),
            "",
            "Card draws blocked — Joss Paper draws prevented by draw limits, a full hand, or draw-prevention effects.");
        Row3(
            sb,
            "Avg cards drawn/combat",
            FormatDecimal(averageCardsDrawnPerCombat),
            "",
            "Average cards drawn per combat — observed Joss Paper draws divided by combats where it was held.");
        return sb.ToString();
    }

    private static string BuildOrichalcumBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(sb, BlockLabel("block gained"), agg.AdditionalBlockGained.ToString(), "");
        Row3(sb, "Triggers blocked", agg.BlockedTriggers.ToString(), "");
        Row3(
            sb,
            "Turns undercut",
            agg.OrichalcumTurnsUndercut.ToString(),
            "",
            "Turns undercut — player turns that ended holding less Block than Orichalcum would have granted, "
            + "so the leftover Block was worth less than the trigger it suppressed.");
        Row3(
            sb,
            BlockLabel("block missed"),
            agg.OrichalcumBlockMissed.ToString(),
            "",
            "Block missed — Block given up across those turns, each scored as the trigger amount minus the "
            + "leftover Block. This is a counterfactual: it assumes the leftover Block could have been spent "
            + "or avoided.");
        Row3(
            sb,
            BlockLabel("block missed per turn"),
            FormatDecimal(CalculateOrichalcumBlockMissedPerTurn(agg)),
            "",
            "Block missed per turn — missed Block across every player turn Orichalcum was held, including "
            + "turns that missed nothing.");
        Row3(
            sb,
            BlockLabel("block missed per combat"),
            FormatDecimal(CalculateOrichalcumBlockMissedPerCombat(agg)),
            "",
            "Block missed per combat — missed Block across every combat Orichalcum was held, including "
            + "combats that missed nothing.");
        return sb.ToString();
    }

    internal static decimal CalculateOrichalcumBlockMissedPerTurn(RelicAggregate agg)
    {
        if (agg == null) return 0m;
        // Undercut turns are themselves held turns, so a stale or missing turn
        // denominator can never make the average understate the total.
        var turns = Math.Max(agg.OrichalcumTurns, agg.OrichalcumTurnsUndercut);
        return turns <= 0 ? 0m : (decimal)agg.OrichalcumBlockMissed / turns;
    }

    internal static decimal CalculateOrichalcumBlockMissedPerCombat(RelicAggregate agg)
    {
        if (agg == null) return 0m;
        return agg.OrichalcumCombats <= 0
            ? 0m
            : (decimal)agg.OrichalcumBlockMissed / agg.OrichalcumCombats;
    }

    private static string BuildPermafrostBodyBBCode(RelicAggregate agg, bool triggeredThisCombat)
    {
        var sb = new StringBuilder();
        var blockPerCombat = agg.Activations <= 0
            ? 0m
            : (decimal)agg.AdditionalBlockGained / agg.Activations;
        var effectivePermafrostCombats = Math.Max(agg.PermafrostCombats, agg.Activations);
        var triggersPerCombat = effectivePermafrostCombats <= 0
            ? 0m
            : (decimal)agg.Activations / effectivePermafrostCombats;
        Row3(sb, "Triggered this combat", triggeredThisCombat ? "true" : "false", "");
        Row3(sb, "Combats triggered", agg.Activations.ToString(), "");
        Row3(sb, "Avg times triggered per combat", FormatDecimal(triggersPerCombat), "");
        Row3(sb, BlockLabel("block gained"), agg.AdditionalBlockGained.ToString(), "");
        Row3(sb, BlockLabel("block gained per combat"), FormatDecimal(blockPerCombat), "");
        return sb.ToString();
    }

    private static bool IsPermafrostActivatedThisCombat(Permafrost permafrost)
    {
        try
        {
            return PermafrostActivatedThisCombatField?.GetValue(permafrost) is true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildVambraceBodyBBCode(RelicAggregate agg, bool usedThisCombat = false)
    {
        var sb = new StringBuilder();
        var blockPerActivation = agg.Activations <= 0
            ? 0m
            : (decimal)agg.AdditionalBlockGained / agg.Activations;
        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, "Used this combat", usedThisCombat ? "true" : "false", "");
        Row3(sb, BlockLabel("extra block gained"), agg.AdditionalBlockGained.ToString(), "");
        Row3(sb, BlockLabel("extra block per activation"), FormatDecimal(blockPerActivation), "");
        return sb.ToString();
    }

    private static string BuildTuningForkBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var skillsPerCombat = agg.TuningForkCombats <= 0
            ? 0m
            : (decimal)agg.TuningForkSkillsPlayed / agg.TuningForkCombats;
        var skillsPerTurn = agg.TuningForkTurns <= 0
            ? 0m
            : (decimal)agg.TuningForkSkillsPlayed / agg.TuningForkTurns;
        var averageEndCharge = agg.TuningForkTurnEndChargeCount <= 0
            ? 0m
            : (decimal)agg.TuningForkTurnEndChargeTotal / agg.TuningForkTurnEndChargeCount;

        Row3(sb, "Skills played", agg.TuningForkSkillsPlayed.ToString(), "");
        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, BlockLabel("block gained"), agg.AdditionalBlockGained.ToString(), "");
        Row3(sb, "Avg skills played per combat", FormatDecimal(skillsPerCombat), "");
        Row3(sb, "Avg skills played per turn", FormatDecimal(skillsPerTurn), "");
        Row3(sb, "Turns ended on 8 charges", agg.TuningForkTurnsEndedOn8Charges.ToString(), "");
        Row3(sb, "Turns ended on 9 charges", agg.TuningForkTurnsEndedOn9Charges.ToString(), "");
        Row3(sb, "Avg charge at turn end", FormatDecimal(averageEndCharge), "");
        return sb.ToString();
    }

    private static string BuildRippleBasinBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var blockPerTurn = agg.RippleBasinTurns <= 0
            ? 0m
            : (decimal)agg.AdditionalBlockGained / agg.RippleBasinTurns;
        var blockPerCombat = agg.RippleBasinCombats <= 0
            ? 0m
            : (decimal)agg.AdditionalBlockGained / agg.RippleBasinCombats;

        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, BlockLabel("block gained"), agg.AdditionalBlockGained.ToString(), "");
        Row3(sb, BlockLabel("avg block gained per turn"), FormatDecimal(blockPerTurn), "");
        Row3(sb, BlockLabel("avg block gained per combat"), FormatDecimal(blockPerCombat), "");
        return sb.ToString();
    }

    private static string BuildTheAbacusBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(sb, BlockLabel("block gained"), agg.AdditionalBlockGained.ToString(), "");
        return sb.ToString();
    }

    private static string BuildAnchorBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, BlockLabel("block gained"), agg.AdditionalBlockGained.ToString(), "");
        return sb.ToString();
    }

    private static string BuildVenerableTeaSetBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, EnergyLabel("Energy gained"), agg.EnergyGenerated.ToString(), "");
        return sb.ToString();
    }

    private static string BuildLetterOpenerBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var rates = CalculateLetterOpenerRates(agg);

        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, "Damage attempted", agg.TotalDamageAttempted.ToString(), "");
        Row3(sb, "Targets hit", agg.TotalTargets.ToString(), "");
        Row3(sb, "Targets hit per activation", FormatDecimal(rates.TargetsPerActivation), "");
        Row3(sb, "Avg damage per combat", FormatDecimal(rates.DamagePerCombat), "");
        Row3(sb, "Avg damage per turn", FormatDecimal(rates.DamagePerTurn), "");
        Row3(sb, "Turns ended at 1 charge", agg.LetterOpenerTurnsEndedAt1Charge.ToString(), "");
        Row3(sb, "Turns ended at 2 charges", agg.LetterOpenerTurnsEndedAt2Charges.ToString(), "");
        Row3(sb, "Avg damage per skill played", FormatDecimal(rates.DamagePerSkillPlayed), "");
        return sb.ToString();
    }

    internal static LetterOpenerRates CalculateLetterOpenerRates(RelicAggregate agg)
    {
        agg ??= new RelicAggregate();
        return new LetterOpenerRates(
            DamagePerCombat: agg.LetterOpenerCombats <= 0
                ? 0m
                : (decimal)agg.TotalDamageAttempted / agg.LetterOpenerCombats,
            DamagePerTurn: agg.LetterOpenerTurns <= 0
                ? 0m
                : (decimal)agg.TotalDamageAttempted / agg.LetterOpenerTurns,
            TargetsPerActivation: agg.Activations <= 0
                ? 0m
                : (decimal)agg.TotalTargets / agg.Activations,
            DamagePerSkillPlayed: agg.LetterOpenerSkillsPlayed <= 0
                ? 0m
                : (decimal)agg.TotalDamageAttempted / agg.LetterOpenerSkillsPlayed);
    }

    private static string BuildAkabekoBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(sb, VigorLabel("vigor gained"), agg.VigorGained.ToString(), "");
        return sb.ToString();
    }

    private static string BuildBronzeScalesBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendRelicDamageStats(
            sb,
            agg,
            triggerDescription: "Times triggered — the number of times this relic has activated.",
            averageLabel: "Damage per trigger",
            averageDenominator: agg.Activations);
        return sb.ToString();
    }

    private static string BuildBookRepairKnifeBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(sb, "Doom kills", agg.DoomKills.ToString(), "");
        AppendHealingStats(sb, agg);
        return sb.ToString();
    }

    private static string BuildEternalFeatherBodyBBCode(
        RelicAggregate agg,
        EternalFeatherLiveHeal? liveHeal = null)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        AppendHealingStats(sb, agg, averageActivations: agg.Activations);

        var deckCardsPerRestSite = agg.EternalFeatherDeckCardsSamples <= 0
            ? 0m
            : (decimal)agg.EternalFeatherDeckCardsTotal / agg.EternalFeatherDeckCardsSamples;
        Row3(
            sb,
            "Avg cards in deck per rest site",
            FormatDecimal(deckCardsPerRestSite),
            "",
            "Average deck size observed when Eternal Feather triggered at a rest site; "
                + "the heal is one unit per whole group of those cards.");

        if (liveHeal.HasValue)
        {
            DescribedIconRow(
                sb,
                ["healing_gained"],
                [],
                "at current deck size",
                FormatDecimal(liveHeal.Value.Heal),
                $"HP this would heal at a rest site right now: {liveHeal.Value.DeckCards} "
                    + $"cards in deck, healing once per {liveHeal.Value.CardsPerHeal} of them.");
        }

        return sb.ToString();
    }

    private static string BuildBoneFluteBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(
            sb,
            agg.BoneFluteTriggers.ToString(),
            "Times triggered — the number of times this relic has activated.");
        Row3(sb, BlockLabel("block gained"), agg.AdditionalBlockGained.ToString(), "");
        return sb.ToString();
    }

    private static string BuildArtOfWarBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var energyPerTurn = agg.ArtOfWarTurns <= 0
            ? 0m
            : (decimal)agg.EnergyGenerated / agg.ArtOfWarTurns;
        var energyPerCombat = agg.EnergyGeneratedCombats <= 0
            ? 0m
            : (decimal)agg.EnergyGenerated / agg.EnergyGeneratedCombats;
        var energyPerTurnThisCombat = agg.ArtOfWarTurnsThisCombat <= 0
            ? 0m
            : (decimal)agg.ArtOfWarEnergyAddedThisCombat / agg.ArtOfWarTurnsThisCombat;

        Row3(sb, EnergyLabel("Total energy gained"), agg.EnergyGenerated.ToString(), "");
        Row3(sb, EnergyLabel("Avg energy gained per turn"), FormatDecimal(energyPerTurn), "");
        Row3(sb, EnergyLabel("Avg energy gained per combat"), FormatDecimal(energyPerCombat), "");
        Row3(
            sb,
            EnergyLabel("Energy added this combat"),
            agg.ArtOfWarEnergyAddedThisCombat.ToString(),
            "");
        Row3(
            sb,
            EnergyLabel("Energy added this turn"),
            agg.ArtOfWarEnergyAddedThisTurn.ToString(),
            "");
        Row3(
            sb,
            EnergyLabel("Avg energy added per turn this combat"),
            FormatDecimal(energyPerTurnThisCombat),
            "");
        return sb.ToString();
    }

    private static string BuildIceCreamBodyBBCode(RelicAggregate agg)
    {
        var conservedPerTurn = CalculateIceCreamEnergyConservedPerTurn(agg);
        var conservedPerCombat = CalculateIceCreamEnergyConservedPerCombat(agg);

        var sb = new StringBuilder();
        Row3(
            sb,
            EnergyLabel("Total energy conserved"),
            agg.IceCreamEnergyConserved.ToString(),
            "",
            "Total energy conserved — Energy carried into a turn that the start-of-turn reset "
            + "would otherwise have discarded, read at the game's own reset-or-carry decision.");
        Row3(
            sb,
            EnergyLabel("Avg energy conserved per turn"),
            FormatDecimal(conservedPerTurn),
            "",
            "Average energy conserved per turn — conserved Energy across every player turn Ice Cream "
            + "was held, including turns that carried nothing and turn 1, which always resets.");
        Row3(
            sb,
            EnergyLabel("Avg energy conserved per combat"),
            FormatDecimal(conservedPerCombat),
            "",
            "Average energy conserved per combat — conserved Energy across every combat Ice Cream "
            + "was held, including combats that carried nothing.");
        return sb.ToString();
    }

    internal static decimal CalculateIceCreamEnergyConservedPerTurn(RelicAggregate agg)
    {
        if (agg == null) return 0m;
        return agg.IceCreamTurns <= 0
            ? 0m
            : (decimal)agg.IceCreamEnergyConserved / agg.IceCreamTurns;
    }

    internal static decimal CalculateIceCreamEnergyConservedPerCombat(RelicAggregate agg)
    {
        if (agg == null) return 0m;
        return agg.IceCreamCombats <= 0
            ? 0m
            : (decimal)agg.IceCreamEnergyConserved / agg.IceCreamCombats;
    }

    private static string BuildStartingLightningCoreBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var lightningIcon = CardHoverShowPatch.RenderOrbInlineIcon(
            OrbCardRegistry.LightningOrbId);
        Row3(
            sb,
            $"{lightningIcon} evoked",
            agg.CrackedCoreOrbEvokes.ToString(),
            "",
            "Times the starting Lightning orb was evoked.");
        Row3(
            sb,
            $"{lightningIcon} passive activations",
            agg.CrackedCoreOrbPassiveTriggers.ToString(),
            "",
            "Passive activations of the starting Lightning orb.");
        Row3(
            sb,
            $"{lightningIcon} fizzled",
            agg.CrackedCoreOrbFizzles.ToString(),
            "",
            "Starting Lightning orbs removed without being evoked.");
        Row3(
            sb,
            $"{lightningIcon} damage attempted",
            agg.TotalDamageAttempted.ToString(),
            "",
            "Damage attempted by the starting Lightning orb.");
        Row3(
            sb,
            $"{lightningIcon} damage dealt",
            agg.TotalDamageDealt.ToString(),
            "",
            "Unblocked damage dealt by the starting Lightning orb.");
        Row3(
            sb,
            $"{lightningIcon} damage blocked",
            agg.TotalDamageBlocked.ToString(),
            "",
            "Damage from the starting Lightning orb absorbed by enemy Block.");
        Row3(
            sb,
            $"{lightningIcon} overkill",
            agg.TotalDamageOverkill.ToString(),
            "",
            "Overkill damage dealt by the starting Lightning orb.");
        Row3(
            sb,
            $"{lightningIcon} kills",
            agg.Kills.ToString(),
            "",
            "Enemies killed by the starting Lightning orb.");
        Row3(
            sb,
            $"{lightningIcon} targets hit",
            agg.TotalTargets.ToString(),
            "",
            "Targets hit by the starting Lightning orb.");
        return sb.ToString();
    }

    private static string BuildSymbioticVirusBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(
            sb,
            "Times orb was evoked",
            agg.SymbioticVirusOrbEvokes.ToString(),
            "");
        Row3(
            sb,
            "Times orb passive triggered",
            agg.SymbioticVirusOrbPassiveTriggers.ToString(),
            "");
        Row3(
            sb,
            "Times orb fizzled",
            agg.SymbioticVirusOrbFizzles.ToString(),
            "");
        return sb.ToString();
    }

    private static string BuildGoldPlatedCablesBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(
            sb,
            "Activations with orb",
            agg.Activations.ToString(),
            "",
            "Activations with orb — times Gold-Plated Cables increased the passive trigger count of the owner's first orb.");

        var activationsByOrb = agg.GoldPlatedCablesActivationsByOrbType
            ?? new Dictionary<string, RelicOrbActivationAggregate>();
        var standardOrbs = new[]
        {
            (OrbCardRegistry.LightningOrbId, "Lightning"),
            (OrbCardRegistry.FrostOrbId, "Frost"),
            (OrbCardRegistry.DarkOrbId, "Dark"),
            (OrbCardRegistry.PlasmaOrbId, "Plasma"),
            (OrbCardRegistry.GlassOrbId, "Glass"),
        };

        foreach (var (orbId, fallbackName) in standardOrbs)
        {
            activationsByOrb.TryGetValue(orbId, out var bucket);
            var displayName = string.IsNullOrWhiteSpace(bucket?.DisplayName)
                ? fallbackName
                : bucket.DisplayName;
            Row3(
                sb,
                $"{displayName} activations",
                Math.Max(0, bucket?.Activations ?? 0).ToString(),
                "",
                $"{displayName} activations — confirmed Gold-Plated Cables activations that targeted a {displayName} orb.");
        }

        foreach (var bucket in activationsByOrb
                     .Where(kvp => standardOrbs.All(standard =>
                         !string.Equals(
                             standard.Item1,
                             kvp.Key,
                             StringComparison.Ordinal)))
                     .Select(kvp => kvp.Value)
                     .Where(bucket => bucket != null && bucket.Activations > 0)
                     .OrderBy(bucket => bucket.DisplayName, StringComparer.Ordinal))
        {
            var displayName = string.IsNullOrWhiteSpace(bucket.DisplayName)
                ? RunTracker.FormatOrbIdForDisplay(bucket.OrbId)
                : bucket.DisplayName;
            Row3(
                sb,
                $"{displayName} activations",
                bucket.Activations.ToString(),
                "",
                $"{displayName} activations — confirmed Gold-Plated Cables activations that targeted a {displayName} orb.");
        }

        Row3(
            sb,
            "Turns with no orb to target",
            agg.GoldPlatedCablesNoOrbTargets.ToString(),
            "",
            "Turns with no orb to target — player turns that ended while Gold-Plated Cables had no first orb available.");
        return sb.ToString();
    }

    private static string BuildHappyFlowerBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendEnergyGeneratedStats(
            sb,
            agg,
            includeAveragePerCombat: true,
            averageLabel: "Avg energy generated per combat",
            combatCount: agg.EnergyGeneratedCombats,
            includeCombatsHeld: true);
        return sb.ToString();
    }

    private static string BuildNunchakuBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var combats = agg.EnergyGeneratedCombats;
        var averageAttacks = combats <= 0
            ? 0m
            : (decimal)agg.NunchakuAttacksPlayed / combats;
        var averageEnergy = combats <= 0
            ? 0m
            : (decimal)agg.EnergyGenerated / combats;
        var averageEndCharge = combats <= 0
            ? 0m
            : (decimal)agg.NunchakuCombatEndChargeTotal / combats;

        Row3(
            sb,
            "Attacks played",
            agg.NunchakuAttacksPlayed.ToString(),
            "",
            AttacksPlayedDescription);
        Row3(sb, "Avg attacks played per combat", FormatDecimal(averageAttacks), "");
        Row3(sb, EnergyLabel("Energy gained total"), agg.EnergyGenerated.ToString(), "");
        Row3(sb, EnergyLabel("Avg energy gained per combat"), FormatDecimal(averageEnergy), "");
        Row3(sb, "Combats ended on 8 charges", agg.NunchakuCombatsEndedOn8Charges.ToString(), "");
        Row3(sb, "Combats ended on 9 charges", agg.NunchakuCombatsEndedOn9Charges.ToString(), "");
        Row3(sb, "Avg charge at combat end", FormatDecimal(averageEndCharge), "");
        return sb.ToString();
    }

    private static string BuildIronClubBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var averageDrawn = agg.IronClubCombats <= 0
            ? 0m
            : (decimal)agg.AdditionalCardsDrawn / agg.IronClubCombats;
        var chargeSamples = agg.IronClubCombatEndChargeCount;
        var chargeTotal = agg.IronClubCombatEndChargeTotal;
        if (chargeSamples <= 0)
        {
            chargeSamples =
                agg.IronClubCombatsEndedOn0Charges
                + agg.IronClubCombatsEndedOn1Charges
                + agg.IronClubCombatsEndedOn2Charges
                + agg.IronClubCombatsEndedOn3Charges;
            chargeTotal =
                agg.IronClubCombatsEndedOn1Charges
                + (agg.IronClubCombatsEndedOn2Charges * 2)
                + (agg.IronClubCombatsEndedOn3Charges * 3);
        }
        var averageEndCharge = chargeSamples <= 0
            ? 0m
            : (decimal)chargeTotal / chargeSamples;

        Row3(sb, "Cards drawn total", agg.AdditionalCardsDrawn.ToString(), "");
        Row3(sb, "Avg cards drawn per combat", FormatDecimal(averageDrawn), "");
        Row3(sb, "Combat ends at 0 charges", agg.IronClubCombatsEndedOn0Charges.ToString(), "");
        Row3(sb, "Combat ends at 1 charge", agg.IronClubCombatsEndedOn1Charges.ToString(), "");
        Row3(sb, "Combat ends at 2 charges", agg.IronClubCombatsEndedOn2Charges.ToString(), "");
        Row3(sb, "Combat ends at 3 charges", agg.IronClubCombatsEndedOn3Charges.ToString(), "");
        Row3(sb, "Avg charge at combat end", FormatDecimal(averageEndCharge), "");
        return sb.ToString();
    }

    private static string BuildLanternBodyBBCode(RelicAggregate agg)
        => BuildTurnEnergyRelicBodyBBCode(
            agg,
            "1st turns ended with excess energy",
            agg.FirstTurnsEndedWithExcessEnergy,
            includeCombatsWithEnergyNotGained: false);

    private static string BuildVeryHotCocoaBodyBBCode(RelicAggregate agg)
        => BuildTurnEnergyRelicBodyBBCode(
            agg,
            "1st turns ended with excess energy",
            agg.FirstTurnsEndedWithExcessEnergy,
            includeCombatsWithEnergyNotGained: false);

    private static string BuildCandelabraBodyBBCode(RelicAggregate agg)
        => BuildTurnEnergyRelicBodyBBCode(
            agg,
            "2nd turns ended with excess energy",
            agg.SecondTurnsEndedWithExcessEnergy,
            includeCombatsWithEnergyNotGained: true);

    private static string BuildChandelierBodyBBCode(RelicAggregate agg)
        => BuildTurnEnergyRelicBodyBBCode(
            agg,
            "3rd turns ended with excess energy",
            agg.ThirdTurnsEndedWithExcessEnergy,
            includeCombatsWithEnergyNotGained: true);

    private static string BuildTurnEnergyRelicBodyBBCode(
        RelicAggregate agg,
        string excessEnergyLabel,
        int turnsEndedWithExcessEnergy,
        bool includeCombatsWithEnergyNotGained)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        AppendEnergyGeneratedStats(sb, agg);
        Row3(sb, excessEnergyLabel, turnsEndedWithExcessEnergy.ToString(), "");
        if (includeCombatsWithEnergyNotGained)
            Row3(sb, "Combats without energy", agg.CombatsWithoutActivation.ToString(), "");
        return sb.ToString();
    }

    private static string BuildBoomingConchBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendEnergyGeneratedStats(sb, agg);
        Row3(sb, "Cards drawn", agg.AdditionalCardsDrawn.ToString(), "");
        return sb.ToString();
    }

    private static string BuildGremlinHornBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var activationsPerTurn = agg.GremlinHornTurns <= 0
            ? 0m
            : (decimal)agg.GremlinHornRateActivations / agg.GremlinHornTurns;
        var activationsPerCombat = agg.GremlinHornCombats <= 0
            ? 0m
            : (decimal)agg.GremlinHornRateActivations / agg.GremlinHornCombats;

        RelicActivationRow(sb, agg.Activations.ToString());
        AppendEnergyGeneratedStats(sb, agg, totalLabel: "Energy gained");
        Row3(sb, "Cards drawn", agg.AdditionalCardsDrawn.ToString(), "");
        Row3(
            sb,
            "Avg activations per turn",
            FormatDecimal(activationsPerTurn),
            "",
            "Average activations per turn — observed Gremlin Horn activations divided by player turns in which it was held, including zero-activation turns.");
        Row3(
            sb,
            "Avg activations per combat",
            FormatDecimal(activationsPerCombat),
            "",
            "Average activations per combat — observed Gremlin Horn activations divided by combats in which it was held, including zero-activation combats.");
        return sb.ToString();
    }

    private static string BuildPendulumBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var cardsDrawnPerCombat = agg.PendulumCombats <= 0
            ? 0m
            : (decimal)agg.AdditionalCardsDrawn / agg.PendulumCombats;
        var chargeSamples = agg.PendulumCombatEndChargeCount;
        var chargeTotal = agg.PendulumCombatEndChargeTotal;
        if (chargeSamples <= 0)
        {
            chargeSamples =
                agg.PendulumCombatsEndedOn0Charges
                + agg.PendulumCombatsEndedOn1Charge
                + agg.PendulumCombatsEndedOn2Charges;
            chargeTotal =
                agg.PendulumCombatsEndedOn1Charge
                + (agg.PendulumCombatsEndedOn2Charges * 2);
        }
        var averageEndCharge = chargeSamples <= 0
            ? 0m
            : (decimal)chargeTotal / chargeSamples;
        var averageActivationTurn = agg.PendulumActivationTurnSamples <= 0
            ? 0m
            : (decimal)agg.PendulumActivationTurnTotal
                / agg.PendulumActivationTurnSamples;

        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, "Cards drawn", agg.AdditionalCardsDrawn.ToString(), "");
        Row3(sb, "Avg cards drawn per combat", FormatDecimal(cardsDrawnPerCombat), "");
        Row3(
            sb,
            "Avg activation turn",
            FormatDecimal(averageActivationTurn),
            "",
            "Average activation turn — the average player turn number when Pendulum activated; combats too short for it to activate are excluded.");
        Row3(sb, "Combats ended on 0 charges", agg.PendulumCombatsEndedOn0Charges.ToString(), "");
        Row3(sb, "Combats ended on 1 charge", agg.PendulumCombatsEndedOn1Charge.ToString(), "");
        Row3(sb, "Combats ended on 2 charges", agg.PendulumCombatsEndedOn2Charges.ToString(), "");
        Row3(sb, "Avg charge at combat end", FormatDecimal(averageEndCharge), "");
        return sb.ToString();
    }

    private static string BuildMercuryHourglassBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var activationsPerCombat = agg.MercuryHourglassCombats <= 0
            ? 0m
            : (decimal)agg.Activations / agg.MercuryHourglassCombats;

        AppendRelicDamageStats(
            sb,
            agg,
            triggerDescription: "Activations — owner turn starts where Mercury Hourglass fired.",
            averageLabel: "Avg damage per combat",
            averageDenominator: agg.MercuryHourglassCombats);
        Row3(
            sb,
            "Avg activations per combat",
            FormatDecimal(activationsPerCombat),
            "",
            "Average activations per combat — activations divided by combats where Mercury Hourglass was held.");
        return sb.ToString();
    }

    private static string BuildMrStrugglesBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendRelicDamageStats(
            sb,
            agg,
            triggerDescription: "Activations — the number of times this relic has activated.",
            averageLabel: "Damage per activation",
            averageDenominator: agg.Activations);
        return sb.ToString();
    }

    private static string BuildLostWispBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var damagePerPower = agg.Activations <= 0
            ? 0m
            : (decimal)agg.TotalDamageDealt / agg.Activations;

        Row3(
            sb,
            "Powers played",
            agg.Activations.ToString(),
            "",
            "Power cards played by this relic's owner while Lost Wisp could activate.");
        Row3(sb, "Damage attempted", agg.TotalDamageAttempted.ToString(), "");
        Row3(sb, "Damage dealt", agg.TotalDamageDealt.ToString(), "");
        Row3(sb, "Damage blocked", agg.TotalDamageBlocked.ToString(), "");
        Row3(sb, "Overkill", agg.TotalDamageOverkill.ToString(), "");
        Row3(sb, "Kills", agg.Kills.ToString(), "");
        Row3(sb, "Targets hit", agg.TotalTargets.ToString(), "");
        Row3(sb, "Avg damage per Power", FormatDecimal(damagePerPower), "");
        return sb.ToString();
    }

    private static string BuildGamePieceBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var cardsDrawnPerTurn = agg.GamePieceTurns <= 0
            ? 0m
            : (decimal)agg.AdditionalCardsDrawn / agg.GamePieceTurns;
        var cardsDrawnPerCombat = agg.GamePieceCombats <= 0
            ? 0m
            : (decimal)agg.AdditionalCardsDrawn / agg.GamePieceCombats;

        Row3(
            sb,
            "Powers played",
            agg.Activations.ToString(),
            "",
            "Power cards played by Game Piece's owner while it could activate.");
        Row3(
            sb,
            DrawLabel("Cards drawn"),
            agg.AdditionalCardsDrawn.ToString(),
            "",
            "Cards drawn — cards that actually reached the hand from Game Piece's direct draw.");
        Row3(
            sb,
            DrawLabel("Card draws blocked"),
            agg.AdditionalCardDrawsBlocked.ToString(),
            "",
            "Card draws blocked — Game Piece draws prevented by draw limits, a full hand, or draw-prevention effects.");
        Row3(
            sb,
            DrawLabel("Avg cards drawn per turn"),
            FormatDecimal(cardsDrawnPerTurn),
            "",
            "Average cards drawn per turn — observed Game Piece cards drawn divided by player turns while held.");
        Row3(
            sb,
            DrawLabel("Avg cards drawn per combat"),
            FormatDecimal(cardsDrawnPerCombat),
            "",
            "Average cards drawn per combat — observed Game Piece cards drawn divided by combats while held.");
        return sb.ToString();
    }

    private static string BuildForgottenSoulBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var damagePerTurn = agg.ForgottenSoulTurns <= 0
            ? 0m
            : (decimal)agg.TotalDamageDealt / agg.ForgottenSoulTurns;
        var damagePerCombat = agg.ForgottenSoulCombats <= 0
            ? 0m
            : (decimal)agg.TotalDamageDealt / agg.ForgottenSoulCombats;

        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            "Activations — same-owner cards exhausted while Forgotten Soul was held.");
        Row3(sb, "Damage dealt", agg.TotalDamageDealt.ToString(), "");
        Row3(sb, "Damage blocked", agg.TotalDamageBlocked.ToString(), "");
        Row3(sb, "Kills", agg.Kills.ToString(), "");
        Row3(sb, "Targets hit", agg.TotalTargets.ToString(), "");
        Row3(sb, "Avg damage dealt per turn", FormatDecimal(damagePerTurn), "");
        Row3(sb, "Avg damage dealt per combat", FormatDecimal(damagePerCombat), "");
        return sb.ToString();
    }

    private static string BuildParryingShieldBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendRelicDamageStats(
            sb,
            agg,
            triggerDescription: "Activations — the number of times this relic has activated.",
            averageLabel: "Damage per activation",
            averageDenominator: agg.Activations);
        return sb.ToString();
    }

    private static string BuildFestivePopperBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendRelicDamageStats(
            sb,
            agg,
            triggerDescription: "Combats triggered — the number of combats in which this relic activated.",
            averageLabel: "Damage per combat",
            averageDenominator: agg.Activations);
        return sb.ToString();
    }

    private static string BuildScreamingFlagonBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var averageCardsHeldPerTurn = agg.ScreamingFlagonTurns <= 0
            ? 0m
            : (decimal)agg.ScreamingFlagonTurnEndHandSizeTotal
                / agg.ScreamingFlagonTurns;
        var averageCardsHeldPerCombat = agg.ScreamingFlagonCombats <= 0
            ? 0m
            : (decimal)agg.ScreamingFlagonTurnEndHandSizeTotal
                / agg.ScreamingFlagonCombats;
        AppendRelicDamageStats(
            sb,
            agg,
            triggerDescription: "Activations — turns when the owner ended with an empty hand and Screaming Flagon fired.",
            averageLabel: "Damage per activation",
            averageDenominator: agg.Activations);
        Row3(
            sb,
            "Avg cards held in hand at turn end per turn",
            FormatDecimal(averageCardsHeldPerTurn),
            "",
            "Average number of cards held in hand at turn end per turn while Screaming Flagon was owned.");
        Row3(
            sb,
            "Avg cards held in hand at turn end per combat",
            FormatDecimal(averageCardsHeldPerCombat),
            "",
            "Average number of cards held in hand at turn end per combat while Screaming Flagon was owned.");
        return sb.ToString();
    }

    private static void AppendRelicDamageStats(
        StringBuilder sb,
        RelicAggregate agg,
        string triggerDescription,
        string? averageLabel = null,
        int averageDenominator = 0)
    {
        RelicActivationRow(sb, agg.Activations.ToString(), triggerDescription);
        Row3(sb, "Damage attempted", agg.TotalDamageAttempted.ToString(), "");
        Row3(sb, "Damage dealt", agg.TotalDamageDealt.ToString(), "");
        Row3(sb, "Damage blocked", agg.TotalDamageBlocked.ToString(), "");
        Row3(sb, "Overkill", agg.TotalDamageOverkill.ToString(), "");
        Row3(sb, "Kills", agg.Kills.ToString(), "");
        Row3(sb, "Targets hit", agg.TotalTargets.ToString(), "");

        if (!string.IsNullOrWhiteSpace(averageLabel))
        {
            var average = averageDenominator <= 0
                ? 0m
                : (decimal)agg.TotalDamageDealt / averageDenominator;
            Row3(sb, averageLabel, FormatDecimal(average), "");
        }
    }

    private static string BuildPenNibBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var averageBaseDamage = agg.PenNibAttacksPlayed <= 0
            ? 0m
            : (decimal)agg.TotalDamageAttempted / agg.PenNibAttacksPlayed;
        var averageEndCharge = agg.PenNibTurnEndChargeCount <= 0
            ? 0m
            : (decimal)agg.PenNibTurnEndChargeTotal / agg.PenNibTurnEndChargeCount;
        var activations = Math.Max(
            agg.Activations,
            agg.PenNibAttacksPlayed / 10);

        RelicActivationRow(sb, activations.ToString());
        Row3(sb, "Base damage added", agg.TotalDamageAttempted.ToString(), "");
        Row3(sb, "Avg base damage added per attack", FormatDecimal(averageBaseDamage), "");
        Row3(
            sb,
            "Attacks played",
            agg.PenNibAttacksPlayed.ToString(),
            "",
            AttacksPlayedDescription);
        Row3(sb, "Turns ended on 8 charges", agg.PenNibTurnsEndedOn8Charges.ToString(), "");
        Row3(sb, "Turns ended on 9 charges", agg.PenNibTurnsEndedOn9Charges.ToString(), "");
        Row3(sb, "Avg charge at turn end", FormatDecimal(averageEndCharge), "");
        return sb.ToString();
    }

    private static string BuildHornCleatBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, BlockLabel("block gained"), agg.AdditionalBlockGained.ToString(), "");
        return sb.ToString();
    }

    private static string BuildCaptainsWheelBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, BlockLabel("block gained"), agg.AdditionalBlockGained.ToString(), "");
        return sb.ToString();
    }

    private static string BuildPrismaticGemBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendEnergyGeneratedStats(sb, agg);
        Row3(sb, "Card rewards affected", agg.CardRewardsAffected.ToString(), "");
        AppendObservedCardRewardPoolRows(sb, agg, "Prismatic Gem");
        return sb.ToString();
    }

    private static string BuildDingyRugBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(sb, "Card rewards affected", agg.CardRewardsAffected.ToString(), "");
        AppendObservedCardRewardPoolRows(sb, agg, "Dingy Rug");
        return sb.ToString();
    }

    private static void AppendObservedCardRewardPoolRows(
        StringBuilder sb,
        RelicAggregate agg,
        string relicName)
    {
        foreach (var (key, displayName) in EnumerateObservedCardRewardPools())
        {
            OfferedTakenRow(
                sb,
                ["card"],
                displayName,
                GetObservedCardRewardPoolOfferCount(agg, key),
                GetObservedCardRewardPoolTakenCount(agg, key),
                $"{displayName} cards offered/taken — final visible {displayName} card options in rewards while {relicName} was held, and how many of those options were actually taken.");
        }
    }

    private static IEnumerable<(string Key, string DisplayName)>
        EnumerateObservedCardRewardPools()
    {
        yield return ("ironclad", "Ironclad");
        yield return ("silent", "Silent");
        yield return ("regent", "Regent");
        yield return ("necrobinder", "Necrobinder");
        yield return ("defect", "Defect");
        yield return ("colorless", "Colorless");
    }

    private static int GetObservedCardRewardPoolOfferCount(RelicAggregate agg, string key)
        => SumObservedCardRewardPool(agg, key, category => category.Count);

    private static int GetObservedCardRewardPoolTakenCount(RelicAggregate agg, string key)
        => SumObservedCardRewardPool(agg, key, category => category.Taken);

    private static int SumObservedCardRewardPool(
        RelicAggregate agg,
        string key,
        Func<CardRewardCategoryAggregate, int> select)
    {
        var legacyKey = $"{key}_card_pool";
        return agg.CardRewardCategories
            .Where(category =>
                string.Equals(category.Key, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(category.Key, legacyKey, StringComparison.OrdinalIgnoreCase))
            .Sum(category => Math.Max(0, select(category.Value)));
    }

    private static string BuildPumpkinCandleBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var combatSamples = agg.PumpkinCandleCombatStartChargeSamples;
        var averageCharges = combatSamples <= 0
            ? 0m
            : (decimal)agg.PumpkinCandleCombatStartChargeTotal / combatSamples;

        AppendEnergyGeneratedStats(
            sb,
            agg,
            totalLabel: "Energy gained total",
            includeAveragePerCombat: true,
            combatCount: combatSamples);
        Row3(
            sb,
            "Avg charges at combat start",
            FormatDecimal(averageCharges),
            "");
        var candleIcon = StatConceptGlossary.RenderHintedInlineImage(
            ResolveGrantedRelicIconPath("RELIC.PUMPKIN_CANDLE"),
            "Pumpkin Candle");
        DescribedIconRow(
            sb,
            ["campfire"],
            [],
            candleIcon,
            agg.PumpkinCandleRekindles.ToString(),
            "Times Pumpkin Candle was rekindled at a campfire.");
        return sb.ToString();
    }

    private static string BuildSpikedGauntletsBodyBBCode(
        RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var powersPlayed = agg.SpikedGauntletsTaxedPowersPlayed;
        var combats = agg.EnergyGeneratedCombats;
        var averagePowerCost = powersPlayed <= 0
            ? 0m
            : agg.SpikedGauntletsPowerCostTotal / powersPlayed;
        var averageSpentPerTurn = agg.SpikedGauntletsTurns <= 0
            ? 0m
            : (decimal)agg.SpikedGauntletsPowerEnergySpent
              / agg.SpikedGauntletsTurns;
        var averageSpentPerCombat = combats <= 0
            ? 0m
            : (decimal)agg.SpikedGauntletsPowerEnergySpent / combats;

        Row3(
            sb,
            "Taxed Powers played",
            powersPlayed.ToString(),
            "",
            "Completed owner Power plays while Spiked Gauntlets applied its +1 Energy cost modifier.");
        Row3(
            sb,
            EnergyLabel("Avg cost of Powers"),
            FormatDecimal(averagePowerCost),
            "",
            "Average resolved Energy cost of Powers played while Spiked Gauntlets was held, including its tax.");
        Row3(
            sb,
            EnergyLabel("Avg spent on Powers per turn"),
            FormatDecimal(averageSpentPerTurn),
            "",
            "Average Energy actually spent on Powers per player turn while Spiked Gauntlets was held, including zero-spend turns.");
        Row3(
            sb,
            EnergyLabel("Avg spent on Powers per combat"),
            FormatDecimal(averageSpentPerCombat),
            "",
            "Average Energy actually spent on Powers per combat while Spiked Gauntlets was held, including zero-spend combats.");
        AppendEnergyGeneratedStats(
            sb,
            agg,
            totalLabel: "Energy gained total",
            includeAveragePerCombat: true,
            combatCount: combats);
        return sb.ToString();
    }

    private static string BuildSealOfGoldBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            "Times triggered — the number of times this relic has activated.");
        Row3(sb, "Gold loss attempted", (agg.Activations * SealOfGoldLossPerTrigger).ToString(), "");
        Row3(sb, "Gold lost", agg.GoldLost.ToString(), "");
        Row3(sb, "Gold loss blocked", agg.GoldLossBlocked.ToString(), "");
        AppendEnergyGeneratedStats(
            sb,
            agg,
            totalLabel: "Energy gained total",
            includeAveragePerCombat: true,
            combatCount: agg.EnergyGeneratedCombats);
        return sb.ToString();
    }

    private static string BuildFresnelLensBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendMaxHpChangeRows(sb, agg, "Max HP lost to Drowning Beacon", MaxHpLost(agg));
        NimbleRow(
            sb,
            "cards taken",
            agg.NimbleCardsTaken,
            "Nimble cards taken from rewards affected by Fresnel Lens.");
        NimbleRow(
            sb,
            "reward screens",
            agg.RewardScreensWithNimbleCards,
            "Reward screens that offered at least one Nimble card.");
        NimbleRow(
            sb,
            "reward screens with 2",
            agg.RewardScreensWithTwoNimbleCards,
            "Reward screens that offered exactly two Nimble cards.");
        NimbleRow(
            sb,
            "reward screens with 3+",
            agg.RewardScreensWithThreeOrMoreNimbleCards,
            "Reward screens that offered three or more Nimble cards.");
        NimbleRow(
            sb,
            "reward screens with none",
            agg.RewardScreensWithoutNimbleCards,
            "Reward screens that offered no Nimble cards.");
        NimbleRow(
            sb,
            "offered, none taken",
            agg.RewardScreensWithNimbleCardsButNoneTaken,
            "Reward screens that offered Nimble cards but from which none were taken.");
        return sb.ToString();
    }

    private static void NimbleRow(
        StringBuilder sb,
        string label,
        int value,
        string fullDescription)
    {
        var presentation = RelicStatRowVocabulary.Create(
            label,
            fullDescription);
        DescribedIconRow(
            sb,
            new[] { "nimble" }
                .Concat(presentation.ConceptIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            presentation.DenominatorConceptIds,
            presentation.Label,
            value.ToString(),
            presentation.FullDescription);
    }

    private static string BuildWingCharmBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var taken = Math.Max(0, agg.WingCharmSwiftCardsTaken);
        WingCharmSwiftOfferedTakenRow(
            sb,
            "card",
            taken + Math.Max(0, agg.WingCharmSwiftCardsNotTaken),
            taken,
            "Swift cards offered/taken — options Wing Charm enchanted, and how many of them were successfully taken.");
        WingCharmSwiftOfferedRow(
            sb,
            "card",
            agg.WingCharmCommonSwiftCardsOffered,
            "Common Swift cards offered by Wing Charm.");
        WingCharmSwiftOfferedRow(
            sb,
            "card_uncommon",
            agg.WingCharmUncommonSwiftCardsOffered,
            "Uncommon Swift cards offered by Wing Charm.");
        WingCharmSwiftOfferedRow(
            sb,
            "card_rare",
            agg.WingCharmRareSwiftCardsOffered,
            "Rare Swift cards offered by Wing Charm.");
        return sb.ToString();
    }

    private static void WingCharmSwiftOfferedTakenRow(
        StringBuilder sb,
        string cardConceptId,
        int offered,
        int taken,
        string fullDescription)
        => WingCharmSwiftRow(
            sb,
            cardConceptId,
            $"{StatConceptGlossary.RenderHintedGlyph("offered")}/{StatConceptGlossary.RenderHintedGlyph("taken")}",
            FormatOfferedTaken(offered, taken),
            fullDescription);

    private static void WingCharmSwiftOfferedRow(
        StringBuilder sb,
        string cardConceptId,
        int offered,
        string fullDescription)
        => WingCharmSwiftRow(
            sb,
            cardConceptId,
            StatConceptGlossary.RenderHintedGlyph("offered"),
            Math.Max(0, offered).ToString(),
            fullDescription);

    private static void WingCharmSwiftRow(
        StringBuilder sb,
        string cardConceptId,
        string actionExpression,
        string value,
        string fullDescription)
    {
        var iconExpression =
            $"[b]+[/b] {StatConceptGlossary.RenderHintedGlyph("swift")} {actionExpression}";
        DescribedIconFlowRow(
            sb,
            new[] { cardConceptId },
            Array.Empty<string>(),
            iconExpression,
            value,
            fullDescription);
    }

    private static string BuildLastingCandyBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        LastingCandyPowerRow(
            sb,
            "power",
            agg.LastingCandyPowersOffered,
            agg.LastingCandyPowersTaken,
            "Powers offered/taken — Power options Lasting Candy added to combat card rewards, and how many of them entered the permanent deck. The remainder was rejected before its reward resolved or rerolled.");
        DescribedIconRow(
            sb,
            ["activation", "in", "all", "elite"],
            [],
            "",
            agg.LastingCandyEliteActivations.ToString(),
            "Elite activations — Lasting Candy Power options added to card rewards after Elite combats.");
        DescribedIconRow(
            sb,
            ["activation"],
            [],
            $"{StatConceptGlossary.RenderHintedGlyph("in")} {StatConceptGlossary.RenderHintedGlyph("all")} Boss {StatConceptGlossary.RenderHintedGlyph("combat")}",
            agg.LastingCandyBossActivations.ToString(),
            "Boss activations — Lasting Candy Power options added to card rewards after Boss combats.");
        LastingCandyPowerRow(
            sb,
            "power_uncommon",
            agg.LastingCandyUncommonPowersOffered,
            agg.LastingCandyUncommonPowersTaken,
            "Uncommon Lasting Candy Powers offered/taken.");
        LastingCandyPowerRow(
            sb,
            "power_rare",
            agg.LastingCandyRarePowersOffered,
            agg.LastingCandyRarePowersTaken,
            "Rare Lasting Candy Powers offered/taken.");
        return sb.ToString();
    }

    /// <summary>
    /// Lasting Candy's rejected counters are incremented in the same call that
    /// increments offered and taken, so rejected is always offered minus taken
    /// and does not need a row of its own.
    /// </summary>
    private static void LastingCandyPowerRow(
        StringBuilder sb,
        string powerConceptId,
        int offered,
        int taken,
        string fullDescription)
        => OfferedTakenRow(
            sb,
            [powerConceptId],
            string.Empty,
            offered,
            taken,
            fullDescription);

    private static string BuildFishingRodBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var upgradedCards = (agg.UpgradedCards ?? new List<string>())
            .Where(card => !string.IsNullOrWhiteSpace(card))
            .ToList();
        var averageFloorsToCombat = CalculateAverageFishingRodFloorDistance(
            agg.FishingRodCombatFloorDistances);
        var averageFloorsPerUpgrade = CalculateAverageFishingRodFloorDistance(
            agg.FishingRodUpgradeFloorDistances);

        Row3(sb, "Cards upgraded", agg.CardsUpgraded.ToString(), "");
        Row3(
            sb,
            "Avg floors to next combat",
            FormatDecimal(averageFloorsToCombat),
            "",
            "Average floors traveled from Fishing Rod's acquisition to its first normal Monster combat, then between consecutive normal Monster combats.");
        Row3(
            sb,
            "Avg floors per upgrade",
            FormatDecimal(averageFloorsPerUpgrade),
            "",
            "Average floors traveled from Fishing Rod's acquisition to its first successful card upgrade, then between consecutive successful upgrades.");

        foreach (var card in upgradedCards)
            TextValueRow(sb, "Upgraded card", StatsTooltip.EscapeBbcode(card), "");

        return sb.ToString();
    }

    internal static decimal CalculateAverageFishingRodFloorDistance(
        IReadOnlyList<int>? distances)
    {
        if (distances == null || distances.Count == 0) return 0m;

        var validDistances = distances.Where(distance => distance >= 0).ToList();
        return validDistances.Count == 0
            ? 0m
            : (decimal)validDistances.Sum() / validDistances.Count;
    }

    private static string BuildEggBodyBBCode(RelicAggregate agg, string cardType)
    {
        var sb = new StringBuilder();
        AppendEggCardRow(
            sb,
            cardType,
            rarity: null,
            offered: agg.UpgradedCardsOffered,
            taken: agg.UpgradedCardsTaken);
        AppendEggCardRow(
            sb,
            cardType,
            "common",
            agg.UpgradedCommonCardsOffered,
            agg.UpgradedCommonCardsTaken);
        AppendEggCardRow(
            sb,
            cardType,
            "uncommon",
            agg.UpgradedUncommonCardsOffered,
            agg.UpgradedUncommonCardsTaken);
        AppendEggCardRow(
            sb,
            cardType,
            "rare",
            agg.UpgradedRareCardsOffered,
            agg.UpgradedRareCardsTaken);
        return sb.ToString();
    }

    private static void AppendEggCardRow(
        StringBuilder sb,
        string cardType,
        string? rarity,
        int offered,
        int taken)
    {
        var pluralCardType = $"{cardType}s";
        var rarityText = string.IsNullOrEmpty(rarity)
            ? string.Empty
            : $"{rarity} ";
        var typeConceptId = string.IsNullOrEmpty(rarity)
            ? cardType
            : $"{cardType}_{rarity}";
        OfferedTakenRow(
            sb,
            ["upgraded", typeConceptId],
            string.Empty,
            offered,
            taken,
            $"Upgraded {rarityText}{pluralCardType} offered/taken.");
    }

    private static string BuildLeadPaperweightBodyBBCode(
        RelicAggregate agg,
        int? floorAcquiredFallback = null)
    {
        var sb = new StringBuilder();
        Row3(
            sb,
            "Floor acquired",
            FormatFloor(agg.FloorAcquired ?? floorAcquiredFallback),
            "");

        var screen = (agg.CardRewardScreens
                ?? new List<RelicCardRewardScreenAggregate>())
            .LastOrDefault(candidate =>
                candidate != null && candidate.ScreenNumber == 1);
        if (screen == null)
        {
            TextValueRow(sb, "Card choice", "not recorded", "");
            return sb.ToString();
        }

        var cards = screen.Cards ?? new List<RelicCardRewardOptionAggregate>();
        if (cards.Count == 0)
        {
            TextValueRow(
                sb,
                "Card choice",
                $"no {StatConceptGlossary.RenderHintedGlyph("offered")}",
                "");
            return sb.ToString();
        }

        foreach (var card in cards)
        {
            if (card == null) continue;

            var displayName = !string.IsNullOrWhiteSpace(card.DisplayName)
                ? card.DisplayName
                : !string.IsNullOrWhiteSpace(card.CardId)
                    ? RunTracker.FormatCardIdForDisplay(card.CardId)
                    : "Unknown card";
            AppendRelicCardChoiceRow(
                sb,
                "Lead Paperweight",
                StatsTooltip.EscapeBbcode(displayName),
                !screen.Resolved ? "pending" : card.Taken ? "taken" : "not taken");
        }

        return sb.ToString();
    }

    private static string BuildSilverCrucibleBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var screens = agg.CardRewardScreens ?? new List<RelicCardRewardScreenAggregate>();

        for (var screenNumber = 1; screenNumber <= 3; screenNumber++)
        {
            var screen = screens.LastOrDefault(candidate =>
                candidate != null && candidate.ScreenNumber == screenNumber);
            if (screen == null)
            {
                TextValueRow(sb, $"Card reward {screenNumber}", "not seen yet", "");
                continue;
            }

            var cards = screen.Cards ?? new List<RelicCardRewardOptionAggregate>();
            if (cards.Count == 0)
            {
                TextValueRow(
                    sb,
                    $"Card reward {screenNumber}",
                    $"no {StatConceptGlossary.RenderHintedGlyph("offered")}",
                    "");
                continue;
            }

            Row3(
                sb,
                $"Card reward {screenNumber}",
                string.Empty,
                "",
                $"The cards offered in Silver Crucible reward {screenNumber}.");
            foreach (var card in cards)
            {
                if (card == null) continue;

                var displayName = !string.IsNullOrWhiteSpace(card.DisplayName)
                    ? card.DisplayName
                    : !string.IsNullOrWhiteSpace(card.CardId)
                        ? RunTracker.FormatCardIdForDisplay(card.CardId)
                        : "Unknown card";
                AppendRelicCardChoiceRow(
                    sb,
                    "Silver Crucible",
                    StatsTooltip.EscapeBbcode(displayName),
                    !screen.Resolved ? "pending" : card.Taken ? "taken" : "not taken");
            }
        }

        return sb.ToString();
    }

    private static void AppendRelicCardChoiceRow(
        StringBuilder sb,
        string relicName,
        string displayName,
        string outcome)
    {
        DescribedIconFlowRow(
            sb,
            ["card"],
            [],
            $"[b]{displayName}[/b]",
            RenderTakenOutcome(outcome),
            $"This card was offered by {relicName} and was {outcome}.");
    }

    private static string RenderTakenOutcome(string outcome)
    {
        if (string.Equals(outcome, "taken", StringComparison.OrdinalIgnoreCase))
            return StatConceptGlossary.RenderHintedGlyph("taken");
        if (string.Equals(outcome, "not taken", StringComparison.OrdinalIgnoreCase))
            return $"not {StatConceptGlossary.RenderHintedGlyph("taken")}";
        return StatsTooltip.EscapeBbcode(outcome);
    }

    private static string BuildOrreryBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var rewards = agg.OrreryRewards ?? new List<OrreryRewardAggregate>();

        for (var rewardNumber = 1; rewardNumber <= 5; rewardNumber++)
        {
            var reward = rewards.LastOrDefault(candidate =>
                candidate != null && candidate.RewardNumber == rewardNumber);
            TextValueRow(
                sb,
                $"Reward {rewardNumber}",
                reward == null ? "not seen yet" : FormatOrreryRewardOutcome(reward),
                "");
        }

        return sb.ToString();
    }

    private static string FormatOrreryRewardOutcome(OrreryRewardAggregate reward)
    {
        if (string.Equals(reward.Outcome, "skipped", StringComparison.OrdinalIgnoreCase))
            return RenderTakenOutcome("not taken");

        if (string.Equals(reward.Outcome, "alternative", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(
                    reward.AlternativeId,
                    PaelsWing.sacrificeAlternativeKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                return StatConceptGlossary.RenderInlineImage(
                    PaelsWingIconPath);
            }

            var alternative = string.IsNullOrWhiteSpace(reward.AlternativeId)
                ? "alternative"
                : reward.AlternativeId.Replace('_', ' ').ToLowerInvariant();
            return $"selected {StatsTooltip.EscapeBbcode(alternative)}";
        }

        if (string.Equals(reward.Outcome, "obtained", StringComparison.OrdinalIgnoreCase))
        {
            var cards = (reward.CardsObtained ?? new List<OrreryObtainedCardAggregate>())
                .Where(card => card != null)
                .Select(card =>
                {
                    var displayName = !string.IsNullOrWhiteSpace(card.DisplayName)
                        ? card.DisplayName
                        : !string.IsNullOrWhiteSpace(card.CardId)
                            ? RunTracker.FormatCardIdForDisplay(card.CardId)
                            : "Unknown card";
                    var upgradeSuffix = card.UpgradeLevel <= 0
                        ? ""
                        : new string('+', card.UpgradeLevel);
                    return StatsTooltip.EscapeBbcode(displayName + upgradeSuffix);
                })
                .ToList();
            return cards.Count == 0
                ? $"{RenderTakenOutcome("taken")} {StatConceptGlossary.RenderHintedGlyph("card")}"
                : $"{RenderTakenOutcome("taken")} {string.Join(", ", cards)}";
        }

        if (string.Equals(
                reward.Outcome,
                "completed_without_card",
                StringComparison.OrdinalIgnoreCase))
            return RenderTakenOutcome("not taken");

        return "pending";
    }

    private static string BuildBloodSoakedRoseBodyBBCode(RelicAggregate agg, CardAggregate curseAgg)
    {
        var sb = new StringBuilder();
        AppendEnergyGeneratedStats(
            sb,
            agg,
            totalLabel: "Energy gained total",
            includeAveragePerCombat: true);

        AppendRelatedCurseCardStats(sb, "Enthralled", curseAgg);
        return sb.ToString();
    }

    private static string BuildStorybookBodyBBCode(CardAggregate brightestFlameAgg)
    {
        brightestFlameAgg ??= new CardAggregate();

        var sb = new StringBuilder();
        Row3(sb, "Brightest Flame played", brightestFlameAgg.Plays.ToString(), "");
        Row3(sb, DrawLabel("Brightest Flame drawn"), brightestFlameAgg.TimesDrawn.ToString(), "");
        Row3(
            sb,
            EnergyLabel("Energy gained by Flame"),
            brightestFlameAgg.TotalEnergyGenerated.ToString(),
            "");
        Row3(sb, DrawLabel("Cards drawn by Flame"), brightestFlameAgg.TimesCardsDrawn.ToString(), "");
        Row3(sb, "Max HP lost to Flame", brightestFlameAgg.TotalMaxHpLost.ToString(), "");
        return sb.ToString();
    }

    private static string BuildRegaliteBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var blockPerTurn = agg.RegaliteTurns <= 0
            ? 0m
            : (decimal)agg.AdditionalBlockGained / agg.RegaliteTurns;
        var blockPerCombat = agg.RegaliteCombats <= 0
            ? 0m
            : (decimal)agg.AdditionalBlockGained / agg.RegaliteCombats;

        Row3(sb, "Cards created", agg.RegaliteCardsCreated.ToString(), "");
        Row3(sb, BlockLabel("block gained"), agg.AdditionalBlockGained.ToString(), "");
        Row3(sb, BlockLabel("avg block per turn"), FormatDecimal(blockPerTurn), "");
        Row3(sb, BlockLabel("avg block per combat"), FormatDecimal(blockPerCombat), "");
        return sb.ToString();
    }

    private static string BuildMusicBoxBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var attacksPerTurn = agg.MusicBoxTurns <= 0
            ? 0m
            : (decimal)agg.MusicBoxAttacksCreated / agg.MusicBoxTurns;
        var attacksPerCombat = agg.MusicBoxCombats <= 0
            ? 0m
            : (decimal)agg.MusicBoxAttacksCreated / agg.MusicBoxCombats;

        DescribedIconRow(
            sb,
            ["attack"],
            [],
            "created",
            agg.MusicBoxAttacksCreated.ToString(),
            "Attacks created by Musical Box that successfully entered combat.");
        DescribedIconRow(
            sb,
            ["attack_common"],
            [],
            "created",
            agg.MusicBoxCommonAttacksCreated.ToString(),
            "Common Attacks created by Musical Box.");
        DescribedIconRow(
            sb,
            ["attack_uncommon"],
            [],
            "created",
            agg.MusicBoxUncommonAttacksCreated.ToString(),
            "Uncommon Attacks created by Musical Box.");
        DescribedIconRow(
            sb,
            ["attack_rare"],
            [],
            "created",
            agg.MusicBoxRareAttacksCreated.ToString(),
            "Rare Attacks created by Musical Box.");
        DescribedIconRow(
            sb,
            ["average", "attack", "turn"],
            ["turn"],
            "created",
            FormatDecimal(attacksPerTurn),
            "Average Attacks created by Musical Box per turn while held.");
        DescribedIconRow(
            sb,
            ["average", "attack", "combat"],
            ["combat"],
            "created",
            FormatDecimal(attacksPerCombat),
            "Average Attacks created by Musical Box per combat while held.");
        DescribedIconRow(
            sb,
            ["attack", "exhaust"],
            [],
            "by Ethereal",
            agg.MusicBoxAttacksExhaustedByEthereal.ToString(),
            "Musical Box Attacks that remained in hand and exhausted because of Ethereal.");
        return sb.ToString();
    }

    private static string BuildCrossbowBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var attacksPerCombat = agg.CrossbowCombats <= 0
            ? 0m
            : (decimal)agg.CrossbowAttacksGained / agg.CrossbowCombats;
        var discountPerTurn = agg.CrossbowTurns <= 0
            ? 0m
            : agg.CrossbowDiscountGivenTotal / agg.CrossbowTurns;
        var discountPerCombat = agg.CrossbowCombats <= 0
            ? 0m
            : agg.CrossbowDiscountGivenTotal / agg.CrossbowCombats;

        DescribedIconRow(
            sb,
            ["attack"],
            [],
            "gained",
            agg.CrossbowAttacksGained.ToString(),
            "Attacks gained from Crossbow that successfully entered combat.");
        DescribedIconRow(
            sb,
            ["average", "attack", "combat"],
            ["combat"],
            "gained",
            FormatDecimal(attacksPerCombat),
            "Average Attacks gained from Crossbow per combat while held.");
        DescribedIconRow(
            sb,
            ["average", "attack", "energy", "turn"],
            ["turn"],
            "discount",
            FormatDecimal(discountPerTurn),
            "Average effective energy discount given to Crossbow Attacks per player turn while held.");
        DescribedIconRow(
            sb,
            ["average", "attack", "energy", "combat"],
            ["combat"],
            "discount",
            FormatDecimal(discountPerCombat),
            "Average effective energy discount given to Crossbow Attacks per combat while held.");
        DescribedIconRow(
            sb,
            ["attack_common"],
            [],
            "gained",
            agg.CrossbowCommonAttacksGained.ToString(),
            "Common Attacks gained from Crossbow.");
        DescribedIconRow(
            sb,
            ["attack_uncommon"],
            [],
            "gained",
            agg.CrossbowUncommonAttacksGained.ToString(),
            "Uncommon Attacks gained from Crossbow.");
        DescribedIconRow(
            sb,
            ["attack_rare"],
            [],
            "gained",
            agg.CrossbowRareAttacksGained.ToString(),
            "Rare Attacks gained from Crossbow.");
        return sb.ToString();
    }

    private static string BuildIntimidatingHelmetBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var blockPerTurn = agg.IntimidatingHelmetTurns <= 0
            ? 0m
            : (decimal)agg.AdditionalBlockGained / agg.IntimidatingHelmetTurns;
        var blockPerCombat = agg.IntimidatingHelmetCombats <= 0
            ? 0m
            : (decimal)agg.AdditionalBlockGained / agg.IntimidatingHelmetCombats;

        Row3(sb, "Cards played costing 2+", agg.Activations.ToString(), "");
        Row3(sb, BlockLabel("block gained"), agg.AdditionalBlockGained.ToString(), "");
        Row3(sb, BlockLabel("avg block per turn"), FormatDecimal(blockPerTurn), "");
        Row3(sb, BlockLabel("avg block per combat"), FormatDecimal(blockPerCombat), "");
        return sb.ToString();
    }

    private static string BuildDaughterOfTheWindBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var blockPerTurn = agg.DaughterOfTheWindTurns <= 0
            ? 0m
            : (decimal)agg.AdditionalBlockGained / agg.DaughterOfTheWindTurns;
        var blockPerCombat = agg.DaughterOfTheWindCombats <= 0
            ? 0m
            : (decimal)agg.AdditionalBlockGained / agg.DaughterOfTheWindCombats;

        Row3(sb, BlockLabel("Total block gained"), agg.AdditionalBlockGained.ToString(), "");
        Row3(sb, BlockLabel("Avg block gained per turn"), FormatDecimal(blockPerTurn), "");
        Row3(sb, BlockLabel("Avg block gained per combat"), FormatDecimal(blockPerCombat), "");
        return sb.ToString();
    }

    private static string BuildSturdyClampBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var blockRetainedPerTurn = agg.SturdyClampTurns <= 0
            ? 0m
            : (decimal)agg.SturdyClampBlockRetained / agg.SturdyClampTurns;
        var blockRetainedPerCombat = agg.SturdyClampCombats <= 0
            ? 0m
            : (decimal)agg.SturdyClampBlockRetained / agg.SturdyClampCombats;
        var excessBlockPerTurn = agg.SturdyClampTurns <= 0
            ? 0m
            : (decimal)agg.SturdyClampExcessBlockOverTen / agg.SturdyClampTurns;
        var excessBlockPerCombat = agg.SturdyClampCombats <= 0
            ? 0m
            : (decimal)agg.SturdyClampExcessBlockOverTen / agg.SturdyClampCombats;

        DescribedIconRow(
            sb,
            ["average", "block", "turn"],
            ["turn"],
            "retained",
            FormatDecimal(blockRetainedPerTurn),
            "Average block retained by Sturdy Clamp per turn.");
        DescribedIconRow(
            sb,
            ["average", "block", "combat"],
            ["combat"],
            "retained",
            FormatDecimal(blockRetainedPerCombat),
            "Average block retained by Sturdy Clamp per combat.");
        DescribedIconRow(
            sb,
            ["average", "block_wasted", "turn"],
            ["turn"],
            "excess over 10",
            FormatDecimal(excessBlockPerTurn),
            "Average block discarded above Sturdy Clamp's 10-block retention cap per turn.");
        DescribedIconRow(
            sb,
            ["average", "block_wasted", "combat"],
            ["combat"],
            "excess over 10",
            FormatDecimal(excessBlockPerCombat),
            "Average block discarded above Sturdy Clamp's 10-block retention cap per combat.");
        return sb.ToString();
    }

    private static string BuildCursedPearlBodyBBCode(RelicAggregate agg, CardAggregate curseAgg)
    {
        var sb = new StringBuilder();
        Row3(
            sb,
            "Floors ascended before first shop",
            (agg.FloorsAscendedBeforeFirstShop ?? 0).ToString(),
            "");
        AppendRelatedCurseCardStats(sb, "Greed", curseAgg, includePlayed: false);
        return sb.ToString();
    }

    private static string BuildNeowsBonesBodyBBCode(
        RelicAggregate agg,
        IReadOnlyDictionary<string, CardAggregate>? curseAggregates)
    {
        var sb = new StringBuilder();
        var relics = agg.RelicsGranted.Values
            .Where(relic => relic.Count > 0)
            .OrderByDescending(relic => relic.Count)
            .ThenBy(relic => relic.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var relicTotal = relics.Sum(relic => Math.Max(0, relic.Count));

        Row3(sb, "Neow relics obtained", relicTotal.ToString(), "");
        foreach (var relic in relics)
        {
            var displayName = StatsTooltip.EscapeBbcode(string.IsNullOrWhiteSpace(relic.DisplayName)
                ? RunTracker.FormatRelicIdForDisplay(relic.RelicId)
                : relic.DisplayName);
            var value = relic.Count == 1 ? displayName : $"{displayName} x{relic.Count}";
            TextValueRow(sb, "Neow relic", value, "");
        }

        var curses = agg.CardsGranted.Values
            .Where(card => card.Count > 0)
            .OrderByDescending(card => card.Count)
            .ThenBy(card => card.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var curseTotal = curses.Sum(card => Math.Max(0, card.Count));

        Row3(sb, "Curses added", curseTotal.ToString(), "");
        if (curses.Count == 0)
        {
            AppendRelatedCurseCardStats(sb, "Curse", new CardAggregate());
            return sb.ToString();
        }

        foreach (var curse in curses)
        {
            var displayName = StatsTooltip.EscapeBbcode(string.IsNullOrWhiteSpace(curse.DisplayName)
                ? RunTracker.FormatCardIdForDisplay(curse.CardId)
                : curse.DisplayName);
            var value = curse.Count == 1 ? displayName : $"{displayName} x{curse.Count}";
            TextValueRow(sb, "Curse added", value, "");

            CardAggregate? curseAgg = null;
            curseAggregates?.TryGetValue(curse.CardId, out curseAgg);
            AppendRelatedCurseCardStats(sb, displayName, curseAgg ?? new CardAggregate());
        }

        return sb.ToString();
    }

    private static void AppendRelatedCurseCardStats(
        StringBuilder sb,
        string displayName,
        CardAggregate curseAgg,
        bool includePlayed = true)
    {
        curseAgg ??= new CardAggregate();
        Row3(sb, $"{displayName} combats", curseAgg.CombatsInDeck.ToString(), "");
        Row3(sb, $"{displayName} drawn", curseAgg.TimesDrawn.ToString(), "");
        Row3(sb, $"{displayName} discarded", curseAgg.TimesDiscarded.ToString(), "");
        if (includePlayed)
            Row3(sb, $"{displayName} played", curseAgg.Plays.ToString(), "");
        Row3(sb, $"{displayName} exhausted", curseAgg.TimesExhausted.ToString(), "");
    }

    private static IReadOnlyDictionary<string, CardAggregate> BuildGrantedCurseAggregates(
        RelicAggregate agg,
        Func<string, CardAggregate> aggregateForDefinition)
    {
        var result = new Dictionary<string, CardAggregate>(StringComparer.Ordinal);
        foreach (var card in agg.CardsGranted.Values)
        {
            if (card.Count <= 0 || string.IsNullOrWhiteSpace(card.CardId)) continue;
            result[card.CardId] = aggregateForDefinition(card.CardId) ?? new CardAggregate();
        }

        return result;
    }

    private static string BuildCloakClaspBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var blockPerTurn = agg.CloakClaspTurns <= 0
            ? 0m
            : (decimal)agg.AdditionalBlockGained / agg.CloakClaspTurns;
        var blockPerCombat = agg.CloakClaspCombats <= 0
            ? 0m
            : (decimal)agg.AdditionalBlockGained / agg.CloakClaspCombats;

        Row3(sb, BlockLabel("Block gained"), agg.AdditionalBlockGained.ToString(), "");
        Row3(sb, BlockLabel("avg block gained per turn"), FormatDecimal(blockPerTurn), "");
        Row3(sb, BlockLabel("avg block gained per combat"), FormatDecimal(blockPerCombat), "");
        return sb.ToString();
    }

    private static string BuildReptileTrinketBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var activationsPerTurn = agg.ReptileTrinketTurns <= 0
            ? 0m
            : (decimal)agg.Activations / agg.ReptileTrinketTurns;
        var activationsPerCombat = agg.ReptileTrinketCombats <= 0
            ? 0m
            : (decimal)agg.Activations / agg.ReptileTrinketCombats;

        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, "Strength added", FormatDecimal(agg.StrengthAdded), "");
        Row3(sb, "Avg activations per turn", FormatDecimal(activationsPerTurn), "");
        Row3(sb, "Avg activations per combat", FormatDecimal(activationsPerCombat), "");
        Row3(
            sb,
            "Turns with exactly 2 activations",
            agg.ReptileTrinketTurnsWithExactlyTwoActivations.ToString(),
            "");
        Row3(
            sb,
            "Turns with more than 2 activations",
            agg.ReptileTrinketTurnsWithMoreThanTwoActivations.ToString(),
            "");
        return sb.ToString();
    }

    private static string BuildRainbowRingBodyBBCode(
        RelicAggregate agg,
        bool attackPlayedThisTurn,
        bool powerPlayedThisTurn,
        bool skillPlayedThisTurn)
    {
        var sb = new StringBuilder();
        var activationsPerTurn = agg.RainbowRingTurns <= 0
            ? 0m
            : (decimal)agg.Activations / agg.RainbowRingTurns;
        var activationsPerCombat = agg.RainbowRingCombats <= 0
            ? 0m
            : (decimal)agg.Activations / agg.RainbowRingCombats;

        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, "Avg activations per turn", FormatDecimal(activationsPerTurn), "");
        Row3(sb, "Avg activations per combat", FormatDecimal(activationsPerCombat), "");
        Row3(sb, "Attack played this turn", FormatBoolean(attackPlayedThisTurn), "");
        Row3(sb, "Power played this turn", FormatBoolean(powerPlayedThisTurn), "");
        Row3(sb, "Skill played this turn", FormatBoolean(skillPlayedThisTurn), "");
        return sb.ToString();
    }

    private static string BuildSparklingRougeBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(
            sb,
            "Combats ended on turn 1",
            agg.SparklingRougeCombatsEndedOnTurn1.ToString(),
            "");
        Row3(
            sb,
            "Combats ended on turn 2",
            agg.SparklingRougeCombatsEndedOnTurn2.ToString(),
            "");
        Row3(
            sb,
            "Combats ended on turn 3+",
            agg.SparklingRougeCombatsEndedOnTurn3Plus.ToString(),
            "");
        return sb.ToString();
    }

    private static (
        bool AttackPlayed,
        bool PowerPlayed,
        bool SkillPlayed) GetRainbowRingTurnState(RainbowRing relic)
    {
        try
        {
            return (
                ReadPositiveInt(RainbowRingAttacksPlayedThisTurnField, relic),
                ReadPositiveInt(RainbowRingPowersPlayedThisTurnField, relic),
                ReadPositiveInt(RainbowRingSkillsPlayedThisTurnField, relic));
        }
        catch
        {
            return (false, false, false);
        }
    }

    private static bool ReadPositiveInt(
        System.Reflection.FieldInfo? field,
        object instance)
        => field?.GetValue(instance) is int value && value > 0;

    private static string FormatBoolean(bool value)
        => value ? "true" : "false";

    private static string BuildGorgetBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, "Plating added", FormatDecimal(agg.PlatingAdded), "");
        return sb.ToString();
    }

    private static string BuildBeatingRemnantBodyBBCode(RelicAggregate agg)
    {
        var preventedPerTurn = agg.BeatingRemnantTurns <= 0
            ? 0m
            : agg.BeatingRemnantHpLossPrevented / agg.BeatingRemnantTurns;
        var preventedPerCombat = agg.BeatingRemnantCombats <= 0
            ? 0m
            : agg.BeatingRemnantHpLossPrevented / agg.BeatingRemnantCombats;

        var sb = new StringBuilder();
        Row3(
            sb,
            "HP loss prevented",
            FormatDecimal(agg.BeatingRemnantHpLossPrevented),
            "");
        Row3(
            sb,
            "Avg HP loss prevented per turn",
            FormatDecimal(preventedPerTurn),
            "");
        Row3(
            sb,
            "Avg HP loss prevented per combat",
            FormatDecimal(preventedPerCombat),
            "");
        return sb.ToString();
    }

    private static string BuildWhisperingEarringBodyBBCode(RelicAggregate agg)
    {
        var lifeLostPerCombat = agg.WhisperingEarringCombats <= 0
            ? 0m
            : agg.WhisperingEarringFirstRoundHpLost / agg.WhisperingEarringCombats;

        var sb = new StringBuilder();
        Row3(
            sb,
            "Total life lost, player's first turn through opponent's first turn",
            FormatDecimal(agg.WhisperingEarringFirstRoundHpLost),
            "");
        Row3(
            sb,
            "Avg life lost, player's first turn through opponent's first turn per combat",
            FormatDecimal(lifeLostPerCombat),
            "");
        return sb.ToString();
    }

    private static string BuildTungstenRodBodyBBCode(RelicAggregate agg)
    {
        var turns = agg.TungstenRodTurns;
        var combats = agg.TungstenRodCombats;
        var sb = new StringBuilder();

        AddPreventionRows(
            "Damage prevented",
            "Lost life prevented",
            agg.TungstenRodDamagePrevented,
            turns,
            combats,
            "HP loss prevented by Tungsten Rod.",
            "Average HP loss prevented by Tungsten Rod per player turn while it was held.",
            "Average HP loss prevented by Tungsten Rod per combat while it was held.");
        AddPreventionRows(
            "Self-inflicted lost life prevented",
            "Self-inflicted lost life prevented",
            agg.TungstenRodSelfDamagePrevented,
            turns,
            combats,
            "HP loss prevented from the player's own non-Curse, non-Status cards and Buff powers.",
            "Average self-inflicted HP loss prevented per player turn while Tungsten Rod was held.",
            "Average self-inflicted HP loss prevented per combat while Tungsten Rod was held.");
        AddPreventionRows(
            "Curse-inflicted lost life prevented",
            "Curse-inflicted lost life prevented",
            agg.TungstenRodCurseDamagePrevented,
            turns,
            combats,
            "HP loss prevented from direct Curse-card damage.",
            "Average Curse-inflicted HP loss prevented per player turn while Tungsten Rod was held.",
            "Average Curse-inflicted HP loss prevented per combat while Tungsten Rod was held.");
        AddPreventionRows(
            "Status-inflicted lost life prevented",
            "Status-inflicted lost life prevented",
            agg.TungstenRodStatusDamagePrevented,
            turns,
            combats,
            "HP loss prevented from direct Status-card damage.",
            "Average Status-inflicted HP loss prevented per player turn while Tungsten Rod was held.",
            "Average Status-inflicted HP loss prevented per combat while Tungsten Rod was held.");
        AddPreventionRows(
            "Enemy-source lost life prevented",
            "Enemy-source lost life prevented",
            agg.TungstenRodEnemyDamagePrevented,
            turns,
            combats,
            "HP loss prevented from enemy creatures and Debuff powers.",
            "Average enemy-source HP loss prevented per player turn while Tungsten Rod was held.",
            "Average enemy-source HP loss prevented per combat while Tungsten Rod was held.");

        return sb.ToString();

        void AddPreventionRows(
            string totalLabel,
            string averageLabel,
            decimal total,
            int turnCount,
            int combatCount,
            string totalDescription,
            string turnDescription,
            string combatDescription)
        {
            var perTurn = turnCount <= 0 ? 0m : total / turnCount;
            var perCombat = combatCount <= 0 ? 0m : total / combatCount;

            Row3(sb, totalLabel, FormatDecimal(total), "", totalDescription);
            Row3(
                sb,
                $"Avg {LowercaseFirst(averageLabel)} per turn",
                FormatDecimal(perTurn),
                "",
                turnDescription);
            Row3(
                sb,
                $"Avg {LowercaseFirst(averageLabel)} per combat",
                FormatDecimal(perCombat),
                "",
                combatDescription);
        }
    }

    private static string LowercaseFirst(string value)
        => string.IsNullOrEmpty(value)
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];

    private static string BuildStoneCrackerBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var upgradedPlaysPerTurn = agg.StoneCrackerTurns <= 0
            ? 0m
            : (decimal)agg.StoneCrackerUpgradedCardPlays / agg.StoneCrackerTurns;
        var upgradedPlaysPerCombat = agg.StoneCrackerCombats <= 0
            ? 0m
            : (decimal)agg.StoneCrackerUpgradedCardPlays / agg.StoneCrackerCombats;

        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, "Cards upgraded", agg.CardsUpgraded.ToString(), "");
        Row3(sb, "Upgraded commons", agg.StoneCrackerUpgradedCommons.ToString(), "");
        Row3(sb, "Upgraded uncommons", agg.StoneCrackerUpgradedUncommons.ToString(), "");
        Row3(sb, "Upgraded rares", agg.StoneCrackerUpgradedRares.ToString(), "");
        Row3(
            sb,
            "Cards played upgraded",
            agg.StoneCrackerUpgradedCardPlays.ToString(),
            "",
            "Cards upgraded by Stone Cracker that were played.");
        Row3(
            sb,
            "Avg cards played upgraded per turn",
            FormatDecimal(upgradedPlaysPerTurn),
            "",
            "Average number of cards upgraded by Stone Cracker that were played per turn.");
        Row3(
            sb,
            "Avg cards played upgraded per combat",
            FormatDecimal(upgradedPlaysPerCombat),
            "",
            "Average number of cards upgraded by Stone Cracker that were played per combat.");
        return sb.ToString();
    }

    private static string BuildRazorToothBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var upgradedPerTurn = agg.RazorToothTurns <= 0
            ? 0m
            : (decimal)agg.CardsUpgraded / agg.RazorToothTurns;
        var upgradedPerCombat = agg.RazorToothCombats <= 0
            ? 0m
            : (decimal)agg.CardsUpgraded / agg.RazorToothCombats;
        var upgradedPlaysPerTurn = agg.RazorToothTurns <= 0
            ? 0m
            : (decimal)agg.RazorToothUpgradedCardPlays / agg.RazorToothTurns;
        var upgradedPlaysPerCombat = agg.RazorToothCombats <= 0
            ? 0m
            : (decimal)agg.RazorToothUpgradedCardPlays / agg.RazorToothCombats;
        var upgradedDrawsPerTurn = agg.RazorToothTurns <= 0
            ? 0m
            : (decimal)agg.RazorToothUpgradedCardDraws / agg.RazorToothTurns;
        var upgradedDrawsPerCombat = agg.RazorToothCombats <= 0
            ? 0m
            : (decimal)agg.RazorToothUpgradedCardDraws / agg.RazorToothCombats;

        Row3(sb, "Cards upgraded", agg.CardsUpgraded.ToString(), "");
        Row3(sb, "Avg cards upgraded/turn", FormatDecimal(upgradedPerTurn), "");
        Row3(sb, "Avg cards upgraded/combat", FormatDecimal(upgradedPerCombat), "");
        Row3(sb, "Upgraded-card plays", agg.RazorToothUpgradedCardPlays.ToString(), "");
        Row3(sb, "Avg upgraded plays/turn", FormatDecimal(upgradedPlaysPerTurn), "");
        Row3(sb, "Avg upgraded plays/combat", FormatDecimal(upgradedPlaysPerCombat), "");
        Row3(sb, "Upgraded-card draws", agg.RazorToothUpgradedCardDraws.ToString(), "");
        Row3(sb, "Avg upgraded draws/turn", FormatDecimal(upgradedDrawsPerTurn), "");
        Row3(sb, "Avg upgraded draws/combat", FormatDecimal(upgradedDrawsPerCombat), "");
        return sb.ToString();
    }

    private static string BuildWarHammerBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var cardsPerActivation = agg.Activations <= 0
            ? 0m
            : (decimal)agg.CardsUpgraded / agg.Activations;
        var upgradedPlaysPerTurn = agg.WarHammerTurns <= 0
            ? 0m
            : (decimal)agg.WarHammerUpgradedCardPlays / agg.WarHammerTurns;
        var upgradedPlaysPerCombat = agg.WarHammerCombats <= 0
            ? 0m
            : (decimal)agg.WarHammerUpgradedCardPlays / agg.WarHammerCombats;

        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, "Cards upgraded", agg.CardsUpgraded.ToString(), "");
        Row3(sb, "Avg cards upgraded/activation", FormatDecimal(cardsPerActivation), "");
        foreach (var card in (agg.UpgradedCards ?? new List<string>())
                     .Where(card => !string.IsNullOrWhiteSpace(card)))
        {
            TextValueRow(sb, "Upgraded card", StatsTooltip.EscapeBbcode(card), "");
        }
        Row3(sb, "Upgraded-card plays", agg.WarHammerUpgradedCardPlays.ToString(), "");
        Row3(sb, "Avg upgraded plays/turn", FormatDecimal(upgradedPlaysPerTurn), "");
        Row3(sb, "Avg upgraded plays/combat", FormatDecimal(upgradedPlaysPerCombat), "");
        return sb.ToString();
    }

    private static string BuildSandCastleBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendUpgradedCardStats(sb, agg);
        return sb.ToString();
    }

    private static string BuildWhetstoneBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendUpgradedCardStats(
            sb,
            agg,
            totalLabel: "Attacks upgraded",
            itemLabel: "Upgraded attack");
        return sb.ToString();
    }

    private static string BuildYummyCookieBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendUpgradedCardStats(sb, agg);
        return sb.ToString();
    }

    private static string BuildGnarledHammerBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var cards = (agg.SharpEnchantedCards ?? new List<string>())
            .Where(card => !string.IsNullOrWhiteSpace(card))
            .ToList();

        Row3(sb, "Cards enchanted with Sharp", cards.Count.ToString(), "");
        foreach (var card in cards)
            TextValueRow(sb, "Sharp-enchanted card", StatsTooltip.EscapeBbcode(card), "");
        return sb.ToString();
    }

    private static string BuildSilkenTressBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var cards = (agg.SilkenTressGlamCards ?? new List<string>())
            .Where(card => !string.IsNullOrWhiteSpace(card))
            .ToList();

        if (cards.Count == 0)
        {
            SilkenTressGlamCardRow(sb, "none");
            return sb.ToString();
        }

        foreach (var card in cards)
            SilkenTressGlamCardRow(sb, StatsTooltip.EscapeBbcode(card));

        return sb.ToString();
    }

    private static void SilkenTressGlamCardRow(
        StringBuilder sb,
        string value)
    {
        var iconExpression =
            $"[b]+[/b] {StatConceptGlossary.RenderHintedGlyph("glam")}";
        DescribedIconFlowRow(
            sb,
            new[] { "card" },
            Array.Empty<string>(),
            iconExpression,
            value,
            "The card successfully taken after Silken Tress applied Glam.");
    }

    private static string BuildTriBoomerangBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var cards = (agg.TriBoomerangInstinctCards
                ?? new List<RelicEnchantedCardAggregate>())
            .Where(card =>
                card != null
                && !string.IsNullOrWhiteSpace(card.CardInstanceId))
            .ToList();
        decimal playsPerCombat = agg.TriBoomerangCombats > 0
            ? (decimal)agg.TriBoomerangInstinctCardPlays
                / agg.TriBoomerangCombats
            : 0m;

        Row3(sb, "Cards enchanted with Instinct", cards.Count.ToString(), "");
        foreach (var card in cards)
        {
            var displayName = string.IsNullOrWhiteSpace(card.DisplayName)
                ? card.CardInstanceId
                : card.DisplayName;
            TextValueRow(
                sb,
                "Instinct-enchanted card",
                StatsTooltip.EscapeBbcode(displayName),
                "");
        }
        Row3(
            sb,
            "Times Instinct cards were played",
            agg.TriBoomerangInstinctCardPlays.ToString(),
            "");
        Row3(
            sb,
            "Avg Instinct-card plays per combat",
            FormatDecimal(playsPerCombat),
            "");
        return sb.ToString();
    }

    private static string BuildWarPaintBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendUpgradedCardStats(
            sb,
            agg,
            totalLabel: "Skills upgraded",
            itemLabel: "Upgraded skill");
        return sb.ToString();
    }

    private static string BuildFragrantMushroomBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        DescribedIconRow(
            sb,
            ["hp"],
            [],
            "starting",
            FormatDecimal(Math.Max(0m, agg.StartingHp ?? 0m)),
            "Starting HP — current HP immediately before Fragrant Mushroom dealt its pickup damage.");
        DescribedIconRow(
            sb,
            ["hp"],
            [],
            "resulting",
            FormatDecimal(Math.Max(0m, agg.ResultingHp ?? 0m)),
            "Resulting HP — current HP after Fragrant Mushroom's pickup damage resolved.");
        AppendUpgradedCardStats(sb, agg);
        return sb.ToString();
    }

    private static void AppendUpgradedCardStats(
        StringBuilder sb,
        RelicAggregate agg,
        string totalLabel = "Cards upgraded",
        string itemLabel = "Upgraded card")
    {
        var upgradedCards = (agg.UpgradedCards ?? new System.Collections.Generic.List<string>())
            .Where(card => !string.IsNullOrWhiteSpace(card))
            .ToList();

        Row3(sb, totalLabel, agg.CardsUpgraded.ToString(), "");
        foreach (var card in upgradedCards)
            TextValueRow(sb, itemLabel, StatsTooltip.EscapeBbcode(card), "");
    }

    private static string BuildMealTicketBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        AppendHealingStats(sb, agg);
        return sb.ToString();
    }

    private static string BuildMeatOnTheBoneBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        // The short-lived previous shape stored a positive magnitude below
        // half. Fold it in as a negative signed value so existing runs keep
        // their captured samples under the corrected presentation.
        var relativeToHalfSamples =
            agg.MeatOnTheBonePreTriggerHpRelativeToHalfSamples
            + agg.MeatOnTheBonePreTriggerHpBelowHalfSamples;
        var relativeToHalfTotal =
            agg.MeatOnTheBonePreTriggerHpRelativeToHalfTotal
            - agg.MeatOnTheBonePreTriggerHpBelowHalfTotal;
        var averageHpRelativeToHalf = relativeToHalfSamples <= 0
            ? 0m
            : relativeToHalfTotal / relativeToHalfSamples;
        var averageHpPercent = agg.MeatOnTheBonePreTriggerHpSamples <= 0
            ? 0m
            : agg.MeatOnTheBonePreTriggerHpPercentTotal
                / agg.MeatOnTheBonePreTriggerHpSamples;
        var averageHpPercentRelativeToHalf = agg.MeatOnTheBonePreTriggerHpSamples <= 0
            ? 0m
            : averageHpPercent - 50m;

        RelicActivationRow(sb, agg.Activations.ToString());
        AppendHealingStats(sb, agg);
        Row3(
            sb,
            "Avg HP relative to 50% before trigger",
            FormatSignedDecimal(averageHpRelativeToHalf),
            "",
            "Average raw HP-point difference from 50% of maximum HP at combat end, immediately before Meat on the Bone triggered. Negative is below 50%; positive is above 50%.");
        Row3(
            sb,
            "Avg HP% relative to 50% before trigger",
            $"{FormatSignedDecimal(averageHpPercentRelativeToHalf)}%",
            "",
            "Average HP percentage-point difference from 50% of maximum HP at combat end, immediately before Meat on the Bone triggered. Negative is below 50%; positive is above 50%.");
        return sb.ToString();
    }

    private static string BuildPlanisphereBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        AppendHealingStats(sb, agg);
        return sb.ToString();
    }

    private static string BuildLizardTailBodyBBCode(RelicAggregate agg, int? floorAcquiredFallback = null)
    {
        var sb = new StringBuilder();
        Row3(sb, "Floor acquired", FormatFloor(agg.FloorAcquired ?? floorAcquiredFallback), "");
        Row3(
            sb,
            "Floor activated",
            agg.FloorActivated.HasValue ? FormatFloor(agg.FloorActivated) : "none yet",
            "");
        Row3(sb, "HP healed", FormatDecimal(agg.TotalHealingRestored), "");
        return sb.ToString();
    }

    private static string BuildPantographBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        AppendHealingStats(sb, agg, lostLabel: "healing wasted", reasonPrefix: "wasted to");
        return sb.ToString();
    }

    private static string BuildBurningBloodBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        ConceptRow(
            sb,
            "activation",
            agg.Activations.ToString(),
            "Times Burning Blood has activated.");
        ConceptRow(
            sb,
            "healing_gained",
            FormatDecimal(agg.TotalHealingRestored),
            "Total HP restored by Burning Blood.");
        ConceptRow(
            sb,
            "healing_blocked",
            FormatDecimal(agg.TotalHealingLost),
            "Total Burning Blood healing that did not restore HP.");

        if (agg.TotalHealingLost <= 0m) return sb.ToString();

        foreach (var reason in NonRedundantHealingLostReasons(agg))
        {
            var reasonName = string.IsNullOrWhiteSpace(reason.DisplayName)
                ? "other/prevented causes"
                : StatsTooltip.EscapeBbcode(reason.DisplayName);
            DescribedIconRow(
                sb,
                ["healing_blocked"],
                [],
                $"blocked by {reasonName}",
                FormatDecimal(reason.Amount),
                $"Burning Blood healing that did not restore HP because of {reasonName}.");
        }

        return sb.ToString();
    }

    private static string BuildLeesWaffleBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendMaxHpChangeRows(sb, agg, "Max HP gained", MaxHpGained(agg));
        Row3(sb, "HP gained", FormatDecimal(agg.TotalHealingRestored), "");
        return sb.ToString();
    }

    private static string BuildFakeLeesWaffleBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        AppendHealingStats(sb, agg);
        return sb.ToString();
    }

    private static string BuildStrawberryBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        AppendMaxHpChangeRows(sb, agg, "Max HP gained", agg.MaxHpGained);
        return sb.ToString();
    }

    private static string BuildPearBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        AppendMaxHpChangeRows(sb, agg, "Max HP gained", agg.MaxHpGained);
        return sb.ToString();
    }

    private static string BuildNutritiousOysterBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        AppendMaxHpChangeRows(sb, agg, "Max HP gained", agg.MaxHpGained);
        return sb.ToString();
    }

    private static string BuildMangoBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        AppendMaxHpChangeRows(sb, agg, "Max HP gained", agg.MaxHpGained);
        return sb.ToString();
    }

    private static string BuildStoneHumidifierBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var activations = agg.MaxHpActivations
            ?? new List<RelicMaxHpActivationAggregate>();

        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            "Times triggered — the number of times this relic has activated.");
        Row3(sb, "Max HP gained", FormatDecimal(Math.Max(0m, agg.MaxHpGained)), "");

        for (var index = 0; index < activations.Count; index++)
        {
            var activation = activations[index];
            if (activation == null) continue;

            var activationNumber = index + 1;
            Row3(
                sb,
                $"Activation {activationNumber} starting HP",
                FormatDecimal(Math.Max(0m, activation.StartingHp)),
                "");
            Row3(
                sb,
                $"Activation {activationNumber} resulting HP",
                FormatDecimal(Math.Max(0m, activation.ResultingHp)),
                "");
        }

        return sb.ToString();
    }

    private static string BuildChosenCheeseBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(sb, "Starting max HP", FormatDecimal(OriginalMaxHp(agg)), "");
        Row3(sb, "Max HP gained", FormatDecimal(Math.Max(0m, agg.MaxHpGained)), "");
        return sb.ToString();
    }

    private static string BuildDarkstonePeriaptBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(sb, "Curses acquired", agg.CursesAcquired.ToString(), "");
        AppendMaxHpChangeRows(sb, agg, "Max HP gained", agg.TotalMaxHpGained);
        return sb.ToString();
    }

    private static string BuildLuckyFyshBodyBBCode(
        RelicAggregate agg,
        int? currentFloor = null,
        int? floorAcquiredFallback = null)
    {
        var sb = new StringBuilder();
        var floorAcquired = agg.FloorAcquired ?? floorAcquiredFallback;
        var floorsHeld = currentFloor.HasValue && floorAcquired.HasValue
            ? Math.Max(1, currentFloor.Value - floorAcquired.Value + 1)
            : Math.Max(1, currentFloor ?? 1);

        Row3(sb, "Gold gained", agg.GoldGained.ToString(), "");
        Row3(sb, "Cards added to deck", agg.CardsAddedToDeck.ToString(), "");
        Row3(
            sb,
            "Curses added to deck",
            agg.CursesAddedToDeck.ToString(),
            "",
            "Curses added to deck — Curse cards among the permanent-deck additions Lucky Fysh was observed paying for.");
        AppendLuckyFyshRateRow(
            sb,
            "Avg cards added per floor",
            agg.CardsAddedToDeck,
            agg.GoldGained,
            floorsHeld,
            "Average cards added per floor, and the gold they paid out — every observed permanent-deck addition divided by the floors held since Lucky Fysh was acquired.");
        AppendLuckyFyshRateRow(
            sb,
            "Avg cards added per combat",
            agg.LuckyFyshCardsAddedInCombats,
            agg.LuckyFyshGoldGainedInCombats,
            agg.LuckyFyshCombatsHeld,
            "Average cards added per combat, and the gold they paid out — additions observed in Monster, Elite, and Boss rooms divided by those rooms entered while Lucky Fysh was held.");
        AppendLuckyFyshRateRow(
            sb,
            "Avg cards added per shop",
            agg.LuckyFyshCardsAddedInShops,
            agg.LuckyFyshGoldGainedInShops,
            agg.LuckyFyshShopsHeld,
            "Average cards added per merchant, and the gold they paid out — additions observed in merchant rooms divided by the merchants entered while Lucky Fysh was held.");
        AppendLuckyFyshRateRow(
            sb,
            "Avg cards added per event",
            agg.LuckyFyshCardsAddedInEvents,
            agg.LuckyFyshGoldGainedInEvents,
            agg.LuckyFyshEventsHeld,
            "Average cards added per event, and the gold they paid out — additions observed in event rooms divided by the events entered while Lucky Fysh was held.");
        AppendLuckyFyshRateRow(
            sb,
            "Avg cards added per campfire",
            agg.LuckyFyshCardsAddedInCampfires,
            agg.LuckyFyshGoldGainedInCampfires,
            agg.LuckyFyshCampfiresHeld,
            "Average cards added per campfire, and the gold they paid out — additions observed at rest sites divided by the rest sites entered while Lucky Fysh was held.");
        return sb.ToString();
    }

    /// <summary>
    /// One Lucky Fysh rate, split across the row's two value columns: cards
    /// added in the primary column, the gold those additions paid out beside
    /// it. Both halves share a denominator, so the pair reads as one rate
    /// rather than two stats that happen to be adjacent. A denominator of zero
    /// leaves the rate undefined rather than reporting a zero the player could
    /// read as a measured result.
    /// </summary>
    private static void AppendLuckyFyshRateRow(
        StringBuilder sb,
        string label,
        int cardsAdded,
        int goldGained,
        int denominator,
        string fullDescription)
    {
        if (denominator <= 0)
        {
            Row3(sb, label, "not yet", "", fullDescription);
            return;
        }

        var goldIcon = StatConceptGlossary.RenderHintedGlyph("gold");
        Row3(
            sb,
            label,
            FormatDecimal((decimal)cardsAdded / denominator),
            $"{goldIcon} {FormatDecimal((decimal)goldGained / denominator)}",
            fullDescription);
    }

    private static string BuildBowlerHatBodyBBCode(
        RelicAggregate agg,
        int? currentFloor,
        int? floorAcquiredFallback = null)
    {
        var sb = new StringBuilder();
        var averageExtraGold = agg.Activations <= 0
            ? 0m
            : (decimal)agg.GoldGained / agg.Activations;
        var floorAcquired = agg.FloorAcquired ?? floorAcquiredFallback;
        var floorsHeld = currentFloor.HasValue && floorAcquired.HasValue
            ? Math.Max(1, currentFloor.Value - floorAcquired.Value + 1)
            : Math.Max(1, currentFloor ?? 1);
        var extraGoldPerFloor = (decimal)agg.GoldGained / floorsHeld;

        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            "Activations — positive gold grants where Bowler Hat's 25% increase added at least one gold after integer truncation.");
        Row3(
            sb,
            "Extra gold gained",
            agg.GoldGained.ToString(),
            "",
            "Extra gold gained — gold that actually reached the player beyond each grant's unmodified integer amount.");
        Row3(
            sb,
            "Avg extra gold/activation",
            FormatDecimal(averageExtraGold),
            "",
            "Average extra gold per activation — observed Bowler Hat bonus gold divided by successful positive-integer bonuses.");
        Row3(
            sb,
            "Avg extra gold/floor",
            FormatDecimal(extraGoldPerFloor),
            "",
            "Average extra gold per floor — observed Bowler Hat bonus gold divided by the floors held since Bowler Hat was acquired.");
        return sb.ToString();
    }

    private static string BuildAmethystAubergineBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            "Times triggered — successful Amethyst Aubergine reward additions.");
        Row3(
            sb,
            "Extra gold received",
            agg.GoldGained.ToString(),
            "",
            "Extra gold received — the total amount on Gold rewards added by Amethyst Aubergine.");
        return sb.ToString();
    }

    private static string BuildWongosMysteryTicketBodyBBCode(
        RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var floorsBeforeActivation =
            agg.FloorAcquired.HasValue && agg.FloorActivated.HasValue
                ? Math.Max(
                    0,
                    agg.FloorActivated.Value - agg.FloorAcquired.Value)
                    .ToString()
                : "not yet";

        Row3(
            sb,
            "Floors ascended before activating",
            floorsBeforeActivation,
            "",
            "Floors ascended before activating — the distance from receiving Wongo's Mystery Ticket to the combat reward where it activated.");

        var relics = agg.RelicsGranted.Values
            .Where(relic => relic.Count > 0)
            .OrderByDescending(relic => relic.Count)
            .ThenBy(
                relic => relic.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
        var total = relics.Sum(relic => Math.Max(0, relic.Count));
        Row3(
            sb,
            "Relics received",
            total.ToString(),
            "",
            "Relics received — relics successfully claimed from Wongo's Mystery Ticket rewards.");

        foreach (var relic in relics)
        {
            var displayName = StatsTooltip.EscapeBbcode(
                string.IsNullOrWhiteSpace(relic.DisplayName)
                    ? RunTracker.FormatRelicIdForDisplay(relic.RelicId)
                    : relic.DisplayName);
            var value = relic.Count == 1
                ? displayName
                : $"{displayName} x{relic.Count}";
            TextValueRow(sb, "Relic received", value, "");
        }

        return sb.ToString();
    }

    private static string BuildMawBankBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            "Activations — completed room entries where Maw Bank was still active.");
        Row3(
            sb,
            "Gold gained",
            agg.GoldGained.ToString(),
            "",
            "Gold gained — the actual gold added by Maw Bank across its completed activations.");
        Row3(
            sb,
            "Shops skipped",
            agg.MawBankShopsSkipped.ToString(),
            "",
            "Shops skipped — shops entered while Maw Bank was active and left without spending gold.");
        Row3(
            sb,
            "Gold spent outside shops",
            agg.MawBankGoldSpentOutsideShops.ToString(),
            "",
            "Gold spent outside shops — actual gold spent while Maw Bank was active and the current room was not a shop.");
        return sb.ToString();
    }

    private static string BuildOldCoinBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(
            sb,
            "Granted gold spent",
            $"{agg.OldCoinGoldSpent}/{agg.OldCoinGoldGranted}",
            "",
            "Granted gold spent — how much of Old Coin's observed gold grant was later consumed by purchases.");
        return sb.ToString();
    }

    private static string BuildMembershipCardBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            "Activations — completed shop purchases whose price Membership Card actually reduced.");
        Row3(
            sb,
            "Gold saved",
            agg.MembershipCardGoldSaved.ToString(),
            "",
            "Gold saved — what those purchases would have cost with only Membership Card's discount removed, minus the gold actually spent. Another price modifier keeps its own share.");
        Row3(
            sb,
            "Gold held after purchase",
            agg.MembershipCardGoldHeldAfterPurchase?.ToString() ?? "not recorded",
            "",
            "Gold held after purchase — the balance remaining the moment Membership Card was obtained, after paying for it.");
        Row3(
            sb,
            "Gold earned after purchase",
            agg.MembershipCardGoldEarnedAfterPurchase.ToString(),
            "",
            "Gold earned after purchase — every gold gain observed since Membership Card was obtained, while it was still held.");
        return sb.ToString();
    }

    private static string BuildGoldenPearlBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(
            sb,
            "Floors before first gold expense",
            agg.FloorsBeforeFirstGoldExpense?.ToString() ?? "not yet",
            "",
            "Floors before first gold expense — floors traveled from receiving Golden Pearl to the first positive gold loss classified by the game as spending; lost or stolen gold does not count.");
        return sb.ToString();
    }

    private static string BuildBookOfFiveRingsBodyBBCode(
        RelicAggregate agg,
        int? currentFloor,
        int? floorAcquiredFallback = null)
    {
        var sb = new StringBuilder();
        var floorAcquired = agg.FloorAcquired ?? floorAcquiredFallback;
        var floorsHeld = currentFloor.HasValue && floorAcquired.HasValue
            ? Math.Max(1, currentFloor.Value - floorAcquired.Value + 1)
            : Math.Max(1, currentFloor ?? 1);
        var cardsPerFloor = (decimal)agg.CardsAddedToDeck / floorsHeld;

        Row3(sb, "Total cards added to deck", agg.CardsAddedToDeck.ToString(), "");
        Row3(sb, "Avg cards added per floor", FormatDecimal(cardsPerFloor), "");
        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            "Total times triggered — the number of times this relic has activated.");
        Row3(sb, "Total HP healed", FormatDecimal(agg.TotalHealingRestored), "");
        Row3(sb, "Total HP healing blocked", FormatDecimal(agg.TotalHealingLost), "");
        Row3(sb, "Card rewards skipped", agg.CardRewardsSkipped.ToString(), "");
        return sb.ToString();
    }

    private static string BuildSignetRingBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(
            sb,
            "Floors traveled until next shop reached",
            (agg.FloorsTraveledUntilNextShop ?? 0).ToString(),
            "");
        return sb.ToString();
    }

    private static string BuildWingedBootsBodyBBCode(
        RelicAggregate agg,
        int liveTimesUsed = 0)
    {
        var sb = new StringBuilder();
        var destinations = agg.WingedBootsDestinations
            ?? new List<WingedBootsDestinationAggregate>();

        for (var useNumber = 1; useNumber <= 3; useNumber++)
        {
            var destination = destinations.FirstOrDefault(
                entry => entry != null && entry.UseNumber == useNumber);
            var value = destination != null
                ? FormatWingedBootsDestinationIcon(destination.Destination)
                : useNumber <= liveTimesUsed
                    ? "not tracked"
                    : "not used yet";

            DescribedIconFlowRow(
                sb,
                ["floor"],
                [],
                string.Empty,
                value,
                WingedBootsDestinationDescription(useNumber));
        }

        return sb.ToString();
    }

    private static string FormatWingedBootsDestinationIcon(string? destination)
    {
        if (string.Equals(destination, "elite", StringComparison.Ordinal))
            return StatConceptGlossary.RenderHintedGlyph("elite");

        var iconPath = destination switch
        {
            "combat" => MapCombatIconPath,
            "shop" => MapShopIconPath,
            "question_mark" => MapUnknownIconPath,
            "treasure" => MapTreasureIconPath,
            "rest_site" => MapRestSiteIconPath,
            _ => null,
        };

        var displayName = RunTracker.FormatWingedBootsDestination(destination);
        return iconPath == null
            ? StatsTooltip.EscapeBbcode(displayName)
            : StatConceptGlossary.RenderHintedInlineImage(
                iconPath,
                $"{displayName} destination");
    }

    private static string WingedBootsDestinationDescription(int useNumber)
        => useNumber switch
        {
            1 => "The destination reached by the first use of Winged Boots.",
            2 => "The destination reached by the second use of Winged Boots.",
            3 => "The destination reached by the third use of Winged Boots.",
            _ => "A destination reached by using Winged Boots.",
        };

    private static string BuildLeafyPoulticeBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        AppendMaxHpChangeRows(sb, agg, "Max HP lost", MaxHpLost(agg));
        AppendCardTransformationRows(sb, agg, expectedCount: 2);
        return sb.ToString();
    }

    private static string BuildRegalPillowBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        AppendHealingStats(sb, agg);
        return sb.ToString();
    }

    private static string BuildPrecariousShearsBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var cardsRemoved = (agg.CardsRemoved ?? new System.Collections.Generic.List<string>())
            .Where(card => !string.IsNullOrWhiteSpace(card))
            .ToList();

        Row3(sb, "Cards removed", cardsRemoved.Count.ToString(), "");
        foreach (var card in cardsRemoved)
            TextValueRow(sb, "Removed card", StatsTooltip.EscapeBbcode(card), "");

        AppendMaxHpChangeRows(sb, agg, "Max HP lost", MaxHpLost(agg));
        return sb.ToString();
    }

    private static string BuildBloodVialBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        AppendHealingStats(sb, agg);
        return sb.ToString();
    }

    private static string BuildToolboxBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(sb, agg.Activations.ToString());
        OfferedTakenRow(
            sb,
            ["card_uncommon"],
            string.Empty,
            agg.UncommonCardsOffered,
            agg.UncommonCardsTaken,
            "Uncommon cards offered/taken from Toolbox's card choices.");
        OfferedTakenRow(
            sb,
            ["card_rare"],
            string.Empty,
            agg.RareCardsOffered,
            agg.RareCardsTaken,
            "Rare cards offered/taken from Toolbox's card choices.");
        return sb.ToString();
    }

    private static string BuildChoicesParadoxBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            "Activations — random card selection screens Choices Paradox offered on the first turn of a combat.");
        OfferedTakenRow(
            sb,
            ["card"],
            string.Empty,
            agg.CommonCardsOffered,
            agg.CommonCardsTaken,
            "Commons offered/taken — Common card options Choices Paradox put on its selection screens, and how many were taken.");
        OfferedTakenRow(
            sb,
            ["card_uncommon"],
            string.Empty,
            agg.UncommonCardsOffered,
            agg.UncommonCardsTaken,
            "Uncommons offered/taken — Uncommon card options Choices Paradox put on its selection screens, and how many were taken.");
        OfferedTakenRow(
            sb,
            ["card_rare"],
            string.Empty,
            agg.RareCardsOffered,
            agg.RareCardsTaken,
            "Rares offered/taken — Rare card options Choices Paradox put on its selection screens, and how many were taken.");
        return sb.ToString();
    }

    private static string BuildToyBoxBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var waxRelics = (agg.ToyBoxWaxRelics
                ?? new List<ToyBoxWaxRelicAggregate>())
            .Where(entry => entry != null)
            .OrderBy(entry => entry.Sequence)
            .ToList();
        var melted = waxRelics.Count(entry => entry.FloorMelted.HasValue);
        var averageFloors = CalculateToyBoxAverageFloorsToMelt(agg);

        Row3(sb, "Wax relics bestowed", waxRelics.Count.ToString(), "");
        Row3(sb, "Wax relics melted", melted.ToString(), "");
        Row3(
            sb,
            "Avg floors to melt",
            averageFloors.HasValue
                ? FormatDecimal(averageFloors.Value)
                : "—",
            "");

        foreach (var waxRelic in waxRelics)
        {
            var displayName = string.IsNullOrWhiteSpace(waxRelic.DisplayName)
                ? RunTracker.FormatRelicIdForDisplay(waxRelic.RelicId)
                : waxRelic.DisplayName;
            var icon = StatConceptGlossary.RenderHintedInlineImage(
                ResolveGrantedRelicIconPath(waxRelic.RelicId),
                displayName);
            var floorIcon = StatConceptGlossary.RenderHintedGlyph("floor");
            var bestowed = waxRelic.FloorBestowed.HasValue
                ? $"{floorIcon} {Math.Max(0, waxRelic.FloorBestowed.Value)}"
                : $"{floorIcon} not tracked";
            var outcome = waxRelic.FloorMelted.HasValue
                ? $"{bestowed} · melted {floorIcon} {Math.Max(0, waxRelic.FloorMelted.Value)}"
                : $"{bestowed} · not melted";
            DescribedIconFlowRow(
                sb,
                [],
                [],
                icon,
                outcome,
                $"{displayName} was bestowed as a wax relic by Toy Box.");
        }

        return sb.ToString();
    }

    internal static decimal? CalculateToyBoxAverageFloorsToMelt(
        RelicAggregate agg)
    {
        if (agg?.ToyBoxWaxRelics == null) return null;

        var durations = agg.ToyBoxWaxRelics
            .Where(entry => entry?.FloorBestowed.HasValue == true
                && entry.FloorMelted.HasValue)
            .Select(entry => Math.Max(
                0,
                entry.FloorMelted!.Value - entry.FloorBestowed!.Value))
            .ToList();
        return durations.Count == 0
            ? null
            : durations.Average(duration => (decimal)duration);
    }

    internal static void ApplyWaxRelicPresentation(
        RelicModel relicModel,
        RelicAggregate? toyBoxAggregate,
        ref string title,
        ref string body)
    {
        if (relicModel?.IsWax != true) return;

        try
        {
            var waxTitle = relicModel.Title.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(waxTitle))
                title = waxTitle;
        }
        catch
        {
            if (!title.StartsWith("Wax ", StringComparison.OrdinalIgnoreCase))
                title = $"Wax {title}";
        }

        var relicId = relicModel.Id.ToString();
        var fallbackBestowedFloor = RelicFloorAddedToDeck(relicModel);
        var entries = toyBoxAggregate?.ToyBoxWaxRelics
            ?? new List<ToyBoxWaxRelicAggregate>();
        var entry = entries
            .Where(candidate => candidate != null
                && string.Equals(
                    candidate.RelicId,
                    relicId,
                    StringComparison.Ordinal))
            .OrderByDescending(candidate =>
                candidate.FloorMelted.HasValue == relicModel.IsMelted)
            .ThenByDescending(candidate => candidate.Sequence)
            .FirstOrDefault();

        var floorBestowed = entry?.FloorBestowed ?? fallbackBestowedFloor;
        var floorMelted = entry?.FloorMelted;
        var floorIcon = StatConceptGlossary.RenderHintedGlyph("floor");
        var sb = new StringBuilder(body ?? string.Empty);
        Row3(
            sb,
            "Wax bestowed",
            floorBestowed.HasValue
                ? $"{floorIcon} {Math.Max(0, floorBestowed.Value)}"
                : "not tracked",
            "");
        Row3(
            sb,
            "Wax melted",
            floorMelted.HasValue
                ? $"{floorIcon} {Math.Max(0, floorMelted.Value)}"
                : relicModel.IsMelted
                    ? "not tracked"
                    : "not yet",
            "");
        if (floorBestowed.HasValue && floorMelted.HasValue)
        {
            Row3(
                sb,
                "Floors until melt",
                Math.Max(0, floorMelted.Value - floorBestowed.Value).ToString(),
                "");
        }
        body = sb.ToString();
    }

    private static string BuildWhiteStarBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            "Activations — extra rare card rewards created by White Star after Elite victories.");
        OfferedTakenRow(
            sb,
            ["card_rare"],
            string.Empty,
            agg.RareCardsOffered,
            agg.RareCardsTaken,
            "Rares offered/taken — Rare card options generated by White Star, including rerolled options, and how many of them reached the deck.");
        OfferedTakenRow(
            sb,
            ["attack_rare"],
            string.Empty,
            agg.RareAttackCardsOffered,
            agg.RareAttackCardsTaken,
            "Rare Attacks offered/taken — Rare Attack options generated by White Star, and how many were taken.");
        OfferedTakenRow(
            sb,
            ["skill_rare"],
            string.Empty,
            agg.RareSkillCardsOffered,
            agg.RareSkillCardsTaken,
            "Rare Skills offered/taken — Rare Skill options generated by White Star, and how many were taken.");
        OfferedTakenRow(
            sb,
            ["power_rare"],
            string.Empty,
            agg.RarePowerCardsOffered,
            agg.RarePowerCardsTaken,
            "Rare Powers offered/taken — Rare Power options generated by White Star, and how many were taken.");
        DescribedIconRow(
            sb,
            ["card_rare"],
            [],
            "reward screens declined",
            agg.RareCardRewardScreensDeclined.ToString(),
            "Rare card reward screens declined — White Star rewards terminally resolved without taking a Rare card.");
        return sb.ToString();
    }

    private static string BuildPrayerWheelBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        DescribedIconRow(
            sb,
            ["card"],
            [],
            "extra reward screens",
            agg.PrayerWheelExtraRewardScreens.ToString(),
            "Extra reward screens — additional card rewards created by Prayer Wheel after normal monster combats.");
        DescribedIconRow(
            sb,
            ["card"],
            [],
            "extra reward screens rejected",
            agg.PrayerWheelExtraRewardScreensRejected.ToString(),
            "Times extra reward screen rejected — Prayer Wheel rewards terminally resolved without taking a card.");
        OfferedTakenRow(
            sb,
            ["card"],
            string.Empty,
            agg.CommonCardsOffered,
            agg.CommonCardsTaken,
            "Commons offered/taken — Common card options generated by Prayer Wheel, including rerolled options, and how many were obtained.");
        OfferedTakenRow(
            sb,
            ["card_uncommon"],
            string.Empty,
            agg.UncommonCardsOffered,
            agg.UncommonCardsTaken,
            "Uncommons offered/taken — Uncommon card options generated by Prayer Wheel, including rerolled options, and how many were obtained.");
        OfferedTakenRow(
            sb,
            ["card_rare"],
            string.Empty,
            agg.RareCardsOffered,
            agg.RareCardsTaken,
            "Rares offered/taken — Rare card options generated by Prayer Wheel, including rerolled options, and how many were obtained.");
        return sb.ToString();
    }

    private static string BuildHeftyTabletBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var cardsGranted = agg.CardsGranted.Values.Sum(card => Math.Max(0, card.Count));
        DescribedIconRow(
            sb,
            ["card_rare"],
            [],
            "[b]+[/b]",
            cardsGranted.ToString(),
            "Rare cards granted by Hefty Tablet.");
        Row3(sb, "Skipped", agg.CardChoicesSkipped.ToString(), "");

        foreach (var card in agg.CardsGranted.Values
                     .Where(card => card.Count > 0)
                     .OrderByDescending(card => card.Count)
                     .ThenBy(card => card.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var displayName = StatsTooltip.EscapeBbcode(string.IsNullOrWhiteSpace(card.DisplayName)
                ? RunTracker.FormatCardIdForDisplay(card.CardId)
                : card.DisplayName);
            var value = card.Count == 1 ? displayName : $"{displayName} x{card.Count}";
            DescribedIconFlowRow(
                sb,
                ["card_rare"],
                [],
                "[b]+[/b]",
                value,
                $"The Rare card granted by Hefty Tablet: {displayName}.");
        }

        return sb.ToString();
    }

    private static string BuildArcaneScrollBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var cards = agg.CardsGranted.Values
            .Where(card => card.Count > 0)
            .OrderByDescending(card => card.Count)
            .ThenBy(card => card.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (cards.Count == 0)
        {
            Row3(sb, "Rare received", "0", "");
            return sb.ToString();
        }

        foreach (var card in cards)
        {
            var displayName = StatsTooltip.EscapeBbcode(string.IsNullOrWhiteSpace(card.DisplayName)
                ? RunTracker.FormatCardIdForDisplay(card.CardId)
                : card.DisplayName);
            var value = card.Count == 1 ? displayName : $"{displayName} x{card.Count}";
            TextValueRow(sb, "Rare received", value, "");
        }

        return sb.ToString();
    }

    private static string BuildScrollBoxesBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var cards = agg.CardsGranted.Values
            .Where(card => card.Count > 0)
            .OrderByDescending(card => card.Count)
            .ThenBy(card => card.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var cardsReceived = cards.Sum(card => Math.Max(0, card.Count));

        Row3(
            sb,
            "Bundles chosen",
            agg.Activations.ToString(),
            "",
            "Bundles chosen from Scroll Boxes.");
        Row3(
            sb,
            "Cards received",
            cardsReceived.ToString(),
            "",
            "Cards actually added to the permanent deck from the chosen bundle.");

        foreach (var card in cards)
        {
            var displayName = StatsTooltip.EscapeBbcode(string.IsNullOrWhiteSpace(card.DisplayName)
                ? RunTracker.FormatCardIdForDisplay(card.CardId)
                : card.DisplayName);
            var value = card.Count == 1 ? displayName : $"{displayName} x{card.Count}";
            TextValueRow(
                sb,
                "Card received",
                value,
                "");
        }

        return sb.ToString();
    }

    private static string BuildLargeCapsuleBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var relics = agg.RelicsGranted.Values
            .Where(relic => relic.Count > 0)
            .OrderByDescending(relic => relic.Count)
            .ThenBy(relic => relic.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var total = relics.Sum(relic => Math.Max(0, relic.Count));

        Row3(sb, "Relics obtained", total.ToString(), "");

        foreach (var relic in relics)
        {
            var displayName = StatsTooltip.EscapeBbcode(string.IsNullOrWhiteSpace(relic.DisplayName)
                ? RunTracker.FormatRelicIdForDisplay(relic.RelicId)
                : relic.DisplayName);
            var value = relic.Count == 1 ? displayName : $"{displayName} x{relic.Count}";
            TextValueRow(sb, "Obtained", value, "");
        }

        return sb.ToString();
    }

    private static string BuildSmallCapsuleBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var choices = (agg.RelicRewardChoices
                ?? new List<RelicRewardChoiceAggregate>())
            .Where(choice => choice != null)
            .OrderBy(choice => choice.ChoiceNumber)
            .ToList();

        if (choices.Count == 0)
        {
            TextValueRow(sb, "Relic choice", "not rolled yet", "");
            return sb.ToString();
        }

        foreach (var choice in choices)
        {
            var displayName = string.IsNullOrWhiteSpace(choice.DisplayName)
                ? string.IsNullOrWhiteSpace(choice.RelicId)
                    ? "Unknown relic"
                    : RunTracker.FormatRelicIdForDisplay(choice.RelicId)
                : choice.DisplayName;
            var icon = StatConceptGlossary.RenderHintedInlineImage(
                ResolveGrantedRelicIconPath(choice.RelicId),
                displayName);
            var outcome = choice.Outcome?.Trim().ToLowerInvariant() switch
            {
                "taken" => "taken",
                "skipped" => "not taken",
                _ => "not chosen yet",
            };
            DescribedIconFlowRow(
                sb,
                [],
                [],
                icon,
                RenderTakenOutcome(outcome),
                $"{displayName} was offered by Small Capsule and was {outcome}.");
        }

        return sb.ToString();
    }

    private static string BuildPaelsWingBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(sb, "common cards consumed", agg.CommonCardsConsumed.ToString(), "");
        Row3(sb, "uncommon cards consumed", agg.UncommonCardsConsumed.ToString(), "");
        Row3(sb, "rare cards consumed", agg.RareCardsConsumed.ToString(), "");
        var relics = agg.RelicsGranted.Values
            .Where(relic => relic.Count > 0)
            .OrderByDescending(relic => relic.Count)
            .ThenBy(relic => relic.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var relicsGained = relics.Sum(relic => Math.Max(0, relic.Count));
        Row3(sb, "Relics gained", relicsGained.ToString(), "");

        foreach (var relic in relics)
        {
            var value = RenderGrantedRelic(relic);
            TextValueRow(sb, "Relic gained", value, "");
        }

        Row3(sb, "Sacrifices made", agg.SacrificesMade.ToString(), "");
        Row3(sb, "Sacrifices skipped", agg.SacrificesSkipped.ToString(), "");
        var sacrificesMade = Math.Max(0, agg.SacrificesMade);
        var sacrificeOpportunities = sacrificesMade + Math.Max(0, agg.SacrificesSkipped);
        if (sacrificeOpportunities > 0)
        {
            var ratePercent = 100m * sacrificesMade / sacrificeOpportunities;
            Row3(
                sb,
                "Sacrifice rate",
                $"{sacrificesMade}/{sacrificeOpportunities}",
                $"{FormatDecimal(ratePercent)}%");
        }

        return sb.ToString();
    }

    private static string RenderGrantedRelic(RelicGrantedAggregate relic)
    {
        var displayName = string.IsNullOrWhiteSpace(relic.DisplayName)
            ? RunTracker.FormatRelicIdForDisplay(relic.RelicId)
            : relic.DisplayName;
        var icon = StatConceptGlossary.RenderHintedInlineImage(
            ResolveGrantedRelicIconPath(relic.RelicId),
            displayName);
        return relic.Count > 1 ? $"{icon} ×{relic.Count}" : icon;
    }

    internal static string ResolveGrantedRelicIconPath(string? relicId)
    {
        if (string.IsNullOrWhiteSpace(relicId))
            return GenericRelicIconPath;

        string? conventionalRelicPath = null;
        const string relicPrefix = "RELIC.";
        if (relicId.StartsWith(relicPrefix, StringComparison.OrdinalIgnoreCase)
            && relicId.Length > relicPrefix.Length)
        {
            conventionalRelicPath =
                "res://images/atlases/relic_atlas.sprites/"
                + $"{relicId[relicPrefix.Length..].ToLowerInvariant()}.tres";
        }

        try
        {
            var modelId = ModelId.Deserialize(relicId);
            var relicModel = ModelDb.GetByIdOrNull<RelicModel>(modelId);
            if (!string.IsNullOrWhiteSpace(relicModel?.IconPath))
                return relicModel.IconPath;

            if (conventionalRelicPath != null)
                return conventionalRelicPath;
        }
        catch
        {
            // Historical data can outlive a removed model. Use the generic
            // relic artwork instead of restoring the old text-only value.
        }

        return conventionalRelicPath ?? GenericRelicIconPath;
    }

    private static string BuildPaelsToothBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var cards = (agg.CardsReturned ?? new List<RelicCardReturnAggregate>())
            .Where(card => card != null)
            .ToList();
        Row3(sb, "Cards returned", cards.Count.ToString(), "");

        foreach (var card in cards)
        {
            var displayName = card.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = string.IsNullOrWhiteSpace(card.CardId)
                    ? "Unknown card"
                    : RunTracker.FormatCardIdForDisplay(card.CardId);
                if (card.UpgradeLevel > 0)
                    displayName += new string('+', card.UpgradeLevel);
            }

            TextValueRow(sb, "Returned card", StatsTooltip.EscapeBbcode(displayName), "");
            if (card.FloorsClimbed.HasValue)
            {
                Row3(
                    sb,
                    "Floors climbed",
                    Math.Max(0, card.FloorsClimbed.Value).ToString(),
                    "");
            }
        }

        return sb.ToString();
    }

    private static string BuildPaelsClawBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var goopyCardsPlayedPerTurn = agg.PaelsClawTurns <= 0
            ? 0m
            : (decimal)agg.PaelsClawGoopyCardsPlayed / agg.PaelsClawTurns;
        var goopyCardsPlayedPerCombat = agg.PaelsClawCombats <= 0
            ? 0m
            : (decimal)agg.PaelsClawGoopyCardsPlayed / agg.PaelsClawCombats;
        var enhancementsPerGoopyCard = agg.PaelsClawGoopyCards <= 0
            ? 0m
            : (decimal)agg.PaelsClawGoopyEnhancements / agg.PaelsClawGoopyCards;

        Row3(sb, "Goopy cards played", agg.PaelsClawGoopyCardsPlayed.ToString(), "");
        Row3(sb, "Avg Goopy cards played per turn", FormatDecimal(goopyCardsPlayedPerTurn), "");
        Row3(sb, "Avg Goopy cards played per combat", FormatDecimal(goopyCardsPlayedPerCombat), "");
        Row3(
            sb,
            "Avg number of Goopy enhancements per card with Goopy",
            FormatDecimal(enhancementsPerGoopyCard),
            "");
        return sb.ToString();
    }

    private static string BuildPaelsEyeBodyBBCode(RelicAggregate agg, bool activatedThisCombat = false)
    {
        var sb = new StringBuilder();
        var cardsExhaustedPerCombat = agg.PaelsEyeCombats <= 0
            ? 0m
            : (decimal)agg.PaelsEyeCardsExhausted / agg.PaelsEyeCombats;
        var averageActivationTurn = agg.PaelsEyeActivationTurnSamples <= 0
            ? 0m
            : (decimal)agg.PaelsEyeActivationTurnTotal
                / agg.PaelsEyeActivationTurnSamples;

        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, "Activated this combat", activatedThisCombat ? "true" : "false", "");
        Row3(
            sb,
            "Combats activations = 0",
            agg.CombatsWithoutActivation.ToString(),
            "",
            "Combats without activation.");
        Row3(
            sb,
            "Avg cards exhausted per combat",
            FormatDecimal(cardsExhaustedPerCombat),
            "",
            "Average cards exhausted per combat — cards observed in Exhaust after Pael's Eye activated divided by every combat where it was held, including combats without an activation.");
        Row3(
            sb,
            "Avg activation turn",
            FormatDecimal(averageActivationTurn),
            "",
            "Average activation turn — the average player turn number when Pael's Eye activated; combats without an activation are excluded.");
        Row3(sb, "Cards exhausted total", agg.PaelsEyeCardsExhausted.ToString(), "");
        Row3(
            sb,
            "Strikes and Defends exhausted",
            agg.PaelsEyeStrikesAndDefendsExhausted.ToString(),
            "");
        Row3(sb, "Statuses exhausted", agg.StatusCardsExhausted.ToString(), "");
        Row3(sb, "Curses exhausted", agg.CurseCardsExhausted.ToString(), "");
        return sb.ToString();
    }

    private static string BuildStrikeDummyBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var strikesPerTurn = agg.StrikeDummyTurns <= 0
            ? 0m
            : (decimal)agg.StrikeDummyRateStrikesPlayed / agg.StrikeDummyTurns;
        var strikesPerCombat = agg.StrikeDummyCombats <= 0
            ? 0m
            : (decimal)agg.StrikeDummyRateStrikesPlayed / agg.StrikeDummyCombats;

        Row3(sb, "Strikes played", agg.StrikeDummyStrikesPlayed.ToString(), "");
        Row3(sb, "Avg Strikes played per turn", FormatDecimal(strikesPerTurn), "");
        Row3(sb, "Avg Strikes played per combat", FormatDecimal(strikesPerCombat), "");
        Row3(sb, "Base Strikes in deck", agg.StrikeDummyBaseStrikesInDeck.ToString(), "");
        Row3(sb, "Non-base Strike cards in deck", agg.StrikeDummyNonBaseStrikeCardsInDeck.ToString(), "");
        return sb.ToString();
    }

    private static string BuildNutritiousSoupBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var averagePlays = agg.Activations <= 0
            ? 0m
            : (decimal)agg.NutritiousSoupEnchantedStrikesPlayed / agg.Activations;

        Row3(sb, "Combats held", agg.Activations.ToString(), "");
        Row3(sb, "Enchanted Strikes played", agg.NutritiousSoupEnchantedStrikesPlayed.ToString(), "");
        Row3(sb, "Avg Enchanted Strikes/combat", FormatDecimal(averagePlays), "");
        return sb.ToString();
    }

    private static string BuildMiniatureCannonBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var averagePlays = agg.Activations <= 0
            ? 0m
            : (decimal)agg.MiniatureCannonUpgradedAttackPlays / agg.Activations;
        var averageHits = agg.Activations <= 0
            ? 0m
            : (decimal)agg.MiniatureCannonUpgradedAttackHits / agg.Activations;

        Row3(sb, "Combats held", agg.Activations.ToString(), "");
        Row3(sb, "Upgraded attacks in deck", agg.MiniatureCannonUpgradedAttacksInDeck.ToString(), "");
        Row3(sb, "Non-upgraded attacks in deck", agg.MiniatureCannonNonUpgradedAttacksInDeck.ToString(), "");
        Row3(sb, "Upgraded attacks in combat", agg.MiniatureCannonUpgradedAttacksInCombat.ToString(), "");
        Row3(sb, "Non-upgraded attacks in combat", agg.MiniatureCannonNonUpgradedAttacksInCombat.ToString(), "");
        Row3(sb, "Upgraded attack plays", agg.MiniatureCannonUpgradedAttackPlays.ToString(), "");
        Row3(sb, "Upgraded attack hits", agg.MiniatureCannonUpgradedAttackHits.ToString(), "");
        Row3(sb, "Avg plays per combat", FormatDecimal(averagePlays), "");
        Row3(sb, "Avg hits per combat", FormatDecimal(averageHits), "");
        return sb.ToString();
    }

    private static string BuildVajraBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(
            sb,
            "Attacks played",
            agg.VajraAttacksPlayed.ToString(),
            "",
            AttacksPlayedDescription);
        Row3(sb, "Attack hits", agg.VajraAttackHits.ToString(), "");
        return sb.ToString();
    }

    private static string BuildOddlySmoothStoneBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        DescribedIconRow(
            sb,
            ["block"],
            [],
            "cards played",
            agg.OddlySmoothStoneBlockCardsPlayed.ToString(),
            "Block cards played — cards classified by the game as immediately gaining Dexterity-scaled Block.");
        return sb.ToString();
    }

    private static string BuildEmberTeaBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var attacksPerTurn = agg.EmberTeaActiveTurns <= 0
            ? 0m
            : (decimal)agg.EmberTeaAttacksPlayedWhileActive / agg.EmberTeaActiveTurns;
        var attacksPerCombat = agg.EmberTeaActiveCombats <= 0
            ? 0m
            : (decimal)agg.EmberTeaAttacksPlayedWhileActive / agg.EmberTeaActiveCombats;
        var hitsPerTurn = agg.EmberTeaActiveTurns <= 0
            ? 0m
            : (decimal)agg.EmberTeaHitsWhileActive / agg.EmberTeaActiveTurns;
        var hitsPerCombat = agg.EmberTeaActiveCombats <= 0
            ? 0m
            : (decimal)agg.EmberTeaHitsWhileActive / agg.EmberTeaActiveCombats;

        Row3(
            sb,
            "Attacks played while active",
            agg.EmberTeaAttacksPlayedWhileActive.ToString(),
            "",
            AttacksPlayedWhileActiveDescription);
        Row3(sb, "Avg attacks played per turn while active", FormatDecimal(attacksPerTurn), "");
        Row3(sb, "Avg attacks played per combat while active", FormatDecimal(attacksPerCombat), "");
        Row3(sb, "Hits while active", agg.EmberTeaHitsWhileActive.ToString(), "");
        Row3(sb, "Avg hits per turn while active", FormatDecimal(hitsPerTurn), "");
        Row3(sb, "Avg hits per combat while active", FormatDecimal(hitsPerCombat), "");
        return sb.ToString();
    }

    private static string BuildRedSkullBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var attacksPerTurn = agg.RedSkullActiveTurns <= 0
            ? 0m
            : (decimal)agg.RedSkullAttacksPlayedWhileActive / agg.RedSkullActiveTurns;
        var attacksPerCombat = agg.RedSkullActiveCombats <= 0
            ? 0m
            : (decimal)agg.RedSkullAttacksPlayedWhileActive / agg.RedSkullActiveCombats;
        var hitsPerTurn = agg.RedSkullActiveTurns <= 0
            ? 0m
            : (decimal)agg.RedSkullHitsWhileActive / agg.RedSkullActiveTurns;
        var hitsPerCombat = agg.RedSkullActiveCombats <= 0
            ? 0m
            : (decimal)agg.RedSkullHitsWhileActive / agg.RedSkullActiveCombats;

        Row3(
            sb,
            "Attacks played while active",
            agg.RedSkullAttacksPlayedWhileActive.ToString(),
            "",
            AttacksPlayedWhileActiveDescription);
        Row3(sb, "Avg attacks played while active per turn", FormatDecimal(attacksPerTurn), "");
        Row3(sb, "Avg attacks played while active per combat", FormatDecimal(attacksPerCombat), "");
        Row3(sb, "Hits while active", agg.RedSkullHitsWhileActive.ToString(), "");
        Row3(sb, "Avg hits while active per turn", FormatDecimal(hitsPerTurn), "");
        Row3(sb, "Avg hits while active per combat", FormatDecimal(hitsPerCombat), "");
        return sb.ToString();
    }

    private static string BuildToastyMittensBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var cardsPerCombat = agg.ToastyMittensCombats <= 0
            ? 0m
            : (decimal)agg.ToastyMittensCardsExhausted / agg.ToastyMittensCombats;
        var strengthPerCombat = agg.ToastyMittensCombats <= 0
            ? 0m
            : agg.StrengthAdded / agg.ToastyMittensCombats;

        Row3(sb, "Cards exhausted total", agg.ToastyMittensCardsExhausted.ToString(), "");
        Row3(sb, "Strength added total", FormatDecimal(agg.StrengthAdded), "");
        Row3(sb, "Cards exhausted per combat", FormatDecimal(cardsPerCombat), "");
        Row3(sb, "Strength added per combat", FormatDecimal(strengthPerCombat), "");
        return sb.ToString();
    }

    private static string BuildKunaiBodyBBCode(RelicAggregate agg)
        => BuildKunaiBodyBBCodeWithLiveActivations(agg, null);

    private static string BuildKunaiBodyBBCodeWithLiveActivations(
        RelicAggregate agg,
        RelicLiveActivationCounts? liveActivationCounts)
    {
        return BuildThreeAttackScalingRelicBodyBBCode(
            new ThreeAttackScalingRelicStats(
                agg.KunaiAttacksPlayed,
                agg.Activations,
                agg.KunaiDexterityGained,
                agg.ThreeAttackScalingRateActivations,
                agg.ThreeAttackScalingTurns,
                agg.ThreeAttackScalingCombats,
                agg.KunaiTurnsEndedAt1Charge,
                agg.KunaiTurnsEndedAt2Charges,
                agg.KunaiTurnEndChargeTotal,
                agg.KunaiTurnEndChargeCount),
            "Dexterity gained",
            liveActivationCounts);
    }

    private static string BuildKusarigamaBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var turnsEndedAt0Charges = Math.Max(
            0,
            agg.KusarigamaTurnEndChargeCount
            - agg.KusarigamaTurnsEndedAt1Charge
            - agg.KusarigamaTurnsEndedAt2Charges);

        Row3(
            sb,
            "Attacks played",
            agg.KusarigamaAttacksPlayed.ToString(),
            "",
            AttacksPlayedDescription);
        AppendRelicDamageStats(
            sb,
            agg,
            triggerDescription: "Activations — the number of times this relic has activated.",
            averageLabel: "Damage per activation",
            averageDenominator: agg.Activations);
        Row3(sb, "Turns ended at 0 charges", turnsEndedAt0Charges.ToString(), "");
        AppendTurnResetChargeRows(
            sb,
            agg.KusarigamaTurnsEndedAt1Charge,
            agg.KusarigamaTurnsEndedAt2Charges,
            agg.KusarigamaTurnEndChargeTotal,
            agg.KusarigamaTurnEndChargeCount);
        return sb.ToString();
    }

    private static string BuildOrnamentalFanBodyBBCode(RelicAggregate agg)
        => BuildOrnamentalFanBodyBBCodeWithLiveActivations(agg, null);

    private static string BuildOrnamentalFanBodyBBCodeWithLiveActivations(
        RelicAggregate agg,
        RelicLiveActivationCounts? liveActivationCounts)
    {
        var sb = new StringBuilder();
        var blockPerTurn = agg.OrnamentalFanTurns <= 0
            ? 0m
            : (decimal)agg.OrnamentalFanRateBlockGained / agg.OrnamentalFanTurns;
        var blockPerCombat = agg.OrnamentalFanCombats <= 0
            ? 0m
            : (decimal)agg.OrnamentalFanRateBlockGained / agg.OrnamentalFanCombats;
        var absorbedPercent = agg.OrnamentalFanRateBlockGained <= 0
            ? 0m
            : 100m * agg.AdditionalBlockEffective / agg.OrnamentalFanRateBlockGained;
        var wastedPercent = agg.OrnamentalFanRateBlockGained <= 0
            ? 0m
            : 100m * agg.AdditionalBlockWasted / agg.OrnamentalFanRateBlockGained;

        Row3(
            sb,
            "Attacks played",
            agg.OrnamentalFanAttacksPlayed.ToString(),
            "",
            AttacksPlayedDescription);
        RelicActivationRow(sb, agg.Activations.ToString());
        AppendLiveActivationRows(sb, liveActivationCounts);
        Row3(sb, BlockLabel("block gained"), agg.AdditionalBlockGained.ToString(), "");
        Row3(
            sb,
            BlockLabel("block absorbed"),
            agg.AdditionalBlockEffective.ToString(),
            $"{absorbedPercent:F0}%");
        Row3(
            sb,
            "block wasted",
            agg.AdditionalBlockWasted.ToString(),
            $"{wastedPercent:F0}%");
        Row3(sb, BlockLabel("avg block gained per turn"), FormatDecimal(blockPerTurn), "");
        Row3(sb, BlockLabel("avg block gained per combat"), FormatDecimal(blockPerCombat), "");
        Row3(sb, "Turns ended at 0 charges", agg.OrnamentalFanTurnsEndedAt0Charges.ToString(), "");
        AppendTurnResetChargeRows(
            sb,
            agg.OrnamentalFanTurnsEndedAt1Charge,
            agg.OrnamentalFanTurnsEndedAt2Charges,
            agg.OrnamentalFanTurnEndChargeTotal,
            agg.OrnamentalFanTurnEndChargeCount);
        return sb.ToString();
    }

    private static string BuildShurikenBodyBBCode(RelicAggregate agg)
        => BuildShurikenBodyBBCodeWithLiveActivations(agg, null);

    private static string BuildShurikenBodyBBCodeWithLiveActivations(
        RelicAggregate agg,
        RelicLiveActivationCounts? liveActivationCounts)
    {
        return BuildThreeAttackScalingRelicBodyBBCode(
            new ThreeAttackScalingRelicStats(
                agg.ShurikenAttacksPlayed,
                agg.Activations,
                agg.StrengthAdded,
                agg.ThreeAttackScalingRateActivations,
                agg.ThreeAttackScalingTurns,
                agg.ThreeAttackScalingCombats,
                agg.ShurikenTurnsEndedAt1Charge,
                agg.ShurikenTurnsEndedAt2Charges,
                agg.ShurikenTurnEndChargeTotal,
                agg.ShurikenTurnEndChargeCount),
            "Strength gained",
            liveActivationCounts);
    }

    private static string BuildThreeAttackScalingRelicBodyBBCode(
        ThreeAttackScalingRelicStats stats,
        string attributeLabel,
        RelicLiveActivationCounts? liveActivationCounts)
    {
        var sb = new StringBuilder();
        var activationsPerTurn = stats.Turns <= 0
            ? 0m
            : (decimal)stats.RateActivations / stats.Turns;
        var activationsPerCombat = stats.Combats <= 0
            ? 0m
            : (decimal)stats.RateActivations / stats.Combats;
        var turnsEndedAt0Charges = Math.Max(
            0,
            stats.TurnEndChargeCount
            - stats.TurnsEndedAt1Charge
            - stats.TurnsEndedAt2Charges);

        Row3(
            sb,
            "Attacks played",
            stats.AttacksPlayed.ToString(),
            "",
            AttacksPlayedDescription);
        RelicActivationRow(sb, stats.Activations.ToString());
        AppendLiveActivationRows(sb, liveActivationCounts);
        Row3(
            sb,
            attributeLabel,
            FormatDecimal(stats.AttributeGained),
            "",
            $"{attributeLabel} — the actual amount added by this relic.");
        Row3(
            sb,
            "Avg activations per turn",
            FormatDecimal(activationsPerTurn),
            "",
            "Average activations per turn — observed activations divided by player turns in which this relic was held during the same tracking window.");
        Row3(
            sb,
            "Avg activations per combat",
            FormatDecimal(activationsPerCombat),
            "",
            "Average activations per combat — observed activations divided by combats in which this relic was held during the same tracking window.");
        Row3(sb, "Turns ended at 0 charges", turnsEndedAt0Charges.ToString(), "");
        AppendTurnResetChargeRows(
            sb,
            stats.TurnsEndedAt1Charge,
            stats.TurnsEndedAt2Charges,
            stats.TurnEndChargeTotal,
            stats.TurnEndChargeCount);
        return sb.ToString();
    }

    private static string BuildRuinedHelmetBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendStrengthIncreaseRelicStats(
            sb,
            agg.Activations,
            agg.StrengthAdded,
            agg.RuinedHelmetStrengthAddedThisCombat,
            agg.StrengthAdded,
            agg.RuinedHelmetCombats);
        return sb.ToString();
    }

    private static string BuildGiryaBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        AppendStrengthIncreaseRelicStats(
            sb,
            agg.Activations,
            agg.StrengthAdded,
            agg.GiryaStrengthAddedThisCombat,
            agg.GiryaStrengthRateAdded,
            agg.GiryaStrengthCombats);
        Row3(
            sb,
            "Attacks played",
            agg.GiryaAttacksPlayed.ToString(),
            "",
            "The number of Attack cards played while Girya's Strength was active.");
        Row3(
            sb,
            "Attack hits",
            agg.GiryaAttackHits.ToString(),
            "",
            "Resolved enemy damage hits from Attack cards while Girya's Strength was active; multi-hit Attacks count once per hit.");

        var averageCountPerFloor = agg.GiryaFloorSamples <= 0
            ? 0m
            : (decimal)agg.GiryaCountFloorTotal / agg.GiryaFloorSamples;
        var averageFloorsPerUse = agg.GiryaUseFloorDistanceSamples <= 0
            ? 0m
            : (decimal)agg.GiryaUseFloorDistanceTotal
                / agg.GiryaUseFloorDistanceSamples;

        DescribedIconRow(
            sb,
            ["average", "charge"],
            ["floor"],
            string.Empty,
            FormatDecimal(averageCountPerFloor),
            "Average Girya Lift count per sampled floor while held, including count zero on its acquisition floor.");
        DescribedIconRow(
            sb,
            ["average", "floor"],
            ["activation"],
            "Lift",
            FormatDecimal(averageFloorsPerUse),
            "Average floors from acquiring Girya to its first successful Lift, then between consecutive successful Lifts.");
        return sb.ToString();
    }

    private static void AppendStrengthIncreaseRelicStats(
        StringBuilder sb,
        int activations,
        decimal strengthGained,
        decimal strengthGainedThisCombat,
        decimal combatRateStrengthGained,
        int combats)
    {
        var strengthPerActivation = activations <= 0
            ? 0m
            : strengthGained / activations;
        var strengthPerCombat = combats <= 0
            ? 0m
            : combatRateStrengthGained / combats;

        RelicActivationRow(
            sb,
            activations.ToString(),
            "Times activated — the number of times this relic has activated.");
        Row3(
            sb,
            "Activated this combat",
            strengthGainedThisCombat > 0m ? "true" : "false",
            "");
        Row3(sb, "Total strength gained", FormatDecimal(strengthGained), "");
        Row3(
            sb,
            "Strength gained this combat",
            FormatDecimal(strengthGainedThisCombat),
            "");
        Row3(
            sb,
            "Avg strength gained per activation",
            FormatDecimal(strengthPerActivation),
            "");
        Row3(
            sb,
            "Avg strength gained per combat",
            FormatDecimal(strengthPerCombat),
            "",
            "Average Strength gained per combat — observed Strength divided by combats in which this relic could grant Strength during the same tracking window.");
    }

    private static string BuildSwordInTheStoneBodyBBCode(
        RelicAggregate agg,
        int? floorAcquiredFallback = null)
    {
        var sb = new StringBuilder();
        var elites = (agg.SwordInTheStoneElitesSlain
                      ?? new List<SwordInTheStoneEliteSlainAggregate>())
            .Where(elite => elite != null && elite.Floor > 0)
            .ToList();
        var averageFloorsPerElite = CalculateAverageFloorsPerElite(elites);
        Row3(
            sb,
            "Floor acquired",
            FormatFloor(agg.FloorAcquired ?? floorAcquiredFallback),
            "");
        Row3(sb, "Elites slain", elites.Count.ToString(), "");
        Row3(
            sb,
            "Avg floors per elite",
            FormatDecimal(averageFloorsPerElite),
            "",
            "Average floors ascended between consecutive Elite victories recorded by Sword in the Stone.");

        foreach (var elite in elites)
        {
            var displayName = StatsTooltip.EscapeBbcode(
                string.IsNullOrWhiteSpace(elite.DisplayName)
                    ? elite.EncounterId
                    : elite.DisplayName);
            TextValueRow(
                sb,
                "Elite slain",
                $"{displayName} — floor {FormatFloor(elite.Floor)}",
                "");
        }

        return sb.ToString();
    }

    private static string BuildSwordOfJadeBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder(BuildSwordInTheStoneBodyBBCode(agg));
        AppendStrengthIncreaseRelicStats(
            sb,
            agg.Activations,
            agg.StrengthAdded,
            agg.SwordInTheStoneStrengthAddedThisCombat,
            agg.SwordInTheStoneStrengthRateAdded,
            agg.SwordInTheStoneStrengthCombats);
        return sb.ToString();
    }

    internal static decimal CalculateAverageFloorsPerElite(
        IReadOnlyList<SwordInTheStoneEliteSlainAggregate>? elites)
    {
        if (elites == null || elites.Count < 2) return 0m;

        var floorGapTotal = 0;
        var floorGapCount = 0;
        for (var i = 1; i < elites.Count; i++)
        {
            if (elites[i - 1].Floor <= 0 || elites[i].Floor <= 0) continue;
            floorGapTotal += Math.Max(0, elites[i].Floor - elites[i - 1].Floor);
            floorGapCount += 1;
        }

        return floorGapCount == 0
            ? 0m
            : (decimal)floorGapTotal / floorGapCount;
    }

    private static void AppendTurnResetChargeRows(
        StringBuilder sb,
        int turnsEndedAt1Charge,
        int turnsEndedAt2Charges,
        int turnEndChargeTotal,
        int turnEndChargeCount)
    {
        var averageEndCharge = turnEndChargeCount <= 0
            ? 0m
            : (decimal)turnEndChargeTotal / turnEndChargeCount;

        Row3(sb, "Turns ended at 1 charge", turnsEndedAt1Charge.ToString(), "");
        Row3(sb, "Turns ended at 2 charges", turnsEndedAt2Charges.ToString(), "");
        Row3(sb, "Avg charge at turn end", FormatDecimal(averageEndCharge), "");
    }

    private static string BuildPaperPhrogBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var averageDamagePerCombat = agg.PaperPhrogCombats <= 0
            ? 0m
            : agg.PaperPhrogDamageAdded / agg.PaperPhrogCombats;
        var averageDamagePerTurn = agg.PaperPhrogTurns <= 0
            ? 0m
            : agg.PaperPhrogDamageAdded / agg.PaperPhrogTurns;
        var averageAttacksPerCombat = agg.PaperPhrogCombats <= 0
            ? 0m
            : (decimal)agg.PaperPhrogEnhancedAttacks / agg.PaperPhrogCombats;
        var averageAttacksPerTurn = agg.PaperPhrogTurns <= 0
            ? 0m
            : (decimal)agg.PaperPhrogEnhancedAttacks / agg.PaperPhrogTurns;

        Row3(sb, "Damage added", FormatDecimal(agg.PaperPhrogDamageAdded), "");
        Row3(sb, "Avg damage added per combat", FormatDecimal(averageDamagePerCombat), "");
        Row3(sb, "Avg damage added per turn", FormatDecimal(averageDamagePerTurn), "");
        Row3(sb, "Vulnerable-enhanced attacks", agg.PaperPhrogEnhancedAttacks.ToString(), "");
        Row3(sb, "Avg enhanced attacks per combat", FormatDecimal(averageAttacksPerCombat), "");
        Row3(sb, "Avg enhanced attacks per turn", FormatDecimal(averageAttacksPerTurn), "");
        return sb.ToString();
    }

    private static string BuildBookmarkBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var averageActivations = agg.BookmarkCombats <= 0
            ? 0m
            : (decimal)agg.Activations / agg.BookmarkCombats;

        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, "common activations", agg.BookmarkCommonActivations.ToString(), "");
        Row3(sb, "uncommon activations", agg.BookmarkUncommonActivations.ToString(), "");
        Row3(sb, "rare activations", agg.BookmarkRareActivations.ToString(), "");
        Row3(sb, "Combats held", agg.BookmarkCombats.ToString(), "");
        Row3(sb, "Avg activations per combat", FormatDecimal(averageActivations), "");
        return sb.ToString();
    }

    private static string BuildBrilliantScarfBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var energySavedPerTurn = agg.DiscountTurns <= 0
            ? 0m
            : (decimal)agg.BrilliantScarfEnergySavedForTurnAverage / agg.DiscountTurns;
        var energySavedPerCombat = agg.DiscountCombats <= 0
            ? 0m
            : (decimal)agg.EnergySavedByDiscount / agg.DiscountCombats;
        var energySavedPerUse = agg.DiscountsTaken <= 0
            ? 0m
            : (decimal)agg.EnergySavedByDiscount / agg.DiscountsTaken;
        Row3(sb, "Combats held", agg.DiscountCombats.ToString(), "");
        Row3(
            sb,
            "Discounts offered/taken",
            FormatOfferedTaken(agg.DiscountsOffered, agg.DiscountsTaken),
            "",
            "Discounts offered/taken — Energy discounts Brilliant Scarf made available, and how many were actually spent on a play.");
        Row3(sb, EnergyLabel("Energy saved"), agg.EnergySavedByDiscount.ToString(), "");
        Row3(sb, EnergyLabel("saved / turn"), FormatDecimal(energySavedPerTurn), "");
        Row3(sb, EnergyLabel("saved / combat"), FormatDecimal(energySavedPerCombat), "");
        Row3(sb, EnergyLabel("saved / use"), FormatDecimal(energySavedPerUse), "");

        for (int energyCost = 0; energyCost <= 3; energyCost++)
        {
            Row3(
                sb,
                BrilliantScarfCostLabel(energyCost, starCost: 0),
                BrilliantScarfCostCount(agg, energyCost, starCost: 0).ToString(),
                "",
                BrilliantScarfCostDescription(energyCost, starCost: 0));
        }

        foreach (var bucket in DynamicBrilliantScarfCostBuckets(agg))
        {
            Row3(
                sb,
                BrilliantScarfCostLabel(bucket.EnergyCost, bucket.StarCost),
                bucket.Count.ToString(),
                "",
                BrilliantScarfCostDescription(bucket.EnergyCost, bucket.StarCost));
        }

        return sb.ToString();
    }

    private static string BuildMummifiedHandBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var averageTriggeringPowerCost = agg.Activations <= 0
            ? 0m
            : agg.MummifiedHandTriggeringPowerCostTotal / agg.Activations;
        var averageDiscountGiven = agg.Activations <= 0
            ? 0m
            : agg.MummifiedHandDiscountGivenTotal / agg.Activations;
        var averageEnergySpentToDiscountedCostRatio =
            agg.MummifiedHandEnergySpentToDiscountedCostRatioCount <= 0
                ? 0m
                : agg.MummifiedHandEnergySpentToDiscountedCostRatioTotal
                  / agg.MummifiedHandEnergySpentToDiscountedCostRatioCount;
        var averageActivationsPerCombat = agg.MummifiedHandCombats <= 0
            ? 0m
            : (decimal)agg.Activations / agg.MummifiedHandCombats;
        var averageActivationsPerTurn = agg.MummifiedHandTurns <= 0
            ? 0m
            : (decimal)agg.Activations / agg.MummifiedHandTurns;

        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            "Times triggered — the number of times this relic has activated.");
        Row3(sb, EnergyLabel("Avg cost of triggering Power"), FormatDecimal(averageTriggeringPowerCost), "");
        Row3(sb, EnergyLabel("Avg discount given"), FormatDecimal(averageDiscountGiven), "");
        Row3(
            sb,
            "Avg ratio: Power energy spent / discounted card cost",
            FormatDecimal(averageEnergySpentToDiscountedCostRatio),
            "");
        Row3(sb, "Avg activations per combat", FormatDecimal(averageActivationsPerCombat), "");
        Row3(sb, "Avg activations per turn", FormatDecimal(averageActivationsPerTurn), "");
        Row3(sb, "Discounted Powers", agg.MummifiedHandDiscountedPowers.ToString(), "");
        Row3(sb, "Discounted Attacks", agg.MummifiedHandDiscountedAttacks.ToString(), "");
        Row3(sb, "Discounted Skills", agg.MummifiedHandDiscountedSkills.ToString(), "");
        Row3(sb, "Discounted Commons", agg.MummifiedHandDiscountedCommons.ToString(), "");
        Row3(sb, "Discounted Uncommons", agg.MummifiedHandDiscountedUncommons.ToString(), "");
        Row3(sb, "Discounted Rares", agg.MummifiedHandDiscountedRares.ToString(), "");
        return sb.ToString();
    }

    private static string BuildBurningSticksBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var averageActivationsPerCombat = agg.BurningSticksCombats <= 0
            ? 0m
            : (decimal)agg.Activations / agg.BurningSticksCombats;
        var averageGeneratedCardPlaysPerCombat = agg.BurningSticksCombats <= 0
            ? 0m
            : (decimal)agg.BurningSticksGeneratedCardPlays / agg.BurningSticksCombats;

        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            "Times this relic successfully duplicated an exhausted Skill.");
        Row3(
            sb,
            "Avg activations per combat",
            FormatDecimal(averageActivationsPerCombat),
            "",
            "Successful Burning Sticks activations divided by combats where it was held.");
        Row3(
            sb,
            "Avg times generated card played per combat",
            FormatDecimal(averageGeneratedCardPlaysPerCombat),
            "",
            "Finished plays of cards generated by Burning Sticks divided by combats where it was held.");
        Row3(
            sb,
            "Commons duplicated",
            agg.BurningSticksCommonCardsDuplicated.ToString(),
            "",
            "Common cards successfully duplicated by Burning Sticks.");
        Row3(
            sb,
            "Uncommons duplicated",
            agg.BurningSticksUncommonCardsDuplicated.ToString(),
            "",
            "Uncommon cards successfully duplicated by Burning Sticks.");
        Row3(
            sb,
            "Rares duplicated",
            agg.BurningSticksRareCardsDuplicated.ToString(),
            "",
            "Rare cards successfully duplicated by Burning Sticks.");
        return sb.ToString();
    }

    private static string BuildThrowingAxeBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var averageEnergyCostPerCombat = agg.ThrowingAxeCombats <= 0
            ? 0m
            : (decimal)agg.ThrowingAxeExtraPlayEnergyCostTotal
              / agg.ThrowingAxeCombats;

        Row3(
            sb,
            "Extra cards played",
            agg.ThrowingAxeExtraCardsPlayed.ToString(),
            "",
            "Finished extra card plays contributed by Throwing Axe.");
        Row3(
            sb,
            "Total energy cost of extra plays",
            agg.ThrowingAxeExtraPlayEnergyCostTotal.ToString(),
            "",
            "The play-time energy values of cards replayed by Throwing Axe.");
        Row3(
            sb,
            "Avg energy cost per combat",
            FormatDecimal(averageEnergyCostPerCombat),
            "",
            "Total energy cost of Throwing Axe extra plays divided by combats where it was held.");
        Row3(
            sb,
            "Commons played",
            agg.ThrowingAxeCommonCardsPlayed.ToString(),
            "",
            "Common cards replayed by Throwing Axe.");
        Row3(
            sb,
            "Uncommons played",
            agg.ThrowingAxeUncommonCardsPlayed.ToString(),
            "",
            "Uncommon cards replayed by Throwing Axe.");
        Row3(
            sb,
            "Rares played",
            agg.ThrowingAxeRareCardsPlayed.ToString(),
            "",
            "Rare cards replayed by Throwing Axe.");
        return sb.ToString();
    }

    private static string BuildBingBongBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(
            sb,
            "Extra cards added",
            agg.BingBongExtraCardsAdded.ToString(),
            "",
            "Extra cards successfully added to the permanent deck by Bing Bong.");
        Row3(
            sb,
            "Commons added",
            agg.BingBongCommonCardsAdded.ToString(),
            "",
            "Non-Curse Common cards successfully added to the permanent deck by Bing Bong.");
        Row3(
            sb,
            "Uncommons added",
            agg.BingBongUncommonCardsAdded.ToString(),
            "",
            "Non-Curse Uncommon cards successfully added to the permanent deck by Bing Bong.");
        Row3(
            sb,
            "Rares added",
            agg.BingBongRareCardsAdded.ToString(),
            "",
            "Non-Curse Rare cards successfully added to the permanent deck by Bing Bong.");
        Row3(
            sb,
            "Curses added",
            agg.BingBongCurseCardsAdded.ToString(),
            "",
            "Curse cards successfully added to the permanent deck by Bing Bong.");
        return sb.ToString();
    }

    private static string BuildJuzuBraceletBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        ConceptRow(
            sb,
            "unknown_room",
            agg.QuestionMarkSitesEntered.ToString(),
            "Question-mark map sites entered while Juzu Bracelet was held.");
        return sb.ToString();
    }

    private static string BuildDowsingRodBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var roomsRemaining = Math.Clamp(
            agg.DowsingQuestionRoomsRemaining ?? RunTracker.DowsingMaxRooms,
            0,
            RunTracker.DowsingMaxRooms);
        Row3(sb, "? rooms remaining", roomsRemaining.ToString(), "");
        return sb.ToString();
    }

    private static string BuildGamblingChipBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var averageDiscarded = agg.Activations <= 0
            ? 0m
            : (decimal)agg.CardsDiscarded / agg.Activations;
        Row3(sb, "Combats held", agg.Activations.ToString(), "");
        Row3(sb, "Cards discarded", agg.CardsDiscarded.ToString(), "");
        Row3(sb, "Avg discarded per combat", FormatDecimal(averageDiscarded), "");
        return sb.ToString();
    }

    private static string BuildCentennialPuzzleBodyBBCode(
        RelicAggregate agg,
        bool triggeredThisCombat = false)
    {
        var sb = new StringBuilder();
        var averageDrawn = agg.Activations <= 0
            ? 0m
            : (decimal)agg.AdditionalCardsDrawn / agg.Activations;
        var averageActivationTurn = agg.CentennialPuzzleActivationTurnSamples <= 0
            ? 0m
            : (decimal)agg.CentennialPuzzleActivationTurnTotal
                / agg.CentennialPuzzleActivationTurnSamples;
        RelicActivationRow(sb, agg.Activations.ToString());
        Row3(sb, "Triggered this combat", triggeredThisCombat ? "true" : "false", "");
        Row3(sb, "Cards drawn total", agg.AdditionalCardsDrawn.ToString(), "");
        Row3(sb, "Avg cards drawn per combat", FormatDecimal(averageDrawn), "");
        Row3(
            sb,
            "Avg activation turn",
            FormatDecimal(averageActivationTurn),
            "",
            "The average player turn number when Centennial Puzzle activated.");
        Row3(
            sb,
            "Activated during your turn",
            agg.CentennialPuzzlePlayerTurnActivations.ToString(),
            "",
            "Times Centennial Puzzle activated during your turn.");
        Row3(
            sb,
            "Activated during opponent's turn",
            agg.CentennialPuzzleOpponentTurnActivations.ToString(),
            "",
            "Times Centennial Puzzle activated during the opponent's turn.");
        Row3(
            sb,
            "Activated by Status",
            agg.CentennialPuzzleStatusActivations.ToString(),
            "",
            "Times a Status card caused the HP loss that activated Centennial Puzzle.");
        Row3(
            sb,
            "Activated by Curse",
            agg.CentennialPuzzleCurseActivations.ToString(),
            "",
            "Times a Curse card caused the HP loss that activated Centennial Puzzle.");
        Row3(
            sb,
            "Activated by enemy source",
            agg.CentennialPuzzleEnemySourceActivations.ToString(),
            "",
            "Times an enemy attack or enemy-applied debuff caused the HP loss that activated Centennial Puzzle.");
        return sb.ToString();
    }

    private static string BuildWhiteBeastStatueBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(sb, "Potions gained", agg.PotionsGained.ToString(), "");
        Row3(sb, "Potions skipped", agg.PotionsSkipped.ToString(), "");
        Row3(sb, "common potions", agg.CommonPotionsGained.ToString(), "");
        Row3(sb, "uncommon potions", agg.UncommonPotionsGained.ToString(), "");
        Row3(sb, "rare potions", agg.RarePotionsGained.ToString(), "");
        return sb.ToString();
    }

    private static string BuildPotionSlotRelicBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        var averagePotionsHeld = agg.CombatStartPotionCountSamples <= 0
            ? 0m
            : (decimal)agg.CombatStartPotionCountTotal
                / agg.CombatStartPotionCountSamples;
        Row3(
            sb,
            "Avg potions held at combat start",
            FormatDecimal(averagePotionsHeld),
            "",
            "Average number of potions held at combat start while this relic was owned.");
        return sb.ToString();
    }

    private static string BuildPetrifiedToadBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        Row3(sb, "Potions given", agg.PetrifiedToadPotionsGiven.ToString(), "");
        Row3(
            sb,
            "Potions blocked by full belt",
            agg.PetrifiedToadPotionsBlockedByFullBelt.ToString(),
            "",
            "Potion Shaped Rocks not given because the potion belt was full.");
        return sb.ToString();
    }

    private static string BuildShovelBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        ConceptRow(
            sb,
            "relic_gained",
            agg.RelicsAcquired.ToString(),
            "Relics acquired by digging with Shovel.");
        ConceptRow(
            sb,
            "relic_common",
            agg.CommonRelicsAcquired.ToString(),
            "Common relics acquired by digging with Shovel.");
        ConceptRow(
            sb,
            "relic_uncommon",
            agg.UncommonRelicsAcquired.ToString(),
            "Uncommon relics acquired by digging with Shovel.");
        ConceptRow(
            sb,
            "relic_rare",
            agg.RareRelicsAcquired.ToString(),
            "Rare relics acquired by digging with Shovel.");
        DescribedIconRow(
            sb,
            ["campfire"],
            [],
            "not dug",
            agg.CampfiresNotDug.ToString(),
            "Campfires where Shovel was available but Dig was not chosen.");
        return sb.ToString();
    }

    private static string BuildTinyMailboxBodyBBCode(RelicAggregate agg)
    {
        var sb = new StringBuilder();
        RelicActivationRow(
            sb,
            agg.Activations.ToString(),
            "Rest-site heals where Tiny Mailbox added its potion rewards.");
        OfferedTakenRow(
            sb,
            ["potion"],
            string.Empty,
            agg.TinyMailboxPotionsOffered,
            agg.TinyMailboxPotionsTaken,
            "Potions offered/taken — potion rewards Tiny Mailbox added, and how many were successfully taken.");
        DescribedIconRow(
            sb,
            ["potion_common", "offered"],
            [],
            string.Empty,
            agg.TinyMailboxCommonPotionsOffered.ToString(),
            "Common potion rewards offered by Tiny Mailbox.");
        DescribedIconRow(
            sb,
            ["potion_uncommon", "offered"],
            [],
            string.Empty,
            agg.TinyMailboxUncommonPotionsOffered.ToString(),
            "Uncommon potion rewards offered by Tiny Mailbox.");
        DescribedIconRow(
            sb,
            ["potion_rare", "offered"],
            [],
            string.Empty,
            agg.TinyMailboxRarePotionsOffered.ToString(),
            "Rare potion rewards offered by Tiny Mailbox.");
        DescribedIconRow(
            sb,
            ["fruit_juice", "offered"],
            [],
            string.Empty,
            agg.TinyMailboxFruitJuicesOffered.ToString(),
            "Fruit Juice rewards offered by Tiny Mailbox; these also count as Rare offers.");
        DescribedIconRow(
            sb,
            ["campfire"],
            [],
            "not rested",
            agg.TinyMailboxCampfiresNotRested.ToString(),
            "Campfires where Rest was available while Tiny Mailbox was held but was not chosen.");
        return sb.ToString();
    }

    private static string BuildPhylacteryBodyBBCode(RunMetaStats stats)
    {
        var sb = new StringBuilder();
        var summonPerTurn = stats.OstyBodyTurns <= 0
            ? 0m
            : stats.TotalOstyHpSummoned / stats.OstyBodyTurns;
        var summonPerCombat = stats.OstyBodyCombats <= 0
            ? 0m
            : stats.TotalOstyHpSummoned / stats.OstyBodyCombats;
        var absorbedPerTurn = stats.OstyBodyTurns <= 0
            ? 0m
            : stats.TotalOstyDamageAbsorbed / stats.OstyBodyTurns;
        var absorbedPerCombat = stats.OstyBodyCombats <= 0
            ? 0m
            : stats.TotalOstyDamageAbsorbed / stats.OstyBodyCombats;

        DescribedIconRow(
            sb,
            ["average", "osty_summon_gained"],
            ["turn"],
            string.Empty,
            FormatDecimal(summonPerTurn),
            "Average observed Osty summon gained per player turn while either form of the Phylactery was held, including zero-summon turns.");
        DescribedIconRow(
            sb,
            ["average", "osty_summon_gained"],
            ["combat"],
            string.Empty,
            FormatDecimal(summonPerCombat),
            "Average observed Osty summon gained per combat while either form of the Phylactery was held, including zero-summon combats.");
        DescribedIconRow(
            sb,
            ["all", "osty_summon_gained", "attack"],
            [],
            "Unleash",
            FormatDecimal(stats.TotalOstyHpWhenUnleashPlayed),
            "Sum of Osty's current HP whenever Unleash was played; this is the Summon contribution added to Unleash's base damage.");
        DescribedIconRow(
            sb,
            ["all", "damage"],
            [],
            "absorbed",
            FormatDecimal(stats.TotalOstyDamageAbsorbed),
            "Total HP lost by the tracked player's Osty bodies; this is damage they absorbed.");
        DescribedIconRow(
            sb,
            ["average", "damage"],
            ["turn"],
            "absorbed",
            FormatDecimal(absorbedPerTurn),
            "Average damage absorbed by Osty per player turn while either form of the Phylactery was held, including zero-damage turns.");
        DescribedIconRow(
            sb,
            ["average", "damage"],
            ["combat"],
            "absorbed",
            FormatDecimal(absorbedPerCombat),
            "Average damage absorbed by Osty per combat while either form of the Phylactery was held, including zero-damage combats.");
        return sb.ToString();
    }

    private static string VulnerableLabel(string suffix)
    {
        var path = NormalizeResourcePath(VulnerableIconPath);
        return $"{StatConceptGlossary.RenderInlineImage(path)} {suffix}";
    }

    private static string WeakLabel(string suffix)
    {
        var path = NormalizeResourcePath(WeakIconPath);
        return $"{StatConceptGlossary.RenderInlineImage(path)} {suffix}";
    }

    private static IEnumerable<AppliedEffectAggregate> OtherUnsettlingLampDebuffs(RelicAggregate agg)
    {
        if (agg.AppliedEffects == null) return Enumerable.Empty<AppliedEffectAggregate>();

        return agg.AppliedEffects.Values
            .Where(effect => effect != null
                && effect.TotalAmountApplied > 0m
                && !RunTracker.IsVulnerableEffect(effect.EffectId, effect.DisplayName)
                && !RunTracker.IsWeakEffect(effect.EffectId, effect.DisplayName))
            .OrderBy(effect => string.IsNullOrWhiteSpace(effect.DisplayName) ? effect.EffectId : effect.DisplayName);
    }

    private static string RelicEffectLabel(AppliedEffectAggregate effect, string suffix)
    {
        var displayName = string.IsNullOrWhiteSpace(effect.DisplayName)
            ? effect.EffectId
            : effect.DisplayName;
        var label = $"{displayName} {suffix}";
        return string.IsNullOrWhiteSpace(effect.IconPath)
            ? label
            : $"{InlineIcon(effect.IconPath)} {label}";
    }

    private static string BlockLabel(string suffix)
    {
        var path = NormalizeResourcePath(BlockIconPath);
        return $"{StatConceptGlossary.RenderInlineImage(path)} {suffix}";
    }

    private static void AppendHealingStats(
        StringBuilder sb,
        RelicAggregate agg,
        string lostLabel = "healing lost",
        string reasonPrefix = "lost to",
        int? averageActivations = null)
    {
        Row3(sb, "HP healed", FormatDecimal(agg.TotalHealingRestored), "");
        if (averageActivations.HasValue)
        {
            var healedPerActivation = averageActivations.Value <= 0
                ? 0m
                : agg.TotalHealingRestored / averageActivations.Value;
            Row3(
                sb,
                "Avg HP healed per activation",
                FormatDecimal(healedPerActivation),
                "",
                "Average HP actually restored per activation of this relic.");
        }

        Row3(sb, lostLabel, FormatDecimal(agg.TotalHealingLost), "");

        if (agg.TotalHealingLost <= 0m) return;

        foreach (var reason in NonRedundantHealingLostReasons(agg))
        {
            var reasonName = string.IsNullOrWhiteSpace(reason.DisplayName)
                ? "other/prevented causes"
                : StatsTooltip.EscapeBbcode(reason.DisplayName);
            DescribedIconRow(
                sb,
                ["healing_blocked"],
                [],
                $"{reasonPrefix} {reasonName}",
                FormatDecimal(reason.Amount),
                $"Healing from this relic that did not restore HP because of {reasonName}.");
        }
    }

    private static IReadOnlyList<HealingLostReasonAggregate> NonRedundantHealingLostReasons(
        RelicAggregate agg)
    {
        var reasons = agg.HealingLostReasons.Values
            .Where(reason => reason.Amount > 0m)
            .OrderByDescending(reason => reason.Amount)
            .ThenBy(reason => reason.DisplayName)
            .ToList();

        if (reasons.Count == 1 && reasons[0].Amount == agg.TotalHealingLost)
            return Array.Empty<HealingLostReasonAggregate>();

        return reasons;
    }

    private static void AppendEnergyGeneratedStats(
        StringBuilder sb,
        RelicAggregate agg,
        string totalLabel = "Energy generated",
        bool includeAveragePerCombat = false,
        string averageLabel = "Avg energy gained per combat",
        int? combatCount = null,
        bool includeCombatsHeld = false)
    {
        Row3(sb, EnergyLabel(totalLabel), agg.EnergyGenerated.ToString(), "");
        var combats = combatCount ?? agg.Activations;
        if (includeCombatsHeld)
            Row3(sb, "Combats held", combats.ToString(), "");

        if (!includeAveragePerCombat) return;

        var average = combats <= 0
            ? 0m
            : (decimal)agg.EnergyGenerated / combats;
        Row3(sb, EnergyLabel(averageLabel), FormatDecimal(average), "");
    }

    private static string FormatDecimal(decimal value)
    {
        return decimal.Truncate(value) == value
            ? value.ToString("0")
            : value.ToString("0.##");
    }

    private static string FormatSignedDecimal(decimal value)
    {
        var formatted = FormatDecimal(value);
        return value > 0m ? $"+{formatted}" : formatted;
    }

    private static string FormatFloor(int? floor)
    {
        return floor.HasValue && floor.Value > 0
            ? floor.Value.ToString()
            : "0";
    }

    private static void AppendMaxHpChangeRows(
        StringBuilder sb,
        RelicAggregate agg,
        string deltaLabel,
        decimal delta)
    {
        Row3(sb, "Original max HP", FormatDecimal(OriginalMaxHp(agg)), "");
        Row3(sb, "New max HP", FormatDecimal(NewMaxHp(agg)), "");
        Row3(sb, deltaLabel, FormatDecimal(Math.Max(0m, delta)), "");
    }

    private static void AppendCardTransformationRows(StringBuilder sb, RelicAggregate agg, int expectedCount)
    {
        var transformations = agg.CardTransformations ?? new List<RelicCardTransformationAggregate>();
        for (var i = 0; i < expectedCount; i++)
        {
            var transformation = i < transformations.Count ? transformations[i] : null;
            TextValueRow(sb, $"Transform {i + 1} source", CardTransformationDisplay(
                transformation?.SourceDisplayName,
                transformation?.SourceCardId), "");
            TextValueRow(sb, $"Transform {i + 1} result", CardTransformationDisplay(
                transformation?.ResultDisplayName,
                transformation?.ResultCardId), "");
        }
    }

    private static string CardTransformationDisplay(string? displayName, string? cardId)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
            return StatsTooltip.EscapeBbcode(displayName);

        if (!string.IsNullOrWhiteSpace(cardId))
            return StatsTooltip.EscapeBbcode(RunTracker.FormatCardIdForDisplay(cardId));

        return "0";
    }

    private static decimal OriginalMaxHp(RelicAggregate agg)
        => agg.OriginalMaxHp ?? agg.StartingMaxHp ?? 0m;

    private static decimal NewMaxHp(RelicAggregate agg)
        => agg.NewMaxHp ?? agg.ResultingMaxHp ?? 0m;

    private static decimal MaxHpGained(RelicAggregate agg)
        => Math.Max(0m, NewMaxHp(agg) - OriginalMaxHp(agg));

    private static decimal MaxHpLost(RelicAggregate agg)
        => Math.Max(0m, OriginalMaxHp(agg) - NewMaxHp(agg));

    private static string EnergyLabel(string suffix)
    {
        return $"{StatEnergyIcon.RenderInline(StatConceptGlossary.IconSlotSize)} {suffix}";
    }

    private static string DrawLabel(string suffix)
    {
        var path = NormalizeResourcePath(DrawIconPath);
        return $"{StatConceptGlossary.RenderInlineImage(path)} {suffix}";
    }

    private static string BrilliantScarfCostLabel(int energyCost, int starCost)
    {
        var energyIcon = StatEnergyIcon.RenderInline(16);
        if (starCost > 0)
        {
            var starIcon = InlineIcon(StarIconPath);
            return $"{Math.Max(0, energyCost)} {energyIcon} {Math.Max(0, starCost)} {starIcon} cost reduced";
        }

        return $"{Math.Max(0, energyCost)} {energyIcon} cost reduced";
    }

    private static string BrilliantScarfCostDescription(int energyCost, int starCost)
    {
        var normalizedEnergy = Math.Max(0, energyCost);
        var normalizedStars = Math.Max(0, starCost);
        var starPart = normalizedStars > 0
            ? $" and {normalizedStars} Star{(normalizedStars == 1 ? string.Empty : "s")}"
            : string.Empty;
        return $"Cards discounted by Brilliant Scarf with a cost of "
               + $"{normalizedEnergy} Energy{starPart}.";
    }

    private static int BrilliantScarfCostCount(RelicAggregate agg, int energyCost, int starCost)
    {
        if (agg.DiscountedCardCosts == null) return 0;
        return agg.DiscountedCardCosts.Values
            .Where(b => b.EnergyCost == energyCost && b.StarCost == starCost)
            .Sum(b => Math.Max(0, b.Count));
    }

    private static IEnumerable<DiscountedCardCostAggregate> DynamicBrilliantScarfCostBuckets(RelicAggregate agg)
    {
        if (agg.DiscountedCardCosts == null) return Enumerable.Empty<DiscountedCardCostAggregate>();

        return agg.DiscountedCardCosts.Values
            .Where(b => b.Count > 0)
            .Select(b => new DiscountedCardCostAggregate
            {
                EnergyCost = Math.Max(0, b.EnergyCost),
                StarCost = Math.Max(0, b.StarCost),
                Count = Math.Max(0, b.Count),
            })
            .Where(b => b.StarCost > 0 || b.EnergyCost > 3)
            .GroupBy(b => new { b.EnergyCost, b.StarCost })
            .Select(g => new DiscountedCardCostAggregate
            {
                EnergyCost = g.Key.EnergyCost,
                StarCost = g.Key.StarCost,
                Count = g.Sum(b => b.Count),
            })
            .OrderBy(b => b.EnergyCost)
            .ThenBy(b => b.StarCost);
    }

    private static string InlineIcon(string path)
    {
        var normalized = NormalizeResourcePath(path);
        return StatConceptGlossary.RenderInlineImage(normalized);
    }

    private static string VigorLabel(string suffix)
    {
        var path = NormalizeResourcePath(VigorIconPath);
        return $"{StatConceptGlossary.RenderInlineImage(path)} {suffix}";
    }

    private static bool IsRelicModel(object model, string typeName)
    {
        for (var type = model.GetType(); type != null; type = type.BaseType)
        {
            if (string.Equals(type.FullName, typeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVambraceUsedThisCombat(Vambrace relic)
    {
        try
        {
            return VambraceBlockGainedThisCombatField?.GetValue(relic) is bool used && used;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAnchorStatsRelicModel(object model)
    {
        return IsRelicModel(model, "MegaCrit.Sts2.Core.Models.Relics.Anchor")
            || IsFakeAnchorRelicModel(model);
    }

    private static bool IsFakeAnchorRelicModel(object model)
    {
        return IsRelicModel(model, "MegaCrit.Sts2.Core.Models.Relics.FakeAnchor");
    }

    private static bool IsStrikeDummyStatsRelicModel(object model)
    {
        return IsRelicModel(model, "MegaCrit.Sts2.Core.Models.Relics.StrikeDummy")
            || IsFakeStrikeDummyRelicModel(model);
    }

    private static bool IsMiniatureCannonStatsRelicModel(object model)
    {
        return IsRelicModel(model, "MegaCrit.Sts2.Core.Models.Relics.MiniatureCannon");
    }

    private static bool IsMrStrugglesStatsRelicModel(object model)
    {
        return IsRelicModel(model, "MegaCrit.Sts2.Core.Models.Relics.MrStruggles");
    }

    private static bool IsFakeStrikeDummyRelicModel(object model)
    {
        return IsRelicModel(model, "MegaCrit.Sts2.Core.Models.Relics.FakeStrikeDummy");
    }

    private static string NormalizeResourcePath(string path)
    {
        return path.StartsWith("res://", StringComparison.Ordinal)
            ? path
            : $"res://{path.TrimStart('/')}";
    }

    private static void Row3(
        StringBuilder sb,
        string label,
        string value,
        string pct,
        string? fullDescription = null)
    {
        var presentation = RelicStatRowVocabulary.Create(label, fullDescription);
        DescribedIconRow(
            sb,
            presentation.ConceptIds,
            presentation.DenominatorConceptIds,
            presentation.Label,
            value,
            presentation.FullDescription,
            pct);
    }

    private static void ConceptRow(
        StringBuilder sb,
        string conceptId,
        string value,
        string fullDescription,
        string pct = "")
    {
        DescribedIconRow(
            sb,
            [conceptId],
            [],
            string.Empty,
            value,
            fullDescription,
            pct);
    }

    private static void RelicActivationRow(
        StringBuilder sb,
        string value,
        string fullDescription = "Activations — the number of times this relic has activated.")
    {
        ConceptRow(
            sb,
            "activation",
            value,
            fullDescription);
    }

    /// <summary>
    /// One row for an offer bucket and the subset of it the player actually
    /// took. The two numbers only mean anything next to each other, so they
    /// share a row instead of forcing the reader to pair up two.
    /// </summary>
    private static void OfferedTakenRow(
        StringBuilder sb,
        IReadOnlyList<string> itemConceptIds,
        string label,
        int offered,
        int taken,
        string fullDescription)
    {
        DescribedIconRow(
            sb,
            [.. itemConceptIds, "offered", "taken"],
            [],
            label,
            FormatOfferedTaken(offered, taken),
            fullDescription);
    }

    private static string FormatOfferedTaken(int offered, int taken)
        => $"{Math.Max(0, offered)}/{Math.Max(0, taken)}";

    private static void AppendLiveActivationRows(
        StringBuilder sb,
        RelicLiveActivationCounts? liveActivationCounts)
    {
        if (!liveActivationCounts.HasValue) return;

        DescribedIconRow(
            sb,
            ["activation"],
            [],
            "this turn",
            liveActivationCounts.Value.ThisTurn.ToString(),
            "Times activated this turn — activations since the current player turn began.");
        DescribedIconRow(
            sb,
            ["activation"],
            [],
            "this combat",
            liveActivationCounts.Value.ThisCombat.ToString(),
            "Times activated this combat — activations since the current combat began.");
    }

    private static void DescribedIconRow(
        StringBuilder sb,
        IReadOnlyList<string> conceptIds,
        IReadOnlyList<string> denominatorConceptIds,
        string label,
        string value,
        string fullDescription,
        string pct = "")
    {
        var presentation = CreateRowPresentation(
            label,
            fullDescription,
            conceptIds,
            denominatorConceptIds);
        StatsTooltip.AppendScalarStatRow(sb, presentation, value, pct);
    }

    private static void TextValueRow(StringBuilder sb, string label, string value, string pct)
    {
        var presentation = RelicStatRowVocabulary.Create(label);
        DescribedIconFlowRow(
            sb,
            presentation.ConceptIds,
            presentation.DenominatorConceptIds,
            presentation.Label,
            value,
            presentation.FullDescription,
            pct);
    }

    private static void DescribedIconFlowRow(
        StringBuilder sb,
        IReadOnlyList<string> conceptIds,
        IReadOnlyList<string> denominatorConceptIds,
        string label,
        string value,
        string fullDescription,
        string pct = "")
    {
        var presentation = CreateRowPresentation(
            label,
            fullDescription,
            conceptIds,
            denominatorConceptIds);
        sb.Append("[table=2]");
        sb.Append("[cell expand=0 padding=0,0,10,0]");
        sb.Append(StatConceptGlossary.RenderInformationHint(
            presentation.FullDescription));
        sb.Append("[/cell]");
        sb.Append("[cell expand=4 padding=0,0,4,0]");
        AppendConceptLabel(
            sb,
            presentation.ConceptIds,
            presentation.DenominatorConceptIds,
            presentation.Label);
        if (!string.IsNullOrEmpty(value))
        {
            sb.Append($"  [b]{value}[/b]");
        }
        if (!string.IsNullOrEmpty(pct))
        {
            sb.Append($"  [color=#b5b5b5]{pct}[/color]");
        }
        sb.Append("[/cell]");
        sb.Append(StatsTableClose);
    }

    private static RelicStatRowPresentation CreateRowPresentation(
        string label,
        string fullDescription,
        IReadOnlyList<string> conceptIds,
        IReadOnlyList<string> denominatorConceptIds)
    {
        if (!IsPrecomposedLabelMarkup(label))
        {
            return StatsTooltip.CreateStatRowPresentation(
                label,
                fullDescription,
                conceptIds,
                denominatorConceptIds);
        }

        var presentation = StatsTooltip.CreateStatRowPresentation(
            string.Empty,
            fullDescription,
            conceptIds,
            denominatorConceptIds);
        return presentation with { Label = label };
    }

    private static bool IsPrecomposedLabelMarkup(string label)
        => label.StartsWith("[b]", StringComparison.Ordinal)
           || label.Contains("[hint", StringComparison.Ordinal)
           || label.Contains("[img", StringComparison.Ordinal);

    private static void AppendConceptLabel(
        StringBuilder sb,
        IReadOnlyList<string> conceptIds,
        IReadOnlyList<string> denominatorConceptIds,
        string label)
        => StatsTooltip.AppendConceptLabel(
            sb,
            conceptIds,
            denominatorConceptIds,
            string.IsNullOrWhiteSpace(label)
                ? string.Empty
                : $"[color=#e0e0e0]{label}[/color]");

}
