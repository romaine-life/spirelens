using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Relics;

namespace SpireLens.Core.Patches;

/// <summary>
/// Observes Seal of Gold's exact owner callback. The relic activates only
/// when its owner participates in the side turn and can afford its five-gold
/// cost, then grants energy before losing gold. Wrapping the returned task
/// gives both completed resource deltas without a broad gold-loss hook.
/// </summary>
[HarmonyPatch]
public static class SealOfGoldStatsPatch
{
    private static MethodBase? TargetMethod()
    {
        return AccessTools.Method(
            typeof(SealOfGold),
            nameof(SealOfGold.AfterSideTurnStart),
            new[]
            {
                typeof(CombatSide),
                typeof(IReadOnlyList<Creature>),
                typeof(ICombatState),
            });
    }

    [HarmonyPrefix]
    public static void Prefix(
        SealOfGold __instance,
        IReadOnlyList<Creature> participants,
        out SealOfGoldState __state)
    {
        __state = default;

        try
        {
            if (__instance == null || participants == null) return;

            var owner = __instance.Owner;
            var playerCombatState = owner?.PlayerCombatState;
            if (owner == null || playerCombatState == null) return;
            if (!participants.Contains(owner.Creature)) return;

            int intendedGoldLoss = Math.Max(0, __instance.DynamicVars.Gold.IntValue);
            if (owner.Gold < intendedGoldLoss) return;

            RunTracker.NoteSealOfGoldActivationStarted(owner);

            __state = new SealOfGoldState(
                __instance,
                owner,
                playerCombatState,
                intendedGoldLoss,
                owner.Gold,
                playerCombatState.Energy);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"SealOfGoldStatsPatch.Prefix failed: {e.Message}");
        }
    }

    [HarmonyPostfix]
    public static void Postfix(ref Task __result, SealOfGoldState __state)
    {
        try
        {
            if (__result == null
                || __state.Relic == null
                || __state.Owner == null
                || __state.PlayerCombatState == null)
                return;

            __result = ObserveAsync(__result, __state);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"SealOfGoldStatsPatch.Postfix failed: {e.Message}");
        }
    }

    private static async Task ObserveAsync(Task inner, SealOfGoldState state)
    {
        await inner.ConfigureAwait(false);

        try
        {
            RunTracker.RecordSealOfGoldActivation(
                state.Relic!,
                state.Owner,
                state.IntendedGoldLoss,
                state.InitialGold,
                state.Owner!.Gold,
                state.InitialEnergy,
                state.PlayerCombatState!.Energy);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"SealOfGoldStatsPatch.ObserveAsync failed: {e.Message}");
        }
    }

    public readonly record struct SealOfGoldState(
        SealOfGold? Relic,
        Player? Owner,
        PlayerCombatState? PlayerCombatState,
        int IntendedGoldLoss,
        int InitialGold,
        int InitialEnergy);
}
