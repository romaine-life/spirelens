using System;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;

namespace SpireLens.Core.Patches;

/// <summary>
/// Paper Phrog changes Vulnerable's multiplier. Track the current damage amount
/// from the Vulnerable hook while inside a real damage command, then record the
/// multiplier delta from the relic's owner-specific method.
/// </summary>
[HarmonyPatch(typeof(PaperPhrog), nameof(PaperPhrog.ModifyVulnerableMultiplier))]
public static class PaperPhrogModifyVulnerableMultiplierPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        PaperPhrog __instance,
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource,
        decimal __result)
    {
        try
        {
            if (__instance == null || !RunTracker.IsTrackedRelic(__instance)) return;
            if (!props.IsPoweredAttack()) return;

            var multiplierDelta = __result - amount;
            if (multiplierDelta <= 0m) return;
            if (!PaperPhrogDamageFrameTracker.TryResolveVulnerableDamageAmount(
                    target,
                    dealer,
                    cardSource,
                    out var currentDamage)
                || currentDamage <= 0m)
            {
                return;
            }

            RunTracker.RecordPaperPhrogVulnerableBonus(__instance, currentDamage * multiplierDelta);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"PaperPhrogModifyVulnerableMultiplierPatch failed: {e.Message}");
        }
    }
}

[HarmonyPatch(typeof(VulnerablePower), nameof(VulnerablePower.ModifyDamageMultiplicative))]
public static class PaperPhrogVulnerableModifyDamagePatch
{
    [HarmonyPrefix]
    public static void Prefix(
        Creature? target,
        decimal amount,
        Creature? dealer,
        CardModel? cardSource,
        CardPlay? cardPlay,
        out bool __state)
    {
        __state = false;

        try
        {
            if (target == null || amount <= 0m) return;
            if (!PaperPhrogDamageFrameTracker.HasActiveDamageCommandFrame(dealer, cardSource, cardPlay)) return;

            PaperPhrogDamageFrameTracker.PushVulnerableDamageFrame(target, dealer, cardSource, amount);
            __state = true;
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"PaperPhrogVulnerableModifyDamagePatch.Prefix failed: {e.Message}");
        }
    }

    [HarmonyPostfix]
    public static void Postfix(bool __state)
    {
        if (!__state) return;

        try
        {
            PaperPhrogDamageFrameTracker.PopVulnerableDamageFrame();
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"PaperPhrogVulnerableModifyDamagePatch.Postfix failed: {e.Message}");
        }
    }
}

[HarmonyPatch]
public static class PaperPhrogCreatureDamagePatch
{
    private static MethodBase? TargetMethod() => CreatureDamageFunnel.Target;

    private static bool Prepare() => TargetMethod() != null;

    [HarmonyPrefix]
    public static void Prefix(
        decimal amount,
        Creature? dealer,
        CardModel? cardSource,
        CardPlay? cardPlay,
        out bool __state)
    {
        __state = false;

        try
        {
            if (amount <= 0m) return;

            PaperPhrogDamageFrameTracker.PushDamageCommandFrame(dealer, cardSource, cardPlay);
            __state = true;
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"PaperPhrogCreatureDamagePatch.Prefix failed: {e.Message}");
        }
    }

    [HarmonyPostfix]
    public static void Postfix(bool __state)
    {
        if (!__state) return;

        try
        {
            PaperPhrogDamageFrameTracker.PopDamageCommandFrame();
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"PaperPhrogCreatureDamagePatch.Postfix failed: {e.Message}");
        }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
public static class HookAfterPlayerTurnStartPaperPhrogPatch
{
    [HarmonyPrefix]
    public static void Prefix(Player player)
    {
        try
        {
            RunTracker.RecordPaperPhrogTurnStarted(player);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"HookAfterPlayerTurnStartPaperPhrogPatch failed: {e.Message}");
        }
    }
}

internal static class PaperPhrogDamageFrameTracker
{
    private static readonly AsyncLocal<Stack<DamageCommandFrame>?> DamageCommandFrames = new();
    private static readonly AsyncLocal<Stack<VulnerableDamageFrame>?> VulnerableFrames = new();

    internal static void PushDamageCommandFrame(Creature? dealer, CardModel? cardSource, CardPlay? cardPlay)
    {
        var frames = DamageCommandFrames.Value;
        if (frames == null)
        {
            frames = new Stack<DamageCommandFrame>();
            DamageCommandFrames.Value = frames;
        }

        frames.Push(new DamageCommandFrame(dealer, cardSource, cardPlay));
    }

    internal static void PopDamageCommandFrame()
    {
        var frames = DamageCommandFrames.Value;
        if (frames == null || frames.Count == 0) return;

        frames.Pop();
        if (frames.Count == 0)
            DamageCommandFrames.Value = null;
    }

    internal static bool HasActiveDamageCommandFrame(Creature? dealer, CardModel? cardSource, CardPlay? cardPlay)
    {
        var frames = DamageCommandFrames.Value;
        if (frames == null || frames.Count == 0) return false;

        foreach (var frame in frames)
        {
            if (ReferenceEquals(frame.Dealer, dealer)
                && ReferenceEquals(frame.CardSource, cardSource)
                && ReferenceEquals(frame.CardPlay, cardPlay))
            {
                return true;
            }
        }

        return false;
    }

    internal static void PushVulnerableDamageFrame(
        Creature target,
        Creature? dealer,
        CardModel? cardSource,
        decimal amount)
    {
        var frames = VulnerableFrames.Value;
        if (frames == null)
        {
            frames = new Stack<VulnerableDamageFrame>();
            VulnerableFrames.Value = frames;
        }

        frames.Push(new VulnerableDamageFrame(target, dealer, cardSource, amount));
    }

    internal static void PopVulnerableDamageFrame()
    {
        var frames = VulnerableFrames.Value;
        if (frames == null || frames.Count == 0) return;

        frames.Pop();
        if (frames.Count == 0)
            VulnerableFrames.Value = null;
    }

    internal static bool TryResolveVulnerableDamageAmount(
        Creature? target,
        Creature? dealer,
        CardModel? cardSource,
        out decimal amount)
    {
        amount = 0m;
        var frames = VulnerableFrames.Value;
        if (frames == null || frames.Count == 0) return false;

        foreach (var frame in frames)
        {
            if (ReferenceEquals(frame.Target, target)
                && ReferenceEquals(frame.Dealer, dealer)
                && ReferenceEquals(frame.CardSource, cardSource))
            {
                amount = frame.Amount;
                return true;
            }
        }

        return false;
    }

    private readonly record struct DamageCommandFrame(
        Creature? Dealer,
        CardModel? CardSource,
        CardPlay? CardPlay);

    private readonly record struct VulnerableDamageFrame(
        Creature Target,
        Creature? Dealer,
        CardModel? CardSource,
        decimal Amount);
}
