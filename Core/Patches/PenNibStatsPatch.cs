using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;

namespace SpireLens.Core.Patches;

/// <summary>
/// Pen Nib contributes by doubling the current attack damage amount. Record
/// that pre-Pen-Nib amount when the real card play is the one being doubled,
/// avoiding target-side downstream multipliers such as Vulnerable.
/// </summary>
[HarmonyPatch(typeof(PenNib), nameof(PenNib.ModifyDamageMultiplicative))]
public static class PenNibModifyDamageMultiplicativePatch
{
    private static readonly AsyncLocal<Stack<DamageFrame>?> DamageFrames = new();

    [HarmonyPostfix]
    public static void Postfix(
        PenNib __instance,
        Creature? target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource,
        CardPlay? cardPlay,
        decimal __result)
    {
        try
        {
            if (__result != 2m) return;
            if (__instance == null || !RunTracker.IsTrackedRelic(__instance)) return;
            if (!props.IsPoweredAttack()) return;
            if (cardSource == null || cardPlay?.Card == null) return;
            if (!ReferenceEquals(cardSource, cardPlay.Card)) return;
            if (cardSource.Type != CardType.Attack) return;
            if (amount <= 0m) return;

            RunTracker.RecordPenNibBaseDamageAdded(ResolveBaseDamageAmount(cardSource, cardPlay, amount));
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"PenNibModifyDamageMultiplicativePatch failed: {e.Message}");
        }
    }

    internal static decimal ResolveBaseDamageAmount(CardModel? cardSource, CardPlay? cardPlay, decimal fallbackAmount)
    {
        var frames = DamageFrames.Value;
        if (frames == null || frames.Count == 0) return fallbackAmount;

        foreach (var frame in frames)
        {
            if (ReferenceEquals(frame.CardSource, cardSource)
                && ReferenceEquals(frame.CardPlay, cardPlay))
            {
                return frame.Amount;
            }
        }

        return fallbackAmount;
    }

    internal static void PushDamageFrame(CardModel? cardSource, CardPlay? cardPlay, decimal amount)
    {
        var frames = DamageFrames.Value;
        if (frames == null)
        {
            frames = new Stack<DamageFrame>();
            DamageFrames.Value = frames;
        }

        frames.Push(new DamageFrame(cardSource, cardPlay, amount));
    }

    internal static void PopDamageFrame()
    {
        var frames = DamageFrames.Value;
        if (frames == null || frames.Count == 0) return;

        frames.Pop();
        if (frames.Count == 0) DamageFrames.Value = null;
    }

    private readonly record struct DamageFrame(CardModel? CardSource, CardPlay? CardPlay, decimal Amount);
}

/// <summary>
/// Counts every Pen Nib-owned attack play before the relic advances its live
/// charge counter.
/// </summary>
[HarmonyPatch(typeof(PenNib), nameof(PenNib.BeforeCardPlayed))]
public static class PenNibBeforeCardPlayedStatsPatch
{
    [HarmonyPrefix]
    public static void Prefix(PenNib __instance, CardPlay cardPlay)
    {
        try
        {
            if (__instance == null || !RunTracker.IsTrackedRelic(__instance)) return;
            RunTracker.RecordPenNibAttackPlayed(__instance, cardPlay);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"PenNibBeforeCardPlayedStatsPatch failed: {e.Message}");
        }
    }
}

/// <summary>
/// Snapshots Pen Nib's charge at the end of each player turn while held.
/// Bound by runtime lookup so a game hook rename does not break build.
/// </summary>
[HarmonyPatch]
public static class HookBeforeSideTurnEndPenNibPatch
{
    private static MethodBase? TargetMethod()
    {
        var hookType = Sts2CoreAssembly()?.GetType("MegaCrit.Sts2.Core.Hooks.Hook", throwOnError: false);
        if (hookType == null) return null;

        return AccessTools.Method(hookType, "BeforeSideTurnEnd")
            ?? AccessTools.Method(hookType, "BeforeTurnEnd");
    }

    private static Assembly? Sts2CoreAssembly()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name == "sts2") return assembly;
        }

        return null;
    }

    private static bool Prepare() => TargetMethod() != null;

    [HarmonyPrefix]
    public static void Prefix(CombatSide side, IEnumerable<Creature> participants)
    {
        try
        {
            if (side != CombatSide.Player) return;
            RunTracker.RecordPenNibTurnEnded(participants);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"HookBeforeSideTurnEndPenNibPatch failed: {e.Message}");
        }
    }
}

/// <summary>
/// Captures the raw damage amount passed into the real damage command, before
/// any hook modifiers run. Pen Nib's multiplicative hook can then record the
/// command's base per-hit amount rather than an intermediate modified value.
/// </summary>
[HarmonyPatch]
public static class PenNibCreatureDamagePatch
{
    private static MethodBase? TargetMethod() => CreatureDamageFunnel.Target;

    private static bool Prepare() => TargetMethod() != null;

    [HarmonyPrefix]
    public static void Prefix(
        decimal amount,
        CardModel? cardSource,
        CardPlay? cardPlay,
        out bool __state)
    {
        __state = false;

        try
        {
            if (amount <= 0m || cardSource == null || cardPlay == null) return;

            PenNibModifyDamageMultiplicativePatch.PushDamageFrame(cardSource, cardPlay, amount);
            __state = true;
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"PenNibCreatureDamagePatch.Prefix failed: {e.Message}");
        }
    }

    [HarmonyPostfix]
    public static void Postfix(bool __state)
    {
        if (!__state) return;

        try
        {
            PenNibModifyDamageMultiplicativePatch.PopDamageFrame();
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"PenNibCreatureDamagePatch.Postfix failed: {e.Message}");
        }
    }
}
