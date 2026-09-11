using System;
using System.Collections.Generic;

namespace SpireLens.Core;

/// <summary>Event kinds an armed window may claim. Multiple windows MAY be armed
/// for one kind (e.g. Orichalcum + Cloak Clasp both gain block in one
/// BeforeSideTurnEnd); arbitration is FIFO at consume time.
///
/// PlayerEnergyLedgerOwner names who OWNS an energy gain without crediting any
/// counter for it, which PlayerEnergyGain does. Relics that already measure
/// their own gain from a before/after pool delta need the first and must not
/// have the second, or the generated total would be counted twice.</summary>
internal enum AttributionEventKind
{
    PlayerBlockGain,
    PlayerEnergyGain,
    PlayerEnergyLedgerOwner,
    CardDraw,
}

internal sealed class AttributionWindow
{
    public required string Key { get; init; }
    public required AttributionEventKind Kind { get; init; }
    public object? OwnerId { get; init; }
    public int ArmedAtHistoryCount { get; init; }
    public int MaxHistoryAdvance { get; init; } // -1 = never staled by history
}

/// <summary>Per-combat FIFO registry. At most one window claims a given event;
/// multiple same-kind windows are allowed and claimed oldest-first. Auto-clears
/// with its owning PendingCombat, so `new PendingCombat()` IS the reset — there
/// is no hand-maintained reset list for these windows to drift.</summary>
internal sealed class AttributionWindowRegistry
{
    private readonly List<AttributionWindow> _windows = new();

    public void Arm(string key, AttributionEventKind kind, int currentHistoryCount,
        object? ownerId = null, int maxHistoryAdvance = -1)
    {
        _windows.Add(new AttributionWindow
        {
            Key = key, Kind = kind, OwnerId = ownerId,
            ArmedAtHistoryCount = currentHistoryCount, MaxHistoryAdvance = maxHistoryAdvance,
        });
    }

    /// <summary>Count of armed windows for a kind (used to log overlaps).</summary>
    public int ArmedCount(AttributionEventKind kind)
    {
        int n = 0;
        foreach (var w in _windows) if (w.Kind == kind) n++;
        return n;
    }

    /// <summary>Claim the oldest fresh matching window. Owner-keyed windows only
    /// match a same-reference ownerId. Prunes stale windows. Returns Key or null.</summary>
    public string? TryConsume(AttributionEventKind kind, int currentHistoryCount, object? ownerId = null)
    {
        for (int i = 0; i < _windows.Count; i++)
        {
            var w = _windows[i];
            if (w.Kind != kind) continue;
            if (IsStale(w, currentHistoryCount)) { _windows.RemoveAt(i); i--; continue; }
            if (w.OwnerId != null && !ReferenceEquals(w.OwnerId, ownerId)) continue;
            _windows.RemoveAt(i);
            return w.Key;
        }
        return null;
    }

    public void Disarm(string key, AttributionEventKind kind, object? ownerId = null)
        => _windows.RemoveAll(w => w.Kind == kind && w.Key == key
            && (ownerId == null || ReferenceEquals(w.OwnerId, ownerId)));

    private static bool IsStale(AttributionWindow w, int currentHistoryCount)
        => w.MaxHistoryAdvance >= 0 && currentHistoryCount - w.ArmedAtHistoryCount > w.MaxHistoryAdvance;
}
