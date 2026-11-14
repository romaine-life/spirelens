using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace SpireLens.Core.Patches;

/// <summary>
/// Keeps delayed channels inside the shared Power-card family that caused
/// them. Without this window, Storm can be mistaken for the Power card whose
/// completed play triggered it, while turn-start channels have no card source
/// at all.
/// </summary>
[HarmonyPatch]
internal static class OrbGenerationPowerCallbackStatsPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(
            typeof(LightningRodPower),
            nameof(LightningRodPower.AfterEnergyReset));
        yield return AccessTools.Method(
            typeof(SpinnerPower),
            nameof(SpinnerPower.AfterEnergyReset));
        yield return AccessTools.Method(
            typeof(StormPower),
            nameof(StormPower.AfterCardPlayed));
        yield return AccessTools.Method(
            typeof(TrashToTreasurePower),
            nameof(TrashToTreasurePower.AfterCardGeneratedForCombat));
    }

    [HarmonyPrefix]
    public static void Prefix(
        PowerModel __instance,
        out OrbGenerationPowerWindow? __state)
    {
        __state = null;
        try
        {
            __state = RunTracker.ArmOrbGenerationPower(__instance);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug(
                $"OrbGenerationPowerCallbackStatsPatch.Prefix failed: {e.Message}");
        }
    }

    [HarmonyPostfix]
    public static void Postfix(
        OrbGenerationPowerWindow? __state,
        ref Task __result)
    {
        try
        {
            if (__state == null) return;
            if (__result == null)
            {
                RunTracker.DisarmOrbGenerationPower(__state);
                return;
            }

            __result = ObserveAsync(__result, __state);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug(
                $"OrbGenerationPowerCallbackStatsPatch.Postfix failed: {e.Message}");
            RunTracker.DisarmOrbGenerationPower(__state);
        }
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(
        Exception? __exception,
        OrbGenerationPowerWindow? __state)
    {
        if (__exception != null)
            RunTracker.DisarmOrbGenerationPower(__state);
        return __exception;
    }

    private static async Task ObserveAsync(
        Task inner,
        OrbGenerationPowerWindow window)
    {
        try
        {
            await inner.ConfigureAwait(false);
        }
        finally
        {
            RunTracker.DisarmOrbGenerationPower(window);
        }
    }
}

/// <summary>
/// Dark and Glass damage is emitted after their activation event. This scope
/// identifies the exact tracked orb while its synchronous call into
/// CreatureCmd.Damage is being constructed.
/// </summary>
[HarmonyPatch]
internal static class TrackedCardOrbDamageScopePatch
{
    private static readonly AsyncLocal<OrbModel?> ActiveTrackedOrb = new();

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(DarkOrb), nameof(DarkOrb.Evoke));
        yield return AccessTools.Method(typeof(GlassOrb), nameof(GlassOrb.Passive));
        yield return AccessTools.Method(typeof(GlassOrb), nameof(GlassOrb.Evoke));
    }

    [HarmonyPrefix]
    public static void Prefix(OrbModel __instance, out OrbModel? __state)
    {
        __state = ActiveTrackedOrb.Value;
        ActiveTrackedOrb.Value = RunTracker.IsTrackedCardSourcedOrb(__instance)
            ? __instance
            : null;
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(Exception? __exception, OrbModel? __state)
    {
        ActiveTrackedOrb.Value = __state;
        return __exception;
    }

    internal static OrbModel? GetActiveTrackedOrb(Creature? dealer)
    {
        var orb = ActiveTrackedOrb.Value;
        return orb != null
               && ReferenceEquals(orb.Owner?.Creature, dealer)
            ? orb
            : null;
    }
}

/// <summary>
/// Captures the resolved result split from Dark and Glass orb damage. The
/// final CreatureCmd overload sees both single-target Dark and multi-target
/// Glass calls without counting any intermediate overload twice.
/// </summary>
[HarmonyPatch]
internal static class TrackedCardOrbDamageResultPatch
{
    private static MethodBase? TargetMethod() => CreatureDamageFunnel.Target;

    private static bool Prepare() => TargetMethod() != null;

    [HarmonyPrefix]
    public static void Prefix(Creature? dealer, out OrbModel? __state)
    {
        __state = TrackedCardOrbDamageScopePatch.GetActiveTrackedOrb(dealer);
    }

    [HarmonyPostfix]
    public static void Postfix(
        OrbModel? __state,
        ref Task<IEnumerable<DamageResult>> __result)
    {
        if (__state == null || __result == null) return;
        __result = ObserveDamageAsync(__result, __state);
    }

    private static async Task<IEnumerable<DamageResult>> ObserveDamageAsync(
        Task<IEnumerable<DamageResult>> inner,
        OrbModel orb)
    {
        try
        {
            var results = await inner.ConfigureAwait(false);
            var materialized = results as IReadOnlyList<DamageResult>
                ?? results.ToList();
            RunTracker.RecordCardSourcedOrbDamage(orb, materialized);
            return materialized;
        }
        catch (Exception e)
        {
            CoreMain.LogDebug(
                $"TrackedCardOrbDamageResultPatch observation failed: {e.Message}");
            throw;
        }
    }
}

[HarmonyPatch(typeof(PlasmaOrb), nameof(PlasmaOrb.Passive))]
internal static class TrackedPlasmaOrbPassiveEnergyStatsPatch
{
    [HarmonyPostfix]
    public static void Postfix(PlasmaOrb __instance, ref Task __result)
    {
        if (__result == null || !RunTracker.IsTrackedCardSourcedOrb(__instance))
            return;
        __result = ObserveAsync(__result, __instance);
    }

    private static async Task ObserveAsync(Task inner, PlasmaOrb orb)
    {
        try
        {
            await inner.ConfigureAwait(false);
        }
        finally
        {
            RunTracker.CompletePlasmaOrbEnergyAttribution(orb);
        }
    }
}

[HarmonyPatch(typeof(PlasmaOrb), nameof(PlasmaOrb.Evoke))]
internal static class TrackedPlasmaOrbEvokeEnergyStatsPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        PlasmaOrb __instance,
        ref Task<IEnumerable<Creature>> __result)
    {
        if (__result == null || !RunTracker.IsTrackedCardSourcedOrb(__instance))
            return;
        __result = ObserveAsync(__result, __instance);
    }

    private static async Task<IEnumerable<Creature>> ObserveAsync(
        Task<IEnumerable<Creature>> inner,
        PlasmaOrb orb)
    {
        try
        {
            return await inner.ConfigureAwait(false);
        }
        finally
        {
            RunTracker.CompletePlasmaOrbEnergyAttribution(orb);
        }
    }
}
