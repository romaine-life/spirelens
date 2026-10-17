using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SpireLens.Core.Replay;

/// <summary>
/// Pure, game-independent pieces of the run-recording format (see
/// <c>docs/run-recording.md</c>): the events.jsonl line shape, the save-point
/// key, and the canonical-timeline cut that turns an append-only log with
/// rewinds into the single path a replay should follow. Kept free of Godot and
/// game types so it can be unit tested and reused by an offline importer.
/// </summary>
public static class ReplayFormat
{
    /// <summary>Bumped only on a breaking change to header.json / events.jsonl.</summary>
    public const int FormatVersion = 1;

    // Event type names. Stable strings: a future replay driver / DB importer keys on them.
    public const string EvRecordingStarted = "recording_started";
    public const string EvRunStarted = "run_started";
    public const string EvCoreReloaded = "core_reloaded";
    public const string EvSavePoint = "save_point";
    public const string EvLoad = "load";
    public const string EvAction = "action";
    public const string EvHookAction = "hook_action";
    public const string EvActionResumed = "action_resumed";
    public const string EvActionResult = "action_result";
    public const string EvChoice = "choice";
    public const string EvChecksum = "checksum";
    public const string EvDecision = "decision";
    public const string EvCombatReplayFile = "combat_replay_file";
    public const string EvRunEnd = "run_end";

    public const string LoadReasonContinue = "continue";
    public const string LoadReasonRoomRestart = "room_restart";
    public const string LoadReasonDeathUndo = "death_undo";

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// One events.jsonl line (no trailing newline). Envelope fields are fixed;
    /// <paramref name="context"/> (floor/act/coord/in_combat) and
    /// <paramref name="data"/> are free-form JSON-serializable values.
    /// </summary>
    public static string FormatLine(
        long seq,
        DateTime utc,
        string type,
        IReadOnlyDictionary<string, object?>? context,
        object? data)
    {
        var line = new Dictionary<string, object?>
        {
            ["seq"] = seq,
            ["t"] = utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["type"] = type,
        };
        if (context != null)
        {
            foreach (var kv in context)
                if (!line.ContainsKey(kv.Key)) line[kv.Key] = kv.Value;
        }
        if (data != null) line["data"] = data;
        return JsonSerializer.Serialize(line, LineOptions);
    }

    /// <summary>
    /// Parse the envelope of one line. Returns false for blank, torn or
    /// non-JSON lines (a crash can leave a partial last line; readers skip it).
    /// </summary>
    public static bool TryParseEnvelope(string line, out ReplayLogEntry entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("seq", out var seqEl) || !seqEl.TryGetInt64(out var seq)) return false;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) return false;
            string? key = null;
            string? snapshot = null;
            long? dseq = null;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String)
                    key = k.GetString();
                if (data.TryGetProperty("snapshot", out var s) && s.ValueKind == JsonValueKind.String)
                    snapshot = s.GetString();
                if (data.TryGetProperty("dseq", out var d) && d.TryGetInt64(out var dv))
                    dseq = dv;
            }
            entry = new ReplayLogEntry(seq, typeEl.GetString()!, key, snapshot, dseq);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Save-point key: where the save puts the player plus a fingerprint of
    /// every RNG stream's position. RNG counters move with every random draw,
    /// so two saves with equal keys describe the same progress point even
    /// though volatile fields (save_time, run_time, num_reloads) differ. The
    /// same function is applied to the save the game writes and to the save
    /// it later loads, which is what lets a load be matched to its save.
    /// </summary>
    public static string BuildSavePointKey(
        int actIndex,
        int? coordCol,
        int? coordRow,
        int floor,
        string? preFinishedRoomType,
        IEnumerable<RngPosition> rngs)
    {
        ulong hash = 14695981039346656037UL; // FNV-1a 64
        foreach (var rng in rngs.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            hash = Fnv(hash, rng.Name);
            hash = Fnv(hash, rng.Counter.ToString(CultureInfo.InvariantCulture));
            hash = Fnv(hash, rng.State0.ToString(CultureInfo.InvariantCulture));
        }

        string coord = coordCol.HasValue && coordRow.HasValue
            ? $"{coordCol.Value},{coordRow.Value}"
            : "none";
        return $"a{actIndex}/c{coord}/f{floor}/p{preFinishedRoomType ?? "-"}/r{hash:x16}";
    }

    private static ulong Fnv(ulong hash, string s)
    {
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        // Field separator so ("ab","c") and ("a","bc") differ.
        hash ^= 0xff;
        hash *= 1099511628211UL;
        return hash;
    }

    /// <summary>
    /// Rebuild the canonical timeline from an append-only log. The game only
    /// ever resumes from a save, so a <c>load(K)</c> means everything recorded
    /// after the most recent <c>save_point(K)</c> on the current path did not
    /// happen in the surviving timeline: it is cut (kept on disk as an
    /// abandoned branch) and the path continues from that save point. A load
    /// whose key never appeared (recording began mid-run, or a save written by
    /// an older build) starts a new unanchored root.
    /// </summary>
    public static ReplayTimeline BuildCanonicalTimeline(IEnumerable<ReplayLogEntry> log)
    {
        var path = new List<ReplayLogEntry>();
        var branches = new List<ReplayBranch>();
        var unanchoredLoads = new List<long>();

        foreach (var entry in log.OrderBy(e => e.Seq))
        {
            if (entry.Type == EvLoad)
            {
                int anchor = -1;
                if (entry.Key != null)
                {
                    for (int i = path.Count - 1; i >= 0; i--)
                    {
                        if (path[i].Type == EvSavePoint && path[i].Key == entry.Key)
                        {
                            anchor = i;
                            break;
                        }
                    }
                }

                if (anchor >= 0)
                {
                    int cutFrom = anchor + 1;
                    if (cutFrom < path.Count)
                    {
                        branches.Add(new ReplayBranch(
                            path[anchor].Seq,
                            entry.Seq,
                            path.Skip(cutFrom).Select(e => e.Seq).ToList()));
                        path.RemoveRange(cutFrom, path.Count - cutFrom);
                    }
                }
                else
                {
                    unanchoredLoads.Add(entry.Seq);
                    if (path.Count > 0)
                    {
                        branches.Add(new ReplayBranch(null, entry.Seq, path.Select(e => e.Seq).ToList()));
                        path.Clear();
                    }
                }
            }

            path.Add(entry);
        }

        return new ReplayTimeline(path, branches, unanchoredLoads);
    }
}

/// <summary>Position of one named RNG stream inside a save.</summary>
public readonly record struct RngPosition(string Name, int Counter, ulong State0);

/// <summary>
/// The envelope fields of one events.jsonl line the timeline cut needs.
/// <see cref="DecisionSeq"/> is the linked decision record (<c>data.dseq</c>), if any.
/// </summary>
public readonly record struct ReplayLogEntry(long Seq, string Type, string? Key, string? Snapshot, long? DecisionSeq = null);

/// <summary>
/// A run of events cut by a load. <see cref="AnchorSeq"/> is the save point the
/// game went back to (null for an unanchored load), <see cref="LoadSeq"/> the
/// load that cut it, <see cref="AbandonedSeqs"/> the cut events.
/// </summary>
public sealed record ReplayBranch(long? AnchorSeq, long LoadSeq, IReadOnlyList<long> AbandonedSeqs);

public sealed record ReplayTimeline(
    IReadOnlyList<ReplayLogEntry> Canonical,
    IReadOnlyList<ReplayBranch> Branches,
    IReadOnlyList<long> UnanchoredLoads);
