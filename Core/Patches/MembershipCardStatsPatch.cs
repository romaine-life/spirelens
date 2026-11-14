using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models.Relics;

namespace SpireLens.Core.Patches;

/// <summary>
/// Holds the counterfactual price of the merchant entry currently being
/// purchased. MerchantEntry.Cost recomputes the whole ModifyMerchantPrice
/// chain on every read, so the undiscounted price is obtained by reading the
/// same property a second time with only Membership Card's own modifier
/// suppressed. That keeps the measured saving marginal: a second price
/// modifier such as The Courier stays inside both reads and is never credited
/// to Membership Card.
///
/// The snapshot is taken before the purchase runs because
/// OnTryPurchaseWrapper restocks or clears the entry — recalculating _cost for
/// a different item — before Hook.AfterItemPurchased reports the sale.
/// </summary>
internal static class MembershipCardDiscountSnapshot
{
    private static readonly object _gate = new();
    private static MerchantEntry? _entry;
    private static int _undiscountedCost;

    /// <summary>
    /// Armed for exactly one nested MerchantEntry.Cost read, on the same
    /// thread that armed it. The suppression prefix ignores every other
    /// instance and every other thread, so live shop prices are untouched.
    /// </summary>
    [ThreadStatic]
    private static MembershipCard? _suppressedRelic;

    internal static bool IsSuppressed(MembershipCard relic)
        => ReferenceEquals(relic, _suppressedRelic);

    internal static void Capture(MerchantEntry? entry, bool ignoreCost)
    {
        lock (_gate)
        {
            _entry = null;
            _undiscountedCost = 0;
        }

        // A free purchase spends nothing, so there is no discount to observe.
        if (entry == null || ignoreCost) return;

        var player = entry._player;
        if (player == null) return;

        var relic = player.GetRelic<MembershipCard>();
        if (relic == null || !RunTracker.IsTrackedRelic(relic)) return;

        var discountedCost = entry.Cost;

        int undiscountedCost;
        _suppressedRelic = relic;
        try
        {
            undiscountedCost = entry.Cost;
        }
        finally
        {
            _suppressedRelic = null;
        }

        // Equal prices mean the relic did not move this entry — a melted card,
        // another player's shop, or a price already at zero.
        if (undiscountedCost <= discountedCost) return;

        lock (_gate)
        {
            _entry = entry;
            _undiscountedCost = undiscountedCost;
        }
    }

    internal static bool TryConsume(MerchantEntry? entry, out int undiscountedCost)
    {
        lock (_gate)
        {
            undiscountedCost = _undiscountedCost;
            if (entry == null || !ReferenceEquals(entry, _entry)) return false;

            _entry = null;
            _undiscountedCost = 0;
            return true;
        }
    }
}

/// <summary>
/// Neutralizes Membership Card's discount for the single counterfactual price
/// read taken above. Every unarmed call falls through to the game's own
/// implementation.
/// </summary>
[HarmonyPatch(typeof(MembershipCard), nameof(MembershipCard.ModifyMerchantPrice))]
public static class MembershipCardCounterfactualPricePatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        MembershipCard __instance,
        decimal originalPrice,
        ref decimal __result)
    {
        try
        {
            if (__instance == null || !MembershipCardDiscountSnapshot.IsSuppressed(__instance))
                return true;

            __result = originalPrice;
            return false;
        }
        catch (Exception e)
        {
            CoreMain.LogDebug(
                $"MembershipCardCounterfactualPricePatch.Prefix failed: {e.Message}");
            return true;
        }
    }
}

/// <summary>
/// Snapshots the entry price before the shared purchase method mutates the
/// entry.
/// </summary>
[HarmonyPatch]
public static class MerchantEntryPurchaseCostSnapshotPatch
{
    private static MethodBase? TargetMethod() => MerchantPurchaseApi.SharedPurchase;

    private static bool Prepare() => TargetMethod() != null;

    [HarmonyPrefix]
    private static void Prefix(MerchantEntry __instance, bool ignoreCost)
    {
        try
        {
            if (!MerchantPurchaseApi.IsLocal(__instance)) return;
            MembershipCardDiscountSnapshot.Capture(__instance, ignoreCost);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug(
                $"MerchantEntryPurchaseCostSnapshotPatch.Prefix failed: {e.Message}");
        }
    }
}

/// <summary>
/// Before STS2 v0.113, card removal declares its own cancelable purchase
/// wrapper instead of going through the shared one, so it needs the same
/// pre-purchase snapshot.
/// </summary>
[HarmonyPatch]
public static class MerchantCardRemovalPurchaseCostSnapshotPatch
{
    private static MethodBase? TargetMethod() => MerchantPurchaseApi.RemovalWrapper;

    private static bool Prepare() => TargetMethod() != null;

    [HarmonyPrefix]
    private static void Prefix(MerchantCardRemovalEntry __instance, bool ignoreCost)
    {
        try
        {
            MembershipCardDiscountSnapshot.Capture(__instance, ignoreCost);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug(
                $"MerchantCardRemovalPurchaseCostSnapshotPatch.Prefix failed: {e.Message}");
        }
    }
}

/// <summary>
/// Both purchase wrappers reach this hook only after a successful sale, and it
/// carries the gold the game actually took. Pairing it with the pre-purchase
/// snapshot gives the observed saving without trusting the relic's listed 50%.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterItemPurchased))]
public static class MembershipCardItemPurchasedPatch
{
    [HarmonyPrefix]
    private static void Prefix(Player player, MerchantEntry itemPurchased, int goldSpent)
    {
        try
        {
            if (!MembershipCardDiscountSnapshot.TryConsume(
                    itemPurchased,
                    out var undiscountedCost))
            {
                return;
            }

            RunTracker.RecordMembershipCardDiscountedPurchase(
                player,
                undiscountedCost,
                goldSpent);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug(
                $"MembershipCardItemPurchasedPatch.Prefix failed: {e.Message}");
        }
    }
}
