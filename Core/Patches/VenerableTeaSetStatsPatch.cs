using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace SpireLens.Core.Patches;

/// <summary>
/// Observes both Venerable Tea Set variants at their owner-specific
/// energy-reset callback. The precondition is captured before the callback
/// clears GainEnergyInNextCombat, then the completed callback's actual player
/// energy delta is recorded.
/// </summary>
[HarmonyPatch]
public static class VenerableTeaSetStatsPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(
            typeof(FakeVenerableTeaSet),
            nameof(FakeVenerableTeaSet.AfterEnergyReset));
        yield return AccessTools.Method(
            typeof(VenerableTeaSet),
            nameof(VenerableTeaSet.AfterEnergyReset));
    }

    [HarmonyPrefix]
    public static void Prefix(
        RelicModel __instance,
        Player player,
        out EnergyState __state)
    {
        __state = default;

        try
        {
            if (__instance?.Owner == null || player?.PlayerCombatState == null) return;
            if (!ReferenceEquals(__instance.Owner, player)) return;
            if (!WillGainEnergy(__instance)) return;

            RunTracker.NoteVenerableTeaSetActivationStarted(
                __instance.Id.ToString(),
                player);

            __state = new EnergyState(
                __instance.Id.ToString(),
                __instance,
                player.PlayerCombatState,
                player.PlayerCombatState.Energy);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"VenerableTeaSetStatsPatch.Prefix failed: {e.Message}");
        }
    }

    [HarmonyPostfix]
    public static void Postfix(EnergyState __state, ref Task __result)
    {
        try
        {
            if (__state.Relic == null || __state.CombatState == null) return;
            if (__result == null) return;

            // Replace the returned task so Hook.AfterEnergyReset cannot advance
            // to another relic before this Tea Set's final energy is sampled.
            __result = ObserveAsync(__result, __state);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"VenerableTeaSetStatsPatch.Postfix failed: {e.Message}");
        }
    }

    private static bool WillGainEnergy(RelicModel relic)
        => relic switch
        {
            FakeVenerableTeaSet fakeTeaSet => fakeTeaSet.GainEnergyInNextCombat,
            VenerableTeaSet teaSet => teaSet.GainEnergyInNextCombat,
            _ => false,
        };

    private static async Task ObserveAsync(Task inner, EnergyState state)
    {
        await inner;

        try
        {
            if (state.Relic == null || state.CombatState == null) return;

            RunTracker.RecordVenerableTeaSetActivation(
                state.RelicId,
                state.Relic,
                state.CombatState,
                state.StartingEnergy,
                state.CombatState.Energy);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"VenerableTeaSetStatsPatch.ObserveAsync failed: {e.Message}");
        }
    }

    public readonly record struct EnergyState(
        string? RelicId,
        RelicModel? Relic,
        PlayerCombatState? CombatState,
        int StartingEnergy);
}
