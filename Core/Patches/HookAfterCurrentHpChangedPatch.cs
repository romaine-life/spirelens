using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;

namespace SpireLens.Core.Patches;

/// <summary>
/// Captures observed HP losses after the game applies prevention or
/// redirection; Osty losses feed summon-body absorbed-damage attribution.
/// Healing is observed at <see cref="CreatureHealObservationPatch"/> instead:
/// CreatureCmd.Heal only reaches this hook in combat, and reports the
/// requested amount rather than the clamped one.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCurrentHpChanged))]
public static class HookAfterCurrentHpChangedPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        ICombatState? combatState,
        Creature creature,
        decimal delta)
    {
        try
        {
            // Positive deltas come from CreatureCmd.Heal, already observed at
            // HealInternal, and from CreatureCmd.SetCurrentHp, whose only
            // HP-raising callers are monsters.
            if (creature == null || delta >= 0m) return;

            RunTracker.RecordRunHpLost(combatState, creature, -delta);
            RunTracker.RecordWhisperingEarringHpLost(
                combatState,
                creature,
                -delta);
            RunTracker.RecordOstyHpLost(creature, -delta);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"HookAfterCurrentHpChangedPatch failed: {e.Message}");
        }
    }
}
