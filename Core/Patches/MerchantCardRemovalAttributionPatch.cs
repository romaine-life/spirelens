using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;

namespace SpireLens.Core.Patches;

/// <summary>
/// Carries the merchant's concrete removal charge into the established
/// CardPileCmd.RemoveFromDeck observation, before STS2 v0.113.
/// DoLocalMerchantCardRemoval starts the private async selector/removal flow
/// before returning its Task, so the AsyncLocal frame is captured by that
/// continuation and can be restored here without keeping unrelated main-thread
/// work attributed to the shopkeeper.
/// </summary>
[HarmonyPatch]
public static class MerchantCardRemovalAttributionPatch
{
    private static MethodBase? TargetMethod() => MerchantPurchaseApi.LocalCardRemoval;

    private static bool Prepare() => TargetMethod() != null;

    [HarmonyPrefix]
    private static void Prefix(int goldCost, out RemovalContextState __state)
    {
        __state = default;

        try
        {
            __state = new RemovalContextState(
                true,
                RunTracker.PushCardRemovalSource("Shopkeeper", Math.Max(0, goldCost)));
        }
        catch (Exception e)
        {
            CoreMain.LogDebug(
                $"MerchantCardRemovalAttributionPatch.Prefix failed: {e.Message}");
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Exception? __exception,
        RemovalContextState __state)
    {
        try
        {
            if (__state.Armed)
                RunTracker.RestoreCardRemovalSource(__state.Previous);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug(
                $"MerchantCardRemovalAttributionPatch.Finalizer failed: {e.Message}");
        }

        return __exception;
    }

    internal readonly record struct RemovalContextState(
        bool Armed,
        CardRemovalSourceFrame? Previous);
}

/// <summary>
/// The same removal charge on STS2 v0.113+, where the entry's own
/// OnTryPurchaseInternal selects and removes the card for whichever player
/// bought it. It reads Cost exactly as the game does when charging, and the
/// AsyncLocal frame flows into its continuation the same way.
/// </summary>
[HarmonyPatch]
public static class MerchantCardRemovalEntryAttributionPatch
{
    private static MethodBase? TargetMethod()
        => MerchantPurchaseApi.Unsynchronized == null
            ? null
            : AccessTools.DeclaredMethod(
                typeof(MerchantCardRemovalEntry),
                "OnTryPurchaseInternal",
                new[] { typeof(MerchantInventory), typeof(bool) });

    private static bool Prepare() => TargetMethod() != null;

    [HarmonyPrefix]
    private static void Prefix(
        MerchantCardRemovalEntry __instance,
        bool ignoreCost,
        out MerchantCardRemovalAttributionPatch.RemovalContextState __state)
    {
        __state = default;

        try
        {
            if (!MerchantPurchaseApi.IsLocal(__instance)) return;
            __state = new MerchantCardRemovalAttributionPatch.RemovalContextState(
                true,
                RunTracker.PushCardRemovalSource(
                    "Shopkeeper",
                    ignoreCost ? 0 : Math.Max(0, __instance.Cost)));
        }
        catch (Exception e)
        {
            CoreMain.LogDebug(
                $"MerchantCardRemovalEntryAttributionPatch.Prefix failed: {e.Message}");
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Exception? __exception,
        MerchantCardRemovalAttributionPatch.RemovalContextState __state)
    {
        try
        {
            if (__state.Armed)
                RunTracker.RestoreCardRemovalSource(__state.Previous);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug(
                $"MerchantCardRemovalEntryAttributionPatch.Finalizer failed: {e.Message}");
        }

        return __exception;
    }
}
