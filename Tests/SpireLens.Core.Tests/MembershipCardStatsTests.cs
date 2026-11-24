using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models.Relics;
using SpireLens.Core.Patches;
using Xunit;

namespace SpireLens.Core.Tests;

public class MembershipCardStatsTests
{
    private const string MembershipCardRelicId = "RELIC.MEMBERSHIP_CARD";

    private static readonly MethodInfo BuildBodyMethod =
        typeof(RelicHoverShowPatch).GetMethod(
            "BuildMembershipCardBodyBBCode",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "BuildMembershipCardBodyBBCode not found.");

    [Fact]
    public void Patch_TargetsMembershipCardPriceModifier()
    {
        var target = typeof(MembershipCard).GetMethod(
            nameof(MembershipCard.ModifyMerchantPrice),
            [typeof(Player), typeof(MerchantEntry), typeof(decimal)]);

        Assert.NotNull(target);
        Assert.Equal(typeof(decimal), target!.ReturnType);
    }

    [Fact]
    public void Patch_TargetsBothPurchaseWrappersAndThePurchaseHook()
    {
        // v0.113+ buys through OnTryPurchaseUnsynchronized; earlier builds through
        // OnTryPurchaseWrapper, with card removal's own overload. Named by string,
        // since each build has only one of them (MerchantPurchaseApi resolves the same).
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        Type[] shared = [typeof(MerchantInventory), typeof(bool)];
        var unsynchronized = typeof(MerchantEntry).GetMethod("OnTryPurchaseUnsynchronized", declared, null, shared, null);
        var sharedWrapper = unsynchronized
            ?? typeof(MerchantEntry).GetMethod("OnTryPurchaseWrapper", declared, null, shared, null);
        var purchased = typeof(Hook).GetMethod(nameof(Hook.AfterItemPurchased));

        Assert.NotNull(sharedWrapper);
        Assert.NotNull(purchased);
        Assert.Equal(typeof(Task<bool>), sharedWrapper!.ReturnType);
        if (unsynchronized == null)
        {
            var removalWrapper = typeof(MerchantCardRemovalEntry).GetMethod(
                "OnTryPurchaseWrapper", declared, null, [typeof(MerchantInventory), typeof(bool), typeof(bool)], null);
            Assert.NotNull(removalWrapper);
            Assert.Equal(typeof(Task<bool>), removalWrapper!.ReturnType);
        }
        Assert.Equal(typeof(Task), purchased!.ReturnType);
    }

    [Fact]
    public void PurchaseWrapperRestocksBeforeTheHookReportsTheSale()
    {
        // The pre-purchase snapshot exists because the wrapper mutates the
        // entry between charging the player and announcing the sale. If the
        // game ever reorders those two calls the snapshot can be dropped.
        var restock = typeof(MerchantEntry).GetMethod(
            "RestockAfterPurchase",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var clear = typeof(MerchantEntry).GetMethod(
            "ClearAfterPurchase",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(restock);
        Assert.NotNull(clear);
    }

    [Fact]
    public void RelicAggregate_MembershipCardFields_DefaultToEmpty()
    {
        var aggregate = new RelicAggregate();

        Assert.Equal(0, aggregate.MembershipCardGoldSaved);
        Assert.Null(aggregate.MembershipCardGoldHeldAfterPurchase);
        Assert.Equal(0, aggregate.MembershipCardGoldEarnedAfterPurchase);
    }

    [Fact]
    public void RelicAggregate_MembershipCardFields_JsonRoundtripPreservesValues()
    {
        var run = new RunData();
        run.RelicAggregates[MembershipCardRelicId] = new RelicAggregate
        {
            Activations = 4,
            MembershipCardGoldSaved = 163,
            MembershipCardGoldHeldAfterPurchase = 42,
            MembershipCardGoldEarnedAfterPurchase = 287,
        };

        var json = JsonSerializer.Serialize(run, RunStorage.Options);
        var restored = JsonSerializer.Deserialize<RunData>(json, RunStorage.Options);

        Assert.Contains("membership_card_gold_saved", json);
        Assert.Contains("membership_card_gold_held_after_purchase", json);
        Assert.Contains("membership_card_gold_earned_after_purchase", json);
        Assert.NotNull(restored);
        var relic = restored!.RelicAggregates[MembershipCardRelicId];
        Assert.Equal(163, relic.MembershipCardGoldSaved);
        Assert.Equal(42, relic.MembershipCardGoldHeldAfterPurchase);
        Assert.Equal(287, relic.MembershipCardGoldEarnedAfterPurchase);
    }

    [Fact]
    public void DiscountedPurchase_CountsOnlyTheMarginalSaving()
    {
        var aggregate = new RelicAggregate();

        Assert.True(RunTracker.RecordMembershipCardDiscountedPurchaseForTest(
            aggregate,
            undiscountedCost: 150,
            goldSpent: 75));
        Assert.True(RunTracker.RecordMembershipCardDiscountedPurchaseForTest(
            aggregate,
            undiscountedCost: 61,
            goldSpent: 30));

        Assert.Equal(2, aggregate.Activations);
        Assert.Equal(106, aggregate.MembershipCardGoldSaved);
    }

    [Fact]
    public void DiscountedPurchase_IgnoresUndiscountedAndFreePurchases()
    {
        var aggregate = new RelicAggregate();

        Assert.False(RunTracker.RecordMembershipCardDiscountedPurchaseForTest(
            aggregate,
            undiscountedCost: 75,
            goldSpent: 75));
        // A free purchase reports zero gold spent; crediting the full price
        // would invent a saving Membership Card did not cause.
        Assert.False(RunTracker.RecordMembershipCardDiscountedPurchaseForTest(
            aggregate,
            undiscountedCost: 0,
            goldSpent: 0));

        Assert.Equal(0, aggregate.Activations);
        Assert.Equal(0, aggregate.MembershipCardGoldSaved);
    }

    [Fact]
    public void GoldHeldAfterPurchase_IsStampedOnce()
    {
        var aggregate = new RelicAggregate();

        Assert.True(RunTracker.RecordMembershipCardGoldHeldForTest(aggregate, 42));
        Assert.Equal(42, aggregate.MembershipCardGoldHeldAfterPurchase);
        Assert.False(RunTracker.RecordMembershipCardGoldHeldForTest(aggregate, 900));
        Assert.Equal(42, aggregate.MembershipCardGoldHeldAfterPurchase);
    }

    [Fact]
    public void GoldHeldAfterPurchase_RecordsAnEmptyPurse()
    {
        var aggregate = new RelicAggregate();

        Assert.True(RunTracker.RecordMembershipCardGoldHeldForTest(aggregate, 0));
        Assert.Equal(0, aggregate.MembershipCardGoldHeldAfterPurchase);
    }

    [Fact]
    public void MergeRelicAggregate_MembershipCardFields_AreAdditiveExceptTheStamp()
    {
        var target = new RelicAggregate();

        RunTracker.MergeRelicAggregateInto(
            target,
            new RelicAggregate
            {
                Activations = 1,
                MembershipCardGoldSaved = 37,
                MembershipCardGoldHeldAfterPurchase = 42,
                MembershipCardGoldEarnedAfterPurchase = 100,
            });
        RunTracker.MergeRelicAggregateInto(
            target,
            new RelicAggregate
            {
                Activations = 3,
                MembershipCardGoldSaved = 126,
                MembershipCardGoldHeldAfterPurchase = 900,
                MembershipCardGoldEarnedAfterPurchase = 187,
            });

        Assert.Equal(4, target.Activations);
        Assert.Equal(163, target.MembershipCardGoldSaved);
        Assert.Equal(42, target.MembershipCardGoldHeldAfterPurchase);
        Assert.Equal(287, target.MembershipCardGoldEarnedAfterPurchase);
    }

    [Fact]
    public void RelicTooltip_MembershipCard_ShowsRequestedRows()
    {
        var body = BuildBody(new RelicAggregate
        {
            Activations = 4,
            MembershipCardGoldSaved = 163,
            MembershipCardGoldHeldAfterPurchase = 42,
            MembershipCardGoldEarnedAfterPurchase = 287,
        });

        Assert.Contains("saved", body);
        Assert.Contains("held after purchase", body);
        Assert.Contains("earned after purchase", body);
        Assert.Contains("[b]4[/b]", body);
        Assert.Contains("[b]163[/b]", body);
        Assert.Contains("[b]42[/b]", body);
        Assert.Contains("[b]287[/b]", body);
    }

    [Fact]
    public void RelicTooltip_MembershipCard_MarksAnUnstampedBalance()
    {
        var body = BuildBody(new RelicAggregate());

        Assert.Contains("[b]not recorded[/b]", body);
    }

    [Fact]
    public void RelicTooltip_MembershipCard_DispatchesForModel()
    {
        var relic = (MembershipCard)
            RuntimeHelpers.GetUninitializedObject(typeof(MembershipCard));

        var recognized = RelicHoverShowPatch.TryBuildBodyBBCode(
            relic,
            new RelicAggregate { MembershipCardGoldSaved = 163 },
            floorCount: null,
            out var title,
            out var body);

        Assert.True(recognized);
        Assert.Equal("Membership Card", title);
        Assert.Contains("[b]163[/b]", body);
    }

    private static string BuildBody(RelicAggregate aggregate)
        => (string)(BuildBodyMethod.Invoke(null, new object?[] { aggregate })
            ?? throw new InvalidOperationException(
                "BuildMembershipCardBodyBBCode returned null."));
}
