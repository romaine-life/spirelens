using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;
using SpireLens.Core.Replay;

namespace SpireLens.Core.Patches;

// Run recording: the player decisions that are neither GameActions nor
// PlayerChoiceSynchronizer choices. In multiplayer each of these is broadcast
// as a small index message from the method patched here, so these methods are
// the game's own "local player decided X" choke points: the UI buttons, the
// MCP bridge (ForceClick) and AutoSlay all end up in them. Prefixes only; they
// record the decision and never alter it.

/// <summary><c>EventSynchronizer.ChooseLocalOption(int index)</c>: event and Ancient option buttons.</summary>
[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption))]
public static class ReplayEventOptionPatch
{
    [HarmonyPrefix]
    public static void Prefix(EventSynchronizer __instance, int index)
    {
        PatchGuard.Run(nameof(ReplayEventOptionPatch), () =>
            ReplayRecorder.OnEventOptionChosen(__instance, index));
    }
}

/// <summary><c>RestSiteSynchronizer.ChooseLocalOption(int index)</c>: rest site options (rest, smith, dig, ...).</summary>
[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.ChooseLocalOption))]
public static class ReplayRestSiteOptionPatch
{
    [HarmonyPrefix]
    public static void Prefix(RestSiteSynchronizer __instance, int index)
    {
        PatchGuard.Run(nameof(ReplayRestSiteOptionPatch), () =>
            ReplayRecorder.OnRestSiteOptionChosen(__instance, index));
    }
}

/// <summary><c>RewardsSetSynchronizer.SelectLocalReward(Reward reward)</c>: taking an item on a rewards screen.</summary>
[HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SelectLocalReward))]
public static class ReplayRewardSelectedPatch
{
    [HarmonyPrefix]
    public static void Prefix(RewardsSetSynchronizer __instance, Reward reward)
    {
        PatchGuard.Run(nameof(ReplayRewardSelectedPatch), () =>
            ReplayRecorder.OnRewardSelected(__instance, reward));
    }
}

/// <summary><c>RewardsSetSynchronizer.SkipLocalRewardsSet()</c>: leaving a rewards screen.</summary>
[HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SkipLocalRewardsSet))]
public static class ReplayRewardsSkippedPatch
{
    [HarmonyPrefix]
    public static void Prefix(RewardsSetSynchronizer __instance)
    {
        PatchGuard.Run(nameof(ReplayRewardsSkippedPatch), () =>
            ReplayRecorder.OnRewardsSkipped(__instance));
    }
}

/// <summary>
/// The shared purchase method (<c>MerchantEntry.OnTryPurchaseUnsynchronized</c>
/// on STS2 v0.113+, <c>OnTryPurchaseWrapper</c> before): shop card / relic /
/// potion purchase attempts, plus card removal on v0.113+ (and before it when
/// reached through the base entry, as AutoSlay does). <c>ignoreCost</c>
/// purchases come from game logic (Lord's Parasol) and are recorded with
/// <c>auto=true</c>.
/// </summary>
[HarmonyPatch]
public static class ReplayMerchantPurchasePatch
{
    private static MethodBase? TargetMethod() => MerchantPurchaseApi.SharedPurchase;

    private static bool Prepare() => TargetMethod() != null;

    [HarmonyPrefix]
    public static void Prefix(MerchantEntry __instance, MerchantInventory? inventory, bool ignoreCost)
    {
        PatchGuard.Run(nameof(ReplayMerchantPurchasePatch), () =>
        {
            if (!MerchantPurchaseApi.IsLocal(__instance)) return;
            ReplayRecorder.OnMerchantPurchaseAttempt(
                __instance, inventory, ignoreCost, MerchantPurchaseApi.SharedPathCancelable(__instance));
        });
    }
}

/// <summary>
/// <c>MerchantCardRemovalEntry.OnTryPurchaseWrapper(MerchantInventory? inventory, bool ignoreCost, bool cancelable)</c>,
/// before STS2 v0.113: the shop's card-removal service as the UI invokes it.
/// The card picked afterwards arrives separately as a PlayerChoice.
/// </summary>
[HarmonyPatch]
public static class ReplayMerchantCardRemovalPatch
{
    private static MethodBase? TargetMethod() => MerchantPurchaseApi.RemovalWrapper;

    private static bool Prepare() => TargetMethod() != null;

    [HarmonyPrefix]
    public static void Prefix(MerchantCardRemovalEntry __instance, MerchantInventory? inventory, bool ignoreCost, bool cancelable)
    {
        PatchGuard.Run(nameof(ReplayMerchantCardRemovalPatch), () =>
            ReplayRecorder.OnMerchantPurchaseAttempt(__instance, inventory, ignoreCost, cancelable));
    }
}

/// <summary><c>OneOffSynchronizer.DoLocalTreasureRoomRewards()</c>: opening the treasure chest.</summary>
[HarmonyPatch(typeof(OneOffSynchronizer), nameof(OneOffSynchronizer.DoLocalTreasureRoomRewards))]
public static class ReplayTreasureChestPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        PatchGuard.Run(nameof(ReplayTreasureChestPatch), () =>
            ReplayRecorder.OnTreasureChestOpened());
    }
}
