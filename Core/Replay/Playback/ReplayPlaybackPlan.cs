using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SpireLens.Core.Replay.Playback;

/// <summary>What the playback driver does with one step of a recording.</summary>
public enum PlaybackStepKind
{
    /// <summary>A recorded game save point: compare the replay's key, resync from its snapshot on divergence.</summary>
    Keyframe,

    /// <summary>A player input the driver feeds into the game.</summary>
    Input,

    /// <summary>A recorded combat checksum value (compared, log only; never waited on).</summary>
    Checksum,

    /// <summary>The recorded run ended here.</summary>
    RunEnd,
}

/// <summary>
/// One step of the canonical timeline, already classified for playback.
/// <see cref="Data"/> is the event's raw <c>data</c> JSON (the driver decodes
/// what it needs); the remaining fields are the parts the plan itself uses.
/// </summary>
public sealed record PlaybackStep(
    long Seq,
    PlaybackStepKind Kind,
    string InputKind,
    string Data,
    string? Key = null,
    string? Snapshot = null,
    int Floor = 0,
    int Act = 0,
    string? PreFinishedRoom = null,
    MapPosition? Coord = null)
{
    /// <summary>
    /// For a keyframe: the floor of the room the save belongs to. A room-entry
    /// save is written before the room's map-history entry is appended, so its
    /// recorded floor is one less than the room's; a pre-finished-room save
    /// (won fight, finished Ancient) already counts the room.
    /// </summary>
    public int RoomFloor => PreFinishedRoom == null ? Floor + 1 : Floor;

    /// <summary>A keyframe written as a room is entered (the start of a floor).</summary>
    public bool IsRoomStart => Kind == PlaybackStepKind.Keyframe && PreFinishedRoom == null;
}

/// <summary>A room's place on its act's map: the game's <c>MapCoord</c> as recorded ("col,row").</summary>
public readonly record struct MapPosition(int Col, int Row)
{
    public static MapPosition? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var parts = text.Split(',');
        return parts.Length == 2 && int.TryParse(parts[0], out int col) && int.TryParse(parts[1], out int row)
            ? new MapPosition(col, row)
            : null;
    }
}

/// <summary>A room the recording entered: its room-start keyframe, act, map position and floor.</summary>
public sealed record RecordedRoom(int KeyframeIndex, int Act, MapPosition Coord, int RoomFloor);

/// <summary>
/// The playable form of a recording: the canonical timeline
/// (<see cref="ReplayFormat.BuildCanonicalTimeline"/>) reduced to keyframes,
/// player inputs, checksum expectations and the run end. Pure and
/// game-independent, so the sequencing rules are unit tested.
///
/// Rules (docs/replay-player.md, "What is fed"):
/// <list type="bullet">
/// <item><c>save_point</c> → keyframe, except <c>reused</c> ones: those are the
/// game's IncrementNumReloads re-save right after a load, which playback never
/// performs (it does not reload at that point).</item>
/// <item><c>action</c> → input only for player decisions the game takes from
/// the local player (play card, potions, end turn, map vote, treasure relic,
/// next act). Actions the game derives itself in singleplayer — the move
/// that follows a map vote, ready-to-begin-enemy-turn, hook actions,
/// resumes — are not fed; the game produces them again. Console commands are
/// not fed (cheats; counted in <see cref="SkippedConsoleCommands"/>).</item>
/// <item><c>choice</c> → input (fed through the card selector or the screen that asked).</item>
/// <item><c>decision</c> → input, except <c>auto</c> purchases (game logic re-does them).</item>
/// <item><c>checksum</c> → checksum expectation; <c>run_end</c> → end.</item>
/// <item>everything else (load markers, run_started, results, file pointers) is bookkeeping.</item>
/// </list>
/// </summary>
public sealed class ReplayPlaybackPlan
{
    public static readonly IReadOnlySet<string> FedActionKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "play_card", "use_potion", "discard_potion", "end_turn", "undo_end_turn",
        "map_vote", "pick_relic", "vote_next_act",
    };

    public static readonly IReadOnlySet<string> FedDecisionKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "event_option", "rest_option", "reward_take", "reward_skip",
        "shop_buy", "shop_remove_card", "treasure_chest_open",
    };

    public IReadOnlyList<PlaybackStep> Steps { get; }
    public IReadOnlyList<ReplayBranch> Branches { get; }
    public int SkippedConsoleCommands { get; }
    public int UnparsableLines { get; }

    private ReplayPlaybackPlan(
        List<PlaybackStep> steps,
        IReadOnlyList<ReplayBranch> branches,
        int skippedConsole,
        int unparsable)
    {
        Steps = steps;
        Branches = branches;
        SkippedConsoleCommands = skippedConsole;
        UnparsableLines = unparsable;

        var rooms = new List<RecordedRoom>();
        var seen = new HashSet<(int, MapPosition)>();
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (!step.IsRoomStart || step.Coord is not { } coord) continue;
            if (seen.Add((step.Act, coord))) rooms.Add(new RecordedRoom(i, step.Act, coord, step.RoomFloor));
        }
        Rooms = rooms;
    }

    public int InputCount => Steps.Count(s => s.Kind == PlaybackStepKind.Input);

    /// <summary>Index of the first keyframe — where playback starts — or -1 when the recording has none.</summary>
    public int StartKeyframeIndex => IndexOfFirst(s => s.Kind == PlaybackStepKind.Keyframe);

    /// <summary>Highest room floor any keyframe reaches.</summary>
    public int MaxRoomFloor => Steps.Where(s => s.IsRoomStart).Select(s => s.RoomFloor).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Every room the recording entered, in play order: the first room-start
    /// keyframe of each (act, map position) that has one. These are the rooms
    /// the map floor picker offers; a room the recording only reached
    /// mid-way (it began mid-run) has no start to jump to and is not listed.
    /// </summary>
    public IReadOnlyList<RecordedRoom> Rooms { get; }

    /// <summary>The acts the recording has rooms in, ascending.</summary>
    public IReadOnlyList<int> Acts => Rooms.Select(r => r.Act).Distinct().OrderBy(a => a).ToList();

    /// <summary>The rooms of <paramref name="act"/>, in play order.</summary>
    public IReadOnlyList<RecordedRoom> RoomsInAct(int act) => Rooms.Where(r => r.Act == act).ToList();

    /// <summary>
    /// The act <paramref name="direction"/> steps (+1 / -1) from
    /// <paramref name="currentAct"/> that the recording has rooms in, or null
    /// at either end.
    /// </summary>
    public int? AdjacentAct(int currentAct, int direction) =>
        direction > 0
            ? Acts.Where(a => a > currentAct).Select(a => (int?)a).FirstOrDefault()
            : Acts.Where(a => a < currentAct).Select(a => (int?)a).LastOrDefault();

    /// <summary>
    /// The room-start keyframe the step at <paramref name="position"/> belongs
    /// to: the latest room-start keyframe strictly before it. -1 if none.
    /// </summary>
    public int CurrentRoomStartIndex(int position)
    {
        for (int i = Math.Min(position, Steps.Count) - 1; i >= 0; i--)
            if (Steps[i].IsRoomStart) return i;
        return -1;
    }

    /// <summary>The next keyframe at or after <paramref name="position"/>, or -1.</summary>
    public int NextKeyframeIndex(int position)
    {
        for (int i = Math.Max(0, position); i < Steps.Count; i++)
            if (Steps[i].Kind == PlaybackStepKind.Keyframe) return i;
        return -1;
    }

    /// <summary>How many input steps come before <paramref name="position"/> (for "input i / n").</summary>
    public int InputsBefore(int position)
    {
        int n = 0;
        for (int i = 0; i < Math.Min(position, Steps.Count); i++)
            if (Steps[i].Kind == PlaybackStepKind.Input) n++;
        return n;
    }

    private int IndexOfFirst(Func<PlaybackStep, bool> match)
    {
        for (int i = 0; i < Steps.Count; i++)
            if (match(Steps[i])) return i;
        return -1;
    }

    /// <summary>Build the plan from events.jsonl lines.</summary>
    public static ReplayPlaybackPlan Build(IEnumerable<string> eventLines)
    {
        var entries = new List<ReplayLogEntry>();
        var raw = new Dictionary<long, string>();
        int unparsable = 0;
        foreach (var line in eventLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!ReplayFormat.TryParseEnvelope(line, out var entry))
            {
                unparsable++;
                continue;
            }
            entries.Add(entry);
            raw[entry.Seq] = line;
        }

        var timeline = ReplayFormat.BuildCanonicalTimeline(entries);
        var steps = new List<PlaybackStep>();
        int skippedConsole = 0;

        foreach (var entry in timeline.Canonical)
        {
            if (!raw.TryGetValue(entry.Seq, out var line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                if (entry.Type == ReplayFormat.EvRunEnd)
                    steps.Add(new PlaybackStep(entry.Seq, PlaybackStepKind.RunEnd, "run_end", "{}"));
                continue;
            }
            string dataJson = data.GetRawText();

            switch (entry.Type)
            {
                case ReplayFormat.EvSavePoint:
                    if (GetBool(data, "reused")) break;
                    steps.Add(new PlaybackStep(
                        entry.Seq,
                        PlaybackStepKind.Keyframe,
                        "save_point",
                        dataJson,
                        Key: GetString(data, "key"),
                        Snapshot: GetString(data, "snapshot"),
                        Floor: GetInt(data, "floor"),
                        Act: GetInt(data, "act"),
                        PreFinishedRoom: GetString(data, "pre_finished_room"),
                        Coord: MapPosition.Parse(GetString(root, "coord"))));
                    break;

                case ReplayFormat.EvAction:
                {
                    if (!GetBool(data, "player_driven")) break;
                    string? kind = data.TryGetProperty("sem", out var sem) && sem.ValueKind == JsonValueKind.Object
                        ? GetString(sem, "kind")
                        : null;
                    if (kind == "console_cmd") { skippedConsole++; break; }
                    if (kind == null || !FedActionKinds.Contains(kind)) break;
                    steps.Add(new PlaybackStep(entry.Seq, PlaybackStepKind.Input, kind, dataJson));
                    break;
                }

                case ReplayFormat.EvChoice:
                    steps.Add(new PlaybackStep(entry.Seq, PlaybackStepKind.Input, "choice", dataJson));
                    break;

                case ReplayFormat.EvDecision:
                {
                    string? kind = GetString(data, "kind");
                    if (kind == null || !FedDecisionKinds.Contains(kind)) break;
                    if (GetBool(data, "auto")) break;
                    steps.Add(new PlaybackStep(entry.Seq, PlaybackStepKind.Input, kind, dataJson));
                    break;
                }

                case ReplayFormat.EvChecksum:
                    steps.Add(new PlaybackStep(entry.Seq, PlaybackStepKind.Checksum, "checksum", dataJson));
                    break;

                case ReplayFormat.EvRunEnd:
                    steps.Add(new PlaybackStep(entry.Seq, PlaybackStepKind.RunEnd, "run_end", dataJson));
                    break;
            }
        }

        return new ReplayPlaybackPlan(steps, timeline.Branches, skippedConsole, unparsable);
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

    private static bool GetBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Whether a recording can be played on the running game. Replays re-simulate
/// through the game itself, so they are only valid on the exact build (commit)
/// and content table (model id hash) that recorded them; anything else is
/// listed but refused with the reason. Pure, unit tested.
/// </summary>
public static class ReplayVersionGate
{
    public static (bool Playable, string? Reason) Check(
        string? recordedCommit,
        uint? recordedModelIdHash,
        string? recordedNetType,
        string? recordedVersion,
        string? liveCommit,
        uint? liveModelIdHash)
    {
        if (recordedNetType != null && !string.Equals(recordedNetType, "Singleplayer", StringComparison.Ordinal))
            return (false, $"{recordedNetType} runs can't be replayed");
        if (string.IsNullOrEmpty(recordedCommit) || recordedModelIdHash == null)
            return (false, "recording has no game build stamp");
        if (string.IsNullOrEmpty(liveCommit) || liveModelIdHash == null)
            return (false, "running game build is unknown");
        if (!string.Equals(recordedCommit, liveCommit, StringComparison.OrdinalIgnoreCase))
            return (false, $"recorded on game {recordedVersion ?? "?"} ({recordedCommit}), running {liveCommit}");
        if (recordedModelIdHash != liveModelIdHash)
            return (false, "game content differs (gameplay mods changed since recording)");
        return (true, null);
    }
}
