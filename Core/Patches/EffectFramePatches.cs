using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Cards;
using SpireLens.Core.Effects;

namespace SpireLens.Core.Patches;

/// <summary>
/// Frames every async hook override declared by an enrolled relic or power
/// (<see cref="EffectRegistry"/>). One patch class, many targets — the same
/// TargetMethods technique as <c>HpLossPowerDamageSourcePatch</c>.
///
/// The prefix pushes the owner's <see cref="EffectFrame"/> into an
/// AsyncLocal; the original hook's state machine captures it, so every
/// command the hook awaits sees it. The postfix restores the caller's frame
/// immediately and closes this frame when the hook's task completes.
///
/// Hooks are invoked through <c>AbstractModel</c> references
/// (<c>await model.AfterX(...)</c> in <c>Hook</c>), a virtual call the JIT
/// cannot devirtualize to an inlined body, so the detour is reached.
/// </summary>
[HarmonyPatch]
internal static class EffectFrameTaskPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var methods = EffectRegistry.TaskHookMethods().ToList();
        CoreMain.LogDebug($"EffectFrameTaskPatch: framing {methods.Count} enrolled hook overrides");
        return methods;
    }

    [HarmonyPrefix]
    public static void Prefix(
        AbstractModel __instance,
        MethodBase __originalMethod,
        out EffectFrameState __state)
    {
        __state = default;
        try
        {
            var frame = RunTracker.TryCreateEffectFrame(__instance, __originalMethod.Name);
            if (frame == null) return;
            __state = new EffectFrameState(frame, EffectFrameContext.Push(frame), true);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"EffectFrameTaskPatch.Prefix failed: {e.Message}");
        }
    }

    [HarmonyPostfix]
    public static void Postfix(ref Task __result, EffectFrameState __state)
    {
        if (!__state.Pushed) return;
        EffectFrameContext.Restore(__state.Previous);
        try
        {
            if (__result == null || __result.IsCompleted)
            {
                RunTracker.CloseEffectFrame(__state.Frame!);
                return;
            }

            __result = CloseWhenDone(__result, __state.Frame!);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"EffectFrameTaskPatch.Postfix failed: {e.Message}");
        }
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(Exception? __exception, EffectFrameState __state)
    {
        if (__exception != null && __state.Pushed)
            EffectFrameContext.Restore(__state.Previous);
        return __exception;
    }

    private static async Task CloseWhenDone(Task inner, EffectFrame frame)
    {
        try
        {
            await inner;
        }
        finally
        {
            RunTracker.CloseEffectFrame(frame);
        }
    }

    public readonly record struct EffectFrameState(
        EffectFrame? Frame,
        EffectFrame? Previous,
        bool Pushed);
}

/// <summary>
/// Turn-start hand-draw modifiers on enrolled models. <c>Hook.ModifyHandDraw</c>
/// is only called by <c>CombatManager</c> as the turn's opening draw is sized,
/// so each call is one real draw. The game itself compares the truncated
/// counts to decide which modifiers changed the draw; this does the same.
/// </summary>
[HarmonyPatch]
public static class EffectHandDrawModifierPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
        => EffectRegistry.DecimalModifierMethods("ModifyHandDraw", "ModifyHandDrawLate");

    [HarmonyPostfix]
    public static void Postfix(AbstractModel __instance, object[] __args, decimal __result)
    {
        try
        {
            // Positional: overrides may rename (Player player, decimal count).
            if (__args.Length < 2 || __args[0] is not Player player || __args[1] is not decimal count) return;
            int delta = (int)__result - (int)count;
            if (delta == 0) return;
            RunTracker.CreditEffectOwnerDirect(__instance, player, EffectOutcomeKeys.HandDrawBonus, delta);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"EffectHandDrawModifierPatch failed: {e.Message}");
        }
    }
}

/// <summary>
/// Max-Energy modifiers on enrolled models. <c>PlayerCombatState.MaxEnergy</c>
/// is re-evaluated by UI as well, so the modifier itself is not a turn
/// signal; the energy reset is. Each enrolled model's marginal change is
/// replayed against the player's base max energy once per reset.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterEnergyReset))]
public static class EffectMaxEnergyResetPatch
{
    [HarmonyPrefix]
    public static void Prefix(Player player)
    {
        try
        {
            RunTracker.RecordEffectMaxEnergyBonuses(player);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"EffectMaxEnergyResetPatch failed: {e.Message}");
        }
    }
}

/// <summary>Relic flashes are the game's own activation signal.</summary>
[HarmonyPatch(
    typeof(RelicModel),
    nameof(RelicModel.Flash),
    new[] { typeof(IEnumerable<Creature>) })]
public static class EffectRelicFlashPatch
{
    [HarmonyPostfix]
    public static void Postfix(RelicModel __instance)
    {
        try
        {
            RunTracker.NoteEffectRelicFlash(__instance);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"EffectRelicFlashPatch failed: {e.Message}");
        }
    }
}

/// <summary>
/// Gold a framed owner — or, with no frame, the card being played — actually
/// added. Measured as the balance delta around the whole command, so gold
/// prevention (Ectoplasm) and truncation count.
/// </summary>
[HarmonyPatch(
    typeof(PlayerCmd),
    nameof(PlayerCmd.GainGold),
    new[] { typeof(decimal), typeof(Player), typeof(bool) })]
internal static class EffectGainGoldPatch
{
    [HarmonyPrefix]
    public static void Prefix(Player player, out GoldGainState __state)
    {
        __state = default;
        try
        {
            if (player == null) return;
            var frame = EffectFrameContext.Current;
            var cardInstanceId = frame == null ? RunTracker.CaptureResolvingCardInstanceId() : null;
            if (frame == null && cardInstanceId == null) return;
            __state = new GoldGainState(frame, cardInstanceId, player.Gold);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"EffectGainGoldPatch.Prefix failed: {e.Message}");
        }
    }

    [HarmonyPostfix]
    public static void Postfix(ref Task __result, Player player, GoldGainState __state)
    {
        if ((__state.Frame == null && __state.CardInstanceId == null) || player == null || __result == null)
            return;
        try
        {
            __result = ObserveAsync(__result, player, __state);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"EffectGainGoldPatch failed: {e.Message}");
        }
    }

    private static async Task ObserveAsync(Task inner, Player player, GoldGainState state)
    {
        await inner;
        try
        {
            int delta = player.Gold - state.Before;
            if (delta <= 0) return;
            if (state.Frame != null)
                RunTracker.CreditEffectFrame(state.Frame, player.Creature, EffectOutcomeKeys.Gold, delta);
            else if (state.CardInstanceId != null)
                RunTracker.CreditCardInstanceOutcome(state.CardInstanceId, EffectOutcomeKeys.Gold, delta);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"EffectGainGoldPatch.ObserveAsync failed: {e.Message}");
        }
    }

    internal readonly record struct GoldGainState(EffectFrame? Frame, string? CardInstanceId, int Before);
}

/// <summary>
/// A card or framed owner auto-playing ANOTHER card (Havoc, Cascade, Mayhem,
/// Hellraiser...). The prefix runs before the auto-played card's own play
/// starts, so the resolving play is still the one that caused it.
/// </summary>
[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.AutoPlay))]
public static class EffectCardAutoPlayPatch
{
    [HarmonyPrefix]
    public static void Prefix(CardModel card)
    {
        try
        {
            if (card == null) return;
            RunTracker.RecordResolvingCardOutcome(EffectOutcomeKeys.CardsAutoPlayed, 1m, otherCard: card);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"EffectCardAutoPlayPatch failed: {e.Message}");
        }
    }
}

/// <summary>Orbs evoked by the card being played (Dualcast, Multi-Cast...)
/// or a framed owner. The orb's own output stays with its channeler.</summary>
[HarmonyPatch]
public static class EffectOrbEvokePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[] { nameof(OrbCmd.EvokeNext), nameof(OrbCmd.EvokeLast) })
        {
            var method = AccessTools.Method(typeof(OrbCmd), name);
            if (method != null) yield return method;
        }
    }

    [HarmonyPrefix]
    public static void Prefix(Player player)
    {
        try
        {
            if (player?.PlayerCombatState?.OrbQueue?.Orbs?.Count is not > 0) return;
            RunTracker.RecordResolvingCardOutcome(EffectOutcomeKeys.OrbsEvoked);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"EffectOrbEvokePatch failed: {e.Message}");
        }
    }
}

/// <summary>Orb passives forced by the card being played (Tesla Coil,
/// Darkness) or a framed owner, rather than by the turn's own trigger.</summary>
[HarmonyPatch(typeof(OrbCmd), nameof(OrbCmd.Passive))]
public static class EffectOrbPassivePatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        try
        {
            RunTracker.RecordResolvingCardOutcome(EffectOutcomeKeys.OrbPassivesTriggered);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"EffectOrbPassivePatch failed: {e.Message}");
        }
    }
}
