using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Godot;

namespace SpireLens.Core;

/// <summary>
/// Persists RunData to JSON on disk. Files land in Godot's user:// directory
/// (typically %APPDATA%/Godot/app_userdata/Slay the Spire 2/ on Windows), under
/// a SpireLens/runs/ subdirectory. One file per run, named by run_id.
///
/// Writes are fire-and-forget on a background task to avoid blocking the game.
/// Each save overwrites the full file — the in-memory RunData is always the
/// source of truth for the current run.
/// </summary>
public static class RunStorage
{
    private sealed class RunFileHeader
    {
        public long? GameStartTime { get; set; }
        public string RunId { get; set; } = "";
        public string? Outcome { get; set; }

        // Cheap content signal for duplicate resolution: a record that never
        // observed a floor is an empty stand-in, not a played run.
        public int? FloorReached { get; set; }
    }

    /// <summary>
    /// The single canonical serializer options for run files. Internal so tests
    /// consume the exact production configuration instead of hand-copied
    /// duplicates (which drift — the same parallel-list hazard as merge lists),
    /// and so the public API layer can serialize in the on-disk shape.
    ///
    /// <c>IncludeFields</c> is required: the game's <c>SerializableCard</c>
    /// (persisted as a removed-card snapshot) stores its data in public FIELDS,
    /// not properties, so without this a removed card's props/enchantment
    /// serialize as <c>{}</c> and rehydrate empty — silent data loss.
    /// </summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        IncludeFields = true,
    };

    /// <summary>Resolved absolute path to runs/ directory. Created on first save.</summary>
    public static string RunsDir => ProjectSettings.GlobalizePath("user://SpireLens/runs/");

    // Deliberately NOT under RunsDir: that directory is enumerated with
    // "*.json" to find run records, and a snapshot sitting in it would be
    // read back as one. (Builds before the save-point tag wrote untagged
    // snapshots to user://SpireLens/room-entry/; nothing reads those.)
    public static string RunSaveSnapshotDir =>
        ProjectSettings.GlobalizePath("user://SpireLens/run-save-snapshot/");

    private static string RunSaveSnapshotPath(string runId) =>
        Path.Combine(RunSaveSnapshotDir, runId + ".json");

    /// <summary>
    /// On-disk shape of the rewind target: the run record as it stood at the
    /// game's last run save, and which save that was (see
    /// <c>RunTracker.DescribeRunSavePoint</c>). A cache, not part of the run
    /// record — a missing or unreadable one only costs the rewind.
    /// </summary>
    private sealed class RunSaveSnapshotFile
    {
        public string? SavePoint { get; set; }
        public JsonElement Run { get; set; }
    }

    /// <summary>
    /// Persist the run-save snapshot so it survives a Core hot reload and a
    /// game restart. Best effort: failing to write one only costs the rewind,
    /// so it must never disturb the game's save.
    /// </summary>
    public static void SaveRunSaveSnapshot(string? runId, string? savePoint, string? runJson)
    {
        if (string.IsNullOrWhiteSpace(runId) || savePoint == null || runJson == null) return;

        try
        {
            using var run = JsonDocument.Parse(runJson);
            var file = new RunSaveSnapshotFile { SavePoint = savePoint, Run = run.RootElement };
            Directory.CreateDirectory(RunSaveSnapshotDir);
            File.WriteAllText(RunSaveSnapshotPath(runId), JsonSerializer.Serialize(file, Options));
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"SaveRunSaveSnapshot failed: {e.Message}");
            // An older snapshot left behind would be read back after a hot
            // reload as if it were this one.
            DeleteRunSaveSnapshot(runId);
        }
    }

    /// <summary>
    /// Remove this run's snapshot, so a stale one can never be read back as the
    /// rewind target. Best effort.
    /// </summary>
    public static void DeleteRunSaveSnapshot(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return;

        try
        {
            var path = RunSaveSnapshotPath(runId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"DeleteRunSaveSnapshot failed: {e.Message}");
        }
    }

    /// <summary>
    /// Read back this run's snapshot. False when there is none or it cannot be
    /// read whole.
    /// </summary>
    public static bool TryLoadRunSaveSnapshot(
        string? runId,
        out string? savePoint,
        out string? runJson)
    {
        savePoint = null;
        runJson = null;
        if (string.IsNullOrWhiteSpace(runId)) return false;

        try
        {
            var path = RunSaveSnapshotPath(runId);
            if (!File.Exists(path)) return false;

            var file = JsonSerializer.Deserialize<RunSaveSnapshotFile>(
                File.ReadAllText(path),
                Options);
            if (file?.SavePoint == null || file.Run.ValueKind != JsonValueKind.Object)
                return false;

            savePoint = file.SavePoint;
            runJson = file.Run.GetRawText();
            return true;
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"TryLoadRunSaveSnapshot failed: {e.Message}");
            return false;
        }
    }

    // Single-writer chain: every save is a continuation of the previous one,
    // so writes to the same run file apply in the exact order SaveAsync was
    // called (which, since callers hold RunTracker's lock and serialize the
    // snapshot on the calling thread, equals logical order). This replaces the
    // old fire-and-forget Task.Run, where two rapid saves of the same file
    // (OnCombatEnded then OnRunEnded on a won/lost run) raced: FileShare
    // collisions dropped a save, or pool scheduling let a stale in_progress
    // snapshot overwrite the final outcome=win/loss one.
    private static readonly object _writeChainGate = new();
    private static Task _writeChain = Task.CompletedTask;

    /// <summary>Serialize and write the run data to disk without blocking the caller.</summary>
    public static void SaveAsync(RunData data)
    {
        // Snapshot-serialize AND resolve paths on the calling thread (the game
        // thread, under RunTracker's lock): ProjectSettings.GlobalizePath and
        // the RunData mutations are not safe to touch from a pool thread,
        // especially during engine teardown.
        string json = JsonSerializer.Serialize(data, Options);
        string dir = RunsDir;
        string path = Path.Combine(dir, data.RunId + ".json");

        lock (_writeChainGate)
        {
            _writeChain = _writeChain.ContinueWith(
                _ => WriteFileAtomic(dir, path, json),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    // Atomic write: full content to a temp file, then replace. A crash mid-write
    // leaves the old file intact (or an orphan .tmp that the "*.json" scans
    // ignore) instead of a truncated .json that LoadKnownSchemaFile silently
    // skips — which would lose the whole run. Swallows its own exceptions so
    // one failed write can't fault the shared chain and stall every later save.
    private static void WriteFileAtomic(string dir, string path, string json)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"RunStorage.SaveAsync failed: {e}");
        }
    }

    private static RunFileHeader? ReadHeader(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RunFileHeader>(json, Options);
    }

    /// <summary>
    /// Detects whether a stored run file uses the current per-instance shape.
    /// Per-instance files always carry <c>instance_numbers_by_def</c> or
    /// <c>def_counters</c> at the top level (the runtime serializes both, even
    /// when empty); the historic pooled shape predates both fields and lacks
    /// them entirely.
    /// </summary>
    private static bool HasPerInstanceShape(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        return root.TryGetProperty("instance_numbers_by_def", out _)
            || root.TryGetProperty("def_counters", out _);
    }

    private static LoadedRunFile? LoadKnownSchemaFile(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"LoadKnownSchemaFile: cannot read {Path.GetFileName(path)}: {e.Message}");
            return null;
        }

        bool perInstance;
        try
        {
            perInstance = HasPerInstanceShape(json);
        }
        catch (JsonException e)
        {
            CoreMain.LogDebug($"LoadKnownSchemaFile: malformed JSON in {Path.GetFileName(path)}: {e.Message}");
            return null;
        }

        RunData? data;
        try
        {
            data = JsonSerializer.Deserialize<RunData>(json, Options);
        }
        catch (JsonException e)
        {
            CoreMain.LogDebug($"LoadKnownSchemaFile: deserialization failed for {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
        if (data == null)
        {
            CoreMain.LogDebug($"LoadKnownSchemaFile: deserialization returned null for {Path.GetFileName(path)}");
            return null;
        }

        return new LoadedRunFile
        {
            SourcePath = path,
            SupportsResume = perInstance,
            HasPerInstanceIdentity = perInstance,
            CompatibilityNote = perInstance
                ? null
                : "File stores pooled per-definition aggregates only. " +
                  "Readable as historical data, but cannot rebuild current per-instance live state.",
            Data = data,
        };
    }

    private static RunData? LoadForResume(string path)
    {
        var loaded = LoadKnownSchemaFile(path);
        if (loaded == null) return null;
        if (loaded.SupportsResume) return loaded.Data;

        CoreMain.Logger.Warn(
            $"LoadForResume: {Path.GetFileName(path)} uses the historic pooled shape and is history-only. " +
            $"{loaded.CompatibilityNote}");
        return null;
    }

    /// <summary>
    /// Load a stored run file for historical viewing / analysis.
    ///
    /// Unlike hot-reload resume, this accepts the historic pooled shape. Callers
    /// must inspect <see cref="LoadedRunFile.HasPerInstanceIdentity"/> before
    /// assuming that aggregate keys look like "CARD#N" or that removed-card
    /// snapshots / resume metadata exist.
    /// </summary>
    public static LoadedRunFile? LoadHistorical(string path)
    {
        try
        {
            return LoadKnownSchemaFile(path);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"LoadHistorical failed for {Path.GetFileName(path)}: {e}");
            return null;
        }
    }

    /// <summary>
    /// Load a stored run file as hot-reload resume state.
    ///
    /// Public mainly so schema fixtures can exercise the exact same gating
    /// logic as live resume without needing to stand up a Godot runtime or a
    /// real <c>user://</c> runs directory.
    /// </summary>
    public static RunData? LoadResumable(string path)
    {
        try
        {
            return LoadForResume(path);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"LoadResumable failed for {Path.GetFileName(path)}: {e}");
            return null;
        }
    }

    /// <summary>
    /// Scan the runs/ directory for a JSON file whose <c>GameStartTime</c>
    /// matches the supplied value. Used by <see cref="RunTracker.TryResumeActiveRun"/>
    /// on hot reload: the game's <c>RunManager._startTime</c> is stable
    /// across our Core assembly reload, so we match on that to find the
    /// run file we were writing to before the reload.
    ///
    /// Returns null if no match or if the directory doesn't exist yet.
    /// Malformed / unreadable files are skipped, not fatal.
    /// </summary>
    // How many of the newest run files FindByGameStartTime probes before
    // giving up. The in-progress run it looks for is by construction among
    // the most recently written files — only one run is ever active and its
    // file is rewritten on every save — so a bounded probe keeps the miss
    // case (fresh run start, no match anywhere) from reading and JSON-parsing
    // months of accumulated run files on the main thread.
    private const int MaxFindHeaderProbes = 25;

    public static RunData? FindByGameStartTime(
        long gameStartTime,
        out bool foundUnsupportedMatch,
        bool requireInProgress = false)
    {
        foundUnsupportedMatch = false;
        try
        {
            if (!Directory.Exists(RunsDir)) return null;

            // Sort newest-first so if multiple files match (shouldn't happen
            // but defensive), we pick the most recent. Sort keys are
            // precomputed — GetLastWriteTimeUtc inside a comparator would
            // stat each file O(log N) times.
            var byNewest = Directory.GetFiles(RunsDir, "*.json")
                .Select(p => (Path: p, Mtime: File.GetLastWriteTimeUtc(p)))
                .OrderByDescending(f => f.Mtime)
                .Select(f => f.Path);

            int probed = 0;
            foreach (var path in byNewest)
            {
                if (probed++ >= MaxFindHeaderProbes)
                {
                    CoreMain.LogDebug(
                        $"FindByGameStartTime: no match for {gameStartTime} in the {MaxFindHeaderProbes} newest run files; stopping scan");
                    break;
                }
                try
                {
                    var header = ReadHeader(path);
                    if (header?.GameStartTime != gameStartTime) continue;

                    // Filter at scan level, not post-hoc: a finished record
                    // (e.g. a hot reload on the game-over screen) must not
                    // shadow an older in-progress one for the same run.
                    // Null outcome (very old files) is treated as unknown
                    // and allowed through; LoadForResume gates the shape.
                    if (requireInProgress && header.Outcome != null
                        && header.Outcome != "in_progress") continue;

                    var data = LoadForResume(path);
                    if (data != null) return data;
                    foundUnsupportedMatch = true;
                }
                catch (Exception e)
                {
                    CoreMain.LogDebug($"FindByGameStartTime: skipping unreadable {Path.GetFileName(path)}: {e.Message}");
                }
            }
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"FindByGameStartTime failed: {e}");
        }
        return null;
    }

    /// <summary>
    /// Historical counterpart to <see cref="FindByGameStartTime"/>. Returns a
    /// loaded run file even when the source uses the historic pooled shape.
    /// </summary>
    /// <summary>
    /// Resolve the run record for one game run, identified by its
    /// <c>game_start_time</c>.
    ///
    /// A game run should own exactly one record, but a duplicate can exist —
    /// historically because a post-run event resurrected a finished run as a
    /// second, empty record (see
    /// <c>RunTracker.IsResurrectedEndedRunLocked</c>). Picking purely by newest
    /// file mtime let such a duplicate win and made the run history screen
    /// report all-zero stats for a real run. So rank the candidates instead of
    /// taking the first match, and prefer the record that actually describes a
    /// completed game. This keeps history correct for duplicates already on
    /// disk, with no file cleanup required.
    /// </summary>
    public static LoadedRunFile? FindHistoricalByGameStartTime(long gameStartTime)
    {
        try
        {
            if (!Directory.Exists(RunsDir)) return null;

            var candidates = new List<RunFileCandidate>();
            foreach (var path in Directory.GetFiles(RunsDir, "*.json"))
            {
                try
                {
                    var header = ReadHeader(path);
                    if (header?.GameStartTime != gameStartTime) continue;
                    candidates.Add(new RunFileCandidate(
                        path,
                        IsFinished: header.Outcome != null
                            && header.Outcome != RunTracker.InProgressOutcome,
                        FloorReached: header.FloorReached ?? -1,
                        LastWriteUtc: File.GetLastWriteTimeUtc(path)));
                }
                catch (Exception e)
                {
                    CoreMain.LogDebug($"FindHistoricalByGameStartTime: skipping unreadable {Path.GetFileName(path)}: {e.Message}");
                }
            }

            var best = SelectBestRunFileCandidate(candidates);
            if (best == null) return null;

            if (candidates.Count > 1)
            {
                // Always-on: more than one record for a single game run means
                // something wrote a duplicate, and that should be visible in the
                // log rather than silently resolved.
                CoreMain.Logger.Warn(
                    $"FindHistoricalByGameStartTime: {candidates.Count} run files share "
                    + $"game_start_time={gameStartTime}; chose {Path.GetFileName(best.Path)} "
                    + $"(finished={best.IsFinished}, floor={best.FloorReached}). Others: "
                    + string.Join(
                        ", ",
                        candidates
                            .Where(c => !ReferenceEquals(c, best))
                            .Select(c => $"{Path.GetFileName(c.Path)}(finished={c.IsFinished}, floor={c.FloorReached})")));
            }

            return LoadKnownSchemaFile(best.Path);
        }
        catch (Exception e)
        {
            CoreMain.Logger.Error($"FindHistoricalByGameStartTime failed: {e}");
        }
        return null;
    }

    /// <summary>
    /// Rank duplicate records for one game run. Finished beats in-progress: a
    /// game run cannot un-finish, so a terminal record is the authoritative one.
    /// Then higher floor, which separates a real run from an empty stand-in that
    /// never recorded a floor. Newest write time only breaks remaining ties —
    /// on its own it is the wrong signal, because the stray duplicate is written
    /// after the real record.
    /// </summary>
    internal static RunFileCandidate? SelectBestRunFileCandidate(
        IReadOnlyList<RunFileCandidate> candidates)
        => candidates == null || candidates.Count == 0
            ? null
            : candidates
                .OrderByDescending(c => c.IsFinished)
                .ThenByDescending(c => c.FloorReached)
                .ThenByDescending(c => c.LastWriteUtc)
                .First();

    internal sealed record RunFileCandidate(
        string Path,
        bool IsFinished,
        int FloorReached,
        DateTime LastWriteUtc);
}
