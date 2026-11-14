using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireLens.Core.Patches;

/// <summary>
/// Resolves the shop purchase entry points for whichever STS2 build is running.
/// STS2 v0.113 routes every purchase, card removal included, through
/// <c>MerchantEntry.OnTryPurchaseUnsynchronized</c>, run for every player via
/// <c>MerchantSynchronizer</c>. Earlier builds run only the local player's
/// purchases through <c>MerchantEntry.OnTryPurchaseWrapper</c>, with card removal
/// declaring its own cancelable overload and removing through
/// <c>OneOffSynchronizer.DoLocalMerchantCardRemoval</c>.
/// </summary>
internal static class MerchantPurchaseApi
{
    private static readonly Type[] SharedSignature = { typeof(MerchantInventory), typeof(bool) };

    internal static readonly MethodInfo? Unsynchronized =
        AccessTools.DeclaredMethod(typeof(MerchantEntry), "OnTryPurchaseUnsynchronized", SharedSignature);

    internal static readonly MethodInfo? Wrapper =
        AccessTools.DeclaredMethod(typeof(MerchantEntry), "OnTryPurchaseWrapper", SharedSignature);

    internal static readonly MethodInfo? RemovalWrapper =
        AccessTools.DeclaredMethod(
            typeof(MerchantCardRemovalEntry),
            "OnTryPurchaseWrapper",
            new[] { typeof(MerchantInventory), typeof(bool), typeof(bool) });

    internal static readonly MethodInfo? LocalCardRemoval =
        AccessTools.DeclaredMethod(
            typeof(OneOffSynchronizer),
            "DoLocalMerchantCardRemoval",
            new[] { typeof(int), typeof(bool) });

    private static readonly FieldInfo? RemovalCancelable =
        AccessTools.DeclaredField(typeof(MerchantCardRemovalEntry), "_isCancelable");

    private static readonly MethodInfo? SetRemovalUncancelable =
        AccessTools.DeclaredMethod(typeof(MerchantCardRemovalEntry), "SetPurchaseUncancelable");

    private static readonly PropertyInfo? RunMerchantSynchronizer =
        AccessTools.DeclaredProperty(typeof(RunManager), "MerchantSynchronizer");

    private static readonly MethodInfo? RoomLocalInventory =
        AccessTools.DeclaredMethod(typeof(MerchantRoom), "GetLocalInventory", Type.EmptyTypes);

    /// <summary>
    /// The local player's shop inventory: kept by the run's MerchantSynchronizer
    /// on v0.113+, by the MerchantRoom before.
    /// </summary>
    internal static MerchantInventory? LocalInventory()
    {
        if (RunMerchantSynchronizer != null)
        {
            var synchronizer = RunMerchantSynchronizer.GetValue(RunManager.Instance);
            if (synchronizer == null) return null;
            return AccessTools.Method(synchronizer.GetType(), "GetLocalInventory", Type.EmptyTypes)
                ?.Invoke(synchronizer, null) as MerchantInventory;
        }

        return RunManager.Instance.State?.CurrentRoom is MerchantRoom room
            ? RoomLocalInventory?.Invoke(room, null) as MerchantInventory
            : null;
    }

    /// <summary>The method every purchase attempt passes through.</summary>
    internal static MethodInfo? SharedPurchase => Unsynchronized ?? Wrapper;

    /// <summary>
    /// The synchronized build also runs other players' purchases on this
    /// machine; the earlier build's entry points only ever see the local one.
    /// </summary>
    internal static bool IsLocal(MerchantEntry entry) => LocalContext.IsMe(entry._player);

    /// <summary>
    /// Card removal's cancelability when the purchase goes through the shared
    /// method, which only the synchronized build carries on the entry.
    /// </summary>
    internal static bool? SharedPathCancelable(MerchantEntry entry)
        => Unsynchronized != null && entry is MerchantCardRemovalEntry && RemovalCancelable != null
            ? (bool)RemovalCancelable.GetValue(entry)!
            : null;

    /// <summary>Buys <paramref name="entry"/> as the local player clicking its slot would.</summary>
    internal static Task<bool> PurchaseLocal(MerchantEntry entry, MerchantInventory inventory, bool cancelable)
    {
        if (Unsynchronized != null)
        {
            if (!cancelable && entry is MerchantCardRemovalEntry)
                SetRemovalUncancelable?.Invoke(entry, null);
            var synchronizer = RunMerchantSynchronizer?.GetValue(RunManager.Instance)
                ?? throw new InvalidOperationException("RunManager.MerchantSynchronizer not found");
            var purchase = AccessTools.Method(synchronizer.GetType(), "PurchaseLocalEntry", new[] { typeof(MerchantEntry) })
                ?? throw new MissingMethodException("MerchantSynchronizer", "PurchaseLocalEntry");
            return (Task<bool>)purchase.Invoke(synchronizer, new object[] { entry })!;
        }

        if (entry is MerchantCardRemovalEntry && RemovalWrapper != null)
            return (Task<bool>)RemovalWrapper.Invoke(entry, new object?[] { inventory, false, cancelable })!;

        if (Wrapper == null)
            throw new MissingMethodException(nameof(MerchantEntry), "OnTryPurchaseWrapper");
        return (Task<bool>)Wrapper.Invoke(entry, new object?[] { inventory, false })!;
    }
}
