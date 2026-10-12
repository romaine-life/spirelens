using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;

namespace SpireLens.Core.Patches;

// Feel No Pain's payoff is tracked by the generic effect frames
// (Core/Effects/EffectRegistry.cs, ADR 0004). Only its active-turn counter
// stays bespoke.

/// <summary>
/// Counts later zero-trigger turns while the power remains active. The
/// application turn is counted from the observed PowerReceived entry.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
internal static class HookAfterPlayerTurnStartFeelNoPainStatsPatch
{
    [HarmonyPrefix]
    public static void Prefix(Player player)
    {
        RunTracker.RecordFeelNoPainPowerTurnStarted(player);
    }
}
