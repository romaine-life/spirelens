using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace SpireLens.Core.Patches;

/// <summary>
/// Capture when a specific card instance gets upgraded mid-run. Every
/// <see cref="CardModel.UpgradeInternal"/> call increments
/// <c>CurrentUpgradeLevel</c> and fires the <c>Upgraded</c> event — we
/// postfix it so we can stamp a "card_upgraded" entry into the run's
/// event log alongside plays and damage.
///
/// Why this matters: some cards get cheaper when upgraded (Defect's
/// Coolheaded goes 1→0, Ironclad's Headbutt drops a cost, etc.). Our
/// energy-spent tracking already captures the post-upgrade cost reduction
/// via <c>Resources.EnergySpent</c>, but the UPGRADE ITSELF is a distinct
/// event — knowing when the upgrade happened (floor, combat count) is
/// useful for understanding the cost curve over a run: "I upgraded my
/// Strike at floor 6, so any play before that counted at full cost."
///
/// Hook scope: any upgrade path routes through <c>UpgradeInternal</c>. The
/// prefix snapshots whether the exact object being upgraded is a permanent
/// deck member before the upgrade can mutate or replace it. Source-specific
/// combat stats still observe combat-copy upgrades, but card lineage records
/// only upgrades to that exact deck object.
///
/// This deliberately does not canonicalize a combat clone through
/// <c>DeckVersion</c> for lineage. Canonicalization is correct for plays, but
/// would turn a temporary combat-copy upgrade into a fake permanent upgrade
/// on the deck card's tooltip.
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.UpgradeInternal))]
public static class CardUpgradePatch
{
    [HarmonyPrefix]
    public static void Prefix(CardModel __instance, out bool __state)
    {
        __state = RunTracker.IsExactPermanentDeckCard(__instance);
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, bool __state)
    {
        try
        {
            RunTracker.RecordUpgrade(__instance, __state);
            RunTracker.RecordEffectFrameCardChange(__instance, Effects.EffectOutcomeKeys.CardsUpgraded);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"CardUpgradePatch failed: {e.Message}");
        }
    }
}
