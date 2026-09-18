using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.PotionLab;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.sts2.Core.Nodes.TopBar;

namespace SpireLens.Core.Patches;

/// <summary>
/// Adds SpireLens data to the same IEnumerable&lt;IHoverTip&gt; that the game
/// is about to render. The resulting node is created, positioned, associated
/// with <paramref name="owner"/>, and removed entirely by NHoverTipSet.
/// </summary>
[HarmonyPatch(
    typeof(NHoverTipSet),
    nameof(NHoverTipSet.CreateAndShow),
    new[]
    {
        typeof(Control),
        typeof(IEnumerable<IHoverTip>),
        typeof(HoverTipAlignment),
    })]
internal static class NativeHoverTipCreateStatsPatch
{
    [HarmonyPrefix]
    public static bool Prefix(
        Control owner,
        ref IEnumerable<IHoverTip> hoverTips,
        ref NHoverTipSet? __result,
        out bool __state)
    {
        __state = false;
        if (StatsTooltipPinManager.ShouldSuppressOrdinaryHoverTip(owner))
        {
            __result = null;
            return false;
        }

        try
        {
            if (!NativeStatsHoverTipFactory.TryCreate(owner, out var statsTip))
                return true;

            // Materialize the sequence before native rendering. NHoverTipSet
            // preserves this order, so the SpireLens tip becomes the final
            // text control in the native set and can be styled in Postfix.
            hoverTips = (hoverTips ?? Enumerable.Empty<IHoverTip>())
                .Append(statsTip)
                .ToList();
            __state = true;
        }
        catch (Exception e)
        {
            // Stats presentation must never prevent the game's own tooltip.
            CoreMain.Logger.Error($"Native stats hover-tip creation failed: {e}");
        }

        return true;
    }

    [HarmonyPostfix]
    public static void Postfix(
        Control owner,
        NHoverTipSet? __result,
        bool __state)
    {
        if (!__state || __result == null) return;

        try
        {
            NativeStatsHoverTipStyler.ApplyToLastTextTip(__result);
            if (owner is NMapLegendItem legendItem)
                MapLegendStatsTooltip.KeepInsideViewport(legendItem, __result);
        }
        catch (Exception e)
        {
            // Visual presentation is supplemental. A styling or positioning
            // failure must not interfere with the native tooltip lifecycle.
            CoreMain.Logger.Error(
                $"Native stats hover-tip presentation failed: {e}");
        }
    }
}

/// <summary>
/// Restores SpireLens's visual identity on the native control created for the
/// appended stats tip. The control remains a child of NHoverTipSet; this class
/// retains no node reference and owns none of its lifecycle.
/// </summary>
internal static class NativeStatsHoverTipStyler
{
    internal const string BrandNodeName = "SpireLensBrand";
    private const string BrandSpacerNodeName = "SpireLensBrandSpacer";
    private const string RegularFontPath =
        "res://themes/kreon_regular_glyph_space_one.tres";

    private static readonly Color PanelTint = new(0.6f, 0.68f, 0.88f, 1f);
    private static readonly Color BrandColor = new(0.408f, 0.408f, 0.408f, 1f);
    private static readonly Color ShadowColor = new(0f, 0f, 0f, 0.251f);

    public static void ApplyToLastTextTip(NHoverTipSet tipSet)
    {
        var container = tipSet._textHoverTipContainer;
        if (container == null || container.GetChildCount() == 0) return;

        if (container.GetChild(container.GetChildCount() - 1) is not Control statsTip)
            return;

        // Tint only this tip's background. The rest of the native set keeps
        // the game's standard appearance, making the SpireLens page distinct.
        var background = statsTip.GetNodeOrNull<CanvasItem>("%Bg");
        if (background != null)
            background.SelfModulate = PanelTint;

        // Godot underlines [hint] spans with a dotted rule by default. The
        // symbols already communicate interactivity, so retain their hover
        // hints without adding that visual noise to every stats row.
        var description = statsTip.GetNodeOrNull<RichTextLabel>("%Description");
        if (description != null)
            description.HintUnderlined = false;

        AddBrand(statsTip);
        StatsTooltipPinManager.EnsureCopyImageButton(statsTip);
    }

    public static RichTextLabel? GetLastStatsDescription(NHoverTipSet tipSet)
    {
        return GetLastStatsControl(tipSet)
            ?.GetNodeOrNull<RichTextLabel>("%Description");
    }

    public static Control? GetLastStatsControl(NHoverTipSet tipSet)
    {
        var container = tipSet._textHoverTipContainer;
        if (container == null || container.GetChildCount() == 0) return null;

        return container.GetChild(container.GetChildCount() - 1) as Control;
    }

    private static void AddBrand(Control statsTip)
    {
        var title = statsTip.GetNodeOrNull<Control>("%Title");
        if (title?.GetParent() is not HBoxContainer header)
        {
            CoreMain.LogDebug(
                "SpireLens brand skipped: native hover-tip title is not in an HBoxContainer.");
            return;
        }

        if (header.GetNodeOrNull<Label>(BrandNodeName) != null) return;

        // The native hover-tip scene is a MarginContainer whose header is an
        // HBoxContainer. Preserve every native title setting and give the
        // header a separate expanding spacer; that pushes the camera/brand
        // pair to the right edge without re-aligning the title control.
        var spacer = new Control
        {
            Name = BrandSpacerNodeName,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };

        var brand = new Label
        {
            Name = BrandNodeName,
            Text = "SpireLens",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        brand.AddThemeColorOverride("font_color", BrandColor);
        ApplyBrandFont(brand);
        brand.AddThemeFontSizeOverride("font_size", 14);
        brand.AddThemeColorOverride("font_shadow_color", ShadowColor);
        brand.AddThemeConstantOverride("shadow_offset_x", 3);
        brand.AddThemeConstantOverride("shadow_offset_y", 2);

        header.AddChild(spacer);
        header.AddChild(brand);
    }

    private static void ApplyBrandFont(Label brand)
    {
        try
        {
            // Do not retain a Godot resource wrapper across scene-cache
            // unloads or Core hot reloads. The game may invalidate that
            // wrapper while this orphan-tolerant assembly remains alive.
            var font = ResourceLoader.Load<Font>(
                RegularFontPath,
                typeHint: null,
                ResourceLoader.CacheMode.Reuse);
            if (font != null && GodotObject.IsInstanceValid(font))
                brand.AddThemeFontOverride("font", font);
        }
        catch (Exception e)
        {
            // Branding is supplemental. Inherit the native hover-tip font if
            // the game's resource cache is between unload/reload phases.
            CoreMain.LogDebug($"SpireLens brand font unavailable: {e.Message}");
        }
    }
}

internal static class NativeStatsHoverTipFactory
{
    public static bool TryCreate(Control? owner, out IHoverTip statsTip)
    {
        statsTip = default!;
        if (owner == null || !ViewStatsInjectorPatch.StatsVisibilityEnabled)
            return false;

        HoverTip tip;
        if (StatsTooltipPinManager.TryBuildPinnedStatsTip(owner, out tip))
        {
            statsTip = tip;
            return true;
        }

        switch (owner)
        {
            case NCardHolder cardHolder
                when CardHoverShowPatch.TryBuildNativeHoverTip(cardHolder, out tip):
                statsTip = tip;
                return true;

            case NRelicInventoryHolder relicHolder
                when RelicHoverShowPatch.TryBuildNativeHoverTip(relicHolder, out tip):
                statsTip = tip;
                return true;

            case NMerchantRelic merchantRelic
                when RelicHoverShowPatch.TryBuildMerchantHoverTip(merchantRelic, out tip):
                statsTip = tip;
                return true;

            case NCreature creature
                when EnemyHoverShowPatch.TryBuildNativeHoverTip(creature, out tip):
                statsTip = tip;
                return true;

            case NRelicCollectionEntry entry
                when CompendiumRelicStatsContext.TryBuildNativeHoverTip(entry, out tip):
                statsTip = tip;
                return true;

            case NLabPotionHolder holder
                when PotionCompendiumHistoryUi.TryBuildNativeHoverTip(holder, out tip):
                statsTip = tip;
                return true;

            case NDeckHistoryEntry entry
                when RunHistoryStatsContext.TryBuildNativeCardHoverTip(entry, out tip):
                statsTip = tip;
                return true;

            case NRelicBasicHolder holder
                when RunHistoryStatsContext.TryBuildNativeRelicHoverTip(holder, out tip):
                statsTip = tip;
                return true;

            case NTopBarHp hp:
                StatsTooltipPinManager.AttachTopBarRunStatsTarget(hp);
                if (MaxHpHistoryTooltip.TryBuildNativeHoverTip(hp, out tip))
                {
                    statsTip = tip;
                    return true;
                }
                return false;

            case NTopBarGold gold:
                StatsTooltipPinManager.AttachTopBarRunStatsTarget(gold);
                if (GoldStatsTooltip.TryBuildNativeHoverTip(gold, out tip))
                {
                    statsTip = tip;
                    return true;
                }
                return false;

            case NPotionHolder holder:
                StatsTooltipPinManager.AttachPotionStatsTarget(holder);
                if (PotionBeltStatsTooltip.TryBuildNativeHoverTip(holder, out tip))
                {
                    statsTip = tip;
                    return true;
                }
                return false;

            case NMapLegendItem legendItem
                when MapLegendStatsTooltip.TryBuildNativeHoverTip(legendItem, out tip):
                statsTip = tip;
                return true;

            default:
                return false;
        }
    }
}
