using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace SpireLens.Core.Replay;

/// <summary>
/// Pure, game-independent pieces of the per-decision training records
/// (decisions/*.jsonl, see <c>docs/run-recording.md</c>): the record line
/// shape, legal-option matching, segment naming, and the join that applies
/// the events.jsonl canonical timeline to decision records.
/// </summary>
public static class DecisionFormat
{
    /// <summary>Per-record <c>format_version</c>. Additive changes keep it; breaking ones bump it.</summary>
    public const int FormatVersion = 1;

    public const string PhaseCombat = "combat";
    public const string PhaseRun = "run";

    /// <summary>
    /// Decision records are written into one segment per save point: the
    /// segment opened by the <c>save_point</c> event with seq
    /// <paramref name="anchorSaveSeq"/> (0 before the first save point).
    /// Segment boundaries are exactly the places a load can cut the timeline.
    /// </summary>
    public static string SegmentFileName(long anchorSaveSeq) =>
        "s" + anchorSaveSeq.ToString("D6", CultureInfo.InvariantCulture) + ".jsonl";

    /// <summary>Anchor seq encoded in a segment file name (plain or gzipped, any part suffix); -1 if not a segment.</summary>
    public static long ParseSegmentAnchor(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName[0] != 's') return -1;
        int end = 1;
        while (end < fileName.Length && char.IsDigit(fileName[end])) end++;
        return end > 1 && long.TryParse(fileName.AsSpan(1, end - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : -1;
    }

    private static readonly JsonSerializerOptions LineOptions = new() { WriteIndented = false };

    /// <summary>One decisions/*.jsonl line (no trailing newline).</summary>
    public static string FormatRecord(DecisionRecordParts r)
    {
        var line = new Dictionary<string, object?>
        {
            ["format_version"] = FormatVersion,
            ["dseq"] = r.DecisionSeq,
            ["event_seq"] = r.EventSeq,
            ["event_type"] = r.EventType,
            ["anchor_save_seq"] = r.AnchorSaveSeq,
            ["t"] = r.Utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["kind"] = r.Kind,
            ["phase"] = r.Phase,
            ["player"] = r.Player,
            ["context"] = r.Context,
            ["chosen"] = r.Chosen,
            ["chosen_index"] = r.ChosenIndex,
            ["chosen_indexes"] = r.ChosenIndexes,
            ["options"] = r.Options,
            ["options_complete"] = r.OptionsComplete,
            ["selection"] = r.Selection,
            ["state"] = r.State,
            ["hidden"] = r.Hidden,
            ["net_state"] = r.NetStateForm == null
                ? null
                : new Dictionary<string, object?> { ["form"] = r.NetStateForm, ["b64"] = r.NetStateBase64 },
            ["notes"] = r.Notes is { Count: > 0 } ? r.Notes : null,
        };
        return JsonSerializer.Serialize(line, LineOptions);
    }

    /// <summary>Read the join keys of one record line; false for torn or foreign lines.</summary>
    public static bool TryParseLink(string line, out long decisionSeq, out long eventSeq)
    {
        decisionSeq = eventSeq = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("dseq", out var d) && d.TryGetInt64(out decisionSeq)
                && root.TryGetProperty("event_seq", out var e) && e.TryGetInt64(out eventSeq);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Index of the option the player took, matching on the option's identity
    /// (kind plus card / target / slot / index / id), not on display fields.
    /// Null when the chosen action is not among the enumerated options (the
    /// enumeration missed something, which a trainer should treat as an
    /// incomplete option set).
    /// </summary>
    public static int? FindChosenIndex(IReadOnlyList<LegalOption> options, LegalOption chosen)
    {
        for (int i = 0; i < options.Count; i++)
            if (options[i].SameChoice(chosen)) return i;
        return null;
    }

    /// <summary>
    /// Records whose linked event is on the canonical path. A load cuts
    /// decision records exactly like the events they hang off, because every
    /// record joins to one event (<c>event_seq</c>).
    /// </summary>
    public static IReadOnlyList<T> CanonicalRecords<T>(
        ReplayTimeline timeline,
        IEnumerable<T> records,
        Func<T, long> eventSeqOf)
    {
        var canonical = new HashSet<long>(timeline.Canonical.Select(e => e.Seq));
        return records.Where(r => canonical.Contains(eventSeqOf(r))).ToList();
    }
}

/// <summary>
/// The identity of one legal option. <see cref="SameChoice"/> compares the
/// identity fields only; <see cref="Label"/> is display.
/// </summary>
public sealed record LegalOption(
    string Kind,
    uint? CombatCardIndex = null,
    uint? TargetCombatId = null,
    ulong? TargetPlayerId = null,
    int? PotionSlot = null,
    int? Index = null,
    string? Id = null,
    string? Label = null)
{
    public bool SameChoice(LegalOption other) =>
        Kind == other.Kind
        && CombatCardIndex == other.CombatCardIndex
        && TargetCombatId == other.TargetCombatId
        && TargetPlayerId == other.TargetPlayerId
        && PotionSlot == other.PotionSlot
        && Index == other.Index
        && Id == other.Id;

    public Dictionary<string, object?> ToDictionary()
    {
        var d = new Dictionary<string, object?> { ["kind"] = Kind };
        if (CombatCardIndex.HasValue) d["combat_card_index"] = CombatCardIndex;
        if (TargetCombatId.HasValue) d["target"] = TargetCombatId;
        if (TargetPlayerId.HasValue) d["target_player"] = TargetPlayerId;
        if (PotionSlot.HasValue) d["slot"] = PotionSlot;
        if (Index.HasValue) d["index"] = Index;
        if (Id != null) d["id"] = Id;
        if (Label != null) d["label"] = Label;
        return d;
    }
}

/// <summary>Everything one decision record line carries.</summary>
public sealed class DecisionRecordParts
{
    public long DecisionSeq { get; init; }
    public long EventSeq { get; init; }
    public string EventType { get; init; } = "";
    public long AnchorSaveSeq { get; init; }
    public DateTime Utc { get; init; }
    public string Kind { get; init; } = "";
    public string Phase { get; init; } = DecisionFormat.PhaseRun;
    public ulong? Player { get; init; }
    public IReadOnlyDictionary<string, object?>? Context { get; init; }
    public object? Chosen { get; init; }
    public int? ChosenIndex { get; init; }
    public IReadOnlyList<int>? ChosenIndexes { get; init; }
    public IReadOnlyList<object?>? Options { get; init; }
    public bool OptionsComplete { get; init; } = true;
    public object? Selection { get; init; }
    public object? State { get; init; }
    public object? Hidden { get; init; }
    public string? NetStateForm { get; init; }
    public string? NetStateBase64 { get; init; }
    public IReadOnlyList<string>? Notes { get; init; }
}

/// <summary>
/// The game's map travelability rule (<c>NMapScreen</c>'s point-state
/// refresh), over plain coordinates: nothing visited yet means the act's
/// starting point; standing on the boss with a second boss means the second
/// boss; standing on the last row means the boss; otherwise whatever
/// <c>MapTravel.GetTravelablePointsFrom</c> allows (children, or the whole
/// next row under free travel such as Winged Boots), falling back to the
/// starting point when the current point is unknown. Used only when the map
/// screen's own node states are unavailable.
/// </summary>
public static class MapTravelRule
{
    public static IReadOnlyList<(int Col, int Row)> Travelable(
        IReadOnlyList<(int Col, int Row)> visited,
        int rowCount,
        (int Col, int Row) start,
        (int Col, int Row) boss,
        (int Col, int Row)? secondBoss,
        Func<(int Col, int Row), IReadOnlyList<(int Col, int Row)>?> travelableFrom)
    {
        if (visited.Count == 0) return new[] { start };
        var last = visited[visited.Count - 1];
        if (secondBoss.HasValue && last == boss) return new[] { secondBoss.Value };
        if (last.Row == rowCount - 1) return new[] { boss };
        return travelableFrom(last) ?? (IReadOnlyList<(int Col, int Row)>)new[] { start };
    }
}
