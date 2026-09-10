using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;

namespace SpireLens.Core.Patches;

/// <summary>
/// Track direct card-driven energy gain by patching the player's actual
/// <c>GainEnergy</c> mutation point. We capture the before/after pool so the
/// recorded amount is the REAL delta applied to the player, not just the
/// requested input amount.
///
/// Attribution is delegated to <see cref="RunTracker.RecordEnergyGained"/>,
/// which only records the gain if a card play is currently resolving and the
/// gaining PlayerCombatState belongs to that card's owner.
/// </summary>
[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.GainEnergy))]
public static class PlayerGainEnergyPatch
{
    [HarmonyPrefix]
    public static void Prefix(PlayerCombatState __instance, out int __state)
    {
        __state = __instance.Energy;
    }

    [HarmonyPostfix]
    public static void Postfix(PlayerCombatState __instance, int __state)
    {
        try
        {
            int gained = __instance.Energy - __state;
            if (gained > 0)
            {
                // Single arbitration: a live relic energy window (Gremlin Horn /
                // Happy Flower / Booming Conch) claims the delta; otherwise the
                // resolving card play is credited.
                RunTracker.DispatchPlayerEnergyGain(__instance, gained);
            }
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"PlayerGainEnergyPatch failed: {e.Message}");
        }
    }
}

/// <summary>
/// The spend side of the player energy ledger. Every route the game has to
/// remove energy — a card's cost, an X-cost payment, an enemy drain — ends at
/// <c>PlayerCombatState.LoseEnergy</c>, so this is the one place a spend can
/// be observed without inferring it.
///
/// Inference is exactly what has to be avoided here. Reading the spend off a
/// finished card play instead would mean the pool is already short by the
/// card's cost while the card is still resolving, and a mid-resolution gain
/// would then reconcile that shortfall as WASTE charged LIFO to the newest
/// chunk rather than as a spend consumed FIFO from the oldest.
/// </summary>
[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.LoseEnergy))]
public static class PlayerLoseEnergyPatch
{
    [HarmonyPrefix]
    public static void Prefix(PlayerCombatState __instance, out int __state)
    {
        __state = __instance.Energy;
    }

    [HarmonyPostfix]
    public static void Postfix(PlayerCombatState __instance, int __state)
    {
        try
        {
            int spent = __state - __instance.Energy;
            if (spent > 0)
                RunTracker.DispatchPlayerEnergySpend(__instance, spent);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"PlayerLoseEnergyPatch failed: {e.Message}");
        }
    }
}

/// <summary>
/// The waste side of the player energy ledger. <c>ResetEnergy</c> overwrites
/// the pool with the turn's fresh allowance, so whatever it still holds at
/// this instant expired unspent — the energy equivalent of block being
/// cleared. A prefix is required: after the call the leftover is gone.
///
/// Conservation needs no handling. <c>CombatManager.SetupPlayerTurn</c> asks
/// <c>Hook.ShouldPlayerResetEnergy</c> first and calls
/// <c>AddMaxEnergyToCurrent</c> instead when the pool carries over, so a
/// conserved pool never reaches this method and its chunks stay spendable.
/// </summary>
[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.ResetEnergy))]
public static class PlayerResetEnergyPatch
{
    [HarmonyPrefix]
    public static void Prefix(PlayerCombatState __instance)
    {
        try
        {
            RunTracker.NotePlayerEnergyReset(__instance);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"PlayerResetEnergyPatch failed: {e.Message}");
        }
    }
}
