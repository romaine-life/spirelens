using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SpireLens.Core.Replay;

/// <summary>
/// header.json shape. A new, explicitly versioned format (unlike
/// <see cref="RunData"/>), see <c>docs/run-recording.md</c>. Additive changes
/// keep <see cref="ReplayFormat.FormatVersion"/>; breaking ones bump it.
/// </summary>
public sealed class ReplayHeader
{
    public int FormatVersion { get; set; } = ReplayFormat.FormatVersion;

    /// <summary>
    /// <see cref="DecisionFormat.FormatVersion"/> of the decisions/ records, set
    /// when the first record is written. Null for recordings without them.
    /// </summary>
    public int? DecisionsFormatVersion { get; set; }
    public long GameStartTime { get; set; }
    public string? SpirelensRunId { get; set; }
    public string CreatedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";

    /// <summary>
    /// True when the first thing recorded for this run was a load of a save
    /// (SpireLens was installed or recording enabled mid-run). The genesis
    /// snapshot is then not the run's real start.
    /// </summary>
    public bool RecordingStartedMidRun { get; set; }

    public ReplayGameBuild Game { get; set; } = new();
    public List<ReplayModInfo> Mods { get; set; } = new();
    public string? SpirelensVersion { get; set; }
    public string? SpirelensCoreBuild { get; set; }
    public string? NetType { get; set; }
    public string? SeedString { get; set; }
    public ulong? SeedHash { get; set; }
    public List<ReplayPlayerInfo> Players { get; set; } = new();
    public int Ascension { get; set; }
    public string? GameMode { get; set; }
    public List<string> Modifiers { get; set; } = new();
    public string? DailyTimeUtc { get; set; }

    /// <summary>in_progress | win | loss | abandoned. Reset to in_progress by a later load (death undo, Continue).</summary>
    public string Outcome { get; set; } = "in_progress";
    public string? EndedAtUtc { get; set; }
    public int? FinalFloor { get; set; }
}

public sealed class ReplayGameBuild
{
    public string? Version { get; set; }
    public string? Commit { get; set; }
    public string? ReleaseDate { get; set; }
    public int? MainAssemblyHash { get; set; }
    /// <summary><c>ModelIdSerializationCache.Hash</c>: the content table the binary net forms index into.</summary>
    public uint? ModelIdHash { get; set; }
}

public sealed class ReplayModInfo
{
    public string? Id { get; set; }
    public string? Version { get; set; }
    public bool AffectsGameplay { get; set; }
}

public sealed class ReplayPlayerInfo
{
    public ulong NetId { get; set; }
    public string? Character { get; set; }
}

/// <summary>
/// Filesystem side of run recording. One directory per game run under
/// <see cref="RootDir"/>, named by the game's own run id (<c>start_time</c>):
/// header.json, events.jsonl, snapshots/NNNN.json.gz, combats/NNNN-fFF.mcr.
///
/// events.jsonl is appended and flushed synchronously per event (a few
/// hundred bytes into the OS cache — cheap); snapshot gzip, .mcr copies and
/// header rewrites go through one ordered background chain so the game thread
/// never compresses or waits on disk for them.
/// </summary>
internal static class ReplayStorage
{
    internal static readonly JsonSerializerOptions HeaderOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Overridable for tests; defaults to user://SpireLens/replays/ (never under runs/, which RunStorage enumerates).</summary>
    internal static Func<string> RootDirProvider = () =>
        Godot.ProjectSettings.GlobalizePath("user://SpireLens/replays/");

    public static string RootDir => RootDirProvider();

    private static readonly object _chainGate = new();
    private static Task _chain = Task.CompletedTask;

    /// <summary>Queue background file work. Exceptions are logged, never thrown into the chain.</summary>
    internal static void Enqueue(string what, Action work)
    {
        lock (_chainGate)
        {
            _chain = _chain.ContinueWith(
                _ =>
                {
                    try { work(); }
                    catch (Exception e) { CoreMain.Logger.Error($"Run recording: {what} failed: {e.Message}"); }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    /// <summary>Block (bounded) until queued writes land. Used at Core shutdown so a reloaded Core reads complete files.</summary>
    internal static bool WaitForPendingWrites(TimeSpan timeout)
    {
        Task chain;
        lock (_chainGate) chain = _chain;
        try { return chain.Wait(timeout); }
        catch { return false; }
    }

    internal static void WriteGzip(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var gz = new GZipStream(file, CompressionLevel.Fastest))
        {
            gz.Write(bytes, 0, bytes.Length);
        }
        File.Move(tmp, path, overwrite: true);
    }

    internal static void WriteBytesAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    internal static ReplayHeader? TryReadHeader(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "header.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ReplayHeader>(File.ReadAllText(path), HeaderOptions);
        }
        catch (Exception e)
        {
            CoreMain.LogDebug($"Run recording: unreadable header in {dir}: {e.Message}");
            return null;
        }
    }

    internal static string DirFor(long gameStartTime) =>
        Path.Combine(RootDir, gameStartTime.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// The open recording for one game run. Not thread-safe by itself; the
/// recorder serializes access under its lock (all capture is on the game's
/// main thread anyway).
/// </summary>
internal sealed class ReplaySession : IDisposable
{
    public long GameStartTime { get; }
    public string Dir { get; }
    public ReplayHeader Header { get; private set; }

    public string? LastSnapshotKey { get; private set; }
    public string? LastSnapshotFile { get; private set; }

    /// <summary>Seq of the latest <c>save_point</c> event: the anchor of the open decision segment (0 = none yet).</summary>
    public long LastSavePointSeq { get; private set; }

    private long _seq;
    private long _dseq;
    private int _nextSnapshot;
    private int _nextCombat;
    private FileStream? _events;

    private ReplaySession(long gameStartTime, string dir, ReplayHeader header)
    {
        GameStartTime = gameStartTime;
        Dir = dir;
        Header = header;
    }

    public string EventsPath => Path.Combine(Dir, "events.jsonl");
    public string HeaderPath => Path.Combine(Dir, "header.json");

    /// <summary>
    /// Open the recording for <paramref name="gameStartTime"/>. When none exists
    /// and <paramref name="createHeader"/> is non-null, a new one is created
    /// with that header (written synchronously so the directory is never
    /// headerless). Returns null when it does not exist and may not be created.
    /// </summary>
    public static ReplaySession? Open(long gameStartTime, Func<ReplayHeader>? createHeader, out bool created)
    {
        created = false;
        string dir = ReplayStorage.DirFor(gameStartTime);
        var header = ReplayStorage.TryReadHeader(dir);
        if (header == null)
        {
            if (createHeader == null) return null;
            header = createHeader();
            header.GameStartTime = gameStartTime;
            Directory.CreateDirectory(dir);
            ReplayStorage.WriteBytesAtomic(
                Path.Combine(dir, "header.json"),
                JsonSerializer.SerializeToUtf8Bytes(header, ReplayStorage.HeaderOptions));
            created = true;
        }

        var session = new ReplaySession(gameStartTime, dir, header);
        session.RecoverCounters();
        return session;
    }

    /// <summary>
    /// Resume seq / snapshot / combat numbering after a Core hot reload or a
    /// game restart, from what is already on disk.
    /// </summary>
    private void RecoverCounters()
    {
        _seq = 0;
        int maxSnapshot = -1;
        try
        {
            if (File.Exists(EventsPath))
            {
                var lines = File.ReadAllLines(EventsPath);
                bool haveSavePoint = false, haveDseq = false;
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    if (!ReplayFormat.TryParseEnvelope(lines[i], out var e)) continue;
                    if (_seq == 0) _seq = e.Seq;
                    if (e.Type == ReplayFormat.EvSavePoint && !haveSavePoint)
                    {
                        haveSavePoint = true;
                        LastSnapshotKey = e.Key;
                        LastSnapshotFile = e.Snapshot;
                        LastSavePointSeq = e.Seq;
                    }
                    if (e.DecisionSeq.HasValue && !haveDseq)
                    {
                        haveDseq = true;
                        _dseq = e.DecisionSeq.Value;
                    }
                    if (_seq != 0 && haveSavePoint && haveDseq) break;
                }
                maxSnapshot = ParseIndex(LastSnapshotFile);
            }
        }
        catch (Exception ex)
        {
            CoreMain.LogDebug($"Run recording: could not scan {EventsPath}: {ex.Message}");
        }

        maxSnapshot = Math.Max(maxSnapshot, MaxIndexIn(Path.Combine(Dir, "snapshots"), "*.json.gz"));
        _nextSnapshot = maxSnapshot + 1;
        _nextCombat = MaxIndexIn(Path.Combine(Dir, "combats"), "*.mcr") + 1;

        // Segments left plain by a crash / hot reload, other than the one
        // still open, are closed now.
        try
        {
            if (Directory.Exists(DecisionsDir))
            {
                foreach (var f in Directory.GetFiles(DecisionsDir, "*.jsonl"))
                {
                    long anchor = DecisionFormat.ParseSegmentAnchor(Path.GetFileName(f));
                    if (anchor >= 0 && anchor != LastSavePointSeq) QueueCompressSegment(anchor);
                }
            }
        }
        catch (Exception ex)
        {
            CoreMain.LogDebug($"Run recording: decision segment sweep failed: {ex.Message}");
        }
    }

    public string DecisionsDir => Path.Combine(Dir, "decisions");

    /// <summary>Allocate the next decision record seq (monotonic per run; recovered from events.jsonl links).</summary>
    public long NextDecisionSeq() => ++_dseq;

    /// <summary>
    /// A save point was logged: it opens a new decision segment, and the
    /// previous one is closed (gzipped off-thread).
    /// </summary>
    public void OnSavePointAppended(long seq)
    {
        long previous = LastSavePointSeq;
        LastSavePointSeq = seq;
        if (previous != seq) QueueCompressSegment(previous);
    }

    /// <summary>
    /// Append one decision record to the open segment. The line is built on
    /// the background chain (<paramref name="buildLine"/> must only touch
    /// detached snapshot data), after every earlier queued write.
    /// </summary>
    public void QueueDecisionRecord(Func<string> buildLine)
    {
        string path = Path.Combine(DecisionsDir, DecisionFormat.SegmentFileName(LastSavePointSeq));
        ReplayStorage.Enqueue("decision record write", () =>
        {
            string line = buildLine();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + "\n");
        });
    }

    /// <summary>Close the open segment (run end); the next record or save point reopens one.</summary>
    public void CloseDecisionSegment() => QueueCompressSegment(LastSavePointSeq);

    private void QueueCompressSegment(long anchor)
    {
        string plain = Path.Combine(DecisionsDir, DecisionFormat.SegmentFileName(anchor));
        ReplayStorage.Enqueue("decision segment compress", () =>
        {
            if (!File.Exists(plain)) return;
            // A segment can be reopened after it was closed (records after a
            // run end that a death undo then continues), so never overwrite an
            // earlier compressed part.
            string gz = plain + ".gz";
            for (int part = 1; File.Exists(gz); part++)
                gz = plain.Substring(0, plain.Length - ".jsonl".Length) + "." + part + ".jsonl.gz";
            ReplayStorage.WriteGzip(gz, File.ReadAllBytes(plain));
            File.Delete(plain);
        });
    }

    private static int MaxIndexIn(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return -1;
        int max = -1;
        foreach (var f in Directory.GetFiles(dir, pattern))
            max = Math.Max(max, ParseIndex(Path.GetFileName(f)));
        return max;
    }

    private static int ParseIndex(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return -1;
        var name = Path.GetFileName(fileName);
        int end = 0;
        while (end < name.Length && char.IsDigit(name[end])) end++;
        return end > 0 && int.TryParse(name.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;
    }

    /// <summary>Append one event line and flush it. Returns the seq used.</summary>
    public long Append(string type, IReadOnlyDictionary<string, object?>? context, object? data)
    {
        long seq = ++_seq;
        string line = ReplayFormat.FormatLine(seq, DateTime.UtcNow, type, context, data);
        var stream = EnsureEventsStream();
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
        return seq;
    }

    private FileStream EnsureEventsStream()
    {
        if (_events != null) return _events;
        Directory.CreateDirectory(Dir);
        // ReadWrite share: a reader (or an orphaned Core that missed teardown)
        // never blocks the append.
        _events = new FileStream(EventsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        // A crash can leave a torn last line; start ours on a fresh line.
        if (_events.Length > 0 && !EndsWithNewline(EventsPath))
        {
            _events.WriteByte((byte)'\n');
        }
        return _events;
    }

    private static bool EndsWithNewline(string path)
    {
        try
        {
            using var r = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (r.Length == 0) return true;
            r.Seek(-1, SeekOrigin.End);
            return r.ReadByte() == '\n';
        }
        catch { return true; }
    }

    /// <summary>
    /// Reserve the snapshot file for a save. Equal key to the previous
    /// snapshot (e.g. the game's IncrementNumReloads re-save right after a
    /// load) reuses that file instead of storing a duplicate.
    /// </summary>
    public (string RelativePath, bool Reused) ReserveSnapshot(string key)
    {
        if (LastSnapshotKey == key && LastSnapshotFile != null)
            return (LastSnapshotFile, true);

        string rel = $"snapshots/{_nextSnapshot++:D4}.json.gz";
        LastSnapshotKey = key;
        LastSnapshotFile = rel;
        return (rel, false);
    }

    public string ReserveCombatFile(int floor) =>
        $"combats/{_nextCombat++:D4}-f{Math.Max(0, floor):D2}.mcr";

    public string AbsolutePath(string relative) =>
        Path.Combine(Dir, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Mutate and persist the header (background, atomic).</summary>
    public void UpdateHeader(Action<ReplayHeader> mutate)
    {
        mutate(Header);
        Header.UpdatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Header, ReplayStorage.HeaderOptions);
        var path = HeaderPath;
        ReplayStorage.Enqueue("header write", () => ReplayStorage.WriteBytesAtomic(path, bytes));
    }

    public void Dispose()
    {
        try { _events?.Flush(); } catch { }
        try { _events?.Dispose(); } catch { }
        _events = null;
    }
}
