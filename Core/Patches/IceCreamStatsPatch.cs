using System;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models.Relics;

namespace SpireLens.Core.Patches;

/// <summary>
/// Observes Ice Cream at the game's own reset-or-carry branch.
/// <c>CombatManager.SetupPlayerTurn</c> asks
/// <see cref="Hook.ShouldPlayerResetEnergy"/> once per player turn setup and
/// then either calls <c>ResetEnergy</c> (overwriting the pool with MaxEnergy)
/// or <c>AddMaxEnergyToCurrent</c> (adding to whatever is already there). The
/// energy sitting on the player when this hook answers is therefore exactly the
/// amount the reset would have discarded, which is what Ice Cream conserved.
///
/// The postfix runs before either branch executes, so the read is the leftover
/// pool and not the post-refill total.
///
/// Credit is gated on Ice Cream's own answer rather than the aggregate result.
/// <c>Hook.ShouldPlayerResetEnergy</c> short-circuits on the first listener
/// that returns false and never reports which one, and it also runs for players
/// who do not own the relic in multiplayer.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ShouldPlayerResetEnergy))]
public static class HookShouldPlayerResetEnergyIceCreamPatch
{
    [HarmonyPostfix]
    public static void Postfix(ICombatState combatState, Player player, bool __result)
    {
        try
        {
            _ = combatState;
            if (player == null) return;

            var iceCream = player.Relics?.OfType<IceCream>().FirstOrDefault();
            if (iceCream == null) return;
            if (!RunTracker.IsTrackedRelic(iceCream)) return;

            // Ask the relic directly: an aggregate false could belong to some
            // other listener, and only Ice Cream's own false means Ice Cream is
            // the reason this turn's pool survived.
            var suppressedByIceCream = !__result && !iceCream.ShouldPlayerResetEnergy(player);
            var leftoverEnergy = player.PlayerCombatState?.Energy ?? 0;

            RunTracker.RecordIceCreamEnergyDecision(
                player,
                suppressedByIceCream,
                leftoverEnergy);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"HookShouldPlayerResetEnergyIceCreamPatch failed: {e.Message}");
        }
    }
}
