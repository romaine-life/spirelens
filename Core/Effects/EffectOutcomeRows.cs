using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MegaCrit.Sts2.Core.Models;

namespace SpireLens.Core.Effects;

/// <summary>One rendered stat row: the label goes through each tooltip's own
/// row vocabulary, so icons and alignment match the hand-written rows.</summary>
internal readonly record struct EffectOutcomeRow(string Label, string Value, string Description);

/// <summary>
/// Turns a generic outcome bag into tooltip rows. Shared by the relic
/// fallback tooltip and the card power-payoff rows so every enrolled owner
/// speaks the same vocabulary.
/// </summary>
internal static class EffectOutcomeRows
{
    public static IReadOnlyList<EffectOutcomeRow> Build(
        IReadOnlyDictionary<string, decimal>? outcomes,
        string ownerName,
        bool includeTriggers = true,
        bool includeRates = true)
    {
        var rows = new List<EffectOutcomeRow>();
        if (outcomes == null || outcomes.Count == 0) return rows;

        outcomes.TryGetValue(EffectOutcomeKeys.Combats, out var combats);

        foreach (var key in EffectOutcomeKeys.DisplayOrder)
        {
            if (!includeTriggers && key == EffectOutcomeKeys.Triggers) continue;
            if (!outcomes.TryGetValue(key, out var value) || value == 0m) continue;

            var label = EffectOutcomeKeys.Label(key);
            rows.Add(new EffectOutcomeRow(
                label,
                Format(value),
                $"{label} by {ownerName} this run (observed)."));

            if (includeRates && combats > 0m && EffectOutcomeKeys.IsCombatRateKey(key))
            {
                rows.Add(new EffectOutcomeRow(
                    $"Avg {label.ToLowerInvariant()} per combat",
                    Format(value / combats),
                    $"{label} by {ownerName} divided by the {Format(combats)} combats it was held for."));
            }
        }

        foreach (var (key, value) in outcomes
                     .Where(kv => EffectOutcomeKeys.TryParsePower(kv.Key, out _, out _))
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (value == 0m) continue;
            EffectOutcomeKeys.TryParsePower(key, out var powerId, out var onEnemy);
            var powerName = PowerDisplayName(powerId);
            var label = onEnemy ? $"{powerName} applied to enemies" : $"{powerName} gained";
            rows.Add(new EffectOutcomeRow(
                label,
                Format(value),
                $"Stacks of {powerName} {(onEnemy ? "put on enemies" : "gained")} through {ownerName} this run."));
        }

        return rows;
    }

    /// <summary>True when the bag holds anything worth a row.</summary>
    public static bool HasVisibleOutcomes(IReadOnlyDictionary<string, decimal>? outcomes)
        => outcomes != null
           && outcomes.Any(kv => kv.Key != EffectOutcomeKeys.Combats && kv.Value != 0m);

    public static string Format(decimal value)
    {
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return rounded == Math.Truncate(rounded)
            ? rounded.ToString("0", CultureInfo.InvariantCulture)
            : rounded.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static readonly Dictionary<string, string> PowerNames = new(StringComparer.Ordinal);

    internal static string PowerDisplayName(string powerId)
    {
        lock (PowerNames)
        {
            if (PowerNames.TryGetValue(powerId, out var cached)) return cached;
        }

        string name = Humanize(powerId);
        try
        {
            var power = ModelDb.GetByIdOrNull<PowerModel>(ModelId.Deserialize(powerId));
            var title = power?.Title.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(title)) name = title;
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"EffectOutcomeRows.PowerDisplayName({powerId}) failed: {e.Message}");
        }

        lock (PowerNames)
        {
            PowerNames[powerId] = name;
        }
        return name;
    }

    /// <summary>"POWER.DEMON_FORM_POWER" → "Demon Form".</summary>
    internal static string Humanize(string modelId)
    {
        var entry = modelId;
        int dot = entry.IndexOf('.');
        if (dot >= 0) entry = entry[(dot + 1)..];
        if (entry.EndsWith("_POWER", StringComparison.Ordinal)) entry = entry[..^"_POWER".Length];
        var words = entry.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
        return string.Join(' ', words);
    }
}
