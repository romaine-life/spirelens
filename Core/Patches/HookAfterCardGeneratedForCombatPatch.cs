using HarmonyLib;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace SpireLens.Core.Patches;

/// <summary>
/// Marks supported synthetic meta cards as available once the run has
/// generated one. We patch the generic generated-card hook so every source
/// flows through the same availability boundary.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardGeneratedForCombat))]
public static class HookAfterCardGeneratedForCombatPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel card)
    {
        try
        {
            RunTracker.RecordShivGenerated(card);
            RunTracker.RecordSoulGenerated(card);
            RunTracker.RecordStatusGenerated(card);
            RunTracker.RecordSovereignBladeGenerated(card);
            RunTracker.RecordEffectFrameCardChange(card, Effects.EffectOutcomeKeys.CardsGenerated);
        }
        catch (System.Exception e)
        {
            CoreMain.Logger.Error($"HookAfterCardGeneratedForCombatPatch failed: {e.Message}");
        }
    }
}
