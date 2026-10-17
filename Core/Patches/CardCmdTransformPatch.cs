using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Random;

namespace SpireLens.Core.Patches;

/// <summary>
/// Observes actual relic-owned card transform results. Leafy Poultice builds
/// its source list in the relic callback, but only CardCmd.Transform knows the
/// random replacement cards after RNG and deck-add modifiers resolve.
/// </summary>
[HarmonyPatch]
public static class CardCmdTransformPatch
{
    private static MethodBase? TargetMethod()
    {
        return AccessTools.Method(
            typeof(CardCmd),
            nameof(CardCmd.Transform),
            new[] { typeof(IEnumerable<CardTransformation>), typeof(Rng), typeof(CardPreviewStyle) });
    }

    [HarmonyPrefix]
    public static void Prefix(
        ref IEnumerable<CardTransformation> transformations,
        out IReadOnlyList<CardModel>? __state)
    {
        __state = null;

        try
        {
            if (RunTracker.TryCaptureLeafyPoulticeTransformSources(ref transformations, out var orderedSources))
                __state = orderedSources;

            if (Effects.EffectFrameContext.Current != null)
            {
                // Materialize once so counting does not re-run a lazy query
                // the game is about to enumerate itself.
                var materialized = transformations as IReadOnlyCollection<CardTransformation>
                    ?? transformations.ToList();
                transformations = materialized;
                foreach (var transformation in materialized)
                    RunTracker.RecordEffectFrameCardChange(
                        transformation.Original,
                        Effects.EffectOutcomeKeys.CardsTransformed);
            }
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"CardCmdTransformPatch.Prefix failed: {e.Message}");
        }
    }

    [HarmonyPostfix]
    public static void Postfix(
        ref Task<IEnumerable<CardPileAddResult>> __result,
        IReadOnlyList<CardModel>? __state)
    {
        try
        {
            if (__state == null || __result == null) return;
            __result = ObserveAsync(__result, __state);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"CardCmdTransformPatch.Postfix failed: {e.Message}");
        }
    }

    private static async Task<IEnumerable<CardPileAddResult>> ObserveAsync(
        Task<IEnumerable<CardPileAddResult>> inner,
        IReadOnlyList<CardModel> orderedSources)
    {
        var results = (await inner.ConfigureAwait(false)).ToList();
        try
        {
            RunTracker.RecordLeafyPoulticeTransformResults(orderedSources, results);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"CardCmdTransformPatch.ObserveAsync failed: {e.Message}");
        }

        return results;
    }
}
