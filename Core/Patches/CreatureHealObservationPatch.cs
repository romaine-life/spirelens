using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace SpireLens.Core.Patches;

/// <summary>
/// Observes the HP every heal actually restored, in or out of combat.
/// <c>Creature.HealInternal</c> is the mutation inside <c>CreatureCmd.Heal</c>
/// (its only caller) and runs synchronously, so a before/after read of
/// <c>CurrentHp</c> is the post-clamp amount with nothing nested in between.
///
/// <c>Hook.AfterCurrentHpChanged</c> cannot serve for healing: <c>CreatureCmd.Heal</c>
/// only fires it while the creature is attached to a <c>CombatState</c>, which
/// the player is not at rest sites, events, shops or on the map, and it passes
/// the requested amount rather than the clamped one.
/// </summary>
[HarmonyPatch(typeof(Creature), nameof(Creature.HealInternal))]
public static class CreatureHealObservationPatch
{
    [HarmonyPrefix]
    public static void Prefix(Creature __instance, out int __state)
    {
        __state = __instance?.CurrentHp ?? 0;
    }

    [HarmonyPostfix]
    public static void Postfix(Creature __instance, int __state)
    {
        try
        {
            if (__instance == null) return;

            decimal restored = __instance.CurrentHp - __state;
            if (restored <= 0m) return;

            RunTracker.RecordRunHpGained(__instance.CombatState, __instance, restored);
            RunTracker.RecordRelicHealingHpChanged(__instance, restored);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"CreatureHealObservationPatch failed: {e.Message}");
        }
    }
}
